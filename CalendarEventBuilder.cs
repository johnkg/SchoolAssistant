using GEvent = Google.Apis.Calendar.v3.Data.Event;
using GEventDateTime = Google.Apis.Calendar.v3.Data.EventDateTime;

namespace SchoolAssistant;

/// <summary>Turns an <see cref="ExtractedEvent"/> into the Google Calendar event that gets saved.</summary>
internal static class CalendarEventBuilder
{
    const string YearlyRule = "RRULE:FREQ=YEARLY";
    const int DefaultDurationHours = 1;    // timed event with a start but no (valid) end
    const int AnnouncementMaxChars = 1500; // announcement text copied into the description

    public static GEvent Build(ExtractedEvent e)
    {
        if (!DateText.TryDate(e.Date, out var first)) throw new InvalidOperationException("Event has no valid date");

        var calendarEvent = new GEvent
        {
            Summary = e.Title,
            Description = Describe(e),
            Location = e.Location
        };

        var isSingleDay = e.EndDate is null || e.EndDate == e.Date;
        if (!e.IsBirthday && isSingleDay && DateText.TryTime(e.StartTime, out var startTime))
        {
            var start = At(first, startTime);
            var end = DateText.TryTime(e.EndTime, out var endTime) && endTime > startTime
                ? At(first, endTime)
                : start.AddHours(DefaultDurationHours);
            calendarEvent.Start = Timed(start);
            calendarEvent.End = Timed(end);
        }
        else
        {
            var last = !e.IsBirthday && DateText.TryDate(e.EndDate, out var endDate) && endDate > first ? endDate : first;
            calendarEvent.Start = new GEventDateTime { Date = DateText.Iso(first) };
            calendarEvent.End = new GEventDateTime { Date = DateText.Iso(last.AddDays(CalendarConventions.AllDayEndOffsetDays)) };
        }

        if (e.IsBirthday) calendarEvent.Recurrence = new List<string> { YearlyRule };
        return calendarEvent;
    }

    public static DateTimeOffset At(DateOnly date, TimeOnly time) => new(date.ToDateTime(time), ManilaTime.Offset);

    static GEventDateTime Timed(DateTimeOffset moment) =>
        new() { DateTimeDateTimeOffset = moment, TimeZone = ManilaTime.TimeZoneId };

    static string Describe(ExtractedEvent e)
    {
        if (!string.IsNullOrWhiteSpace(e.Description)) return e.Description!;

        var lines = new List<string>();
        if (DateText.TryTime(e.StartTime, out var start))
        {
            var time = DateText.Time(start);
            if (DateText.TryTime(e.EndTime, out var end)) time += Formats.RangeSeparator + DateText.Time(end);
            lines.Add($"Time: {time}");
        }
        AddList(lines, "Topics", e.Topics);
        AddList(lines, "Pages", e.Pages);
        AddList(lines, "References", e.References);
        if (!string.IsNullOrWhiteSpace(e.Details)) lines.Add(e.Details!);
        if (e.IsAssessment && !string.IsNullOrWhiteSpace(e.Body))
        {
            lines.Add("");
            lines.Add("Announcement:");
            lines.Add(TextUtil.Truncate(e.Body!.Trim(), AnnouncementMaxChars));
        }
        return string.Join("\n", lines);
    }

    static void AddList(List<string> lines, string label, List<string> values)
    {
        if (values.Count > 0) lines.Add($"{label}: {string.Join("; ", values)}");
    }
}
