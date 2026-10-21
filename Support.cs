using System.Text.RegularExpressions;
using Azure.Data.Tables;

namespace SchoolAssistant;

internal static class Env
{
    public static string Get(string key) =>
        Environment.GetEnvironmentVariable(key) ?? throw new InvalidOperationException($"Missing setting: {key}");

    public static string GetOr(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;
}

internal static class Db
{
    public static TableClient Table(string name)
    {
        // Flex Consumption uses identity-based storage (no AzureWebJobsStorage connection string),
        // so tables use their own connection string setting when present.
        var cs = Environment.GetEnvironmentVariable(Settings.TablesConnectionString);
        if (string.IsNullOrEmpty(cs)) cs = Env.Get(Settings.WebJobsStorage);
        var t = new TableClient(cs, name);
        t.CreateIfNotExists();
        return t;
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

public static class LlmUtil
{
    /// <summary>Today's date in Manila time, so "next Friday" resolves correctly.</summary>
    public static string Today() => ManilaTime.Now.ToString(Formats.LongDay, Formats.Inv);

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
