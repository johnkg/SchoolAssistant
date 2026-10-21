using System.Globalization;
using System.Net;
using System.Text;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using GCal = Google.Apis.Calendar.v3;
using GEvent = Google.Apis.Calendar.v3.Data.Event;
using GEventDateTime = Google.Apis.Calendar.v3.Data.EventDateTime;

namespace SchoolAssistant;

/// <summary>Reads and writes the Juju school calendar using a Google service account.</summary>
public class JujuCalendar
{
    const string PrevMarker = "— Previous details —";
    const string CalendarScope = "https://www.googleapis.com/auth/calendar";
    const string GoogleAppName = "SchoolNotifs";
    const string CancelledStatus = "cancelled";
    const string YearlyRule = "RRULE:FREQ=YEARLY";

    const int DefaultNearbyDays = 7;
    const int NearbyMaxResults = 50;
    const int RangeMaxResults = 250;
    const int DefaultDurationHours = 1;        // timed event with a start but no (valid) end
    const int LineDescriptionMaxChars = 80;    // per existing event, in the similarity prompt
    const int AnnouncementMaxChars = 1500;     // announcement text copied into the description
    const int AllDayEndOffsetDays = 1;         // all-day end dates are exclusive

    static readonly TimeSpan Manila = ManilaTime.Offset;
    static readonly CultureInfo Inv = Formats.Inv;

    readonly Lazy<GCal.CalendarService> _svc = new(CreateService);
    static string CalId => Env.Get(Settings.CalendarId);

