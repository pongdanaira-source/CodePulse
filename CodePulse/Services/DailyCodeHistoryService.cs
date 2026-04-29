namespace CodePulse.Services;

public sealed class DailyCodeHistoryService
{
    private static readonly TimeSpan BusinessDayStartTime = TimeSpan.FromHours(3);

    private readonly string _historyRootPath;
    private readonly object _sync = new();
    private readonly Dictionary<string, HashSet<string>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public DailyCodeHistoryService(string? appFolderPath = null)
    {
        var appFolder = Path.Combine(
            string.IsNullOrWhiteSpace(appFolderPath)
                ? SettingsStore.GetDefaultAppFolderPath()
                : appFolderPath,
            "history");
        Directory.CreateDirectory(appFolder);
        _historyRootPath = appFolder;
    }

    public bool ContainsForCurrentBusinessDay(Guid channelId, string code, DateTimeOffset now)
    {
        var dayKey = BuildDayKey(channelId, now);
        var normalizedCode = NormalizeCode(code);

        lock (_sync)
        {
            var history = LoadIfNeeded(dayKey);
            return history.Contains(normalizedCode);
        }
    }

    public void RegisterForCurrentBusinessDay(Guid channelId, string code, DateTimeOffset now)
    {
        var dayKey = BuildDayKey(channelId, now);
        var normalizedCode = NormalizeCode(code);
        var filePath = GetFilePath(channelId, GetBusinessDate(now));

        lock (_sync)
        {
            var history = LoadIfNeeded(dayKey);
            if (!history.Add(normalizedCode))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.AppendAllText(
                filePath,
                $"{now:yyyy-MM-ddTHH:mm:sszzz}|{normalizedCode}{Environment.NewLine}");
        }
    }

    public IReadOnlyList<string> FindContainedCodesForCurrentBusinessDay(Guid channelId, string text, DateTimeOffset now)
    {
        var dayKey = BuildDayKey(channelId, now);
        var normalizedText = NormalizeCode(text);

        lock (_sync)
        {
            var history = LoadIfNeeded(dayKey);
            return history
                .Where(code => !string.IsNullOrWhiteSpace(code) && normalizedText.Contains(code, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static code => code.Length)
                .ThenBy(static code => code, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private HashSet<string> LoadIfNeeded(string dayKey)
    {
        if (_cache.TryGetValue(dayKey, out var existing))
        {
            return existing;
        }

        var parts = dayKey.Split('|', 2);
        var channelId = Guid.Parse(parts[0]);
        var businessDate = DateOnly.ParseExact(parts[1], "yyyy-MM-dd");
        var filePath = GetFilePath(channelId, businessDate);
        var history = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(filePath))
        {
            foreach (var line in File.ReadLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var separatorIndex = line.IndexOf('|');
                var code = separatorIndex >= 0 ? line[(separatorIndex + 1)..] : line;
                code = NormalizeCode(code);
                if (!string.IsNullOrWhiteSpace(code))
                {
                    history.Add(code);
                }
            }
        }

        _cache[dayKey] = history;
        return history;
    }

    private string BuildDayKey(Guid channelId, DateTimeOffset now)
    {
        return $"{channelId}|{GetBusinessDate(now):yyyy-MM-dd}";
    }

    private string GetFilePath(Guid channelId, DateOnly businessDate)
    {
        return Path.Combine(_historyRootPath, channelId.ToString("N"), $"{businessDate:yyyy-MM-dd}.log");
    }

    private static DateOnly GetBusinessDate(DateTimeOffset now)
    {
        var localDateTime = now.LocalDateTime;
        if (localDateTime.TimeOfDay < BusinessDayStartTime)
        {
            localDateTime = localDateTime.AddDays(-1);
        }

        return DateOnly.FromDateTime(localDateTime.Date);
    }

    private static string NormalizeCode(string code)
    {
        return code.Trim().ToUpperInvariant();
    }
}
