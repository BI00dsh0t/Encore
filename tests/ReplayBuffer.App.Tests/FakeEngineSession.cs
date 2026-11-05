using ReplayBuffer.App.Engine;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Tests;

/// <summary>A session whose state the test sets and whose replies come from <see cref="Reply"/>, synchronously.</summary>
internal sealed class FakeEngineSession : IEngineSession
{
    public EngineStatus Status { get; set; } = EngineStatus.Connected;

    public string? Problem { get; set; }

    public StateSnapshot? State { get; set; } = StateSnapshot.Initial("run-1", 4242, SettingsDocument.Empty);

    public event Action? Changed;

    /// <summary>Answers a command: returns its result, or throws its error.</summary>
    public Func<CommandType, object, object> Reply { get; set; } = (command, _) => throw new InvalidOperationException($"No reply for {command}.");

    public List<object> Sent { get; } = [];

    /// <summary>
    /// Answers <c>monitors.list</c>, which the Hotkeys page's screen picker sends on its own whenever an engine run
    /// connects; kept apart from <see cref="Reply"/> and <see cref="Sent"/> so other tests don't see it.
    /// </summary>
    public Func<MonitorList> Monitors { get; set; } = () => new MonitorList([]);

    public int MonitorFetches { get; private set; }

    public Task<TResult> SendAsync<TArgs, TResult>(CommandType<TArgs, TResult> command, TArgs args, CancellationToken cancellationToken = default)
    {
        if (ReferenceEquals(command, RecordingMessages.MonitorsList))
        {
            MonitorFetches++;
            try
            {
                return Task.FromResult((TResult)(object)Monitors());
            }
            catch (Exception e)
            {
                return Task.FromException<TResult>(e);
            }
        }

        Sent.Add(args!);
        try
        {
            return Task.FromResult((TResult)Reply(command, args!));
        }
        catch (Exception e)
        {
            return Task.FromException<TResult>(e);
        }
    }

    public void RaiseChanged() => Changed?.Invoke();
}
