using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using GEvent = Google.Apis.Calendar.v3.Data.Event;

namespace SchoolAssistant;

/// <summary>What we've already done for each sheet item (one row per tab + subject + first day).</summary>
internal static class SyncStore
{
    public static Task SetAsync(string gid, string rk, string key, string hash, string eventId, string status, string title) =>
        Db.Table("Synced").UpsertEntityAsync(new TableEntity(gid, rk)
        {
            ["Key"] = key,
            ["Hash"] = hash,
            ["AskedHash"] = hash,
            ["EventId"] = eventId,
            ["Status"] = status,
            ["Title"] = title
        }, TableUpdateMode.Replace);

    // Remember that we asked about this version, so we don't ask again every hour.
    public static Task AskedAsync(string gid, string rk, string key, string askedHash) =>
        Db.Table("Synced").UpsertEntityAsync(new TableEntity(gid, rk)
        {
            ["Key"] = key,
            ["AskedHash"] = askedHash
        }, TableUpdateMode.Merge);
}

public class SheetPollTimer(SheetSync sync)
{
    [Function("sheet-poll")]
    public Task Run([TimerTrigger("0 0 * * * *")] TimerInfo timer) => // hourly
        sync.RunAsync(long.Parse(Env.Get("TELEGRAM_CHAT_ID")), manual: false);
}

public class SheetSync(ILlmClient llm, TelegramApi tg, JujuCalendar cal, IHttpClientFactory http, ILogger<SheetSync> log)
{
    static readonly SemaphoreSlim Gate = new(1, 1);
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly HashSet<string> Types = new() { "written_work", "quiz", "exam", "performance_task", "event", "other" };

    const string TitlePrompt = """
        You name calendar events from a school's Grade 1 homework, test and task tracker. Each ITEM is one cell of the tracker: subject, date (or date range) and the cell text. Today is {TODAY}.
        Reply with ONLY JSON: {"items":[{"id":"i0","title":"...","type":"written_work|quiz|exam|performance_task|event|other"}]}
        Rules:
        - title: short (at most 8 words) and includes the subject, e.g. "Math Written Work 3", "English 2Q Exam", "Science Written Work 2", "Music 2Q Exam", "English Performance Task". For things that apply to the whole school (a parent-teacher conference, a school-wide activity, a no-classes day) leave the subject out, e.g. "Parent-Teacher Conference (No Classes)".
        - Use normal capitalization, not ALL CAPS. Expand "WW" to "Written Work" and "PT" to "Performance Task".
        - type: written_work, quiz (a quiz or "Maikling Pagsusulit"), exam (quarterly exam, "2Q EXAM"), performance_task, event (activities, review days, conferences, homework), or other.
        - Never invent details that are not in the cell text.
        """;

    const string MatchPrompt = """
        You match NEW school tracker items against EXISTING calendar events.
        Reply with ONLY JSON: {"matches":[{"id":"i0","existing":"<event id>"}]}. List only items that match; leave out items with no match.
        Match only if the NEW item is clearly the same real-world event as one EXISTING entry: same subject and kind (for example both are an English Performance Task), even if the date or time differs.
        Different subjects or kinds never match. If unsure, leave it out.
        """;

