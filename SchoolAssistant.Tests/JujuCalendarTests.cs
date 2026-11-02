using System.Net;
using System.Security.Cryptography;
using System.Text;
using Google;
using Google.Apis.Calendar.v3.Data;
using Xunit;

namespace SchoolAssistant.Tests;

public class JujuCalendarTests : AppTestBase
{
    readonly FakeCalendarServer _server = new();
    readonly JujuCalendar _calendar;

    public JujuCalendarTests() => _calendar = new JujuCalendar(_server.Service);

    static Event AllDay(string title, string first = "2026-10-09", string endExclusive = "2026-10-10", string? description = null) => new()
    {
        Summary = title, Description = description,
        Start = new EventDateTime { Date = first }, End = new EventDateTime { Date = endExclusive }
    };

    [Fact]
    public async Task Nearby_lists_events_within_a_week_of_the_date()
    {
        _server.Add(AllDay("Quiz"));
        var events = await _calendar.NearbyAsync(new DateOnly(2026, 10, 9));

        Assert.Equal("Quiz", Assert.Single(events).Summary);
        var query = Uri.UnescapeDataString(_server.Handler.Requests[0].Uri.Query);
        Assert.Contains("timeMin=2026-10-02T00:00:00+08", query);
        Assert.Contains("timeMax=2026-10-17T00:00:00+08", query); // midnight after the last day
        Assert.Contains("singleEvents=false", query);
        Assert.Contains("maxResults=50", query);
        Assert.Contains("/calendars/cal-id/events", _server.Handler.Requests[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task ListRange_asks_for_up_to_250_events()
    {
        await _calendar.ListRangeAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Assert.DoesNotContain("maxResults=50", _server.Handler.Requests[0].Uri.Query); // 250 is the API default, so it is not sent
    }

    [Fact]
    public async Task A_calendar_with_no_items_gives_an_empty_list()
    {
        _server.ReturnNullItems = true;
        Assert.Empty(await _calendar.ListRangeAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2)));
    }

    [Fact]
    public async Task TryGet_returns_a_live_event()
    {
        var added = _server.Add(AllDay("Quiz"));
        Assert.Equal("Quiz", (await _calendar.TryGetAsync(added.Id))!.Summary);
    }

    [Fact] public async Task TryGet_returns_null_for_a_missing_event() => Assert.Null(await _calendar.TryGetAsync("nope"));

    [Fact]
    public async Task TryGet_returns_null_for_a_cancelled_event()
    {
        var added = _server.Add(AllDay("Quiz"));
        added.Status = "cancelled";
        Assert.Null(await _calendar.TryGetAsync(added.Id));
    }

    [Fact]
    public async Task TryGet_lets_other_errors_through()
    {
        _server.FailWith = HttpStatusCode.Forbidden;
        await Assert.ThrowsAsync<GoogleApiException>(() => _calendar.TryGetAsync("x"));
    }

    [Fact]
    public async Task Insert_saves_the_event_and_returns_it_with_an_id()
    {
        var created = await _calendar.InsertAsync(AllDay("Quiz"));
        Assert.Equal("Quiz", _server.Events[created.Id].Summary);
    }

    [Fact]
    public async Task Delete_removes_the_event()
    {
        var added = _server.Add(AllDay("Quiz"));
        await _calendar.DeleteAsync(added.Id);
        Assert.Empty(_server.Events);
    }

    [Fact] public async Task Delete_of_an_event_that_is_already_gone_is_fine() => await _calendar.DeleteAsync("missing");

    [Fact]
    public async Task Delete_lets_other_errors_through()
    {
        _server.FailWith = HttpStatusCode.Forbidden;
        await Assert.ThrowsAsync<GoogleApiException>(() => _calendar.DeleteAsync("x"));
    }

    [Fact]
    public async Task Update_replaces_summary_and_dates_and_keeps_old_details_under_a_marker()
    {
        var added = _server.Add(AllDay("Old title", description: "old text"));
        var fresh = AllDay("New title", "2026-10-12", "2026-10-13", "new text");

        await _calendar.UpdateAsync(added.Id, fresh, keepPreviousDetails: true);

        var saved = _server.Events[added.Id];
        Assert.Equal(("New title", "2026-10-12"), (saved.Summary, saved.Start.Date));
        Assert.Equal("new text\n\n— Previous details —\nold text", saved.Description);
    }

    [Fact]
    public async Task Update_without_keeping_details_uses_the_new_text_or_falls_back_to_the_old()
    {
        var first = _server.Add(AllDay("A", description: "old"));
        await _calendar.UpdateAsync(first.Id, AllDay("A", description: "new"), keepPreviousDetails: false);
        Assert.Equal("new", _server.Events[first.Id].Description);

        var second = _server.Add(AllDay("B", description: "old"));
        await _calendar.UpdateAsync(second.Id, AllDay("B"), keepPreviousDetails: false);
        Assert.Equal("old", _server.Events[second.Id].Description);
    }

    [Fact]
    public async Task Update_keeps_only_one_level_of_history_and_skips_identical_or_empty_text()
    {
        var withHistory = _server.Add(AllDay("A", description: "middle\n\n— Previous details —\nancient"));
        await _calendar.UpdateAsync(withHistory.Id, AllDay("A", description: "newest"), keepPreviousDetails: true);
        Assert.Equal("newest\n\n— Previous details —\nmiddle", _server.Events[withHistory.Id].Description);

        var same = _server.Add(AllDay("B", description: "same"));
        await _calendar.UpdateAsync(same.Id, AllDay("B", description: " same "), keepPreviousDetails: true);
        Assert.Equal(" same ", _server.Events[same.Id].Description);

        var noNew = _server.Add(AllDay("C", description: "only old"));
        await _calendar.UpdateAsync(noNew.Id, AllDay("C"), keepPreviousDetails: true);
        Assert.Equal("only old", _server.Events[noNew.Id].Description);

        var noOld = _server.Add(AllDay("D"));
        await _calendar.UpdateAsync(noOld.Id, AllDay("D", description: "fresh"), keepPreviousDetails: true);
        Assert.Equal("fresh", _server.Events[noOld.Id].Description);
    }

    [Fact]
    public async Task Update_keeps_location_and_recurrence_unless_the_new_event_has_them()
    {
        var added = _server.Add(AllDay("A"));
        added.Location = "Room 1";
        added.Recurrence = new[] { "RRULE:FREQ=YEARLY" };

        await _calendar.UpdateAsync(added.Id, AllDay("A"), keepPreviousDetails: true);
        Assert.Equal(("Room 1", "RRULE:FREQ=YEARLY"), (_server.Events[added.Id].Location, _server.Events[added.Id].Recurrence.Single()));

        var fresh = AllDay("A");
        fresh.Location = "Room 2";
        fresh.Recurrence = new[] { "RRULE:FREQ=DAILY" };
        await _calendar.UpdateAsync(added.Id, fresh, keepPreviousDetails: true);
        Assert.Equal(("Room 2", "RRULE:FREQ=DAILY"), (_server.Events[added.Id].Location, _server.Events[added.Id].Recurrence.Single()));
    }

    [Fact]
    public async Task Update_of_a_cancelled_event_says_it_was_deleted()
    {
        var added = _server.Add(AllDay("A"));
        added.Status = "cancelled";
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _calendar.UpdateAsync(added.Id, AllDay("A"), true));
    }

    [Fact]
    public async Task Update_of_a_missing_event_is_a_gone_error()
    {
        var ex = await Assert.ThrowsAsync<GoogleApiException>(() => _calendar.UpdateAsync("missing", AllDay("A"), true));
        Assert.True(JujuCalendar.IsGone(ex));
    }

    [Fact] public void The_public_constructor_is_lazy_and_needs_no_settings() => Assert.NotNull(new JujuCalendar());

    [Fact]
    public void Service_is_built_from_raw_service_account_json_or_base64()
    {
        var json = ServiceAccountJson();
        SetEnv(Settings.GoogleServiceAccountJson, json);
        Assert.NotNull(JujuCalendar.CreateService());

        SetEnv(Settings.GoogleServiceAccountJson, Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
        Assert.NotNull(JujuCalendar.CreateService());
    }

    static string ServiceAccountJson()
    {
        using var rsa = RSA.Create(2048);
        var key = rsa.ExportPkcs8PrivateKeyPem().Replace("\n", "\\n");
        return $$"""{"type":"service_account","project_id":"p","client_email":"bot@p.iam.gserviceaccount.com","private_key":"{{key}}","token_uri":"https://oauth2.googleapis.com/token"}""";
    }
}
