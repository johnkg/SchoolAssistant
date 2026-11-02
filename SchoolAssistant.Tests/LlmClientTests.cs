using System.Net;
using System.Text.Json;
using Xunit;

namespace SchoolAssistant.Tests;

public class OpenAiLlmClientTests : AppTestBase
{
    static (OpenAiLlmClient Client, StubHandler Handler) Make(string reply = """{"choices":[{"message":{"content":"answer"}}]}""", HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHandler(_ => StubHandler.Json(reply, status));
        return (new OpenAiLlmClient(new StubHttpFactory(handler)), handler);
    }

    [Fact]
    public async Task Sends_system_prompt_and_all_part_kinds_and_returns_the_reply()
    {
        var (client, handler) = Make();
        var reply = await client.AskAsync("be brief", new LlmPart[] { new TextPart("hi"), new ImagePart(new byte[] { 1, 2 }, "image/png"), new PdfPart(new byte[] { 3 }) });

        Assert.Equal("answer", reply);
        var request = handler.Requests.Single();
        Assert.Equal("https://api.openai.com/v1/chat/completions", request.Uri.ToString());
        Assert.Equal("Bearer openai-key", request.Headers["Authorization"]);

        var body = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal("gpt-test", body.GetProperty("model").GetString());
        Assert.Equal(4000, body.GetProperty("max_completion_tokens").GetInt32());
        var messages = body.GetProperty("messages");
        Assert.Equal(("system", "be brief"), (messages[0].GetProperty("role").GetString(), messages[0].GetProperty("content").GetString()));
        var content = messages[1].GetProperty("content");
        Assert.Equal("hi", content[0].GetProperty("text").GetString());
        Assert.Equal("data:image/png;base64,AQI=", content[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal(("document.pdf", "data:application/pdf;base64,Aw=="),
            (content[2].GetProperty("file").GetProperty("filename").GetString(), content[2].GetProperty("file").GetProperty("file_data").GetString()));
    }

    [Fact]
    public async Task An_error_status_throws_with_provider_and_body()
    {
        var (client, _) = Make("quota", HttpStatusCode.TooManyRequests);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.AskAsync("s", new LlmPart[] { new TextPart("x") }));
        Assert.Equal("OpenAI 429: quota", ex.Message);
    }

    [Fact] public async Task Null_content_becomes_empty_text() => Assert.Equal("", await Make("""{"choices":[{"message":{"content":null}}]}""").Client.AskAsync("s", new LlmPart[] { new TextPart("x") }));

    [Fact]
    public async Task An_unknown_part_type_is_not_supported() =>
        await Assert.ThrowsAsync<NotSupportedException>(() => Make().Client.AskAsync("s", new LlmPart[] { new OtherPart() }));

    sealed record OtherPart : LlmPart;
}

public class AnthropicLlmClientTests : AppTestBase
{
    static (AnthropicLlmClient Client, StubHandler Handler) Make(string reply = """{"content":[{"text":"answer"}]}""", HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHandler(_ => StubHandler.Json(reply, status));
        return (new AnthropicLlmClient(new StubHttpFactory(handler)), handler);
    }

    [Fact]
    public async Task Sends_parts_with_headers_and_returns_the_reply()
    {
        var (client, handler) = Make();
        var reply = await client.AskAsync("be brief", new LlmPart[] { new TextPart("hi"), new ImagePart(new byte[] { 1, 2 }, "image/jpeg"), new PdfPart(new byte[] { 3 }) });

        Assert.Equal("answer", reply);
        var request = handler.Requests.Single();
        Assert.Equal("https://api.anthropic.com/v1/messages", request.Uri.ToString());
        Assert.Equal("anthropic-key", request.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", request.Headers["anthropic-version"]);

        var body = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal(("claude-haiku-4-5-20251001", "be brief", 4000),
            (body.GetProperty("model").GetString(), body.GetProperty("system").GetString(), body.GetProperty("max_tokens").GetInt32()));
        var content = body.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("hi", content[0].GetProperty("text").GetString());
        Assert.Equal(("image", "image/jpeg", "AQI="), (content[1].GetProperty("type").GetString(), content[1].GetProperty("source").GetProperty("media_type").GetString(), content[1].GetProperty("source").GetProperty("data").GetString()));
        Assert.Equal(("document", "application/pdf"), (content[2].GetProperty("type").GetString(), content[2].GetProperty("source").GetProperty("media_type").GetString()));
    }

    [Fact]
    public async Task The_model_comes_from_the_setting_when_present()
    {
        SetEnv(Settings.AnthropicModel, "claude-custom");
        var (client, handler) = Make();
        await client.AskAsync("s", new LlmPart[] { new TextPart("x") });
        Assert.Equal("claude-custom", JsonDocument.Parse(handler.Requests[0].Body).RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task An_error_status_throws_with_provider_and_body()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Make("overloaded", HttpStatusCode.ServiceUnavailable).Client.AskAsync("s", new LlmPart[] { new TextPart("x") }));
        Assert.Equal("Anthropic 503: overloaded", ex.Message);
    }

    [Fact] public async Task Null_text_becomes_empty() => Assert.Equal("", await Make("""{"content":[{"text":null}]}""").Client.AskAsync("s", new LlmPart[] { new TextPart("x") }));

    [Fact]
    public async Task An_unknown_part_type_is_not_supported() =>
        await Assert.ThrowsAsync<NotSupportedException>(() => Make().Client.AskAsync("s", new LlmPart[] { new OtherPart() }));

    sealed record OtherPart : LlmPart;
}