    /// <param name="manual">true for the /sync command: always reports back, and re-checks even if the sheet looks unchanged.</param>
    public async Task RunAsync(long chatId, bool manual)
    {
        if (!await Gate.WaitAsync(0))
        {
            if (manual) await tg.SendAsync(chatId, "⏳ A sync is already running.");
            return;
        }
        try
        {
            var sheetId = Env.GetOr("SHEET_ID", "");
            var gids = Env.GetOr("SHEET_GIDS", "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(g => g.All(char.IsDigit))
                .ToList();
            if (sheetId.Length == 0 || gids.Count == 0)
            {
                if (manual) await tg.SendAsync(chatId, "⚠️ Set SHEET_ID and SHEET_GIDS first.");
                return;
            }

            var lines = new List<string>();
            foreach (var gid in gids)
            {
                try { await SyncTabAsync(chatId, sheetId, gid, manual, lines); }
                catch (Exception ex)
                {
                    log.LogError(ex, "Sheet sync failed for tab {Gid}", gid);
                    if (manual) lines.Add($"❌ Sheet tab {gid}: {Short(ex)}");
                }
            }

            if (lines.Count == 0 && manual) lines.Add("📊 Sheet is up to date.");
            if (lines.Count > 0) await tg.SendAsync(chatId, string.Join("\n", lines));
        }
        finally { Gate.Release(); }
    }

    async Task SyncTabAsync(long chatId, string sheetId, string gid, bool manual, List<string> lines)
    {
        var today = JujuCalendar.TodayManila;

        var csv = await http.CreateClient().GetStringAsync(
            $"https://docs.google.com/spreadsheets/d/{sheetId}/export?format=csv&gid={gid}");
        if (csv.Length > 0 && csv[0] == '﻿') csv = csv[1..];
        if (csv.TrimStart().StartsWith('<'))
            throw new InvalidOperationException("The sheet asked for a sign-in. It must be viewable by anyone with the link.");

        var csvHash = SheetParser.Sha(csv)[..16];
        var state = Db.Table("State");
        var stateRk = "csv-" + gid;
        string? prevHash = null;
        try { prevHash = (await state.GetEntityAsync<TableEntity>("sheet", stateRk)).Value.GetString("Hash"); }
        catch (RequestFailedException e) when (e.Status == 404) { }
        if (!manual && prevHash == csvHash) return; // unchanged since the last complete pass

        // Upcoming items only; the past is never back-filled
        var items = SheetParser.Parse(gid, SheetParser.ParseCsv(csv), today).Where(i => i.End >= today).ToList();
        if (items.Count == 0)
        {
            if (manual) lines.Add($"⚠️ No upcoming dated items found on tab {gid}.");
            await SaveHashAsync(state, stateRk, csvHash);
            return;
        }

        // What did we already do?
        var synced = new Dictionary<string, TableEntity>();
        await foreach (var existingRow in Db.Table("Synced").QueryAsync<TableEntity>(filter: $"PartitionKey eq '{gid}'"))
            synced[existingRow.RowKey] = existingRow;

        var fresh = new List<SheetItem>();
        var changed = new List<(SheetItem Item, TableEntity Row)>();
        foreach (var it in items)
        {
            synced.TryGetValue(it.Rk, out var rowState);
            var hash = it.Hash;
            if (rowState != null && (rowState.GetString("Hash") == hash || rowState.GetString("AskedHash") == hash))
                continue; // already applied, skipped, or already asked about

            if (rowState != null && rowState.GetString("Status") == "synced" && !string.IsNullOrEmpty(rowState.GetString("EventId")))
                changed.Add((it, rowState));
            else
                fresh.Add(it);
        }

        if (fresh.Count == 0 && changed.Count == 0)
        {
            await SaveHashAsync(state, stateRk, csvHash);
            return;
        }

        // One LLM call names everything new or changed
        var all = fresh.Concat(changed.Select(c => c.Item)).ToList();
        var named = await NameItemsAsync(all);
        var events = all.Select((it, i) => ToEvent(it, named[i].Title, named[i].Type)).ToList();

        // One LLM call checks new items against events already on the calendar
        var matches = new Dictionary<int, GEvent>();
        if (fresh.Count > 0)
        {
            var existing = await cal.ListRangeAsync(fresh.Min(i => i.Start).AddDays(-7), fresh.Max(i => i.End).AddDays(7));
            var linked = synced.Values.Select(r => r.GetString("EventId")).Where(s => !string.IsNullOrEmpty(s)).ToHashSet();
            var pool = existing.Where(g => !linked.Contains(g.Id)).ToList();
            matches = await FindMatchesAsync(fresh, events, pool);
        }

        var failed = false;
        var added = new List<string>();

        for (var i = 0; i < fresh.Count; i++)
        {
            var it = fresh[i];
            var ev = events[i];
            try
            {
                if (matches.TryGetValue(i, out var hit))
                {
                    await PromptAsync(chatId, "sheet-new", it, ev, hit.Id,
                        $"❓ Similar event on calendar:\n• {hit.Summary}, {JujuCalendar.WhenEvent(hit)}\nSheet: {ev.Title}, {JujuCalendar.When(ev)}",
                        KeyboardKind.Match);
                    continue;
                }
                var created = await cal.InsertAsync(cal.Build(ev));
                await SyncStore.SetAsync(gid, it.Rk, it.Key, it.Hash, created.Id, "synced", ev.Title);
                added.Add($"✅ {ev.Title}, {JujuCalendar.When(ev)}");
            }
            catch (Exception ex)
            {
                failed = true;
                log.LogError(ex, "Sheet item failed: {Title}", ev.Title);
                if (manual) lines.Add($"❌ {ev.Title}: {Short(ex)}");
            }
        }

        for (var k = 0; k < changed.Count; k++)
        {
            var (it, old) = changed[k];
            var ev = events[fresh.Count + k];
            try
            {
                await PromptAsync(chatId, "sheet-chg", it, ev, old.GetString("EventId") ?? "",
                    $"❓ Sheet changed:\n• Was: {old.GetString("Title") ?? it.Subject}\nNow: {ev.Title}, {JujuCalendar.When(ev)}\n{Flat(it.Text, 160)}",
                    KeyboardKind.Update);
            }
            catch (Exception ex)
            {
                failed = true;
                log.LogError(ex, "Sheet change prompt failed: {Title}", ev.Title);
                if (manual) lines.Add($"❌ {ev.Title}: {Short(ex)}");
            }
        }

        if (added.Count > 0)
        {
            lines.Add($"📊 Sheet: {added.Count} added");
            lines.AddRange(added.Take(8));
            if (added.Count > 8) lines.Add($"…and {added.Count - 8} more");
        }

        if (!failed) await SaveHashAsync(state, stateRk, csvHash); // otherwise retry next time
    }

    static Task SaveHashAsync(TableClient state, string rk, string hash) =>
        state.UpsertEntityAsync(new TableEntity("sheet", rk) { ["Hash"] = hash });

    // Pending proposal + buttons; also remembers we asked, so the next sync doesn't repeat it.
    async Task PromptAsync(long chatId, string kind, SheetItem it, ExtractedEvent ev, string matchId, string text, KeyboardKind keys)
    {
        var pid = Guid.NewGuid().ToString("N")[..12];
        await Db.Table("Pending").AddEntityAsync(new TableEntity("p", pid)
        {
            ["EventJson"] = JsonSerializer.Serialize(ev),
            ["MatchId"] = matchId,
            ["Status"] = "pending",
            ["Kind"] = kind,
            ["Gid"] = it.Gid,
            ["Rk"] = it.Rk,
            ["Key"] = it.Key,
            ["Hash"] = it.Hash
        });
        await SyncStore.AskedAsync(it.Gid, it.Rk, it.Key, it.Hash);
        await tg.SendAsync(chatId, text, TelegramApi.Keyboard(pid, keys));
    }

    static ExtractedEvent ToEvent(SheetItem it, string title, string type) => new()
    {
        Type = type,
        Title = title,
        Date = it.Start.ToString("yyyy-MM-dd", Inv),
        EndDate = it.End > it.Start ? it.End.ToString("yyyy-MM-dd", Inv) : null,
        Description = $"{it.Text}\n\n— School tracker sheet · {it.Subject} · {it.CellRef}",
        Confidence = "high"
    };

    // ---------- LLM helpers ----------
    async Task<List<(string Title, string Type)>> NameItemsAsync(List<SheetItem> all)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < all.Count; i++)
        {
            var it = all[i];
            var when = it.Start.ToString("ddd MMM d", Inv) + (it.End > it.Start ? " – " + it.End.ToString("ddd MMM d", Inv) : "");
            sb.AppendLine($"i{i} | {it.Subject} | {when} | {Flat(it.Text, 400)}");
        }

