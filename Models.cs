using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchoolAssistant;

public class ExtractedEvent
{
    public string Type { get; set; } = "event";
    public string Title { get; set; } = "School event";
    public string? Date { get; set; }
    public string? EndDate { get; set; }
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    public string? Location { get; set; }
    public List<string> Topics { get; set; } = new();
    public List<string> Pages { get; set; } = new();
    public List<string> References { get; set; } = new();
    public string? Details { get; set; }
    public string? Body { get; set; } // announcement text for important events
    public string? Description { get; set; } // full description override (used for sheet items)
    public string Confidence { get; set; } = "medium";

    [JsonIgnore] public bool IsBirthday => Type == "birthday";
    [JsonIgnore] public bool IsAssessment => Type is "quiz" or "exam" or "written_work" or "performance_task";
}

public record Extraction(List<ExtractedEvent> Events, string? Note)
{
    // Tolerant parser: LLMs sometimes return numbers where we expect strings.
    public static Extraction Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var list = new List<ExtractedEvent>();

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("events", out var evs) && evs.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in evs.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                list.Add(new ExtractedEvent
                {
                    Type = (Str(e, "type") ?? "event").ToLowerInvariant().Replace(' ', '_').Replace('-', '_'),
                    Title = Str(e, "title") ?? "School event",
                    Date = Str(e, "date"),
                    EndDate = Str(e, "end_date"),
                    StartTime = Str(e, "start_time"),
                    EndTime = Str(e, "end_time"),
                    Location = Str(e, "location"),
                    Topics = Strs(e, "topics"),
                    Pages = Strs(e, "pages"),
                    References = Strs(e, "references"),
                    Details = Str(e, "details"),
                    Body = Str(e, "body"),
                    Confidence = (Str(e, "confidence") ?? "medium").ToLowerInvariant()
                });
            }
        }
        return new Extraction(list, root.ValueKind == JsonValueKind.Object ? Str(root, "note") : null);
    }

    static string? Str(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v)) return null;
        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };
        s = s?.Trim();
        return string.IsNullOrEmpty(s) || s.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : s;
    }

    static List<string> Strs(JsonElement e, string key)
    {
        var res = new List<string>();
        if (!e.TryGetProperty(key, out var v)) return res;
        if (v.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in v.EnumerateArray())
            {
                if (x.ValueKind == JsonValueKind.Null) continue;
                var t = x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText();
                if (!string.IsNullOrWhiteSpace(t)) res.Add(t.Trim());
            }
        }
        else if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
        {
            res.Add(v.GetString()!.Trim());
        }
        return res;
    }
}