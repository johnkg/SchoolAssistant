using System.Net.Http.Headers;
using System.Text.Json;

namespace SchoolAssistant;

// Uses the Chat Completions endpoint. Model ID comes from the OPENAI_MODEL setting.
public class OpenAiLlmClient(IHttpClientFactory httpFactory) : HttpLlmClient(httpFactory)
{
    const string Endpoint = "https://api.openai.com/v1/chat/completions";

    protected override string ProviderName => "OpenAI";

    protected override HttpRequestMessage BuildRequest(string system, IReadOnlyList<LlmPart> parts)
    {
        var content = parts.Select<LlmPart, object>(part => part switch
        {
            TextPart text => new { type = "text", text = text.Text },
            ImagePart image => new { type = "image_url", image_url = new { url = DataUrl(image.MimeType, image.Data) } },
            PdfPart pdf => new { type = "file", file = new { filename = pdf.FileName, file_data = DataUrl(MimeTypes.Pdf, pdf.Data) } },
            _ => throw new NotSupportedException(part.GetType().Name)
        }).ToArray();

        var request = JsonPost(Endpoint, new
        {
            model = Env.Get(Settings.OpenAiModel),
            max_completion_tokens = MaxOutputTokens, // newer/reasoning models reject max_tokens
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content }
            }
        });
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Env.Get(Settings.OpenAiApiKey));
        return request;
    }

    protected override string ReadReply(JsonElement response) =>
        response.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";

    static string DataUrl(string mimeType, byte[] data) => $"data:{mimeType};base64,{Base64(data)}";
}
