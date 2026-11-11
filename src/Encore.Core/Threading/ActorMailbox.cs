using System.Threading.Channels;

namespace Encore.Core.Threading;

/// <summary>An actor's mailbox: messages posted from any thread are handled one at a time, in order, on the actor's own thread.</summary>
internal sealed class ActorMailbox<TMessage>
{
    private readonly Channel<TMessage> _channel = Channel.CreateUnbounded<TMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });

    private readonly Action<TMessage> _handle;
    private readonly Action _closed;
    private readonly Action? _drained;
    private readonly Thread _thread;

    /// <summary>All three run on the actor's thread: drained after each batch of messages, closed last of all.</summary>
    public ActorMailbox(string threadName, Action<TMessage> handle, Action closed, Action? drained = null)
    {
        _handle = handle;
        _closed = closed;
        _drained = drained;
        _thread = new Thread(Run) { Name = threadName, IsBackground = true };
    }

    /// <summary>The actor thread's managed id.</summary>
    public int ThreadId => _thread.ManagedThreadId;

    /// <summary>Whether the caller is on the actor's thread.</summary>
    public bool IsOnThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    /// <summary>Starts the actor's thread; messages posted before it are handled first.</summary>
    public void Start() => _thread.Start();

    /// <summary>Posts a message from any thread; false once the mailbox is closed.</summary>
    public bool Post(TMessage message) => _channel.Writer.TryWrite(message);

    /// <summary>Takes no more messages, lets those already posted be handled, and waits for the thread unless called on it.</summary>
    public void Close()
    {
        _channel.Writer.TryComplete();
        if (!IsOnThread)
        {
            _thread.Join();
        }
    }

    private void Run()
    {
        var reader = _channel.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var message))
            {
                _handle(message);
            }

            _drained?.Invoke();
        }

        _closed();
    }
}
