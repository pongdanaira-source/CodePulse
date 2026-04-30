using System.Text.Json;
using CodePulse.Models;

namespace CodePulse.Services;

public sealed class YouTubeCommentScannerService
{
    private const int InitialMaxResults = 100;
    private const int PollMaxResults = 20;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MinimumPollInterval = TimeSpan.FromSeconds(1);

    private readonly AppSettings _settings;
    private readonly AppLogService _appLogService;
    private readonly WatchCoordinator _watchCoordinator;
    private readonly HttpClient _httpClient = new();
    private readonly Dictionary<Guid, CommentScannerRuntime> _runningScanners = new();
    private readonly HashSet<string> _failedApiKeys = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public YouTubeCommentScannerService(
        AppSettings settings,
        AppLogService appLogService,
        WatchCoordinator watchCoordinator)
    {
        _settings = settings;
        _appLogService = appLogService;
        _watchCoordinator = watchCoordinator;
    }

    public bool IsRunning(Guid channelId)
    {
        lock (_sync)
        {
            return _runningScanners.ContainsKey(channelId);
        }
    }

    public bool Start(ChannelProfile channel, string videoUrlOrId, TimeSpan pollInterval, Action refreshChannels)
    {
        if (GetUsableApiKeys().Count == 0)
        {
            _appLogService.Write("[Comment Scanner] ยังไม่ได้ตั้งค่า YouTube API key");
            return false;
        }

        if (!TryExtractVideoId(videoUrlOrId, out var videoId))
        {
            _appLogService.Write("[Comment Scanner] ลิงก์หรือ video id ไม่ถูกต้อง");
            return false;
        }

        Stop(channel.Id, refreshChannels, emitLog: false);

        var source = new CancellationTokenSource();
        var effectivePollInterval = pollInterval < MinimumPollInterval ? MinimumPollInterval : pollInterval;
        var runtime = new CommentScannerRuntime(channel, videoId, effectivePollInterval, source);
        lock (_sync)
        {
            _runningScanners[channel.Id] = runtime;
        }

        _appLogService.Write($"[{channel.Name}] เริ่มดักคอมเมนต์เจ้าของช่องทุก {effectivePollInterval.TotalSeconds:0} วิ: {videoId}");
        runtime.Task = Task.Run(() => RunScannerAsync(runtime, refreshChannels), CancellationToken.None);
        return true;
    }

    public void Stop(ChannelProfile channel, Action refreshChannels, bool emitLog = true)
    {
        Stop(channel.Id, refreshChannels, emitLog);
    }

    public void StopAll()
    {
        List<CommentScannerRuntime> runtimes;
        lock (_sync)
        {
            runtimes = _runningScanners.Values.ToList();
            _runningScanners.Clear();
        }

        foreach (var runtime in runtimes)
        {
            runtime.CancellationTokenSource.Cancel();
        }
    }

    private void Stop(Guid channelId, Action refreshChannels, bool emitLog)
    {
        CommentScannerRuntime? runtime;
        lock (_sync)
        {
            _runningScanners.TryGetValue(channelId, out runtime);
            _runningScanners.Remove(channelId);
        }

        if (runtime is null)
        {
            return;
        }

        runtime.CancellationTokenSource.Cancel();
        if (emitLog)
        {
            _appLogService.Write($"[{runtime.Channel.Name}] หยุดดักคอมเมนต์");
        }

        refreshChannels();
    }

