namespace CodePulse.Services;

public sealed class ApiUsageTracker
{
    private readonly object _sync = new();
    private DateOnly _youtubeUsageDate = DateOnly.FromDateTime(DateTime.Today);
    private DateOnly _ocrSpaceUsageDate = DateOnly.FromDateTime(DateTime.Today);
    private DateTime _ocrSpaceUsageHour = TruncateToHour(DateTime.Now);
    private int _youtubeDailyUnits;
    private int _ocrSpaceDailyRequests;
    private int _ocrSpaceHourlyRequests;

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
        if (_youtubeUsageDate != today)
        {
            _youtubeUsageDate = today;
            _youtubeDailyUnits = 0;
        }

        if (_ocrSpaceUsageDate != today)
        {
            _ocrSpaceUsageDate = today;
            _ocrSpaceDailyRequests = 0;
        }

        var currentHour = TruncateToHour(now);
        if (_ocrSpaceUsageHour != currentHour)
        {
            _ocrSpaceUsageHour = currentHour;
            _ocrSpaceHourlyRequests = 0;
        }
    }

    private static DateTime TruncateToHour(DateTime value)
    {
        return new DateTime(value.Year, value.Month, value.Day, value.Hour, 0, 0, value.Kind);
    }
}

public readonly record struct ApiUsageSnapshot(
    DateOnly YouTubeUsageDate,
    int YouTubeDailyUnits,
    DateOnly OcrSpaceUsageDate,
    int OcrSpaceDailyRequests,
    DateTime OcrSpaceUsageHour,
    int OcrSpaceHourlyRequests);
