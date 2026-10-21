using System.Text;
using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using GEvent = Google.Apis.Calendar.v3.Data.Event;

namespace SchoolAssistant;

public class SheetPollTimer(SheetSync sync)
{
    const string Hourly = "0 0 * * * *";

    [Function("sheet-poll")]
    public Task Run([TimerTrigger(Hourly)] TimerInfo timer) =>
        sync.RunAsync(long.Parse(Env.Get(Settings.TelegramChatId)), manual: false);
}

public class SheetSync(ILlmClient llm, TelegramApi tg, JujuCalendar cal, IHttpClientFactory http, ILogger<SheetSync> log)
{
    /// <summary>Footer added to each event description; also used to read the cell text back out.</summary>
    public const string SourceFooter = "— School tracker sheet";

    const int CsvHashHexChars = 16;
    const int MatchWindowDays = 7;          // look this far either side of an item for similar calendar events
    const int MaxCandidatesPerItem = 12;    // existing events shown to the LLM per new item
    const int AddedPreviewCount = 8;        // "added" lines listed in the summary before "…and N more"
    const int ChangedTextPreviewChars = 160;
    const int PromptCellTextChars = 400;
    const int FallbackTitleMaxChars = 60;
    const char ItemIdPrefix = 'i';          // items are "i0", "i1", ... in the prompts

