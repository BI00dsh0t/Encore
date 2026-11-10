namespace Encore.Platform.Desktop;

/// <summary>A thumbnail's pixels: 32-bpp premultiplied BGRA, top-down, <c>Width * 4</c> bytes a row.</summary>
public sealed record ShellImage(int Width, int Height, byte[] Bgra);
