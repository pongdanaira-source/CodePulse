using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Diagnostics;
using CodePulse.Models;
using Tesseract;

namespace CodePulse.Services;

public sealed class OcrService
{
    private const string DefaultOcrSpaceApiKey = "helloworld";
    private const float HighConfidenceEarlyExitThreshold = 0.86f;
    private const float FastStageAcceptThreshold = 0.72f;
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private readonly string _tessDataPath;
    private readonly AppSettings _settings;

    public OcrService(AppSettings settings)
    {
        _settings = settings;
        _tessDataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
    }

    public Task<OcrReadResult> ReadAsync(Bitmap sourceBitmap, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stages = CreateVariantStages(sourceBitmap);
            var totalStopwatch = Stopwatch.StartNew();
            try
            {
                using var engine = new TesseractEngine(_tessDataPath, "eng", EngineMode.Default);
                engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
                engine.SetVariable("preserve_interword_spaces", "0");

                var passes = new List<OcrPassResult>();
                var stageMetrics = new List<OcrStageMetric>();
                for (var stageIndex = 0; stageIndex < stages.Count; stageIndex++)
                {
                    var stageStopwatch = Stopwatch.StartNew();
                    var pageSegModes = GetPageSegModes(stageIndex).ToList();
                    var pageSegModeCount = pageSegModes.Count;
                    var passCountBeforeStage = passes.Count;
                    foreach (var variant in stages[stageIndex])
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var pix = CreatePix(variant.Bitmap);
                        foreach (var pageSegMode in pageSegModes)
                        {
                            var pass = ReadSingleVariant(engine, variant.Name, pageSegMode, pix);
                            if (string.IsNullOrWhiteSpace(pass.Text))
                            {
                                continue;
                            }

                            passes.Add(pass);
                            if (ShouldStopEarly(pass))
                            {
                                stageStopwatch.Stop();
                                totalStopwatch.Stop();
                                stageMetrics.Add(CreateStageMetric(stageIndex, stages[stageIndex].Count, pageSegModeCount, passes.Count - passCountBeforeStage, stageStopwatch.Elapsed.TotalMilliseconds, returnedEarly: true));
                                return new OcrReadResult
                                {
                                    Passes = passes,
                                    TotalElapsedMs = totalStopwatch.Elapsed.TotalMilliseconds,
                                    StageMetrics = stageMetrics
                                };
                            }
                        }
                    }

                    if (stageIndex == 0 && HasAcceptableFastStageResult(passes))
                    {
                        stageStopwatch.Stop();
                        totalStopwatch.Stop();
                        stageMetrics.Add(CreateStageMetric(stageIndex, stages[stageIndex].Count, pageSegModeCount, passes.Count - passCountBeforeStage, stageStopwatch.Elapsed.TotalMilliseconds, returnedEarly: true));
                        return new OcrReadResult
                        {
                            Passes = passes,
                            TotalElapsedMs = totalStopwatch.Elapsed.TotalMilliseconds,
                            StageMetrics = stageMetrics
                        };
                    }

                    stageStopwatch.Stop();
                    stageMetrics.Add(CreateStageMetric(stageIndex, stages[stageIndex].Count, pageSegModeCount, passes.Count - passCountBeforeStage, stageStopwatch.Elapsed.TotalMilliseconds, returnedEarly: false));
                }

                totalStopwatch.Stop();
                return new OcrReadResult
                {
                    Passes = passes,
                    TotalElapsedMs = totalStopwatch.Elapsed.TotalMilliseconds,
                    StageMetrics = stageMetrics
                };
            }
            finally
            {
                foreach (var stage in stages)
                {
                    foreach (var variant in stage)
                    {
                        variant.Bitmap.Dispose();
                    }
                }
            }
        }, cancellationToken);
    }

    public async Task<OcrReadResult> ReadWithOcrSpaceAsync(Bitmap sourceBitmap, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new MemoryStream();
        sourceBitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;

        using var content = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(stream.ToArray());
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        content.Add(fileContent, "file", "capture.png");
        content.Add(new StringContent(GetOcrSpaceLanguage()), "language");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.ocr.space/parse/image")
        {
            Content = content
        };
        request.Headers.Add("apikey", GetOcrSpaceApiKey());

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        if (root.TryGetProperty("IsErroredOnProcessing", out var errorElement) && errorElement.GetBoolean())
        {
            var message = ExtractOcrSpaceError(root);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message)
                    ? "OCR.space ตอบกลับว่าประมวลผลไม่สำเร็จ"
                    : message);
        }

        var passes = new List<OcrPassResult>();
        if (root.TryGetProperty("ParsedResults", out var parsedResultsElement) &&
            parsedResultsElement.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var parsedResult in parsedResultsElement.EnumerateArray())
            {
                var text = parsedResult.TryGetProperty("ParsedText", out var textElement)
                    ? NormalizeOcrText(textElement.GetString())
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(text))
                {
                    index++;
                    continue;
                }

                passes.Add(new OcrPassResult
                {
                    PassName = $"ocr.space/{index}",
                    Text = text,
                    Confidence = 1.0f
                });
                index++;
            }
        }

        return new OcrReadResult
        {
            Passes = passes
        };
    }

    private string GetOcrSpaceApiKey()
    {
        return string.IsNullOrWhiteSpace(_settings.OcrSpaceApiKey)
            ? DefaultOcrSpaceApiKey
            : _settings.OcrSpaceApiKey.Trim();
    }

    private string GetOcrSpaceLanguage()
    {
        return string.IsNullOrWhiteSpace(_settings.OcrSpaceLanguage)
            ? "eng"
            : _settings.OcrSpaceLanguage.Trim();
    }

    private static string ExtractOcrSpaceError(JsonElement root)
    {
        if (root.TryGetProperty("ErrorMessage", out var errorMessageElement))
        {
            if (errorMessageElement.ValueKind == JsonValueKind.Array)
            {
                var messages = errorMessageElement
                    .EnumerateArray()
                    .Select(static item => item.GetString())
                    .Where(static item => !string.IsNullOrWhiteSpace(item));
                return string.Join(" ", messages!);
            }

            var single = errorMessageElement.GetString();
            if (!string.IsNullOrWhiteSpace(single))
            {
                return single;
            }
        }

        if (root.TryGetProperty("ErrorDetails", out var detailsElement))
        {
            var details = detailsElement.GetString();
            if (!string.IsNullOrWhiteSpace(details))
            {
                return details;
            }
        }

        return string.Empty;
    }

    private static string NormalizeOcrText(string? value)
    {
        return (value ?? string.Empty)
            .ReplaceLineEndings(string.Empty)
            .Replace(" ", string.Empty)
            .Trim();
    }

    private static List<List<OcrVariant>> CreateVariantStages(Bitmap sourceBitmap)
    {
        var grayscale = ToGrayscale(sourceBitmap);
        var contrastStrong = AdjustContrast(grayscale, 1.8f);
        var upscale2x = Upscale(sourceBitmap, 2);
        var crop = CropMargins(sourceBitmap, 8);
        var grayscaleUpscale2x = Upscale(grayscale, 2);
        var brightForeground = ExtractBrightForeground(sourceBitmap, 120);
        var brightForegroundUpscale2x = Upscale(brightForeground, 2);
        var greenForeground = ExtractGreenForeground(sourceBitmap, 100);
        var greenForegroundUpscale2x = Upscale(greenForeground, 2);
        var greenForegroundInvertedUpscale3x = UpscaleNearest(Invert(greenForeground), 3);
        var yellowGreenForeground = ExtractYellowGreenForeground(sourceBitmap, 90);
        var yellowGreenForegroundInvertedUpscale3x = UpscaleNearest(Invert(yellowGreenForeground), 3);
        var threshold170 = ToThreshold(grayscale, 170);
        var threshold200 = ToThreshold(grayscale, 200);
        var cropUpscale2x = Upscale(crop, 2);
        var sharpenedCrop = Sharpen(crop);
        var upperBand = CropVerticalBand(sourceBitmap, 0.00f, 0.58f);
        var middleBand = CropVerticalBand(sourceBitmap, 0.20f, 0.80f);
        var lowerBand = CropVerticalBand(sourceBitmap, 0.42f, 1.00f);
        var upperBandUpscale2x = Upscale(upperBand, 2);
        var middleBandUpscale2x = Upscale(middleBand, 2);
        var lowerBandUpscale2x = Upscale(lowerBand, 2);

        return
        [
            [
                new OcrVariant("original", CloneBitmap(sourceBitmap)),
                new OcrVariant("crop", crop),
                new OcrVariant("grayscale", grayscale),
                new OcrVariant("upscale-2x", upscale2x),
                new OcrVariant("bright-threshold-120", brightForeground),
                new OcrVariant("green-threshold-100-upscale-2x", greenForegroundUpscale2x),
                new OcrVariant("green-threshold-100-invert-upscale-3x", greenForegroundInvertedUpscale3x),
                new OcrVariant("yellowgreen-threshold-090-invert-upscale-3x", yellowGreenForegroundInvertedUpscale3x),
                new OcrVariant("band-upper-upscale-2x", upperBandUpscale2x),
                new OcrVariant("band-middle-upscale-2x", middleBandUpscale2x),
                new OcrVariant("band-lower-upscale-2x", lowerBandUpscale2x)
            ],
            [
                new OcrVariant("grayscale-upscale-2x", grayscaleUpscale2x),
                new OcrVariant("bright-threshold-120-upscale-2x", brightForegroundUpscale2x),
                new OcrVariant("green-threshold-100", greenForeground),
                new OcrVariant("yellowgreen-threshold-090", yellowGreenForeground),
                new OcrVariant("threshold-170", threshold170),
                new OcrVariant("threshold-200", threshold200),
                new OcrVariant("crop-upscale-2x", cropUpscale2x),
                new OcrVariant("sharpen-crop", sharpenedCrop),
                new OcrVariant("contrast-180", contrastStrong),
                new OcrVariant("band-upper", upperBand),
                new OcrVariant("band-middle", middleBand),
                new OcrVariant("band-lower", lowerBand)
            ]
        ];
    }

    private static IEnumerable<PageSegMode> GetPageSegModes(int stageIndex)
    {
        yield return PageSegMode.SingleLine;

        if (stageIndex > 0)
        {
            yield return PageSegMode.SingleWord;
            yield return PageSegMode.SingleBlock;
        }
    }

    private static bool ShouldStopEarly(OcrPassResult pass)
    {
        return pass.Confidence >= HighConfidenceEarlyExitThreshold &&
               LooksLikeCode(pass.Text);
    }

    private static bool HasAcceptableFastStageResult(IEnumerable<OcrPassResult> passes)
    {
        return passes.Any(static pass =>
            pass.Confidence >= FastStageAcceptThreshold &&
            LooksLikeCode(pass.Text));
    }

    private static bool LooksLikeCode(string text)
    {
        return text.Length is >= 8 and <= 24 &&
               text.All(static character => char.IsAsciiLetterUpper(character) || char.IsDigit(character));
    }

    private static OcrStageMetric CreateStageMetric(
        int stageIndex,
        int variantCount,
        int pageSegModeCount,
        int passCount,
        double elapsedMs,
        bool returnedEarly)
    {
        return new OcrStageMetric
        {
            StageName = stageIndex == 0 ? "fast" : $"fallback-{stageIndex}",
            VariantCount = variantCount,
            PageSegModeCount = pageSegModeCount,
            PassCount = passCount,
            ElapsedMs = elapsedMs,
            ReturnedEarly = returnedEarly
        };
    }

    private static Pix CreatePix(Bitmap bitmap)
    {
        using var memoryStream = new MemoryStream();
        bitmap.Save(memoryStream, System.Drawing.Imaging.ImageFormat.Png);
        memoryStream.Position = 0;

        return Pix.LoadFromMemory(memoryStream.ToArray());
    }

    private static OcrPassResult ReadSingleVariant(TesseractEngine engine, string passName, PageSegMode pageSegMode, Pix pix)
    {
        engine.DefaultPageSegMode = pageSegMode;
        using var page = engine.Process(pix);
        var text = NormalizeOcrText(page.GetText());

        return new OcrPassResult
        {
            PassName = $"{passName}/{pageSegMode}",
            Text = text,
            Confidence = page.GetMeanConfidence()
        };
    }

    private static Bitmap CloneBitmap(Bitmap source)
    {
        return new Bitmap(source);
    }

    private static Bitmap ToGrayscale(Bitmap source)
    {
        var bitmap = new Bitmap(source.Width, source.Height);
        using var graphics = Graphics.FromImage(bitmap);
        using var attributes = new ImageAttributes();
        var matrix = new ColorMatrix(
        [
            [0.299f, 0.299f, 0.299f, 0, 0],
            [0.587f, 0.587f, 0.587f, 0, 0],
            [0.114f, 0.114f, 0.114f, 0, 0],
            [0, 0, 0, 1, 0],
            [0, 0, 0, 0, 1]
        ]);
        attributes.SetColorMatrix(matrix);
        graphics.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        return bitmap;
    }

    private static Bitmap AdjustContrast(Bitmap source, float contrast)
    {
        var translated = (1.0f - contrast) / 2.0f;
        var bitmap = new Bitmap(source.Width, source.Height);
        using var graphics = Graphics.FromImage(bitmap);
        using var attributes = new ImageAttributes();
        var matrix = new ColorMatrix(
        [
            [contrast, 0, 0, 0, 0],
            [0, contrast, 0, 0, 0],
            [0, 0, contrast, 0, 0],
            [0, 0, 0, 1, 0],
            [translated, translated, translated, 0, 1]
        ]);
        attributes.SetColorMatrix(matrix);
        graphics.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        return bitmap;
    }

    private static Bitmap ToThreshold(Bitmap source, int threshold)
    {
        using var sourceBitmap = CloneAs32BppArgb(source);
        var bitmap = new Bitmap(sourceBitmap.Width, sourceBitmap.Height, PixelFormat.Format32bppArgb);

        var sourceData = sourceBitmap.LockBits(
            new Rectangle(0, 0, sourceBitmap.Width, sourceBitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        var targetData = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var sourceBytes = new byte[Math.Abs(sourceData.Stride) * sourceBitmap.Height];
            var targetBytes = new byte[Math.Abs(targetData.Stride) * bitmap.Height];
            Marshal.Copy(sourceData.Scan0, sourceBytes, 0, sourceBytes.Length);

            for (var y = 0; y < sourceBitmap.Height; y++)
            {
                var sourceRow = y * sourceData.Stride;
                var targetRow = y * targetData.Stride;
                for (var x = 0; x < sourceBitmap.Width; x++)
                {
                    var sourceIndex = sourceRow + (x * 4);
                    var targetIndex = targetRow + (x * 4);

                    var blue = sourceBytes[sourceIndex];
                    var green = sourceBytes[sourceIndex + 1];
                    var red = sourceBytes[sourceIndex + 2];
                    var alpha = sourceBytes[sourceIndex + 3];
                    var brightness = (red + green + blue) / 3;
                    var value = (byte)(brightness >= threshold ? 255 : 0);

                    targetBytes[targetIndex] = value;
                    targetBytes[targetIndex + 1] = value;
                    targetBytes[targetIndex + 2] = value;
                    targetBytes[targetIndex + 3] = alpha;
                }
            }

            Marshal.Copy(targetBytes, 0, targetData.Scan0, targetBytes.Length);
        }
        finally
        {
            sourceBitmap.UnlockBits(sourceData);
            bitmap.UnlockBits(targetData);
        }

        return bitmap;
    }

    private static Bitmap ExtractBrightForeground(Bitmap source, int threshold)
    {
        using var sourceBitmap = CloneAs32BppArgb(source);
        var bitmap = new Bitmap(sourceBitmap.Width, sourceBitmap.Height, PixelFormat.Format32bppArgb);

        var sourceData = sourceBitmap.LockBits(
            new Rectangle(0, 0, sourceBitmap.Width, sourceBitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        var targetData = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var sourceBytes = new byte[Math.Abs(sourceData.Stride) * sourceBitmap.Height];
            var targetBytes = new byte[Math.Abs(targetData.Stride) * bitmap.Height];
            Marshal.Copy(sourceData.Scan0, sourceBytes, 0, sourceBytes.Length);

            for (var y = 0; y < sourceBitmap.Height; y++)
            {
                var sourceRow = y * sourceData.Stride;
                var targetRow = y * targetData.Stride;
                for (var x = 0; x < sourceBitmap.Width; x++)
                {
                    var sourceIndex = sourceRow + (x * 4);
                    var targetIndex = targetRow + (x * 4);

                    var blue = sourceBytes[sourceIndex];
                    var green = sourceBytes[sourceIndex + 1];
                    var red = sourceBytes[sourceIndex + 2];
                    var alpha = sourceBytes[sourceIndex + 3];
                    var brightness = Math.Max(red, Math.Max(green, blue));
                    var value = (byte)(brightness >= threshold ? 255 : 0);

                    targetBytes[targetIndex] = value;
                    targetBytes[targetIndex + 1] = value;
                    targetBytes[targetIndex + 2] = value;
                    targetBytes[targetIndex + 3] = alpha;
                }
            }

            Marshal.Copy(targetBytes, 0, targetData.Scan0, targetBytes.Length);
        }
        finally
        {
            sourceBitmap.UnlockBits(sourceData);
            bitmap.UnlockBits(targetData);
        }

        return bitmap;
    }

    private static Bitmap ExtractGreenForeground(Bitmap source, int threshold)
    {
        using var sourceBitmap = CloneAs32BppArgb(source);
        var bitmap = new Bitmap(sourceBitmap.Width, sourceBitmap.Height, PixelFormat.Format32bppArgb);

        var sourceData = sourceBitmap.LockBits(
            new Rectangle(0, 0, sourceBitmap.Width, sourceBitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        var targetData = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var sourceBytes = new byte[Math.Abs(sourceData.Stride) * sourceBitmap.Height];
            var targetBytes = new byte[Math.Abs(targetData.Stride) * bitmap.Height];
            Marshal.Copy(sourceData.Scan0, sourceBytes, 0, sourceBytes.Length);

            for (var y = 0; y < sourceBitmap.Height; y++)
            {
                var sourceRow = y * sourceData.Stride;
                var targetRow = y * targetData.Stride;
                for (var x = 0; x < sourceBitmap.Width; x++)
                {
                    var sourceIndex = sourceRow + (x * 4);
                    var targetIndex = targetRow + (x * 4);

                    var green = sourceBytes[sourceIndex + 1];
                    var red = sourceBytes[sourceIndex + 2];
                    var blue = sourceBytes[sourceIndex];
                    var alpha = sourceBytes[sourceIndex + 3];
                    var emphasis = green - Math.Max(red / 3, blue / 3);
                    var value = (byte)(emphasis >= threshold ? 255 : 0);

                    targetBytes[targetIndex] = value;
                    targetBytes[targetIndex + 1] = value;
                    targetBytes[targetIndex + 2] = value;
                    targetBytes[targetIndex + 3] = alpha;
                }
            }

            Marshal.Copy(targetBytes, 0, targetData.Scan0, targetBytes.Length);
        }
        finally
        {
            sourceBitmap.UnlockBits(sourceData);
            bitmap.UnlockBits(targetData);
        }

        return bitmap;
    }

    private static Bitmap Upscale(Bitmap source, int scale)
    {
        var bitmap = new Bitmap(source.Width * scale, source.Height * scale);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        return bitmap;
    }

    private static Bitmap UpscaleNearest(Bitmap source, int scale)
    {
        var bitmap = new Bitmap(source.Width * scale, source.Height * scale);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        return bitmap;
    }

    private static Bitmap CropMargins(Bitmap source, int margin)
    {
        var safeMargin = Math.Max(0, Math.Min(margin, Math.Min(source.Width / 4, source.Height / 4)));
        var width = Math.Max(1, source.Width - (safeMargin * 2));
        var height = Math.Max(1, source.Height - (safeMargin * 2));
        var crop = new Rectangle(safeMargin, safeMargin, width, height);
        return source.Clone(crop, source.PixelFormat);
    }

    private static Bitmap CropVerticalBand(Bitmap source, float startRatio, float endRatio)
    {
        var safeStart = Math.Clamp(startRatio, 0f, 1f);
        var safeEnd = Math.Clamp(endRatio, safeStart + 0.05f, 1f);
        var top = (int)Math.Round(source.Height * safeStart);
        var bottom = (int)Math.Round(source.Height * safeEnd);
        var clampedTop = Math.Clamp(top, 0, Math.Max(0, source.Height - 1));
        var height = Math.Max(1, Math.Min(source.Height - clampedTop, bottom - clampedTop));
        return source.Clone(new Rectangle(0, clampedTop, source.Width, height), source.PixelFormat);
    }

    private static Bitmap Sharpen(Bitmap source)
    {
        using var sourceBitmap = CloneAs32BppArgb(source);
        var bitmap = new Bitmap(sourceBitmap.Width, sourceBitmap.Height, PixelFormat.Format32bppArgb);
        var kernel =
        new[,]
        {
            { 0, -1, 0 },
            { -1, 5, -1 },
            { 0, -1, 0 }
        };

        var sourceData = sourceBitmap.LockBits(
            new Rectangle(0, 0, sourceBitmap.Width, sourceBitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        var targetData = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var sourceBytes = new byte[Math.Abs(sourceData.Stride) * sourceBitmap.Height];
            var targetBytes = new byte[Math.Abs(targetData.Stride) * bitmap.Height];
            Marshal.Copy(sourceData.Scan0, sourceBytes, 0, sourceBytes.Length);
            Array.Copy(sourceBytes, targetBytes, sourceBytes.Length);

            for (var x = 1; x < sourceBitmap.Width - 1; x++)
            {
                for (var y = 1; y < sourceBitmap.Height - 1; y++)
                {
                    var red = 0;
                    var green = 0;
                    var blue = 0;

                    for (var filterX = -1; filterX <= 1; filterX++)
                    {
                        for (var filterY = -1; filterY <= 1; filterY++)
                        {
                            var factor = kernel[filterX + 1, filterY + 1];
                            var sourceIndex = ((y + filterY) * sourceData.Stride) + ((x + filterX) * 4);
                            blue += sourceBytes[sourceIndex] * factor;
                            green += sourceBytes[sourceIndex + 1] * factor;
                            red += sourceBytes[sourceIndex + 2] * factor;
                        }
                    }

                    var targetIndex = (y * targetData.Stride) + (x * 4);
                    targetBytes[targetIndex] = (byte)ClampToByte(blue);
                    targetBytes[targetIndex + 1] = (byte)ClampToByte(green);
                    targetBytes[targetIndex + 2] = (byte)ClampToByte(red);
                }
            }

            Marshal.Copy(targetBytes, 0, targetData.Scan0, targetBytes.Length);
        }
        finally
        {
            sourceBitmap.UnlockBits(sourceData);
            bitmap.UnlockBits(targetData);
        }

        return bitmap;
    }

    private static Bitmap Invert(Bitmap source)
    {
        using var sourceBitmap = CloneAs32BppArgb(source);
        var bitmap = new Bitmap(sourceBitmap.Width, sourceBitmap.Height, PixelFormat.Format32bppArgb);

        var sourceData = sourceBitmap.LockBits(
            new Rectangle(0, 0, sourceBitmap.Width, sourceBitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        var targetData = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var sourceBytes = new byte[Math.Abs(sourceData.Stride) * sourceBitmap.Height];
            var targetBytes = new byte[Math.Abs(targetData.Stride) * bitmap.Height];
            Marshal.Copy(sourceData.Scan0, sourceBytes, 0, sourceBytes.Length);

            for (var y = 0; y < sourceBitmap.Height; y++)
            {
                var sourceRow = y * sourceData.Stride;
                var targetRow = y * targetData.Stride;
                for (var x = 0; x < sourceBitmap.Width; x++)
                {
                    var sourceIndex = sourceRow + (x * 4);
                    var targetIndex = targetRow + (x * 4);

                    targetBytes[targetIndex] = (byte)(255 - sourceBytes[sourceIndex]);
                    targetBytes[targetIndex + 1] = (byte)(255 - sourceBytes[sourceIndex + 1]);
                    targetBytes[targetIndex + 2] = (byte)(255 - sourceBytes[sourceIndex + 2]);
                    targetBytes[targetIndex + 3] = sourceBytes[sourceIndex + 3];
                }
            }

            Marshal.Copy(targetBytes, 0, targetData.Scan0, targetBytes.Length);
        }
        finally
        {
            sourceBitmap.UnlockBits(sourceData);
            bitmap.UnlockBits(targetData);
        }

        return bitmap;
    }

    private static Bitmap ExtractYellowGreenForeground(Bitmap source, int threshold)
    {
        using var sourceBitmap = CloneAs32BppArgb(source);
        var bitmap = new Bitmap(sourceBitmap.Width, sourceBitmap.Height, PixelFormat.Format32bppArgb);

        var sourceData = sourceBitmap.LockBits(
            new Rectangle(0, 0, sourceBitmap.Width, sourceBitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        var targetData = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var sourceBytes = new byte[Math.Abs(sourceData.Stride) * sourceBitmap.Height];
            var targetBytes = new byte[Math.Abs(targetData.Stride) * bitmap.Height];
            Marshal.Copy(sourceData.Scan0, sourceBytes, 0, sourceBytes.Length);

            for (var y = 0; y < sourceBitmap.Height; y++)
            {
                var sourceRow = y * sourceData.Stride;
                var targetRow = y * targetData.Stride;
                for (var x = 0; x < sourceBitmap.Width; x++)
                {
                    var sourceIndex = sourceRow + (x * 4);
                    var targetIndex = targetRow + (x * 4);

                    var blue = sourceBytes[sourceIndex];
                    var green = sourceBytes[sourceIndex + 1];
                    var red = sourceBytes[sourceIndex + 2];
                    var alpha = sourceBytes[sourceIndex + 3];
                    var emphasis = Math.Max(green, red) - (blue / 2);
                    var value = (byte)(emphasis >= threshold ? 255 : 0);

                    targetBytes[targetIndex] = value;
                    targetBytes[targetIndex + 1] = value;
                    targetBytes[targetIndex + 2] = value;
                    targetBytes[targetIndex + 3] = alpha;
                }
            }

            Marshal.Copy(targetBytes, 0, targetData.Scan0, targetBytes.Length);
        }
        finally
        {
            sourceBitmap.UnlockBits(sourceData);
            bitmap.UnlockBits(targetData);
        }

        return bitmap;
    }

    private static Bitmap CloneAs32BppArgb(Bitmap source)
    {
        var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        return bitmap;
    }

    private static int ClampToByte(int value)
    {
        return Math.Max(0, Math.Min(255, value));
    }

    private sealed record OcrVariant(string Name, Bitmap Bitmap);
}
