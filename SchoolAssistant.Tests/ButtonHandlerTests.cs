using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static SchoolAssistant.Tests.Harness;

namespace SchoolAssistant.Tests;

public class ButtonHandlerTests : Harness
{
    readonly ButtonHandler _handler;

    public ButtonHandlerTests() => _handler = new ButtonHandler(Telegram, Calendar, NullLogger<ButtonHandler>.Instance);

    static ExtractedEvent Quiz(string title = "Science Quiz") =>
        new() { Type = EventTypes.Quiz, Title = title, Date = FutureDate(5), Description = "new text" };

    /// <summary>Telegram's callback_query object for a button tap.</summary>
    static System.Text.Json.JsonElement Callback(string? data, long chatId = ChatId)
    {
        var dataPart = data == null ? "" : ",\"data\":\"" + data + "\"";
        return System.Text.Json.JsonDocument.Parse("{\"id\":\"cb1\"" + dataPart + ",\"message\":{\"message_id\":9,\"chat\":{\"id\":" + chatId + "}}}").RootElement;
    }

    async Task<string> Propose(ExtractedEvent? extracted = null, string matchId = "", string? kind = null, string gid = "g", string oldRk = "")
    {
        var extra = new Dictionary<string, object>();
        if (kind != null)
        {
            extra[Columns.Kind] = kind;
            extra[Columns.Gid] = gid;
            extra[Columns.Rk] = "rk";
            extra[Columns.Key] = "key";
            extra[Columns.Hash] = "hash";
            extra[Columns.OldRk] = oldRk;
        }
        return await PendingStore.AddAsync(extracted ?? Quiz(), matchId, extra);
    }

    Task TapAsync(string action, string id) => _handler.HandleAsync(Callback($"{action}:{id}"));

    // ---------- guards ----------
    [Fact]
    public async Task A_tap_from_another_chat_is_acknowledged_and_ignored()
    {
        var id = await Propose();
        await _handler.HandleAsync(Callback($"n:{id}", chatId: 7));
        Assert.Equal(new[] { "" }, CallbackAnswers);
        Assert.Empty(Server.Events);
        Assert.Equal(ClaimOutcome.Claimed, (await PendingStore.ClaimAsync(id)).Outcome);
    }

    [Fact]
    public async Task A_tap_without_usable_data_is_acknowledged_and_ignored()
    {
        await _handler.HandleAsync(Callback("garbage"));
        await _handler.HandleAsync(Callback(null));
        Assert.Equal(new[] { "", "" }, CallbackAnswers);
    }

    [Fact]
    public async Task An_unknown_proposal_says_expired()
    {
        await TapAsync("n", "missing");
        Assert.Equal(new[] { "Expired" }, CallbackAnswers);
    }

    [Fact]
    public async Task A_second_tap_says_already_handled_and_does_nothing()
    {
        var id = await Propose();
        await TapAsync("n", id);
        await TapAsync("n", id);
        Assert.Equal(new[] { "", "Already handled" }, CallbackAnswers);
        Assert.Single(Server.Events);
    }

    // ---------- Telegram proposals ----------
    [Fact]
    public async Task Add_creates_the_event_and_replaces_the_question_with_the_result()
    {
        await TapAsync("n", await Propose());
        Assert.Equal("Science Quiz", Server.Events.Values.Single().Summary);
        Assert.StartsWith("✅ Added: Science Quiz,", Assert.Single(Edited));
    }

    [Fact]
    public async Task An_unrecognised_action_falls_back_to_adding()
    {
        await TapAsync("zzz", await Propose());
        Assert.Single(Server.Events);
    }

    [Fact]
    public async Task Skip_leaves_the_calendar_alone()
    {
        await TapAsync("s", await Propose());
        Assert.Empty(Server.Events);
        Assert.Equal("⏭ Skipped: Science Quiz", Edited.Single());
        Assert.Empty(Table(Tables.Synced).Rows);
    }

