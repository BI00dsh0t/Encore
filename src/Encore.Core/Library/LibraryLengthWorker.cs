using System.Threading.Channels;

namespace Encore.Core.Library;

/// <summary>Reads Library items' lengths on a thread of its own, at the lowest priority. It only reads a file and reports.</summary>
internal sealed class LibraryLengthWorker
{
    // Written by the actor thread only.
    private readonly Channel<LengthJob> _jobs = Channel.CreateUnbounded<LengthJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });

    private readonly Func<string, double?> _readLength;
    private readonly Action<Exception> _failed;
    private readonly Func<bool> _stopping;
    private readonly Action<LengthJob, DateTime, double?> _done;
    private readonly Thread _thread;

    /// <summary>done gets each job with its file's modified time and its length, or null; once stopping, jobs are dropped.</summary>
    public LibraryLengthWorker(
        Func<string, double?> readLength,
        Action<Exception> failed,
        Func<bool> stopping,
        Action<LengthJob, DateTime, double?> done)
    {
        _readLength = readLength;
        _failed = failed;
        _stopping = stopping;
        _done = done;
        _thread = new Thread(Run) { Name = "LibraryLength", IsBackground = true, Priority = ThreadPriority.Lowest };
        _thread.Start();
    }

    public bool TryQueue(LengthJob job) => _jobs.Writer.TryWrite(job);

    /// <summary>Takes no more jobs and waits for the thread to end.</summary>
    public void Stop()
    {
        _jobs.Writer.TryComplete();
        if (Environment.CurrentManagedThreadId != _thread.ManagedThreadId)
        {
            _thread.Join();
        }
    }

    private void Run()
    {
        var reader = _jobs.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var job))
            {
                // Once the actor is disposed, it would drop the result.
                if (!_stopping())
                {
                    Read(job);
                }
            }
        }
    }

    private void Read(LengthJob job)
    {
        double? length = null;
        var modified = default(DateTime);
        try
        {
            modified = File.GetLastWriteTimeUtc(job.Path);
            length = _readLength(job.Path);
        }
#pragma warning disable CA1031 // A reader that throws only costs the item its length.
        catch (Exception e)
#pragma warning restore CA1031
        {
            _failed(e);
        }

        _done(job, modified, length);
    }
}