    private async Task RunScannerAsync(CommentScannerRuntime runtime, Action refreshChannels)
    {
        try
        {
            await EnsureOwnerChannelIdAsync(runtime, runtime.CancellationTokenSource.Token);
            await PollOnceAsync(runtime, InitialMaxResults, runtime.CancellationTokenSource.Token);

            using var timer = new PeriodicTimer(runtime.PollInterval);
            while (await timer.WaitForNextTickAsync(runtime.CancellationTokenSource.Token))
            {
                await PollOnceAsync(runtime, PollMaxResults, runtime.CancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop.
        }
        catch (Exception ex)
        {
            _appLogService.Write($"[{runtime.Channel.Name}] Comment scanner ล้มเหลว: {Summarize(ex)}");
        }
        finally
        {
            lock (_sync)
            {
                if (_runningScanners.TryGetValue(runtime.Channel.Id, out var current) &&
                    ReferenceEquals(current, runtime))
                {
                    _runningScanners.Remove(runtime.Channel.Id);
                }
            }

            refreshChannels();
        }
    }

    private async Task PollOnceAsync(CommentScannerRuntime runtime, int maxResults, CancellationToken cancellationToken)
    {
        await EnsureOwnerChannelIdAsync(runtime, cancellationToken);

        var content = await GetYouTubeApiJsonAsync(
            key => BuildCommentThreadsUrl(runtime.VideoId, maxResults, key),
            cancellationToken);

        var comments = ParseComments(content);
        var ownerComments = comments
            .Where(comment => string.Equals(comment.AuthorChannelId, runtime.OwnerChannelId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var processedOwnerComments = 0;
        var dispatchedCodes = new List<string>();
        foreach (var comment in ownerComments.OrderBy(static comment => comment.PublishedAt ?? DateTimeOffset.MinValue))
        {
            if (string.IsNullOrWhiteSpace(comment.Id) || string.IsNullOrWhiteSpace(comment.Text))
            {
                continue;
            }

            if (runtime.SeenComments.TryGetValue(comment.Id, out var previousUpdatedAt) &&
                previousUpdatedAt == comment.UpdatedAt)
            {
                continue;
            }

            runtime.SeenComments[comment.Id] = comment.UpdatedAt;
            processedOwnerComments++;
            _appLogService.Write($"[{runtime.Channel.Name}] พบคอมเมนต์เจ้าของช่อง: {TrimForLog(comment.Text)}");

            var result = await _watchCoordinator.ProcessOwnerTextAsync(runtime.Channel, comment.Text, cancellationToken);
            if (result.Status == OwnerTextProcessingStatus.Dispatched && !string.IsNullOrWhiteSpace(result.Code))
            {
                dispatchedCodes.Add(result.Code);
            }
        }

        _appLogService.Write(
            $"[{runtime.Channel.Name}] Comment scan: fetched {comments.Count}, owner {ownerComments.Count}, new owner {processedOwnerComments}, sent {dispatchedCodes.Count}");
    }

    private static string BuildCommentThreadsUrl(string videoId, int maxResults, string apiKey)
    {
        var query = new Dictionary<string, string>
        {
            ["part"] = "snippet,replies",
            ["videoId"] = videoId,
            ["maxResults"] = maxResults.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["order"] = "time",
            ["textFormat"] = "plainText",
            ["key"] = apiKey
        };

        return "https://www.googleapis.com/youtube/v3/commentThreads?" +
               string.Join("&", query.Select(static item =>
                   $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));
    }

    private async Task EnsureOwnerChannelIdAsync(CommentScannerRuntime runtime, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(runtime.OwnerChannelId))
        {
            return;
        }

        if (TryGetRawYouTubeChannelId(runtime.Channel.ChatLink, out var configuredChannelId))
        {
            runtime.OwnerChannelId = configuredChannelId;
            return;
        }

        runtime.OwnerChannelId = await FetchVideoOwnerChannelIdAsync(runtime.VideoId, cancellationToken);
        _appLogService.Write($"[{runtime.Channel.Name}] ใช้ owner channel id จากวิดีโอ: {runtime.OwnerChannelId}");
    }

    private async Task<string> FetchVideoOwnerChannelIdAsync(string videoId, CancellationToken cancellationToken)
    {
        var content = await GetYouTubeApiJsonAsync(
            key => "https://www.googleapis.com/youtube/v3/videos?" +
                   $"part=snippet&id={Uri.EscapeDataString(videoId)}&key={Uri.EscapeDataString(key)}",
            cancellationToken);

        using var document = JsonDocument.Parse(content);
        var items = document.RootElement.GetProperty("items");
        if (items.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("YouTube video not found");
        }

        return items[0].GetProperty("snippet").GetProperty("channelId").GetString() ?? string.Empty;
    }

    private async Task<string> GetYouTubeApiJsonAsync(
        Func<string, string> buildUrl,
        CancellationToken cancellationToken)
    {
        var apiKeys = GetUsableApiKeys();
        if (apiKeys.Count == 0)
        {
            throw new InvalidOperationException("No usable YouTube API key configured");
        }

        Exception? lastException = null;
        foreach (var apiKey in apiKeys)
        {
            using var response = await _httpClient.GetAsync(buildUrl(apiKey), cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return content;
            }

            var message = $"YouTube API {(int)response.StatusCode}: {TrimForLog(content)}";
            lastException = new InvalidOperationException(message);
            if (!IsKeyOrQuotaFailure(content))
            {
                throw lastException;
            }

            RegisterFailedApiKey(apiKey);
            _appLogService.Write($"[Comment Scanner] YouTube API key failed, switching key: {SummarizeKeyFailure(content)}");
        }

        throw lastException ?? new InvalidOperationException("All YouTube API keys failed");
    }

    private List<string> GetUsableApiKeys()
    {
        var allKeys = new[] { _settings.YouTubeApiKey }
            .Concat(_settings.YouTubeApiBackupKeys)
            .Select(static key => key.Trim())
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        lock (_sync)
        {
            return allKeys
                .Where(key => !_failedApiKeys.Contains(key))
                .ToList();
        }
    }

    private void RegisterFailedApiKey(string apiKey)
    {
        lock (_sync)
        {
            _failedApiKeys.Add(apiKey);
        }
    }

    private static bool IsKeyOrQuotaFailure(string responseBody)
    {
        return responseBody.Contains("quotaExceeded", StringComparison.OrdinalIgnoreCase) ||
               responseBody.Contains("dailyLimitExceeded", StringComparison.OrdinalIgnoreCase) ||
               responseBody.Contains("rateLimitExceeded", StringComparison.OrdinalIgnoreCase) ||
               responseBody.Contains("keyInvalid", StringComparison.OrdinalIgnoreCase) ||
               responseBody.Contains("API key not valid", StringComparison.OrdinalIgnoreCase);
    }

    private static string SummarizeKeyFailure(string responseBody)
    {
        foreach (var reason in new[] { "quotaExceeded", "dailyLimitExceeded", "rateLimitExceeded", "keyInvalid" })
        {
            if (responseBody.Contains(reason, StringComparison.OrdinalIgnoreCase))
            {
                return reason;
            }
        }

        return "key/quota error";
    }

    private static List<YouTubeCommentInfo> ParseComments(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("items", out var itemsElement) ||
            itemsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var comments = new List<YouTubeCommentInfo>();
        foreach (var item in itemsElement.EnumerateArray())
        {
            var snippet = item.GetProperty("snippet");
            var topLevelSnippet = snippet
                .GetProperty("topLevelComment")
                .GetProperty("snippet");

            comments.Add(new YouTubeCommentInfo(
                item.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty,
                topLevelSnippet.TryGetProperty("authorChannelId", out var authorChannelIdElement) &&
                authorChannelIdElement.TryGetProperty("value", out var authorChannelIdValue)
                    ? authorChannelIdValue.GetString() ?? string.Empty
                    : string.Empty,
                topLevelSnippet.TryGetProperty("textDisplay", out var textElement) ? textElement.GetString() ?? string.Empty : string.Empty,
                TryGetDateTimeOffset(topLevelSnippet, "publishedAt"),
                TryGetDateTimeOffset(topLevelSnippet, "updatedAt")));

            if (!item.TryGetProperty("replies", out var repliesElement) ||
                !repliesElement.TryGetProperty("comments", out var repliesCommentsElement) ||
                repliesCommentsElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var reply in repliesCommentsElement.EnumerateArray())
            {
                var replySnippet = reply.GetProperty("snippet");
                comments.Add(new YouTubeCommentInfo(
                    reply.TryGetProperty("id", out var replyIdElement) ? replyIdElement.GetString() ?? string.Empty : string.Empty,
                    replySnippet.TryGetProperty("authorChannelId", out var replyAuthorChannelIdElement) &&
                    replyAuthorChannelIdElement.TryGetProperty("value", out var replyAuthorChannelIdValue)
                        ? replyAuthorChannelIdValue.GetString() ?? string.Empty
                        : string.Empty,
                    replySnippet.TryGetProperty("textDisplay", out var replyTextElement) ? replyTextElement.GetString() ?? string.Empty : string.Empty,
                    TryGetDateTimeOffset(replySnippet, "publishedAt"),
                    TryGetDateTimeOffset(replySnippet, "updatedAt")));
            }
        }

        return comments;
    }

    private static bool TryExtractVideoId(string input, out string videoId)
    {
        videoId = string.Empty;
        var value = input.Trim();
        if (value.Length == 11 && value.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
        {
            videoId = value;
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            videoId = uri.AbsolutePath.Trim('/');
            return videoId.Length > 0;
        }

        var pathSegments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pathSegments.Length >= 2 &&
            string.Equals(pathSegments[0], "live", StringComparison.OrdinalIgnoreCase))
        {
            videoId = pathSegments[1];
            return videoId.Length > 0;
        }

        videoId = ParseQueryValue(uri.Query, "v") ?? string.Empty;
        return videoId.Length > 0;
    }

    private static string? ParseQueryValue(string query, string key)
    {
        var trimmedQuery = query.TrimStart('?');
        foreach (var part in trimmedQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 0)
            {
                continue;
            }

            var name = Uri.UnescapeDataString(pieces[0].Replace('+', ' '));
            if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return pieces.Length == 2
                ? Uri.UnescapeDataString(pieces[1].Replace('+', ' '))
                : string.Empty;
        }

        return null;
    }

    private static bool TryGetRawYouTubeChannelId(string? value, out string ownerChannelId)
    {
        ownerChannelId = value?.Trim() ?? string.Empty;
        return ownerChannelId.StartsWith("UC", StringComparison.OrdinalIgnoreCase) &&
               ownerChannelId.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            !DateTimeOffset.TryParse(property.GetString(), out var value))
        {
            return null;
        }

        return value;
    }

    private static string TrimForLog(string value)
    {
        var flattened = value.ReplaceLineEndings(" ").Trim();
        return flattened.Length <= 120 ? flattened : flattened[..120] + "...";
    }

    private static string Summarize(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        var message = current.Message.ReplaceLineEndings(" ").Trim();
        return string.IsNullOrWhiteSpace(message) ? current.GetType().Name : message;
    }

    private sealed class CommentScannerRuntime
    {
        public CommentScannerRuntime(ChannelProfile channel, string videoId, TimeSpan pollInterval, CancellationTokenSource cancellationTokenSource)
        {
            Channel = channel;
            VideoId = videoId;
            PollInterval = pollInterval;
            CancellationTokenSource = cancellationTokenSource;
        }

        public ChannelProfile Channel { get; }

        public string VideoId { get; }

        public string OwnerChannelId { get; set; } = string.Empty;

        public TimeSpan PollInterval { get; }

        public CancellationTokenSource CancellationTokenSource { get; }

        public Dictionary<string, DateTimeOffset?> SeenComments { get; } = new(StringComparer.Ordinal);

        public Task? Task { get; set; }
    }

    private sealed record YouTubeCommentInfo(
        string Id,
        string AuthorChannelId,
        string Text,
        DateTimeOffset? PublishedAt,
        DateTimeOffset? UpdatedAt);
}
