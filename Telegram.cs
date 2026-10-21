using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchoolAssistant;

public enum KeyboardKind { Match, Add, Update, Remove }

public class TelegramApi(IHttpClientFactory f)
{
    const string ApiBase = "https://api.telegram.org";
    const int MaxMessageChars = 4000; // Telegram rejects messages over 4096 characters

    static readonly JsonSerializerOptions Opts = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    string Token => Env.Get(Settings.TelegramToken);

    async Task CallAsync(string method, object payload)
    {
        var res = await f.CreateClient().PostAsync($"{ApiBase}/bot{Token}/{method}",
            new StringContent(JsonSerializer.Serialize(payload, Opts), Encoding.UTF8, "application/json"));
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Telegram {method} {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
    }

    static string Trunc(string s) => TextUtil.Truncate(s, MaxMessageChars);

    public Task SendAsync(long chatId, string text, object? replyMarkup = null)
        => CallAsync("sendMessage", new { chat_id = chatId, text = Trunc(text), reply_markup = replyMarkup });

    // Editing without reply_markup also removes the buttons.
    public Task EditAsync(long chatId, long messageId, string text)
        => CallAsync("editMessageText", new { chat_id = chatId, message_id = messageId, text = Trunc(text) });

    public Task AnswerCallbackAsync(string callbackId, string? text = null)
        => CallAsync("answerCallbackQuery", new { callback_query_id = callbackId, text });

    public sealed record Btn(string text, string callback_data);

    // Match: similar event found. Add: not sure, add it? Update: the sheet changed. Remove: the item left the sheet.
    public static object Keyboard(string id, KeyboardKind kind) => new
    {
        inline_keyboard = new[]
        {
            kind switch
            {
                KeyboardKind.Match => new[] { Button("Update existing", CallbackAction.Update, id), Button("Add as new", CallbackAction.AddNew, id), Button("Skip", CallbackAction.Skip, id) },
                KeyboardKind.Update => new[] { Button("Update", CallbackAction.Update, id), Button("Skip", CallbackAction.Skip, id) },
                KeyboardKind.Remove => new[] { Button("Delete", CallbackAction.Delete, id), Button("Keep", CallbackAction.Skip, id) },
                _ => new[] { Button("Add", CallbackAction.AddNew, id), Button("Skip", CallbackAction.Skip, id) }
            }
        }
    };

    static Btn Button(string text, string action, string id) => new(text, action + CallbackAction.Separator + id);

    public async Task<byte[]> DownloadAsync(string fileId)
    {
        var http = f.CreateClient();
        var info = await http.GetStringAsync($"{ApiBase}/bot{Token}/getFile?file_id={fileId}");
        using var doc = JsonDocument.Parse(info);
        var path = doc.RootElement.GetProperty("result").GetProperty("file_path").GetString();
        return await http.GetByteArrayAsync($"{ApiBase}/file/bot{Token}/{path}");
    }
}