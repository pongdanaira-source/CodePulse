using System.Drawing;
using System.Drawing.Imaging;

namespace CodePulse.Services;

public sealed class ManualCaptureArtifactService
{
    private readonly string _tempRootPath;

    public ManualCaptureArtifactService()
    {
        _tempRootPath = Path.Combine(Path.GetTempPath(), "CodePulse", "manual-capture");
    }

    public string PrepareCaptureImage(Bitmap bitmap)
    {
        Directory.CreateDirectory(_tempRootPath);
        ClearExistingArtifacts();

        var filePath = Path.Combine(_tempRootPath, $"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.png");
        bitmap.Save(filePath, ImageFormat.Png);
        return filePath;
    }

    private void ClearExistingArtifacts()
    {
        if (!Directory.Exists(_tempRootPath))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(_tempRootPath))
        {
            try
            {
                File.Delete(filePath);
            }
            catch
            {
                // Ignore temp cleanup failures; the newest capture still matters most.
            }
        }
    }
}
