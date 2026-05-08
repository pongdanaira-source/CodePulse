using System.Drawing;
using System.Text.RegularExpressions;
using CodePulse.Integrations;
using CodePulse.Models;

namespace CodePulse.Services;

public sealed class OcrWorkflowService
{
    private const float MinimumOcrConfidence = 0.68f;
    private static readonly TimeSpan OcrNoticeThrottleWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BusinessDayStartTime = TimeSpan.FromHours(3);

    private readonly AppSettings _settings;
    private readonly OcrService _ocrService;
    private readonly CodeExtractorService _codeExtractorService;
    private readonly DailyCodeHistoryService _dailyCodeHistoryService;
    private readonly ChannelDuplicateGuard _duplicateGuard;
    private readonly WatchCoordinator _watchCoordinator;
    private readonly TelegramBotClient _telegramBotClient;
    private readonly Dictionary<Guid, OcrNoticeState> _ocrNoticeStates = new();
    private readonly Dictionary<Guid, DateTimeOffset> _ocrSpaceFallbackAttempts = new();
    private readonly HashSet<string> _sentSuspiciousAlertKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sentOcrProblemAlertKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _suspiciousAlertedChannelsThisSession = new();
    private readonly object _sync = new();

    public OcrWorkflowService(
        AppSettings settings,
        OcrService ocrService,
        CodeExtractorService codeExtractorService,
        DailyCodeHistoryService dailyCodeHistoryService,
        ChannelDuplicateGuard duplicateGuard,
        WatchCoordinator watchCoordinator,
        TelegramBotClient telegramBotClient)
    {
        _settings = settings;
        _ocrService = ocrService;
        _codeExtractorService = codeExtractorService;
        _dailyCodeHistoryService = dailyCodeHistoryService;
        _duplicateGuard = duplicateGuard;
        _watchCoordinator = watchCoordinator;
        _telegramBotClient = telegramBotClient;
    }

    public async Task<OwnerTextProcessingResult> ProcessCapturedBitmapAsync(
        ChannelProfile channel,
        Bitmap bitmap,
        CancellationToken cancellationToken,
        Action<string> emitLog,
        bool logStart = true,
        bool suppressNoChangeLogs = false,
        string? capturedImagePath = null,
        bool allowOcrSpaceFallback = false,
        bool useOcrSpaceFallbackForNoCode = true,
        TimeSpan? ocrSpaceFallbackCooldown = null)
    {
        if (logStart)
        {
            Log(emitLog, channel, "กำลัง OCR");
        }

        var ocrReadResult = await _ocrService.ReadAsync(bitmap, cancellationToken);
        var shouldDeferTesseractProblemAlert = allowOcrSpaceFallback && _settings.EnableOcrSpaceFallback;
        var result = await EvaluateOcrReadResultAsync(
            channel,
            ocrReadResult,
            cancellationToken,
            emitLog,
            suppressNoChangeLogs,
            capturedImagePath,
            providerName: "Tesseract",
            deferOcrProblemAlert: shouldDeferTesseractProblemAlert);

        if (!allowOcrSpaceFallback ||
            !ShouldUseOcrSpaceFallback(result, useOcrSpaceFallbackForNoCode) ||
            !_settings.EnableOcrSpaceFallback)
        {
            return result;
        }

        if (!TryReserveOcrSpaceFallback(channel.Id, ocrSpaceFallbackCooldown, out var remainingCooldown))
        {
            if (!suppressNoChangeLogs && remainingCooldown > TimeSpan.Zero)
            {
                Log(emitLog, channel, $"OCR.space fallback cooldown {remainingCooldown.TotalSeconds:0}s");
            }

            return result;
        }

        if (string.IsNullOrWhiteSpace(_settings.OcrSpaceApiKey))
        {
            Log(emitLog, channel, "OCR.space fallback skipped: API key is not configured");
            return result;
        }

        Log(emitLog, channel, "Tesseract ไม่มั่นใจ กำลังใช้ OCR.space");

        try
        {
            var ocrSpaceResult = await _ocrService.ReadWithOcrSpaceAsync(bitmap, cancellationToken);
            var usage = _ocrService.UsageSnapshot;
            Log(
                emitLog,
                channel,
                $"OCR.space usage: {usage.OcrSpaceDailyRequests}/{_settings.OcrSpaceDailyRequestGuard} today, {usage.OcrSpaceHourlyRequests}/{_settings.OcrSpaceHourlyRequestGuard} this hour");
            if (ocrSpaceResult.Passes.Count == 0)
            {
                Log(emitLog, channel, "OCR.space ไม่พบข้อความ");
                return result;
            }

            var mergedText = string.Join(" ", ocrSpaceResult.Passes
                .Select(static pass => pass.Text)
                .Where(static text => !string.IsNullOrWhiteSpace(text))
                .Distinct(StringComparer.Ordinal));

            Log(emitLog, channel, $"OCR.space ได้ข้อความ: {TrimForDebugLog(mergedText)}");
            return await EvaluateOcrReadResultAsync(
                channel,
                ocrSpaceResult,
                cancellationToken,
                emitLog,
                suppressNoChangeLogs: false,
                capturedImagePath: capturedImagePath,
                providerName: "OCR.space",
                deferOcrProblemAlert: false);
        }
        catch (Exception ex)
        {
            Log(emitLog, channel, $"OCR.space ล้มเหลว: {SummarizeException(ex)}");
            return result;
        }
    }

