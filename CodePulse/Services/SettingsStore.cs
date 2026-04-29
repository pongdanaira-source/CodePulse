using System.Text.Json;
using CodePulse.Enums;
using CodePulse.Models;

namespace CodePulse.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public SettingsStore(string? appFolderPath = null)
    {
        var appFolder = string.IsNullOrWhiteSpace(appFolderPath)
            ? GetDefaultAppFolderPath()
            : appFolderPath;
        Directory.CreateDirectory(appFolder);
        _settingsPath = Path.Combine(appFolder, "settings.json");
        SeedFromDefaultIfNeeded(appFolderPath);
    }

    public string SettingsPath => _settingsPath;

    public string AppFolderPath => Path.GetDirectoryName(_settingsPath)
        ?? GetDefaultAppFolderPath();

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            NormalizeTransientChannelState(settings);
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var json = Serialize(settings);
        File.WriteAllText(_settingsPath, json);
    }

    public static string Serialize(AppSettings settings)
    {
        return JsonSerializer.Serialize(settings, JsonOptions);
    }

    public static bool TryDeserialize(string json, out AppSettings settings)
    {
        settings = new AppSettings();

        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            NormalizeTransientChannelState(settings);
            return true;
        }
        catch
        {
            settings = new AppSettings();
            return false;
        }
    }

    private void SeedFromDefaultIfNeeded(string? appFolderPath)
    {
        if (string.IsNullOrWhiteSpace(appFolderPath) || File.Exists(_settingsPath))
        {
            return;
        }

        var defaultSettingsPath = Path.Combine(GetDefaultAppFolderPath(), "settings.json");
        if (!File.Exists(defaultSettingsPath))
        {
            return;
        }

        File.Copy(defaultSettingsPath, _settingsPath, overwrite: false);
    }

    private static void NormalizeTransientChannelState(AppSettings settings)
    {
        foreach (var channel in settings.Channels)
        {
            channel.EnableAutoScan = false;
            channel.Status = SessionState.Idle;
            channel.LastStatusMessage = "พร้อม";
            channel.LastCheckedAt = null;
        }
    }

    public static string GetDefaultAppFolderPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CodePulse");
    }
}