    static GCal.CalendarService CreateService()
    {
        // GOOGLE_SA_JSON is the service account key file: raw JSON, or the same thing base64-encoded.
        var raw = Env.Get(Settings.GoogleServiceAccountJson).Trim();
        var json = raw.StartsWith('{') ? raw : Encoding.UTF8.GetString(Convert.FromBase64String(raw));
#pragma warning disable CS0618
        var cred = GoogleCredential.FromJson(json).CreateScoped(CalendarScope);
#pragma warning restore CS0618
        return new GCal.CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = cred,
            ApplicationName = GoogleAppName
        });
    }

    // ---------- date helpers ----------
    public static DateOnly TodayManila => ManilaTime.Today;

    public static bool TryDate(string? s, out DateOnly d) =>
        DateOnly.TryParseExact(s, Formats.IsoDate, Inv, DateTimeStyles.None, out d);

    static bool TryTime(string? s, out TimeOnly t) =>
        TimeOnly.TryParseExact(s, Formats.Time24, Inv, DateTimeStyles.None, out t);

    static DateTimeOffset At(DateOnly d, TimeOnly t) => new(d.ToDateTime(t), Manila);

    static string Fmt(TimeOnly t) => t.ToString(Formats.Time12, Inv);

    static string Day(DateOnly d) => d.ToString(Formats.ShortDay, Inv);

    static string Iso(DateOnly d) => d.ToString(Formats.IsoDate, Inv);

    // ---------- friendly text for Telegram ----------
    public static string When(ExtractedEvent e)
    {
        if (!TryDate(e.Date, out var d)) return "no date";
        var s = Day(d);
        if (!e.IsBirthday && TryDate(e.EndDate, out var ed) && ed > d)
            return s + Formats.RangeSeparator + Day(ed);
        if (TryTime(e.StartTime, out var t)) s += " " + Fmt(t);
        return s;
    }

    /// <summary>Existing calendar event: single day, time, or all-day range.</summary>
    public static string WhenEvent(GEvent g)
    {
        if (TryDate(g.Start?.Date, out var first) && TryDate(g.End?.Date, out var endExclusive))
        {
            var last = endExclusive.AddDays(-AllDayEndOffsetDays);
            if (last > first)
                return Day(first) + Formats.RangeSeparator + Day(last);
        }
        return When(g.Start);
    }

    public static string When(GEventDateTime? t)
    {
        if (t == null) return "?";
        if (TryDate(t.Date, out var d))
            return Day(d);
        if (t.DateTimeDateTimeOffset is DateTimeOffset o)
            return o.ToOffset(Manila).ToString(Formats.ShortDayTime, Inv);
        return t.DateTimeRaw ?? "?";
    }

    /// <summary>One compact line per existing event, used when asking the LLM about similarity.</summary>
    public static string Line(GEvent g)
    {
        var desc = (g.Description ?? "").Replace("\n", " ");
        if (desc.Length > LineDescriptionMaxChars) desc = desc[..LineDescriptionMaxChars];
        var yearly = g.Recurrence is { Count: > 0 } ? " (repeats)" : "";
        return $"{g.Id} | {WhenEvent(g)}{yearly} | {g.Summary} | {desc}";
    }

    // ---------- building events ----------
    public static string Describe(ExtractedEvent e)
    {
        if (!string.IsNullOrWhiteSpace(e.Description)) return e.Description!;

        var lines = new List<string>();
        if (TryTime(e.StartTime, out var st))
        {
            var t = Fmt(st);
            if (TryTime(e.EndTime, out var et)) t += Formats.RangeSeparator + Fmt(et);
            lines.Add($"Time: {t}");
        }
        if (e.Topics.Count > 0) lines.Add("Topics: " + string.Join("; ", e.Topics));
        if (e.Pages.Count > 0) lines.Add("Pages: " + string.Join("; ", e.Pages));
        if (e.References.Count > 0) lines.Add("References: " + string.Join("; ", e.References));
        if (!string.IsNullOrWhiteSpace(e.Details)) lines.Add(e.Details!);
        if (e.IsAssessment && !string.IsNullOrWhiteSpace(e.Body))
        {
            var body = TextUtil.Truncate(e.Body!.Trim(), AnnouncementMaxChars);
            lines.Add("");
            lines.Add("Announcement:");
            lines.Add(body);
        }
        return string.Join("\n", lines);
    }

    public GEvent Build(ExtractedEvent e)
    {
        if (!TryDate(e.Date, out var d)) throw new InvalidOperationException("Event has no valid date");

        TimeOnly st = default;
        var timed = !e.IsBirthday
                    && TryTime(e.StartTime, out st)
                    && (e.EndDate is null || e.EndDate == e.Date);

        var ev = new GEvent
        {
            Summary = e.Title,
            Description = Describe(e),
            Location = e.Location
        };

        if (timed)
        {
            var start = At(d, st);
            var end = TryTime(e.EndTime, out var et) && et > st ? At(d, et) : start.AddHours(DefaultDurationHours);
            ev.Start = new GEventDateTime { DateTimeDateTimeOffset = start, TimeZone = ManilaTime.TimeZoneId };
            ev.End = new GEventDateTime { DateTimeDateTimeOffset = end, TimeZone = ManilaTime.TimeZoneId };
        }
        else
        {
            var last = !e.IsBirthday && TryDate(e.EndDate, out var ed) && ed > d ? ed : d;
            ev.Start = new GEventDateTime { Date = Iso(d) };
            ev.End = new GEventDateTime { Date = Iso(last.AddDays(AllDayEndOffsetDays)) };
        }

        if (e.IsBirthday) ev.Recurrence = new List<string> { YearlyRule };
        return ev;
    }

    // ---------- calendar calls ----------
    /// <summary>Events within +/- days of the given date (recurring events come back as one master entry).</summary>
    public Task<IList<GEvent>> NearbyAsync(DateOnly d, int days = DefaultNearbyDays, CancellationToken ct = default) =>
        ListAsync(d.AddDays(-days), d.AddDays(days), NearbyMaxResults, ct);

    /// <summary>Events overlapping the date range (recurring events come back as one master entry).</summary>
    public Task<IList<GEvent>> ListRangeAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        ListAsync(from, to, RangeMaxResults, ct);

    // Inclusive day range: the query ends at midnight after the last day.
    async Task<IList<GEvent>> ListAsync(DateOnly first, DateOnly last, int maxResults, CancellationToken ct)
    {
        var req = _svc.Value.Events.List(CalId);
        req.TimeMinDateTimeOffset = At(first, TimeOnly.MinValue);
        req.TimeMaxDateTimeOffset = At(last.AddDays(AllDayEndOffsetDays), TimeOnly.MinValue);
        req.SingleEvents = false;
        req.MaxResults = maxResults;
        var res = await req.ExecuteAsync(ct);
        return res.Items ?? new List<GEvent>();
    }

    public static DateOnly? StartDay(GEvent g)
    {
        var t = g.Start;
        if (t == null) return null;
        if (TryDate(t.Date, out var d)) return d;
        if (t.DateTimeDateTimeOffset is DateTimeOffset o) return DateOnly.FromDateTime(o.ToOffset(Manila).DateTime);
        return null;
    }

    public static DateOnly? EndDay(GEvent g)
    {
        var s = StartDay(g);
        if (TryDate(g.End?.Date, out var e))
        {
            var last = e.AddDays(-AllDayEndOffsetDays);
            return s is DateOnly sd && last < sd ? sd : last;
        }
        return s;
    }

    public Task<GEvent> InsertAsync(GEvent ev, CancellationToken ct = default) =>
        _svc.Value.Events.Insert(ev, CalId).ExecuteAsync(ct);

    /// <summary>True when the Calendar API says the event no longer exists.</summary>
    public static bool IsGone(Exception ex) =>
        ex is Google.GoogleApiException g && (g.HttpStatusCode == HttpStatusCode.NotFound || g.HttpStatusCode == HttpStatusCode.Gone);

    /// <summary>The event, or null if it was deleted from the calendar.</summary>
    public async Task<GEvent?> TryGetAsync(string id, CancellationToken ct = default)
    {
        try
        {
            var g = await _svc.Value.Events.Get(CalId, id).ExecuteAsync(ct);
            return g.Status == CancelledStatus ? null : g;
        }
        catch (Exception ex) when (IsGone(ex)) { return null; }
    }

    /// <summary>Deletes the event; already gone counts as success.</summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        try { await _svc.Value.Events.Delete(CalId, id).ExecuteAsync(ct); }
        catch (Exception ex) when (IsGone(ex)) { }
    }

    /// <summary>The sheet cell text we stored in the event description (without the footer or old details).</summary>
    public static string SheetText(GEvent g)
    {
        var d = (g.Description ?? "").Split(PrevMarker)[0];
        return d.Split(SheetSync.SourceFooter)[0].Trim();
    }

    // Get + Update (not Patch) so switching between all-day and timed can't leave both date and dateTime set.
    public async Task<GEvent> UpdateAsync(string id, GEvent fresh, bool keepPrevious = true, CancellationToken ct = default)
    {
        var svc = _svc.Value;
        var cur = await svc.Events.Get(CalId, id).ExecuteAsync(ct);
        if (cur.Status == CancelledStatus) throw new KeyNotFoundException("The calendar event was deleted");
        cur.Summary = fresh.Summary;
        cur.Start = fresh.Start;
        cur.End = fresh.End;
        if (!string.IsNullOrWhiteSpace(fresh.Location)) cur.Location = fresh.Location;
        if (fresh.Recurrence != null) cur.Recurrence = fresh.Recurrence;
        cur.Description = keepPrevious ? MergeDescription(fresh.Description, cur.Description) : fresh.Description ?? cur.Description;
        return await svc.Events.Update(cur, CalId, id).ExecuteAsync(ct);
    }

    // Keeps the old text under a marker so nothing is lost; only one level deep.
    static string? MergeDescription(string? fresh, string? old)
    {
        old = old?.Split(PrevMarker)[0].Trim();
        if (string.IsNullOrWhiteSpace(old) || old == fresh?.Trim()) return fresh;
        if (string.IsNullOrWhiteSpace(fresh)) return old;
        return $"{fresh}\n\n{PrevMarker}\n{old}";
    }
}