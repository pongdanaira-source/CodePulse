using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodePulse.Enums;
using CodePulse.Models;

namespace CodePulse.Services;

public sealed class SettingsStore
{
    private const string ProtectedValuePrefix = "dpapi:v1:";
    private const string PasswordProtectedValuePrefix = "pbes:v1:";
    private const int PasswordSaltSize = 16;
    private const int PasswordNonceSize = 12;
    private const int PasswordTagSize = 16;
    private const int PasswordKeySize = 32;
    private const int PasswordDerivationIterations = 120_000;
    private static readonly byte[] ProtectionEntropy = Encoding.UTF8.GetBytes("CodePulse.Settings.v1");

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

    public string? LastLoadFailureMessage { get; private set; }

    public AppSettings Load()
    {
        LastLoadFailureMessage = null;
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            var shouldMigrateSecrets = HasUnprotectedSecrets(settings);
            UnprotectSecrets(settings);
            NormalizeTransientChannelState(settings);
            if (shouldMigrateSecrets)
            {
                Save(settings);
            }

            return settings;
        }
        catch (Exception ex)
        {
            LastLoadFailureMessage = BackupInvalidSettingsFile(ex);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var json = SerializeForStorage(settings);
        File.WriteAllText(_settingsPath, json);
    }

    public static string Serialize(AppSettings settings)
    {
        return JsonSerializer.Serialize(settings, JsonOptions);
    }

    public static string SerializeForExport(AppSettings settings, string exportPassword)
    {
        if (string.IsNullOrWhiteSpace(exportPassword))
        {
            throw new ArgumentException("Export password is required.", nameof(exportPassword));
        }

        var exportSettings = CloneSettings(settings);
        ProtectSecretsForExport(exportSettings, exportPassword);
        return JsonSerializer.Serialize(exportSettings, JsonOptions);
    }

    public static bool TryDeserialize(string json, out AppSettings settings)
    {
        return TryDeserialize(json, out settings, exportPassword: null);
    }

