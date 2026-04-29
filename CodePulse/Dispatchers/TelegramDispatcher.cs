using System.IO;
using CodePulse.Integrations;
using CodePulse.Models;

namespace CodePulse.Dispatchers;

public sealed class TelegramDispatcher : IDispatcher
{
    private readonly AppSettings _settings;
    private readonly TelegramBotClient _telegramBotClient;

    public TelegramDispatcher(AppSettings settings, TelegramBotClient telegramBotClient)
    {
        _settings = settings;
        _telegramBotClient = telegramBotClient;
    }

    public async Task DispatchAsync(CodeDetectedEvent detectedEvent, CancellationToken cancellationToken)
    {
        _telegramBotClient.BotToken = _settings.Dispatch.TelegramBotToken;
        _telegramBotClient.ChatId = _settings.Dispatch.TelegramChatId;

        if (_settings.Dispatch.SendManualCaptureImage &&
            !string.IsNullOrWhiteSpace(detectedEvent.CapturedImagePath) &&
            File.Exists(detectedEvent.CapturedImagePath))
        {
            await _telegramBotClient.SendPhotoAsync(
                detectedEvent.Candidate.Value,
                detectedEvent.CapturedImagePath,
                detectedEvent.SourceMessage,
                detectedEvent.Channel.Name,
                cancellationToken);
            return;
        }

        await _telegramBotClient.SendMessageAsync(
            detectedEvent.Candidate.Value,
            detectedEvent.SourceMessage,
            detectedEvent.Channel.Name,
            cancellationToken);
    }
}
