using System.Collections.Concurrent;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Settings;

namespace ReplayBuffer.Core.Feedback;

/// <summary>Where Feedback Sounds play: the platform's render endpoints.</summary>
public interface IFeedbackSoundOutput
{
    /// <summary>The id of the default communications render endpoint, or <c>null</c> when there is none.</summary>
    string? DefaultEndpointId();

    /// <summary>
    /// Plays <paramref name="pcm"/> (<see cref="FeedbackSoundSynth"/>'s format) on <paramref name="endpointId"/>
    /// and returns once it has played.
    /// </summary>
    /// <exception cref="Exception">The endpoint can't be opened or failed while playing.</exception>
    void Play(string endpointId, ReadOnlyMemory<float> pcm);
}

/// <summary>An output that plays nothing and has no endpoint; test instances get it.</summary>
public sealed class SilentFeedbackSoundOutput : IFeedbackSoundOutput
{
    public static SilentFeedbackSoundOutput Instance { get; } = new();

    public string? DefaultEndpointId() => null;

    public void Play(string endpointId, ReadOnlyMemory<float> pcm)
    {
    }
}

/// <summary>
/// Plays Feedback Sounds (FR-6) in order on one worker thread, so callers never block on audio.
/// The <c>feedbackSounds</c> flag is read at each enqueue from the current settings, and the
/// endpoint is resolved at each play: <c>feedbackSoundDevice</c>, else the default communications
/// endpoint, so it follows a change of the default. A failing play is reported to
/// <c>failed</c> and skipped; nothing throws to the caller.
/// </summary>
public sealed class FeedbackSoundPlayer : IDisposable
{
    private readonly IFeedbackSoundOutput _output;
    private readonly Func<SettingsDocument> _settings;
    private readonly Action<string, Exception> _failed;
    private readonly BlockingCollection<FeedbackSound> _queue = [];
    private readonly Thread _thread;
    private int _disposed;

    /// <param name="failed">Called on the worker with a message and the failure when a play fails.</param>
    public FeedbackSoundPlayer(IFeedbackSoundOutput output, Func<SettingsDocument> settings, Action<string, Exception> failed)
    {
        _output = output;
        _settings = settings;
        _failed = failed;
        _thread = new Thread(Run) { IsBackground = true, Name = "Feedback sounds" };
        _thread.Start();
    }

    /// <summary>The sound for a <c>feedbackSounds</c> name such as <c>clipSaved</c>, or <c>null</c> for an unknown one.</summary>
    public static FeedbackSound? Parse(string? name) => name switch
    {
        "clipSaved" => FeedbackSound.ClipSaved,
        "saveFailed" => FeedbackSound.SaveFailed,
        "recordingStarted" => FeedbackSound.RecordingStarted,
        "recordingStopped" => FeedbackSound.RecordingStopped,
        "diskFull" => FeedbackSound.DiskFull,
        _ => null,
    };

    /// <summary>Whether <paramref name="sound"/> is switched on in <paramref name="sounds"/>.</summary>
    public static bool IsOn(FeedbackSounds sounds, FeedbackSound sound)
    {
        ArgumentNullException.ThrowIfNull(sounds);
        return sound switch
        {
            FeedbackSound.ClipSaved => sounds.ClipSaved,
            FeedbackSound.SaveFailed => sounds.SaveFailed,
            FeedbackSound.RecordingStarted => sounds.RecordingStarted,
            FeedbackSound.RecordingStopped => sounds.RecordingStopped,
            FeedbackSound.DiskFull => sounds.DiskFull,
            _ => false,
        };
    }

    /// <summary>
    /// The endpoint sounds play on now: <c>feedbackSoundDevice</c>, else the default communications
    /// endpoint, else <c>null</c>.
    /// </summary>
    /// <exception cref="Exception">The output couldn't read the default endpoint.</exception>
    public string? Endpoint() => SettingsResolver.Resolve(_settings(), null).FeedbackSoundDevice ?? _output.DefaultEndpointId();

    /// <summary>
    /// Queues the sound named <paramref name="name"/> if it is switched on. Returns whether it was
    /// queued: <c>false</c> when it is off, the name is unknown or the player is disposed. Never blocks.
    /// </summary>
    public bool Play(string? name)
    {
        if (Parse(name) is not { } sound || !IsOn(SettingsResolver.Resolve(_settings(), null).FeedbackSounds, sound))
        {
            return false;
        }

        return Enqueue(sound);
    }

    /// <summary>Queues <paramref name="sound"/> even when it is switched off (<c>feedback.preview</c>).</summary>
    public void Preview(FeedbackSound sound) => Enqueue(sound);

    /// <summary>Plays what is queued, then joins the worker; later calls queue nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.CompleteAdding();
        _thread.Join();
        _queue.Dispose();
    }

    private bool Enqueue(FeedbackSound sound)
    {
        try
        {
            return _queue.TryAdd(sound);
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // Disposed: adding is complete.
            return false;
        }
    }

    private void Run()
    {
        foreach (var sound in _queue.GetConsumingEnumerable())
        {
            try
            {
                var endpoint = Endpoint()
                    ?? throw new InvalidOperationException("There is no Feedback Sound device and no default communications endpoint.");
                _output.Play(endpoint, FeedbackSoundSynth.Render(sound));
            }
            catch (Exception e)
            {
                try
                {
                    _failed($"The Feedback Sound {sound} didn't play", e);
                }
                catch (Exception)
                {
                    // A failing report must not end the worker.
                }
            }
        }
    }
}
