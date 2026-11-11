using Microsoft.Win32;

namespace Encore.Platform.Windows.Stores;

/// <summary>Reads the registry for the store readers; tests fake it.</summary>
public interface IRegistryReader
{
    /// <summary>A string value, or null when the key or the value is missing or isn't a string.</summary>
    string? Value(RegistryHive hive, string key, string name);

    /// <summary>The names of a key's subkeys; empty when the key is missing.</summary>
    IReadOnlyList<string> SubKeys(RegistryHive hive, string key);
}
