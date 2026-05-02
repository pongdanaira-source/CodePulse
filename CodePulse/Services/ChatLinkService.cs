using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodePulse.Services;

public static class ChatLinkService
{
    private static readonly string[] LiveMarkers =
    [
        @"""style"":""LIVE""",
        @"""BADGE_STYLE_TYPE_LIVE_NOW""",
        @"""isLiveNow"":true",
        @"""thumbnailOverlayTimeStatusRenderer"":{""style"":""LIVE""",
        @"""publishedTimeText"":{""simpleText"":""กำลังถ่ายทอดสด"""
    ];

    private static readonly Regex ChannelIdRegex = new(
        @"^UC[A-Z0-9_-]{22}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex VideoRendererStartRegex = new(
        @"""videoRenderer"":\{""videoId"":""([A-Za-z0-9_-]{11})""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly HttpClient HttpClient = CreateHttpClient();

    public static bool TryNormalize(string input, out string normalizedLink)
    {
        normalizedLink = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        if (TryNormalizeLiveChatLink(trimmed, out normalizedLink))
        {
            return true;
        }

        if (TryNormalizeWatchUrl(trimmed, out normalizedLink))
        {
            return true;
        }

        return TryNormalizeChannelId(trimmed, out normalizedLink);
    }

    public static Task<WatchSourceResolutionResult> ResolveToChatLinkAsync(string input, CancellationToken cancellationToken)
    {
        return ResolveToChatLinkAsync(input, null, cancellationToken);
    }

    public static async Task<WatchSourceResolutionResult> ResolveToChatLinkAsync(
        string input,
        string? youtubeApiKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return WatchSourceResolutionResult.Fail("ยังไม่ได้ตั้งแหล่งเฝ้า");
        }

        var trimmed = input.Trim();
        if (TryNormalizeLiveChatLink(trimmed, out var normalizedLiveChatLink))
        {
            return WatchSourceResolutionResult.Success(normalizedLiveChatLink);
        }

        if (TryNormalizeWatchUrl(trimmed, out var normalizedWatchLink))
        {
            return WatchSourceResolutionResult.Success(normalizedWatchLink);
        }

        if (!TryNormalizeChannelId(trimmed, out var normalizedChannelId))
        {
            return WatchSourceResolutionResult.Fail("แหล่งเฝ้าไม่ถูกต้อง");
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(youtubeApiKey))
            {
                WatchSourceResolutionResult apiResolution;
                try
                {
                    apiResolution = await ResolveChannelIdWithApiAsync(
                        normalizedChannelId,
                        youtubeApiKey.Trim(),
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    apiResolution = WatchSourceResolutionResult.Fail("API fallback");
                }

                if (apiResolution.Succeeded)
                {
                    return apiResolution;
                }

                if (string.Equals(apiResolution.ErrorMessage, "ช่องนี้ยังไม่ไลฟ์", StringComparison.Ordinal))
                {
                    return apiResolution;
                }

                return await ResolveChannelIdWithPublicPageAsync(normalizedChannelId, cancellationToken);
            }

            return await ResolveChannelIdWithPublicPageAsync(normalizedChannelId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return WatchSourceResolutionResult.Fail(Summarize(ex));
        }
    }

    private static async Task<WatchSourceResolutionResult> ResolveChannelIdWithApiAsync(
        string normalizedChannelId,
        string youtubeApiKey,
        CancellationToken cancellationToken)
    {
        var endpoint =
            "https://www.googleapis.com/youtube/v3/search" +
            "?part=snippet" +
            $"&channelId={Uri.EscapeDataString(normalizedChannelId)}" +
            "&eventType=live" +
            "&type=video" +
            "&maxResults=1" +
            $"&key={Uri.EscapeDataString(youtubeApiKey)}";

        using var response = await HttpClient.GetAsync(endpoint, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return WatchSourceResolutionResult.Fail(BuildApiFailureMessage(response.StatusCode, content));
        }

        if (TryExtractVideoIdFromApiResponse(content, out var videoId))
        {
            return WatchSourceResolutionResult.Success(BuildLiveChatLink(videoId));
        }

        return WatchSourceResolutionResult.Fail("ช่องนี้ยังไม่ไลฟ์");
    }

    private static async Task<WatchSourceResolutionResult> ResolveChannelIdWithPublicPageAsync(
        string normalizedChannelId,
        CancellationToken cancellationToken)
    {
        var liveUri = new Uri($"https://www.youtube.com/channel/{normalizedChannelId}/live");
        using var response = await HttpClient.GetAsync(liveUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        var finalUri = response.RequestMessage?.RequestUri;
        if (TryExtractWatchVideoId(finalUri, out var redirectedVideoId))
        {
            return WatchSourceResolutionResult.Success(BuildLiveChatLink(redirectedVideoId));
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (TryExtractLiveVideoIdFromRendererBlocks(html, out var htmlVideoId))
        {
            return WatchSourceResolutionResult.Success(BuildLiveChatLink(htmlVideoId));
        }

        return WatchSourceResolutionResult.Fail("ช่องนี้ยังไม่ไลฟ์");
    }

    private static bool TryNormalizeLiveChatLink(string input, out string normalizedLink)
    {
        normalizedLink = string.Empty;

        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!IsAllowedYouTubeHost(uri.Host))
        {
            return false;
        }

        if (!string.Equals(uri.AbsolutePath.Trim('/'), "live_chat", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("v", out var videoId) || string.IsNullOrWhiteSpace(videoId))
        {
            return false;
        }

        normalizedLink = BuildLiveChatLink(videoId);
        return true;
    }

    private static bool TryNormalizeWatchUrl(string input, out string normalizedLink)
    {
        normalizedLink = string.Empty;

        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!IsAllowedYouTubeHost(uri.Host))
        {
            return false;
        }

        if (!string.Equals(uri.AbsolutePath.Trim('/'), "watch", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("v", out var videoId) || string.IsNullOrWhiteSpace(videoId))
        {
            return false;
        }

        normalizedLink = BuildLiveChatLink(videoId);
        return true;
    }

    private static bool TryNormalizeChannelId(string input, out string normalizedChannelId)
    {
        normalizedChannelId = string.Empty;
        var trimmed = input.Trim();
        if (!ChannelIdRegex.IsMatch(trimmed))
        {
            return false;
        }

        normalizedChannelId = trimmed;
        return true;
    }

    private static string BuildLiveChatLink(string videoId)
    {
        return $"https://www.youtube.com/live_chat?is_popout=1&v={Uri.EscapeDataString(videoId)}";
    }

    private static bool IsAllowedYouTubeHost(string host)
    {
        return string.Equals(host, "youtube.com", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "www.youtube.com", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "m.youtube.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractWatchVideoId(Uri? uri, out string videoId)
    {
        videoId = string.Empty;
        if (uri is null)
        {
            return false;
        }

        if (!string.Equals(uri.AbsolutePath, "/watch", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("v", out var extractedVideoId) || string.IsNullOrWhiteSpace(extractedVideoId))
        {
            return false;
        }

        videoId = extractedVideoId.Trim();
        return videoId.Length == 11;
    }

    private static bool TryExtractLiveVideoIdFromRendererBlocks(string html, out string videoId)
    {
        videoId = string.Empty;
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        var matches = VideoRendererStartRegex.Matches(html);
        if (matches.Count == 0)
        {
            return false;
        }

        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            var startIndex = match.Index;
            var endIndex = index + 1 < matches.Count
                ? matches[index + 1].Index
                : Math.Min(html.Length, startIndex + 30_000);

            var length = Math.Max(0, endIndex - startIndex);
            if (length == 0)
            {
                continue;
            }

            var block = html.Substring(startIndex, length);
            if (!ContainsLiveMarker(block))
            {
                continue;
            }

            var candidateVideoId = match.Groups[1].Value.Trim();
            if (candidateVideoId.Length != 11)
            {
                continue;
            }

            videoId = candidateVideoId;
            return true;
        }

        return false;
    }

    private static bool ContainsLiveMarker(string block)
    {
        foreach (var marker in LiveMarkers)
        {
            if (block.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractVideoIdFromApiResponse(string json, out string videoId)
    {
        videoId = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!idElement.TryGetProperty("videoId", out var videoIdElement) || videoIdElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var candidate = videoIdElement.GetString()?.Trim() ?? string.Empty;
            if (candidate.Length != 11)
            {
                continue;
            }

            videoId = candidate;
            return true;
        }

        return false;
    }

    private static string BuildApiFailureMessage(HttpStatusCode statusCode, string content)
    {
        var apiMessage = TryExtractApiErrorMessage(content);
        var suffix = string.IsNullOrWhiteSpace(apiMessage) ? string.Empty : $": {apiMessage}";
        return $"YouTube API ล้มเหลว ({(int)statusCode}){suffix}";
    }

    private static string TryExtractApiErrorMessage(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("error", out var errorElement) || errorElement.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            if (!errorElement.TryGetProperty("message", out var messageElement) || messageElement.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return messageElement.GetString()?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var value = query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        foreach (var pair in value.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separatorIndex]);
            var itemValue = Uri.UnescapeDataString(pair[(separatorIndex + 1)..]);
            result[key] = itemValue;
        }

        return result;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true
        });
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36");
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    private static string Summarize(Exception ex)
    {
        var message = ex.Message.ReplaceLineEndings(" ").Trim();
        return string.IsNullOrWhiteSpace(message) ? "ไม่ทราบสาเหตุ" : message;
    }
}

public sealed class WatchSourceResolutionResult
{
    public bool Succeeded { get; init; }

    public string NormalizedChatLink { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;

    public static WatchSourceResolutionResult Success(string normalizedChatLink)
    {
        return new WatchSourceResolutionResult
        {
            Succeeded = true,
            NormalizedChatLink = normalizedChatLink
        };
    }

    public static WatchSourceResolutionResult Fail(string errorMessage)
    {
        return new WatchSourceResolutionResult
        {
            Succeeded = false,
            ErrorMessage = errorMessage
        };
    }
}
