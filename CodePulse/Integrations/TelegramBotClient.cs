using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.IO;

namespace CodePulse.Integrations;

public sealed class TelegramBotClient
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    public string BotToken { get; set; } = string.Empty;

    public string ChatId { get; set; } = string.Empty;

    public async Task SendMessageAsync(
        string code,
        string sourceMessage,
        string channelName,
        CancellationToken cancellationToken)
    {
        _ = sourceMessage;
        _ = channelName;

        if (string.IsNullOrWhiteSpace(BotToken) || string.IsNullOrWhiteSpace(ChatId))
        {
            throw new InvalidOperationException("ยังไม่ได้ตั้งค่า Telegram Bot Token หรือ Chat ID");
        }

        var endpoint = $"https://api.telegram.org/bot{BotToken}/sendMessage";
        var payload = new
        {
            chat_id = ChatId,
            text = code.Trim()
        };

        using var response = await HttpClient.PostAsJsonAsync(endpoint, payload, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"Telegram API error {(int)response.StatusCode}: {errorBody}");
    }

    public async Task SendPhotoAsync(
        string code,
        string imagePath,
        string sourceMessage,
        string channelName,
        CancellationToken cancellationToken)
    {
        _ = sourceMessage;
        _ = channelName;

        if (string.IsNullOrWhiteSpace(BotToken) || string.IsNullOrWhiteSpace(ChatId))
        {
            throw new InvalidOperationException("à¸¢à¸±à¸‡à¹„à¸¡à¹ˆà¹„à¸”à¹‰à¸•à¸±à¹‰à¸‡à¸„à¹ˆà¸² Telegram Bot Token à¸«à¸£à¸·à¸­ Chat ID");
        }

        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException("à¹„à¸¡à¹ˆà¸žà¸šà¹„à¸Ÿà¸¥à¹Œà¸£à¸¹à¸›à¸ à¸²à¸žà¸ªà¸³à¸«à¸£à¸±à¸šà¸ªà¹ˆà¸‡à¹„à¸› Telegram", imagePath);
        }

        try
        {
            await SendPhotoCoreAsync(code, imagePath, cancellationToken);
        }
        catch (HttpRequestException ex) when (ShouldFallbackToDocument(ex))
        {
            await SendDocumentCoreAsync(code, imagePath, cancellationToken);
        }
    }

    private async Task SendPhotoCoreAsync(string code, string imagePath, CancellationToken cancellationToken)
    {
        var endpoint = $"https://api.telegram.org/bot{BotToken}/sendPhoto";
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(ChatId), "chat_id");
        form.Add(new StringContent(code.Trim()), "caption");

        await using var stream = File.OpenRead(imagePath);
        using var imageContent = new StreamContent(stream);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(imageContent, "photo", Path.GetFileName(imagePath));

        using var response = await HttpClient.PostAsync(endpoint, form, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"Telegram API error {(int)response.StatusCode}: {errorBody}");
    }

    private async Task SendDocumentCoreAsync(string code, string imagePath, CancellationToken cancellationToken)
    {
        var endpoint = $"https://api.telegram.org/bot{BotToken}/sendDocument";
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(ChatId), "chat_id");
        form.Add(new StringContent(code.Trim()), "caption");

        await using var stream = File.OpenRead(imagePath);
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(fileContent, "document", Path.GetFileName(imagePath));

        using var response = await HttpClient.PostAsync(endpoint, form, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"Telegram API error {(int)response.StatusCode}: {errorBody}");
    }

    private static bool ShouldFallbackToDocument(HttpRequestException exception)
    {
        return exception.Message.Contains("PHOTO_INVALID_DIMENSIONS", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("wrong file identifier", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("failed to get HTTP URL content", StringComparison.OrdinalIgnoreCase);
    }
}
