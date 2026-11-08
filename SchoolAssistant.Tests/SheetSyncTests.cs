using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using GEvent = Google.Apis.Calendar.v3.Data.Event;
using static SchoolAssistant.Tests.Harness;

namespace SchoolAssistant.Tests;

public class SheetSyncTests : Harness
{
    const string Gid = "55";
    readonly SheetSync _sync;
    readonly StubHandler _sheetHandler;
    readonly FakeSheetTabLister _tabs = new();
    string _csv = "";
    string _matchAnswer = """{"matches":[]}""";
    string? _nameAnswerOverride;

    public SheetSyncTests()
    {
        SetEnv(Settings.SheetId, "sheet-1");
        SetEnv(Settings.SheetGids, Gid);
        _sheetHandler = new StubHandler(_ => new HttpResponseMessage { Content = new StringContent(_csv) });
        _sync = new SheetSync(Llm, Telegram, Calendar, _tabs, new StubHttpFactory(_sheetHandler), NullLogger<SheetSync>.Instance);

        LlmAnswer = (system, text) =>
        {
            if (system.Contains("match NEW school tracker items")) return _matchAnswer;
            if (_nameAnswerOverride != null) return _nameAnswerOverride;
            var items = Regex.Matches(text, @"^i(?<n>\d+) \| (?<subject>[^|]+?) \|", RegexOptions.Multiline)
                .Select(m => $$"""{"id":"i{{m.Groups["n"].Value}}","title":"{{m.Groups["subject"].Value}} Task {{m.Groups["n"].Value}}","type":"written_work"}""");
            return "{\"items\":[" + string.Join(",", items) + "]}";
        };
    }

    /// <summary>A tracker tab: header of dates (today + offset), one row per subject.</summary>
    static string Csv(params (string Subject, int Offset, string Text)[] cells)
    {
        var offsets = Enumerable.Range(3, 14).ToList();
        string Day(int offset) => ManilaTime.Today.AddDays(offset).ToDateTime(TimeOnly.MinValue).ToString("MMM d", CultureInfo.InvariantCulture);
        var lines = new List<string> { "," + string.Join(",", offsets.Select(Day)) };
        foreach (var subject in cells.Select(c => c.Subject).Distinct())
        {
            var row = offsets.Select(o => cells.FirstOrDefault(c => c.Subject == subject && c.Offset == o).Text ?? "");
            lines.Add(subject + "," + string.Join(",", row.Select(t => t.Contains(',') ? $"\"{t}\"" : t)));
        }
        return string.Join("\n", lines) + "\n";
    }

    Task Run(bool manual = true) => _sync.RunAsync(ChatId, manual);

    IEnumerable<(string Title, string Description)> Created => Server.Events.Values.Select(e => (e.Summary, e.Description));

    static string FutureTitle(int n) => $"Math Task {n}";

    // ---------- settings and locking ----------
    [Fact]
    public async Task Without_a_sheet_id_a_manual_sync_explains_and_a_timed_one_stays_quiet()
    {
        SetEnv(Settings.SheetId, null);
        await Run(manual: false);
        Assert.Empty(Sent);
        await Run(manual: true);
        Assert.Equal("⚠️ Set SHEET_ID first.", Assert.Single(Sent));
    }

    [Fact]
    public async Task Only_numeric_tab_ids_are_used()
    {
        SetEnv(Settings.SheetGids, "abc, 12 ,,3x,77");
        _csv = Csv();
        await Run();
        Assert.Equal(new[] { "gid=12", "gid=77" }, _sheetHandler.Requests.Select(r => r.Uri.Query.Split('&').Last()));
        Assert.StartsWith("https://docs.google.com/spreadsheets/d/sheet-1/export?format=csv", _sheetHandler.Requests[0].Uri.ToString());
    }

