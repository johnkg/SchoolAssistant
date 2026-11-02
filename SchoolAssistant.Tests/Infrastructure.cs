using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Azure;
using Azure.Data.Tables;
using Google.Apis.Http;
using Google.Apis.Json;
using Google.Apis.Services;
using GCal = Google.Apis.Calendar.v3;
using GEvent = Google.Apis.Calendar.v3.Data.Event;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)] // tests share environment variables and Db.TableFactory

namespace SchoolAssistant.Tests;

/// <summary>In-memory stand-in for <see cref="TableClient"/>; supports what the stores use.</summary>
public sealed class FakeTable : TableClient
{
    readonly Dictionary<(string Pk, string Rk), TableEntity> _rows = new();
    int _etagCounter;

    /// <summary>Makes every update fail, to exercise error handling.</summary>
    public bool FailUpdates { get; set; }

    public IReadOnlyCollection<TableEntity> Rows => _rows.Values;

    public TableEntity? Find(string pk, string rk) => _rows.GetValueOrDefault((pk, rk));

    public override Response<Azure.Data.Tables.Models.TableItem> CreateIfNotExists(CancellationToken cancellationToken = default) => null!;

    public override Task<Response> AddEntityAsync<T>(T entity, CancellationToken cancellationToken = default)
    {
        if (_rows.ContainsKey((entity.PartitionKey, entity.RowKey))) throw new RequestFailedException((int)HttpStatusCode.Conflict, "exists");
        Store(entity, replace: true);
        return Task.FromResult<Response>(null!);
    }

    public override Task<Response<T>> GetEntityAsync<T>(
        string partitionKey, string rowKey, IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
    {
        if (!_rows.TryGetValue((partitionKey, rowKey), out var row)) throw new RequestFailedException((int)HttpStatusCode.NotFound, "missing");
        return Task.FromResult(Response.FromValue((T)(object)Copy(row), null!));
    }

    public override Task<Response> UpdateEntityAsync<T>(
        T entity, ETag ifMatch, TableUpdateMode mode = TableUpdateMode.Merge, CancellationToken cancellationToken = default)
    {
        if (FailUpdates) throw new RequestFailedException((int)HttpStatusCode.InternalServerError, "storage down");
        if (!_rows.TryGetValue((entity.PartitionKey, entity.RowKey), out var existing)) throw new RequestFailedException((int)HttpStatusCode.NotFound, "missing");
        if (ifMatch != ETag.All && ifMatch != existing.ETag) throw new RequestFailedException((int)HttpStatusCode.PreconditionFailed, "etag");
        Store(entity, replace: mode == TableUpdateMode.Replace);
        return Task.FromResult<Response>(null!);
    }

    public override Task<Response> UpsertEntityAsync<T>(T entity, TableUpdateMode mode = TableUpdateMode.Merge, CancellationToken cancellationToken = default)
    {
        Store(entity, replace: mode == TableUpdateMode.Replace || !_rows.ContainsKey((entity.PartitionKey, entity.RowKey)));
        return Task.FromResult<Response>(null!);
    }

    public override AsyncPageable<T> QueryAsync<T>(
        string? filter = null, int? maxPerPage = null, IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
    {
        var match = Regex.Match(filter ?? "", @"^PartitionKey eq '(?<pk>[^']*)'$");
        Assert.True(match.Success || filter is null, $"FakeTable only understands a PartitionKey filter, got: {filter}");
        var rows = _rows.Values
            .Where(r => filter is null || r.PartitionKey == match.Groups["pk"].Value)
            .Select(r => (T)(object)Copy(r)).ToList();
        return AsyncPageable<T>.FromPages(new[] { Page<T>.FromValues(rows, null, null!) });
    }

    void Store(ITableEntity entity, bool replace)
    {
        var key = (entity.PartitionKey, entity.RowKey);
        var merged = replace || !_rows.TryGetValue(key, out var existing) ? new TableEntity(key.PartitionKey, key.RowKey) : Copy(existing);
        foreach (var (name, value) in (TableEntity)entity)
            if (name is not ("PartitionKey" or "RowKey" or "odata.etag" or "Timestamp")) merged[name] = value;
        merged.ETag = new ETag($"\"{++_etagCounter}\"");
        _rows[key] = merged;
        if (entity is TableEntity live) live.ETag = merged.ETag;
    }

    static TableEntity Copy(TableEntity source)
    {
        var copy = new TableEntity(source.PartitionKey, source.RowKey) { ETag = source.ETag };
        foreach (var (name, value) in source)
            if (name is not ("PartitionKey" or "RowKey" or "odata.etag" or "Timestamp")) copy[name] = value;
        return copy;
    }
}

/// <summary>All fake tables by name, plus the environment every app test needs. Dispose restores both.</summary>
public abstract class AppTestBase : IDisposable
{
    readonly Dictionary<string, FakeTable> _tables = new();
    readonly Dictionary<string, string?> _savedEnv = new();

    public const long ChatId = 42;
    public const string Secret = "secret";

    protected AppTestBase()
    {
        Db.TableFactory = name => Table(name);
        foreach (var key in new[]
        {
            Settings.TelegramToken, Settings.TelegramSecret, Settings.TelegramChatId, Settings.DebugReplies,
            Settings.ConfirmLowConfidence, Settings.LlmProvider, Settings.OpenAiApiKey, Settings.OpenAiModel,
            Settings.AnthropicApiKey, Settings.AnthropicModel, Settings.GoogleServiceAccountJson, Settings.CalendarId,
            Settings.SheetId, Settings.SheetGids, Settings.TablesConnectionString, Settings.WebJobsStorage
        })
            _savedEnv[key] = Environment.GetEnvironmentVariable(key);

        SetEnv(Settings.TelegramToken, "bot-token");
        SetEnv(Settings.TelegramSecret, Secret);
        SetEnv(Settings.TelegramChatId, ChatId.ToString());
        SetEnv(Settings.CalendarId, "cal-id");
        SetEnv(Settings.OpenAiApiKey, "openai-key");
        SetEnv(Settings.OpenAiModel, "gpt-test");
        SetEnv(Settings.AnthropicApiKey, "anthropic-key");
        foreach (var key in new[] { Settings.DebugReplies, Settings.ConfirmLowConfidence, Settings.LlmProvider, Settings.AnthropicModel,
                     Settings.GoogleServiceAccountJson, Settings.SheetId, Settings.SheetGids, Settings.TablesConnectionString, Settings.WebJobsStorage })
            SetEnv(key, null);
    }

    public FakeTable Table(string name)
    {
        if (!_tables.TryGetValue(name, out var table)) _tables[name] = table = new FakeTable();
        return table;
    }

    protected static void SetEnv(string key, string? value) => Environment.SetEnvironmentVariable(key, value);

    public virtual void Dispose()
    {
        foreach (var (key, value) in _savedEnv) Environment.SetEnvironmentVariable(key, value);
        Db.TableFactory = null!;
        GC.SuppressFinalize(this);
    }
}

public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>Answers every request from a function and records what was sent.</summary>
public sealed class StubHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = new();

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, body, headers);
        Requests.Add(recorded);
        return respond(recorded);
    }
}

