using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using GEvent = Google.Apis.Calendar.v3.Data.Event;

namespace SchoolAssistant;

// ---------- 1) Telegram webhook: announcement -> Juju calendar ----------
public class TelegramWebhook(ILlmClient llm, TelegramApi tg, JujuCalendar cal, SheetSync sync, ILogger<TelegramWebhook> log)
{
    const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";
    const string SyncCommand = "/sync";
    const string PdfMime = "application/pdf";
    const string JpegMime = "image/jpeg";
    const string PngMime = "image/png";
    const long MaxAttachmentBytes = 10_000_000;
    const int VerbatimBodyMaxChars = 1500; // a short message is copied as-is into an important event's description

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

    [Function("telegram")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
    {
        var ok = req.CreateResponse(HttpStatusCode.OK); // always 200 so Telegram doesn't retry forever

        // Verify the request really came from Telegram
        if (!req.Headers.TryGetValues(SecretHeader, out var s) ||
            s.FirstOrDefault() != Env.Get(Settings.TelegramSecret))
            return req.CreateResponse(HttpStatusCode.Unauthorized);

        var expected = Env.Get(Settings.TelegramChatId);
        var debug = Env.GetOr(Settings.DebugReplies, "false") == "true"; // only used for the wrong-chat-id hint
        long? replyChat = null;

        try
        {
            using var doc = JsonDocument.Parse(await req.ReadAsStringAsync() ?? "{}");
            var root = doc.RootElement;

            // Button taps
            if (root.TryGetProperty("callback_query", out var cb))
            {
                if (cb.TryGetProperty("message", out var cm) && ChatOf(cm) is long cc && cc.ToString() == expected)
                    replyChat = cc;
                await HandleCallbackAsync(cb, expected);
                return ok;
            }

            if (!root.TryGetProperty("message", out var msg))
            {
                log.LogWarning("Update has no 'message' field, ignoring");
                return ok;
            }

            var chatId = ChatOf(msg);
            if (chatId.ToString() != expected)
            {
                log.LogWarning("Ignoring chat {ChatId}; TELEGRAM_CHAT_ID is {Expected}", chatId, expected);
                if (debug) await tg.SendAsync(chatId, $"⚠️ This chat's ID is {chatId}, but TELEGRAM_CHAT_ID is {expected}.");
                return ok;
            }
            replyChat = chatId;

            var updateId = root.GetProperty("update_id").GetInt64().ToString();
            var text = msg.TryGetProperty("text", out var t) ? t.GetString()
                     : msg.TryGetProperty("caption", out var c) ? c.GetString() : "";

            // Dedupe: a repeated update_id is skipped
            var table = Db.Table(Tables.Messages);
            try { await table.AddEntityAsync(new TableEntity(Tables.MessagesPartition, updateId) { [Columns.Text] = text ?? "" }); }
            catch (RequestFailedException e) when (e.Status == (int)HttpStatusCode.Conflict)
            {
                log.LogInformation("Duplicate update {UpdateId}, skipping", updateId);
                return ok;
            }

            // Manual sheet sync: send /sync to the bot
            if (text?.Trim().StartsWith(SyncCommand, StringComparison.OrdinalIgnoreCase) == true)
            {
                await sync.RunAsync(chatId, manual: true);
                return ok;
            }

            // Provider-neutral parts: optional image/PDF, then the text
            var parts = new List<LlmPart>();
            if (msg.TryGetProperty("photo", out var photos))
            {
                var fileId = photos.EnumerateArray().Last().GetProperty("file_id").GetString()!;
                parts.Add(new ImagePart(await tg.DownloadAsync(fileId), JpegMime));
            }
            else if (msg.TryGetProperty("document", out var d))
            {
                var mime = d.TryGetProperty("mime_type", out var m) ? m.GetString() : "";
                var size = d.TryGetProperty("file_size", out var sz) ? sz.GetInt64() : 0;
                var fileId = d.GetProperty("file_id").GetString()!;
                if (mime == PdfMime && size < MaxAttachmentBytes)
                    parts.Add(new PdfPart(await tg.DownloadAsync(fileId)));
                else if (mime is JpegMime or PngMime && size < MaxAttachmentBytes)
                    parts.Add(new ImagePart(await tg.DownloadAsync(fileId), mime!));
                else
                    await tg.SendAsync(chatId, $"⚠️ Can't read this file type ({mime}).");
            }
            parts.Add(new TextPart(string.IsNullOrWhiteSpace(text) ? "(no text, see attachment)" : text));

            var raw = await llm.AskAsync(ExtractPrompt.Replace("{TODAY}", LlmUtil.Today()), parts);
            var json = LlmUtil.StripFences(raw);

            var saved = await table.GetEntityAsync<TableEntity>(Tables.MessagesPartition, updateId);
            saved.Value[Columns.ResultJson] = json;
            await table.UpdateEntityAsync(saved.Value, ETag.All);

            Extraction x;
            try { x = Extraction.Parse(json); }
            catch (JsonException)
            {
                await tg.SendAsync(chatId, "⚠️ Couldn't read the AI result.");
                return ok;
            }

            // One important event in a short text message: copy the message itself, exactly as sent
            var important = x.Events.Where(ev => ev.IsAssessment).ToList();
            if (important.Count == 1 && !string.IsNullOrWhiteSpace(text) && text!.Length <= VerbatimBodyMaxChars)
                important[0].Body = text.Trim();

            await ProcessAsync(chatId, x);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Webhook failed");
            if (replyChat is long c)
            {
                try { await tg.SendAsync(c, "❌ " + TextUtil.Short(ex)); }
                catch (Exception inner) { log.LogError(inner, "Could not send error reply"); }
            }
        }
        return ok;
    }

    static long ChatOf(JsonElement message) => message.GetProperty("chat").GetProperty("id").GetInt64();

    // ---------- extracted events -> calendar (or ask first) ----------
    async Task ProcessAsync(long chatId, Extraction x)
    {
        var lines = new List<string>();
        var confirmLow = Env.GetOr(Settings.ConfirmLowConfidence, "true") != "false";
        var today = JujuCalendar.TodayManila;

        foreach (var ev in x.Events)
        {
            try
            {
                if (!JujuCalendar.TryDate(ev.Date, out var date))
                {
                    lines.Add($"⚠️ No date: {ev.Title}");
                    continue;
                }
                if (date < today) ev.Confidence = Confidence.Low; // past dates are usually a misread year

                GEvent? match = null;
                var nearby = await cal.NearbyAsync(date);
                if (nearby.Count > 0)
                {
                    var matchId = await FindSimilarAsync(ev, nearby);
                    match = nearby.FirstOrDefault(g => g.Id == matchId);
                }

                var mustAsk = match != null || (confirmLow && ev.Confidence == Confidence.Low);
                if (!mustAsk)
                {
                    await cal.InsertAsync(cal.Build(ev));
                    lines.Add($"✅ {ev.Title}, {JujuCalendar.When(ev)}");
                    continue;
                }

                // Hold the proposal until a button is tapped
                var pid = await PendingStore.AddAsync(ev, match?.Id ?? "");

                if (match != null)
                    await tg.SendAsync(chatId,
                        $"❓ Similar event on calendar:\n• {match.Summary}, {JujuCalendar.WhenEvent(match)}\nNew: {ev.Title}, {JujuCalendar.When(ev)}",
                        TelegramApi.Keyboard(pid, KeyboardKind.Match));
                else
                    await tg.SendAsync(chatId,
                        $"❓ Not sure about this one. Add it?\n{ev.Title}, {JujuCalendar.When(ev)}",
                        TelegramApi.Keyboard(pid, KeyboardKind.Add));
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed processing event {Title}", ev.Title);
                lines.Add($"❌ {ev.Title}: {TextUtil.Short(ex)}");
            }
        }

        if (!string.IsNullOrWhiteSpace(x.Note)) lines.Add("ℹ️ " + x.Note);
        if (x.Events.Count == 0 && lines.Count == 0) lines.Add("ℹ️ No dated events found.");
        if (lines.Count > 0) await tg.SendAsync(chatId, string.Join("\n", lines));
    }

    async Task<string?> FindSimilarAsync(ExtractedEvent ev, IList<GEvent> existing)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"NEW: {ev.Type} | {JujuCalendar.When(ev)} | {ev.Title}");
        sb.AppendLine("EXISTING:");
        foreach (var g in existing) sb.AppendLine(JujuCalendar.Line(g));

