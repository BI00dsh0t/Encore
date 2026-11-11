using Encore.Core.Feedback;
using Encore.Core.Library;
using Encore.Core.Sessions;
using Encore.Engine.Audio;
using Encore.Engine.Hotkeys;
using Encore.Engine.Ipc;
using Encore.Engine.Sessions;
using Encore.Engine.Settings;
using Encore.Engine.Supervision;
using Encore.Platform.Audio;
using Encore.Platform.Windows;

namespace Encore.Engine.Hosting;

/// <summary>The services a running engine owns; disposes them in shutdown order.</summary>
internal sealed class EngineServices : IAsyncDisposable
{
    public required IpcServer Server { get; init; }

    public required SettingsStore Settings { get; init; }

    public required EventHub Hub { get; init; }

    public required CommandRouter Router { get; init; }

    public required Warnings Warnings { get; init; }

    public required AppSupervisor Apps { get; init; }

    public required IDisposable MonitorsPublished { get; init; }

    public required HotkeyService Hotkeys { get; init; }

    public required GameMonitor? Games { get; init; }

    public required SessionOrchestrator Sessions { get; init; }

    public required LibraryActor Library { get; init; }

    public required FeedbackRouter Feedback { get; init; }

    public required FeedbackDeviceCheck FeedbackCheck { get; init; }

    public required FeedbackSoundPlayer Sounds { get; init; }

    public required TrackMeters Meters { get; init; }

    public required DeviceListPublisher Devices { get; init; }

    public required DeviceDropoutReporter Dropouts { get; init; }

    public required MemoryMarginReporter MarginReporter { get; init; }

    public required EstimatePublisher Estimates { get; init; }

    /// <summary>The engine's one audio hub; disposed last, after every session has ended.</summary>
    public required AudioDeviceHub Audio { get; init; }

    public async ValueTask DisposeAsync()
    {
        Apps.Dispose();
        MonitorsPublished.Dispose();

        // No more presses or proposals are posted to the orchestrator once these return.
        Hotkeys.Dispose();
        Games?.Dispose();

        // Ends a running session, finishing its file, and joins the orchestrator thread.
        Sessions.Dispose();

        // After the orchestrator, so every write it ended is saved; one still in flight is found by the next start scan.
        Library.Dispose();

        // No more outcomes reach the router once the orchestrator has stopped.
        Feedback.Dispose();

        // Plays what the router queued, then joins the sound worker.
        FeedbackCheck.Dispose();
        Sounds.Dispose();
        await Server.DisposeAsync().ConfigureAwait(false);

        // Closes the watch's subscriptions before the hub goes.
        Meters.Dispose();
        Devices.Dispose();
        Dropouts.Dispose();
        MarginReporter.Dispose();
        Estimates.Dispose();
        Audio.Dispose();
    }
}
