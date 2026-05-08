using System.Drawing;
using CodePulse.Models;
using CodePulse.Services;
using Xunit;

namespace CodePulse.Tests;

public sealed class OcrServiceTests
{
    [Fact]
    public async Task ReadWithOcrSpaceAsync_WithoutApiKey_FailsBeforeReservingUsage()
    {
        var settings = new AppSettings
        {
            EnableOcrSpaceFallback = true,
            OcrSpaceApiKey = string.Empty
        };
        var usageTracker = new ApiUsageTracker();
        var service = new OcrService(settings, usageTracker);
        using var bitmap = new Bitmap(4, 4);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReadWithOcrSpaceAsync(bitmap, CancellationToken.None));

        Assert.Contains("API key", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, usageTracker.Snapshot().OcrSpaceDailyRequests);
        Assert.Equal(0, usageTracker.Snapshot().OcrSpaceHourlyRequests);
    }
}
