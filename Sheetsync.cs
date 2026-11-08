using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using GEvent = Google.Apis.Calendar.v3.Data.Event;

namespace SchoolAssistant;

public class SheetPollTimer(SheetSync sync)
{
    const string Hourly = "0 0 * * * *"; // NCRONTAB: top of every hour

    [Function("sheet-poll")]
    public Task Run([TimerTrigger(Hourly)] TimerInfo timer) =>
        sync.RunAsync(Env.TelegramChatId, manual: false);
}

public class SheetSync(
    ILlmClient llm, TelegramApi telegram, JujuCalendar calendar, ISheetTabLister tabLister, IHttpClientFactory httpFactory, ILogger<SheetSync> log)
{
    /// <summary>Footer added to each event description; also used to read the cell text back out.</summary>
    public const string SourceFooter = "— School tracker sheet";

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
            if (manual) await telegram.SendAsync(chatId, "⏳ A sync is already running.");
            return;
        }
        try
        {
            var sheetId = Env.GetOr(Settings.SheetId, "");
            if (sheetId.Length == 0)
            {
                if (manual) await telegram.SendAsync(chatId, $"⚠️ Set {Settings.SheetId} first.");
                return;
            }

            IReadOnlyList<string> gids;
            try { gids = await ResolveTabsAsync(sheetId); }
            catch (Exception ex)
            {
                log.LogError(ex, "Could not list the tabs of the sheet");
                if (manual) await telegram.SendAsync(chatId, $"❌ {TextUtil.Short(ex)}");
                return;
            }
            if (gids.Count == 0)
            {
                if (manual) await telegram.SendAsync(chatId, "⚠️ The sheet has no visible tabs.");
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
            if (lines.Count > 0) await telegram.SendAsync(chatId, string.Join("\n", lines));
        }
        finally { Gate.Release(); }
    }

    /// <summary>What one tab's sync reports back, and whether anything failed (so the sheet is retried next time).</summary>
    sealed class TabReport(bool manual, ILogger log)
    {
        public List<string> Lines { get; } = new();
        public bool Failed { get; private set; }

        public void Fail(Exception ex, string what, string line)
        {
            Failed = true;
            log.LogError(ex, "Sheet {What} failed", what);
            if (manual) Lines.Add(line);
        }
    }

    // SHEET_GIDS, when set, picks the tabs by hand. Otherwise every visible tab is synced, so a new month needs no setting change.
    async Task<IReadOnlyList<string>> ResolveTabsAsync(string sheetId)
    {
        var configured = Env.GetOr(Settings.SheetGids, "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(g => g.All(char.IsDigit))
            .ToList();
        return configured.Count > 0 ? configured : await tabLister.ListGidsAsync(sheetId);
    }

    async Task SyncTabAsync(long chatId, string sheetId, string gid, bool manual, List<string> lines)
    {
        var today = ManilaTime.Today;

        var csv = await DownloadCsvAsync(sheetId, gid);
        var csvHash = SheetParser.Sha(csv, SheetItem.ContentHashHexChars);
        if (!manual && await SheetStateStore.GetCsvHashAsync(gid) == csvHash) return; // unchanged since the last complete pass

        // Upcoming items only; the past is never back-filled
        var items = SheetParser.Parse(gid, SheetParser.ParseCsv(csv), today).Where(i => i.End >= today).ToList();
        if (items.Count == 0)
        {
            log.LogInformation("Tab {Gid} has no upcoming dated items", gid); // old months are normal, so no chat message
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

        // One LLM call names everything new or changed (nothing to name when items only vanished)
        var allItems = fresh.Concat(changed.Select(c => c.Item)).ToList();
        var names = allItems.Count > 0 ? await NameItemsAsync(allItems) : new List<(string Title, string Type)>();
        var allEvents = allItems.Select((item, i) => ToEvent(item, names[i].Title, names[i].Type)).ToList();
        var freshEvents = allEvents.Take(fresh.Count).ToList();
        var changedEvents = allEvents.Skip(fresh.Count).ToList();

        var report = new TabReport(manual, log);
        var addedLines = await AddOrAskAboutNewItemsAsync(chatId, gid, fresh, freshEvents, synced, report);
        await AskAboutChangesAsync(chatId, changed, changedEvents, goneEvents, report);
        await AskAboutRemovalsAsync(chatId, gid, gone, report);

        lines.AddRange(report.Lines);
        if (addedLines.Count > 0)
        {
            lines.Add($"📊 Sheet: {addedLines.Count} added");
            lines.AddRange(addedLines.Take(AddedPreviewCount));
            if (addedLines.Count > AddedPreviewCount) lines.Add($"…and {addedLines.Count - AddedPreviewCount} more");
        }

        if (!report.Failed) await SheetStateStore.SaveCsvHashAsync(gid, csvHash); // otherwise retry next time
    }

    /// <summary>Adds items with no look-alike on the calendar; asks about the rest. Returns a summary line per added item.</summary>
    async Task<List<string>> AddOrAskAboutNewItemsAsync(
        long chatId, string gid, List<SheetItem> fresh, List<ExtractedEvent> freshEvents,
        Dictionary<string, TableEntity> synced, TabReport report)
    {
        var addedLines = new List<string>();
        if (fresh.Count == 0) return addedLines;

        // One LLM call checks new items against events already on the calendar
        var existing = await calendar.ListRangeAsync(
            fresh.Min(i => i.Start).AddDays(-MatchWindowDays), fresh.Max(i => i.End).AddDays(MatchWindowDays));
        var linkedEventIds = synced.Values.Select(r => r.GetString(Columns.EventId)).Where(id => !string.IsNullOrEmpty(id)).ToHashSet();
        var unlinked = existing.Where(g => !linkedEventIds.Contains(g.Id)).ToList();
        var matches = await FindMatchesAsync(fresh, freshEvents, unlinked);

        for (var i = 0; i < fresh.Count; i++)
        {
            var item = fresh[i];
            var extracted = freshEvents[i];
            try
            {
                if (matches.TryGetValue(i, out var similar))
                {
                    await PromptAsync(chatId, PendingKind.SheetNew, item, extracted, similar.Id,
                        $"❓ Similar event on calendar:\n• {similar.Summary}, {EventText.When(similar)}\nSheet: {extracted.Title}, {EventText.When(extracted)}",
                        KeyboardKind.Match);
                    continue;
                }
                var created = await calendar.InsertAsync(CalendarEventBuilder.Build(extracted));
                await SyncStore.SetAsync(gid, item.Rk, item.Key, item.Hash, created.Id, SyncStatus.Synced, extracted.Title);
                addedLines.Add($"✅ {extracted.Title}, {EventText.When(extracted)}");
            }
            catch (Exception ex) { report.Fail(ex, $"item '{extracted.Title}'", $"❌ {extracted.Title}: {TextUtil.Short(ex)}"); }
        }
        return addedLines;
    }

    async Task AskAboutChangesAsync(
        long chatId, List<(SheetItem Item, TableEntity Row)> changed, List<ExtractedEvent> changedEvents,
        Dictionary<string, GEvent> goneEvents, TabReport report)
    {
        for (var i = 0; i < changed.Count; i++)
        {
            var (item, oldRow) = changed[i];
            var extracted = changedEvents[i];
            try
            {
                var moved = oldRow.RowKey != item.Rk;
                var was = oldRow.GetString(Columns.Title) ?? item.Subject;
                if (moved && goneEvents.TryGetValue(oldRow.RowKey, out var oldEvent)) was += ", " + EventText.When(oldEvent);
                await PromptAsync(chatId, PendingKind.SheetChanged, item, extracted, oldRow.GetString(Columns.EventId) ?? "",
                    $"❓ Sheet {(moved ? "date moved" : "changed")}:\n• Was: {was}\nNow: {extracted.Title}, {EventText.When(extracted)}\n{TextUtil.Flat(item.Text, ChangedTextPreviewChars)}",
                    KeyboardKind.Update, moved ? oldRow.RowKey : "");
            }
            catch (Exception ex) { report.Fail(ex, $"change prompt '{extracted.Title}'", $"❌ {extracted.Title}: {TextUtil.Short(ex)}"); }
        }
    }

    async Task AskAboutRemovalsAsync(long chatId, string gid, List<(TableEntity Row, GEvent Ev)> gone, TabReport report)
    {
        foreach (var (row, calendarEvent) in gone)
        {
            try { await PromptRemovedAsync(chatId, gid, row, calendarEvent); }
            catch (Exception ex) { report.Fail(ex, $"removal prompt {row.RowKey}", $"❌ Removal check: {TextUtil.Short(ex)}"); }
        }
    }

    async Task<string> DownloadCsvAsync(string sheetId, string gid)
    {
        const char ByteOrderMark = '﻿';

        var response = await httpFactory.CreateClient().GetAsync(
            $"https://docs.google.com/spreadsheets/d/{sheetId}/export?format=csv&gid={gid}");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ExplainDownloadFailure(response.StatusCode, sheetId, gid));
        var csv = await response.Content.ReadAsStringAsync();
        if (csv.Length > 0 && csv[0] == ByteOrderMark) csv = csv[1..];
        if (csv.TrimStart().StartsWith('<')) // an HTML sign-in page instead of CSV
            throw new InvalidOperationException("The sheet asked for a sign-in. It must be viewable by anyone with the link.");
        return csv;
    }

    // What the person can do about each kind of failure, with the sheet and tab named.
    static string ExplainDownloadFailure(HttpStatusCode status, string sheetId, string gid) => status switch
    {
        HttpStatusCode.BadRequest =>
            $"Google rejected sheet {sheetId} tab {gid} (400). Check {Settings.SheetId} and {Settings.SheetGids}.",
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            $"Google denied access to sheet {sheetId} tab {gid} ({(int)status}). Share the sheet so anyone with the link can view it.",
        HttpStatusCode.NotFound =>
            $"Sheet {sheetId} tab {gid} not found (404). Check {Settings.SheetId} and {Settings.SheetGids}.",
        _ => $"Google returned {(int)status} for sheet {sheetId} tab {gid}."
    };

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
        await telegram.SendAsync(chatId, text, TelegramApi.Keyboard(pid, keys));
    }

    async Task PromptRemovedAsync(long chatId, string gid, TableEntity row, GEvent gEv)
    {
        var title = gEv.Summary ?? row.GetString(Columns.Title) ?? "Event";
        var ev = new ExtractedEvent { Title = title, Date = JujuCalendar.StartDay(gEv) is DateOnly startDay ? DateText.Iso(startDay) : null };
        var pid = await PendingStore.AddAsync(ev, row.GetString(Columns.EventId) ?? "", new Dictionary<string, object>
        {
            [Columns.Kind] = PendingKind.SheetRemoved,
            [Columns.Gid] = gid,
            [Columns.Rk] = row.RowKey
        });
        await SyncStore.GoneAskedAsync(gid, row.RowKey);
        await telegram.SendAsync(chatId, $"❓ Removed from the sheet:\n• {title}, {EventText.When(gEv)}\nDelete it from the calendar?",
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

            var ev = await calendar.TryGetAsync(eventId);
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
        Date = DateText.Iso(it.Start),
        EndDate = it.End > it.Start ? DateText.Iso(it.End) : null,
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
            sb.AppendLine($"{ItemIdPrefix}{i} | {it.Subject} | {DateText.Range(it.Start, it.End)} | {TextUtil.Flat(it.Text, PromptCellTextChars)}");
        }

        var result = all.Select(it => (Title: Fallback(it), Type: EventTypes.Event)).ToList();
        var raw = await llm.AskAsync(TitlePrompt.Replace("{TODAY}", LlmUtil.Today()), new LlmPart[] { new TextPart(sb.ToString()) });
        try
        {
            using var doc = LlmUtil.ParseJson(raw);
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
            sb.AppendLine($"NEW {ItemIdPrefix}{i}: {events[i].Type} | {EventText.When(events[i])} | {events[i].Title}");
            foreach (var g in near) sb.AppendLine("  " + EventText.Line(g));
        }
        if (cands.Count == 0) return result;

        var raw = await llm.AskAsync(MatchPrompt, new LlmPart[] { new TextPart(sb.ToString()) });
        try
        {
            using var doc = LlmUtil.ParseJson(raw);
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
