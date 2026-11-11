using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Encore.Bench;

/// <summary>The screen grabs that tell, without a person, whether WGC's capture border showed around the marker window.</summary>
internal sealed record BorderProbeResult(
    string Verdict, int RingPixels, int YellowBefore, int YellowDuring, string? BeforeImage, string? DuringImage, string? Error);

/// <summary>Grabs the screen around the marker window before and during the capture, and counts yellow pixels at its edge.</summary>
internal sealed class BorderProbe(string folder, TimeSpan delay)
{
    /// <summary>The band's half-width: the border sits on the window's edge, inside or out.</summary>
    public const int Margin = 12;

    private int _ring;
    private int? _before;
    private int? _during;
    private string? _beforeImage;
    private string? _duringImage;
    private string? _error;

    /// <summary>How long into the capture <see cref="During"/> runs.</summary>
    public TimeSpan Delay { get; } = delay;

    public void Before(nint hwnd) => _before = Grab(hwnd, "playback-border-before.png", out _beforeImage);

    public void During(nint hwnd) => _during = Grab(hwnd, "playback-border-during.png", out _duringImage);

    public BorderProbeResult Result() =>
        _before is { } before && _during is { } during
            ? new BorderProbeResult(Classify(_ring, before, during), _ring, before, during, _beforeImage, _duringImage, null)
            : new BorderProbeResult("not-checked", _ring, _before ?? 0, _during ?? 0, _beforeImage, _duringImage, _error ?? "a screen grab did not run");

    /// <summary><c>yes</c> when the band gained at least 2 % of its pixels (and at least 50) in yellow during the capture.</summary>
    internal static string Classify(int ringPixels, int yellowBefore, int yellowDuring) =>
        ringPixels > 0 && yellowDuring - yellowBefore >= Math.Max(50, ringPixels * 0.02) ? "yes" : "no";

    /// <summary>A saturated yellow to orange, as the capture border is drawn; never the marker's black or white.</summary>
    internal static bool IsYellow(int red, int green, int blue) => red >= 180 && green >= 120 && blue <= 110 && red - blue >= 100;

    /// <summary>The yellow pixels, and all the pixels, within a margin of the edge of the window's rectangle in the grab.</summary>
    internal static (int Yellow, int Ring) CountRing(ReadOnlySpan<int> argb, int width, Rectangle inner, int margin)
    {
        var outer = Rectangle.Inflate(inner, margin, margin);
        var core = Rectangle.Inflate(inner, -margin, -margin);
        var height = argb.Length / width;
        int yellow = 0, ring = 0;
        for (var y = Math.Max(0, outer.Top); y < Math.Min(height, outer.Bottom); y++)
        {
            for (var x = Math.Max(0, outer.Left); x < Math.Min(width, outer.Right); x++)
            {
                if (core.Width > 0 && core.Height > 0 && core.Contains(x, y))
                {
                    continue;
                }

                ring++;
                var pixel = argb[(y * width) + x];
                if (IsYellow((pixel >> 16) & 0xFF, (pixel >> 8) & 0xFF, pixel & 0xFF))
                {
                    yellow++;
                }
            }
        }

        return (yellow, ring);
    }

    private int? Grab(nint hwnd, string file, out string? image)
    {
        image = null;
        try
        {
            if (!GetWindowRect(hwnd, out var r))
            {
                throw new InvalidOperationException($"GetWindowRect failed with error {Marshal.GetLastPInvokeError()}.");
            }

            var window = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            var area = Rectangle.Intersect(Rectangle.Inflate(window, Margin, Margin), System.Windows.Forms.SystemInformation.VirtualScreen);
            using var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);
            }

            var pixels = new int[area.Width * area.Height];
            var data = bitmap.LockBits(new Rectangle(0, 0, area.Width, area.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (var y = 0; y < area.Height; y++)
                {
                    Marshal.Copy(data.Scan0 + (y * data.Stride), pixels, y * area.Width, area.Width);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            image = Path.Combine(folder, file);
            bitmap.Save(image, ImageFormat.Png);
            var inner = window with { X = window.X - area.X, Y = window.Y - area.Y };
            var (yellow, ring) = CountRing(pixels, area.Width, inner, Margin);
            _ring = ring;
            return yellow;
        }
        catch (Exception e) when (e is InvalidOperationException or ExternalException or ArgumentException or IOException)
        {
            _error ??= $"the screen grab failed: {e.Message}";
            return null;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out WindowRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