    private async Task<OwnerTextProcessingResult> EvaluateOcrReadResultAsync(
        ChannelProfile channel,
        OcrReadResult ocrReadResult,
        CancellationToken cancellationToken,
        Action<string> emitLog,
        bool suppressNoChangeLogs,
        string? capturedImagePath,
        string providerName,
        bool deferOcrProblemAlert)
    {
        if (ocrReadResult.Passes.Count == 0)
        {
            if (!suppressNoChangeLogs)
            {
                Log(emitLog, channel, "OCR ไม่พบข้อความ");
            }

            return new OwnerTextProcessingResult { Status = OwnerTextProcessingStatus.NoText };
        }

        var passCandidates = ocrReadResult.Passes
            .SelectMany(pass => _codeExtractorService
                .ExtractCandidates(channel, pass.Text, normalizeThaiCodeAliases: false)
                .Select(candidate => new
                {
                    Pass = pass,
                    Candidate = candidate
                }))
            .ToList();

        var shouldEmitDebugPassLogs = _settings.EnableOcrDebugLog && (!suppressNoChangeLogs || passCandidates.Count > 0);
        if (shouldEmitDebugPassLogs)
        {
            Log(emitLog, channel, $"OCR timing total {ocrReadResult.TotalElapsedMs:0}ms");
            foreach (var stage in ocrReadResult.StageMetrics)
            {
                Log(
                    emitLog,
                    channel,
                    $"OCR stage {stage.StageName} | {stage.ElapsedMs:0}ms | variants {stage.VariantCount} | psm {stage.PageSegModeCount} | passes {stage.PassCount}{(stage.ReturnedEarly ? " | early" : string.Empty)}");
            }

            foreach (var pass in ocrReadResult.Passes.OrderByDescending(static item => item.Confidence))
            {
                Log(emitLog, channel, $"OCR pass {pass.PassName} | {(pass.Confidence * 100):0}% | {TrimForDebugLog(pass.Text)}");
            }
        }

        var suspiciousBlob = FindSuspiciousBlob(channel, ocrReadResult);
        if (suspiciousBlob is not null)
        {
            var historyStrippedCandidates = TryExtractCandidatesAfterRemovingKnownCodes(
                channel,
                suspiciousBlob.Value.Text,
                DateTimeOffset.Now);

            if (historyStrippedCandidates.Count > 0)
            {
                var syntheticPass = new OcrPassResult
                {
                    PassName = "history-strip/SingleLine",
                    Text = suspiciousBlob.Value.Text,
                    Confidence = MinimumOcrConfidence
                };

                foreach (var candidate in historyStrippedCandidates)
                {
                    passCandidates.Add(new
                    {
                        Pass = syntheticPass,
                        Candidate = candidate
                    });
                }

                if (_settings.EnableOcrDebugLog)
                {
                    Log(
                        emitLog,
                        channel,
                        $"OCR history-strip recovered: {string.Join(", ", historyStrippedCandidates.Select(static item => item.Value))}");
                }
            }
        }

        if (passCandidates.Count == 0)
        {
            if (suspiciousBlob is not null)
            {
                await TrySendSuspiciousOcrAlertAsync(channel, suspiciousBlob.Value.Text, suspiciousBlob.Value.Reason, cancellationToken, emitLog);
            }

            if (!suppressNoChangeLogs)
            {
                Log(emitLog, channel, "OCR ไม่พบโค้ด");
            }

            return new OwnerTextProcessingResult { Status = OwnerTextProcessingStatus.NoCode };
        }

        var groupedCandidates = passCandidates
            .GroupBy(static item => item.Candidate!.Value, StringComparer.Ordinal)
            .Select(group => new
            {
                Code = group.Key,
                FirstIndex = group.Min(static item => item.Candidate!.SourceMessage
                    .ToUpperInvariant()
                    .IndexOf(item.Candidate!.Value, StringComparison.Ordinal)),
                Count = group.Count(),
                BestScore = group.Max(static item => item.Candidate!.Score),
                BestConfidence = group.Max(static item => item.Pass.Confidence),
                AverageConfidence = group.Average(static item => item.Pass.Confidence),
                SelectedPass = group
                    .OrderByDescending(static item => item.Candidate!.Score)
                    .ThenByDescending(static item => item.Pass.Confidence)
                    .First()
            })
            .OrderByDescending(static item => item.BestScore)
            .ThenByDescending(static item => item.Count)
            .ThenByDescending(static item => item.BestConfidence)
            .ThenByDescending(static item => item.AverageConfidence)
            .ToList();

        groupedCandidates = groupedCandidates
            .GroupBy(group => BuildCandidateFamilyKey(channel, group.Code), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var normalizedValue = GetNormalizedGenericFamilyValue(channel, group.First().Code);
                return group
                    .OrderByDescending(item =>
                    {
                        var exactPassTextValue = NormalizeCodeLikeText(item.SelectedPass.Pass.Text);
                        return exactPassTextValue.Length > 0 &&
                               item.Code.Equals(exactPassTextValue, StringComparison.OrdinalIgnoreCase);
                    })
                    .ThenByDescending(item => normalizedValue is not null &&
                                               item.Code.Equals(normalizedValue, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(item => item.BestScore)
                    .ThenByDescending(item => item.Count)
                    .ThenByDescending(item => item.BestConfidence)
                    .ThenByDescending(item => item.AverageConfidence)
                    .First();
            })
            .OrderByDescending(static item => item.BestScore)
            .ThenByDescending(static item => item.Count)
            .ThenByDescending(static item => item.BestConfidence)
            .ThenByDescending(static item => item.AverageConfidence)
            .ToList();

        var selectedGroup = groupedCandidates[0];
        var selectedPass = selectedGroup.SelectedPass;
        var secondGroup = groupedCandidates.Skip(1).FirstOrDefault();
        var isAmbiguous = secondGroup is not null &&
                          selectedGroup.BestScore == secondGroup.BestScore &&
                          selectedGroup.Count >= 2 &&
                          selectedGroup.Count == secondGroup.Count &&
                          Math.Abs(selectedGroup.BestConfidence - secondGroup.BestConfidence) < 0.08f;

        if (shouldEmitDebugPassLogs)
        {
            Log(
                emitLog,
                channel,
                $"OCR selected {selectedPass.Pass.PassName} | {(selectedPass.Pass.Confidence * 100):0}% | {selectedGroup.Code} | source {TrimForDebugLog(selectedPass.Pass.Text)}");
            LogRiskyCharacters(emitLog, channel, selectedGroup.Code, "OCR risky chars");
        }

        var dispatchableGroups = groupedCandidates
            .Where(group => providerName != "Tesseract" || group.BestConfidence >= MinimumOcrConfidence)
            .OrderBy(group => group.FirstIndex)
            .ThenByDescending(group => group.BestScore)
            .ThenByDescending(group => group.BestConfidence)
            .ToList();

        if (dispatchableGroups.Count > 1)
        {
            var multipleCodes = dispatchableGroups
                .Select(static group => group.Code)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var multipleCodeSignature = suppressNoChangeLogs
                ? "multiple-results"
                : string.Join(",", multipleCodes);

            if (ShouldLogOcrNotice(channel.Id, OwnerTextProcessingStatus.Ambiguous, multipleCodeSignature))
            {
                Log(emitLog, channel, "OCR พบหลายโค้ดในเฟรม ไม่ส่งอัตโนมัติ");
                Log(emitLog, channel, $"OCR candidates: {string.Join(", ", multipleCodes)}");
            }

            if (ShouldDispatchMultipleOcrCodes(providerName, selectedPass.Pass.Text, multipleCodes))
            {
                Log(emitLog, channel, "OCR candidates แยกชัดเจน กำลังส่งตรวจทีละโค้ด");
                var multiDispatchResult = await DispatchMultipleOcrCodesAsync(
                    channel,
                    multipleCodes,
                    selectedPass.Pass.Text,
                    capturedImagePath,
                    cancellationToken,
                    emitLog);

                if (multiDispatchResult.Status == OwnerTextProcessingStatus.Dispatched)
                {
                    ResetOcrNotice(channel.Id);
                }

                return multiDispatchResult;
            }

            if (!deferOcrProblemAlert)
            {
                await TrySendOcrProblemAlertAsync(
                    channel,
                    "OCR พบหลายโค้ดในเฟรม",
                    multipleCodes,
                    selectedPass.Pass.Text,
                    cancellationToken,
                    emitLog);
            }

            return new OwnerTextProcessingResult
            {
                Status = OwnerTextProcessingStatus.Ambiguous,
                Message = string.Join(", ", multipleCodes)
            };
        }

        var ambiguousNoticeSignature = suppressNoChangeLogs ? "multiple-results" : selectedGroup.Code;
        if (isAmbiguous && ShouldLogOcrNotice(channel.Id, OwnerTextProcessingStatus.Ambiguous, ambiguousNoticeSignature))
        {
            Log(emitLog, channel, "OCR ได้หลายผล กรุณาตรวจสอบ");
            Log(emitLog, channel, $"OCR candidate: {selectedGroup.Code}");
            LogRiskyCharacters(emitLog, channel, selectedGroup.Code, "Risky chars");
        }

        if (isAmbiguous)
        {
            if (!deferOcrProblemAlert)
            {
                await TrySendOcrProblemAlertAsync(
                    channel,
                    "OCR ได้หลายผลคะแนนใกล้กัน",
                    groupedCandidates.Take(3).Select(static group => group.Code).ToList(),
                    selectedPass.Pass.Text,
                    cancellationToken,
                    emitLog);
            }

            return new OwnerTextProcessingResult
            {
                Status = OwnerTextProcessingStatus.Ambiguous,
                Message = selectedGroup.Code
            };
        }

        if (_duplicateGuard.Contains(channel.Id, selectedGroup.Code))
        {
            return new OwnerTextProcessingResult
            {
                Status = OwnerTextProcessingStatus.Duplicate,
                Code = selectedGroup.Code
            };
        }

        if (providerName == "Tesseract" && selectedPass.Pass.Confidence < MinimumOcrConfidence)
        {
            if (suspiciousBlob is not null)
            {
                await TrySendSuspiciousOcrAlertAsync(channel, suspiciousBlob.Value.Text, suspiciousBlob.Value.Reason, cancellationToken, emitLog);
            }

            var lowConfidenceSignature = suppressNoChangeLogs
                ? "low-confidence"
                : $"{selectedGroup.Code}:{selectedPass.Pass.Confidence:0.00}";

            if (ShouldLogOcrNotice(channel.Id, OwnerTextProcessingStatus.LowConfidence, lowConfidenceSignature))
            {
                Log(emitLog, channel, "OCR ความมั่นใจต่ำ ไม่ส่งอัตโนมัติ");
                Log(emitLog, channel, $"OCR candidate: {selectedGroup.Code}");
                LogRiskyCharacters(emitLog, channel, selectedGroup.Code, "Risky chars");
            }

            return new OwnerTextProcessingResult
            {
                Status = OwnerTextProcessingStatus.LowConfidence,
                Message = $"{selectedPass.Pass.Confidence:0.00}"
            };
        }

        if (!suppressNoChangeLogs)
        {
            Log(emitLog, channel, "OCR เสร็จแล้ว กำลังส่งเข้าระบบตรวจโค้ด");
        }

        var result = await _watchCoordinator.ProcessDetectedCodeAsync(
            channel,
            selectedGroup.Code,
            selectedPass.Pass.Text,
            capturedImagePath,
            cancellationToken);
        ResetOcrNotice(channel.Id);

        switch (result.Status)
        {
            case OwnerTextProcessingStatus.NoText:
                if (!suppressNoChangeLogs)
                {
                    Log(emitLog, channel, "OCR ไม่พบข้อความ");
                }
                break;
            case OwnerTextProcessingStatus.TooShort:
                if (!suppressNoChangeLogs)
                {
                    Log(emitLog, channel, "OCR ได้ข้อความสั้นเกินไป");
                }
                break;
            case OwnerTextProcessingStatus.NoCode:
                if (!suppressNoChangeLogs)
                {
                    Log(emitLog, channel, "OCR ไม่พบโค้ด");
                }
                break;
            case OwnerTextProcessingStatus.LowConfidence:
            case OwnerTextProcessingStatus.Ambiguous:
            case OwnerTextProcessingStatus.Suspicious:
            case OwnerTextProcessingStatus.AlreadySentToday:
            case OwnerTextProcessingStatus.Duplicate:
                break;
            case OwnerTextProcessingStatus.DispatchFailed:
                Log(emitLog, channel, $"ส่งต่อจาก OCR ล้มเหลว: {result.Message}");
                break;
        }

        return result;
    }

    private async Task<OwnerTextProcessingResult> DispatchMultipleOcrCodesAsync(
        ChannelProfile channel,
        IReadOnlyList<string> codes,
        string sourceText,
        string? capturedImagePath,
        CancellationToken cancellationToken,
        Action<string> emitLog)
    {
        var dispatchedCodes = new List<string>();
        OwnerTextProcessingResult? firstSkippedResult = null;
        var now = DateTimeOffset.Now;
        var checkedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var code in codes)
        {
            if (!checkedCodes.Add(code))
            {
                firstSkippedResult ??= new OwnerTextProcessingResult
                {
                    Status = OwnerTextProcessingStatus.Duplicate,
                    Code = code
                };
                Log(emitLog, channel, $"OCR ข้ามโค้ดซ้ำใน candidates: {code}");
                continue;
            }

            if (_dailyCodeHistoryService.ContainsForCurrentBusinessDay(channel.Id, code, now))
            {
                firstSkippedResult ??= new OwnerTextProcessingResult
                {
                    Status = OwnerTextProcessingStatus.AlreadySentToday,
                    Code = code
                };
                Log(emitLog, channel, $"OCR ข้ามโค้ดเก่าของรอบวันนี้ก่อนส่ง: {code}");
                continue;
            }

            if (_duplicateGuard.Contains(channel.Id, code))
            {
                firstSkippedResult ??= new OwnerTextProcessingResult
                {
                    Status = OwnerTextProcessingStatus.Duplicate,
                    Code = code
                };
                Log(emitLog, channel, $"OCR ข้ามโค้ดซ้ำใน session ก่อนส่ง: {code}");
                continue;
            }

            var result = await _watchCoordinator.ProcessDetectedCodeAsync(
                channel,
                code,
                sourceText,
                capturedImagePath,
                cancellationToken);

            if (result.Status == OwnerTextProcessingStatus.Dispatched && !string.IsNullOrWhiteSpace(result.Code))
            {
                dispatchedCodes.Add(result.Code);
                continue;
            }

            firstSkippedResult ??= result;
        }

        if (dispatchedCodes.Count > 0)
        {
            return new OwnerTextProcessingResult
            {
                Status = OwnerTextProcessingStatus.Dispatched,
                Code = dispatchedCodes[0],
                Codes = dispatchedCodes,
                Message = string.Join(", ", dispatchedCodes)
            };
        }

        return firstSkippedResult ?? new OwnerTextProcessingResult
        {
            Status = OwnerTextProcessingStatus.NoCode
        };
    }

