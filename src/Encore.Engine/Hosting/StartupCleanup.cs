namespace Encore.Engine.Hosting;

/// <summary>The services start-up has built so far; if a later step fails, disposes them newest first.</summary>
internal sealed class StartupCleanup
{
    private readonly Stack<Action> _undo = new();

    public T Add<T>(T service)
        where T : IDisposable
    {
        _undo.Push(service.Dispose);
        return service;
    }

    public T AddAsync<T>(T service)
        where T : IAsyncDisposable
    {
        _undo.Push(() => service.DisposeAsync().AsTask().GetAwaiter().GetResult());
        return service;
    }

    public void DisposeAll()
    {
        while (_undo.TryPop(out var dispose))
        {
            dispose();
        }
    }
}
