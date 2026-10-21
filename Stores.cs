using System.Net;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace SchoolAssistant;

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
}

/// <summary>What we've already done for each sheet item (Synced table, one row per tab + subject + first day).</summary>
internal static class SyncStore
{
    const string Yes = "1";

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
        Db.Table(Tables.Synced).UpsertEntityAsync(new TableEntity(gid, rk) { [Columns.GoneAsked] = Yes }, TableUpdateMode.Merge);

    public static bool IsGoneAsked(TableEntity row) => row.GetString(Columns.GoneAsked) == Yes;

    // Remember that we asked about this version, so we don't ask again every hour.
    public static Task AskedAsync(string gid, string rk, string key, string askedHash) =>
        Db.Table(Tables.Synced).UpsertEntityAsync(new TableEntity(gid, rk)
        {
            [Columns.Key] = key,
            [Columns.AskedHash] = askedHash
        }, TableUpdateMode.Merge);

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
