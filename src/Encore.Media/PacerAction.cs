namespace Encore.Media;

/// <summary>What the pacer tells the encoder thread to do.</summary>
public enum PacerAction
{
    /// <summary>Nothing is due yet.</summary>
    None,

    /// <summary>Encode the frame just offered into <see cref="PacerDecision.Slot"/>.</summary>
    Emit,

    /// <summary>Keep the frame just offered as the held frame, for the next slot.</summary>
    Hold,

    /// <summary>The frame just offered is older than one already taken: release it.</summary>
    Drop,

    /// <summary>Encode the held frame into <see cref="PacerDecision.Slot"/>.</summary>
    EmitHeld,

    /// <summary>Encode the last emitted frame again into <see cref="PacerDecision.Slot"/>.</summary>
    Repeat,
}
