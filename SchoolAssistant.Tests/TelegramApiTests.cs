using System.Net;
using System.Text.Json;
using Xunit;

namespace SchoolAssistant.Tests;

public class TelegramApiTests : AppTestBase
{
    readonly StubHandler _handler;
    readonly TelegramApi _telegram;

    public TelegramApiTests()
    {
        _handler = new StubHandler(_ => StubHandler.Json("""{"ok":true}"""));
        _telegram = new TelegramApi(new StubHttpFactory(_handler));
    }

    static JsonElement Body(RecordedRequest request) => JsonDocument.Parse(request.Body).RootElement;

    [Fact]
    public async Task SendAsync_posts_to_the_bot_url_with_text_and_chat()
    {
        await _telegram.SendAsync(ChatId, "hello");
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://api.telegram.org/botbot-token/sendMessage", request.Uri.ToString());
        Assert.Equal(("hello", ChatId), (Body(request).GetProperty("text").GetString(), Body(request).GetProperty("chat_id").GetInt64()));
        Assert.False(Body(request).TryGetProperty("reply_markup", out _));
    }

    [Fact]
    public async Task SendAsync_cuts_text_over_the_telegram_limit()
    {
        await _telegram.SendAsync(ChatId, new string('a', 5000));
        Assert.Equal(4001, Body(_handler.Requests[0]).GetProperty("text").GetString()!.Length);
    }

    [Fact]
    public async Task SendAsync_includes_buttons_when_given()
    {
        await _telegram.SendAsync(ChatId, "q", TelegramApi.Keyboard("abc", KeyboardKind.Add));
        var buttons = Body(_handler.Requests[0]).GetProperty("reply_markup").GetProperty("inline_keyboard")[0];
        Assert.Equal(new[] { "Add", "Skip" }, buttons.EnumerateArray().Select(b => b.GetProperty("text").GetString()));
        Assert.Equal(new[] { "n:abc", "s:abc" }, buttons.EnumerateArray().Select(b => b.GetProperty("callback_data").GetString()));
    }

    [Fact]
    public async Task EditAsync_edits_the_message_text()
    {
        await _telegram.EditAsync(ChatId, 7, "done");
        Assert.EndsWith("/editMessageText", _handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal(7, Body(_handler.Requests[0]).GetProperty("message_id").GetInt64());
    }

    [Fact]
    public async Task AnswerCallbackAsync_sends_the_notice_only_when_given()
    {
        await _telegram.AnswerCallbackAsync("cb1");
        await _telegram.AnswerCallbackAsync("cb2", "Expired");
        Assert.False(Body(_handler.Requests[0]).TryGetProperty("text", out _));
        Assert.Equal("Expired", Body(_handler.Requests[1]).GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_late_button_answer_is_logged_and_ignored_instead_of_failing()
    {
        var tooOld = """{"ok":false,"error_code":400,"description":"Bad Request: query is too old"}""";
        var telegram = new TelegramApi(
            new StubHttpFactory(new StubHandler(_ => StubHandler.Json(tooOld, HttpStatusCode.BadRequest))),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TelegramApi>.Instance);

        await telegram.AnswerCallbackAsync("cb1", "Expired"); // must not throw
    }

    [Fact]
    public async Task A_late_button_answer_is_ignored_even_without_a_logger()
    {
        var telegram = new TelegramApi(new StubHttpFactory(new StubHandler(_ => StubHandler.Json("{}", HttpStatusCode.BadRequest))));
        await telegram.AnswerCallbackAsync("cb1");
    }

    [Fact]
    public async Task A_failed_call_throws_with_the_telegram_answer()
    {
        var failing = new TelegramApi(new StubHttpFactory(new StubHandler(_ => StubHandler.Json("""{"description":"bad"}""", HttpStatusCode.BadRequest))));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => failing.SendAsync(ChatId, "x"));
        Assert.Contains("sendMessage 400", ex.Message);
        Assert.Contains("bad", ex.Message);
    }

    [Fact]
    public async Task DownloadAsync_looks_up_the_path_then_downloads_the_file()
    {
        var handler = new StubHandler(r => r.Uri.AbsolutePath.EndsWith("getFile")
            ? StubHandler.Json("""{"result":{"file_path":"photos/a.jpg"}}""")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) });
        var bytes = await new TelegramApi(new StubHttpFactory(handler)).DownloadAsync("file-9");

        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
        Assert.Equal("https://api.telegram.org/botbot-token/getFile?file_id=file-9", handler.Requests[0].Uri.ToString());
        Assert.Equal("https://api.telegram.org/file/botbot-token/photos/a.jpg", handler.Requests[1].Uri.ToString());
    }

    [Theory]
    [InlineData(KeyboardKind.Match, "Update existing|Add as new|Skip", "u:id|n:id|s:id")]
    [InlineData(KeyboardKind.Add, "Add|Skip", "n:id|s:id")]
    [InlineData(KeyboardKind.Update, "Update|Skip", "u:id|s:id")]
    [InlineData(KeyboardKind.Remove, "Delete|Keep", "d:id|s:id")]
    public void Keyboard_offers_the_right_buttons(KeyboardKind kind, string labels, string callbacks)
    {
        var json = JsonSerializer.SerializeToElement(TelegramApi.Keyboard("id", kind));
        var buttons = json.GetProperty("inline_keyboard")[0].EnumerateArray().ToList();
        Assert.Equal(labels, string.Join("|", buttons.Select(b => b.GetProperty("text").GetString())));
        Assert.Equal(callbacks, string.Join("|", buttons.Select(b => b.GetProperty("callback_data").GetString())));
    }

    [Fact]
    public void TryParseCallback_splits_action_and_id()
    {
        Assert.True(TelegramApi.TryParseCallback("u:abc:def", out var action, out var id));
        Assert.Equal(("u", "abc:def"), (action, id));
    }

    [Fact] public void TryParseCallback_rejects_data_without_a_separator() => Assert.False(TelegramApi.TryParseCallback("nonsense", out _, out _));
}
