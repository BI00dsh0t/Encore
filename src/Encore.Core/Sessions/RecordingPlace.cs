namespace Encore.Core.Sessions;

/// <summary>The place a Recording held in its <see cref="RecordingSlot"/>.</summary>
internal enum RecordingPlace
{
    /// <summary>Not in the slot: its result was already taken, or it never started.</summary>
    None,
    Running,

    /// <summary>Ended by an encoder restart and still finishing.</summary>
    Ending,
}