    private SuspiciousOcrBlob? FindSuspiciousBlob(ChannelProfile channel, OcrReadResult readResult)
    {
        var prefixes = PrefixRule.ParseMany(channel.Prefixes);
        if (prefixes.Count == 0)
        {
            return null;
        }

        foreach (var pass in readResult.Passes.OrderByDescending(static item => item.Text.Length))
        {
            foreach (Match tokenMatch in Regex.Matches(pass.Text.ToUpperInvariant(), @"[A-Z0-9]{15,}", RegexOptions.CultureInvariant))
            {
                if (!tokenMatch.Success)
                {
                    continue;
                }

                var token = tokenMatch.Value;
                foreach (var prefix in prefixes)
                {
                    var repeatedCount = CountOccurrences(token, prefix.Prefix);
                    if (repeatedCount >= 2)
                    {
                        return new SuspiciousOcrBlob(
                            token,
                            $"พบ prefix {prefix.Prefix} ซ้ำ {repeatedCount} ครั้งในก้อนเดียว");
                    }
                }
            }
        }

        return null;
    }

    private IReadOnlyList<CodeCandidate> TryExtractCandidatesAfterRemovingKnownCodes(
        ChannelProfile channel,
        string suspiciousText,
        DateTimeOffset now)
    {
        var knownCodes = _dailyCodeHistoryService.FindContainedCodesForCurrentBusinessDay(channel.Id, suspiciousText, now);
        if (knownCodes.Count == 0)
        {
            return [];
        }

        var normalizedText = suspiciousText.Trim().ToUpperInvariant();
        foreach (var knownCode in knownCodes)
        {
            normalizedText = normalizedText.Replace(knownCode.Trim().ToUpperInvariant(), " ", StringComparison.OrdinalIgnoreCase);
        }

        normalizedText = Regex.Replace(normalizedText, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return [];
        }

        return _codeExtractorService
            .ExtractCandidates(channel, normalizedText, normalizeThaiCodeAliases: false)
            .Where(candidate => knownCodes.All(knownCode => !candidate.Value.Equals(knownCode, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(static candidate => candidate.Value, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(static candidate => candidate.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task TrySendSuspiciousOcrAlertAsync(
        ChannelProfile channel,
        string suspiciousText,
        string reason,
        CancellationToken cancellationToken,
        Action<string> emitLog)
    {
        var knownCodes = _dailyCodeHistoryService.FindContainedCodesForCurrentBusinessDay(channel.Id, suspiciousText, DateTimeOffset.Now);
        var signature = $"suspicious:{reason}:{suspiciousText}";
        if (!ShouldLogOcrNotice(channel.Id, OwnerTextProcessingStatus.Suspicious, signature))
        {
            return;
        }

        if (!TryRegisterSuspiciousAlert(channel.Id, signature, DateTimeOffset.Now))
        {
            return;
        }

        if (!TryRegisterSuspiciousAlertForCurrentSession(channel.Id))
        {
            return;
        }

        Log(emitLog, channel, "OCR พบเคสแปลก ส่งเตือน Telegram only");

        if (string.IsNullOrWhiteSpace(_settings.Dispatch.TelegramBotToken) ||
            string.IsNullOrWhiteSpace(_settings.Dispatch.TelegramChatId))
        {
            Log(emitLog, channel, "ข้ามการเตือน Telegram เพราะยังไม่ได้ตั้งค่า Telegram");
            return;
        }

        try
        {
            _telegramBotClient.BotToken = _settings.Dispatch.TelegramBotToken;
            _telegramBotClient.ChatId = _settings.Dispatch.TelegramChatId;
            var knownCodesLine = knownCodes.Count > 0
                ? $"{Environment.NewLine}Known today: {string.Join(", ", knownCodes)}"
                : string.Empty;
            var riskyChars = DescribeRiskyCharacters(suspiciousText);
            var riskyCharsLine = string.IsNullOrWhiteSpace(riskyChars)
                ? string.Empty
                : $"{Environment.NewLine}Risky chars: {riskyChars}";
            await _telegramBotClient.SendMessageAsync(
                $"Suspicious OCR [{channel.Name}]{Environment.NewLine}Reason: {reason}{knownCodesLine}{riskyCharsLine}{Environment.NewLine}Text: {TrimForDebugLog(suspiciousText)}",
                suspiciousText,
                channel.Name,
                cancellationToken);
            Log(emitLog, channel, "ส่งเตือน Telegram สำเร็จ");
        }
        catch (Exception ex)
        {
            Log(emitLog, channel, $"ส่งเตือน Telegram ล้มเหลว: {SummarizeException(ex)}");
        }
    }

    private async Task TrySendOcrProblemAlertAsync(
        ChannelProfile channel,
        string reason,
        IReadOnlyList<string> candidates,
        string sourceText,
        CancellationToken cancellationToken,
        Action<string> emitLog)
    {
        var signature = $"ocr-problem:{reason}:{string.Join(",", candidates)}";
        if (!TryRegisterOcrProblemAlert(channel.Id, signature, DateTimeOffset.Now))
        {
            return;
        }

        Log(emitLog, channel, "OCR พบ Code มีปัญหา ส่งเตือน Telegram only");

        if (string.IsNullOrWhiteSpace(_settings.Dispatch.TelegramBotToken) ||
            string.IsNullOrWhiteSpace(_settings.Dispatch.TelegramChatId))
        {
            Log(emitLog, channel, "ข้ามการเตือน Telegram เพราะยังไม่ได้ตั้งค่า Telegram");
            return;
        }

        try
        {
            _telegramBotClient.BotToken = _settings.Dispatch.TelegramBotToken;
            _telegramBotClient.ChatId = _settings.Dispatch.TelegramChatId;
            var candidatesLine = candidates.Count > 0
                ? $"{Environment.NewLine}Candidates: {string.Join(", ", candidates)}"
                : string.Empty;
            await _telegramBotClient.SendMessageAsync(
                $"Code มีปัญหา [{channel.Name}]{Environment.NewLine}Reason: {reason}{candidatesLine}{Environment.NewLine}Text: {TrimForDebugLog(sourceText)}",
                sourceText,
                channel.Name,
                cancellationToken);
            Log(emitLog, channel, "ส่งเตือน Telegram สำเร็จ");
        }
        catch (Exception ex)
        {
            Log(emitLog, channel, $"ส่งเตือน Telegram ล้มเหลว: {SummarizeException(ex)}");
        }
    }

    private bool ShouldLogOcrNotice(Guid channelId, OwnerTextProcessingStatus status, string? signature)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.Now;
            if (_ocrNoticeStates.TryGetValue(channelId, out var previous))
            {
                if (previous.Status == status &&
                    string.Equals(previous.Signature, signature, StringComparison.Ordinal) &&
                    now - previous.LoggedAt < OcrNoticeThrottleWindow)
                {
                    return false;
                }
            }

            _ocrNoticeStates[channelId] = new OcrNoticeState(status, signature, now);
            return true;
        }
    }

    private void ResetOcrNotice(Guid channelId)
    {
        lock (_sync)
        {
            _ocrNoticeStates.Remove(channelId);
        }
    }

    private bool TryRegisterSuspiciousAlert(Guid channelId, string signature, DateTimeOffset now)
    {
        var key = $"{channelId:N}|{GetBusinessDate(now):yyyy-MM-dd}|{signature}";
        lock (_sync)
        {
            return _sentSuspiciousAlertKeys.Add(key);
        }
    }

    private bool TryRegisterOcrProblemAlert(Guid channelId, string signature, DateTimeOffset now)
    {
        var key = $"{channelId:N}|{GetBusinessDate(now):yyyy-MM-dd}|{signature}";
        lock (_sync)
        {
            return _sentOcrProblemAlertKeys.Add(key);
        }
    }

    private bool TryRegisterSuspiciousAlertForCurrentSession(Guid channelId)
    {
        lock (_sync)
        {
            return _suspiciousAlertedChannelsThisSession.Add(channelId);
        }
    }

    private bool TryReserveOcrSpaceFallback(
        Guid channelId,
        TimeSpan? cooldown,
        out TimeSpan remainingCooldown)
    {
        remainingCooldown = TimeSpan.Zero;
        if (cooldown is null || cooldown.Value <= TimeSpan.Zero)
        {
            return true;
        }

        lock (_sync)
        {
            var now = DateTimeOffset.Now;
            if (_ocrSpaceFallbackAttempts.TryGetValue(channelId, out var lastAttempt))
            {
                var elapsed = now - lastAttempt;
                if (elapsed < cooldown.Value)
                {
                    remainingCooldown = cooldown.Value - elapsed;
                    return false;
                }
            }

            _ocrSpaceFallbackAttempts[channelId] = now;
            return true;
        }
    }

    private static bool ShouldUseOcrSpaceFallback(
        OwnerTextProcessingResult result,
        bool useForNoCode)
    {
        return result.Status switch
        {
            OwnerTextProcessingStatus.LowConfidence => true,
            OwnerTextProcessingStatus.Ambiguous => true,
            OwnerTextProcessingStatus.NoText => useForNoCode,
            OwnerTextProcessingStatus.NoCode => useForNoCode,
            _ => false
        };
    }

    private static bool ShouldDispatchMultipleOcrCodes(
        string providerName,
        string sourceText,
        IReadOnlyList<string> codes)
    {
        if (!string.Equals(providerName, "OCR.space", StringComparison.OrdinalIgnoreCase) ||
            codes.Count < 2 ||
            codes.Count > 4)
        {
            return false;
        }

        var normalizedSource = NormalizeCodeLikeText(sourceText);
        if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            return false;
        }

        var searchStartIndex = 0;
        foreach (var code in codes)
        {
            var normalizedCode = NormalizeCodeLikeText(code);
            if (normalizedCode.Length < 10)
            {
                return false;
            }

            var matchIndex = normalizedSource.IndexOf(normalizedCode, searchStartIndex, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                return false;
            }

            searchStartIndex = matchIndex + normalizedCode.Length;
        }

        return true;
    }

    private static string TrimForDebugLog(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var flattened = value.ReplaceLineEndings(" ").Trim();
        return flattened.Length <= 160 ? flattened : flattened[..160] + "...";
    }

    private static string SummarizeException(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        var message = current.Message.ReplaceLineEndings(" ").Trim();
        return string.IsNullOrWhiteSpace(message) ? current.GetType().Name : message;
    }

    private static void Log(Action<string> emitLog, ChannelProfile channel, string message)
    {
        emitLog($"[{channel.Name}] {message}");
    }

    private static void LogRiskyCharacters(Action<string> emitLog, ChannelProfile channel, string text, string prefix)
    {
        var riskyChars = DescribeRiskyCharacters(text);
        if (!string.IsNullOrWhiteSpace(riskyChars))
        {
            Log(emitLog, channel, $"{prefix}: {riskyChars}");
        }
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var startIndex = 0;
        while (startIndex < source.Length)
        {
            var index = source.IndexOf(value, startIndex, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            count++;
            startIndex = index + 1;
        }

        return count;
    }

    private static string BuildCandidateFamilyKey(ChannelProfile channel, string code)
    {
        var normalizedGenericValue = GetNormalizedGenericFamilyValue(channel, code);
        return normalizedGenericValue is null
            ? $"EXACT|{code}"
            : $"GENERIC|{normalizedGenericValue}";
    }

    private static string? GetNormalizedGenericFamilyValue(ChannelProfile channel, string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 10)
        {
            return null;
        }

        var prefixes = PrefixRule.ParseMany(channel.Prefixes);
        if (prefixes.Any(prefix => code.StartsWith(prefix.Prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var chars = code.ToUpperInvariant().ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            chars[index] = chars[index] switch
            {
                'O' or 'Q' or 'G' => '9',
                _ => chars[index]
            };
        }

        return new string(chars);
    }

    private static string NormalizeCodeLikeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return new string(text
            .Trim()
            .ToUpperInvariant()
            .Where(static character => char.IsAsciiLetterUpper(character) || char.IsDigit(character))
            .ToArray());
    }

    private static string DescribeRiskyCharacters(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Trim().ToUpperInvariant();
        var riskyEntries = new List<string>();
        for (var index = 0; index < normalized.Length; index++)
        {
            var family = normalized[index] switch
            {
                'A' or '4' => "A/4",
                'O' or 'Q' or '0' or '9' => "O/Q/0/9",
                'G' or '6' => "G/6/9",
                'S' or '5' => "S/5",
                'B' or '8' => "B/8",
                'I' or 'L' or '1' => "I/L/1",
                'Z' or '2' => "Z/2",
                _ => null
            };

            if (family is not null)
            {
                riskyEntries.Add($"{family}@{index + 1}");
            }
        }

        return string.Join(", ", riskyEntries);
    }

    private static DateOnly GetBusinessDate(DateTimeOffset now)
    {
        var localDateTime = now.LocalDateTime;
        if (localDateTime.TimeOfDay < BusinessDayStartTime)
        {
            localDateTime = localDateTime.AddDays(-1);
        }

        return DateOnly.FromDateTime(localDateTime.Date);
    }

    private readonly record struct OcrNoticeState(
        OwnerTextProcessingStatus Status,
        string? Signature,
        DateTimeOffset LoggedAt);

    private readonly record struct SuspiciousOcrBlob(
        string Text,
        string Reason);
}
