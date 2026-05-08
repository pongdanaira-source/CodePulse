using System.Text.Json;

namespace CodePulse.Services;

public sealed class ApiUsageTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string? _storagePath;
    private readonly object _sync = new();
    private DateOnly _youtubeUsageDate = DateOnly.FromDateTime(DateTime.Today);
    private DateOnly _ocrSpaceUsageDate = DateOnly.FromDateTime(DateTime.Today);
    private DateTime _ocrSpaceUsageHour = TruncateToHour(DateTime.Now);
    private int _youtubeDailyUnits;
    private int _ocrSpaceDailyRequests;
    private int _ocrSpaceHourlyRequests;

    public ApiUsageTracker(string? storagePath = null)
    {
        _storagePath = string.IsNullOrWhiteSpace(storagePath)
            ? null
            : storagePath;
        Load();
        ResetPeriodsIfNeeded();
        Persist();
    }

    public ApiUsageSnapshot Snapshot()
    {
        lock (_sync)
        {
            ResetPeriodsIfNeeded();
            return new ApiUsageSnapshot(
                _youtubeUsageDate,
                _youtubeDailyUnits,
                _ocrSpaceUsageDate,
                _ocrSpaceDailyRequests,
                _ocrSpaceUsageHour,
                _ocrSpaceHourlyRequests);
        }
    }

    public bool TryReserveYouTubeUnits(int units, int dailyLimit, out ApiUsageSnapshot snapshot)
    {
        lock (_sync)
        {
            ResetPeriodsIfNeeded();
            if (dailyLimit > 0 && _youtubeDailyUnits + units > dailyLimit)
            {
                snapshot = CreateSnapshot();
                return false;
            }

            _youtubeDailyUnits += units;
            snapshot = CreateSnapshot();
            Persist();
            return true;
        }
    }

    public bool TryReserveOcrSpaceRequest(int dailyLimit, int hourlyLimit, out ApiUsageSnapshot snapshot)
    {
        lock (_sync)
        {
            ResetPeriodsIfNeeded();
            if (dailyLimit > 0 && _ocrSpaceDailyRequests + 1 > dailyLimit)
            {
                snapshot = CreateSnapshot();
                return false;
            }

            if (hourlyLimit > 0 && _ocrSpaceHourlyRequests + 1 > hourlyLimit)
            {
                snapshot = CreateSnapshot();
                return false;
            }

            _ocrSpaceDailyRequests++;
            _ocrSpaceHourlyRequests++;
            snapshot = CreateSnapshot();
            Persist();
            return true;
        }
    }

    public ApiUsageSnapshot Reset()
    {
        lock (_sync)
        {
            _youtubeUsageDate = DateOnly.FromDateTime(DateTime.Today);
            _ocrSpaceUsageDate = DateOnly.FromDateTime(DateTime.Today);
            _ocrSpaceUsageHour = TruncateToHour(DateTime.Now);
            _youtubeDailyUnits = 0;
            _ocrSpaceDailyRequests = 0;
            _ocrSpaceHourlyRequests = 0;
            Persist();
            return CreateSnapshot();
        }
    }

    private ApiUsageSnapshot CreateSnapshot()
    {
        return new ApiUsageSnapshot(
            _youtubeUsageDate,
            _youtubeDailyUnits,
            _ocrSpaceUsageDate,
            _ocrSpaceDailyRequests,
            _ocrSpaceUsageHour,
            _ocrSpaceHourlyRequests);
    }

    private void ResetPeriodsIfNeeded()
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        var changed = false;
        if (_youtubeUsageDate != today)
        {
            _youtubeUsageDate = today;
            _youtubeDailyUnits = 0;
            changed = true;
        }

        if (_ocrSpaceUsageDate != today)
        {
            _ocrSpaceUsageDate = today;
            _ocrSpaceDailyRequests = 0;
            changed = true;
        }

        var currentHour = TruncateToHour(now);
        if (_ocrSpaceUsageHour != currentHour)
        {
            _ocrSpaceUsageHour = currentHour;
            _ocrSpaceHourlyRequests = 0;
            changed = true;
        }

        if (changed)
        {
            Persist();
        }
    }

    private static DateTime TruncateToHour(DateTime value)
    {
        return new DateTime(value.Year, value.Month, value.Day, value.Hour, 0, 0, value.Kind);
    }

    private void Load()
    {
        if (string.IsNullOrWhiteSpace(_storagePath) || !File.Exists(_storagePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_storagePath);
            var stored = JsonSerializer.Deserialize<StoredApiUsageCounters>(json, JsonOptions);
            if (stored is null)
            {
                return;
            }

            _youtubeUsageDate = stored.YouTubeUsageDate;
            _youtubeDailyUnits = Math.Max(0, stored.YouTubeDailyUnits);
            _ocrSpaceUsageDate = stored.OcrSpaceUsageDate;
            _ocrSpaceDailyRequests = Math.Max(0, stored.OcrSpaceDailyRequests);
            _ocrSpaceUsageHour = stored.OcrSpaceUsageHour;
            _ocrSpaceHourlyRequests = Math.Max(0, stored.OcrSpaceHourlyRequests);
        }
        catch
        {
            // Keep quota protection best-effort; a bad counter file should not block app startup.
        }
    }

    private void Persist()
    {
        if (string.IsNullOrWhiteSpace(_storagePath))
        {
            return;
        }

        try
        {
            var directoryPath = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            var stored = new StoredApiUsageCounters
            {
                YouTubeUsageDate = _youtubeUsageDate,
                YouTubeDailyUnits = _youtubeDailyUnits,
                OcrSpaceUsageDate = _ocrSpaceUsageDate,
                OcrSpaceDailyRequests = _ocrSpaceDailyRequests,
                OcrSpaceUsageHour = _ocrSpaceUsageHour,
                OcrSpaceHourlyRequests = _ocrSpaceHourlyRequests
            };
            File.WriteAllText(_storagePath, JsonSerializer.Serialize(stored, JsonOptions));
        }
        catch
        {
            // Runtime counters are advisory; failures fall back to in-memory counting.
        }
    }

    private sealed class StoredApiUsageCounters
    {
        public DateOnly YouTubeUsageDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

        public int YouTubeDailyUnits { get; set; }

        public DateOnly OcrSpaceUsageDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

        public int OcrSpaceDailyRequests { get; set; }

        public DateTime OcrSpaceUsageHour { get; set; } = TruncateToHour(DateTime.Now);

        public int OcrSpaceHourlyRequests { get; set; }
    }
}

public readonly record struct ApiUsageSnapshot(
    DateOnly YouTubeUsageDate,
    int YouTubeDailyUnits,
    DateOnly OcrSpaceUsageDate,
    int OcrSpaceDailyRequests,
    DateTime OcrSpaceUsageHour,
    int OcrSpaceHourlyRequests);
