namespace Encore.Platform.Capture;

/// <summary>Receives one captured frame; the texture is an <c>ID3D11Texture2D*</c> valid only during the call.</summary>
public delegate void CapturedFrameHandler(long qpc, nint texture, VideoSize contentSize);
