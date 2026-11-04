using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Library;
using ReplayBuffer.Engine.Supervision;

namespace ReplayBuffer.Engine.Tests;

/// <summary>The LibraryActor over a real pipe of a throwaway test instance (story 7.1).</summary>
public sealed class LibraryServeTests : IAsyncLifetime
{
    private const string ProfileId = @"c:\games\a.exe";
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(2);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-library-" + Guid.NewGuid().ToString("N"));
    private Instance _instance = null!;
    private EngineHost _host = null!;
    private EngineClient _client = null!;
    private Channel<IpcEvent> _events = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Output => Path.Combine(_folder, "out");

    private string ClipPath => Path.Combine(Output, "Game", "Game_2026-11-03_10-00-00.mp4");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(Output, "Game"));
        _instance = new Instance(Instance.TestPrefix + Path.GetFileName(_folder), Path.Combine(_folder, "roaming"), Path.Combine(_folder, "local"));
        await StartAsync();

        var update = new SettingsUpdate(
            _client.State.Settings.Revision,
            [SettingsOp.SetGlobal(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(Output))]);
        await _client.SendAsync(SettingsMessages.Update, update, Token);
        await NextAsync("settings.changed");
        await NextAsync("library.folderChanged");
        await NextAsync("library.reset");
        Assert.Equal(Output, _client.State.LibraryFolder);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task ASavedClipAppearsWithItsProfileAndSize()
    {
        await SaveAsync();

        var item = Assert.Single(_client.State.Library);
        Assert.Equal(new LibraryItem(@"Game\Game_2026-11-03_10-00-00.mp4", ProfileId, 64, item.Date, false, Modified: item.Modified), item);
        Assert.NotNull(item.Modified);
    }

    [Fact]
    public async Task SetFavouriteOverIpcShowsInTheSnapshot()
    {
        await SaveAsync();
        var item = Assert.Single(_client.State.Library);

        await _client.SendAsync(LibraryMessages.SetFavourite, new LibrarySetFavourite(item.Path, item.Size, item.Modified, true), Token);
        await NextAsync("library.itemChanged");

        Assert.True(Assert.Single(_client.State.Library).Favourite);
    }

    [Fact]
    public async Task ASavedClipIsInTheFirstSnapshotAfterARestart()
    {
        await SaveAsync();
        await StopAsync();
        await StartAsync();

        var item = Assert.Single(_client.State.Library);
        Assert.Equal((@"Game\Game_2026-11-03_10-00-00.mp4", ProfileId, 64L, false), (item.Path, item.ProfileId, item.Size, item.BeingWritten));
    }

    [Fact]
    public async Task AFailedWriteLeavesNoItem()
    {
        _host.Library.BeginWrite(ClipPath, ProfileId);
        await NextAsync("library.itemAdded");
        _host.Library.EndWrite(ClipPath, WriteOutcome.Failed);
        await NextAsync("library.itemRemoved");

        Assert.Empty(_client.State.Library);
    }

    [Fact]
    public async Task AFileCopiedInIsAdded()
    {
        await File.WriteAllBytesAsync(Path.Combine(Output, "Game", "copied.mp4"), Mp4(), Token);

        var added = LibraryMessages.ItemAdded.Read((await NextAsync("library.itemAdded")).Payload);
        Assert.Equal((@"Game\copied.mp4", 48L, false), (added.Path, added.Size, added.BeingWritten));
    }

    [Fact]
    public async Task ACorruptIndexIsRebuiltAndBlocksCleanupUntilDismissed()
    {
        await StopAsync();
        await File.WriteAllBytesAsync(Path.Combine(Output, "Game", "clip.mp4"), Mp4(), Token);
        var libraryFile = Path.Combine(_instance.RoamingDataFolder, "library.json");
        await File.WriteAllTextAsync(libraryFile, "{ not json", Token);
        await File.WriteAllTextAsync(libraryFile + ".bak", "{ not json either", Token);
        await StartAsync();

        var state = _client.State;
        var warning = Assert.Single(state.Warnings, w => w.Code == ErrorCodes.LibraryCorrupt);
        Assert.True(warning.Dismissable);
        Assert.True(state.CleanupBlocked);
        Assert.Equal(@"Game\clip.mp4", Assert.Single(state.Library).Path);

        await _client.SendAsync(FeedbackMessages.Dismiss, new WarningDismiss(warning.Id), Token);
        await NextAsync("warning.dismissed");
        var unblocked = await NextAsync("library.cleanupBlockedChanged");
        Assert.False(LibraryMessages.CleanupBlockedChanged.Read(unblocked.Payload).Blocked);
    }

    /// <summary>A minimal complete MP4 of 48 bytes: <c>ftyp</c>, an empty <c>moov</c> and an <c>mdat</c>.</summary>
    private static byte[] Mp4() =>
    [
        0, 0, 0, 16, .. "ftypisom"u8, 0, 0, 0, 0,
        0, 0, 0, 8, .. "moov"u8,
        0, 0, 0, 24, .. "mdat"u8, .. new byte[16],
    ];

    private async Task SaveAsync()
    {
        _host.Library.BeginWrite(ClipPath, ProfileId);
        var added = await NextAsync("library.itemAdded");
        Assert.True(LibraryMessages.ItemAdded.Read(added.Payload).BeingWritten);

        await File.WriteAllBytesAsync(ClipPath, new byte[64], Token);
        _host.Library.EndWrite(ClipPath, WriteOutcome.Written);
        await NextAsync("library.itemChanged");
    }

    private async Task StartAsync()
    {
        _host = EngineHost.Start(
            _instance,
            NullLoggerFactory.Instance,
            hostOptions: new EngineHostOptions { Heartbeat = null, ProbeEncoders = false, AppLauncher = new StayingLauncher() });
        _events = Channel.CreateUnbounded<IpcEvent>();
        _client = await EngineClient.ConnectAsync(_host.PipeName, new EngineClientOptions { OnEvent = e => _events.Writer.TryWrite(e) }, Token);
    }

    private async Task StopAsync()
    {
        await _client.DisposeAsync();
        await _host.DisposeAsync();
    }

    private async Task<IpcEvent> NextAsync(string type)
    {
        var next = await _events.Reader.ReadAsync(Token).AsTask().WaitAsync(Within, Token);
        Assert.Equal(type, next.Type);
        return next;
    }

    /// <summary>An App launcher whose Apps never exit.</summary>
    private sealed class StayingLauncher : IAppLauncher
    {
        public IWatchedProcess? Open(int pid) => new Staying(pid);

        public IWatchedProcess Start() => new Staying(0);

        private sealed class Staying(int id) : IWatchedProcess
        {
            public int Id { get; } = id;

            public Task<int> Exited { get; } = new TaskCompletionSource<int>().Task;

            public void Dispose()
            {
            }
        }
    }
}