    static readonly SemaphoreSlim Gate = new(initialCount: 1, maxCount: 1); // one sync at a time
    static readonly HashSet<string> Types = new()
    {
        EventTypes.WrittenWork, EventTypes.Quiz, EventTypes.Exam, EventTypes.PerformanceTask, EventTypes.Event, EventTypes.Other
    };

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
        if (!await Gate.WaitAsync(TimeSpan.Zero))
        {
            if (manual) await tg.SendAsync(chatId, "⏳ A sync is already running.");
            return;
        }
        try
        {
            var sheetId = Env.GetOr(Settings.SheetId, "");
            var gids = Env.GetOr(Settings.SheetGids, "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(g => g.All(char.IsDigit))
                .ToList();
            if (sheetId.Length == 0 || gids.Count == 0)
            {
                if (manual) await tg.SendAsync(chatId, $"⚠️ Set {Settings.SheetId} and {Settings.SheetGids} first.");
                return;
            }

            var lines = new List<string>();
            foreach (var gid in gids)
            {
                try { await SyncTabAsync(chatId, sheetId, gid, manual, lines); }
                catch (Exception ex)
                {
                    log.LogError(ex, "Sheet sync failed for tab {Gid}", gid);
                    if (manual) lines.Add($"❌ Sheet tab {gid}: {TextUtil.Short(ex)}");
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

        var csv = await DownloadCsvAsync(sheetId, gid);
        var csvHash = SheetParser.Sha(csv)[..CsvHashHexChars];
        if (!manual && await SheetStateStore.GetCsvHashAsync(gid) == csvHash) return; // unchanged since the last complete pass

        // Upcoming items only; the past is never back-filled
        var items = SheetParser.Parse(gid, SheetParser.ParseCsv(csv), today).Where(i => i.End >= today).ToList();
        if (items.Count == 0)
        {
            if (manual) lines.Add($"⚠️ No upcoming dated items found on tab {gid}.");
            await SheetStateStore.SaveCsvHashAsync(gid, csvHash);
            return;
        }

        // What did we already do?
        var synced = await SyncStore.LoadTabAsync(gid);
        var (fresh, changed) = Classify(items, synced);

        // Items we synced earlier that are no longer in the sheet: either moved to another first day or removed.
        var gone = await FindGoneAsync(gid, synced, items, today);
        var goneEvents = gone.ToDictionary(g => g.Row.RowKey, g => g.Ev);
        PairMoves(gone, fresh, changed);

        if (fresh.Count == 0 && changed.Count == 0 && gone.Count == 0)
        {
            await SheetStateStore.SaveCsvHashAsync(gid, csvHash);
            return;
        }

        // One LLM call names everything new or changed
        var all = fresh.Concat(changed.Select(c => c.Item)).ToList();
        var named = all.Count > 0 ? await NameItemsAsync(all) : new List<(string Title, string Type)>(); // nothing to name when items only vanished
        var events = all.Select((it, i) => ToEvent(it, named[i].Title, named[i].Type)).ToList();

        // One LLM call checks new items against events already on the calendar
        var matches = new Dictionary<int, GEvent>();
        if (fresh.Count > 0)
        {
            var existing = await cal.ListRangeAsync(
                fresh.Min(i => i.Start).AddDays(-MatchWindowDays), fresh.Max(i => i.End).AddDays(MatchWindowDays));
            var linked = synced.Values.Select(r => r.GetString(Columns.EventId)).Where(s => !string.IsNullOrEmpty(s)).ToHashSet();
            var pool = existing.Where(g => !linked.Contains(g.Id)).ToList();
            matches = await FindMatchesAsync(fresh, events, pool);
        }

        var failed = false;
        var added = new List<string>();

        void Fail(Exception ex, string what, string line)
        {
            failed = true;
            log.LogError(ex, "Sheet {What} failed", what);
            if (manual) lines.Add(line);
        }

        for (var i = 0; i < fresh.Count; i++)
        {
            var it = fresh[i];
            var ev = events[i];
            try
            {
                if (matches.TryGetValue(i, out var hit))
                {
                    await PromptAsync(chatId, PendingKind.SheetNew, it, ev, hit.Id,
                        $"❓ Similar event on calendar:\n• {hit.Summary}, {JujuCalendar.WhenEvent(hit)}\nSheet: {ev.Title}, {JujuCalendar.When(ev)}",
                        KeyboardKind.Match);
                    continue;
                }
                var created = await cal.InsertAsync(cal.Build(ev));
                await SyncStore.SetAsync(gid, it.Rk, it.Key, it.Hash, created.Id, SyncStatus.Synced, ev.Title);
                added.Add($"✅ {ev.Title}, {JujuCalendar.When(ev)}");
            }
            catch (Exception ex) { Fail(ex, $"item '{ev.Title}'", $"❌ {ev.Title}: {TextUtil.Short(ex)}"); }
        }

        for (var k = 0; k < changed.Count; k++)
        {
            var (it, old) = changed[k];
            var ev = events[fresh.Count + k];
            try
            {
                var moved = old.RowKey != it.Rk;
                var was = old.GetString(Columns.Title) ?? it.Subject;
                if (moved && goneEvents.TryGetValue(old.RowKey, out var oldEv)) was += ", " + JujuCalendar.WhenEvent(oldEv);
                await PromptAsync(chatId, PendingKind.SheetChanged, it, ev, old.GetString(Columns.EventId) ?? "",
                    $"❓ Sheet {(moved ? "date moved" : "changed")}:\n• Was: {was}\nNow: {ev.Title}, {JujuCalendar.When(ev)}\n{TextUtil.Flat(it.Text, ChangedTextPreviewChars)}",
                    KeyboardKind.Update, moved ? old.RowKey : "");
            }
            catch (Exception ex) { Fail(ex, $"change prompt '{ev.Title}'", $"❌ {ev.Title}: {TextUtil.Short(ex)}"); }
        }

        foreach (var (row, gEv) in gone)
        {
            try { await PromptRemovedAsync(chatId, gid, row, gEv); }
            catch (Exception ex) { Fail(ex, $"removal prompt {row.RowKey}", $"❌ Removal check: {TextUtil.Short(ex)}"); }
        }

        if (added.Count > 0)
        {
            lines.Add($"📊 Sheet: {added.Count} added");
            lines.AddRange(added.Take(AddedPreviewCount));
            if (added.Count > AddedPreviewCount) lines.Add($"…and {added.Count - AddedPreviewCount} more");
        }

        if (!failed) await SheetStateStore.SaveCsvHashAsync(gid, csvHash); // otherwise retry next time
    }

    async Task<string> DownloadCsvAsync(string sheetId, string gid)
    {
        const char ByteOrderMark = '﻿';

        var csv = await http.CreateClient().GetStringAsync(
            $"https://docs.google.com/spreadsheets/d/{sheetId}/export?format=csv&gid={gid}");
        if (csv.Length > 0 && csv[0] == ByteOrderMark) csv = csv[1..];
        if (csv.TrimStart().StartsWith('<')) // an HTML sign-in page instead of CSV
            throw new InvalidOperationException("The sheet asked for a sign-in. It must be viewable by anyone with the link.");
        return csv;
    }

    // New items, and items whose text or end date changed since we synced them.
    static (List<SheetItem> Fresh, List<(SheetItem Item, TableEntity Row)> Changed) Classify(
        List<SheetItem> items, Dictionary<string, TableEntity> synced)
    {
        var fresh = new List<SheetItem>();
        var changed = new List<(SheetItem Item, TableEntity Row)>();
        foreach (var it in items)
        {
            synced.TryGetValue(it.Rk, out var rowState);
            var hash = it.Hash;
            if (rowState != null && (rowState.GetString(Columns.Hash) == hash || rowState.GetString(Columns.AskedHash) == hash))
                continue; // already applied, skipped, or already asked about

            if (rowState != null && rowState.GetString(Columns.Status) == SyncStatus.Synced && !string.IsNullOrEmpty(rowState.GetString(Columns.EventId)))
                changed.Add((it, rowState));
            else
                fresh.Add(it);
        }
        return (fresh, changed);
    }

    // A vanished item with a matching new one is a date move: handled as a change of the old row, not an add plus a delete.
    static void PairMoves(List<(TableEntity Row, GEvent Ev)> gone, List<SheetItem> fresh, List<(SheetItem Item, TableEntity Row)> changed)
    {
        if (gone.Count == 0 || fresh.Count == 0) return;
        foreach (var g in gone.ToList())
        {
            var move = FindMove(g, fresh);
            if (move == null) continue;
            fresh.Remove(move);
            changed.Add((move, g.Row));
            gone.Remove(g);
        }
    }

    // Pending proposal + buttons; also remembers we asked, so the next sync doesn't repeat it.
    // oldRk is set when the item moved to a new first day: the old row is closed once the user answers.
    async Task PromptAsync(long chatId, string kind, SheetItem it, ExtractedEvent ev, string matchId, string text, KeyboardKind keys, string oldRk = "")
    {
        var pid = await PendingStore.AddAsync(ev, matchId, new Dictionary<string, object>
        {
            [Columns.Kind] = kind,
            [Columns.Gid] = it.Gid,
            [Columns.Rk] = it.Rk,
            [Columns.Key] = it.Key,
            [Columns.Hash] = it.Hash,
            [Columns.OldRk] = oldRk
        });
        await SyncStore.AskedAsync(it.Gid, it.Rk, it.Key, it.Hash);
        if (oldRk.Length > 0) await SyncStore.GoneAskedAsync(it.Gid, oldRk); // don't also ask to delete it
        await tg.SendAsync(chatId, text, TelegramApi.Keyboard(pid, keys));
    }

    async Task PromptRemovedAsync(long chatId, string gid, TableEntity row, GEvent gEv)
    {
        var title = gEv.Summary ?? row.GetString(Columns.Title) ?? "Event";
        var ev = new ExtractedEvent { Title = title, Date = JujuCalendar.StartDay(gEv)?.ToString(Formats.IsoDate, Formats.Inv) };
        var pid = await PendingStore.AddAsync(ev, row.GetString(Columns.EventId) ?? "", new Dictionary<string, object>
        {
            [Columns.Kind] = PendingKind.SheetRemoved,
            [Columns.Gid] = gid,
            [Columns.Rk] = row.RowKey
        });
        await SyncStore.GoneAskedAsync(gid, row.RowKey);
        await tg.SendAsync(chatId, $"❓ Removed from the sheet:\n• {title}, {JujuCalendar.WhenEvent(gEv)}\nDelete it from the calendar?",
            TelegramApi.Keyboard(pid, KeyboardKind.Remove));
    }

    // Synced rows whose sheet item has disappeared. Only upcoming ones, and only if the calendar event still exists.
    async Task<List<(TableEntity Row, GEvent Ev)>> FindGoneAsync(string gid, Dictionary<string, TableEntity> synced, List<SheetItem> items, DateOnly today)
    {
        var live = items.Select(i => i.Rk).ToHashSet();
        var result = new List<(TableEntity, GEvent)>();
        foreach (var row in synced.Values)
        {
            var eventId = row.GetString(Columns.EventId);
            if (live.Contains(row.RowKey) || row.GetString(Columns.Status) != SyncStatus.Synced || string.IsNullOrEmpty(eventId)
                || SyncStore.IsGoneAsked(row)) continue;

            if (!SheetItem.TryParseKey(row.GetString(Columns.Key), out _, out var start) || start < today)
                continue; // the past is left alone

            var ev = await cal.TryGetAsync(eventId);
            if (ev == null) { await SyncStore.MarkAsync(gid, row.RowKey, SyncStatus.Removed); continue; } // already deleted by hand
            result.Add((row, ev));
        }
        return result;
    }

    // A new item that is the same subject and cell text as a vanished one, closest in date: the date was moved.
    static SheetItem? FindMove((TableEntity Row, GEvent Ev) gone, List<SheetItem> fresh)
    {
        if (!SheetItem.TryParseKey(gone.Row.GetString(Columns.Key), out var subject, out _)) return null;
        var text = SheetParser.Norm(JujuCalendar.SheetText(gone.Ev));
        if (text.Length == 0 || JujuCalendar.StartDay(gone.Ev) is not DateOnly oldStart) return null;

        return fresh
            .Where(f => string.Equals(f.Subject, subject, StringComparison.OrdinalIgnoreCase) && SheetParser.Norm(f.Text) == text)
            .OrderBy(f => Math.Abs(f.Start.DayNumber - oldStart.DayNumber))
            .FirstOrDefault();
    }

    static ExtractedEvent ToEvent(SheetItem it, string title, string type) => new()
    {
        Type = type,
        Title = title,
        Date = it.Start.ToString(Formats.IsoDate, Formats.Inv),
        EndDate = it.End > it.Start ? it.End.ToString(Formats.IsoDate, Formats.Inv) : null,
        Description = $"{it.Text}\n\n{SourceFooter} · {it.Subject} · {it.CellRef}",
        Confidence = Confidence.High
    };

    // ---------- LLM helpers ----------
    async Task<List<(string Title, string Type)>> NameItemsAsync(List<SheetItem> all)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < all.Count; i++)
        {
            var it = all[i];
            var when = it.Start.ToString(Formats.ShortDay, Formats.Inv)
                       + (it.End > it.Start ? Formats.RangeSeparator + it.End.ToString(Formats.ShortDay, Formats.Inv) : "");
            sb.AppendLine($"{ItemIdPrefix}{i} | {it.Subject} | {when} | {TextUtil.Flat(it.Text, PromptCellTextChars)}");
        }

        var result = all.Select(it => (Title: Fallback(it), Type: EventTypes.Event)).ToList();
        var raw = await llm.AskAsync(TitlePrompt.Replace("{TODAY}", LlmUtil.Today()), new LlmPart[] { new TextPart(sb.ToString()) });
        try
        {
            using var doc = JsonDocument.Parse(LlmUtil.StripFences(raw));
            foreach (var o in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var id = o.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (!TryItemIndex(id, result.Count, out var idx)) continue;
                var title = o.TryGetProperty("title", out var tEl) ? tEl.GetString()?.Trim() : null;
                var type = o.TryGetProperty("type", out var tyEl) ? tyEl.GetString()?.Trim().ToLowerInvariant() : null;
                result[idx] = (
                    string.IsNullOrEmpty(title) ? result[idx].Title : title,
                    type != null && Types.Contains(type) ? type : EventTypes.Event);
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
                return s is DateOnly sd && e is DateOnly ed
                       && sd <= it.End.AddDays(MatchWindowDays) && ed >= it.Start.AddDays(-MatchWindowDays);
            }).Take(MaxCandidatesPerItem).ToList();
            if (near.Count == 0) continue;

            cands[i] = near;
            sb.AppendLine($"NEW {ItemIdPrefix}{i}: {events[i].Type} | {JujuCalendar.When(events[i])} | {events[i].Title}");
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
                if (existingId is null || !TryItemIndex(id, fresh.Count, out var idx)) continue;
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

    // "i3" -> 3, if it is a valid index into a list of `count` items.
    static bool TryItemIndex(string? id, int count, out int index)
    {
        index = -1;
        return id is not null && id.StartsWith(ItemIdPrefix)
            && int.TryParse(id[1..], out index) && index >= 0 && index < count;
    }

    static string Fallback(SheetItem it)
    {
        var subject = Formats.Inv.TextInfo.ToTitleCase(it.Subject.ToLowerInvariant());
        var first = it.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? it.Text;
        var title = $"{subject}: {first}";
        return title.Length > FallbackTitleMaxChars ? title[..FallbackTitleMaxChars] : title;
    }
}
