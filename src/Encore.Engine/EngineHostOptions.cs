using Microsoft.Extensions.Logging;
using Encore.Contracts.Messages;
using Encore.Core.Audio;
using Encore.Core.Feedback;
using Encore.Core.Sessions;
using Encore.Engine.Supervision;
using Encore.Platform.Audio;
using Encore.Platform.Windows;

namespace Encore.Engine;

/// <summary>The engine host's own options; tests turn the heartbeat off and fake the App.</summary>
internal sealed record EngineHostOptions
{
    /// <summary>The <c>engine.heartbeat</c> interval, or <c>null</c> for none.</summary>
    public TimeSpan? Heartbeat { get; init; } = Contracts.Supervision.HeartbeatInterval;

    /// <summary>Whether the encoders are probed at startup and the result published; tests that count events turn it off.</summary>
    public bool ProbeEncoders { get; init; } = true;

    /// <summary>Opens and starts the App; <c>null</c> for <c>Encore.App.exe</c> beside the engine.</summary>
    public IAppLauncher? AppLauncher { get; init; }

    /// <summary>The PC's audio endpoints for the device list; <c>null</c> for the engine's <see cref="AudioDeviceHub"/>.</summary>
    public IAudioEndpointSource? AudioEndpoints { get; init; }

    /// <summary>Builds the engine's one audio hub; null uses the PC's endpoints. Tests pass a hub over fakes.</summary>
    public Func<ILoggerFactory, AudioDeviceHub>? AudioHub { get; init; }

    /// <summary>The hub the Track meters subscribe to; <c>null</c> for the engine's <see cref="AudioDeviceHub"/>.</summary>
    public IAudioDeviceHub? MeterAudio { get; init; }

    /// <summary>Game Detection's window probe; null is the real one. Injecting one turns detection on for a test instance.</summary>
    public IGameWindowProbe? GameWindows { get; init; }

    /// <summary><c>serve --debug-capture</c>: a capture session follows the Target Game.</summary>
    public bool DebugCapture { get; init; }

    /// <summary>Whether Rewind follows the Target Game and a save writes a Clip; null is on except for a test instance.</summary>
    public bool? Rewind { get; init; }

    /// <summary>Why the App relaunched this engine, if it did; <c>engine-restarted</c> is raised before the pipe serves.</summary>
    public EngineRestartReason? Restarted { get; init; }

    /// <summary>Whether a first-run Track Layout is seeded from the default devices; null is on except for a test instance.</summary>
    public bool? SeedTrackLayout { get; init; }

    /// <summary>The time between Game Detection polls.</summary>
    public TimeSpan GameScanInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Where Feedback Sounds play; null is WASAPI, or a silent output on a test instance.</summary>
    public IFeedbackSoundOutput? FeedbackSounds { get; init; }

    /// <summary>The PC's sleep and resume; <c>null</c> for Windows' suspend and resume notifications.</summary>
    public IPowerNotifications? Power { get; init; }

    /// <summary>The attached monitors and their changes; <c>null</c> for Windows' (<c>WM_DISPLAYCHANGE</c>).</summary>
    public IDisplayMonitors? Displays { get; init; }

    /// <summary>Moves a Library file to the Recycle Bin; null is the Windows one. Tests delete their temp files instead.</summary>
    public Func<string, bool>? Recycle { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;
}
