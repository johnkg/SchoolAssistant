using System.Net;
using System.Security.Cryptography;
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
        if (!req.Headers.TryGetValues("X-Telegram-Bot-Api-Secret-Token", out var s) ||
            s.FirstOrDefault() != Env.Get("TELEGRAM_SECRET"))
            return req.CreateResponse(HttpStatusCode.Unauthorized);

        var expected = Env.Get("TELEGRAM_CHAT_ID");
        var debug = Env.GetOr("DEBUG_REPLIES", "false") == "true"; // only used for the wrong-chat-id hint
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
            var table = Db.Table("Messages");
            try { await table.AddEntityAsync(new TableEntity("tg", updateId) { ["Text"] = text ?? "" }); }
            catch (RequestFailedException e) when (e.Status == 409)
            {
                log.LogInformation("Duplicate update {UpdateId}, skipping", updateId);
                return ok;
            }

            // Manual sheet sync: send /sync to the bot
            if (text?.Trim().StartsWith("/sync", StringComparison.OrdinalIgnoreCase) == true)
            {
                await sync.RunAsync(chatId, manual: true);
                return ok;
            }

            // Provider-neutral parts: optional image/PDF, then the text
            var parts = new List<LlmPart>();
            if (msg.TryGetProperty("photo", out var photos))
            {
                var fileId = photos.EnumerateArray().Last().GetProperty("file_id").GetString()!;
                parts.Add(new ImagePart(await tg.DownloadAsync(fileId), "image/jpeg"));
            }
            else if (msg.TryGetProperty("document", out var d))
            {
                var mime = d.TryGetProperty("mime_type", out var m) ? m.GetString() : "";
                var size = d.TryGetProperty("file_size", out var sz) ? sz.GetInt64() : 0;
                var fileId = d.GetProperty("file_id").GetString()!;
                if (mime == "application/pdf" && size < 10_000_000)
                    parts.Add(new PdfPart(await tg.DownloadAsync(fileId)));
                else if (mime is "image/jpeg" or "image/png" && size < 10_000_000)
                    parts.Add(new ImagePart(await tg.DownloadAsync(fileId), mime!));
                else
                    await tg.SendAsync(chatId, $"⚠️ Can't read this file type ({mime}).");
            }
            parts.Add(new TextPart(string.IsNullOrWhiteSpace(text) ? "(no text, see attachment)" : text));

            var raw = await llm.AskAsync(ExtractPrompt.Replace("{TODAY}", LlmUtil.Today()), parts);
            var json = LlmUtil.StripFences(raw);

            var saved = await table.GetEntityAsync<TableEntity>("tg", updateId);
            saved.Value["ResultJson"] = json;
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
            if (important.Count == 1 && !string.IsNullOrWhiteSpace(text) && text!.Length <= 1500)
                important[0].Body = text.Trim();

            await ProcessAsync(chatId, x);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Webhook failed");
            if (replyChat is long c)
            {
                try { await tg.SendAsync(c, "❌ " + Short(ex)); }
                catch (Exception inner) { log.LogError(inner, "Could not send error reply"); }
            }
        }
        return ok;
    }

    static long ChatOf(JsonElement message) => message.GetProperty("chat").GetProperty("id").GetInt64();

    static string Short(Exception ex)
    {
        var m = ex.Message.Replace("\n", " ");
        return m.Length > 200 ? m[..200] + "…" : m;
    }

    // ---------- extracted events -> calendar (or ask first) ----------
    async Task ProcessAsync(long chatId, Extraction x)
    {
        var lines = new List<string>();
        var confirmLow = Env.GetOr("CONFIRM_LOW_CONFIDENCE", "true") != "false";
        var today = JujuCalendar.TodayManila;
        var pending = Db.Table("Pending");

        foreach (var ev in x.Events)
        {
            try
            {
                if (!JujuCalendar.TryDate(ev.Date, out var date))
                {
                    lines.Add($"⚠️ No date: {ev.Title}");
                    continue;
                }
                if (date < today) ev.Confidence = "low"; // past dates are usually a misread year

                GEvent? match = null;
                var nearby = await cal.NearbyAsync(date);
                if (nearby.Count > 0)
                {
                    var matchId = await FindSimilarAsync(ev, nearby);
                    match = nearby.FirstOrDefault(g => g.Id == matchId);
                }

                var mustAsk = match != null || (confirmLow && ev.Confidence == "low");
                if (!mustAsk)
                {
                    await cal.InsertAsync(cal.Build(ev));
                    lines.Add($"✅ {ev.Title}, {JujuCalendar.When(ev)}");
                    continue;
                }

                // Hold the proposal until a button is tapped
                var pid = Guid.NewGuid().ToString("N")[..12];
                await pending.AddEntityAsync(new TableEntity("p", pid)
                {
                    ["EventJson"] = JsonSerializer.Serialize(ev),
                    ["MatchId"] = match?.Id ?? "",
                    ["Status"] = "pending"
                });

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
                lines.Add($"❌ {ev.Title}: {Short(ex)}");
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
        catch (Exception ex) when (ex is KeyNotFoundException ||
            (ex is Google.GoogleApiException g && (g.HttpStatusCode == HttpStatusCode.NotFound || g.HttpStatusCode == HttpStatusCode.Gone)))
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
        var bits = data.Split(':', 2);
        if (bits.Length != 2) { await tg.AnswerCallbackAsync(cbId); return; }
        var act = bits[0];
        var pid = bits[1];

        var table = Db.Table("Pending");
        TableEntity p;
        try { p = (await table.GetEntityAsync<TableEntity>("p", pid)).Value; }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            await tg.AnswerCallbackAsync(cbId, "Expired");
            return;
        }

        if (p.GetString("Status") != "pending") { await tg.AnswerCallbackAsync(cbId, "Already handled"); return; }

        // Claim it so a double tap can't run twice (ETag makes the second write fail)
        p["Status"] = "done";
        try { await table.UpdateEntityAsync(p, p.ETag); }
        catch (RequestFailedException e) when (e.Status == 412)
        {
            await tg.AnswerCallbackAsync(cbId, "Already handled");
            return;
        }

        await tg.AnswerCallbackAsync(cbId);

        var ev = JsonSerializer.Deserialize<ExtractedEvent>(p.GetString("EventJson")!)!;
        var matchId = p.GetString("MatchId") ?? "";
        var kind = p.GetString("Kind") ?? "tg";
        var fromSheet = kind.StartsWith("sheet", StringComparison.Ordinal);
        string result;
        try
        {
            switch (act)
            {
                case "s":
                    result = $"⏭ Skipped: {ev.Title}";
                    if (fromSheet)
                        await SyncStore.SetAsync(p.GetString("Gid")!, p.GetString("Rk")!, p.GetString("Key")!, p.GetString("Hash")!,
                            kind == "sheet-chg" ? matchId : "", kind == "sheet-chg" ? "synced" : "skipped", ev.Title);
                    break;
                case "u" when matchId.Length > 0:
                    var linkedId = await UpdateOrInsertAsync(matchId, ev, keepPrevious: kind != "sheet-chg");
                    result = $"✏️ Updated: {ev.Title}, {JujuCalendar.When(ev)}";
                    if (fromSheet)
                        await SyncStore.SetAsync(p.GetString("Gid")!, p.GetString("Rk")!, p.GetString("Key")!, p.GetString("Hash")!,
                            linkedId, "synced", ev.Title);
                    break;
                default:
                    var created = await cal.InsertAsync(cal.Build(ev));
                    result = $"✅ Added: {ev.Title}, {JujuCalendar.When(ev)}";
                    if (fromSheet)
                        await SyncStore.SetAsync(p.GetString("Gid")!, p.GetString("Rk")!, p.GetString("Key")!, p.GetString("Hash")!,
                            created.Id, "synced", ev.Title);
                    break;
            }
        }
        catch
        {
            // Let the buttons work again after a failure
            try { p["Status"] = "pending"; await table.UpdateEntityAsync(p, ETag.All); } catch { }
            throw;
        }

        await tg.EditAsync(chatId, messageId, result);
    }
}