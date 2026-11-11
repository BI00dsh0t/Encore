using Microsoft.Win32;

namespace Encore.Platform.Windows.Stores;

/// <summary>The real registry, read only, in its 64-bit view.</summary>
public sealed class WindowsRegistry : IRegistryReader
{
    public static WindowsRegistry Instance { get; } = new();

    public string? Value(RegistryHive hive, string key, string name)
    {
        using var opened = Open(hive, key);
        return opened?.GetValue(name) as string;
    }

    public IReadOnlyList<string> SubKeys(RegistryHive hive, string key)
    {
        using var opened = Open(hive, key);
        return opened?.GetSubKeyNames() ?? [];
    }

    private static RegistryKey? Open(RegistryHive hive, string key)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        return root.OpenSubKey(key, writable: false);
    }
}
