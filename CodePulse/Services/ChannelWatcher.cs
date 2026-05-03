using System.Text.Json;
using System.Threading.Channels;
using CodePulse.Enums;
using CodePulse.Models;

namespace CodePulse.Services;

public sealed class ChannelWatcher
{
    private static readonly TimeSpan NoMessageWarningThreshold = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NoMessageWarningGraceWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BoostMonitorInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DomRecoveryThreshold = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ContainerRecoveryThreshold = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RecoveryCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DuplicateOwnerMessageSuppressionWindow = TimeSpan.FromSeconds(10);
    private const int MinimumMessageLength = 8;
    private const int MaxSeenMessageKeys = 4000;

    private readonly AppSettings _settings;
    private readonly CodeExtractorService _codeExtractorService;
    private readonly DailyCodeHistoryService _dailyCodeHistoryService;
    private readonly ChannelDuplicateGuard _duplicateGuard;
    private readonly DispatchService _dispatchService;
    private readonly Func<IHiddenWebViewHost> _hiddenWebViewHostFactory;
    private readonly Dictionary<Guid, WatchRuntime> _runningWatchers = new();
    private readonly object _sync = new();

    public ChannelWatcher(
        AppSettings settings,
        CodeExtractorService codeExtractorService,
        DailyCodeHistoryService dailyCodeHistoryService,
        ChannelDuplicateGuard duplicateGuard,
        DispatchService dispatchService,
        Func<IHiddenWebViewHost> hiddenWebViewHostFactory)
    {
        _settings = settings;
        _codeExtractorService = codeExtractorService;
        _dailyCodeHistoryService = dailyCodeHistoryService;
        _duplicateGuard = duplicateGuard;
        _dispatchService = dispatchService;
        _hiddenWebViewHostFactory = hiddenWebViewHostFactory;
    }

    public event Action<ChannelProfile, string>? StatusChanged;
    public event Action<CodeDetectedEvent>? CodeDetected;
    public event Action<CodeDetectedEvent>? CodeDispatched;

    public bool IsWatching(Guid channelId)
    {
        lock (_sync)
        {
            return _runningWatchers.ContainsKey(channelId);
        }
    }

    public void SetBoostMode(Guid channelId, bool enabled)
    {
        WatchRuntime? runtime;
        lock (_sync)
        {
            _runningWatchers.TryGetValue(channelId, out runtime);
            if (runtime is not null)
            {
                runtime.LowLatencyMode = enabled;
            }
        }

        if (runtime is null)
        {
            return;
        }

        _ = ApplyHostLowLatencyModeAsync(runtime, enabled);
    }

    public async Task<bool> StartAsync(ChannelProfile channel, string chatLink, CancellationToken cancellationToken)
    {
        _ = _settings;
        Stop(channel.Id);

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var observerQueue = Channel.CreateUnbounded<WatcherSignal>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var dispatchQueue = Channel.CreateUnbounded<CodeDetectedEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var host = _hiddenWebViewHostFactory();
        var session = new LiveSession
        {
            Channel = channel,
            ChatLink = chatLink,
            StartedAt = DateTimeOffset.Now,
            LastNewMessageAt = DateTimeOffset.Now,
            LastDomHealthyAt = DateTimeOffset.Now,
            LastChatContainerSeenAt = DateTimeOffset.Now
        };

        var runtime = new WatchRuntime(session, source, observerQueue, dispatchQueue, host);
        runtime.ObserverTask = Task.Run(() => ProcessObserverQueueAsync(observerQueue.Reader, dispatchQueue.Writer, session, source.Token), source.Token);
        runtime.MonitorTask = Task.Run(() => MonitorAsync(runtime, source.Token), source.Token);
        runtime.DispatchTask = Task.Run(() => ProcessDispatchQueueAsync(channel, dispatchQueue.Reader, source.Token), source.Token);

        host.ObserverMessageReceived += message => observerQueue.Writer.TryWrite(new WatcherSignal(WatcherSignalKind.ObserverMessage, message));
        host.NavigationFailed += error => observerQueue.Writer.TryWrite(new WatcherSignal(WatcherSignalKind.NavigationFailed, error));
        host.BrowserProcessFailed += error => observerQueue.Writer.TryWrite(new WatcherSignal(WatcherSignalKind.BrowserFailed, error));
        host.DebugLogEmitted += message => EmitStatus(channel, message);

        lock (_sync)
        {
            _runningWatchers[channel.Id] = runtime;
        }

        try
        {
            channel.Status = SessionState.LoadingChat;
            EmitStatus(channel, "กำลังโหลดหน้าแชท");

            EmitStatus(channel, "host: initialize");
            await host.InitializeAsync(source.Token);
            EmitStatus(channel, $"host: navigate to {chatLink}");
            var ready = await host.NavigateAndWaitUntilReadyAsync(chatLink, source.Token);
            if (!ready)
            {
                channel.Status = SessionState.Error;
                EmitStatus(channel, "โหลดหน้าแชทไม่สำเร็จ");
                Stop(channel.Id);
                return false;
            }

            session.PageTitle = host.DocumentTitle;
            if (channel.IsBoosting)
            {
                runtime.LowLatencyMode = true;
                await host.SetLowLatencyModeAsync(true, source.Token);
            }

            channel.Status = SessionState.Watching;
            EmitStatus(channel, "โหลดหน้าแชทสำเร็จ");
            EmitStatus(channel, "เริ่มฟังแชท");
            return true;
        }
        catch (OperationCanceledException)
        {
            Stop(channel.Id);
            return false;
        }
        catch (Exception ex)
        {
            channel.Status = SessionState.Error;
            EmitStatus(channel, $"โหลดหน้าแชทไม่สำเร็จ: {Summarize(ex)}");
            Stop(channel.Id);
            return false;
        }
    }

