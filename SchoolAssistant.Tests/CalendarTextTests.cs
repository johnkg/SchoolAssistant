using Google.Apis.Calendar.v3.Data;
using Xunit;

namespace SchoolAssistant.Tests;

public class EventTextTests
{
    static Event AllDay(string first, string endExclusive) =>
        new() { Id = "e1", Summary = "Trip", Start = new EventDateTime { Date = first }, End = new EventDateTime { Date = endExclusive } };

    [Fact] public void Extracted_event_without_a_date() => Assert.Equal("no date", EventText.When(new ExtractedEvent()));
    [Fact] public void Extracted_single_day() => Assert.Equal("Fri Oct 9", EventText.When(new ExtractedEvent { Date = "2026-10-09" }));
    [Fact] public void Extracted_with_start_time() => Assert.Equal("Fri Oct 9 9:30 AM", EventText.When(new ExtractedEvent { Date = "2026-10-09", StartTime = "09:30" }));
    [Fact] public void Extracted_range() => Assert.Equal("Mon Oct 12 – Fri Oct 16", EventText.When(new ExtractedEvent { Date = "2026-10-12", EndDate = "2026-10-16" }));
    [Fact] public void Extracted_birthday_ignores_end_date() => Assert.Equal("Mon Oct 12", EventText.When(new ExtractedEvent { Type = EventTypes.Birthday, Date = "2026-10-12", EndDate = "2026-10-16" }));
    [Fact] public void Extracted_end_before_start_is_a_single_day() => Assert.Equal("Mon Oct 12", EventText.When(new ExtractedEvent { Date = "2026-10-12", EndDate = "2026-10-10" }));

    [Fact] public void Calendar_all_day_single() => Assert.Equal("Mon Oct 12", EventText.When(AllDay("2026-10-12", "2026-10-13")));
    [Fact] public void Calendar_all_day_range_shows_the_inclusive_last_day() => Assert.Equal("Mon Oct 12 – Wed Oct 14", EventText.When(AllDay("2026-10-12", "2026-10-15")));

    [Fact]
    public void Calendar_timed_event_shows_manila_time()
    {
        var timed = new Event { Start = new EventDateTime { DateTimeDateTimeOffset = new DateTimeOffset(2026, 10, 9, 1, 0, 0, TimeSpan.Zero) } };
        Assert.Equal("Fri Oct 9 9:00 AM", EventText.When(timed));
    }

    [Fact] public void Calendar_event_without_start_is_a_question_mark() => Assert.Equal("?", EventText.When(new Event()));
    [Fact] public void Calendar_event_with_empty_start_is_a_question_mark() => Assert.Equal("?", EventText.When(new Event { Start = new EventDateTime() }));

    [Fact]
    public void Line_has_id_when_title_and_flattened_short_description()
    {
        var e = AllDay("2026-10-12", "2026-10-13");
        e.Description = "line one\n\nline two " + new string('x', 100);
        var line = EventText.Line(e);
        Assert.StartsWith("e1 | Mon Oct 12 | Trip | line one line two ", line);
        Assert.Equal(80, line[(line.LastIndexOf("| ") + 2)..].Length);
    }

    [Fact] public void Line_marks_repeating_events() => Assert.Contains("(repeats)", EventText.Line(new Event { Id = "b", Summary = "Bday", Recurrence = new[] { "RRULE:FREQ=YEARLY" }, Start = new EventDateTime { Date = "2026-10-12" } }));
    [Fact] public void Line_copes_with_a_missing_description() => Assert.EndsWith("| Trip | ", EventText.Line(AllDay("2026-10-12", "2026-10-13")));
}

public class CalendarEventBuilderTests
{
    static ExtractedEvent Quiz(Action<ExtractedEvent>? change = null)
    {
        var e = new ExtractedEvent { Type = EventTypes.Quiz, Title = "Science Quiz", Date = "2026-10-09" };
        change?.Invoke(e);
        return e;
    }

