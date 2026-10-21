using System.Globalization;

namespace SchoolAssistant;

/// <summary>App setting (environment variable) names.</summary>
internal static class Settings
{
    public const string TelegramToken = "TELEGRAM_TOKEN";
    public const string TelegramSecret = "TELEGRAM_SECRET";
    public const string TelegramChatId = "TELEGRAM_CHAT_ID";
    public const string DebugReplies = "DEBUG_REPLIES";
    public const string ConfirmLowConfidence = "CONFIRM_LOW_CONFIDENCE";
    public const string LlmProvider = "LLM_PROVIDER";
    public const string OpenAiApiKey = "OPENAI_API_KEY";
    public const string OpenAiModel = "OPENAI_MODEL";
    public const string AnthropicApiKey = "ANTHROPIC_API_KEY";
    public const string AnthropicModel = "ANTHROPIC_MODEL";
    public const string GoogleServiceAccountJson = "GOOGLE_SA_JSON";
    public const string CalendarId = "JUJU_CALENDAR_ID";
    public const string SheetId = "SHEET_ID";
    public const string SheetGids = "SHEET_GIDS";
    public const string TablesConnectionString = "TABLES_CONNECTION_STRING";
    public const string WebJobsStorage = "AzureWebJobsStorage";
    public const string AppInsightsConnectionString = "APPLICATIONINSIGHTS_CONNECTION_STRING";
}

/// <summary>Table Storage names. These are stored data: changing a value orphans existing rows.</summary>
internal static class Tables
{
    public const string Messages = "Messages";
    public const string Pending = "Pending";
    public const string State = "State";
    public const string Synced = "Synced";

    public const string MessagesPartition = "tg";
    public const string PendingPartition = "p";
    public const string StatePartition = "sheet";
}

/// <summary>Column names shared by the code that writes a row and the code that reads it back.</summary>
internal static class Columns
{
    // Messages
    public const string Text = "Text";
    public const string ResultJson = "ResultJson";

    // Pending
    public const string EventJson = "EventJson";
    public const string MatchId = "MatchId";
    public const string Status = "Status";
    public const string Kind = "Kind";
    public const string Gid = "Gid";
    public const string Rk = "Rk";
    public const string OldRk = "OldRk";

    // Pending and Synced
    public const string Key = "Key";
    public const string Hash = "Hash";

    // Synced
    public const string AskedHash = "AskedHash";
    public const string EventId = "EventId";
    public const string Title = "Title";
    public const string GoneAsked = "GoneAsked";
}

/// <summary>Where a pending proposal came from (Pending.Kind).</summary>
internal static class PendingKind
{
    public const string Telegram = "tg";
    public const string SheetPrefix = "sheet";
    public const string SheetNew = "sheet-new";
    public const string SheetChanged = "sheet-chg";
    public const string SheetRemoved = "sheet-del";
}

/// <summary>Pending.Status: a proposal is claimed (done) when a button is tapped.</summary>
internal static class PendingStatus
{
    public const string Pending = "pending";
    public const string Done = "done";
}

/// <summary>Synced.Status for one sheet item.</summary>
internal static class SyncStatus
{
    public const string Synced = "synced";
    public const string Skipped = "skipped";
    public const string Removed = "removed";
    public const string Kept = "kept";
    public const string Moved = "moved";
}

/// <summary>Telegram button callback data: "{action}:{pending id}".</summary>
internal static class CallbackAction
{
    public const char Separator = ':';
    public const string Update = "u";
    public const string AddNew = "n";
    public const string Skip = "s";
    public const string Delete = "d";
}

/// <summary>Date and time formats used for storage, the LLM and Telegram text.</summary>
internal static class Formats
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const string IsoDate = "yyyy-MM-dd";
    public const string CompactDate = "yyyyMMdd";
    public const string Time24 = "HH:mm";
    public const string Time12 = "h:mm tt";
    public const string ShortDay = "ddd MMM d";
    public const string ShortDayTime = "ddd MMM d h:mm tt";
    public const string LongDay = "dddd, yyyy-MM-dd";
    public const string RangeSeparator = " – ";
    public const string Ellipsis = "…";
}

internal static class ManilaTime
{
    const int UtcOffsetHours = 8; // Asia/Manila has no DST

    public const string TimeZoneId = "Asia/Manila";
    public static readonly TimeSpan Offset = TimeSpan.FromHours(UtcOffsetHours);

    public static DateTimeOffset Now => DateTimeOffset.UtcNow.ToOffset(Offset);
    public static DateOnly Today => DateOnly.FromDateTime(Now.DateTime);
}