    public async Task<OwnerTextProcessingResult> ProcessExternalOwnerTextAsync(ChannelProfile channel, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new OwnerTextProcessingResult { Status = OwnerTextProcessingStatus.NoText };
        }

        var trimmedText = text.Trim();
        if (trimmedText.Length < MinimumMessageLength)
        {
            return new OwnerTextProcessingResult { Status = OwnerTextProcessingStatus.TooShort };
        }

        var candidates = _codeExtractorService.ExtractCandidates(channel, trimmedText);
        if (candidates.Count == 0)
        {
            return new OwnerTextProcessingResult { Status = OwnerTextProcessingStatus.NoCode };
        }

        var detectedAt = DateTimeOffset.Now;
        var dispatchedCodes = new List<string>();
        OwnerTextProcessingResult? firstSkippedResult = null;

        foreach (var candidate in candidates)
        {
            var creation = TryCreateDetectedEvent(channel, candidate, trimmedText, detectedAt);
            if (creation.DetectedEvent is null)
            {
                firstSkippedResult ??= creation.Result;
                continue;
            }

            try
            {
                var dispatchSucceeded = await DispatchDetectedEventAsync(creation.DetectedEvent, cancellationToken);
                if (!dispatchSucceeded)
                {
                    firstSkippedResult ??= new OwnerTextProcessingResult
                    {
                        Status = OwnerTextProcessingStatus.DispatchFailed,
                        Code = creation.DetectedEvent.Candidate.Value,
                        Message = "ไม่มีปลายทางใดส่งสำเร็จ"
                    };
                    continue;
                }

                dispatchedCodes.Add(creation.DetectedEvent.Candidate.Value);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                channel.Status = SessionState.Error;
                EmitStatus(channel, $"เกิดข้อผิดพลาดระหว่างส่งต่อ: {Summarize(ex)}");
                return new OwnerTextProcessingResult
                {
                    Status = OwnerTextProcessingStatus.DispatchFailed,
                    Code = creation.DetectedEvent.Candidate.Value,
                    Codes = dispatchedCodes,
                    Message = Summarize(ex)
                };
            }
        }

        if (dispatchedCodes.Count > 0)
        {
            return new OwnerTextProcessingResult
            {
                Status = OwnerTextProcessingStatus.Dispatched,
                Code = dispatchedCodes[0],
                Codes = dispatchedCodes
            };
        }

