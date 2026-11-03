using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Engine.Settings;

namespace ReplayBuffer.Engine.Tests;

/// <summary><c>runtime.json</c>: mic mute kept between runs (AD-5), muted whenever it can't be read.</summary>
public sealed class RuntimeStateStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-runtime-" + Guid.NewGuid().ToString("N"));
    private readonly ListLogger _log = new();

    private string FilePath => Path.Combine(_folder, RuntimeStateStore.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void ACorruptFileIsMutedAndLoggedAtWarning()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, "{not json");

        Assert.True(Store().Load());

        Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning);
    }

    private RuntimeStateStore Store() => new(_folder, _log);

    private sealed class ListLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
