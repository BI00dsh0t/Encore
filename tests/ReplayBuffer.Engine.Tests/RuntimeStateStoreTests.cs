using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Engine.Settings;

namespace ReplayBuffer.Engine.Tests;

/// <summary><c>runtime.json</c>: mic mute kept between runs (AD-5), muted whenever it can't be read, and the open Recordings (FR-12).</summary>
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

    [Fact]
    public void OpenRecordingsPersistBesideMicMuteAndAreTakenOnce()
    {
        var store = Store();
        Assert.True(store.Load());
        store.Save(false);
        store.Opened("D:/Videos/a.mp4");
        store.Opened("D:/Videos/b.mp4");
        store.Closed("D:/Videos/a.mp4");
        Assert.Equal("""{"micMuted":false,"openRecordings":["D:/Videos/b.mp4"]}""", File.ReadAllText(FilePath));

        // The next run: the paths come back once, and the file keeps only its mic mute.
        var next = Store();
        Assert.Equal(["D:/Videos/b.mp4"], next.TakeInterrupted());
        Assert.Equal("""{"micMuted":false}""", File.ReadAllText(FilePath));
        Assert.Empty(next.TakeInterrupted());
        Assert.False(next.Load());
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
