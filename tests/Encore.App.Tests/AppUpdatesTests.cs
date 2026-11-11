using System.IO;
using Encore.App.Updates;

namespace Encore.App.Tests;

public sealed class AppUpdatesTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("encore-updates-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void TheRunValuePointsAtTheLauncherForAnInstalledAppAndAtTheAppOtherwise()
    {
        var installed = @"C:\Users\u\AppData\Local\EncoreApp\current\Encore.App.exe";
        var updater = @"C:\Users\u\AppData\Local\EncoreApp\Update.exe";

        Assert.Equal(@"C:\Users\u\AppData\Local\EncoreApp\Encore.App.exe", InstallLayout.LaunchPath(installed, path => path == updater));
        Assert.Equal(@"C:\dev\Encore.App.exe", InstallLayout.LaunchPath(@"C:\dev\Encore.App.exe", _ => true));
    }

    [Fact]
    public async Task ADownloadedUpdateWaitsForTheRecordingThenAppliesOnQuit()
    {
        var updater = new FakeUpdater { Newer = "0.9.0" };
        var busy = true;
        var quits = 0;
        var updates = new AppUpdates(updater, Path.Combine(_folder, "updates.json"), () => busy, () =>
        {
            quits++;
            return Task.CompletedTask;
        });

        await updates.CheckAsync();
        Assert.True(updates.IsReady);
        Assert.Contains("0.9.0", updates.Status);

        updates.RestartToUpdateCommand.Execute(null);
        Assert.Equal(0, quits);
        Assert.False(updater.Applied);

        busy = false;
        updates.RestartToUpdateCommand.Execute(null);
        Assert.Equal(1, quits);
        Assert.True(updater.Applied);
    }

    [Fact]
    public void TheSwitchesAreKeptForTheNextStart()
    {
        var path = Path.Combine(_folder, "updates.json");
        var updates = new AppUpdates(new FakeUpdater(), path, () => false, () => Task.CompletedTask)
        {
            CheckAutomatically = false,
            Beta = true,
        };

        var again = new AppUpdates(new FakeUpdater(), path, () => false, () => Task.CompletedTask);

        Assert.False(again.CheckAutomatically);
        Assert.True(again.Beta);
        Assert.False(updates.IsReady);
    }

    private sealed class FakeUpdater : IUpdater
    {
        public string? Newer { get; init; }

        public bool Applied { get; private set; }

        public bool IsInstalled => true;

        public Task<string?> DownloadNewerAsync(bool beta, CancellationToken cancel) => Task.FromResult(Newer);

        public void ApplyOnExit() => Applied = true;
    }
}
