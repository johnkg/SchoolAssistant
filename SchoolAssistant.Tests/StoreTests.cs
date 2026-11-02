using Azure.Data.Tables;
using Xunit;

namespace SchoolAssistant.Tests;

public class MessageStoreTests : AppTestBase
{
    [Fact]
    public async Task First_record_wins_and_repeats_are_refused()
    {
        Assert.True(await MessageStore.TryRecordAsync("10", "hello"));
        Assert.False(await MessageStore.TryRecordAsync("10", "hello again"));
        Assert.Equal("hello", Table(Tables.Messages).Find(Tables.MessagesPartition, "10")![Columns.Text]);
    }

    [Fact]
    public async Task SaveResult_adds_the_result_and_keeps_the_text()
    {
        await MessageStore.TryRecordAsync("10", "hello");
        await MessageStore.SaveResultAsync("10", "{}");
        var row = Table(Tables.Messages).Find(Tables.MessagesPartition, "10")!;
        Assert.Equal(("hello", "{}"), (row.GetString(Columns.Text), row.GetString(Columns.ResultJson)));
    }
}

public class PendingStoreTests : AppTestBase
{
    static ExtractedEvent Event() => new() { Title = "Quiz", Date = "2026-10-09" };

    [Fact]
    public async Task Added_proposal_has_a_short_id_and_pending_status()
    {
        var id = await PendingStore.AddAsync(Event(), "match1");
        Assert.Equal(12, id.Length);
        var row = Table(Tables.Pending).Find(Tables.PendingPartition, id)!;
        Assert.Equal(("match1", PendingStatus.Pending), (row.GetString(Columns.MatchId), row.GetString(Columns.Status)));
    }

    [Fact]
    public async Task Claim_returns_the_saved_proposal_once()
    {
        var id = await PendingStore.AddAsync(Event(), "m", new Dictionary<string, object>
        {
            [Columns.Kind] = PendingKind.SheetChanged, [Columns.Gid] = "g", [Columns.Rk] = "r", [Columns.Key] = "k", [Columns.Hash] = "h", [Columns.OldRk] = "o"
        });

        var (outcome, proposal) = await PendingStore.ClaimAsync(id);
        Assert.Equal(ClaimOutcome.Claimed, outcome);
        Assert.Equal(("Quiz", "m", "g", "r", "k", "h", "o"),
            (proposal!.Event.Title, proposal.MatchId, proposal.Gid, proposal.Rk, proposal.Key, proposal.Hash, proposal.OldRk));
        Assert.True(proposal.FromSheet);
        Assert.True(proposal.ChangedOnSheet);
        Assert.False(proposal.RemovedFromSheet);

        var (second, none) = await PendingStore.ClaimAsync(id);
        Assert.Equal(ClaimOutcome.AlreadyHandled, second);
        Assert.Null(none);
    }

    [Fact]
    public async Task Telegram_proposals_default_to_the_telegram_kind_with_empty_sheet_columns()
    {
        var (_, proposal) = await PendingStore.ClaimAsync(await PendingStore.AddAsync(Event(), ""));
        Assert.Equal(PendingKind.Telegram, proposal!.Kind);
        Assert.False(proposal.FromSheet);
        Assert.Equal("", proposal.Gid);
    }

    [Fact]
    public async Task Removed_kind_is_recognised()
    {
        var id = await PendingStore.AddAsync(Event(), "", new Dictionary<string, object> { [Columns.Kind] = PendingKind.SheetRemoved });
        Assert.True((await PendingStore.ClaimAsync(id)).Proposal!.RemovedFromSheet);
    }

    [Fact] public async Task Unknown_id_has_expired() => Assert.Equal(ClaimOutcome.Expired, (await PendingStore.ClaimAsync("nope")).Outcome);

    [Fact]
    public async Task A_competing_claim_that_loses_the_race_is_already_handled()
    {
        var id = await PendingStore.AddAsync(Event(), "");
        var table = Table(Tables.Pending);
        var racer = new RacingTable(table);
        Db.TableFactory = _ => racer; // the other tap changes the row between our read and our write

        Assert.Equal(ClaimOutcome.AlreadyHandled, (await PendingStore.ClaimAsync(id)).Outcome);
    }

    [Fact]
    public async Task Release_makes_a_claimed_proposal_claimable_again()
    {
        var id = await PendingStore.AddAsync(Event(), "");
        await PendingStore.ClaimAsync(id);
        await PendingStore.ReleaseAsync(id);
        Assert.Equal(ClaimOutcome.Claimed, (await PendingStore.ClaimAsync(id)).Outcome);
    }