    [Fact]
    public void Single_day_is_all_day_with_exclusive_end()
    {
        var built = CalendarEventBuilder.Build(Quiz());
        Assert.Equal(("Science Quiz", "2026-10-09", "2026-10-10"), (built.Summary, built.Start.Date, built.End.Date));
        Assert.Null(built.Recurrence);
    }

    [Fact]
    public void Range_runs_through_the_last_day()
    {
        var built = CalendarEventBuilder.Build(Quiz(e => e.EndDate = "2026-10-12"));
        Assert.Equal("2026-10-13", built.End.Date);
    }

    [Fact]
    public void Timed_event_uses_manila_time_and_the_given_end()
    {
        var built = CalendarEventBuilder.Build(Quiz(e => { e.StartTime = "09:00"; e.EndTime = "10:30"; e.Location = "Room 4"; }));
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.FromHours(8)), built.Start.DateTimeDateTimeOffset);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 10, 30, 0, TimeSpan.FromHours(8)), built.End.DateTimeDateTimeOffset);
        Assert.Equal("Asia/Manila", built.Start.TimeZone);
        Assert.Equal("Room 4", built.Location);
    }

    [Theory, InlineData(null), InlineData("08:00"), InlineData("nonsense")]
    public void Timed_event_without_a_usable_end_lasts_one_hour(string? endTime)
    {
        var built = CalendarEventBuilder.Build(Quiz(e => { e.StartTime = "09:00"; e.EndTime = endTime; }));
        Assert.Equal(built.Start.DateTimeDateTimeOffset!.Value.AddHours(1), built.End.DateTimeDateTimeOffset);
    }

    [Fact]
    public void A_time_on_a_multi_day_event_is_ignored_for_the_schedule()
    {
        var built = CalendarEventBuilder.Build(Quiz(e => { e.StartTime = "09:00"; e.EndDate = "2026-10-12"; }));
        Assert.Equal("2026-10-09", built.Start.Date);
        Assert.Null(built.Start.DateTimeDateTimeOffset);
    }

    [Fact]
    public void Same_day_end_date_still_counts_as_a_timed_single_day()
    {
        var built = CalendarEventBuilder.Build(Quiz(e => { e.StartTime = "09:00"; e.EndDate = "2026-10-09"; }));
        Assert.NotNull(built.Start.DateTimeDateTimeOffset);
    }

    [Fact]
    public void Birthdays_repeat_yearly_all_day_and_ignore_times_and_end_dates()
    {
        var built = CalendarEventBuilder.Build(Quiz(e => { e.Type = EventTypes.Birthday; e.StartTime = "09:00"; e.EndDate = "2026-10-20"; }));
        Assert.Equal(new[] { "RRULE:FREQ=YEARLY" }, built.Recurrence);
        Assert.Equal(("2026-10-09", "2026-10-10"), (built.Start.Date, built.End.Date));
    }

    [Theory, InlineData(null), InlineData("nope")]
    public void An_invalid_date_is_rejected(string? date) =>
        Assert.Throws<InvalidOperationException>(() => CalendarEventBuilder.Build(Quiz(e => e.Date = date)));

    [Fact]
    public void Description_override_wins()
    {
        Assert.Equal("From the sheet", CalendarEventBuilder.Build(Quiz(e => { e.Description = "From the sheet"; e.Topics.Add("ignored"); })).Description);
    }

    [Fact]
    public void Description_lists_time_topics_pages_references_details_and_announcement_for_assessments()
    {
        var built = CalendarEventBuilder.Build(Quiz(e =>
        {
            e.StartTime = "09:00"; e.EndTime = "10:00";
            e.Topics.AddRange(new[] { "Fractions", "Decimals" });
            e.Pages.Add("pp. 1-5");
            e.References.Add("Workbook");
            e.Details = "Bring a pen";
            e.Body = "  Original text  ";
        }));
        Assert.Equal("Time: 9:00 AM – 10:00 AM\nTopics: Fractions; Decimals\nPages: pp. 1-5\nReferences: Workbook\nBring a pen\n\nAnnouncement:\nOriginal text", built.Description);
    }

    [Fact] public void Description_shows_start_time_alone() => Assert.Equal("Time: 9:00 AM", CalendarEventBuilder.Build(Quiz(e => e.StartTime = "09:00")).Description);

    [Fact]
    public void Announcement_is_only_for_assessments_and_is_truncated()
    {
        Assert.DoesNotContain("Announcement", CalendarEventBuilder.Build(Quiz(e => { e.Type = EventTypes.Event; e.Body = "text"; })).Description);
        var longBody = CalendarEventBuilder.Build(Quiz(e => e.Body = new string('x', 2000))).Description;
        Assert.EndsWith("…", longBody);
        Assert.Equal(1501, longBody![(longBody.IndexOf("Announcement:\n") + 14)..].Length);
    }

    [Fact] public void At_is_manila_time() => Assert.Equal(TimeSpan.FromHours(8), CalendarEventBuilder.At(new DateOnly(2026, 1, 1), new TimeOnly(0, 0)).Offset);
}