    public static bool TryDeserialize(string json, out AppSettings settings, string? exportPassword)
    {
        settings = new AppSettings();

        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            UnprotectSecrets(settings, exportPassword);
            NormalizeTransientChannelState(settings);
            return true;
        }
        catch
        {
            settings = new AppSettings();
            return false;
        }
    }

    public static bool ContainsPasswordProtectedSecrets(string json)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return settings is not null && HasPasswordProtectedSecrets(settings);
        }
        catch
        {
            return false;
        }
    }

    private static string SerializeForStorage(AppSettings settings)
    {
        var storageSettings = CloneSettings(settings);
        ProtectSecrets(storageSettings);
        return JsonSerializer.Serialize(storageSettings, JsonOptions);
    }

    private string BackupInvalidSettingsFile(Exception exception)
    {
        try
        {
            var backupPath = Path.Combine(
                AppFolderPath,
                $"settings.invalid-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(_settingsPath, backupPath, overwrite: false);
            return $"Settings file could not be loaded and was backed up to {backupPath}: {exception.Message}";
        }
        catch (Exception backupException)
        {
            return $"Settings file could not be loaded and backup failed: {exception.Message}; backup: {backupException.Message}";
        }
    }

    private static void ProtectSecrets(AppSettings settings)
    {
        settings.Dispatch.TelegramBotToken = ProtectSecret(settings.Dispatch.TelegramBotToken);
        settings.Dispatch.TelegramChatId = ProtectSecret(settings.Dispatch.TelegramChatId);
        settings.OcrSpaceApiKey = ProtectSecret(settings.OcrSpaceApiKey);
        settings.YouTubeApiKey = ProtectSecret(settings.YouTubeApiKey);
        settings.YouTubeApiBackupKeys = settings.YouTubeApiBackupKeys
            .Select(ProtectSecret)
            .ToList();
    }

    private static void ProtectSecretsForExport(AppSettings settings, string exportPassword)
    {
        settings.Dispatch.TelegramBotToken = ProtectSecretForExport(settings.Dispatch.TelegramBotToken, exportPassword);
        settings.Dispatch.TelegramChatId = ProtectSecretForExport(settings.Dispatch.TelegramChatId, exportPassword);
        settings.OcrSpaceApiKey = ProtectSecretForExport(settings.OcrSpaceApiKey, exportPassword);
        settings.YouTubeApiKey = ProtectSecretForExport(settings.YouTubeApiKey, exportPassword);
        settings.YouTubeApiBackupKeys = settings.YouTubeApiBackupKeys
            .Select(key => ProtectSecretForExport(key, exportPassword))
            .ToList();
    }

    private static void UnprotectSecrets(AppSettings settings, string? exportPassword = null)
    {
        settings.Dispatch.TelegramBotToken = UnprotectSecret(settings.Dispatch.TelegramBotToken, exportPassword);
        settings.Dispatch.TelegramChatId = UnprotectSecret(settings.Dispatch.TelegramChatId, exportPassword);
        settings.OcrSpaceApiKey = UnprotectSecret(settings.OcrSpaceApiKey, exportPassword);
        settings.YouTubeApiKey = UnprotectSecret(settings.YouTubeApiKey, exportPassword);
        settings.YouTubeApiBackupKeys = settings.YouTubeApiBackupKeys
            .Select(key => UnprotectSecret(key, exportPassword))
            .ToList();
    }

    private static bool HasPasswordProtectedSecrets(AppSettings settings)
    {
        return IsPasswordProtectedSecret(settings.Dispatch.TelegramBotToken) ||
               IsPasswordProtectedSecret(settings.Dispatch.TelegramChatId) ||
               IsPasswordProtectedSecret(settings.OcrSpaceApiKey) ||
               IsPasswordProtectedSecret(settings.YouTubeApiKey) ||
               settings.YouTubeApiBackupKeys.Any(IsPasswordProtectedSecret);
    }

    private static bool HasUnprotectedSecrets(AppSettings settings)
    {
        return IsUnprotectedSecret(settings.Dispatch.TelegramBotToken) ||
               IsUnprotectedSecret(settings.Dispatch.TelegramChatId) ||
               IsUnprotectedSecret(settings.OcrSpaceApiKey) ||
               IsUnprotectedSecret(settings.YouTubeApiKey) ||
               settings.YouTubeApiBackupKeys.Any(IsUnprotectedSecret);
    }

    private static string ProtectSecret(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith(ProtectedValuePrefix, StringComparison.Ordinal))
        {
            return value;
        }

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value),
            ProtectionEntropy,
            DataProtectionScope.CurrentUser);
        return ProtectedValuePrefix + Convert.ToBase64String(protectedBytes);
    }

    private static string ProtectSecretForExport(string value, string exportPassword)
    {
        if (string.IsNullOrWhiteSpace(value) || IsPasswordProtectedSecret(value))
        {
            return value;
        }

        var plaintext = IsMachineProtectedSecret(value)
            ? UnprotectSecret(value)
            : value;
        var salt = RandomNumberGenerator.GetBytes(PasswordSaltSize);
        var nonce = RandomNumberGenerator.GetBytes(PasswordNonceSize);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plaintextBytes.Length];
        var tag = new byte[PasswordTagSize];
        var key = DeriveExportKey(exportPassword, salt);

        using (var aes = new AesGcm(key, PasswordTagSize))
        {
            aes.Encrypt(nonce, plaintextBytes, cipherBytes, tag);
        }

        var payload = new byte[salt.Length + nonce.Length + tag.Length + cipherBytes.Length];
        Buffer.BlockCopy(salt, 0, payload, 0, salt.Length);
        Buffer.BlockCopy(nonce, 0, payload, salt.Length, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, salt.Length + nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherBytes, 0, payload, salt.Length + nonce.Length + tag.Length, cipherBytes.Length);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(plaintextBytes);

        return PasswordProtectedValuePrefix + Convert.ToBase64String(payload);
    }

    private static string UnprotectSecret(string value, string? exportPassword = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (IsPasswordProtectedSecret(value))
        {
            if (string.IsNullOrWhiteSpace(exportPassword))
            {
                throw new CryptographicException("This settings export contains password-protected secrets.");
            }

            return UnprotectSecretForExport(value, exportPassword);
        }

        if (!IsMachineProtectedSecret(value))
        {
            return value;
        }

        var payload = value[ProtectedValuePrefix.Length..];
        var protectedBytes = Convert.FromBase64String(payload);
        var unprotectedBytes = ProtectedData.Unprotect(
            protectedBytes,
            ProtectionEntropy,
            DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(unprotectedBytes);
    }

    private static string UnprotectSecretForExport(string value, string exportPassword)
    {
        var payload = Convert.FromBase64String(value[PasswordProtectedValuePrefix.Length..]);
        var minimumLength = PasswordSaltSize + PasswordNonceSize + PasswordTagSize;
        if (payload.Length < minimumLength)
        {
            throw new CryptographicException("The encrypted secret payload is invalid.");
        }

        var salt = payload[..PasswordSaltSize];
        var nonce = payload[PasswordSaltSize..(PasswordSaltSize + PasswordNonceSize)];
        var tagStart = PasswordSaltSize + PasswordNonceSize;
        var tag = payload[tagStart..(tagStart + PasswordTagSize)];
        var cipherBytes = payload[(tagStart + PasswordTagSize)..];
        var plaintextBytes = new byte[cipherBytes.Length];
        var key = DeriveExportKey(exportPassword, salt);

        using (var aes = new AesGcm(key, PasswordTagSize))
        {
            aes.Decrypt(nonce, cipherBytes, tag, plaintextBytes);
        }

        var plaintext = Encoding.UTF8.GetString(plaintextBytes);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(plaintextBytes);
        return plaintext;
    }

    private static byte[] DeriveExportKey(string password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            PasswordDerivationIterations,
            HashAlgorithmName.SHA256,
            PasswordKeySize);
    }

    private static bool IsMachineProtectedSecret(string value)
    {
        return value.StartsWith(ProtectedValuePrefix, StringComparison.Ordinal);
    }

    private static bool IsPasswordProtectedSecret(string value)
    {
        return value.StartsWith(PasswordProtectedValuePrefix, StringComparison.Ordinal);
    }

    private static bool IsUnprotectedSecret(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               !IsMachineProtectedSecret(value) &&
               !IsPasswordProtectedSecret(value);
    }

    private static AppSettings CloneSettings(AppSettings source)
    {
        return new AppSettings
        {
            Dispatch = new DispatchSettings
            {
                TelegramBotToken = source.Dispatch.TelegramBotToken,
                TelegramChatId = source.Dispatch.TelegramChatId,
                EnableLine = source.Dispatch.EnableLine,
                EnableFacebook = source.Dispatch.EnableFacebook,
                BlockDesktopDispatchForOcr = source.Dispatch.BlockDesktopDispatchForOcr,
                EnableSound = source.Dispatch.EnableSound,
                SkipIfWindowNotFound = source.Dispatch.SkipIfWindowNotFound,
                PasteDelayMs = source.Dispatch.PasteDelayMs,
                EnterAfterPaste = source.Dispatch.EnterAfterPaste,
                EnableSafeDesktopPaste = source.Dispatch.EnableSafeDesktopPaste,
                EnableDesktopTargetVerification = false,
                LineTargetTitleKeyword = source.Dispatch.LineTargetTitleKeyword,
                LineTargetWindowTitle = source.Dispatch.LineTargetWindowTitle,
                FacebookTargetTitleKeyword = source.Dispatch.FacebookTargetTitleKeyword,
                FacebookTargetUrl = source.Dispatch.FacebookTargetUrl,
                EnableDryRun = source.Dispatch.EnableDryRun,
                SendManualCaptureImage = source.Dispatch.SendManualCaptureImage,
                SaveManualCaptureImageToTempInDryRun = source.Dispatch.SaveManualCaptureImageToTempInDryRun
            },
            EnableOcrDebugLog = source.EnableOcrDebugLog,
            EnableOcrSpaceFallback = source.EnableOcrSpaceFallback,
            OcrSpaceApiKey = source.OcrSpaceApiKey,
            OcrSpaceLanguage = source.OcrSpaceLanguage,
            YouTubeApiKey = source.YouTubeApiKey,
            YouTubeApiBackupKeys = source.YouTubeApiBackupKeys.ToList(),
            YouTubeApiDailyQuotaGuardUnits = source.YouTubeApiDailyQuotaGuardUnits,
            YouTubeApiHealthCheckLastRunDate = source.YouTubeApiHealthCheckLastRunDate,
            OcrSpaceDailyRequestGuard = source.OcrSpaceDailyRequestGuard,
            OcrSpaceHourlyRequestGuard = source.OcrSpaceHourlyRequestGuard,
            BoostTimeoutSeconds = source.BoostTimeoutSeconds,
            CommentScannerLastVideoUrls = new Dictionary<Guid, string>(source.CommentScannerLastVideoUrls),
            Channels = source.Channels.Select(CloneChannel).ToList()
        };
    }

    private static ChannelProfile CloneChannel(ChannelProfile source)
    {
        return new ChannelProfile
        {
            Id = source.Id,
            Name = source.Name,
            ChatLink = source.ChatLink,
            Enabled = source.Enabled,
            Prefixes = source.Prefixes.ToList(),
            PrefixOnly = source.PrefixOnly,
            LastCaptureRegion = source.LastCaptureRegion,
            EnableAutoScan = source.EnableAutoScan,
            AutoScanIntervalMs = source.AutoScanIntervalMs,
            Status = source.Status,
            LastStatusMessage = source.LastStatusMessage,
            LastCheckedAt = source.LastCheckedAt
        };
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
