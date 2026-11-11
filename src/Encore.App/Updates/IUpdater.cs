namespace Encore.App.Updates;

/// <summary>Finds, downloads and applies updates for the installed App.</summary>
internal interface IUpdater
{
    /// <summary>Whether this copy came from the installer; only that copy can update.</summary>
    bool IsInstalled { get; }

    /// <summary>Downloads a newer version from the channel, if there is one, and returns its version; null when up to date.</summary>
    Task<string?> DownloadNewerAsync(bool beta, CancellationToken cancel);

    /// <summary>Applies the downloaded version once the App exits, then starts it again.</summary>
    void ApplyOnExit();
}
