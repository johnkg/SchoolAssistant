using System.Text.Json;
using Xunit;

namespace SchoolAssistant.Tests;

public class ExtractedEventTests
{
    [Fact] public void Defaults_are_a_medium_confidence_event() { var e = new ExtractedEvent(); Assert.Equal(EventTypes.Event, e.Type); Assert.Equal(Confidence.Medium, e.Confidence); Assert.Equal(ExtractedEvent.DefaultTitle, e.Title); }
    [Theory, InlineData(EventTypes.Quiz), InlineData(EventTypes.Exam), InlineData(EventTypes.WrittenWork), InlineData(EventTypes.PerformanceTask)]
    public void Assessments_are_flagged(string type) => Assert.True(new ExtractedEvent { Type = type }.IsAssessment);
    [Theory, InlineData(EventTypes.Event), InlineData(EventTypes.Birthday), InlineData(EventTypes.Other)]
    public void Other_types_are_not_assessments(string type) => Assert.False(new ExtractedEvent { Type = type }.IsAssessment);
    [Fact] public void Birthday_is_flagged() => Assert.True(new ExtractedEvent { Type = EventTypes.Birthday }.IsBirthday);
}

public class ExtractionParseTests
{
    [Fact]
    public void Reads_every_field()
    {
        var x = Extraction.Parse("""
            {"events":[{"type":"quiz","title":"Math Quiz","date":"2026-10-09","end_date":"2026-10-10","start_time":"09:00","end_time":"10:00",
            "location":"Room 4","topics":["a","b"],"pages":["pp. 1-2"],"references":["Book"],"details":"Bring pen","body":"Full text","confidence":"HIGH"}],"note":"hi"}
            """);
        var e = Assert.Single(x.Events);
        Assert.Equal("hi", x.Note);
        Assert.Equal(("quiz", "Math Quiz", "2026-10-09", "2026-10-10"), (e.Type, e.Title, e.Date, e.EndDate));
        Assert.Equal(("09:00", "10:00", "Room 4"), (e.StartTime, e.EndTime, e.Location));
        Assert.Equal(new[] { "a", "b" }, e.Topics);
        Assert.Equal(new[] { "pp. 1-2" }, e.Pages);
        Assert.Equal(new[] { "Book" }, e.References);
        Assert.Equal(("Bring pen", "Full text", "high"), (e.Details, e.Body, e.Confidence));
    }

    [Theory, InlineData("Written Work"), InlineData("written-work"), InlineData("WRITTEN_WORK")]
    public void Normalizes_type_spelling(string type)
    {
        var x = Extraction.Parse($"{{\"events\":[{{\"type\":\"{type}\"}}]}}");
        Assert.Equal(EventTypes.WrittenWork, x.Events[0].Type);
    }

    [Fact]
    public void Missing_fields_get_defaults()
    {
        var e = Extraction.Parse("""{"events":[{}]}""").Events.Single();
        Assert.Equal(EventTypes.Event, e.Type);
        Assert.Equal(ExtractedEvent.DefaultTitle, e.Title);
        Assert.Equal(Confidence.Medium, e.Confidence);
        Assert.Null(e.Date);
        Assert.Empty(e.Topics);
    }

    [Fact]
    public void Tolerates_numbers_and_literal_null_text()
    {
        var e = Extraction.Parse("""{"events":[{"title":123,"date":"null","location":true,"details":"  ","topics":[1,null,"x"," "]}]}""").Events.Single();
        Assert.Equal("123", e.Title);
        Assert.Null(e.Date);
        Assert.Null(e.Location);
        Assert.Null(e.Details);
        Assert.Equal(new[] { "1", "x" }, e.Topics);
    }

    [Fact]
    public void A_single_string_is_a_one_item_list_and_other_shapes_are_empty()
    {
        var events = Extraction.Parse("""{"events":[{"topics":" Fractions "},{"topics":5},{"topics":"  "}]}""").Events;
        Assert.Equal(new[] { "Fractions" }, events[0].Topics);
        Assert.Empty(events[1].Topics);
        Assert.Empty(events[2].Topics);
    }

    [Fact] public void Skips_events_that_are_not_objects() => Assert.Single(Extraction.Parse("""{"events":[1,"x",{"title":"ok"}]}""").Events);
    [Fact] public void Events_that_are_not_an_array_give_an_empty_list() => Assert.Empty(Extraction.Parse("""{"events":{}}""").Events);
    [Fact] public void A_root_that_is_not_an_object_gives_nothing() { var x = Extraction.Parse("[1]"); Assert.Empty(x.Events); Assert.Null(x.Note); }
    [Fact] public void Invalid_json_throws() => Assert.ThrowsAny<JsonException>(() => Extraction.Parse("{"));
}
