using System.Collections.Concurrent;
using System.Text;
using CodePulse.Models;

namespace CodePulse.Services;

public sealed class AppLogService
{
    private const int MaxEntries = 1000;

    private readonly ConcurrentQueue<AppLogEntry> _entries = new();
    private readonly object _syncRoot = new();
    private string? _sessionLogPath;

    public event Action<AppLogEntry>? EntryEmitted;

    public string? CurrentSessionLogPath
    {
        get
        {
            lock (_syncRoot)
            {
                return _sessionLogPath;
            }
        }
    }

    public bool ConfigureDryRunSessionLogging(bool enabled, string logsRootPath)
    {
        lock (_syncRoot)
        {
            if (enabled)
            {
                if (!string.IsNullOrWhiteSpace(_sessionLogPath))
                {
                    return false;
                }

                Directory.CreateDirectory(logsRootPath);
                _sessionLogPath = Path.Combine(
                    logsRootPath,
                    $"dry-run-{DateTime.Now:yyyyMMdd-HHmmss}.log");

                SafeAppendToSessionLog(_sessionLogPath, BuildSessionMarker("BEGIN DRY RUN SESSION"));

                return true;
            }

            if (string.IsNullOrWhiteSpace(_sessionLogPath))
            {
                return false;
            }

            SafeAppendToSessionLog(_sessionLogPath, BuildSessionMarker("END DRY RUN SESSION"));
            _sessionLogPath = null;
            return true;
        }
    }

    public void Write(string message)
    {
        var entry = new AppLogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Message = message
        };

        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _))
        {
        }

        var sessionLogPath = CurrentSessionLogPath;
        if (!string.IsNullOrWhiteSpace(sessionLogPath))
        {
            SafeAppendToSessionLog(
                sessionLogPath,
                $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss}] {entry.Message}{Environment.NewLine}");
        }

        EntryEmitted?.Invoke(entry);
    }

    public IReadOnlyList<AppLogEntry> GetSnapshot()
    {
        return _entries.ToArray();
    }

    public void Clear()
    {
        while (_entries.TryDequeue(out _))
        {
        }
    }

    private static string BuildSessionMarker(string label)
    {
        return $"===== {label} {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====={Environment.NewLine}";
    }

    private void SafeAppendToSessionLog(string path, string content)
    {
        try
        {
            File.AppendAllText(path, content, Encoding.UTF8);
        }
        catch
        {
            lock (_syncRoot)
            {
                if (string.Equals(_sessionLogPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _sessionLogPath = null;
                }
            }
        }
    }
}
