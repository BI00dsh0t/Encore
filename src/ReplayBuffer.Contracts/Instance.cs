namespace ReplayBuffer.Contracts;

/// <summary>
/// The instance a process belongs to (AD-16): <c>ReplayBuffer</c> for the installed copy,
/// <c>ReplayBuffer-Dev</c> for everything else. The name is appended to the data folders.
/// </summary>
public sealed record Instance(string Name, string RoamingDataFolder, string LocalDataFolder)
{
    public const string InstalledName = "ReplayBuffer";
    public const string DevName = "ReplayBuffer-Dev";

    /// <summary>The instance of the running process.</summary>
    public static Instance Current { get; } = Resolve(
        Environment.ProcessPath ?? AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

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
}
