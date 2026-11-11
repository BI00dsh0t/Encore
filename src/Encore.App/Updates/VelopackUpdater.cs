using System.IO;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Encore.App.Updates;

/// <summary>Updates from the GitHub Releases the release workflow publishes, through Velopack.</summary>
internal sealed class VelopackUpdater(string repositoryUrl) : IUpdater
{
    private const string BetaChannel = "beta";

    private UpdateManager? _manager;
    private UpdateInfo? _ready;

    public bool IsInstalled { get; } = ManagerFor(repositoryUrl, beta: false).IsInstalled;

    public async Task<string?> DownloadNewerAsync(bool beta, CancellationToken cancel)
    {
        var manager = ManagerFor(repositoryUrl, beta);
        if (!manager.IsInstalled)
        {
            return null;
        }

        var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (update is null)
        {
            return null;
        }

        // Velopack checks what it downloads, but not a package it rebuilds from deltas, so every package is checked here too.
        await manager.DownloadUpdatesAsync(update, cancelToken: cancel).ConfigureAwait(false);
        var target = update.TargetFullRelease;
        var package = Path.Combine(VelopackLocator.Current.PackagesDir ?? "", target.FileName);
        await PackageCheck.VerifyAsync(package, target.Size, target.SHA256, cancel).ConfigureAwait(false);
        _manager = manager;
        _ready = update;
        return update.TargetFullRelease.Version.ToString();
    }

    public void ApplyOnExit()
    {
        if (_manager is null || _ready is null)
        {
            return;
        }

        _manager.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: true);
    }

    private static UpdateManager ManagerFor(string repositoryUrl, bool beta)
    {
        var source = new GithubSource(repositoryUrl, accessToken: null, prerelease: beta);
        var options = new UpdateOptions { ExplicitChannel = beta ? BetaChannel : null };
        return new UpdateManager(source, options);
    }
}
