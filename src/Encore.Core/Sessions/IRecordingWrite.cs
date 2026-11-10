namespace Encore.Core.Sessions;

/// <summary>A Recording a session can write: one fragmented MP4 from the session's encoders.</summary>
public interface IRecordingWrite
{
    /// <summary>Starts writing a Recording to a reserved path, off the calling thread; done is called once when it ends.</summary>
    void Start(string path, Action<Exception?> done);

    /// <summary>Ends the Recording at the press, then finishes its file and calls the Start callback. Never blocks on the file.</summary>
    void Stop(long qpc);
}
