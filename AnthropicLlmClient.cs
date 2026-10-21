using System.Text;
using System.Text.Json;

namespace SchoolAssistant;

public class AnthropicLlmClient(IHttpClientFactory factory) : ILlmClient
{
    const string Endpoint = "https://api.anthropic.com/v1/messages";
    const string ApiVersion = "2023-06-01";
    const string DefaultModel = "claude-haiku-4-5-20251001";
    const int MaxTokens = 4000;

    public async Task<string> AskAsync(string system, IReadOnlyList<LlmPart> parts, CancellationToken ct = default)
    {
        var content = parts.Select<LlmPart, object>(p => p switch
        {
            TextPart t => new { type = "text", text = t.Text },
            ImagePart i => new { type = "image", source = new { type = "base64", media_type = i.MimeType, data = Convert.ToBase64String(i.Data) } },
            PdfPart d => new { type = "document", source = new { type = "base64", media_type = "application/pdf", data = Convert.ToBase64String(d.Data) } },
            _ => throw new NotSupportedException(p.GetType().Name)
        }).ToArray();

        var body = JsonSerializer.Serialize(new
        {
            model = Env.GetOr(Settings.AnthropicModel, DefaultModel),
            max_tokens = MaxTokens,
            system,
            messages = new[] { new { role = "user", content } }
        });

        var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add("x-api-key", Env.Get(Settings.AnthropicApiKey));
        req.Headers.Add("anthropic-version", ApiVersion);

        var res = await factory.CreateClient().SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Anthropic {(int)res.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }
}