using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static SchoolAssistant.Tests.Harness;

namespace SchoolAssistant.Tests;

public class AnnouncementProcessorTests : Harness
{
    readonly AnnouncementProcessor _processor;

    public AnnouncementProcessorTests()
    {
        _processor = new AnnouncementProcessor(Llm, Telegram, Calendar, NullLogger<AnnouncementProcessor>.Instance);
        MessageStore.TryRecordAsync("u1", "text").GetAwaiter().GetResult();
    }

    string Extract(params string[] events) => EventsJson(events);

    Task Handle(string? text = "Science quiz on Friday", params LlmPart[] attachments) =>
        _processor.HandleAsync(ChatId, "u1", text, attachments);

    void Answer(string extraction, string? matchAnswer = null) =>
        LlmAnswer = (system, _) => system.Contains("You extract") ? extraction : matchAnswer ?? """{"match_id":null}""";

    [Fact]
    public async Task A_clear_future_event_goes_straight_onto_the_calendar()
    {
        Answer(Extract(EventJson("Science Quiz", FutureDate(5))));
        await Handle();

        var added = Assert.Single(Server.Events.Values);
        Assert.Equal("Science Quiz", added.Summary);
        Assert.Contains("✅ Science Quiz,", Assert.Single(Sent));
        Assert.Single(Llm.Calls); // nothing nearby, so no similarity call
    }

    [Fact]
    public async Task The_extraction_prompt_gets_todays_date_and_the_text_after_any_attachment()
    {
        Answer(Extract());
        await Handle("hello", new ImagePart(new byte[] { 1 }, MimeTypes.Jpeg));

        var (system, parts) = Llm.Calls[0];
        Assert.Contains(LlmUtil.Today(), system);
        Assert.IsType<ImagePart>(parts[0]);
        Assert.Equal("hello", ((TextPart)parts[1]).Text);
    }

    [Fact]
    public async Task Missing_text_is_replaced_by_a_placeholder()
    {
        Answer(Extract());
        await Handle(null, new PdfPart(new byte[] { 1 }));
        Assert.Equal("(no text, see attachment)", ((TextPart)Llm.Calls[0].Parts[^1]).Text);
    }

    [Fact]
    public async Task The_raw_model_answer_is_saved_against_the_update()
    {
        Answer("Sure! " + Extract());
        await Handle();
        Assert.Equal(Extract(), Table(Tables.Messages).Find(Tables.MessagesPartition, "u1")![Columns.ResultJson]);
    }

    [Fact]
    public async Task An_event_with_no_date_is_reported()
    {
        Answer(Extract(EventJson("Mystery", "null")));
        await Handle();
        Assert.Equal("⚠️ No date: Mystery", Assert.Single(Sent));
        Assert.Empty(Server.Events);
    }

    [Fact]
    public async Task Unreadable_model_output_is_reported()
    {
        Answer("I could not do that");
        await Handle();
        Assert.Equal("⚠️ Couldn't read the AI result.", Assert.Single(Sent));
    }

    [Fact]
    public async Task No_events_and_a_note_are_reported()
    {
        Answer("""{"events":[],"note":"blurry image"}""");
        await Handle();
        Assert.Equal("ℹ️ blurry image", Assert.Single(Sent));
    }

    [Fact]
    public async Task No_events_and_no_note_says_nothing_was_found()
    {
        Answer(Extract());
        await Handle();
        Assert.Equal("ℹ️ No dated events found.", Assert.Single(Sent));
    }

    [Fact]
    public async Task Low_confidence_is_confirmed_first_with_add_and_skip_buttons()
    {
        Answer(Extract(EventJson("Maybe Quiz", FutureDate(5), confidence: "low")));
        await Handle();

        Assert.Empty(Server.Events);
        Assert.StartsWith("❓ Not sure about this one. Add it?\nMaybe Quiz,", Sent[0]);
        Assert.Equal(new[] { "n:", "s:" }, ButtonsOfSent(0).Select(b => b[..2]));
        Assert.Single(Table(Tables.Pending).Rows);
    }

    [Fact]
    public async Task Low_confidence_is_added_directly_when_confirmation_is_switched_off()
    {
        SetEnv(Settings.ConfirmLowConfidence, "false");
        Answer(Extract(EventJson("Maybe Quiz", FutureDate(5), confidence: "low")));
        await Handle();
        Assert.Single(Server.Events);
    }

