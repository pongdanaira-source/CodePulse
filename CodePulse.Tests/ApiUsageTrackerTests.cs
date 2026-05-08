using CodePulse.Services;
using Xunit;

namespace CodePulse.Tests;

public sealed class ApiUsageTrackerTests
{
    [Fact]
    public void OcrSpaceCounters_PersistAcrossTrackerInstances()
    {
        using var directory = new TempDirectory();
        var filePath = Path.Combine(directory.Path, "usage-counters.json");
        var tracker = new ApiUsageTracker(filePath);

        var reserved = tracker.TryReserveOcrSpaceRequest(100, 20, out var firstSnapshot);

        Assert.True(reserved);
        Assert.Equal(1, firstSnapshot.OcrSpaceDailyRequests);
        Assert.Equal(1, firstSnapshot.OcrSpaceHourlyRequests);

        var reloaded = new ApiUsageTracker(filePath);
        var reloadedSnapshot = reloaded.Snapshot();

        Assert.Equal(1, reloadedSnapshot.OcrSpaceDailyRequests);
        Assert.Equal(1, reloadedSnapshot.OcrSpaceHourlyRequests);
    }

    [Fact]
    public void Reset_PersistsClearedCounters()
    {
        using var directory = new TempDirectory();
        var filePath = Path.Combine(directory.Path, "usage-counters.json");
        var tracker = new ApiUsageTracker(filePath);
        tracker.TryReserveYouTubeUnits(7, 100, out _);
        tracker.TryReserveOcrSpaceRequest(100, 20, out _);

        tracker.Reset();

        var reloaded = new ApiUsageTracker(filePath);
        var snapshot = reloaded.Snapshot();
        Assert.Equal(0, snapshot.YouTubeDailyUnits);
        Assert.Equal(0, snapshot.OcrSpaceDailyRequests);
        Assert.Equal(0, snapshot.OcrSpaceHourlyRequests);
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