    [Fact]
    public async Task A_second_manual_sync_while_one_runs_is_turned_away()
    {
        var gate = (SemaphoreSlim)typeof(SheetSync).GetField("Gate", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        await gate.WaitAsync();
        try
        {
            await Run(manual: true);
            await Run(manual: false);
        }
        finally { gate.Release(); }
        Assert.Equal("⏳ A sync is already running.", Assert.Single(Sent));
        Assert.Empty(_sheetHandler.Requests);
    }

    // ---------- first sync ----------
    [Fact]
    public async Task New_items_are_named_added_and_recorded()
    {
        _csv = Csv(("Math", 5, "WW 1"), ("Science", 6, "Quiz"));
        await Run();

        Assert.Equal(2, Server.Events.Count);
        var math = Server.Events.Values.Single(e => e.Summary.StartsWith("Math"));
        Assert.Equal("Math Task 0", math.Summary);
        Assert.StartsWith($"WW 1\n\n{SheetSync.SourceFooter} · Math · D2", math.Description);
        Assert.Equal(DateText.Iso(ManilaTime.Today.AddDays(5)), math.Start.Date);

        Assert.Equal(2, Table(Tables.Synced).Rows.Count);
        Assert.All(Table(Tables.Synced).Rows, r => Assert.Equal(SyncStatus.Synced, r.GetString(Columns.Status)));
        Assert.NotNull(await SheetStateStore.GetCsvHashAsync(Gid));

        Assert.Equal("📊 Sheet: 2 added", Sent[0].Split('\n')[0]);
        Assert.Contains("✅ Math Task 0,", Sent[0]);
    }

    [Fact]
    public async Task A_run_of_identical_cells_becomes_one_multi_day_event()
    {
        _csv = Csv(("Math", 5, "Exam week"), ("Math", 6, "Exam week"), ("Math", 7, "Exam week"));
        await Run();
        var created = Assert.Single(Server.Events.Values);
        Assert.Equal(DateText.Iso(ManilaTime.Today.AddDays(8)), created.End.Date); // exclusive end
    }

    [Fact]
    public async Task Only_the_first_eight_added_items_are_listed()
    {
        _csv = Csv(Enumerable.Range(3, 10).Select(i => ($"Subj{i}", i, "task")).ToArray());
        await Run();
        var lines = Sent[0].Split('\n');
        Assert.Equal("📊 Sheet: 10 added", lines[0]);
        Assert.Equal(1 + 8 + 1, lines.Length);
        Assert.Equal("…and 2 more", lines[^1]);
    }

    [Fact]
    public async Task An_unchanged_sheet_is_skipped_by_the_timer_but_rechecked_by_a_manual_sync()
    {
        _csv = Csv(("Math", 5, "WW 1"));
        await Run(manual: false);
        var llmCalls = Llm.Calls.Count;
        ClearSent();

        await Run(manual: false);
        Assert.Equal(llmCalls, Llm.Calls.Count);
        Assert.Empty(Sent);

        await Run(manual: true);
        Assert.Equal("📊 Sheet is up to date.", Assert.Single(Sent));
        Assert.Single(Server.Events);
    }

    [Fact]
    public async Task A_sheet_with_only_past_items_is_silent_and_remembered()
    {
        var past = ManilaTime.Today.AddDays(-40);
        _csv = $"S,{Fmt(past)},{Fmt(past.AddDays(1))},{Fmt(past.AddDays(2))}\nMath,a,b,c\n";
        await Run();
        Assert.Equal("📊 Sheet is up to date.", Assert.Single(Sent)); // old months are not worth a warning
        Assert.NotNull(await SheetStateStore.GetCsvHashAsync(Gid));

        ClearSent();
        await Run(manual: false);
        Assert.Empty(Sent);
    }

    static string Fmt(DateOnly date) => date.ToDateTime(TimeOnly.MinValue).ToString("MMM d", CultureInfo.InvariantCulture);

    // ---------- download problems ----------
    [Fact]
    public async Task A_sign_in_page_is_reported_to_a_manual_sync_only()
    {
        _csv = "<!DOCTYPE html><html>Sign in</html>";
        await Run(manual: false);
        Assert.Empty(Sent);
        await Run(manual: true);
        Assert.Contains("❌ Sheet tab 55: The sheet asked for a sign-in", Assert.Single(Sent));
    }

    [Fact]
    public async Task A_byte_order_mark_is_ignored()
    {
        _csv = "﻿" + Csv(("Math", 5, "WW 1"));
        await Run();
        Assert.Single(Server.Events);
    }

    // ---------- naming ----------
    [Fact]
    public async Task Garbled_names_fall_back_to_the_cell_text()
    {
        _nameAnswerOverride = "no json";
        _csv = Csv(("MATH", 5, "WW 1\nsecond line"));
        await Run();
        Assert.Equal("Math: WW 1", Server.Events.Values.Single().Summary);
    }

    [Fact]
    public async Task The_fallback_title_is_cut_to_sixty_characters()
    {
        _nameAnswerOverride = "{}";
        _csv = Csv(("MATH", 5, new string('x', 100)));
        await Run();
        Assert.Equal(60, Server.Events.Values.Single().Summary.Length);
    }

    [Fact]
    public async Task Model_names_are_used_when_valid_and_bad_entries_are_ignored()
    {
        _nameAnswerOverride = """
            {"items":[{"id":"i0","title":" Math Quiz 1 ","type":"QUIZ"},{"id":"i1","title":"","type":"nonsense"},{"id":"i9","title":"ghost"},{"id":"x1","title":"bad id"},{"id":"i-1","title":"negative"},{"title":"no id"},{"id":"i2"}]}
            """;
        _csv = Csv(("Math", 5, "a"), ("Science", 6, "b"), ("Art", 7, "c"));
        await Run();

        Assert.Equal(new[] { "Math Quiz 1", "Science: b", "Art: c" }, Server.Events.Values.Select(e => e.Summary).OrderByDescending(t => t == "Math Quiz 1"));
    }

    // ---------- look-alikes on the calendar ----------
    GEvent ExistingOnCalendar(int offset, string title = "Math Quiz") =>
        Server.Add(new GEvent { Summary = title, Start = new() { Date = FutureDate(offset) }, End = new() { Date = FutureDate(offset + 1) } });

    [Fact]
    public async Task A_new_item_that_matches_a_calendar_event_asks_before_adding()
    {
        var existing = ExistingOnCalendar(6);
        _matchAnswer = $$"""{"matches":[{"id":"i0","existing":"{{existing.Id}}"}]}""";
        _csv = Csv(("Math", 5, "Quiz"));
        await Run();

        Assert.Single(Server.Events);
        Assert.StartsWith("❓ Similar event on calendar:\n• Math Quiz,", Sent[0]);
        Assert.Contains("Sheet: Math Task 0,", Sent[0]);
        Assert.Equal(new[] { "u:", "n:", "s:" }, ButtonsOfSent(0).Select(b => b[..2]));

        var row = Table(Tables.Synced).Rows.Single();
        Assert.Equal("", row.GetString(Columns.EventId) ?? "");
        Assert.NotNull(row.GetString(Columns.AskedHash));
        var (_, proposal) = await PendingStore.ClaimAsync(ProposalIdOfSent(0));
        Assert.Equal((PendingKind.SheetNew, existing.Id, Gid), (proposal!.Kind, proposal.MatchId, proposal.Gid));
    }

    [Theory]
    [InlineData("""{"matches":[{"id":"i0","existing":"made-up"}]}""")]
    [InlineData("""{"matches":[{"id":"i7","existing":"ev1"}]}""")]
    [InlineData("""{"matches":[{"id":"i0","existing":null}]}""")]
    [InlineData("""{"matches":[{"existing":"ev1"}]}""")]
    [InlineData("""{"matches":{}}""")]
    [InlineData("""{"other":1}""")]
    [InlineData("garbage")]
    public async Task Unusable_match_answers_mean_the_item_is_added(string matchAnswer)
    {
        ExistingOnCalendar(6);
        _matchAnswer = matchAnswer;
        _csv = Csv(("Math", 5, "Quiz"));
        await Run();
        Assert.Equal(2, Server.Events.Count);
    }

    [Fact]
    public async Task Calendar_events_far_from_the_item_are_not_offered_to_the_model()
    {
        ExistingOnCalendar(30);
        _csv = Csv(("Math", 5, "Quiz"));
        await Run();
        Assert.DoesNotContain(Llm.Calls, c => c.System.Contains("match NEW school tracker items"));
    }

    [Fact]
    public async Task Events_already_linked_to_a_sheet_item_are_not_offered_again()
    {
        _csv = Csv(("Math", 5, "Quiz"));
        await Run();
        var llmCalls = Llm.Calls.Count;

        _csv = Csv(("Math", 5, "Quiz"), ("Science", 6, "Other"));
        await Run();
        // the second sheet item sees only unlinked events; the first run's event is linked, so no match call is needed
        Assert.DoesNotContain(Llm.Calls.Skip(llmCalls), c => c.System.Contains("match NEW school tracker items"));
    }

    // ---------- changes, moves, removals ----------
    [Fact]
    public async Task Changed_cell_text_asks_whether_to_update_and_only_asks_once()
    {
        _csv = Csv(("Math", 5, "WW 1"));
        await Run();
        ClearSent();

        _csv = Csv(("Math", 5, "WW 1 - chapters 3 and 4"));
        await Run(manual: false);
        Assert.StartsWith("❓ Sheet changed:\n• Was: Math Task 0\nNow: Math Task 0,", Sent[0]);
        Assert.EndsWith("WW 1 - chapters 3 and 4", Sent[0]);
        Assert.Equal(new[] { "u:", "s:" }, ButtonsOfSent(0).Select(b => b[..2]));

        await Run(manual: false);
        Assert.Single(Sent);
    }

    [Fact]
    public async Task A_date_that_moved_is_one_question_not_an_add_plus_a_delete()
    {
        _csv = Csv(("Math", 5, "WW 1"));
        await Run();
        ClearSent();

        _csv = Csv(("Math", 6, "WW 1"));
        await Run(manual: false);

        var question = Assert.Single(Sent);
        Assert.StartsWith("❓ Sheet date moved:\n• Was: Math Task 0, ", question);
        var moveProposal = (await PendingStore.ClaimAsync(ProposalIdOfSent(0))).Proposal!;
        Assert.Equal(PendingKind.SheetChanged, moveProposal.Kind);
        Assert.NotEqual("", moveProposal.OldRk);
        Assert.Single(Server.Events); // nothing added

        var oldRow = Table(Tables.Synced).Rows.Single(r => r.RowKey == moveProposal.OldRk);
        Assert.True(SyncStore.IsGoneAsked(oldRow));
    }

    [Fact]
    public async Task A_vanished_item_asks_whether_to_delete_the_event()
    {
        _csv = Csv(("Math", 5, "WW 1"), ("Science", 6, "Quiz"));
        await Run();
        ClearSent();

        _csv = Csv(("Math", 5, "WW 1"));
        await Run(manual: false);

        Assert.StartsWith("❓ Removed from the sheet:\n• Science Task 1,", Sent[0]);
        Assert.EndsWith("Delete it from the calendar?", Sent[0]);
        Assert.Equal(new[] { "d:", "s:" }, ButtonsOfSent(0).Select(b => b[..2]));

        await Run(manual: false);
        Assert.Single(Sent); // not asked again
    }

    [Fact]
    public async Task A_vanished_item_whose_event_was_already_deleted_is_closed_quietly()
    {
        _csv = Csv(("Math", 5, "WW 1"), ("Science", 6, "Quiz"));
        await Run();
        ClearSent();
        Server.Events.Remove(Server.Events.Values.Single(e => e.Summary.StartsWith("Science")).Id);

        _csv = Csv(("Math", 5, "WW 1"));
        await Run();

        Assert.Equal("📊 Sheet is up to date.", Assert.Single(Sent));
        Assert.Contains(Table(Tables.Synced).Rows, r => r.GetString(Columns.Status) == SyncStatus.Removed);
    }

    [Fact]
    public async Task Rows_that_are_past_skipped_or_unlinked_are_never_offered_for_deletion()
    {
        await SyncStore.SetAsync(Gid, "past", $"{Gid}|OLD|{DateText.Compact(ManilaTime.Today.AddDays(-5))}", "h", "ev-past", SyncStatus.Synced, "Old");
        await SyncStore.SetAsync(Gid, "skipped", $"{Gid}|SKIP|{DateText.Compact(ManilaTime.Today.AddDays(5))}", "h", "ev1", SyncStatus.Skipped, "Skipped");
        await SyncStore.SetAsync(Gid, "nolink", $"{Gid}|NOLINK|{DateText.Compact(ManilaTime.Today.AddDays(5))}", "h", "", SyncStatus.Synced, "NoLink");
        await SyncStore.SetAsync(Gid, "badkey", "garbage", "h", "ev2", SyncStatus.Synced, "BadKey");
        await SyncStore.SetAsync(Gid, "asked", $"{Gid}|ASKED|{DateText.Compact(ManilaTime.Today.AddDays(5))}", "h", "ev3", SyncStatus.Synced, "Asked");
        await SyncStore.GoneAskedAsync(Gid, "asked");

        _csv = Csv(("Math", 5, "WW 1"));
        await Run();
        Assert.DoesNotContain(Sent, s => s.Contains("Removed from the sheet"));
    }

    [Fact]
    public async Task A_previously_skipped_item_is_offered_again_when_its_text_changes()
    {
        _csv = Csv(("Math", 5, "WW 1"));
        await Run();
        var key = Table(Tables.Synced).Rows.Single();
        await SyncStore.SetAsync(Gid, key.RowKey, key.GetString(Columns.Key), key.GetString(Columns.Hash), "", SyncStatus.Skipped, "Math Task 0");
        ClearSent();

        _csv = Csv(("Math", 5, "WW 1 revised"));
        await Run();
        Assert.Equal(2, Server.Events.Count);
    }

    // ---------- failures ----------
    [Fact]
    public async Task A_failed_insert_is_reported_to_manual_syncs_and_the_sheet_is_retried_next_time()
    {
        _csv = Csv(("Math", 5, "WW 1"));
        Server.OnRequest = r => Server.FailWith = r.Method == HttpMethod.Post ? System.Net.HttpStatusCode.Forbidden : null;
        await Run(manual: false);
        Assert.Empty(Sent);
        Assert.Null(await SheetStateStore.GetCsvHashAsync(Gid));

        await Run(manual: true);
        Assert.Contains("❌ Math Task 0:", Assert.Single(Sent));
        Assert.Null(await SheetStateStore.GetCsvHashAsync(Gid));

        Server.OnRequest = null;
        Server.FailWith = null;
        await Run(manual: false);
        Assert.Single(Server.Events);
    }

    [Fact]
    public async Task A_tab_that_throws_does_not_stop_other_tabs()
    {
        SetEnv(Settings.SheetGids, "1,2");
        var calls = 0;
        var handler = new StubHandler(_ => ++calls == 1
            ? new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            : new HttpResponseMessage { Content = new StringContent(Csv(("Math", 5, "WW 1"))) });
        var sync = new SheetSync(Llm, Telegram, Calendar, _tabs, new StubHttpFactory(handler), NullLogger<SheetSync>.Instance);

        await sync.RunAsync(ChatId, manual: true);
        Assert.Single(Server.Events);
        Assert.Contains("❌ Sheet tab 1:", Sent[0]);
    }

    [Fact]
    public async Task Questions_that_cannot_be_sent_are_reported_and_retried()
    {
        var existing = ExistingOnCalendar(6);
        _matchAnswer = $$"""{"matches":[{"id":"i0","existing":"{{existing.Id}}"}]}""";
        _csv = Csv(("Math", 5, "Quiz"));
        TelegramFails = true;
        await Run(manual: false);
        Assert.Null(await SheetStateStore.GetCsvHashAsync(Gid));
    }

    [Fact]
    public async Task Change_and_removal_questions_that_cannot_be_sent_leave_the_sheet_unmarked()
    {
        _csv = Csv(("Math", 5, "WW 1"), ("Science", 6, "Quiz"));
        await Run();

        _csv = Csv(("Math", 5, "WW 1 changed"));
        var hashBefore = await SheetStateStore.GetCsvHashAsync(Gid);
        TelegramFails = true;
        await Run(manual: false);
        Assert.Equal(hashBefore, await SheetStateStore.GetCsvHashAsync(Gid)); // not advanced, so the next run retries
        Assert.NotEqual(SheetParser.Sha(_csv, SheetItem.ContentHashHexChars), await SheetStateStore.GetCsvHashAsync(Gid));
    }

    // ---------- which tabs ----------
    [Fact]
    public async Task Without_SHEET_GIDS_every_listed_tab_is_synced()
    {
        SetEnv(Settings.SheetGids, null);
        _tabs.Gids = new List<string> { "11", "22" };
        _csv = Csv(("Math", 5, "WW 1"));
        await Run();

        Assert.Equal(new[] { "sheet-1" }, _tabs.AskedFor);
        Assert.Equal(new[] { "gid=11", "gid=22" }, _sheetHandler.Requests.Select(r => r.Uri.Query.Split('&').Last()));
    }

    [Fact]
    public async Task SHEET_GIDS_overrides_tab_discovery()
    {
        _tabs.Gids = new List<string> { "99" };
        _csv = Csv();
        await Run();
        Assert.Empty(_tabs.AskedFor);
        Assert.Equal($"gid={Gid}", _sheetHandler.Requests.Single().Uri.Query.Split('&').Last());
    }

    [Fact]
    public async Task A_SHEET_GIDS_with_no_usable_ids_falls_back_to_discovery()
    {
        SetEnv(Settings.SheetGids, "abc");
        _tabs.Gids = new List<string> { "7" };
        _csv = Csv();
        await Run();
        Assert.Equal("gid=7", _sheetHandler.Requests.Single().Uri.Query.Split('&').Last());
    }

    [Fact]
    public async Task A_sheet_with_no_tabs_is_reported_to_a_manual_sync_only()
    {
        SetEnv(Settings.SheetGids, null);
        await Run(manual: false);
        Assert.Empty(Sent);
        await Run(manual: true);
        Assert.Equal("⚠️ The sheet has no visible tabs.", Assert.Single(Sent));
    }

    [Fact]
    public async Task If_the_tabs_cannot_be_listed_a_manual_sync_says_why_and_a_timed_one_stays_quiet()
    {
        SetEnv(Settings.SheetGids, null);
        _tabs.Failure = new InvalidOperationException("Can't list the tabs (403). Share the sheet.");
        await Run(manual: false);
        Assert.Empty(Sent);
        await Run(manual: true);
        Assert.Equal("❌ Can't list the tabs (403). Share the sheet.", Assert.Single(Sent));
        Assert.Empty(_sheetHandler.Requests);
    }

    // ---------- download errors name the sheet, the tab and the likely cause ----------
    [Theory]
    [InlineData(400, "Google rejected sheet sheet-1 tab 55 (400). Check SHEET_ID and SHEET_GIDS.")]
    [InlineData(401, "Google denied access to sheet sheet-1 tab 55 (401). Share the sheet so anyone with the link can view it.")]
    [InlineData(403, "Google denied access to sheet sheet-1 tab 55 (403). Share the sheet so anyone with the link can view it.")]
    [InlineData(404, "Sheet sheet-1 tab 55 not found (404). Check SHEET_ID and SHEET_GIDS.")]
    [InlineData(500, "Google returned 500 for sheet sheet-1 tab 55.")]
    public async Task A_failed_download_explains_itself(int status, string explanation)
    {
        var failing = new StubHandler(_ => new HttpResponseMessage((System.Net.HttpStatusCode)status));
        var sync = new SheetSync(Llm, Telegram, Calendar, _tabs, new StubHttpFactory(failing), NullLogger<SheetSync>.Instance);

        await sync.RunAsync(ChatId, manual: true);
        Assert.Equal($"❌ Sheet tab 55: {explanation}", Assert.Single(Sent));
    }

    // ---------- timer ----------
    [Fact]
    public async Task The_hourly_timer_runs_a_quiet_sync_for_the_configured_chat()
    {
        _csv = Csv(("Math", 5, "WW 1"));
        await new SheetPollTimer(_sync).Run(new TimerInfo());
        Assert.Single(Server.Events);
    }
}
