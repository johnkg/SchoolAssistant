using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SchoolAssistant;

// Uses the Chat Completions endpoint. Model ID comes from the OPENAI_MODEL setting.
public class OpenAiLlmClient(IHttpClientFactory factory) : ILlmClient
{
    public async Task<string> AskAsync(string system, IReadOnlyList<LlmPart> parts, CancellationToken ct = default)
    {
        var content = parts.Select<LlmPart, object>(p => p switch
        {
            TextPart t => new { type = "text", text = t.Text },
            ImagePart i => new { type = "image_url", image_url = new { url = $"data:{i.MimeType};base64,{Convert.ToBase64String(i.Data)}" } },
            PdfPart d => new { type = "file", file = new { filename = d.FileName, file_data = $"data:application/pdf;base64,{Convert.ToBase64String(d.Data)}" } },
            _ => throw new NotSupportedException(p.GetType().Name)
        }).ToArray();

        var body = JsonSerializer.Serialize(new
        {
            model = Env.Get("OPENAI_MODEL"),
            max_completion_tokens = 4000, // newer/reasoning models reject max_tokens; reasoning also counts toward this
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content }
            }
        });

        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Env.Get("OPENAI_API_KEY"));

        var res = await factory.CreateClient().SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"OpenAI {(int)res.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}