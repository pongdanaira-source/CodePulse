using System.Drawing;
using CodePulse.Models;

namespace CodePulse.Services;

public sealed class CaptureWorkflowService
{
    private readonly AppSettings _settings;
    private readonly AppLogService _appLogService;
    private readonly WatchCoordinator _watchCoordinator;
    private readonly ScreenCaptureService _screenCaptureService;
    private readonly OcrWorkflowService _ocrWorkflowService;
    private readonly ManualCaptureArtifactService _manualCaptureArtifactService;

    public CaptureWorkflowService(
        AppSettings settings,
        AppLogService appLogService,
        WatchCoordinator watchCoordinator,
        ScreenCaptureService screenCaptureService,
        OcrWorkflowService ocrWorkflowService,
        ManualCaptureArtifactService manualCaptureArtifactService)
    {
        _settings = settings;
        _appLogService = appLogService;
        _watchCoordinator = watchCoordinator;
        _screenCaptureService = screenCaptureService;
        _ocrWorkflowService = ocrWorkflowService;
        _manualCaptureArtifactService = manualCaptureArtifactService;
    }

    public void SaveCaptureRegion(ChannelProfile channel, Rectangle selectedRegion, Action refreshUi)
    {
        channel.LastCaptureRegion = CaptureRegion.FromRectangle(selectedRegion);
        _watchCoordinator.SaveSettings();
        _appLogService.Write($"[{channel.Name}] บันทึกพื้นที่จับล่าสุดแล้ว");
        refreshUi();
    }

    public async Task ProcessCaptureAsync(ChannelProfile channel, Rectangle captureRectangle, CancellationToken cancellationToken)
    {
        using var bitmap = await _screenCaptureService.CaptureAsync(captureRectangle, cancellationToken);
        var capturedImagePath = PrepareManualCaptureArtifact(channel, bitmap);
        await _ocrWorkflowService.ProcessCapturedBitmapAsync(
            channel,
            bitmap,
            cancellationToken,
            _appLogService.Write,
            capturedImagePath: capturedImagePath,
            allowOcrSpaceFallback: true);
    }

    public void ResetCaptureRegion(ChannelProfile channel, Action refreshUi)
    {
        channel.LastCaptureRegion = null;
        _watchCoordinator.SaveSettings();
        _appLogService.Write($"[{channel.Name}] ล้างพื้นที่จับล่าสุดแล้ว");
        refreshUi();
    }

    private string? PrepareManualCaptureArtifact(ChannelProfile channel, Bitmap bitmap)
    {
        var shouldSaveTempArtifact = _settings.Dispatch.EnableDryRun &&
                                     _settings.Dispatch.SaveManualCaptureImageToTempInDryRun;
        var shouldSaveDispatchImage = _settings.Dispatch.SendManualCaptureImage;
        if (!shouldSaveTempArtifact && !shouldSaveDispatchImage)
        {
            return null;
        }

        var imagePath = _manualCaptureArtifactService.PrepareCaptureImage(bitmap);
        if (shouldSaveTempArtifact)
        {
            _appLogService.Write($"[{channel.Name}] บันทึกภาพจับล่าสุดไว้ที่ {imagePath}");
        }

        return imagePath;
    }
}