    [Fact]
    public async Task Update_changes_the_matched_event_and_keeps_its_old_details()
    {
        var existing = Server.Add(new Google.Apis.Calendar.v3.Data.Event { Summary = "Old", Description = "old text", Start = new() { Date = FutureDate(1) }, End = new() { Date = FutureDate(2) } });
        await TapAsync("u", await Propose(matchId: existing.Id));

        Assert.Equal("Science Quiz", Server.Events[existing.Id].Summary);
        Assert.Equal("new text\n\n— Previous details —\nold text", Server.Events[existing.Id].Description);
        Assert.StartsWith("✏️ Updated: Science Quiz,", Edited.Single());
    }

    [Fact]
    public async Task Update_recreates_the_event_when_it_was_deleted_in_the_meantime()
    {
        await TapAsync("u", await Propose(matchId: "deleted-event"));
        Assert.Equal("Science Quiz", Assert.Single(Server.Events.Values).Summary);
        Assert.StartsWith("✏️ Updated", Edited.Single());
    }

    [Fact]
    public async Task Update_recreates_the_event_when_it_is_cancelled()
    {
        var cancelled = Server.Add(new Google.Apis.Calendar.v3.Data.Event { Summary = "Old", Status = "cancelled", Start = new() { Date = FutureDate(1) }, End = new() { Date = FutureDate(2) } });
        await TapAsync("u", await Propose(matchId: cancelled.Id));
        Assert.Equal(2, Server.Events.Count);
    }

    [Fact]
    public async Task Update_without_a_match_adds_a_new_event_instead()
    {
        await TapAsync("u", await Propose(matchId: ""));
        Assert.Single(Server.Events);
    }

    // ---------- late answers ----------
    [Fact]
    public async Task The_action_still_runs_when_telegram_refuses_the_late_button_answer()
    {
        TelegramOverride = r => r.Uri.AbsolutePath.EndsWith("answerCallbackQuery")
            ? StubHandler.Json("""{"ok":false,"description":"Bad Request: query is too old"}""", System.Net.HttpStatusCode.BadRequest)
            : null;
        var id = await Propose();

        await TapAsync("n", id);

        Assert.Single(Server.Events);
        Assert.StartsWith("✅ Added: Science Quiz", Assert.Single(Edited));
        Assert.Empty(Sent); // no error message to the chat
    }

    // ---------- failures ----------
    [Fact]
    public async Task A_failed_action_gives_the_buttons_back_and_reports_the_error()
    {
        var id = await Propose();
        Server.FailWith = System.Net.HttpStatusCode.Forbidden;
        await Assert.ThrowsAnyAsync<Exception>(() => TapAsync("n", id));

        Server.FailWith = null;
        Assert.Empty(Edited);
        await TapAsync("n", id); // works again
        Assert.Single(Server.Events);
    }

    [Fact]
    public async Task If_the_buttons_cannot_be_given_back_the_original_error_still_surfaces()
    {
        var id = await Propose();
        Server.OnRequest = _ => { Table(Tables.Pending).FailUpdates = true; Server.FailWith = System.Net.HttpStatusCode.Forbidden; };
        var ex = await Assert.ThrowsAsync<Google.GoogleApiException>(() => TapAsync("n", id));
        Assert.NotNull(ex);
    }

    // ---------- sheet proposals ----------
    [Fact]
    public async Task Adding_a_new_sheet_item_links_the_event_in_the_synced_table()
    {
        await TapAsync("n", await Propose(kind: PendingKind.SheetNew));
        var row = Table(Tables.Synced).Find("g", "rk")!;
        Assert.Equal((SyncStatus.Synced, "key", "hash", "Science Quiz"), (row.GetString(Columns.Status), row.GetString(Columns.Key), row.GetString(Columns.Hash), row.GetString(Columns.Title)));
        Assert.Equal(Server.Events.Keys.Single(), row.GetString(Columns.EventId));
    }

