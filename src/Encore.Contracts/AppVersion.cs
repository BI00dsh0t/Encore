using System.Reflection;

namespace Encore.Contracts;

/// <summary>The build's version, stamped from the release tag; a local build is 0.0.0-dev.</summary>
public static class AppVersion
{
    /// <summary>The full version, with the commit after a plus sign when the build knew it.</summary>
    public static string Text { get; } = Read();

    /// <summary>The version without the commit, as the app shows it.</summary>
    public static string Short { get; } = Text.Split('+')[0];

    private static string Read()
    {
        var attribute = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return attribute?.InformationalVersion ?? "0.0.0-dev";
    }
}
