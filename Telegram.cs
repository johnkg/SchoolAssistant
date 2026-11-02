using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchoolAssistant;

/// <summary>Which buttons a proposal message offers.</summary>
public enum KeyboardKind
{
    Match,  // a similar event exists: update it, add as new, or skip
    Add,    // not sure about this one: add it or skip
    Update, // the sheet item changed: update or skip
    Remove  // the item left the sheet: delete or keep
}

public class TelegramApi(IHttpClientFactory httpFactory)
{
    const string ApiBase = "https://api.telegram.org";
    const int MaxMessageChars = 4000; // Telegram rejects messages over 4096 characters

    static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    string Token => Env.Get(Settings.TelegramToken);

    public Task SendAsync(long chatId, string text, object? replyMarkup = null) =>
        CallAsync("sendMessage", new { chat_id = chatId, text = Shorten(text), reply_markup = replyMarkup });

    // Editing without reply_markup also removes the buttons.
    public Task EditAsync(long chatId, long messageId, string text) =>
        CallAsync("editMessageText", new { chat_id = chatId, message_id = messageId, text = Shorten(text) });

    public Task AnswerCallbackAsync(string callbackId, string? text = null) =>
        CallAsync("answerCallbackQuery", new { callback_query_id = callbackId, text });

    public async Task<byte[]> DownloadAsync(string fileId)
    {
        var http = httpFactory.CreateClient();
        var info = await http.GetStringAsync($"{ApiBase}/bot{Token}/getFile?file_id={fileId}");
        using var doc = JsonDocument.Parse(info);
        var path = doc.RootElement.GetProperty("result").GetProperty("file_path").GetString();
        return await http.GetByteArrayAsync($"{ApiBase}/file/bot{Token}/{path}");
    }

    // ---------- inline buttons ----------
    public static object Keyboard(string proposalId, KeyboardKind kind) => new
    {
        inline_keyboard = new[]
        {
            kind switch
            {
                KeyboardKind.Match => new[] { Button("Update existing", CallbackAction.Update, proposalId), Button("Add as new", CallbackAction.AddNew, proposalId), Button("Skip", CallbackAction.Skip, proposalId) },
                KeyboardKind.Update => new[] { Button("Update", CallbackAction.Update, proposalId), Button("Skip", CallbackAction.Skip, proposalId) },
                KeyboardKind.Remove => new[] { Button("Delete", CallbackAction.Delete, proposalId), Button("Keep", CallbackAction.Skip, proposalId) },
                _ => new[] { Button("Add", CallbackAction.AddNew, proposalId), Button("Skip", CallbackAction.Skip, proposalId) }
            }
        }
    };

    /// <summary>Reads back what <see cref="Keyboard"/> put in a button's callback data.</summary>
    public static bool TryParseCallback(string data, out string action, out string proposalId)
    {
        var separator = data.IndexOf(CallbackAction.Separator);
        action = separator < 0 ? "" : data[..separator];
        proposalId = separator < 0 ? "" : data[(separator + 1)..];
        return separator >= 0;
    }

    static InlineButton Button(string label, string action, string proposalId) =>
        new(label, action + CallbackAction.Separator + proposalId);

    sealed record InlineButton(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("callback_data")] string CallbackData);

    // ---------- plumbing ----------
    async Task CallAsync(string method, object payload)
    {
        var body = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, MimeTypes.Json);
        var response = await httpFactory.CreateClient().PostAsync($"{ApiBase}/bot{Token}/{method}", body);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Telegram {method} {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    static string Shorten(string text) => TextUtil.Truncate(text, MaxMessageChars);
}
