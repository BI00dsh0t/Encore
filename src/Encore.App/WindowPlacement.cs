using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Encore.App;

/// <summary>The Main App window's normal bounds in device-independent pixels and whether it was maximized: <c>window.json</c>.</summary>
internal sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool IsMaximized)
{
    /// <summary>Reads the file; a missing or unreadable file gives null, so the window opens at its centred default.</summary>
    public static WindowPlacement? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), WindowPlacementJson.Default.WindowPlacement);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the file; a write that fails only loses the size for the next start.</summary>
    public void Write(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, WindowPlacementJson.Default.WindowPlacement));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: the next start opens at the default size.
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(WindowPlacement))]
internal sealed partial class WindowPlacementJson : JsonSerializerContext;