public sealed class StubHttpFactory(HttpMessageHandler handler) : System.Net.Http.IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>Scripted LLM: answers come from a function of (system prompt, user text); every call is recorded.</summary>
public sealed class FakeLlm(Func<string, string, string> answer) : ILlmClient
{
    public List<(string System, IReadOnlyList<LlmPart> Parts)> Calls { get; } = new();

    public FakeLlm(string fixedAnswer) : this((_, _) => fixedAnswer) { }

    public Task<string> AskAsync(string system, IReadOnlyList<LlmPart> parts)
    {
        Calls.Add((system, parts));
        var userText = string.Join("\n", parts.OfType<TextPart>().Select(p => p.Text));
        return Task.FromResult(answer(system, userText));
    }
}

/// <summary>A tiny in-memory Google Calendar REST server, reached through a real <see cref="GCal.CalendarService"/>.</summary>
public sealed class FakeCalendarServer
{
    public Dictionary<string, GEvent> Events { get; } = new();
    public List<string> Calls { get; } = new();
    public HttpStatusCode? FailWith { get; set; }
    public bool ReturnNullItems { get; set; }
    public Action<RecordedRequest>? OnRequest { get; set; }
    int _nextId;

    public StubHandler Handler { get; }
    public GCal.CalendarService Service { get; }

    public FakeCalendarServer()
    {
        Handler = new StubHandler(Respond);
        Service = new GCal.CalendarService(new BaseClientService.Initializer
        {
            HttpClientFactory = new GoogleFactory(Handler),
            ApplicationName = "tests",
            GZipEnabled = false
        });
    }

    public GEvent Add(GEvent calendarEvent)
    {
        calendarEvent.Id ??= $"ev{++_nextId}";
        Events[calendarEvent.Id] = calendarEvent;
        return calendarEvent;
    }

    static string Serialize(object value) => NewtonsoftJsonSerializer.Instance.Serialize(value);

