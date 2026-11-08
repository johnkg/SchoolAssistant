using System.Net;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using static SchoolAssistant.Tests.Harness;

namespace SchoolAssistant.Tests;

sealed class FakeRequest(FunctionContext context, string body, string? secret) : HttpRequestData(context)
{
    public override Stream Body { get; } = new MemoryStream(Encoding.UTF8.GetBytes(body));
    public override HttpHeadersCollection Headers { get; } = secret == null ? new() : new() { { "X-Telegram-Bot-Api-Secret-Token", secret } };
    public override IReadOnlyCollection<IHttpCookie> Cookies { get; } = Array.Empty<IHttpCookie>();
    public override Uri Url { get; } = new("https://app.test/api/telegram");
    public override IEnumerable<System.Security.Claims.ClaimsIdentity> Identities { get; } = Array.Empty<System.Security.Claims.ClaimsIdentity>();
    public override string Method => "POST";
    public override HttpResponseData CreateResponse() => new FakeResponse(FunctionContext);
}

sealed class FakeResponse(FunctionContext context) : HttpResponseData(context)
{
    public override HttpStatusCode StatusCode { get; set; }
    public override HttpHeadersCollection Headers { get; set; } = new();
    public override Stream Body { get; set; } = new MemoryStream();
    public override HttpCookies Cookies => throw new NotSupportedException();
}

public class TelegramWebhookTests : Harness
{
    readonly TelegramWebhook _webhook;
    readonly SheetSync _sync;
    readonly FunctionContext _context = Substitute.For<FunctionContext>();
    int _nextUpdateId = 100;

    public TelegramWebhookTests()
    {
        var announcements = new AnnouncementProcessor(Llm, Telegram, Calendar, NullLogger<AnnouncementProcessor>.Instance);
        var buttons = new ButtonHandler(Telegram, Calendar, NullLogger<ButtonHandler>.Instance);
        _sync = new SheetSync(Llm, Telegram, Calendar, new FakeSheetTabLister(), new StubHttpFactory(new StubHandler(_ => new HttpResponseMessage())), NullLogger<SheetSync>.Instance);
        _webhook = new TelegramWebhook(Telegram, announcements, buttons, _sync, NullLogger<TelegramWebhook>.Instance);
        LlmAnswer = (system, _) => system.Contains("You extract")
            ? EventsJson(EventJson("Science Quiz", FutureDate(5)))
            : """{"match_id":null}""";
    }

    async Task<HttpStatusCode> Post(string body, string? secret = Secret) =>
        (await _webhook.Run(new FakeRequest(_context, body, secret))).StatusCode;

    string MessageUpdate(string messageFields, long chatId = ChatId) =>
        "{\"update_id\":" + _nextUpdateId++ + ",\"message\":{\"chat\":{\"id\":" + chatId + "}," + messageFields + "}}";

    string TextUpdate(string text, long chatId = ChatId) => MessageUpdate("\"text\":\"" + text + "\"", chatId);

