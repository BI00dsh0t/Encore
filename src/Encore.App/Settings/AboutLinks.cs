using System.Diagnostics;
using System.IO;

namespace Encore.App.Settings;

/// <summary>The About section's links: the GitHub page, and the license files that ship beside the exe.</summary>
internal sealed class AboutLinks
{
    public const string GitHubPage = "https://github.com/BI00dsh0t/Encore";
    public const string LicenseFile = "LICENSE";
    public const string NoticesFile = "THIRD-PARTY-NOTICES.txt";

    private readonly string _folder;
    private readonly Action<string> _openPage;
    private readonly Action<string> _openText;

    /// <summary>The folder holds the license files; a dev build has none there, so a link falls back to the file on GitHub.</summary>
    public AboutLinks(string folder, Action<string> openPage, Action<string> openText)
    {
        _folder = folder;
        _openPage = openPage;
        _openText = openText;
        OpenGitHubCommand = new RelayCommand(() => _openPage(GitHubPage));
        OpenLicenseCommand = new RelayCommand(() => OpenFile(LicenseFile));
        OpenNoticesCommand = new RelayCommand(() => OpenFile(NoticesFile));
    }

    public string Description => "Instant replay and recording for Windows.";

    public RelayCommand OpenGitHubCommand { get; }

    public RelayCommand OpenLicenseCommand { get; }

    public RelayCommand OpenNoticesCommand { get; }

    /// <summary>The links for this install: pages in the browser, and the text files in Notepad, as LICENSE has no extension.</summary>
    public static AboutLinks ForThisApp() => new(AppContext.BaseDirectory, OpenInShell, OpenInNotepad);

    private void OpenFile(string name)
    {
        var path = Path.Combine(_folder, name);
        if (File.Exists(path))
        {
            _openText(path);
            return;
        }

        _openPage($"{GitHubPage}/blob/main/{name}");
    }

    private static void OpenInShell(string target)
    {
        using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    private static void OpenInNotepad(string path)
    {
        using var _ = Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = false });
    }
}
