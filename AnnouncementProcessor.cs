using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using GEvent = Google.Apis.Calendar.v3.Data.Event;

namespace SchoolAssistant;

/// <summary>Announcement text, image or PDF -> events from the LLM -> calendar (or a question first).</summary>
public class AnnouncementProcessor(ILlmClient llm, TelegramApi telegram, JujuCalendar calendar, ILogger<AnnouncementProcessor> log)
{
    const int VerbatimBodyMaxChars = 1500; // a short message is copied as-is into an important event's description
    const string NoTextPlaceholder = "(no text, see attachment)";

    const string ExtractPrompt = """
        You extract school events for a parent. Philippines, Asia/Manila time. Today is {TODAY}.
        Read the message text and any attached image or PDF. Find every dated item: events, quizzes, exams, written works, performance tasks, deadlines, birthdays, no-class days.
        Reply with ONLY this JSON, no markdown:
        {"events":[{"type":"birthday|event|quiz|exam|written_work|performance_task|deadline|other","title":"...","date":"YYYY-MM-DD","end_date":null,"start_time":null,"end_time":null,"location":null,"topics":[],"pages":[],"references":[],"details":null,"body":null,"confidence":"high|medium|low"}],"note":null}
        Rules:
        - One event per subject and period. A schedule with Math on Oct 14 and Science on Oct 15 is two events.
        - Events can span several days (exam week Oct 12 to 16, or a performance task due over a week). Set date to the first day and end_date to the last day, inclusive. Use end_date null for single-day events.
        - title: short and includes the subject, e.g. "Science Quiz", "Math Exam", "English Performance Task", "Ana's Birthday".
        - Dates: if no year is given, use the nearest upcoming date. Resolve "next Friday" and similar from today. If the date is missing or unclear, use null.
        - Times are 24-hour HH:mm and only if stated.
        - Important events are written_work, quiz, exam and performance_task. For these: put every topic or coverage item in "topics", page numbers or ranges in "pages" (e.g. "pp. 12-20"), and books, worksheets, handouts or other materials in "references". All values are strings.
        - For those important events also set "body" to the announcement text that applies to the event, copied as written (keep line breaks as \n, do not reword). If the announcement is long, copy only the relevant parts (date, coverage, instructions, materials) and keep it under about 1200 characters. For an image or PDF, transcribe the relevant text. For other event types set body to null.
        - details: one short sentence of other useful info (what to bring, dress code), else null.
        - Never guess. If unsure about a date or time, use null and set confidence to "low".
        - note: at most 12 words, only if something is unreadable or ambiguous; else null.
        """;

    const string MatchPrompt = """
        You compare a NEW school event against EXISTING calendar events.
        Reply with ONLY JSON: {"match_id": "<id>"} or {"match_id": null}.
        Match only if NEW is clearly the same real-world event as one EXISTING entry: same subject and kind (for example both are a Science Quiz), even if the date or time changed.
        Different subjects or kinds are not a match. If unsure, answer null.
        """;

    /// <param name="updateId">Telegram update id; the raw model answer is saved against it for debugging.</param>
    public async Task HandleAsync(long chatId, string updateId, string? text, IReadOnlyList<LlmPart> attachments)
    {
        var parts = attachments.Append(new TextPart(string.IsNullOrWhiteSpace(text) ? NoTextPlaceholder : text)).ToList();
        var answer = await llm.AskAsync(ExtractPrompt.Replace("{TODAY}", LlmUtil.Today()), parts);
        var json = LlmUtil.StripFences(answer);
        await MessageStore.SaveResultAsync(updateId, json);

        Extraction extraction;
        try { extraction = Extraction.Parse(json); }
        catch (JsonException)
        {
            await telegram.SendAsync(chatId, "⚠️ Couldn't read the AI result.");
            return;
        }

        CopyShortMessageIntoImportantEvent(extraction, text);
        await ProcessAsync(chatId, extraction);
    }

