using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Data.Tables;

namespace SchoolAssistant;

internal static class Env
{
    public static string Get(string key) =>
        Environment.GetEnvironmentVariable(key) ?? throw new InvalidOperationException($"Missing setting: {key}");

    public static string GetOr(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

    /// <summary>A "true"/"false" setting; anything missing or unreadable gives <paramref name="defaultValue"/>.</summary>
    public static bool GetFlag(string key, bool defaultValue) =>
        bool.TryParse(Environment.GetEnvironmentVariable(key), out var value) ? value : defaultValue;

    public static long TelegramChatId => long.Parse(Get(Settings.TelegramChatId), CultureInfo.InvariantCulture);
}

internal static class Db
{
    /// <summary>How table clients are made. Tests replace it with an in-memory fake.</summary>
    public static Func<string, TableClient> TableFactory { get; set; } = CreateTable;

    public static TableClient Table(string name) => TableFactory(name);

    // Talks to Azure Storage, so it can't run in unit tests; tests swap TableFactory instead.
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    static TableClient CreateTable(string name)
    {
        // Flex Consumption uses identity-based storage (no AzureWebJobsStorage connection string),
        // so tables use their own connection string setting when present.
        var connectionString = Environment.GetEnvironmentVariable(Settings.TablesConnectionString);
        if (string.IsNullOrEmpty(connectionString)) connectionString = Env.Get(Settings.WebJobsStorage);
        var table = new TableClient(connectionString, name);
        table.CreateIfNotExists();
        return table;
    }
}

internal static class TextUtil
{
    const int ErrorMessageMaxChars = 200;

    /// <summary>Collapses all whitespace (including line breaks) to single spaces.</summary>
    public static string Flatten(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>Flattened and cut to at most <paramref name="max"/> characters, marked with an ellipsis.</summary>
    public static string Flat(string s, int max) => Truncate(Flatten(s), max);

    public static string Truncate(string s, int max) => s.Length > max ? s[..max] + Formats.Ellipsis : s;

    /// <summary>An exception message short enough for a Telegram reply.</summary>
    public static string Short(Exception ex) => Truncate(ex.Message.Replace("\n", " "), ErrorMessageMaxChars);
}

/// <summary>Parsing and formatting of the dates and times shared by the calendar, sheet and chat code.</summary>
internal static class DateText
{
    public static bool TryDate(string? s, out DateOnly date) =>
        DateOnly.TryParseExact(s, Formats.IsoDate, Formats.Inv, DateTimeStyles.None, out date);

    public static bool TryTime(string? s, out TimeOnly time) =>
        TimeOnly.TryParseExact(s, Formats.Time24, Formats.Inv, DateTimeStyles.None, out time);

    public static string Iso(DateOnly date) => date.ToString(Formats.IsoDate, Formats.Inv);

    public static string Compact(DateOnly date) => date.ToString(Formats.CompactDate, Formats.Inv);

    public static string Day(DateOnly date) => date.ToString(Formats.ShortDay, Formats.Inv);

    public static string Time(TimeOnly time) => time.ToString(Formats.Time12, Formats.Inv);

    /// <summary>One day, or "first – last" when the range spans more than one.</summary>
    public static string Range(DateOnly first, DateOnly last) =>
        last > first ? Day(first) + Formats.RangeSeparator + Day(last) : Day(first);
}

public static class LlmUtil
{
    /// <summary>Today's date in Manila time, so "next Friday" resolves correctly.</summary>
    public static string Today() => ManilaTime.Now.ToString(Formats.LongDay, Formats.Inv);

    /// <summary>Parses model output after <see cref="StripFences"/>. Throws <see cref="JsonException"/> if it is not JSON.</summary>
    internal static JsonDocument ParseJson(string modelOutput) => JsonDocument.Parse(StripFences(modelOutput));

    /// <summary>
    /// Cleans model output down to the first JSON value: drops code fences and anything the model
    /// wrote before or after the JSON (explanations, "The image shows...").
    /// </summary>
    public static string StripFences(string s)
    {
        s = s.Replace("```json", "").Replace("```", "").Trim();

        var start = s.IndexOfAny(new[] { '{', '[' });
        if (start < 0) return s;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{' || c == '[') depth++;
            else if (c == '}' || c == ']')
            {
                depth--;
                if (depth == 0) return s[start..(i + 1)];
            }
        }
        return s[start..]; // unbalanced (cut off): let the parser report it
    }
}
