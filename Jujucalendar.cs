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
    static readonly TimeSpan Manila = TimeSpan.FromHours(8); // Asia/Manila has no DST
    const string Tz = "Asia/Manila";
    const string PrevMarker = "— Previous details —";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly Lazy<GCal.CalendarService> _svc = new(CreateService);
    static string CalId => Env.Get("JUJU_CALENDAR_ID");

    static GCal.CalendarService CreateService()
    {
        // GOOGLE_SA_JSON is the service account key file: raw JSON, or the same thing base64-encoded.
        var raw = Env.Get("GOOGLE_SA_JSON").Trim();
        var json = raw.StartsWith('{') ? raw : Encoding.UTF8.GetString(Convert.FromBase64String(raw));
#pragma warning disable CS0618
        var cred = GoogleCredential.FromJson(json).CreateScoped("https://www.googleapis.com/auth/calendar");
#pragma warning restore CS0618
        return new GCal.CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = cred,
            ApplicationName = "SchoolNotifs"
        });
    }

    // ---------- date helpers ----------
    public static DateOnly TodayManila => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(Manila).DateTime);

    public static bool TryDate(string? s, out DateOnly d) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", Inv, DateTimeStyles.None, out d);

    static bool TryTime(string? s, out TimeOnly t) =>
        TimeOnly.TryParseExact(s, "HH:mm", Inv, DateTimeStyles.None, out t);

    static DateTimeOffset At(DateOnly d, TimeOnly t) => new(d.ToDateTime(t), Manila);

    static string Fmt(TimeOnly t) => t.ToString("h:mm tt", Inv);

    // ---------- friendly text for Telegram ----------
    public static string When(ExtractedEvent e)
    {
        if (!TryDate(e.Date, out var d)) return "no date";
        var s = d.ToString("ddd MMM d", Inv);
        if (!e.IsBirthday && TryDate(e.EndDate, out var ed) && ed > d)
            return s + " – " + ed.ToString("ddd MMM d", Inv);
        if (TryTime(e.StartTime, out var t)) s += " " + Fmt(t);
        return s;
    }

    /// <summary>Existing calendar event: single day, time, or all-day range.</summary>
    public static string WhenEvent(GEvent g)
    {
        if (g.Start?.Date != null && g.End?.Date != null
            && DateOnly.TryParseExact(g.Start.Date, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var first)
            && DateOnly.TryParseExact(g.End.Date, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var endExclusive))
        {
            var last = endExclusive.AddDays(-1); // all-day end dates are exclusive
            if (last > first)
                return first.ToString("ddd MMM d", Inv) + " – " + last.ToString("ddd MMM d", Inv);
        }
        return When(g.Start);
    }

    public static string When(GEventDateTime? t)
    {
        if (t == null) return "?";
        if (t.Date != null && DateOnly.TryParseExact(t.Date, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d))
            return d.ToString("ddd MMM d", Inv);
        if (t.DateTimeDateTimeOffset is DateTimeOffset o)
            return o.ToOffset(Manila).ToString("ddd MMM d h:mm tt", Inv);
        return t.DateTimeRaw ?? "?";
    }

    /// <summary>One compact line per existing event, used when asking the LLM about similarity.</summary>
    public static string Line(GEvent g)
    {
        var desc = (g.Description ?? "").Replace("\n", " ");
        if (desc.Length > 80) desc = desc[..80];
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
            if (TryTime(e.EndTime, out var et)) t += " – " + Fmt(et);
            lines.Add($"Time: {t}");
        }
        if (e.Topics.Count > 0) lines.Add("Topics: " + string.Join("; ", e.Topics));
        if (e.Pages.Count > 0) lines.Add("Pages: " + string.Join("; ", e.Pages));
        if (e.References.Count > 0) lines.Add("References: " + string.Join("; ", e.References));
        if (!string.IsNullOrWhiteSpace(e.Details)) lines.Add(e.Details!);
        if (e.IsAssessment && !string.IsNullOrWhiteSpace(e.Body))
        {
            var body = e.Body!.Trim();
            if (body.Length > 1500) body = body[..1500] + "…";
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
            var end = TryTime(e.EndTime, out var et) && et > st ? At(d, et) : start.AddHours(1);
            ev.Start = new GEventDateTime { DateTimeDateTimeOffset = start, TimeZone = Tz };
            ev.End = new GEventDateTime { DateTimeDateTimeOffset = end, TimeZone = Tz };
        }
        else
        {
            var last = !e.IsBirthday && TryDate(e.EndDate, out var ed) && ed > d ? ed : d;
            ev.Start = new GEventDateTime { Date = d.ToString("yyyy-MM-dd", Inv) };
            ev.End = new GEventDateTime { Date = last.AddDays(1).ToString("yyyy-MM-dd", Inv) }; // end is exclusive
        }

        if (e.IsBirthday) ev.Recurrence = new List<string> { "RRULE:FREQ=YEARLY" };
        return ev;
    }

    // ---------- calendar calls ----------
    /// <summary>Events within +/- days of the given date (recurring events come back as one master entry).</summary>
    public async Task<IList<GEvent>> NearbyAsync(DateOnly d, int days = 7, CancellationToken ct = default)
    {
        var req = _svc.Value.Events.List(CalId);
        req.TimeMinDateTimeOffset = At(d.AddDays(-days), TimeOnly.MinValue);
        req.TimeMaxDateTimeOffset = At(d.AddDays(days + 1), TimeOnly.MinValue);
        req.SingleEvents = false;
        req.MaxResults = 50;
        var res = await req.ExecuteAsync(ct);
        return res.Items ?? new List<GEvent>();
    }

    /// <summary>Events overlapping the date range (recurring events come back as one master entry).</summary>
    public async Task<IList<GEvent>> ListRangeAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var req = _svc.Value.Events.List(CalId);
        req.TimeMinDateTimeOffset = At(from, TimeOnly.MinValue);
        req.TimeMaxDateTimeOffset = At(to.AddDays(1), TimeOnly.MinValue);
        req.SingleEvents = false;
        req.MaxResults = 250;
        var res = await req.ExecuteAsync(ct);
        return res.Items ?? new List<GEvent>();
    }

    public static DateOnly? StartDay(GEvent g)
    {
        var t = g.Start;
        if (t == null) return null;
        if (t.Date != null && DateOnly.TryParseExact(t.Date, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d)) return d;
        if (t.DateTimeDateTimeOffset is DateTimeOffset o) return DateOnly.FromDateTime(o.ToOffset(Manila).DateTime);
        return null;
    }

    public static DateOnly? EndDay(GEvent g)
    {
        var s = StartDay(g);
        if (g.End?.Date != null && DateOnly.TryParseExact(g.End.Date, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var e))
        {
            var last = e.AddDays(-1); // all-day end dates are exclusive
            return s is DateOnly sd && last < sd ? sd : last;
        }
        return s;
    }

    public Task<GEvent> InsertAsync(GEvent ev, CancellationToken ct = default) =>
        _svc.Value.Events.Insert(ev, CalId).ExecuteAsync(ct);

    static bool IsGone(Exception ex) =>
        ex is Google.GoogleApiException g && (g.HttpStatusCode == HttpStatusCode.NotFound || g.HttpStatusCode == HttpStatusCode.Gone);

    /// <summary>The event, or null if it was deleted from the calendar.</summary>
    public async Task<GEvent?> TryGetAsync(string id, CancellationToken ct = default)
    {
        try
        {
            var g = await _svc.Value.Events.Get(CalId, id).ExecuteAsync(ct);
            return g.Status == "cancelled" ? null : g;
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
        return d.Split("— School tracker sheet")[0].Trim();
    }

    // Get + Update (not Patch) so switching between all-day and timed can't leave both date and dateTime set.
    public async Task<GEvent> UpdateAsync(string id, GEvent fresh, bool keepPrevious = true, CancellationToken ct = default)
    {
        var svc = _svc.Value;
        var cur = await svc.Events.Get(CalId, id).ExecuteAsync(ct);
        if (cur.Status == "cancelled") throw new KeyNotFoundException("The calendar event was deleted");
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