    /// <summary>Rewrites the row right after it is read, as a concurrent button tap would.</summary>
    sealed class RacingTable(FakeTable inner) : TableClient
    {
        public override Task<Azure.Response<T>> GetEntityAsync<T>(string partitionKey, string rowKey, IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
        {
            var result = inner.GetEntityAsync<T>(partitionKey, rowKey, select, cancellationToken);
            inner.UpsertEntityAsync(new TableEntity(partitionKey, rowKey) { ["Touched"] = "1" }).GetAwaiter().GetResult();
            return result;
        }

        public override Task<Azure.Response> UpdateEntityAsync<T>(T entity, Azure.ETag ifMatch, TableUpdateMode mode = TableUpdateMode.Merge, CancellationToken cancellationToken = default) =>
            inner.UpdateEntityAsync(entity, ifMatch, mode, cancellationToken);
    }
}

public class SyncStoreTests : AppTestBase
{
    FakeTable Synced => Table(Tables.Synced);

    [Fact]
    public async Task Set_writes_the_full_row_and_marks_the_version_as_asked()
    {
        await SyncStore.SetAsync("g", "r", "key", "hash", "ev1", SyncStatus.Synced, "Title");
        var row = Synced.Find("g", "r")!;
        Assert.Equal(("key", "hash", "hash", "ev1", SyncStatus.Synced, "Title"),
            (row.GetString(Columns.Key), row.GetString(Columns.Hash), row.GetString(Columns.AskedHash), row.GetString(Columns.EventId), row.GetString(Columns.Status), row.GetString(Columns.Title)));
    }

    [Fact]
    public async Task Mark_changes_the_status_and_unlinks_the_event_but_keeps_the_rest()
    {
        await SyncStore.SetAsync("g", "r", "key", "hash", "ev1", SyncStatus.Synced, "Title");
        await SyncStore.MarkAsync("g", "r", SyncStatus.Removed);
        var row = Synced.Find("g", "r")!;
        Assert.Equal((SyncStatus.Removed, "", "Title"), (row.GetString(Columns.Status), row.GetString(Columns.EventId), row.GetString(Columns.Title)));
    }

    [Fact]
    public async Task GoneAsked_is_remembered()
    {
        await SyncStore.SetAsync("g", "r", "key", "hash", "ev1", SyncStatus.Synced, "T");
        Assert.False(SyncStore.IsGoneAsked(Synced.Find("g", "r")!));
        await SyncStore.GoneAskedAsync("g", "r");
        Assert.True(SyncStore.IsGoneAsked(Synced.Find("g", "r")!));
    }

    [Fact]
    public async Task Asked_records_the_version_without_changing_the_applied_hash()
    {
        await SyncStore.SetAsync("g", "r", "key", "old", "ev1", SyncStatus.Synced, "T");
        await SyncStore.AskedAsync("g", "r", "key2", "new");
        var row = Synced.Find("g", "r")!;
        Assert.Equal(("old", "new", "key2"), (row.GetString(Columns.Hash), row.GetString(Columns.AskedHash), row.GetString(Columns.Key)));
    }

    [Fact]
    public async Task LoadTab_returns_only_that_tabs_rows_by_row_key()
    {
        await SyncStore.SetAsync("g1", "a", "k", "h", "e", SyncStatus.Synced, "T");
        await SyncStore.SetAsync("g1", "b", "k", "h", "e", SyncStatus.Synced, "T");
        await SyncStore.SetAsync("g2", "c", "k", "h", "e", SyncStatus.Synced, "T");
        Assert.Equal(new[] { "a", "b" }, (await SyncStore.LoadTabAsync("g1")).Keys.OrderBy(k => k));
    }
}

public class SheetStateStoreTests : AppTestBase
{
    [Fact] public async Task Unknown_tab_has_no_hash() => Assert.Null(await SheetStateStore.GetCsvHashAsync("1"));

    [Fact]
    public async Task Saved_hash_is_read_back_and_overwritten()
    {
        await SheetStateStore.SaveCsvHashAsync("1", "aaa");
        Assert.Equal("aaa", await SheetStateStore.GetCsvHashAsync("1"));
        await SheetStateStore.SaveCsvHashAsync("1", "bbb");
        Assert.Equal("bbb", await SheetStateStore.GetCsvHashAsync("1"));
    }
}
