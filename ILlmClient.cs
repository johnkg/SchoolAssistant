using System.Text;
using System.Text.Json;

namespace SchoolAssistant;

// Provider-neutral input. The callers build these; each adapter translates them to its API.
public abstract record LlmPart;
public sealed record TextPart(string Text) : LlmPart;
public sealed record ImagePart(byte[] Data, string MimeType) : LlmPart;
public sealed record PdfPart(byte[] Data, string FileName = "document.pdf") : LlmPart;

/// <summary>The only LLM surface the app depends on. Add a new provider by implementing this.</summary>
public interface ILlmClient
{
    Task<string> AskAsync(string system, IReadOnlyList<LlmPart> parts);
}

/// <summary>Shared request/response handling for providers that are one JSON POST that returns text.</summary>
public abstract class HttpLlmClient(IHttpClientFactory httpFactory) : ILlmClient
{
    protected const int MaxOutputTokens = 4000; // reasoning tokens count toward this too

    protected abstract string ProviderName { get; }
    protected abstract HttpRequestMessage BuildRequest(string system, IReadOnlyList<LlmPart> parts);
    protected abstract string ReadReply(JsonElement response);

    public async Task<string> AskAsync(string system, IReadOnlyList<LlmPart> parts)
    {
        using var request = BuildRequest(system, parts);
        var response = await httpFactory.CreateClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{ProviderName} {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        return ReadReply(doc.RootElement);
    }

    protected static HttpRequestMessage JsonPost(string endpoint, object payload) => new(HttpMethod.Post, endpoint)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MimeTypes.Json)
    };

    protected static string Base64(byte[] data) => Convert.ToBase64String(data);
}
