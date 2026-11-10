using System.Diagnostics.CodeAnalysis;
using Encore.App.Library;
using Encore.Contracts;

namespace Encore.App;

/// <summary>The App's command line. Switches: docs/code-notes.md.</summary>
internal static class AppArguments
{
    public const string TrayArgument = "--tray";
    public const string InstanceArgument = "--instance";
    public const string RewindArgument = "--rewind";

    /// <summary>A dev switch: the player reports that codec as undecodable, to check the missing-extension page.</summary>
    public const string PlayerUnsupportedArgument = "--player-unsupported=";

    /// <summary>Whether the command line asks for no window at start.</summary>
    public static bool StartsInTray(IEnumerable<string> args) => args.Contains(TrayArgument, StringComparer.OrdinalIgnoreCase);

    /// <summary>The codec the player-unsupported switch names ("HEVC" or "AV1"), except on the installed instance; else null.</summary>
    public static string? PlayerUnsupported(IEnumerable<string> args, Instance instance)
    {
        var codec = args
            .FirstOrDefault(argument => argument.StartsWith(PlayerUnsupportedArgument, StringComparison.OrdinalIgnoreCase))
            ?[PlayerUnsupportedArgument.Length..];
        return instance.Name == Instance.InstalledName || CodecExtension.For(codec) is not { } extension ? null : extension.Codec;
    }

    /// <summary>Whether the command line passes <c>--rewind</c> on to the engine; valid on a test instance only.</summary>
    public static bool WantsRewind(IEnumerable<string> args) => args.Contains(RewindArgument, StringComparer.OrdinalIgnoreCase);

    /// <summary>The engine's command line: plain on the installed instance, else with the test instance and its switches.</summary>
    public static IReadOnlyList<string> ForEngine(Instance instance, IEnumerable<string> args)
    {
        if (!instance.IsTest)
        {
            return ["serve"];
        }

        return WantsRewind(args)
            ? ["serve", InstanceArgument, instance.Name, RewindArgument]
            : ["serve", InstanceArgument, instance.Name];
    }

    /// <summary>The App's command line: the instance and <c>--rewind</c>, both for test instances only. False means exit 2.</summary>
    public static bool TryParse(IReadOnlyList<string> args, [NotNullWhen(true)] out Instance? instance)
    {
        if (TryParseInstance(args, out instance) && (instance.IsTest || !WantsRewind(args)))
        {
            return true;
        }

        instance = null;
        return false;
    }

    /// <summary>The instance <c>--instance</c> names, which must be a test instance, or the process's own; false when malformed.</summary>
    public static bool TryParseInstance(IReadOnlyList<string> args, [NotNullWhen(true)] out Instance? instance)
    {
        instance = Instance.Current;
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], InstanceArgument, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 >= args.Count || !Instance.IsTestName(args[i + 1]))
            {
                instance = null;
                return false;
            }

            instance = Instance.ForTest(args[++i]);
        }

        return true;
    }
}