public class JujuCalendarStaticTests
{
    [Fact] public void StartDay_of_all_day_event() => Assert.Equal(new DateOnly(2026, 10, 9), JujuCalendar.StartDay(new Event { Start = new EventDateTime { Date = "2026-10-09" } }));
    [Fact] public void StartDay_of_timed_event_uses_manila_date() => Assert.Equal(new DateOnly(2026, 10, 10), JujuCalendar.StartDay(new Event { Start = new EventDateTime { DateTimeDateTimeOffset = new DateTimeOffset(2026, 10, 9, 20, 0, 0, TimeSpan.Zero) } }));
    [Fact] public void StartDay_without_start_is_null() => Assert.Null(JujuCalendar.StartDay(new Event()));
    [Fact] public void StartDay_with_empty_start_is_null() => Assert.Null(JujuCalendar.StartDay(new Event { Start = new EventDateTime() }));

    [Fact] public void EndDay_of_all_day_range_is_inclusive() => Assert.Equal(new DateOnly(2026, 10, 11), JujuCalendar.EndDay(new Event { Start = new EventDateTime { Date = "2026-10-09" }, End = new EventDateTime { Date = "2026-10-12" } }));
    [Fact] public void EndDay_never_precedes_the_start() => Assert.Equal(new DateOnly(2026, 10, 9), JujuCalendar.EndDay(new Event { Start = new EventDateTime { Date = "2026-10-09" }, End = new EventDateTime { Date = "2026-10-09" } }));
    [Fact] public void EndDay_without_an_all_day_end_is_the_start() => Assert.Equal(new DateOnly(2026, 10, 9), JujuCalendar.EndDay(new Event { Start = new EventDateTime { Date = "2026-10-09" } }));
    [Fact] public void EndDay_of_an_event_with_no_dates_is_null() => Assert.Null(JujuCalendar.EndDay(new Event()));

    [Fact]
    public void SheetText_drops_footer_and_previous_details()
    {
        var text = $"Cell text\n\n{SheetSync.SourceFooter} · MATH · B4\n\n— Previous details —\nold";
        Assert.Equal("Cell text", JujuCalendar.SheetText(new Event { Description = text }));
    }

    [Fact] public void SheetText_of_no_description_is_empty() => Assert.Equal("", JujuCalendar.SheetText(new Event()));

    [Theory, InlineData(System.Net.HttpStatusCode.NotFound, true), InlineData(System.Net.HttpStatusCode.Gone, true), InlineData(System.Net.HttpStatusCode.Forbidden, false)]
    public void IsGone_is_true_for_404_and_410(System.Net.HttpStatusCode status, bool expected) =>
        Assert.Equal(expected, JujuCalendar.IsGone(new Google.GoogleApiException("svc", "msg") { HttpStatusCode = status }));

    [Fact] public void IsGone_is_false_for_other_exceptions() => Assert.False(JujuCalendar.IsGone(new InvalidOperationException()));
}
