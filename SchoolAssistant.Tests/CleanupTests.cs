using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static SchoolAssistant.Tests.Harness;

namespace SchoolAssistant.Tests;

public class CleanupTests : Harness
{
    static readonly DateOnly FirstOfThisMonth = new(ManilaTime.Today.Year, ManilaTime.Today.Month, 1);
    static readonly DateTimeOffset LastMonth = CalendarEventBuilder.At(FirstOfThisMonth.AddDays(-3), TimeOnly.MinValue);
    static readonly DateTimeOffset Cutoff = CalendarEventBuilder.At(FirstOfThisMonth, TimeOnly.MinValue);

    static string KeyFor(DateOnly start) => $"g|MATH|{DateText.Compact(start)}";

    async Task AddSyncRow(string gid, string rk, string? key)
    {
        await SyncStore.SetAsync(gid, rk, key ?? "", "hash", "ev", SyncStatus.Synced, "T");
        if (key == null) await Table(Tables.Synced).UpsertEntityAsync(new TableEntity(gid, rk) { [Columns.Key] = null }, TableUpdateMode.Replace);
    }

    // ---------- the stores ----------
    [Fact]
    public async Task Old_messages_are_deleted_and_recent_ones_kept()
    {
        await MessageStore.TryRecordAsync("old", "a");
        await MessageStore.TryRecordAsync("new", "b");
        Table(Tables.Messages).SetTimestamp(Tables.MessagesPartition, "old", LastMonth);

        Assert.Equal(1, await MessageStore.DeleteOlderThanAsync(Cutoff));
        Assert.Null(Table(Tables.Messages).Find(Tables.MessagesPartition, "old"));
        Assert.NotNull(Table(Tables.Messages).Find(Tables.MessagesPartition, "new"));
    }

    [Fact]
    public async Task Only_old_answered_questions_are_deleted()
    {
        var ev = new ExtractedEvent { Title = "Quiz", Date = FutureDate(3) };
        var oldDone = await PendingStore.AddAsync(ev, "");
        var oldWaiting = await PendingStore.AddAsync(ev, "");
        var newDone = await PendingStore.AddAsync(ev, "");
        await PendingStore.ClaimAsync(oldDone);
        await PendingStore.ClaimAsync(newDone);
        Table(Tables.Pending).SetTimestamp(Tables.PendingPartition, oldDone, LastMonth);
        Table(Tables.Pending).SetTimestamp(Tables.PendingPartition, oldWaiting, LastMonth);

        Assert.Equal(1, await PendingStore.DeleteAnsweredOlderThanAsync(Cutoff));
        Assert.Null(Table(Tables.Pending).Find(Tables.PendingPartition, oldDone));
        Assert.NotNull(Table(Tables.Pending).Find(Tables.PendingPartition, oldWaiting)); // still waiting for a tap
        Assert.NotNull(Table(Tables.Pending).Find(Tables.PendingPartition, newDone));
    }

    [Fact]
    public async Task Sheet_records_are_deleted_by_the_first_day_of_the_item()
    {
        await AddSyncRow("g", "old", KeyFor(FirstOfThisMonth.AddDays(-10)));
        await AddSyncRow("g", "lastDay", KeyFor(FirstOfThisMonth.AddDays(-1)));
        await AddSyncRow("g", "firstOfMonth", KeyFor(FirstOfThisMonth));
        await AddSyncRow("g", "later", KeyFor(FirstOfThisMonth.AddDays(20)));
        await AddSyncRow("g", "badKey", "garbage");
        await AddSyncRow("g", "noKey", null);

        Assert.Equal(2, await SyncStore.DeleteStartingBeforeAsync(FirstOfThisMonth));
        Assert.Equal(new[] { "badKey", "firstOfMonth", "later", "noKey" }, Table(Tables.Synced).Rows.Select(r => r.RowKey).OrderBy(k => k));
    }

    // ---------- the whole cleanup ----------
    [Fact]
    public async Task Cleanup_removes_last_months_records_and_keeps_this_months_and_the_sheet_fingerprints()
    {
        var ev = new ExtractedEvent { Title = "Quiz", Date = FutureDate(3) };
        await MessageStore.TryRecordAsync("old", "a");
        await MessageStore.TryRecordAsync("new", "b");
        Table(Tables.Messages).SetTimestamp(Tables.MessagesPartition, "old", LastMonth);
        var oldDone = await PendingStore.AddAsync(ev, "");
        await PendingStore.ClaimAsync(oldDone);
        Table(Tables.Pending).SetTimestamp(Tables.PendingPartition, oldDone, LastMonth);
        await AddSyncRow("g", "old", KeyFor(FirstOfThisMonth.AddDays(-5)));
        await AddSyncRow("g", "current", KeyFor(ManilaTime.Today));
        await SheetStateStore.SaveCsvHashAsync("g", "fingerprint");

        var result = await new StorageCleanup(NullLogger<StorageCleanup>.Instance).RunAsync();

        Assert.Equal((1, 1, 1, FirstOfThisMonth), (result.Messages, result.AnsweredQuestions, result.SheetRecords, result.KeptFrom));
        Assert.Equal(3, result.Total);
        Assert.Equal(new[] { "current" }, Table(Tables.Synced).Rows.Select(r => r.RowKey));
        Assert.Equal(new[] { "new" }, Table(Tables.Messages).Rows.Select(r => r.RowKey));
        Assert.Equal("fingerprint", await SheetStateStore.GetCsvHashAsync("g"));
    }

    [Fact]
    public async Task Cleanup_with_nothing_old_removes_nothing()
    {
        await MessageStore.TryRecordAsync("new", "b");
        var result = await new StorageCleanup(NullLogger<StorageCleanup>.Instance).RunAsync();
        Assert.Equal(0, result.Total);
        Assert.Single(Table(Tables.Messages).Rows);
    }

    [Fact]
    public void The_reply_names_the_counts_or_says_nothing_was_found()
    {
        var first = new DateOnly(2026, 11, 1);
        Assert.Equal("🧹 Cleaned up everything before Sun Nov 1: 4 messages, 2 answered questions, 7 old sheet records.", new CleanupResult(4, 2, 7, first).Describe());
        Assert.Equal("🧹 Nothing to clean up before Sun Nov 1.", new CleanupResult(0, 0, 0, first).Describe());
    }

    // ---------- the monthly timer ----------
    [Fact]
    public async Task The_timer_reports_only_when_it_removed_something()
    {
        var timer = new CleanupTimer(new StorageCleanup(NullLogger<StorageCleanup>.Instance), Telegram);

        await timer.Run(new TimerInfo());
        Assert.Empty(Sent);

        await MessageStore.TryRecordAsync("old", "a");
        Table(Tables.Messages).SetTimestamp(Tables.MessagesPartition, "old", LastMonth);
        await timer.Run(new TimerInfo());
        Assert.Contains("1 messages", Assert.Single(Sent));
    }
}