    // One important event in a short text message: copy the message itself, exactly as sent
    static void CopyShortMessageIntoImportantEvent(Extraction extraction, string? text)
    {
        var important = extraction.Events.Where(e => e.IsAssessment).ToList();
        if (important.Count == 1 && !string.IsNullOrWhiteSpace(text) && text.Length <= VerbatimBodyMaxChars)
            important[0].Body = text.Trim();
    }

    async Task ProcessAsync(long chatId, Extraction extraction)
    {
        var replyLines = new List<string>();
        var confirmLowConfidence = Env.GetFlag(Settings.ConfirmLowConfidence, defaultValue: true);

        foreach (var extracted in extraction.Events)
        {
            try
            {
                var line = await ProcessEventAsync(chatId, extracted, confirmLowConfidence);
                if (line != null) replyLines.Add(line);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed processing event {Title}", extracted.Title);
                replyLines.Add($"❌ {extracted.Title}: {TextUtil.Short(ex)}");
            }
        }

        if (!string.IsNullOrWhiteSpace(extraction.Note)) replyLines.Add("ℹ️ " + extraction.Note);
        if (extraction.Events.Count == 0 && replyLines.Count == 0) replyLines.Add("ℹ️ No dated events found.");
        if (replyLines.Count > 0) await telegram.SendAsync(chatId, string.Join("\n", replyLines));
    }

    /// <summary>Adds the event, or sends a question with buttons. Returns the summary line to reply with, or null if a question was sent.</summary>
    async Task<string?> ProcessEventAsync(long chatId, ExtractedEvent extracted, bool confirmLowConfidence)
    {
        if (!DateText.TryDate(extracted.Date, out var date)) return $"⚠️ No date: {extracted.Title}";
        if (date < ManilaTime.Today) extracted.Confidence = Confidence.Low; // past dates are usually a misread year

        var similar = await FindSimilarOnCalendarAsync(extracted, date);
        var mustAsk = similar != null || (confirmLowConfidence && extracted.Confidence == Confidence.Low);
        if (!mustAsk)
        {
            await calendar.InsertAsync(CalendarEventBuilder.Build(extracted));
            return $"✅ {extracted.Title}, {EventText.When(extracted)}";
        }

        // Hold the proposal until a button is tapped
        var proposalId = await PendingStore.AddAsync(extracted, similar?.Id ?? "");
        if (similar != null)
            await telegram.SendAsync(chatId,
                $"❓ Similar event on calendar:\n• {similar.Summary}, {EventText.When(similar)}\nNew: {extracted.Title}, {EventText.When(extracted)}",
                TelegramApi.Keyboard(proposalId, KeyboardKind.Match));
        else
            await telegram.SendAsync(chatId,
                $"❓ Not sure about this one. Add it?\n{extracted.Title}, {EventText.When(extracted)}",
                TelegramApi.Keyboard(proposalId, KeyboardKind.Add));
        return null;
    }

    async Task<GEvent?> FindSimilarOnCalendarAsync(ExtractedEvent extracted, DateOnly date)
    {
        var nearby = await calendar.NearbyAsync(date);
        if (nearby.Count == 0) return null;

        var prompt = new StringBuilder();
        prompt.AppendLine($"NEW: {extracted.Type} | {EventText.When(extracted)} | {extracted.Title}");
        prompt.AppendLine("EXISTING:");
        foreach (var existing in nearby) prompt.AppendLine(EventText.Line(existing));

        var answer = await llm.AskAsync(MatchPrompt, new LlmPart[] { new TextPart(prompt.ToString()) });
        try
        {
            using var doc = LlmUtil.ParseJson(answer);
            var matchId = doc.RootElement.TryGetProperty("match_id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() : null;
            return nearby.FirstOrDefault(g => g.Id == matchId); // ignores ids the model made up
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Couldn't read the similar-event check; treating as no match");
            return null;
        }
    }
}
