using System.Net;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace SchoolAssistant;

/// <summary>Telegram updates already seen (Messages table), so a repeated delivery is processed once.</summary>
internal static class MessageStore
{
    /// <summary>Records the update. Returns false if it was recorded before.</summary>
    public static async Task<bool> TryRecordAsync(string updateId, string text)
    {
        try
        {
            await Db.Table(Tables.Messages).AddEntityAsync(new TableEntity(Tables.MessagesPartition, updateId) { [Columns.Text] = text });
            return true;
        }
        catch (RequestFailedException e) when (e.Status == (int)HttpStatusCode.Conflict) { return false; }
    }

    /// <summary>Deletes updates last written before <paramref name="cutoff"/>. Returns how many.</summary>
    public static Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff) =>
        TableCleanup.DeleteWhereAsync(Db.Table(Tables.Messages), Array.Empty<string>(), row => row.Timestamp < cutoff);

    public static Task SaveResultAsync(string updateId, string resultJson) =>
        Db.Table(Tables.Messages).UpdateEntityAsync(
            new TableEntity(Tables.MessagesPartition, updateId) { [Columns.ResultJson] = resultJson }, ETag.All, TableUpdateMode.Merge);
}

/// <summary>A saved proposal, read back when its button is tapped. Sheet columns are empty for Telegram proposals.</summary>
internal sealed record Proposal(
    ExtractedEvent Event, string MatchId, string Kind, string Gid, string Rk, string Key, string Hash, string OldRk)
{
    public bool FromSheet => Kind.StartsWith(PendingKind.SheetPrefix, StringComparison.Ordinal);
    public bool ChangedOnSheet => Kind == PendingKind.SheetChanged;
    public bool RemovedFromSheet => Kind == PendingKind.SheetRemoved;
}

internal enum ClaimOutcome { Claimed, Expired, AlreadyHandled }

/// <summary>Proposals waiting for a button tap (Pending table).</summary>
internal static class PendingStore
{
    const int IdLength = 12; // keeps "{action}:{id}" well under Telegram's 64-byte callback_data limit

    /// <summary>Saves a proposal and returns its id for the buttons. <paramref name="extra"/> adds sheet-specific columns.</summary>
    public static async Task<string> AddAsync(ExtractedEvent ev, string matchId, IReadOnlyDictionary<string, object>? extra = null)
    {
        var id = Guid.NewGuid().ToString("N")[..IdLength];
        var row = new TableEntity(Tables.PendingPartition, id)
        {
            [Columns.EventJson] = JsonSerializer.Serialize(ev),
            [Columns.MatchId] = matchId,
            [Columns.Status] = PendingStatus.Pending
        };
        if (extra != null)
            foreach (var (k, v) in extra) row[k] = v;

        await Db.Table(Tables.Pending).AddEntityAsync(row);
        return id;
    }

    /// <summary>
    /// Marks the proposal done so a double tap can't run twice (the ETag makes the second write fail).
    /// Returns the proposal only when this call won the claim.
    /// </summary>
    public static async Task<(ClaimOutcome Outcome, Proposal? Proposal)> ClaimAsync(string id)
    {
        var table = Db.Table(Tables.Pending);
        TableEntity row;
        try { row = (await table.GetEntityAsync<TableEntity>(Tables.PendingPartition, id)).Value; }
        catch (RequestFailedException e) when (e.Status == (int)HttpStatusCode.NotFound) { return (ClaimOutcome.Expired, null); }

        if (row.GetString(Columns.Status) != PendingStatus.Pending) return (ClaimOutcome.AlreadyHandled, null);

        row[Columns.Status] = PendingStatus.Done;
        try { await table.UpdateEntityAsync(row, row.ETag); }
        catch (RequestFailedException e) when (e.Status == (int)HttpStatusCode.PreconditionFailed) { return (ClaimOutcome.AlreadyHandled, null); }

        return (ClaimOutcome.Claimed, new Proposal(
            JsonSerializer.Deserialize<ExtractedEvent>(row.GetString(Columns.EventJson))!,
            Text(row, Columns.MatchId), row.GetString(Columns.Kind) ?? PendingKind.Telegram,
            Text(row, Columns.Gid), Text(row, Columns.Rk), Text(row, Columns.Key), Text(row, Columns.Hash), Text(row, Columns.OldRk)));
    }

    /// <summary>
    /// Deletes answered questions last written before <paramref name="cutoff"/>. Unanswered ones stay however old,
    /// because deleting one would leave its sheet item marked as asked-about with no way to answer.
    /// </summary>
    public static Task<int> DeleteAnsweredOlderThanAsync(DateTimeOffset cutoff) =>
        TableCleanup.DeleteWhereAsync(Db.Table(Tables.Pending), new[] { Columns.Status },
            row => row.GetString(Columns.Status) == PendingStatus.Done && row.Timestamp < cutoff);