        var result = all.Select(it => (Title: Fallback(it), Type: "event")).ToList();
        var raw = await llm.AskAsync(TitlePrompt.Replace("{TODAY}", LlmUtil.Today()), new LlmPart[] { new TextPart(sb.ToString()) });
        try
        {
            using var doc = JsonDocument.Parse(LlmUtil.StripFences(raw));
            foreach (var o in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var id = o.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (id is null || !id.StartsWith('i') || !int.TryParse(id[1..], out var idx) || idx < 0 || idx >= result.Count) continue;
                var title = o.TryGetProperty("title", out var tEl) ? tEl.GetString()?.Trim() : null;
                var type = o.TryGetProperty("type", out var tyEl) ? tyEl.GetString()?.Trim().ToLowerInvariant() : null;
                result[idx] = (
                    string.IsNullOrEmpty(title) ? result[idx].Title : title,
                    type != null && Types.Contains(type) ? type : "event");
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            log.LogWarning(ex, "Couldn't read item names from the model; using fallback titles");
        }
        return result;
    }

    async Task<Dictionary<int, GEvent>> FindMatchesAsync(List<SheetItem> fresh, List<ExtractedEvent> events, List<GEvent> pool)
    {
        var result = new Dictionary<int, GEvent>();
        var cands = new Dictionary<int, List<GEvent>>();
        var sb = new StringBuilder();

        for (var i = 0; i < fresh.Count; i++)
        {
            var it = fresh[i];
            var near = pool.Where(g =>
            {
                var s = JujuCalendar.StartDay(g);
                var e = JujuCalendar.EndDay(g);
                return s is DateOnly sd && e is DateOnly ed && sd <= it.End.AddDays(7) && ed >= it.Start.AddDays(-7);
            }).Take(12).ToList();
            if (near.Count == 0) continue;

            cands[i] = near;
            sb.AppendLine($"NEW i{i}: {events[i].Type} | {JujuCalendar.When(events[i])} | {events[i].Title}");
            foreach (var g in near) sb.AppendLine("  " + JujuCalendar.Line(g));
        }
        if (cands.Count == 0) return result;

        var raw = await llm.AskAsync(MatchPrompt, new LlmPart[] { new TextPart(sb.ToString()) });
        try
        {
            using var doc = JsonDocument.Parse(LlmUtil.StripFences(raw));
            if (!doc.RootElement.TryGetProperty("matches", out var arr) || arr.ValueKind != JsonValueKind.Array) return result;

            foreach (var m in arr.EnumerateArray())
            {
                var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var existingId = m.TryGetProperty("existing", out var exEl) && exEl.ValueKind == JsonValueKind.String ? exEl.GetString() : null;
                if (id is null || existingId is null || !id.StartsWith('i') || !int.TryParse(id[1..], out var idx)) continue;
                if (!cands.TryGetValue(idx, out var near2)) continue;
                var hit = near2.FirstOrDefault(g => g.Id == existingId); // ignore ids the model made up
                if (hit != null) result[idx] = hit;
            }
        }
        catch (JsonException ex)
        {
            // Better to add the items than to stop the whole sync
            log.LogWarning(ex, "Couldn't read the similar-event check; adding items without it");
        }
        return result;
    }

    // ---------- small helpers ----------
    static string Flat(string s, int max)
    {
        var t = Regex.Replace(s, @"\s+", " ").Trim();
        return t.Length > max ? t[..max] + "…" : t;
    }

    static string Fallback(SheetItem it)
    {
        var subject = Inv.TextInfo.ToTitleCase(it.Subject.ToLowerInvariant());
        var first = it.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? it.Text;
        var title = $"{subject}: {first}";
        return title.Length > 60 ? title[..60] : title;
    }

    static string Short(Exception ex)
    {
        var m = ex.Message.Replace("\n", " ");
        return m.Length > 200 ? m[..200] + "…" : m;
    }
}