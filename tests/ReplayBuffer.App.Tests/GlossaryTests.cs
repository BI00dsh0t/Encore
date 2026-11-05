using System.IO;
using System.Text.RegularExpressions;
using ReplayBuffer.App.Engine;
using ReplayBuffer.App.Overlay;
using ReplayBuffer.App.Status;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Tests;

/// <summary>
/// Story 10.11 (R11): no text the user sees uses an internal term from epic 10's EXPERIENCE.md glossary.
/// Settings keys, codes, message names, Contracts, logs and automation ids keep the internal names, so the
/// test reads only what is shown: every literal Text, Content, Header and ToolTip in the App's XAML.
/// </summary>
public sealed partial class GlossaryTests
{
    /// <summary>The glossary's left column, as whole words or phrases, ignoring case.</summary>
    private static readonly string[] Internal =
    [
        "Replay Buffer", "Buffer Window", "ring", "ring folder", "ring file", "Target Game", "Game Detection",
        "Game Profile", "Global", "override", "Track Layout", "Mic Device", "Output Device", "Input Device",
        "Output Settings", "Output Folder", "Feedback Sound", "Feedback Sounds", "Buffering", "Draining", "engine",
        "encoder chain", "generation", "segment", "fragment", "anchor", "packet", "Save cooldown",
    ];

    [Fact]
    public void NoAppViewShowsAnInternalTerm()
    {
        var folder = Path.Combine(RepoFolder(), "src", "ReplayBuffer.App");
        var found = Directory.EnumerateFiles(folder, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => Violations(File.ReadAllText(f)).Select(v => $"{Path.GetFileName(f)}: {v}"))
            .ToList();

        Assert.Empty(found);
    }

    /// <summary>
    /// The source files whose string literals are user-facing text (story 10.7, QA 10.11's note): the App's
    /// status, tray, Overlay, welcome card and settings pages, and the engine's warning and notice texts.
    /// Log and exception lines are skipped; so are comments.
    /// </summary>
    private static readonly string[] UserFacingSources =
    [
        @"src\ReplayBuffer.App\Status\StatusLine.cs",
        @"src\ReplayBuffer.App\TrayCapture.cs",
        @"src\ReplayBuffer.App\Overlay\OverlayViewModel.cs",
        @"src\ReplayBuffer.App\Library\LibraryWelcome.cs",
        @"src\ReplayBuffer.App\Library\LibraryPage.cs",
        @"src\ReplayBuffer.App\ShellApp.cs",
        @"src\ReplayBuffer.App\Settings\*Page.cs",
        @"src\ReplayBuffer.App\Settings\FolderPicker.cs",
        @"src\ReplayBuffer.App\Settings\MonitorPicker.cs",
        @"src\ReplayBuffer.Engine\Audio\DeviceDropoutReporter.cs",
        @"src\ReplayBuffer.Engine\Audio\FeedbackDeviceCheck.cs",
        @"src\ReplayBuffer.Engine\Sessions\MemoryMarginReporter.cs",
        @"src\ReplayBuffer.Engine\Sessions\CaptureWaitWarnings.cs",
        @"src\ReplayBuffer.Engine\Sessions\InterruptedRecordings.cs",
        @"src\ReplayBuffer.Engine\Supervision\AppSupervisor.cs",
        @"src\ReplayBuffer.Core\Feedback\FeedbackRouter.cs",
    ];

