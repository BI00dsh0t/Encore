using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Encore.App.Updates;

/// <summary>
/// Reads Encore's releases from GitHub, trusting a release's feed only when its signature verifies.
/// With beta on, it reads both channels, so a newer stable release reaches beta users too.
/// </summary>
internal sealed class SignedGithubSource(string repositoryUrl, bool beta) : GithubSource(repositoryUrl, accessToken: null, prerelease: beta)
{
    public const string StableChannel = "win";
    public const string BetaChannel = "beta";

    /// <summary>Releases whose feed was skipped because its signature didn't verify.</summary>
    public int RejectedFeeds { get; private set; }

    public override async Task<VelopackAssetFeed> GetReleaseFeed(
        IVelopackLogger logger,
        string? appId,
        string channel,
        Guid? stagingId = null,
        VelopackAsset? latestLocalRelease = null)
    {
        var releases = await GetReleases(Prerelease).ConfigureAwait(false);
        var entries = new List<VelopackAsset>();
        foreach (var release in releases ?? [])
        {
            foreach (var feedName in FeedNames())
            {
                if (await ReadSignedFeed(logger, release, feedName).ConfigureAwait(false) is { } feed)
                {
                    entries.AddRange(feed.Assets.Select(asset => new GitBaseAsset(asset, release)));
                }
            }
        }

        return new VelopackAssetFeed { Assets = [.. entries] };
    }

    private IEnumerable<string> FeedNames()
    {
        yield return $"releases.{StableChannel}.json";
        if (Prerelease)
        {
            yield return $"releases.{BetaChannel}.json";
        }
    }

    private async Task<VelopackAssetFeed?> ReadSignedFeed(IVelopackLogger logger, GithubRelease release, string feedName)
    {
        string feedUrl;
        string signatureUrl;
        try
        {
            feedUrl = GetAssetUrlFromName(release, feedName);
            signatureUrl = GetAssetUrlFromName(release, FeedSignature.SignatureName(feedName));
        }
        catch (Exception e)
        {
            // A release without this channel's feed, or without a signature, offers nothing.
            logger.Trace(e.ToString());
            return null;
        }

        var feed = await Downloader.DownloadBytes(feedUrl, GetRequestHeaders("application/octet-stream")).ConfigureAwait(false);
        var signature = await Downloader.DownloadBytes(signatureUrl, GetRequestHeaders("application/octet-stream")).ConfigureAwait(false);
        if (!FeedSignature.IsValid(feed, FeedSignature.Text(signature)))
        {
            RejectedFeeds++;
            logger.Warn($"Ignored {feedName} in release {release.Name}: its signature doesn't verify.");
            return null;
        }

        return VelopackAssetFeed.FromJson(FeedSignature.Text(feed));
    }
}