    [Fact]
    public async Task A_date_in_the_past_counts_as_low_confidence()
    {
        Answer(Extract(EventJson("Old Quiz", FutureDate(-30))));
        await Handle();
        Assert.Empty(Server.Events);
        Assert.StartsWith("❓ Not sure", Sent[0]);
    }

    [Fact]
    public async Task A_similar_event_on_the_calendar_asks_whether_to_update_it()
    {
        var existing = Server.Add(new Google.Apis.Calendar.v3.Data.Event
        {
            Summary = "Science Quiz",
            Start = new() { Date = FutureDate(6) },
            End = new() { Date = FutureDate(7) }
        });
        Answer(Extract(EventJson("Science Quiz", FutureDate(5))), $$"""{"match_id":"{{existing.Id}}"}""");
        await Handle();

        Assert.Single(Server.Events); // nothing added yet
        Assert.StartsWith("❓ Similar event on calendar:\n• Science Quiz,", Sent[0]);
        Assert.Contains("New: Science Quiz,", Sent[0]);
        Assert.Equal(new[] { "u:", "n:", "s:" }, ButtonsOfSent(0).Select(b => b[..2]));
        var (_, proposal) = await PendingStore.ClaimAsync(ProposalIdOfSent(0));
        Assert.Equal(existing.Id, proposal!.MatchId);
        Assert.Contains("NEW: quiz |", Llm.Calls[1].Parts.OfType<TextPart>().Single().Text);
    }

    [Theory]
    [InlineData("""{"match_id":"made-up"}""")]
    [InlineData("""{"match_id":null}""")]
    [InlineData("""{"match_id":5}""")]
    [InlineData("""{}""")]
    [InlineData("not json at all")]
    public async Task An_unusable_similarity_answer_means_no_match_so_the_event_is_added(string matchAnswer)
    {
        Server.Add(new Google.Apis.Calendar.v3.Data.Event { Summary = "Other", Start = new() { Date = FutureDate(6) }, End = new() { Date = FutureDate(7) } });
        Answer(Extract(EventJson("Science Quiz", FutureDate(5))), matchAnswer);
        await Handle();
        Assert.Equal(2, Server.Events.Count);
    }

    [Fact]
    public async Task One_failing_event_does_not_stop_the_others()
    {
        Answer(Extract(EventJson("Quiz A", FutureDate(5)), EventJson("Quiz B", FutureDate(6))));
        var calls = 0;
        Server.OnRequest = r => { if (r.Method == HttpMethod.Post && ++calls == 1) Server.FailWith = System.Net.HttpStatusCode.Forbidden; else Server.FailWith = null; };
        await Handle();

        var reply = Assert.Single(Sent);
        Assert.Contains("❌ Quiz A:", reply);
        Assert.Contains("✅ Quiz B,", reply);
    }

    [Fact]
    public async Task A_single_important_event_in_a_short_message_copies_the_message_into_the_description()
    {
        Answer(Extract(EventJson("Science Quiz", FutureDate(5))));
        await Handle("  Science quiz on Friday, chapters 1-2  ");
        Assert.Contains("Announcement:\nScience quiz on Friday, chapters 1-2", Server.Events.Values.Single().Description);
    }

    [Fact]
    public async Task The_message_is_not_copied_when_it_is_long_or_there_are_several_important_events_or_none()
    {
        Answer(Extract(EventJson("Science Quiz", FutureDate(5), extra: ",\"body\":\"from model\"")));
        await Handle(new string('x', 1600));
        Assert.Contains("from model", Server.Events.Values.Single().Description);

        Server.Events.Clear();
        Answer(Extract(EventJson("Quiz A", FutureDate(5), extra: ",\"body\":\"model A\""), EventJson("Quiz B", FutureDate(6), extra: ",\"body\":\"model B\"")));
        await Handle("short");
        Assert.All(Server.Events.Values, e => Assert.DoesNotContain("short", e.Description));

        Server.Events.Clear();
        Answer(Extract(EventJson("Picnic", FutureDate(5), type: "event")));
        await Handle("short");
        Assert.DoesNotContain("Announcement", Server.Events.Values.Single().Description);
    }

    [Fact]
    public async Task An_attachment_only_message_never_copies_the_placeholder()
    {
        Answer(Extract(EventJson("Science Quiz", FutureDate(5), extra: ",\"body\":\"transcribed\"")));
        await Handle(null, new ImagePart(new byte[] { 1 }, MimeTypes.Jpeg));
        Assert.Contains("transcribed", Server.Events.Values.Single().Description);
    }
}
