using System.Text.Json;

namespace SchoolAssistant;

public class AnthropicLlmClient(IHttpClientFactory httpFactory) : HttpLlmClient(httpFactory)
{
    const string Endpoint = "https://api.anthropic.com/v1/messages";
    const string ApiVersion = "2023-06-01";
    const string DefaultModel = "claude-haiku-4-5-20251001";

    protected override string ProviderName => "Anthropic";

    protected override HttpRequestMessage BuildRequest(string system, IReadOnlyList<LlmPart> parts)
    {
        var content = parts.Select<LlmPart, object>(part => part switch
        {
            TextPart text => new { type = "text", text = text.Text },
            ImagePart image => new { type = "image", source = Base64Source(image.MimeType, image.Data) },
            PdfPart pdf => new { type = "document", source = Base64Source(MimeTypes.Pdf, pdf.Data) },
            _ => throw new NotSupportedException(part.GetType().Name)
        }).ToArray();

        var request = JsonPost(Endpoint, new
        {
            model = Env.GetOr(Settings.AnthropicModel, DefaultModel),
            max_tokens = MaxOutputTokens,
            system,
            messages = new[] { new { role = "user", content } }
        });
        request.Headers.Add("x-api-key", Env.Get(Settings.AnthropicApiKey));
        request.Headers.Add("anthropic-version", ApiVersion);
        return request;
    }

    protected override string ReadReply(JsonElement response) =>
        response.GetProperty("content")[0].GetProperty("text").GetString() ?? "";

    static object Base64Source(string mimeType, byte[] data) =>
        new { type = "base64", media_type = mimeType, data = Base64(data) };
}