    /// <summary>Lets the buttons work again after the action failed.</summary>
    public static Task ReleaseAsync(string id) =>
        Db.Table(Tables.Pending).UpdateEntityAsync(
            new TableEntity(Tables.PendingPartition, id) { [Columns.Status] = PendingStatus.Pending }, ETag.All, TableUpdateMode.Merge);

    static string Text(TableEntity row, string column) => row.GetString(column) ?? "";
}

/// <summary>What we've already done for each sheet item (Synced table, one row per tab + subject + first day).</summary>
internal static class SyncStore
{
    const string FlagSet = "1"; // stored value of a yes/no column

    public static Task SetAsync(string gid, string rk, string key, string hash, string eventId, string status, string title) =>
        Db.Table(Tables.Synced).UpsertEntityAsync(new TableEntity(gid, rk)
        {
            [Columns.Key] = key,
            [Columns.Hash] = hash,
            [Columns.AskedHash] = hash,
            [Columns.EventId] = eventId,
            [Columns.Status] = status,
            [Columns.Title] = title
        }, TableUpdateMode.Replace);

    // Closes out a row whose sheet item is gone (deleted, moved to another day, or kept on purpose).
    public static Task MarkAsync(string gid, string rk, string status) =>
        Db.Table(Tables.Synced).UpsertEntityAsync(new TableEntity(gid, rk)
        {
            [Columns.Status] = status,
            [Columns.EventId] = ""
        }, TableUpdateMode.Merge);

    // Remember that we asked what to do about a vanished item, so we don't ask again every hour.
    public static Task GoneAskedAsync(string gid, string rk) =>
        Db.Table(Tables.Synced).UpsertEntityAsync(new TableEntity(gid, rk) { [Columns.GoneAsked] = FlagSet }, TableUpdateMode.Merge);

    public static bool IsGoneAsked(TableEntity row) => row.GetString(Columns.GoneAsked) == FlagSet;

    // Remember that we asked about this version, so we don't ask again every hour.
    public static Task AskedAsync(string gid, string rk, string key, string askedHash) =>
        Db.Table(Tables.Synced).UpsertEntityAsync(new TableEntity(gid, rk)
        {
            [Columns.Key] = key,
            [Columns.AskedHash] = askedHash
        }, TableUpdateMode.Merge);

    /// <summary>
    /// Deletes records of sheet items whose first day is before <paramref name="firstDayToKeep"/>. The past is never
    /// back-filled, so these are not needed again. Rows with no readable key are kept.
    /// </summary>
    public static Task<int> DeleteStartingBeforeAsync(DateOnly firstDayToKeep) =>
        TableCleanup.DeleteWhereAsync(Db.Table(Tables.Synced), new[] { Columns.Key },
            row => SheetItem.TryParseKey(row.GetString(Columns.Key), out _, out var start) && start < firstDayToKeep);

    public static async Task<Dictionary<string, TableEntity>> LoadTabAsync(string gid)
    {
        var rows = new Dictionary<string, TableEntity>();
        await foreach (var row in Db.Table(Tables.Synced).QueryAsync<TableEntity>(filter: $"PartitionKey eq '{gid}'"))
            rows[row.RowKey] = row;
        return rows;
    }
}

/// <summary>Hash of each tab's last fully processed CSV (State table), so unchanged sheets are skipped.</summary>
internal static class SheetStateStore
{
    const string RowKeyPrefix = "csv-";

    public static async Task<string?> GetCsvHashAsync(string gid)
    {
        try
        {
            var row = await Db.Table(Tables.State).GetEntityAsync<TableEntity>(Tables.StatePartition, RowKeyPrefix + gid);
            return row.Value.GetString(Columns.Hash);
        }
        catch (RequestFailedException e) when (e.Status == (int)HttpStatusCode.NotFound) { return null; }
    }

    public static Task SaveCsvHashAsync(string gid, string hash) =>
        Db.Table(Tables.State).UpsertEntityAsync(new TableEntity(Tables.StatePartition, RowKeyPrefix + gid) { [Columns.Hash] = hash });
}

/// <summary>Shared by the cleanup of old rows.</summary>
internal static class TableCleanup
{
    /// <summary>
    /// Deletes every row the test picks. Only the key columns, the timestamp and <paramref name="columns"/> are read.
    /// The picking is done here rather than in a query so the rules stay readable.
    /// </summary>
    public static async Task<int> DeleteWhereAsync(TableClient table, string[] columns, Func<TableEntity, bool> shouldDelete)
    {
        var select = columns.Concat(new[] { "PartitionKey", "RowKey", "Timestamp" }).ToList();
        var doomed = new List<(string PartitionKey, string RowKey)>();
        await foreach (var row in table.QueryAsync<TableEntity>(select: select))
            if (shouldDelete(row)) doomed.Add((row.PartitionKey, row.RowKey));

        foreach (var (partitionKey, rowKey) in doomed)
            await table.DeleteEntityAsync(partitionKey, rowKey);
        return doomed.Count;
    }
}