    // ---------- authentication and filtering ----------
    [Theory, InlineData(null), InlineData("wrong")]
    public async Task Requests_without_the_right_secret_are_rejected_untouched(string? secret)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await Post(TextUpdate("hi"), secret));
        Assert.Empty(Llm.Calls);
        Assert.Empty(TelegramHandler.Requests);
    }

    [Fact]
    public async Task Messages_from_other_chats_are_ignored()
    {
        Assert.Equal(HttpStatusCode.OK, await Post(TextUpdate("hi", chatId: 7)));
        Assert.Empty(Llm.Calls);
        Assert.Empty(TelegramHandler.Requests);
    }

    [Fact]
    public async Task With_debug_replies_a_stranger_is_told_the_chat_ids()
    {
        SetEnv(Settings.DebugReplies, "true");
        await Post(TextUpdate("hi", chatId: 7));
        Assert.Equal("⚠️ This chat's ID is 7, but TELEGRAM_CHAT_ID is 42.", Assert.Single(Sent));
    }

    [Fact]
    public async Task Updates_without_a_message_are_acknowledged_and_ignored()
    {
        Assert.Equal(HttpStatusCode.OK, await Post("""{"update_id":1,"edited_message":{}}"""));
        Assert.Equal(HttpStatusCode.OK, await Post(""));
        Assert.Empty(TelegramHandler.Requests);
    }

    // ---------- text messages ----------
    [Fact]
    public async Task A_text_announcement_ends_up_on_the_calendar()
    {
        Assert.Equal(HttpStatusCode.OK, await Post(TextUpdate("Science quiz on Friday")));
        Assert.Equal("Science Quiz", Server.Events.Values.Single().Summary);
        Assert.Contains("Science quiz on Friday", ((TextPart)Llm.Calls[0].Parts[^1]).Text);
    }

    [Fact]
    public async Task A_caption_counts_as_the_text()
    {
        await Post(MessageUpdate("\"caption\":\"from caption\""));
        Assert.Equal("from caption", ((TextPart)Llm.Calls[0].Parts[^1]).Text);
    }

    [Fact]
    public async Task A_message_without_text_still_goes_to_the_model_with_a_placeholder()
    {
        await Post(MessageUpdate("\"sticker\":{}"));
        Assert.Equal("(no text, see attachment)", ((TextPart)Llm.Calls[0].Parts[^1]).Text);
    }

    [Fact]
    public async Task A_repeated_update_is_processed_once()
    {
        var update = TextUpdate("hello");
        await Post(update);
        await Post(update);
        Assert.Single(Llm.Calls);
        Assert.Single(Server.Events);
    }

    [Theory, InlineData("/sync"), InlineData("  /SYNC now")]
    public async Task The_sync_command_runs_a_manual_sheet_sync_instead_of_reading_an_announcement(string command)
    {
        await Post(TextUpdate(command));
        Assert.Empty(Llm.Calls);
        Assert.Equal("⚠️ Set SHEET_ID first.", Assert.Single(Sent));
    }

    // ---------- attachments ----------
    void FilesAre(byte[] content) => TelegramOverride = r =>
        r.Uri.AbsolutePath.EndsWith("getFile") ? StubHandler.Json("""{"result":{"file_path":"files/x"}}""")
        : r.Uri.AbsolutePath.Contains("/file/") ? new HttpResponseMessage { Content = new ByteArrayContent(content) }
        : null;

    [Fact]
    public async Task The_largest_photo_is_downloaded_and_sent_as_a_jpeg()
    {
        FilesAre(new byte[] { 9, 9 });
        await Post(MessageUpdate("\"photo\":[{\"file_id\":\"small\"},{\"file_id\":\"big\"}]"));

        Assert.Contains(TelegramHandler.Requests, r => r.Uri.Query.Contains("file_id=big"));
        Assert.DoesNotContain(TelegramHandler.Requests, r => r.Uri.Query.Contains("file_id=small"));
        var image = Assert.IsType<ImagePart>(Llm.Calls[0].Parts[0]);
        Assert.Equal(new byte[] { 9, 9 }, image.Data);
        Assert.Equal("image/jpeg", image.MimeType);
    }

    [Fact]
    public async Task A_small_pdf_document_is_sent_as_a_pdf()
    {
        FilesAre(new byte[] { 1 });
        await Post(MessageUpdate("\"document\":{\"file_id\":\"d1\",\"mime_type\":\"application/pdf\",\"file_size\":5000}"));
        Assert.IsType<PdfPart>(Llm.Calls[0].Parts[0]);
    }

    [Theory, InlineData("image/jpeg"), InlineData("image/png")]
    public async Task Image_documents_keep_their_type(string mime)
    {
        FilesAre(new byte[] { 1 });
        await Post(MessageUpdate($"\"document\":{{\"file_id\":\"d1\",\"mime_type\":\"{mime}\",\"file_size\":5000}}"));
        Assert.Equal(mime, Assert.IsType<ImagePart>(Llm.Calls[0].Parts[0]).MimeType);
    }

    [Theory]
    [InlineData("\"mime_type\":\"application/zip\",\"file_size\":10", "application/zip")]
    [InlineData("\"mime_type\":\"application/pdf\",\"file_size\":99999999", "application/pdf")]
    [InlineData("\"file_size\":10", "")]
    public async Task Unreadable_documents_are_reported_but_the_text_is_still_processed(string fields, string shownType)
    {
        await Post(MessageUpdate("\"document\":{\"file_id\":\"d1\"," + fields + "},\"caption\":\"see file\""));
        Assert.Equal($"⚠️ Can't read this file type ({shownType}).", Sent[0]);
        Assert.Single(Llm.Calls);
        Assert.DoesNotContain(Llm.Calls[0].Parts, p => p is not TextPart);
    }

    [Fact]
    public async Task A_document_with_no_size_counts_as_small()
    {
        FilesAre(new byte[] { 1 });
        await Post(MessageUpdate("\"document\":{\"file_id\":\"d1\",\"mime_type\":\"application/pdf\"}"));
        Assert.IsType<PdfPart>(Llm.Calls[0].Parts[0]);
    }

    // ---------- button taps ----------
    [Fact]
    public async Task A_button_tap_from_the_right_chat_carries_out_the_proposal()
    {
        var id = await PendingStore.AddAsync(new ExtractedEvent { Title = "Quiz", Date = FutureDate(4) }, "");
        await Post(Tap($"n:{id}", ChatId));
        Assert.Single(Server.Events);
        Assert.StartsWith("✅ Added: Quiz", Assert.Single(Edited));
    }

    [Fact]
    public async Task A_button_tap_from_another_chat_is_only_acknowledged()
    {
        const long chatId = 7;
        var id = await PendingStore.AddAsync(new ExtractedEvent { Title = "Quiz", Date = FutureDate(4) }, "");
        await Post(Tap($"n:{id}", chatId));
        Assert.Empty(Server.Events);
        Assert.Single(CallbackAnswers);
    }

    [Fact]
    public async Task A_button_tap_without_a_message_is_acknowledged_and_ignored()
    {
        await Post("""{"update_id":5,"callback_query":{"id":"cb9","data":"n:abc"}}""");
        Assert.Single(CallbackAnswers);
        Assert.Empty(Server.Events);
    }

    static string Tap(string data, long chatId) =>
        "{\"update_id\":9,\"callback_query\":{\"id\":\"cb1\",\"data\":\"" + data + "\",\"message\":{\"message_id\":3,\"chat\":{\"id\":" + chatId + "}}}}";

    // ---------- failures ----------
    [Fact]
    public async Task A_failure_is_reported_to_the_chat_and_still_answers_ok()
    {
        LlmAnswer = (_, _) => throw new InvalidOperationException("model exploded");
        Assert.Equal(HttpStatusCode.OK, await Post(TextUpdate("hello")));
        Assert.Equal("❌ model exploded", Assert.Single(Sent));
    }

    [Fact]
    public async Task A_failure_in_a_button_tap_is_reported_too()
    {
        var id = await PendingStore.AddAsync(new ExtractedEvent { Title = "Quiz", Date = FutureDate(4) }, "");
        Server.FailWith = HttpStatusCode.Forbidden;
        Assert.Equal(HttpStatusCode.OK, await Post(Tap($"n:{id}", ChatId)));
        Assert.StartsWith("❌ ", Assert.Single(Sent));
    }

    [Fact]
    public async Task A_failure_from_a_stranger_is_not_reported_to_them()
    {
        TelegramOverride = _ => throw new InvalidOperationException("telegram down");
        SetEnv(Settings.DebugReplies, "true");
        Assert.Equal(HttpStatusCode.OK, await Post(TextUpdate("hi", chatId: 7)));
    }

    [Fact]
    public async Task If_the_error_reply_cannot_be_sent_the_webhook_still_answers_ok()
    {
        LlmAnswer = (_, _) => throw new InvalidOperationException("model exploded");
        TelegramFails = true;
        Assert.Equal(HttpStatusCode.OK, await Post(TextUpdate("hello")));
    }
}
