using CodePulse.Models;
using CodePulse.Services;
using Xunit;

namespace CodePulse.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void Save_ProtectsSecretsInLocalSettingsFile()
    {
        using var directory = new TempDirectory();
        var store = new SettingsStore(directory.Path);
        var settings = CreateSettings();

        store.Save(settings);

        var storedJson = File.ReadAllText(store.SettingsPath);
        Assert.DoesNotContain("telegram-token", storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("-100123456", storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ocr-key", storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("youtube-key", storedJson, StringComparison.Ordinal);
        Assert.Contains("dpapi:v1:", storedJson, StringComparison.Ordinal);

        var loaded = store.Load();
        Assert.Equal("telegram-token", loaded.Dispatch.TelegramBotToken);
        Assert.Equal("-100123456", loaded.Dispatch.TelegramChatId);
        Assert.Equal("ocr-key", loaded.OcrSpaceApiKey);
        Assert.Equal("youtube-key", loaded.YouTubeApiKey);
        Assert.Equal(["backup-one", "backup-two"], loaded.YouTubeApiBackupKeys);
    }

    [Fact]
    public void Load_MigratesPlaintextSecretsToProtectedLocalSettingsFile()
    {
        using var directory = new TempDirectory();
        var store = new SettingsStore(directory.Path);
        File.WriteAllText(store.SettingsPath, SettingsStore.Serialize(CreateSettings()));

        var loaded = store.Load();

        Assert.Equal("telegram-token", loaded.Dispatch.TelegramBotToken);
        Assert.Equal("-100123456", loaded.Dispatch.TelegramChatId);

        var migratedJson = File.ReadAllText(store.SettingsPath);
        Assert.DoesNotContain("telegram-token", migratedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("-100123456", migratedJson, StringComparison.Ordinal);
        Assert.Contains("dpapi:v1:", migratedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeForExport_EncryptsSecretsWithPassword()
    {
        var exportedJson = SettingsStore.SerializeForExport(CreateSettings(), "correct horse battery staple");

        Assert.DoesNotContain("telegram-token", exportedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("-100123456", exportedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ocr-key", exportedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("youtube-key", exportedJson, StringComparison.Ordinal);
        Assert.Contains("pbes:v1:", exportedJson, StringComparison.Ordinal);
        Assert.True(SettingsStore.ContainsPasswordProtectedSecrets(exportedJson));
    }

    [Fact]
    public void TryDeserialize_WithCorrectPassword_UnlocksExportedSecrets()
    {
        var exportedJson = SettingsStore.SerializeForExport(CreateSettings(), "move-password");

        var success = SettingsStore.TryDeserialize(exportedJson, out var imported, "move-password");

        Assert.True(success);
        Assert.Equal("telegram-token", imported.Dispatch.TelegramBotToken);
        Assert.Equal("-100123456", imported.Dispatch.TelegramChatId);
        Assert.Equal("ocr-key", imported.OcrSpaceApiKey);
        Assert.Equal("youtube-key", imported.YouTubeApiKey);
        Assert.Equal(["backup-one", "backup-two"], imported.YouTubeApiBackupKeys);
    }

    [Fact]
    public void TryDeserialize_WithWrongPassword_FailsClosed()
    {
        var exportedJson = SettingsStore.SerializeForExport(CreateSettings(), "right-password");

        var success = SettingsStore.TryDeserialize(exportedJson, out var imported, "wrong-password");

        Assert.False(success);
        Assert.Empty(imported.Dispatch.TelegramBotToken);
        Assert.Empty(imported.Dispatch.TelegramChatId);
    }

    private static AppSettings CreateSettings()
    {
        return new AppSettings
        {
            Dispatch = new DispatchSettings
            {
                TelegramBotToken = "telegram-token",
                TelegramChatId = "-100123456",
                EnableSound = true,
                EnableLine = true,
                PasteDelayMs = 150,
                EnterAfterPaste = true
            },
            EnableOcrSpaceFallback = true,
            OcrSpaceApiKey = "ocr-key",
            YouTubeApiKey = "youtube-key",
            YouTubeApiBackupKeys = ["backup-one", "backup-two"],
            Channels =
            [
                new ChannelProfile
                {
                    Id = Guid.NewGuid(),
                    Name = "Test channel",
                    Enabled = true,
                    Prefixes = ["ABC"]
                }
            ]
        };
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodePulseTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temp test data.
            }
        }
    }
}
