using System.IO;
using Encore.App.Library;
using Encore.Contracts.Messages;

namespace Encore.App.Tests;

public sealed class ClipMediaServiceTests
{
    [Fact]
    public async Task TheWorkersRefusalOfAMissingSourceComesBackAsItsError()
    {
        var missing = new ClipSource(Path.Combine(Path.GetTempPath(), $"encore-missing-{Guid.NewGuid():N}.mp4"), 1, 1);
        var request = new ClipMediaRequest("probe", missing);

        var error = await Assert.ThrowsAsync<IOException>(
            () => new ClipMediaService().ExecuteAsync(request, null, TestContext.Current.CancellationToken));

        Assert.Equal("The source disappeared or changed. Reopen it from the Library.", error.Message);
    }
}
