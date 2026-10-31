using System.Net;
using System.Text;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using GCal = Google.Apis.Calendar.v3;
using GEvent = Google.Apis.Calendar.v3.Data.Event;

namespace SchoolAssistant;

/// <summary>Reads and writes the Juju school calendar using a Google service account.</summary>
public class JujuCalendar
{
    const string PreviousDetailsMarker = "— Previous details —";
    const string CalendarScope = "https://www.googleapis.com/auth/calendar";
    const string GoogleAppName = "SchoolNotifs";
    const string CancelledStatus = "cancelled";

    const int DefaultNearbyDays = 7;
    const int NearbyMaxResults = 50;
    const int RangeMaxResults = 250;

    readonly Lazy<GCal.CalendarService> _service = new(CreateService);
    static string CalendarId => Env.Get(Settings.CalendarId);

    static GCal.CalendarService CreateService()
    {
        // GOOGLE_SA_JSON is the service account key file: raw JSON, or the same thing base64-encoded.
        var raw = Env.Get(Settings.GoogleServiceAccountJson).Trim();
        var json = raw.StartsWith('{') ? raw : Encoding.UTF8.GetString(Convert.FromBase64String(raw));
#pragma warning disable CS0618
        var credential = GoogleCredential.FromJson(json).CreateScoped(CalendarScope);
#pragma warning restore CS0618
        return new GCal.CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = GoogleAppName
        });
    }

    // ---------- reading ----------
    /// <summary>Events within a week either side of the date (recurring events come back as one master entry).</summary>
    public Task<IList<GEvent>> NearbyAsync(DateOnly date) =>
        ListAsync(date.AddDays(-DefaultNearbyDays), date.AddDays(DefaultNearbyDays), NearbyMaxResults);

    /// <summary>Events overlapping the date range (recurring events come back as one master entry).</summary>
    public Task<IList<GEvent>> ListRangeAsync(DateOnly first, DateOnly last) =>
        ListAsync(first, last, RangeMaxResults);

    // Inclusive day range: the query ends at midnight after the last day.
    async Task<IList<GEvent>> ListAsync(DateOnly first, DateOnly last, int maxResults)
    {
        var request = _service.Value.Events.List(CalendarId);
        request.TimeMinDateTimeOffset = CalendarEventBuilder.At(first, TimeOnly.MinValue);
        request.TimeMaxDateTimeOffset = CalendarEventBuilder.At(last.AddDays(CalendarConventions.AllDayEndOffsetDays), TimeOnly.MinValue);
        request.SingleEvents = false;
        request.MaxResults = maxResults;
        var response = await request.ExecuteAsync();
        return response.Items ?? new List<GEvent>();
    }

    /// <summary>The event, or null if it was deleted from the calendar.</summary>
    public async Task<GEvent?> TryGetAsync(string id)
    {
        try
        {
            var calendarEvent = await _service.Value.Events.Get(CalendarId, id).ExecuteAsync();
            return calendarEvent.Status == CancelledStatus ? null : calendarEvent;
        }
        catch (Exception ex) when (IsGone(ex)) { return null; }
    }

    public static DateOnly? StartDay(GEvent g)
    {
        if (g.Start == null) return null;
        if (DateText.TryDate(g.Start.Date, out var day)) return day;
        if (g.Start.DateTimeDateTimeOffset is DateTimeOffset moment)
            return DateOnly.FromDateTime(moment.ToOffset(ManilaTime.Offset).DateTime);
        return null;
    }

    public static DateOnly? EndDay(GEvent g)
    {
        var start = StartDay(g);
        if (!DateText.TryDate(g.End?.Date, out var endExclusive)) return start;

        var last = endExclusive.AddDays(-CalendarConventions.AllDayEndOffsetDays);
        return start is DateOnly s && last < s ? s : last;
    }

    /// <summary>The sheet cell text we stored in the event description (without the footer or old details).</summary>
    public static string SheetText(GEvent g)
    {
        var description = (g.Description ?? "").Split(PreviousDetailsMarker)[0];
        return description.Split(SheetSync.SourceFooter)[0].Trim();
    }

    // ---------- writing ----------
    public Task<GEvent> InsertAsync(GEvent calendarEvent) =>
        _service.Value.Events.Insert(calendarEvent, CalendarId).ExecuteAsync();

    /// <summary>Deletes the event; already gone counts as success.</summary>
    public async Task DeleteAsync(string id)
    {
        try { await _service.Value.Events.Delete(CalendarId, id).ExecuteAsync(); }
        catch (Exception ex) when (IsGone(ex)) { }
    }

    // Get + Update (not Patch) so switching between all-day and timed can't leave both date and dateTime set.
    public async Task<GEvent> UpdateAsync(string id, GEvent fresh, bool keepPreviousDetails)
    {
        var service = _service.Value;
        var current = await service.Events.Get(CalendarId, id).ExecuteAsync();
        if (current.Status == CancelledStatus) throw new KeyNotFoundException("The calendar event was deleted");

        current.Summary = fresh.Summary;
        current.Start = fresh.Start;
        current.End = fresh.End;
        if (!string.IsNullOrWhiteSpace(fresh.Location)) current.Location = fresh.Location;
        if (fresh.Recurrence != null) current.Recurrence = fresh.Recurrence;
        current.Description = keepPreviousDetails
            ? MergeDescription(fresh.Description, current.Description)
            : fresh.Description ?? current.Description;
        return await service.Events.Update(current, CalendarId, id).ExecuteAsync();
    }

    /// <summary>True when the Calendar API says the event no longer exists.</summary>
    public static bool IsGone(Exception ex) =>
        ex is Google.GoogleApiException g && g.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone;

    // Keeps the old text under a marker so nothing is lost; only one level deep.
    static string? MergeDescription(string? fresh, string? old)
    {
        old = old?.Split(PreviousDetailsMarker)[0].Trim();
        if (string.IsNullOrWhiteSpace(old) || old == fresh?.Trim()) return fresh;
        if (string.IsNullOrWhiteSpace(fresh)) return old;
        return $"{fresh}\n\n{PreviousDetailsMarker}\n{old}";
    }
}
