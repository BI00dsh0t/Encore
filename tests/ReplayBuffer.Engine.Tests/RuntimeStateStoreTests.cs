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

    private string TempPath => FilePath + ".tmp";

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void AMissingFileIsMutedAndNotAWarning()
    {
        Assert.True(Store().Load());

        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
        Assert.False(Directory.Exists(_folder));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"micMuted":"no"}""")]
    [InlineData("""{"micMuted":0}""")]
    [InlineData("""{"micMuted":null}""")]
    public void ACorruptOrWrongShapedFileIsMutedAndLoggedOnceAtWarning(string json)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, json);

        Assert.True(Store().Load());

        Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(json, File.ReadAllText(FilePath));
    }

    [Fact]
    public void AnUnreadableFileIsMutedAndLoggedAtWarning()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, """{"micMuted":false}""");
        using var held = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.True(Store().Load());

        Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void ASaveRoundTripsAndLeavesNoTempFile()
    {
        var store = Store();

        store.Save(false);
        Assert.False(Store().Load());
        Assert.Equal("""{"micMuted":false}""", File.ReadAllText(FilePath));

        store.Save(true);
        Assert.True(Store().Load());

        Assert.False(File.Exists(TempPath));
        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void ASaveThatCantWriteTheTempFileReturnsLogsAndLeavesTheOldFile()
    {
        Store().Save(false);

        // A directory where the temp file goes: the write fails before the old file is touched.
        Directory.CreateDirectory(TempPath);
        Store().Save(true);

        Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning);
        Assert.False(Store().Load());
    }

    [Fact]
    public void ASaveThatCantReplaceTheFileReturnsLogsRemovesTheTempFileAndLeavesTheOldFile()
    {
        Store().Save(false);

        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Store().Save(true);
        }

        Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning);
        Assert.False(File.Exists(TempPath));
        Assert.False(Store().Load());
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
