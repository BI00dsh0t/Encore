using Microsoft.Win32;

namespace Encore.Platform.Desktop;

/// <summary>
/// One string value under an HKCU key, such as the Run value that starts the App with Windows
/// (AD-16). Tests point it at a throwaway subkey; only <see cref="Startup"/> names the real one.
/// </summary>
/// <param name="subKey">The key under <c>HKEY_CURRENT_USER</c>.</param>
/// <param name="valueName">The value's name.</param>
public sealed class RunKey(string subKey, string valueName)
{
    public const string StartupSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string StartupValueName = "Encore";

    /// <summary>The Run value the app had before its rebrand to Encore; removed whenever the startup value changes.</summary>
    public const string LegacyStartupValueName = "ReplayBuffer";

    /// <summary><c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>, value <c>Encore</c>.</summary>
    public static RunKey Startup { get; } = new(StartupSubKey, StartupValueName);

    public string SubKey { get; } = subKey;

    public string ValueName { get; } = valueName;

    /// <summary>The value's data, or <c>null</c> when the key or the value is missing.</summary>
    /// <exception cref="System.Security.SecurityException">The key can't be read.</exception>
    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SubKey, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    /// <summary>Writes <paramref name="command"/> as the value's data, creating the key when it is missing.</summary>
    /// <exception cref="UnauthorizedAccessException">The key can't be written.</exception>
    /// <exception cref="IOException">The registry write failed.</exception>
    public void Write(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var key = Registry.CurrentUser.CreateSubKey(SubKey, writable: true);
        key.SetValue(ValueName, command, RegistryValueKind.String);
        DeleteLegacy(key);
    }

    /// <summary>Deletes the value. A missing key or value is fine.</summary>
    /// <exception cref="UnauthorizedAccessException">The key can't be written.</exception>
    /// <exception cref="IOException">The registry write failed.</exception>
    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SubKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
        if (key is not null)
        {
            DeleteLegacy(key);
        }
    }

    /// <summary>On the real startup key, also drops the pre-rebrand ReplayBuffer value, so Windows doesn't start both.</summary>
    private void DeleteLegacy(RegistryKey key)
    {
        if (SubKey == StartupSubKey && ValueName == StartupValueName)
        {
            key.DeleteValue(LegacyStartupValueName, throwOnMissingValue: false);
        }
    }
}
