using System.Drawing;

namespace Encore.App;

/// <summary>The tray's icons, read from the App's embedded <c>Icons/tray-*.ico</c> files.</summary>
internal static class TrayIcons
{
    public const string IdleDark = "tray-idle-dark.ico";
    public const string IdleLight = "tray-idle-light.ico";
    public const string Ready = "tray-ready.ico";
    public const string Recording = "tray-recording.ico";
    public const string ErrorDark = "tray-error-dark.ico";
    public const string ErrorLight = "tray-error-light.ico";

    public static IReadOnlyList<string> All { get; } = [IdleDark, IdleLight, Ready, Recording, ErrorDark, ErrorLight];

    /// <summary>A new icon, the named file's frame nearest <paramref name="size"/> pixels; the caller disposes it.</summary>
    public static Icon Load(string name, int size)
    {
        using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream($"Encore.App.Icons.{name}")
            ?? throw new ArgumentException($"The App embeds no icon named {name}.", nameof(name));
        return new Icon(stream, size, size);
    }
}
