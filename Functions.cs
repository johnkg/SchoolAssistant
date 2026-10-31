using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace SchoolAssistant;

/// <summary>Helpers for reading Telegram update JSON.</summary>
internal static class TelegramUpdate
{
    public static long ChatOf(JsonElement message) => message.GetProperty("chat").GetProperty("id").GetInt64();
}

/// <summary>Telegram webhook: checks the request, ignores other chats, and routes to the sheet sync, the button handler or the announcement processor.</summary>
public class TelegramWebhook(
    TelegramApi telegram, AnnouncementProcessor announcements, ButtonHandler buttons, SheetSync sheetSync, ILogger<TelegramWebhook> log)
{
    const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";
    const string SyncCommand = "/sync";
    const long MaxAttachmentBytes = 10_000_000;

    [Function("telegram")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
    {
        if (!req.Headers.TryGetValues(SecretHeader, out var secrets) ||
            secrets.FirstOrDefault() != Env.Get(Settings.TelegramSecret))
            return req.CreateResponse(HttpStatusCode.Unauthorized);

        long? replyChatId = null;
        try
        {
            using var doc = JsonDocument.Parse(await req.ReadAsStringAsync() ?? "{}");
            replyChatId = await RouteAsync(doc.RootElement);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Webhook failed");
            await TryReplyWithErrorAsync(replyChatId, ex);
        }
        return req.CreateResponse(HttpStatusCode.OK); // always 200 so Telegram doesn't retry forever
    }

    /// <summary>Handles one update. Returns the chat to report errors to (only the configured chat), if any.</summary>
    async Task<long?> RouteAsync(JsonElement update)
    {
        var allowedChatId = Env.TelegramChatId;

        if (update.TryGetProperty("callback_query", out var callback))
        {
            var fromAllowedChat = callback.TryGetProperty("message", out var callbackMessage)
                                  && TelegramUpdate.ChatOf(callbackMessage) == allowedChatId;
            if (fromAllowedChat) await buttons.HandleAsync(callback);
            else await AnswerAndIgnoreAsync(callback);
            return fromAllowedChat ? allowedChatId : null;
        }

        if (!update.TryGetProperty("message", out var message))
        {
            log.LogWarning("Update has no 'message' field, ignoring");
            return null;
        }

        var chatId = TelegramUpdate.ChatOf(message);
        if (chatId != allowedChatId)
        {
            log.LogWarning("Ignoring chat {ChatId}; {Setting} is {Expected}", chatId, Settings.TelegramChatId, allowedChatId);
            if (Env.GetFlag(Settings.DebugReplies, defaultValue: false))
                await telegram.SendAsync(chatId, $"⚠️ This chat's ID is {chatId}, but {Settings.TelegramChatId} is {allowedChatId}.");
            return null;
        }

        await HandleMessageAsync(chatId, update.GetProperty("update_id").GetInt64().ToString(), message);
        return chatId;
    }

    async Task HandleMessageAsync(long chatId, string updateId, JsonElement message)
    {
        var text = message.TryGetProperty("text", out var textElement) ? textElement.GetString()
                 : message.TryGetProperty("caption", out var captionElement) ? captionElement.GetString() : null;

        if (!await MessageStore.TryRecordAsync(updateId, text ?? ""))
        {
            log.LogInformation("Duplicate update {UpdateId}, skipping", updateId);
            return;
        }

        if (text?.Trim().StartsWith(SyncCommand, StringComparison.OrdinalIgnoreCase) == true)
        {
            await sheetSync.RunAsync(chatId, manual: true);
            return;
        }

        var attachments = await ReadAttachmentsAsync(chatId, message);
        await announcements.HandleAsync(chatId, updateId, text, attachments);
    }

    /// <summary>The photo or the PDF/image document sent with the message, as provider-neutral parts.</summary>
    async Task<List<LlmPart>> ReadAttachmentsAsync(long chatId, JsonElement message)
    {
        var attachments = new List<LlmPart>();
        if (message.TryGetProperty("photo", out var photoSizes))
        {
            var largest = photoSizes.EnumerateArray().Last(); // Telegram lists sizes smallest to largest
            attachments.Add(new ImagePart(await telegram.DownloadAsync(FileIdOf(largest)), MimeTypes.Jpeg));
        }
        else if (message.TryGetProperty("document", out var document))
        {
            var mimeType = document.TryGetProperty("mime_type", out var mimeElement) ? mimeElement.GetString() ?? "" : "";
            var size = document.TryGetProperty("file_size", out var sizeElement) ? sizeElement.GetInt64() : 0;
            var isSmallEnough = size < MaxAttachmentBytes;

            if (mimeType == MimeTypes.Pdf && isSmallEnough)
                attachments.Add(new PdfPart(await telegram.DownloadAsync(FileIdOf(document))));
            else if (mimeType is MimeTypes.Jpeg or MimeTypes.Png && isSmallEnough)
                attachments.Add(new ImagePart(await telegram.DownloadAsync(FileIdOf(document)), mimeType));
            else
                await telegram.SendAsync(chatId, $"⚠️ Can't read this file type ({mimeType}).");
        }
        return attachments;
    }

    static string FileIdOf(JsonElement file) => file.GetProperty("file_id").GetString()!;

    async Task AnswerAndIgnoreAsync(JsonElement callback) =>
        await telegram.AnswerCallbackAsync(callback.GetProperty("id").GetString()!);

    async Task TryReplyWithErrorAsync(long? chatId, Exception error)
    {
        if (chatId is not long id) return;
        try { await telegram.SendAsync(id, "❌ " + TextUtil.Short(error)); }
        catch (Exception inner) { log.LogError(inner, "Could not send error reply"); }
    }
}