        var raw = await llm.AskAsync(MatchPrompt, new LlmPart[] { new TextPart(sb.ToString()) });
        try
        {
            using var doc = JsonDocument.Parse(LlmUtil.StripFences(raw));
            var id = doc.RootElement.TryGetProperty("match_id", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() : null;
            return existing.Any(g => g.Id == id) ? id : null; // ignore ids the model made up
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Couldn't read the similar-event check; treating as no match");
            return null;
        }
    }

    // If the calendar event was deleted in the meantime, create it again instead of failing.
    async Task<string> UpdateOrInsertAsync(string id, ExtractedEvent ev, bool keepPrevious)
    {
        try
        {
            var updated = await cal.UpdateAsync(id, cal.Build(ev), keepPrevious);
            return updated.Id;
        }
        catch (Exception ex) when (ex is KeyNotFoundException || JujuCalendar.IsGone(ex))
        {
            var created = await cal.InsertAsync(cal.Build(ev));
            return created.Id;
        }
    }

    // ---------- button taps ----------
    async Task HandleCallbackAsync(JsonElement cb, string expected)
    {
        var cbId = cb.GetProperty("id").GetString()!;
        var msg = cb.GetProperty("message");
        var chatId = ChatOf(msg);
        var messageId = msg.GetProperty("message_id").GetInt64();

        if (chatId.ToString() != expected) { await tg.AnswerCallbackAsync(cbId); return; }

        var data = cb.TryGetProperty("data", out var dd) ? dd.GetString() ?? "" : "";
        var sep = data.IndexOf(CallbackAction.Separator);
        if (sep < 0) { await tg.AnswerCallbackAsync(cbId); return; }
        var act = data[..sep];
        var pid = data[(sep + 1)..];

        var table = Db.Table(Tables.Pending);
        TableEntity p;
        try { p = (await table.GetEntityAsync<TableEntity>(Tables.PendingPartition, pid)).Value; }
        catch (RequestFailedException e) when (e.Status == (int)HttpStatusCode.NotFound)
        {
            await tg.AnswerCallbackAsync(cbId, "Expired");
            return;
        }

        if (p.GetString(Columns.Status) != PendingStatus.Pending) { await tg.AnswerCallbackAsync(cbId, "Already handled"); return; }

        // Claim it so a double tap can't run twice (ETag makes the second write fail)
        p[Columns.Status] = PendingStatus.Done;
        try { await table.UpdateEntityAsync(p, p.ETag); }
        catch (RequestFailedException e) when (e.Status == (int)HttpStatusCode.PreconditionFailed)
        {
            await tg.AnswerCallbackAsync(cbId, "Already handled");
            return;
        }

        await tg.AnswerCallbackAsync(cbId);

        var ev = JsonSerializer.Deserialize<ExtractedEvent>(p.GetString(Columns.EventJson)!)!;
        var matchId = p.GetString(Columns.MatchId) ?? "";
        var kind = p.GetString(Columns.Kind) ?? PendingKind.Telegram;
        var fromSheet = kind.StartsWith(PendingKind.SheetPrefix, StringComparison.Ordinal);
        var changedOnSheet = kind == PendingKind.SheetChanged;

        // Sheet proposals only: which Synced row to write back
        var gid = p.GetString(Columns.Gid) ?? "";
        var rk = p.GetString(Columns.Rk) ?? "";
        var key = p.GetString(Columns.Key) ?? "";
        var hash = p.GetString(Columns.Hash) ?? "";

        string result;
        try
        {
            switch (act)
            {
                case CallbackAction.Delete when kind == PendingKind.SheetRemoved:
                    await cal.DeleteAsync(matchId);
                    await SyncStore.MarkAsync(gid, rk, SyncStatus.Removed);
                    result = $"🗑 Deleted: {ev.Title}";
                    break;
                case CallbackAction.Skip when kind == PendingKind.SheetRemoved:
                    await SyncStore.MarkAsync(gid, rk, SyncStatus.Kept);
                    result = $"📌 Kept: {ev.Title}";
                    break;
                case CallbackAction.Skip:
                    result = $"⏭ Skipped: {ev.Title}";
                    // A skipped change keeps its link to the existing event; a skipped new item has none
                    if (fromSheet)
                        await SyncStore.SetAsync(gid, rk, key, hash,
                            changedOnSheet ? matchId : "", changedOnSheet ? SyncStatus.Synced : SyncStatus.Skipped, ev.Title);
                    break;
                case CallbackAction.Update when matchId.Length > 0:
                    var linkedId = await UpdateOrInsertAsync(matchId, ev, keepPrevious: !changedOnSheet);
                    result = $"✏️ Updated: {ev.Title}, {JujuCalendar.When(ev)}";
                    if (fromSheet) await SyncStore.SetAsync(gid, rk, key, hash, linkedId, SyncStatus.Synced, ev.Title);
                    break;
                default: // CallbackAction.AddNew
                    var created = await cal.InsertAsync(cal.Build(ev));
                    result = $"✅ Added: {ev.Title}, {JujuCalendar.When(ev)}";
                    if (fromSheet) await SyncStore.SetAsync(gid, rk, key, hash, created.Id, SyncStatus.Synced, ev.Title);
                    break;
            }
        }
        catch
        {
            // Let the buttons work again after a failure
            try { p[Columns.Status] = PendingStatus.Pending; await table.UpdateEntityAsync(p, ETag.All); } catch { }
            throw;
        }

        // A moved item: its old row is done whichever way the user answered
        var oldRk = p.GetString(Columns.OldRk);
        if (fromSheet && !string.IsNullOrEmpty(oldRk))
            await SyncStore.MarkAsync(gid, oldRk, SyncStatus.Moved);

        await tg.EditAsync(chatId, messageId, result);
    }
}