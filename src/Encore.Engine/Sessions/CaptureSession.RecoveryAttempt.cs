using Encore.Platform.Capture;

namespace Encore.Engine.Sessions;

internal sealed partial class CaptureSession
{
    /// <summary>What one recovery attempt has built; whatever it did not install is freed when it ends.</summary>
    private sealed class RecoveryAttempt
    {
        public D3D11Device? Device { get; set; }

        public ICaptureSource? Capture { get; set; }

        public EncoderChain? Next { get; set; }

        public bool Installed { get; set; }
    }
}