    [Fact]
    public void NoUserFacingStringSetInCodeUsesAnInternalTerm()
    {
        var repo = RepoFolder();
        var found = UserFacingSources
            .SelectMany(pattern => Directory.EnumerateFiles(Path.Combine(repo, Path.GetDirectoryName(pattern)!), Path.GetFileName(pattern)))
            .SelectMany(file => File.ReadAllLines(file).Select((line, at) => (File: file, Line: at + 1, Text: line)))
            .Where(l => !IsSkipped(l.Text))
            .SelectMany(l => CodeLiteral().Matches(l.Text).Select(m => (l.File, l.Line, Text: Interpolation().Replace(m.Groups["text"].Value, ""))))
            .SelectMany(l => Internal
                .Where(term => Regex.IsMatch(l.Text, $@"\b{Regex.Escape(term)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .Select(term => $"{Path.GetFileName(l.File)}:{l.Line}: \"{l.Text}\" uses \"{term}\""))
            .ToList();

        Assert.True(found.Count == 0, string.Join(Environment.NewLine, found));
    }

    // Logs, exceptions, comments and argument checks keep the internal names (AD-22).
    private static bool IsSkipped(string line)
    {
        var text = line.TrimStart();
        return text.StartsWith("//", StringComparison.Ordinal)
            || text.StartsWith('*')
            || LogCall().IsMatch(line)
            || line.Contains("Exception(", StringComparison.Ordinal)
            || line.Contains("nameof(", StringComparison.Ordinal);
    }

    [Fact]
    public void AViewWithAnInternalTermFails()
    {
        Assert.Equal(
            ["\"Ring folder\" uses \"ring\"", "\"Ring folder\" uses \"ring folder\""],
            Violations("""<StackPanel><TextBlock Text="Ring folder" /><TextBlock Text="{Binding RingPath}" /><Button Content="Cache folder" /></StackPanel>"""));
    }

    [Fact]
    public void TheStatusTextsUseThePlainWords()
    {
        var engine = new FakeEngineSession();
        var status = new StatusLine(engine);
        var state = engine.State!;

        status.Render(EngineStatus.Connected, null, state with { Capture = CaptureState.Initial with { Replay = ReplayState.Buffering, MicMuted = false }, TargetGame = new TargetGame(@"c:\g\v.exe", "Valorant", 1) });
        Assert.Equal("Ready to save Clips in Valorant", status.State);
        status.Render(EngineStatus.Connected, null, state with { Capture = CaptureState.Initial with { Replay = ReplayState.Draining, MicMuted = false } });
        Assert.Equal("Finishing the save…", status.State);
        status.Render(EngineStatus.Connected, null, state with { Capture = CaptureState.Initial with { MicMuted = false } });
        Assert.Equal("Waiting for a game", status.State);

        status.Render(EngineStatus.NotRunning, null, null);
        Assert.Equal("ReplayBuffer's background service isn't running.", status.State);
        status.Render(EngineStatus.Connecting, null, null);
        Assert.Equal("Connecting to ReplayBuffer's background service…", status.State);
        Assert.Equal("Ready to save Clips", TrayCapture.Describe(OverlayDot.Hollow));
    }

    /// <summary>Each literal user-facing attribute holding an internal term, as "{text}" uses "{term}".</summary>
    internal static List<string> Violations(string xaml)
    {
        List<string> found = [];
        foreach (Match match in ShownText().Matches(xaml))
        {
            var text = match.Groups["text"].Value;
            if (text.StartsWith('{'))
            {
                continue;
            }

            foreach (var term in Internal)
            {
                if (Regex.IsMatch(text, $@"\b{Regex.Escape(term)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    found.Add($"\"{text}\" uses \"{term}\"");
                }
            }
        }

        return found;
    }

    // The worktree's own solution folder: the nearest one above the test binary.
    private static string RepoFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ReplayBuffer.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("ReplayBuffer.slnx wasn't found above the test.");
    }

    [GeneratedRegex(@"\b(?:Text|Content|Header|ToolTip)=""(?<text>[^""]*)""")]
    private static partial Regex ShownText();

    // A C# string literal, plain or interpolated (not verbatim paths), on one line.
    [GeneratedRegex(@"(?<!@)\$?""(?<text>(?:[^""\\]|\\.)*)""")]
    private static partial Regex CodeLiteral();

    // A logger call (log.LogWarning, _log.LogInformation, logger.Log…) or a bare Log(…) helper.
    [GeneratedRegex(@"\b_?(?:log|logger)\.\w+\(|\bLog\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LogCall();

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Interpolation();
}
