using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Ims.Core.Diagnostics;

/// <summary>
/// Writes log entries to a local daily file.
/// </summary>
/// <remarks>
/// <para>
/// NFR-10: "Log application errors locally in a form useful for debugging, subject
/// to PR-6.3." Local is the operative word — this writes to disk on the user's own
/// machine and nowhere else, which is also what PR-6.5 requires.
/// </para>
/// <para>
/// Deliberately small. A logging framework would be a dependency to justify, and
/// the requirement is one text file a developer can open.
/// </para>
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);
    private readonly Lock _writeLock = new();
    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly long _maximumBytes;

    public FileLoggerProvider(
        string directory,
        LogLevel minimumLevel = LogLevel.Information,
        long maximumBytes = 8 * 1024 * 1024)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _minimumLevel = minimumLevel;
        _maximumBytes = maximumBytes;

        Directory.CreateDirectory(_directory);
    }

    /// <summary>The default location: <c>%LOCALAPPDATA%\IMS\logs</c>.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IMS",
        "logs");

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void Dispose() => _loggers.Clear();

    private void Write(string categoryName, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        var builder = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(Abbreviate(level)).Append("] ")
            .Append(categoryName);

        if (eventId.Id != 0)
        {
            builder.Append('(').Append(eventId.Id.ToString(CultureInfo.InvariantCulture)).Append(')');
        }

        builder.Append(": ").AppendLine(message);

        if (exception is not null)
        {
            builder.AppendLine(exception.ToString());
        }

        string path = Path.Combine(
            _directory,
            $"ims-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");

        lock (_writeLock)
        {
            try
            {
                RollIfTooLarge(path);
                File.AppendAllText(path, builder.ToString(), Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never take the application down (NFR-3).
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void RollIfTooLarge(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length < _maximumBytes)
        {
            return;
        }

        string rolled = Path.Combine(
            _directory,
            $"{Path.GetFileNameWithoutExtension(path)}-"
            + $"{DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture)}.log");

        File.Move(path, rolled, overwrite: true);
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private sealed class FileLogger(FileLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= provider._minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(formatter);

            provider.Write(categoryName, logLevel, eventId, formatter(state, exception), exception);
        }
    }
}
