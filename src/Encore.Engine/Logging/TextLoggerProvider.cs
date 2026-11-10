using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Encore.Engine.Logging;

/// <summary>A small NativeAOT-safe logging provider: a daily file plus the console, written by one background thread.</summary>
public sealed class TextLoggerProvider : ILoggerProvider
{
    private readonly string _folder;
    private readonly string _prefix;
    private readonly LogLevel _fileLevel;
    private readonly LogLevel _consoleLevel;
    private readonly BlockingCollection<Entry> _queue = new(boundedCapacity: 10_000);
    private readonly Thread _writer;
    private StreamWriter? _file;
    private DateOnly _fileDate;

    public TextLoggerProvider(
        string folder,
        string prefix,
        LogLevel fileLevel = LogLevel.Debug,
        LogLevel consoleLevel = LogLevel.Information)
    {
        _folder = folder;
        _prefix = prefix;
        _fileLevel = fileLevel;
        _consoleLevel = consoleLevel;
        Directory.CreateDirectory(folder);
        _writer = new Thread(WriteLoop) { Name = "Log writer", IsBackground = true };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new TextLogger(this, categoryName);

    /// <summary>Writes everything queued, then closes the file.</summary>
    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join();
        _file?.Dispose();
        _queue.Dispose();
    }

    private bool IsEnabled(LogLevel level) => level != LogLevel.None && (level >= _fileLevel || level >= _consoleLevel);

    private void Enqueue(Entry entry)
    {
        // A full queue drops the line rather than blocking a capture thread.
        if (!_queue.IsAddingCompleted)
        {
            _queue.TryAdd(entry);
        }
    }

    private void WriteLoop()
    {
        foreach (var entry in _queue.GetConsumingEnumerable())
        {
            if (entry.Level >= _fileLevel)
            {
                WriteFile(entry);
            }

            if (entry.Level >= _consoleLevel)
            {
                var console = entry.Level >= LogLevel.Warning ? Console.Error : Console.Out;
                console.WriteLine($"[{entry.Time:HH:mm:ss} {Abbreviation(entry.Level)}] {entry.Message}");
            }
        }
    }

    private void WriteFile(Entry entry)
    {
        var date = DateOnly.FromDateTime(entry.Time.LocalDateTime);
        if (_file is null || date != _fileDate)
        {
            _file?.Dispose();
            var path = Path.Combine(_folder, $"{_prefix}-{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _file = new StreamWriter(stream) { AutoFlush = true };
            _fileDate = date;
        }

        _file.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff zzz} [{Abbreviation(entry.Level)}] {entry.Category}: {entry.Message}"));
        if (entry.Exception is not null)
        {
            _file.WriteLine(entry.Exception);
        }
    }

    private static string Abbreviation(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        _ => "CRT",
    };

    private readonly record struct Entry(DateTimeOffset Time, LogLevel Level, string Category, string Message, Exception? Exception);

    private sealed class TextLogger(TextLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Enqueue(new Entry(DateTimeOffset.Now, logLevel, category, formatter(state, exception), exception));
            }
        }
    }
}
