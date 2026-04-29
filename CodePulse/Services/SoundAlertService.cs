using NAudio.Wave;

namespace CodePulse.Services;

public sealed class SoundAlertService
{
    private const string DingFileName = "notify-ding.mp3";
    private const string VoiceFileName = "notify-voice.wav";

    public event Action<string>? LogEmitted;

    public async Task PlayAsync(CancellationToken cancellationToken)
    {
        await PlayNotifySoundAsync(cancellationToken);
    }

    public async Task PlayNotifySoundAsync(CancellationToken cancellationToken)
    {
        var dingPath = GetSoundPath(DingFileName);
        var voicePath = GetSoundPath(VoiceFileName);

        await Task.WhenAll(
            PlayFileIfExistsAsync(dingPath, "ding", cancellationToken),
            PlayFileIfExistsAsync(voicePath, "voice", cancellationToken));
    }

    private async Task PlayFileIfExistsAsync(string filePath, string soundName, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            LogEmitted?.Invoke($"ไม่พบไฟล์เสียง {soundName}: {filePath}");
            return;
        }

        try
        {
            await PlayFileAsync(filePath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogEmitted?.Invoke($"เล่นไฟล์เสียง {soundName} ไม่สำเร็จ: {ex.Message}");
        }
    }

    private static async Task PlayFileAsync(string filePath, CancellationToken cancellationToken)
    {
        using var audioFile = new AudioFileReader(filePath);
        using var outputDevice = new WaveOutEvent();
        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        outputDevice.PlaybackStopped += (_, args) =>
        {
            if (args.Exception is not null)
            {
                completionSource.TrySetException(args.Exception);
                return;
            }

            completionSource.TrySetResult();
        };

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                outputDevice.Stop();
            }
            catch
            {
                // ไม่ต้องทำอะไรเพิ่มใน callback ของ cancellation
            }

            completionSource.TrySetCanceled(cancellationToken);
        });

        outputDevice.Init(audioFile);
        outputDevice.Play();

        await completionSource.Task;
    }

    private static string GetSoundPath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "assets", "sounds", fileName);
    }
}
