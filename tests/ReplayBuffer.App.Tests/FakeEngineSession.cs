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

    public Task<TResult> SendAsync<TArgs, TResult>(CommandType<TArgs, TResult> command, TArgs args, CancellationToken cancellationToken = default)
    {
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