    HttpResponseMessage Respond(RecordedRequest request)
    {
        Calls.Add($"{request.Method} {request.Uri.AbsolutePath}");
        OnRequest?.Invoke(request);
        if (FailWith is { } status) return Error(status);

        var match = Regex.Match(request.Uri.AbsolutePath, @"/events(/(?<id>[^/]+))?$");
        var id = match.Groups["id"].Success ? Uri.UnescapeDataString(match.Groups["id"].Value) : null;

        if (request.Method == HttpMethod.Get && id == null)
            return StubHandler.Json(Serialize(new GCal.Data.Events { Items = ReturnNullItems ? null : Events.Values.ToList() }));
        if (request.Method == HttpMethod.Get)
            return Events.TryGetValue(id!, out var found) ? StubHandler.Json(Serialize(found)) : Error(HttpStatusCode.NotFound);
        if (request.Method == HttpMethod.Post)
        {
            var created = NewtonsoftJsonSerializer.Instance.Deserialize<GEvent>(request.Body);
            created.Id = $"ev{++_nextId}";
            Events[created.Id] = created;
            return StubHandler.Json(Serialize(created));
        }
        if (request.Method == HttpMethod.Put)
        {
            if (!Events.ContainsKey(id!)) return Error(HttpStatusCode.NotFound);
            var updated = NewtonsoftJsonSerializer.Instance.Deserialize<GEvent>(request.Body);
            updated.Id = id;
            Events[id!] = updated;
            return StubHandler.Json(Serialize(updated));
        }
        if (request.Method == HttpMethod.Delete)
        {
            if (!Events.Remove(id!)) return Error(HttpStatusCode.Gone);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        return Error(HttpStatusCode.MethodNotAllowed);
    }

    static HttpResponseMessage Error(HttpStatusCode status) =>
        StubHandler.Json($"{{\"error\":{{\"code\":{(int)status},\"message\":\"{status}\"}}}}", status);

    sealed class GoogleFactory(HttpMessageHandler inner) : Google.Apis.Http.IHttpClientFactory
    {
        public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args) =>
            new(new ConfigurableMessageHandler(inner));
    }
}


/// <summary>Everything the orchestration classes need, wired to fakes. LLM answers come from <see cref="LlmAnswer"/>.</summary>
public class Harness : AppTestBase
{
    public StubHandler TelegramHandler { get; }

    /// <summary>Makes every Telegram call fail with a 500.</summary>
    public bool TelegramFails { get; set; }

    /// <summary>Answers some Telegram calls itself (for example file downloads); return null to use the default answer.</summary>
    public Func<RecordedRequest, HttpResponseMessage?>? TelegramOverride { get; set; }
    public TelegramApi Telegram { get; }
    public FakeCalendarServer Server { get; } = new();
    public JujuCalendar Calendar { get; }
    public FakeLlm Llm { get; }
    public Func<string, string, string> LlmAnswer { get; set; } = (_, _) => "{}";

    public Harness()
    {
        TelegramHandler = new StubHandler(request => TelegramOverride?.Invoke(request) ?? (TelegramFails
            ? StubHandler.Json("""{"ok":false}""", HttpStatusCode.InternalServerError)
            : StubHandler.Json("""{"ok":true}""")));
        Telegram = new TelegramApi(new StubHttpFactory(TelegramHandler));
        Calendar = new JujuCalendar(Server.Service);
        Llm = new FakeLlm((system, text) => LlmAnswer(system, text));
    }

    static System.Text.Json.JsonElement Json(RecordedRequest request) => System.Text.Json.JsonDocument.Parse(request.Body).RootElement;

    IEnumerable<RecordedRequest> Calls(string method) => TelegramHandler.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/" + method));

    int _sentBaseline;

    /// <summary>Forget the messages sent so far, so later assertions see only new ones.</summary>
    public void ClearSent() => _sentBaseline = Calls("sendMessage").Count();

    /// <summary>Text of every message the bot sent.</summary>
    public List<string> Sent => Calls("sendMessage").Skip(_sentBaseline).Select(r => Json(r).GetProperty("text").GetString()!).ToList();

    /// <summary>Callback data of the buttons on the n-th sent message (empty when it had none).</summary>
    public List<string> ButtonsOfSent(int index)
    {
        var message = Json(Calls("sendMessage").Skip(_sentBaseline).ElementAt(index));
        return message.TryGetProperty("reply_markup", out var markup)
            ? markup.GetProperty("inline_keyboard")[0].EnumerateArray().Select(b => b.GetProperty("callback_data").GetString()!).ToList()
            : new List<string>();
    }

    public List<string> Edited => Calls("editMessageText").Select(r => Json(r).GetProperty("text").GetString()!).ToList();
    public List<string> CallbackAnswers => Calls("answerCallbackQuery").Select(r => Json(r).TryGetProperty("text", out var t) ? t.GetString()! : "").ToList();

    /// <summary>The id inside the first button of the n-th sent message.</summary>
    public string ProposalIdOfSent(int index) => ButtonsOfSent(index)[0][2..];

    public static string FutureDate(int days) => DateText.Iso(ManilaTime.Today.AddDays(days));

    public static string EventsJson(params string[] events) => "{\"events\":[" + string.Join(",", events) + "]}";

    public static string EventJson(string title, string date, string type = "quiz", string confidence = "high", string extra = "") =>
        $"{{\"type\":\"{type}\",\"title\":\"{title}\",\"date\":{(date == "null" ? "null" : $"\"{date}\"")},\"confidence\":\"{confidence}\"{extra}}}";
}
