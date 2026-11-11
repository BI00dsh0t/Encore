namespace Encore.Contracts;

/// <summary>The instance a process belongs to: <c>Encore</c> for the installed copy, <c>Encore-Dev</c> for everything else.</summary>
public sealed record Instance(string Name, string RoamingDataFolder, string LocalDataFolder)
{
    /// <summary>The instance name; one that starts with <see cref="TestPrefix"/> must be a valid test name, or it throws.</summary>
    public string Name { get; init => field = ValidateName(value); } = ValidateName(Name);

    public const string InstalledName = "Encore";
    public const string DevName = "Encore-Dev";

    /// <summary>The installer's folder under %LOCALAPPDATA%; not <see cref="InstalledName"/>, which holds the user's data.</summary>
    public const string InstallerFolderName = "EncoreApp";

    /// <summary>The only instance names <c>--instance</c> accepts, so tests never touch a real instance.</summary>
    public const string TestPrefix = "Encore-Test-";

    /// <summary>The instance of the running process.</summary>
    public static Instance Current { get; } = Resolve(
        Environment.ProcessPath ?? AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>Whether this is a throwaway test instance, which may have test-only commands.</summary>
    public bool IsTest => IsTestName(Name);

    /// <summary>Resolves the instance of a process image; only one under the user's Programs\Encore folder is installed.</summary>
    public static Instance Resolve(string processPath, string roamingAppData, string localAppData)
    {
        var image = Path.GetFullPath(processPath);
        bool isInstalled = IsUnder(image, Path.Combine(localAppData, InstallerFolderName))
            || IsUnder(image, Path.Combine(localAppData, "Programs", InstalledName));
        var name = isInstalled ? InstalledName : DevName;
        return new Instance(name, Path.Combine(roamingAppData, name), Path.Combine(localAppData, name));
    }

    private static bool IsUnder(string image, string folder)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return image.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether <paramref name="name"/> is <see cref="TestPrefix"/> followed by letters, digits or dashes.</summary>
    public static bool IsTestName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var suffix = name.StartsWith(TestPrefix, StringComparison.Ordinal) ? name[TestPrefix.Length..] : "";
        return suffix.Length > 0 && suffix.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }

    private static string ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.StartsWith(TestPrefix, StringComparison.Ordinal) && !IsTestName(name))
        {
            throw new ArgumentException($"'{name}' starts with {TestPrefix} but is not {TestPrefix}<letters, digits or dashes>, so it would be treated as a real instance.", nameof(name));
        }

        return name;
    }

    /// <summary>The test instance with this name, its data folders beside the real ones; throws for a name that isn't one.</summary>
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
