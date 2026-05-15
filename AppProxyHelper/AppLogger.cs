namespace AppProxyHelper;

public enum LogLevel
{
    Debug = 0,
    Information = 1,
    Warning = 2,
    Error = 3
}

public sealed class AppLogger : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;
    private readonly LogLevel _minimumLevel;
    private bool _disposed;

    private AppLogger(string logFilePath, LogLevel minimumLevel)
    {
        LogFilePath = logFilePath;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
        _writer = new StreamWriter(new FileStream(logFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    public string LogFilePath { get; }
    public event Action<LogLevel, string>? MessageWritten;

    public static AppLogger Create(LoadedConfig loadedConfig)
    {
        var config = loadedConfig.Value;
        var logDirectory = PathResolver.Resolve(config.LogDirectory, loadedConfig.BaseDirectory);
        var logFile = Path.Combine(logDirectory, $"app-proxy-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");
        var minimumLevel = ParseLevel(config.MinimumLogLevel);
        return new AppLogger(logFile, minimumLevel);
    }

    public void Debug(string message) => Write(LogLevel.Debug, message);

    public void Info(string message) => Write(LogLevel.Information, message);

    public void Warn(string message) => Write(LogLevel.Warning, message);

    public void Error(string message, Exception? exception = null) => Write(LogLevel.Error, message, exception);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            _writer.Dispose();
            _disposed = true;
        }
    }

    private static LogLevel ParseLevel(string value)
    {
        return Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level)
            ? level
            : LogLevel.Information;
    }

    private void Write(LogLevel level, string message, Exception? exception = null)
    {
        if (level < _minimumLevel)
        {
            return;
        }

        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
        var line = $"{timestamp} [{level}] {message}";

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            Console.WriteLine(line);
            _writer.WriteLine(line);

            if (exception is not null)
            {
                _writer.WriteLine(exception);
                Console.WriteLine(exception.Message);
            }
        }

        MessageWritten?.Invoke(level, exception is null ? line : $"{line}{Environment.NewLine}{exception}");
    }
}
