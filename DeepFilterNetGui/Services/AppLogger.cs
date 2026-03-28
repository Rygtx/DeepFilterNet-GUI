using System.IO;

namespace DeepFilterNetGui.Services;

public enum LogLevel
{
    Trace,
    Debug,
    Info,
    Warning,
    Error
}

public sealed class LogEntry
{
    public LogEntry(DateTime timestamp, LogLevel level, string message, string? exception)
    {
        Timestamp = timestamp;
        Level = level;
        Message = message;
        Exception = exception;
    }

    public DateTime Timestamp { get; }
    public LogLevel Level { get; }
    public string Message { get; }
    public string? Exception { get; }

    public string ToLine()
    {
        return $"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level}] {Message}";
    }

    public override string ToString() => ToLine();
}

public static class AppLogger
{
    private static readonly object LockObj = new();
    private static StreamWriter? _writer;
    private static bool _fileEnabled;

    public static event Action<LogEntry>? Logged;

    public static string? LogFilePath { get; private set; }
    public static bool IsFileLoggingEnabled => _fileEnabled;

    public static void SetFileLoggingEnabled(bool enableFileLogging)
    {
        if (enableFileLogging == _fileEnabled)
            return;

        if (enableFileLogging)
        {
            Initialize(true);
            return;
        }

        lock (LockObj)
        {
            _writer?.Dispose();
            _writer = null;
        }
        _fileEnabled = false;
        LogFilePath = null;
        Info("文件日志已关闭。");
    }

    public static void Initialize(bool enableFileLogging)
    {
        if (_writer != null || _fileEnabled == enableFileLogging)
            return;

        _fileEnabled = enableFileLogging;
        if (enableFileLogging)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var logDir = Path.Combine(baseDir, "logs");
            Directory.CreateDirectory(logDir);
            LogFilePath = Path.Combine(logDir, $"deepfilternet3-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            _writer = new StreamWriter(File.Open(LogFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true
            };

            Info($"日志系统初始化，日志文件：{LogFilePath}");
        }
        else
        {
            LogFilePath = null;
            Info("日志系统初始化（文件输出已关闭）。");
        }
    }

    public static void Shutdown()
    {
        Info("日志系统关闭。");
        lock (LockObj)
        {
            _writer?.Dispose();
            _writer = null;
        }
        _fileEnabled = false;
    }

    public static void Trace(string message) => Write(LogLevel.Trace, message, null);
    public static void Debug(string message) => Write(LogLevel.Debug, message, null);
    public static void Info(string message) => Write(LogLevel.Info, message, null);
    public static void Warning(string message) => Write(LogLevel.Warning, message, null);
    public static void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);

    private static void Write(LogLevel level, string message, Exception? ex)
    {
        var entry = new LogEntry(DateTime.Now, level, message, ex?.ToString());
        lock (LockObj)
        {
            if (_writer != null)
            {
                _writer.WriteLine(entry.ToLine());
                if (!string.IsNullOrWhiteSpace(entry.Exception))
                {
                    _writer.WriteLine(entry.Exception);
                }
            }
        }

        Logged?.Invoke(entry);
    }
}