        return firstSkippedResult ?? new OwnerTextProcessingResult { Status = OwnerTextProcessingStatus.NoCode };
    }

    public async Task<OwnerTextProcessingResult> ProcessExternalDetectedCodeAsync(
        ChannelProfile channel,
        string code,
        string sourceMessage,
        string? capturedImagePath,
        CancellationToken cancellationToken)
    {
        var normalizedCode = code.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            return new OwnerTextProcessingResult { Status = OwnerTextProcessingStatus.NoCode };
        }

        var candidate = new CodeCandidate
        {
            Value = normalizedCode,
            Score = int.MaxValue,
            Reason = "ocr-selected",
            SourceMessage = sourceMessage
        };

        var creation = TryCreateDetectedEvent(channel, candidate, sourceMessage, DateTimeOffset.Now, capturedImagePath: capturedImagePath);
        if (creation.DetectedEvent is null)
        {
            return creation.Result;
        }

        try
        {
            var dispatchSucceeded = await DispatchDetectedEventAsync(creation.DetectedEvent, cancellationToken);
            if (!dispatchSucceeded)
            {
                return new OwnerTextProcessingResult
                {
                    Status = OwnerTextProcessingStatus.DispatchFailed,
                    Code = creation.DetectedEvent.Candidate.Value,
                    Message = "ไม่มีปลายทางใดส่งสำเร็จ"
                };
            }

            return new OwnerTextProcessingResult
            {
                Status = OwnerTextProcessingStatus.Dispatched,
                Code = creation.DetectedEvent.Candidate.Value
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            channel.Status = SessionState.Error;
            EmitStatus(channel, $"เกิดข้อผิดพลาดระหว่างส่งต่อ: {Summarize(ex)}");
            return new OwnerTextProcessingResult
            {
                Status = OwnerTextProcessingStatus.DispatchFailed,
                Code = creation.DetectedEvent.Candidate.Value,
                Message = Summarize(ex)
            };
        }
    }

    public void Stop(Guid channelId)
    {
        WatchRuntime? runtime;
        lock (_sync)
        {
            _runningWatchers.TryGetValue(channelId, out runtime);
            _runningWatchers.Remove(channelId);
        }

        if (runtime is null)
        {
            _duplicateGuard.Reset(channelId);
            return;
        }

        runtime.CancellationTokenSource.Cancel();
        runtime.ObserverQueue.Writer.TryComplete();
        runtime.DispatchQueue.Writer.TryComplete();
        runtime.Host.DisposeHost();
        _duplicateGuard.Reset(channelId);
    }

    private async Task ProcessObserverQueueAsync(
        ChannelReader<WatcherSignal> reader,
        ChannelWriter<CodeDetectedEvent> dispatchWriter,
        LiveSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var signal in reader.ReadAllAsync(cancellationToken))
            {
                switch (signal.Kind)
                {
                    case WatcherSignalKind.ObserverMessage:
                        ProcessObserverMessage(signal.Payload, dispatchWriter, session);
                        break;
                    case WatcherSignalKind.NavigationFailed:
                        session.State = SessionState.Error;
                        session.Channel.Status = SessionState.Error;
                        EmitStatus(session.Channel, $"โหลดหน้าแชทไม่สำเร็จ: {signal.Payload}");
                        Stop(session.Channel.Id);
                        return;
                    case WatcherSignalKind.BrowserFailed:
                        session.State = SessionState.Error;
                        session.Channel.Status = SessionState.Error;
                        EmitStatus(session.Channel, $"หน้าแชทใช้งานไม่ได้: {signal.Payload}");
                        Stop(session.Channel.Id);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            session.Channel.Status = SessionState.Error;
            EmitStatus(session.Channel, $"เกิดข้อผิดพลาดระหว่างอ่านหน้าแชท: {Summarize(ex)}");
        }
    }

    private void ProcessObserverMessage(string payload, ChannelWriter<CodeDetectedEvent> dispatchWriter, LiveSession session)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var typeElement))
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var type = typeElement.GetString();
        switch (type)
        {
            case "ready":
                session.LastDomHealthyAt = now;
                session.LastChatContainerSeenAt = now;
                if (!string.Equals(session.Channel.LastStatusMessage, "ข้ามข้อความเก่าก่อนเริ่มฟัง", StringComparison.Ordinal))
                {
                    EmitStatus(session.Channel, "ข้ามข้อความเก่าก่อนเริ่มฟัง");
                }
                break;

            case "health":
                var appExists = root.TryGetProperty("appExists", out var appExistsElement) && appExistsElement.GetBoolean();
                var containerExists = root.TryGetProperty("containerExists", out var containerExistsElement) && containerExistsElement.GetBoolean();
                var domHealthy = root.TryGetProperty("domHealthy", out var domHealthyElement) && domHealthyElement.GetBoolean();

                if (domHealthy || appExists)
                {
                    session.LastDomHealthyAt = now;
                }

                if (containerExists)
                {
                    session.LastChatContainerSeenAt = now;
                }
                break;

            case "activity":
                MarkChatActivity(session, now);
                break;

            case "message":
                ProcessChatMessage(root, dispatchWriter, session, now);
                break;
        }
    }

    private void ProcessChatMessage(JsonElement root, ChannelWriter<CodeDetectedEvent> dispatchWriter, LiveSession session, DateTimeOffset now)
    {
        var key = root.TryGetProperty("key", out var keyElement)
            ? keyElement.GetString() ?? string.Empty
            : string.Empty;

        if (string.IsNullOrWhiteSpace(key) || !TryRegisterMessageKey(session.Channel.Id, key))
        {
            return;
        }

        MarkChatActivity(session, now);

        var text = root.TryGetProperty("text", out var textElement)
            ? textElement.GetString() ?? string.Empty
            : string.Empty;
        var isOwner = root.TryGetProperty("isOwner", out var ownerElement) && ownerElement.GetBoolean();

        if (!isOwner || string.IsNullOrWhiteSpace(text) || text.Length < MinimumMessageLength)
        {
            return;
        }

        if (ShouldSuppressOwnerMessage(session.Channel.Id, text, now))
        {
            return;
        }

        EmitStatus(session.Channel, $"ได้รับข้อความจากเจ้าของช่อง: {TrimForLog(text)}");

        var creation = TryCreateDetectedEvent(session.Channel, text, now, session);
        if (creation.DetectedEvent is null)
        {
            return;
        }

        dispatchWriter.TryWrite(creation.DetectedEvent);
    }

    private (CodeDetectedEvent? DetectedEvent, OwnerTextProcessingResult Result) TryCreateDetectedEvent(
        ChannelProfile channel,
        string text,
        DateTimeOffset detectedAt,
        LiveSession? session = null)
    {
        var candidates = _codeExtractorService.ExtractCandidates(channel, text);
        if (candidates.Count == 0)
        {
            return (null, new OwnerTextProcessingResult { Status = OwnerTextProcessingStatus.NoCode });
        }

        CodeCandidate? firstOldToday = null;
        CodeCandidate? firstDuplicate = null;
        CodeCandidate? selectedCandidate = null;

        foreach (var candidate in candidates)
        {
            if (_dailyCodeHistoryService.ContainsForCurrentBusinessDay(channel.Id, candidate.Value, detectedAt))
            {
                firstOldToday ??= candidate;
                continue;
            }

            if (_duplicateGuard.Contains(channel.Id, candidate.Value))
            {
                firstDuplicate ??= candidate;
                continue;
            }

            selectedCandidate = candidate;
            break;
        }

        if (selectedCandidate is null && firstOldToday is not null)
        {
            EmitStatus(channel, $"โค้ดเก่าของรอบวันนี้ {firstOldToday.Value}");
            return (null, new OwnerTextProcessingResult
            {
                Status = OwnerTextProcessingStatus.AlreadySentToday,
                Code = firstOldToday.Value
            });
        }

        if (selectedCandidate is null && firstDuplicate is not null)
        {
            return (null, new OwnerTextProcessingResult
            {
                Status = OwnerTextProcessingStatus.Duplicate,
                Code = firstDuplicate.Value
            });
        }

        if (selectedCandidate is null)
        {
            return (null, new OwnerTextProcessingResult { Status = OwnerTextProcessingStatus.NoCode });
        }

        if (!_duplicateGuard.TryRegister(channel.Id, selectedCandidate.Value))
        {
            return (null, new OwnerTextProcessingResult
            {
                Status = OwnerTextProcessingStatus.Duplicate,
                Code = selectedCandidate.Value
            });
        }

        EmitStatus(channel, $"พบโค้ด {selectedCandidate.Value}");

        var detectedEvent = new CodeDetectedEvent
        {
            Channel = channel,
            Session = session ?? new LiveSession
            {
                Channel = channel,
                ChatLink = channel.ChatLink,
                StartedAt = detectedAt
            },
            Candidate = selectedCandidate,
            SourceMessage = text,
            DetectedAt = detectedAt
        };

        CodeDetected?.Invoke(detectedEvent);
        return (detectedEvent, new OwnerTextProcessingResult
        {
            Status = OwnerTextProcessingStatus.Dispatched,
            Code = selectedCandidate.Value
        });
    }

    private (CodeDetectedEvent? DetectedEvent, OwnerTextProcessingResult Result) TryCreateDetectedEvent(
        ChannelProfile channel,
        CodeCandidate candidate,
        string sourceMessage,
        DateTimeOffset detectedAt,
        LiveSession? session = null,
        string? capturedImagePath = null)
    {
        if (_dailyCodeHistoryService.ContainsForCurrentBusinessDay(channel.Id, candidate.Value, detectedAt))
        {
            EmitStatus(channel, $"โค้ดเก่าของรอบวันนี้ {candidate.Value}");
            return (null, new OwnerTextProcessingResult
            {
                Status = OwnerTextProcessingStatus.AlreadySentToday,
                Code = candidate.Value
            });
        }

        if (_duplicateGuard.Contains(channel.Id, candidate.Value))
        {
            return (null, new OwnerTextProcessingResult
            {
                Status = OwnerTextProcessingStatus.Duplicate,
                Code = candidate.Value
            });
        }

        if (!_duplicateGuard.TryRegister(channel.Id, candidate.Value))
        {
            return (null, new OwnerTextProcessingResult
            {
                Status = OwnerTextProcessingStatus.Duplicate,
                Code = candidate.Value
            });
        }

        EmitStatus(channel, $"พบโค้ด {candidate.Value}");

        var detectedEvent = new CodeDetectedEvent
        {
            Channel = channel,
            Session = session ?? new LiveSession
            {
                Channel = channel,
                ChatLink = channel.ChatLink,
                StartedAt = detectedAt
            },
            Candidate = candidate,
            SourceMessage = sourceMessage,
            DetectedAt = detectedAt,
            CapturedImagePath = capturedImagePath,
            IsOcrSource = string.Equals(candidate.Reason, "ocr-selected", StringComparison.Ordinal)
        };

        CodeDetected?.Invoke(detectedEvent);
        return (detectedEvent, new OwnerTextProcessingResult
        {
            Status = OwnerTextProcessingStatus.Dispatched,
            Code = candidate.Value
        });
    }

    private async Task MonitorAsync(WatchRuntime runtime, CancellationToken cancellationToken)
    {
        var session = runtime.Session;
        try
        {
            while (true)
            {
                await Task.Delay(runtime.LowLatencyMode ? BoostMonitorInterval : MonitorInterval, cancellationToken);

                var now = DateTimeOffset.Now;
                var noMessageDuration = now - session.LastNewMessageAt;
                var domStaleDuration = now - session.LastDomHealthyAt;
                var containerStaleDuration = now - session.LastChatContainerSeenAt;

                if (domStaleDuration >= DomRecoveryThreshold ||
                    containerStaleDuration >= ContainerRecoveryThreshold)
                {
                    await TryRecoverChatDomAsync(runtime, now, cancellationToken);
                }

                if (noMessageDuration >= NoMessageWarningThreshold + NoMessageWarningGraceWindow &&
                    session.Channel.Status != SessionState.NoMessages)
                {
                    session.Channel.Status = SessionState.NoMessages;
                    session.LastNoMessagesAt = now;
                    EmitStatus(session.Channel, "ไม่มีข้อความใหม่ แต่ยังเฝ้าแชทต่อ");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            session.Channel.Status = SessionState.Error;
            EmitStatus(session.Channel, $"เกิดข้อผิดพลาดระหว่างตรวจสถานะหน้าแชท: {Summarize(ex)}");
        }
    }

    private async Task ApplyHostLowLatencyModeAsync(WatchRuntime runtime, bool enabled)
    {
        try
        {
            await runtime.Host.SetLowLatencyModeAsync(enabled, runtime.CancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            EmitStatus(runtime.Session.Channel, $"Boost low-latency mode failed: {Summarize(ex)}");
        }
    }

    private void MarkChatActivity(LiveSession session, DateTimeOffset now)
    {
        session.LastNewMessageAt = now;
        session.LastDomHealthyAt = now;
        session.LastChatContainerSeenAt = now;

        if (session.Channel.Status != SessionState.NoMessages)
        {
            return;
        }

        session.Channel.Status = SessionState.Watching;
        var noMessagesAt = session.LastNoMessagesAt;
        session.LastNoMessagesAt = null;

        if (noMessagesAt is null || now - noMessagesAt.Value >= NoMessageWarningGraceWindow)
        {
            EmitStatus(session.Channel, "มีข้อความใหม่ กลับมาเฝ้าแชทต่อ");
        }
    }

    private async Task TryRecoverChatDomAsync(WatchRuntime runtime, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (runtime.RecoveryInProgress)
        {
            return;
        }

        if (runtime.LastRecoveryAttemptAt is not null &&
            now - runtime.LastRecoveryAttemptAt.Value < RecoveryCooldown)
        {
            return;
        }

        runtime.RecoveryInProgress = true;
        runtime.LastRecoveryAttemptAt = now;
        try
        {
            EmitStatus(runtime.Session.Channel, "หน้าแชทไม่ตอบสนอง กำลังรีโหลด");
            var reloaded = await runtime.Host.ReloadAndWaitUntilReadyAsync(cancellationToken);
            if (!reloaded)
            {
                EmitStatus(runtime.Session.Channel, "รีโหลดหน้าแชทไม่สำเร็จ แต่ยังเฝ้าต่อ");
                return;
            }

            var recoveredAt = DateTimeOffset.Now;
            runtime.Session.LastDomHealthyAt = recoveredAt;
            runtime.Session.LastChatContainerSeenAt = recoveredAt;
            runtime.Session.PageTitle = runtime.Host.DocumentTitle;

            if (runtime.Session.Channel.Status == SessionState.NoMessages)
            {
                EmitStatus(runtime.Session.Channel, "แชทกลับมาทำงานแล้ว ยังเฝ้าต่อ");
            }
            else
            {
                EmitStatus(runtime.Session.Channel, "รีโหลดหน้าแชทสำเร็จ ยังเฝ้าต่อ");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            EmitStatus(runtime.Session.Channel, $"รีโหลดหน้าแชทไม่สำเร็จ: {Summarize(ex)}");
        }
        finally
        {
            runtime.RecoveryInProgress = false;
        }
    }

    private async Task ProcessDispatchQueueAsync(ChannelProfile channel, ChannelReader<CodeDetectedEvent> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var detectedEvent in reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    var dispatchSucceeded = await DispatchDetectedEventAsync(detectedEvent, cancellationToken);
                    if (!dispatchSucceeded)
                    {
                        EmitStatus(channel, $"ส่งต่อไม่สำเร็จสำหรับโค้ด {detectedEvent.Candidate.Value}");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    EmitStatus(channel, $"เกิดข้อผิดพลาดระหว่างส่งต่อ: {Summarize(ex)}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            EmitStatus(channel, $"เกิดข้อผิดพลาดในคิวส่งต่อ: {Summarize(ex)}");
        }
    }

    private async Task<bool> DispatchDetectedEventAsync(CodeDetectedEvent detectedEvent, CancellationToken cancellationToken)
    {
        try
        {
            var dispatchSucceeded = await _dispatchService.DispatchAsync(detectedEvent, cancellationToken);
            if (!dispatchSucceeded)
            {
                _duplicateGuard.Unregister(detectedEvent.Channel.Id, detectedEvent.Candidate.Value);
                return false;
            }

            _dailyCodeHistoryService.RegisterForCurrentBusinessDay(
                detectedEvent.Channel.Id,
                detectedEvent.Candidate.Value,
                detectedEvent.DetectedAt);
            CodeDispatched?.Invoke(detectedEvent);
            return true;
        }
        catch (OperationCanceledException)
        {
            _duplicateGuard.Unregister(detectedEvent.Channel.Id, detectedEvent.Candidate.Value);
            throw;
        }
        catch
        {
            _duplicateGuard.Unregister(detectedEvent.Channel.Id, detectedEvent.Candidate.Value);
            throw;
        }
    }

    private void EmitStatus(ChannelProfile channel, string message)
    {
        channel.LastCheckedAt = DateTimeOffset.Now;
        channel.LastStatusMessage = message;
        StatusChanged?.Invoke(channel, message);
    }

    private bool TryRegisterMessageKey(Guid channelId, string key)
    {
        lock (_sync)
        {
            if (!_runningWatchers.TryGetValue(channelId, out var runtime))
            {
                return false;
            }

            if (!runtime.SeenMessageKeys.Add(key))
            {
                return false;
            }

            runtime.SeenMessageKeyOrder.Enqueue(key);
            TrimSeenMessageKeys(runtime);
            return true;
        }
    }

    private bool ShouldSuppressOwnerMessage(Guid channelId, string text, DateTimeOffset now)
    {
        var normalizedText = text.Trim();
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return true;
        }

        lock (_sync)
        {
            if (!_runningWatchers.TryGetValue(channelId, out var runtime))
            {
                return false;
            }

            TrimRecentOwnerMessages(runtime, now);

            if (runtime.RecentOwnerMessages.TryGetValue(normalizedText, out var previousSeenAt) &&
                now - previousSeenAt <= DuplicateOwnerMessageSuppressionWindow)
            {
                return true;
            }

            runtime.RecentOwnerMessages[normalizedText] = now;
            return false;
        }
    }

    private static void TrimSeenMessageKeys(WatchRuntime runtime)
    {
        while (runtime.SeenMessageKeys.Count > MaxSeenMessageKeys &&
               runtime.SeenMessageKeyOrder.TryDequeue(out var oldestKey))
        {
            runtime.SeenMessageKeys.Remove(oldestKey);
        }
    }

    private static void TrimRecentOwnerMessages(WatchRuntime runtime, DateTimeOffset now)
    {
        if (runtime.RecentOwnerMessages.Count == 0)
        {
            return;
        }

        var expiredKeys = runtime.RecentOwnerMessages
            .Where(pair => now - pair.Value > DuplicateOwnerMessageSuppressionWindow)
            .Select(static pair => pair.Key)
            .ToList();

        foreach (var expiredKey in expiredKeys)
        {
            runtime.RecentOwnerMessages.Remove(expiredKey);
        }
    }

    private static string TrimForLog(string text)
    {
        const int maxLength = 120;
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    private static string Summarize(Exception ex)
    {
        var message = ex.Message.ReplaceLineEndings(" ").Trim();
        return string.IsNullOrWhiteSpace(message) ? "ไม่ทราบสาเหตุ" : message;
    }

    private sealed class WatchRuntime
    {
        public WatchRuntime(
            LiveSession session,
            CancellationTokenSource cancellationTokenSource,
            Channel<WatcherSignal> observerQueue,
            Channel<CodeDetectedEvent> dispatchQueue,
            IHiddenWebViewHost host)
        {
            Session = session;
            CancellationTokenSource = cancellationTokenSource;
            ObserverQueue = observerQueue;
            DispatchQueue = dispatchQueue;
            Host = host;
        }

        public LiveSession Session { get; }
        public CancellationTokenSource CancellationTokenSource { get; }
        public Channel<WatcherSignal> ObserverQueue { get; }
        public Channel<CodeDetectedEvent> DispatchQueue { get; }
        public IHiddenWebViewHost Host { get; }
        public HashSet<string> SeenMessageKeys { get; } = new(StringComparer.Ordinal);
        public Queue<string> SeenMessageKeyOrder { get; } = new();
        public Dictionary<string, DateTimeOffset> RecentOwnerMessages { get; } = new(StringComparer.Ordinal);
        public Task? ObserverTask { get; set; }
        public Task? MonitorTask { get; set; }
        public Task? DispatchTask { get; set; }
        public DateTimeOffset? LastRecoveryAttemptAt { get; set; }
        public bool RecoveryInProgress { get; set; }
        public bool LowLatencyMode { get; set; }
    }

    private readonly record struct WatcherSignal(WatcherSignalKind Kind, string Payload);

    private enum WatcherSignalKind
    {
        ObserverMessage,
        NavigationFailed,
        BrowserFailed
    }
}
