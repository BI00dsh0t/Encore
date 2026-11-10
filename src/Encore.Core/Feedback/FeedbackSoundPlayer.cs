using System.Collections.Concurrent;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Settings;

namespace Encore.Core.Feedback;

/// <summary>The <c>feedbackSounds</c> names a <see cref="FeedbackNotice"/> carries as its sound.</summary>
public static class FeedbackSoundNames
{
    public const string ClipSaved = "clipSaved";
    public const string SaveFailed = "saveFailed";
    public const string RecordingStarted = "recordingStarted";
    public const string RecordingStopped = "recordingStopped";
    public const string DiskFull = "diskFull";
}

/// <summary>Where Feedback Sounds play: the platform's render endpoints.</summary>
public interface IFeedbackSoundOutput
{
    /// <summary>The id of the default communications render endpoint, or <c>null</c> when there is none.</summary>
    string? DefaultEndpointId();

    /// <summary>Plays the samples on an endpoint and returns once played; throws when the endpoint can't be opened or fails.</summary>
    void Play(string endpointId, ReadOnlyMemory<float> pcm);
}

/// <summary>An output that plays nothing, on a placeholder endpoint no layout holds; test instances get it.</summary>
public sealed class SilentFeedbackSoundOutput : IFeedbackSoundOutput
{
    public static SilentFeedbackSoundOutput Instance { get; } = new();

    public string? DefaultEndpointId() => "silent";

    public void Play(string endpointId, ReadOnlyMemory<float> pcm)
    {
    }
}

/// <summary>Plays Feedback Sounds in order on one worker thread, so callers never block on audio and nothing throws.</summary>
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
        FeedbackSoundNames.ClipSaved => FeedbackSound.ClipSaved,
        FeedbackSoundNames.SaveFailed => FeedbackSound.SaveFailed,
        FeedbackSoundNames.RecordingStarted => FeedbackSound.RecordingStarted,
        FeedbackSoundNames.RecordingStopped => FeedbackSound.RecordingStopped,
        FeedbackSoundNames.DiskFull => FeedbackSound.DiskFull,
        _ => null,
    };

    /// <summary>The endpoint sounds play on now: <c>feedbackSoundDevice</c>, else the default communications one, else null.</summary>
    public string? Endpoint() => SettingsResolver.Resolve(_settings(), null).FeedbackSoundDevice ?? _output.DefaultEndpointId();

    /// <summary>Queues a named sound when it is switched on and returns whether it was queued. Never blocks.</summary>
    public bool Play(string? name)
    {
        if (Parse(name) is not { } sound || !SettingsResolver.Resolve(_settings(), null).FeedbackSounds.IsOn(sound))
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
