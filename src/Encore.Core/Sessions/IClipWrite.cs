namespace Encore.Core.Sessions;

/// <summary>A Clip a session can write.</summary>
public interface IClipWrite
{
    /// <summary>Writes the Clip to a reserved path, off the calling thread; done is called once, from the writer's thread.</summary>
    void Write(string path, Action<Exception?> done);

    /// <summary>The spec this write's press switched the session to, or null; it stands whatever the write's outcome.</summary>
    SessionSpec? SwitchesTo => null;

    /// <summary>Makes this Clip's end Rewind's Reset, so the next Clip starts where this one ended. Only after success.</summary>
    void Reset()
    {
    }
}
