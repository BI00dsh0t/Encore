using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Feedback;

namespace Encore.Engine.Tests;

/// <summary>Feedback Sounds over a real pipe of a throwaway test instance, with a fake sound output.</summary>
public sealed class FeedbackSoundServeTests : IAsyncLifetime
{
    private const string Comms = "{0.0.0.00000000}.{headset}";

    private static readonly TimeSpan Within = TimeSpan.FromSeconds(5);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-sounds-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _disposables = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Instance TestInstance => new(ServeCommand.TestInstancePrefix + Path.GetFileName(_folder).Replace(".", "-"), Path.Combine(_folder, "roaming"), Path.Combine(_folder, "local"));

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            await disposable.DisposeAsync();
        }

        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task ALayoutCapturingTheSoundEndpointWarnsAndAPreviewPlaysASwitchedOffSound()
    {
        var output = new RecordingOutput();
        var host = EngineHost.Start(
            TestInstance,
            NullLoggerFactory.Instance,
            hostOptions: new EngineHostOptions { Heartbeat = null, ProbeEncoders = false, AudioEndpoints = new FakeEndpointSource(), FeedbackSounds = output });
        _disposables.Add(host);
        var events = Channel.CreateUnbounded<IpcEvent>();
        var client = await EngineClient.ConnectAsync(host.PipeName, new EngineClientOptions { OnEvent = e => events.Writer.TryWrite(e) }, Token);
        _disposables.Add(client);
        Assert.DoesNotContain(client.State.Warnings, w => w.Code == ErrorCodes.FeedbackDeviceCaptured);

        var headset = new LayoutDevice(Guid.NewGuid(), Comms, "Headset", "", AudioFlow.Output, IsMic: false);
        var layout = new TrackLayout([headset], [new Track(Guid.NewGuid(), "Game", [headset.Key], MuteAffected: false)]);
        await client.SendAsync(
            SettingsMessages.Update,
            new SettingsUpdate(0, [
                SettingsOp.SetGlobal(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(layout)),
                SettingsOp.SetGlobal(Keys.FeedbackSounds.Name, Keys.FeedbackSounds.ToJson(Keys.FeedbackSounds.Default with { ClipSaved = false })),
            ]),
            Token).WaitAsync(Within, Token);

        await UntilAsync(client, events, s => s.Warnings.Any(w => w.Code == ErrorCodes.FeedbackDeviceCaptured));
        var warning = client.State.Warnings.Single(w => w.Code == ErrorCodes.FeedbackDeviceCaptured);
        Assert.Equal((WarningSeverity.Warning, "feedback", false, true), (warning.Severity, warning.Source, warning.Overlay, warning.Dismissable));

        await client.SendAsync(FeedbackMessages.Preview, new FeedbackPreview(FeedbackSound.ClipSaved), Token).WaitAsync(Within, Token);

        Assert.True(output.Played.Wait(Within, Token));
        Assert.Equal(Comms, output.Endpoint);
    }

    /// <summary>Reads events until <paramref name="done"/> holds for the client's snapshot.</summary>
    private static async Task UntilAsync(EngineClient client, Channel<IpcEvent> events, Func<StateSnapshot, bool> done)
    {
        while (!done(client.State))
        {
            await events.Reader.ReadAsync(Token).AsTask().WaitAsync(Within, Token);
        }
    }

    private sealed class RecordingOutput : IFeedbackSoundOutput
    {
        public SemaphoreSlim Played { get; } = new(0);

        public string? Endpoint { get; private set; }

        public string? DefaultEndpointId() => Comms;

        public void Play(string endpointId, ReadOnlyMemory<float> pcm)
        {
            Endpoint = endpointId;
            Played.Release();
        }
    }
}
