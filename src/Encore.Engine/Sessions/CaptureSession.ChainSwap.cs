using Encore.Platform.FFmpeg;

namespace Encore.Engine.Sessions;

internal sealed partial class CaptureSession
{
    /// <summary>What one restart has made so far, so a failure frees only what was not made current.</summary>
    private sealed class ChainSwap
    {
        /// <summary>The old chain's Recording tap, closed when the restart ends unless the recovery takes it.</summary>
        public RecordingTap? Tap { get; set; }

        public EncoderChain? Next { get; set; }

        public bool Installed { get; set; }
    }
}
