using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Encore.App.Library;

/// <summary>What the user has seen once, kept by the App in <c>first-use.json</c>: whether they closed the Library's tips.</summary>
internal sealed record FirstUse(bool TipsClosed = false)
{
    /// <summary>Reads the file; a missing or unreadable file counts as nothing seen yet.</summary>
    public static FirstUse Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), FirstUseJson.Default.FirstUse) ?? new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    /// <summary>Writes the file; a write that fails only shows the tips again at the next start.</summary>
    public void Write(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, FirstUseJson.Default.FirstUse));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: the tips come back next time rather than failing the close.
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(FirstUse))]
internal sealed partial class FirstUseJson : JsonSerializerContext;