    [Fact]
    public async Task Skipping_a_new_sheet_item_remembers_it_without_a_link()
    {
        await TapAsync("s", await Propose(kind: PendingKind.SheetNew));
        var row = Table(Tables.Synced).Find("g", "rk")!;
        Assert.Equal((SyncStatus.Skipped, ""), (row.GetString(Columns.Status), row.GetString(Columns.EventId)));
    }

    [Fact]
    public async Task Skipping_a_sheet_change_keeps_the_link_to_the_existing_event()
    {
        await TapAsync("s", await Propose(matchId: "ev9", kind: PendingKind.SheetChanged));
        var row = Table(Tables.Synced).Find("g", "rk")!;
        Assert.Equal((SyncStatus.Synced, "ev9"), (row.GetString(Columns.Status), row.GetString(Columns.EventId)));
    }

    [Fact]
    public async Task Updating_for_a_sheet_change_replaces_the_description_and_records_the_link()
    {
        var existing = Server.Add(new Google.Apis.Calendar.v3.Data.Event { Summary = "Old", Description = "old text", Start = new() { Date = FutureDate(1) }, End = new() { Date = FutureDate(2) } });
        await TapAsync("u", await Propose(matchId: existing.Id, kind: PendingKind.SheetChanged));

        Assert.Equal("new text", Server.Events[existing.Id].Description); // no "previous details" for sheet changes
        Assert.Equal(existing.Id, Table(Tables.Synced).Find("g", "rk")![Columns.EventId]);
    }

    [Fact]
    public async Task Deleting_a_removed_sheet_item_deletes_the_event_and_marks_the_row()
    {
        var existing = Server.Add(new Google.Apis.Calendar.v3.Data.Event { Summary = "Old", Start = new() { Date = FutureDate(1) }, End = new() { Date = FutureDate(2) } });
        await SyncStore.SetAsync("g", "rk", "key", "hash", existing.Id, SyncStatus.Synced, "Old");
        await TapAsync("d", await Propose(matchId: existing.Id, kind: PendingKind.SheetRemoved));

        Assert.Empty(Server.Events);
        Assert.Equal(SyncStatus.Removed, Table(Tables.Synced).Find("g", "rk")![Columns.Status]);
        Assert.StartsWith("🗑 Deleted", Edited.Single());
    }

    [Fact]
    public async Task Keeping_a_removed_sheet_item_leaves_the_event_and_marks_the_row()
    {
        var existing = Server.Add(new Google.Apis.Calendar.v3.Data.Event { Summary = "Old", Start = new() { Date = FutureDate(1) }, End = new() { Date = FutureDate(2) } });
        await SyncStore.SetAsync("g", "rk", "key", "hash", existing.Id, SyncStatus.Synced, "Old");
        await TapAsync("s", await Propose(matchId: existing.Id, kind: PendingKind.SheetRemoved));

        Assert.Single(Server.Events);
        Assert.Equal(SyncStatus.Kept, Table(Tables.Synced).Find("g", "rk")![Columns.Status]);
        Assert.StartsWith("📌 Kept", Edited.Single());
    }

    [Fact]
    public async Task A_moved_item_closes_its_old_row_whatever_the_answer()
    {
        await SyncStore.SetAsync("g", "old-rk", "oldkey", "h", "ev1", SyncStatus.Synced, "Old");
        await TapAsync("s", await Propose(matchId: "ev1", kind: PendingKind.SheetChanged, oldRk: "old-rk"));
        Assert.Equal(SyncStatus.Moved, Table(Tables.Synced).Find("g", "old-rk")![Columns.Status]);
    }

    [Fact]
    public async Task Telegram_proposals_never_touch_the_synced_table()
    {
        await TapAsync("n", await Propose());
        Assert.Empty(Table(Tables.Synced).Rows);
    }
}
