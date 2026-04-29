using CodePulse.Models;

namespace CodePulse.Services;

public sealed class WatchWorkflowService
{
    private readonly AppLogService _appLogService;
    private readonly WatchCoordinator _watchCoordinator;

    public WatchWorkflowService(
        AppLogService appLogService,
        WatchCoordinator watchCoordinator)
    {
        _appLogService = appLogService;
        _watchCoordinator = watchCoordinator;
    }

    public async Task StartWatchingChannelsAsync(
        IEnumerable<ChannelProfile> channels,
        Action refreshUi,
        CancellationToken cancellationToken)
    {
        var selectedChannels = channels
            .Where(static channel => channel is not null)
            .DistinctBy(static channel => channel.Id)
            .ToList();

        if (selectedChannels.Count == 0)
        {
            _appLogService.Write("ยังไม่ได้เลือกช่อง");
            return;
        }

        foreach (var channel in selectedChannels)
        {
            channel.Status = Enums.SessionState.LoadingChat;
            channel.LastStatusMessage = "กำลังโหลดหน้าแชท";
            refreshUi();
            _appLogService.Write($"เริ่มเฝ้าช่อง {channel.Name}");
            await _watchCoordinator.StartWatchingChannelAsync(channel, cancellationToken);
        }
    }

    public void StopChannels(
        IEnumerable<ChannelProfile> channels,
        Action<ChannelProfile> beforeStop,
        Action refreshUi)
    {
        var selectedChannels = channels
            .Where(static channel => channel is not null)
            .DistinctBy(static channel => channel.Id)
            .ToList();

        if (selectedChannels.Count == 0)
        {
            _appLogService.Write("ยังไม่ได้เลือกช่อง");
            return;
        }

        foreach (var channel in selectedChannels)
        {
            beforeStop(channel);
            _watchCoordinator.StopChannel(channel);
            channel.LastStatusMessage = "หยุดแล้ว";
        }

        refreshUi();
    }
}
