using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Encore.App.Updates;

/// <summary>The user's update choices, kept by the App in <c>updates.json</c>: whether to check, and which channel.</summary>
internal sealed record UpdatePreferences(bool CheckAutomatically = true, bool Beta = false)
{
    /// <summary>Reads the file; a missing or unreadable file gives the defaults.</summary>
    public static UpdatePreferences Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), UpdatePreferencesJson.Default.UpdatePreferences) ?? new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    /// <summary>Writes the file; a write that fails keeps the choice for this run only.</summary>
    public void Write(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, UpdatePreferencesJson.Default.UpdatePreferences));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: the switch still applies until the App quits.
        }
    }
}

[JsonSerializable(typeof(UpdatePreferences))]
internal sealed partial class UpdatePreferencesJson : JsonSerializerContext;
