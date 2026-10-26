namespace ReplayBuffer.Contracts;

/// <summary>
/// The instance a process belongs to (AD-16): <c>ReplayBuffer</c> for the installed copy,
/// <c>ReplayBuffer-Dev</c> for everything else. The name is appended to the data folders.
/// </summary>
public sealed record Instance(string Name, string RoamingDataFolder, string LocalDataFolder)
{
    public const string InstalledName = "ReplayBuffer";
    public const string DevName = "ReplayBuffer-Dev";

    /// <summary>The only instance names <c>--instance</c> accepts, so tests never touch a real instance.</summary>
    public const string TestPrefix = "ReplayBuffer-Test-";

    /// <summary>The instance of the running process.</summary>
    public static Instance Current { get; } = Resolve(
        Environment.ProcessPath ?? AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>Whether this is a throwaway test instance, which may have test-only commands.</summary>
    public bool IsTest => IsTestName(Name);

    /// <summary>
    /// Resolves the instance for a process image. Only an image under
    /// <c>%LOCALAPPDATA%\Programs\ReplayBuffer\</c> is the installed instance.
    /// </summary>
    public static Instance Resolve(string processPath, string roamingAppData, string localAppData)
    {
        var installFolder = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(localAppData, "Programs", InstalledName))) + Path.DirectorySeparatorChar;
        var image = Path.GetFullPath(processPath);
        var name = image.StartsWith(installFolder, StringComparison.OrdinalIgnoreCase) ? InstalledName : DevName;
        return new Instance(name, Path.Combine(roamingAppData, name), Path.Combine(localAppData, name));
    }

    /// <summary>Whether <paramref name="name"/> is <see cref="TestPrefix"/> followed by letters, digits or dashes.</summary>
    public static bool IsTestName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var suffix = name.StartsWith(TestPrefix, StringComparison.Ordinal) ? name[TestPrefix.Length..] : "";
        return suffix.Length > 0 && suffix.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }

    /// <summary>The test instance named <paramref name="name"/>, with its data folders beside the real ones.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a test instance name.</exception>
    public static Instance ForTest(string name)
    {
        if (!IsTestName(name))
        {
            throw new ArgumentException($"'{name}' is not {TestPrefix}<letters, digits or dashes>.", nameof(name));
        }

        return new Instance(
            name,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), name),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name));
    }
}
