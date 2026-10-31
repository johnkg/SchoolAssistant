using GEvent = Google.Apis.Calendar.v3.Data.Event;
using GEventDateTime = Google.Apis.Calendar.v3.Data.EventDateTime;

namespace SchoolAssistant;

/// <summary>Short text for events: what the Telegram messages and the LLM comparison prompts show.</summary>
internal static class EventText
{
    const int LineDescriptionMaxChars = 80; // per existing event, in the similarity prompt
    const string UnknownWhen = "?";

    /// <summary>When an extracted event happens: day, range or day and start time.</summary>
    public static string When(ExtractedEvent e)
    {
        if (!DateText.TryDate(e.Date, out var first)) return "no date";
        if (!e.IsBirthday && DateText.TryDate(e.EndDate, out var last) && last > first)
            return DateText.Range(first, last);
        return DateText.TryTime(e.StartTime, out var start)
            ? $"{DateText.Day(first)} {DateText.Time(start)}"
            : DateText.Day(first);
    }

    /// <summary>When an existing calendar event happens: single day, time, or all-day range.</summary>
    public static string When(GEvent g)
    {
        if (DateText.TryDate(g.Start?.Date, out var first) && DateText.TryDate(g.End?.Date, out var endExclusive))
        {
            var last = endExclusive.AddDays(-CalendarConventions.AllDayEndOffsetDays);
            if (last > first) return DateText.Range(first, last);
        }
        return When(g.Start);
    }

    /// <summary>One compact line per existing event, used when asking the LLM about similarity.</summary>
    public static string Line(GEvent g)
    {
        var description = TextUtil.Flatten(g.Description ?? "");
        if (description.Length > LineDescriptionMaxChars) description = description[..LineDescriptionMaxChars];
        var repeats = g.Recurrence is { Count: > 0 } ? " (repeats)" : "";
        return $"{g.Id} | {When(g)}{repeats} | {g.Summary} | {description}";
    }

    static string When(GEventDateTime? t)
    {
        if (t == null) return UnknownWhen;
        if (DateText.TryDate(t.Date, out var day)) return DateText.Day(day);
        if (t.DateTimeDateTimeOffset is DateTimeOffset moment)
            return moment.ToOffset(ManilaTime.Offset).ToString(Formats.ShortDayTime, Formats.Inv);
        return t.DateTimeRaw ?? UnknownWhen;
    }
}
