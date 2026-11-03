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
        Assert.Equal(new LibraryItem(@"Game\Game_2026-11-03_10-00-00.mp4", ProfileId, 64, item.Date, false), item);
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
