using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchoolAssistant;

public enum KeyboardKind { Match, Add, Update }

public class TelegramApi(IHttpClientFactory f)
{
    static readonly JsonSerializerOptions Opts = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    string Token => Env.Get("TELEGRAM_TOKEN");

    async Task CallAsync(string method, object payload)
    {
        var res = await f.CreateClient().PostAsync($"https://api.telegram.org/bot{Token}/{method}",
            new StringContent(JsonSerializer.Serialize(payload, Opts), Encoding.UTF8, "application/json"));
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Telegram {method} {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
    }

    static string Trunc(string s) => s.Length > 4000 ? s[..4000] + "…" : s;

    public Task SendAsync(long chatId, string text, object? replyMarkup = null)
        => CallAsync("sendMessage", new { chat_id = chatId, text = Trunc(text), reply_markup = replyMarkup });

    // Editing without reply_markup also removes the buttons.
    public Task EditAsync(long chatId, long messageId, string text)
        => CallAsync("editMessageText", new { chat_id = chatId, message_id = messageId, text = Trunc(text) });

    public Task AnswerCallbackAsync(string callbackId, string? text = null)
        => CallAsync("answerCallbackQuery", new { callback_query_id = callbackId, text });

    public sealed record Btn(string text, string callback_data);

    // Match: similar event found. Add: not sure, add it? Update: the sheet changed.
    public static object Keyboard(string id, KeyboardKind kind) => new
    {
        inline_keyboard = new[]
        {
            kind switch
            {
                KeyboardKind.Match => new[] { new Btn("Update existing", "u:" + id), new Btn("Add as new", "n:" + id), new Btn("Skip", "s:" + id) },
                KeyboardKind.Update => new[] { new Btn("Update", "u:" + id), new Btn("Skip", "s:" + id) },
                _ => new[] { new Btn("Add", "n:" + id), new Btn("Skip", "s:" + id) }
            }
        }
    };

    public async Task<byte[]> DownloadAsync(string fileId)
    {
        var http = f.CreateClient();
        var info = await http.GetStringAsync($"https://api.telegram.org/bot{Token}/getFile?file_id={fileId}");
        using var doc = JsonDocument.Parse(info);
        var path = doc.RootElement.GetProperty("result").GetProperty("file_path").GetString();
        return await http.GetByteArrayAsync($"https://api.telegram.org/file/bot{Token}/{path}");
    }
}