using System.IO;
using Velopack;
using Velopack.Locators;

namespace Encore.App.Updates;

/// <summary>Updates from the GitHub Releases the release workflow publishes, through Velopack, trusting only signed feeds.</summary>
internal sealed class VelopackUpdater(string repositoryUrl) : IUpdater
{
    private UpdateManager? _manager;
    private UpdateInfo? _ready;

    public bool IsInstalled { get; } = new UpdateManager(new SignedGithubSource(repositoryUrl, beta: false)).IsInstalled;

    public async Task<string?> DownloadNewerAsync(bool beta, CancellationToken cancel)
    {
        var source = new SignedGithubSource(repositoryUrl, beta);
        var manager = new UpdateManager(source);
        if (!manager.IsInstalled)
        {
            return null;
        }

        var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (update is null)
        {
            if (source.RejectedFeeds > 0)
            {
                throw new InvalidDataException($"{source.RejectedFeeds} release feed(s) on GitHub failed their signature check.");
            }

            return null;
        }

        // Velopack checks what it downloads against the signed feed, but not a package it rebuilds from deltas, so check that too.
        await manager.DownloadUpdatesAsync(update, cancelToken: cancel).ConfigureAwait(false);
        var target = update.TargetFullRelease;
        var package = Path.Combine(VelopackLocator.Current.PackagesDir ?? "", target.FileName);
        await PackageCheck.VerifyAsync(package, target.Size, target.SHA256, cancel).ConfigureAwait(false);
        _manager = manager;
        _ready = update;
        return target.Version.ToString();
    }

    public void ApplyOnExit()
    {
        if (_manager is null || _ready is null)
        {
            return;
        }

        _manager.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: true);
    }
}
