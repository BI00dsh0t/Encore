using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReplayBuffer.App.Settings;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Tests;

[Collection(nameof(WpfCollection))]
public sealed class LibrarySettingsPageTests
{
    private const long Gb = 1_000_000_000;

    private readonly FakeEngineSession _engine = new();
    private readonly List<SettingsUpdate> _updates = [];
    private readonly List<LibraryCleanupPreview> _previews = [];
    private readonly List<string> _prompts = [];
    private LibraryCleanupPlan _plan = new(3, 1_200_000_000, ["a.mp4", "b.mp4", "c.mp4"]);
    private bool _answer;

    public LibrarySettingsPageTests()
    {
        _engine.Reply = (command, args) =>
        {
            if (command == SettingsMessages.Update)
            {
                _updates.Add((SettingsUpdate)args);
                return new SettingsUpdateResult(2);
            }

            if (command == LibraryMessages.CleanupPreview)
            {
                _previews.Add((LibraryCleanupPreview)args);
                return _plan;
            }

            throw new InvalidOperationException($"No reply for {command}.");
        };
    }

    [Fact]
    public void EnablingTheSizeLimitPreviewsAndSendsOnlyAfterYes()
    {
        var page = Create();

        page.SizeLimitText = "50";
        Assert.Empty(_engine.Sent);

        _answer = false;
        page.SizeLimitEnabled = true;
        Assert.Equal([new LibraryCleanupPreview(50 * Gb, null)], _previews);
        Assert.Contains("3 files (1.2 GB)", Assert.Single(_prompts), StringComparison.Ordinal);
        Assert.Contains("Recycle Bin", _prompts[0], StringComparison.Ordinal);
        Assert.Empty(_updates);
        Assert.False(page.SizeLimitEnabled);

        _answer = true;
        page.SizeLimitEnabled = true;
        var op = Assert.Single(Assert.Single(_updates).Ops);
        Assert.Equal(Keys.Cleanup.Name, op.Key);
        Assert.Equal(Keys.Cleanup.ToJson(new CleanupSettings(50 * Gb, null)).GetRawText(), op.Value.GetRawText());
        Assert.True(page.SizeLimitEnabled);
    }

    [Fact]
    public void LoweringAsksAgainButRaisingAndTurningOffDoNot()
    {
        var page = Create(new CleanupSettings(50 * Gb, null));
        _answer = true;
        _plan = new LibraryCleanupPlan(0, 0, []);

        page.SizeLimitText = "40";
        Assert.Contains("Nothing would be deleted now.", Assert.Single(_prompts), StringComparison.Ordinal);
        Assert.Single(_updates);

        page.SizeLimitText = "60";
        page.SizeLimitEnabled = false;
        Assert.Single(_prompts);
        Assert.Single(_previews);
        Assert.Equal(
            [new CleanupSettings(40 * Gb, null), new CleanupSettings(60 * Gb, null), new CleanupSettings(null, null)],
            _updates.Select(u => Keys.Cleanup.TryRead(Assert.Single(u.Ops).Value, out var read) ? read : null));
    }

    [Fact]
    public void BadValuesSendNothing()
    {
        var page = Create(new CleanupSettings(50 * Gb, 30 * 86_400));

        foreach (var text in (string[])["0", "-1", "abc", ""])
        {
            page.SizeLimitText = text;
            Assert.Equal("50", page.SizeLimitText);
        }

        foreach (var text in (string[])["0", "1.5"])
        {
            page.AgeLimitText = text;
            Assert.Equal("30", page.AgeLimitText);
        }

        Assert.Empty(_engine.Sent);
        Assert.NotNull(page.Message);
    }

    [Fact]
    public void ChoosingAFolderSendsOutputFolderAndTheSizeSumsTheSnapshot()
    {
        _engine.State = _engine.State! with
        {
            Library =
            [
                new LibraryItem("a.mp4", null, 700_000_000, DateTimeOffset.UnixEpoch, BeingWritten: false),
                new LibraryItem("b.mp4", null, 500_000_000, DateTimeOffset.UnixEpoch, BeingWritten: false),
            ],
            CleanupBlocked = true,
        };
        var page = Create();
        Assert.Equal("1.2 GB in 2 files", page.FolderSizeText);
        Assert.Equal("the default folder under Videos", page.OutputFolderText);
        Assert.NotNull(page.CleanupBlockedText);
        Assert.False(page.CanUseDefaultFolder);

        page.ChooseOutputFolder("Clips");
        Assert.Empty(_engine.Sent);
        Assert.NotNull(page.Message);

        _answer = true;
        page.ChooseOutputFolder(@"D:\Clips");
        Assert.Contains("Existing files aren't moved.", Assert.Single(_prompts), StringComparison.Ordinal);
        var op = Assert.Single(Assert.Single(_updates).Ops);
        Assert.Equal((Keys.OutputFolder.Name, "\"D:\\\\Clips\""), (op.Key, op.Value.GetRawText()));
    }

    [Fact]
    public void TheViewRendersWithoutBindingErrors() => Sta.Run(() =>
    {
        _engine.State = _engine.State! with
        {
            LibraryFolder = @"C:\Users\me\Videos\ReplayBuffer",
            Library = [new LibraryItem("a.mp4", null, 1_500_000_000, DateTimeOffset.UnixEpoch, BeingWritten: false)],
            CleanupBlocked = true,
        };
        var model = new MainViewModel(_engine, action => action());
        model.SelectedPage = model.LibrarySettings;

        var errors = new BindingErrors();
        var level = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            var view = model.SelectedPage.View;
            view.Measure(new Size(600, 400));
            view.Arrange(new Rect(0, 0, 600, 400));
            view.UpdateLayout();
            var bitmap = new RenderTargetBitmap(600, 400, 96, 96, PixelFormats.Pbgra32);
            var white = new DrawingVisual();
            using (var context = white.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 600, 400));
            }

            bitmap.Render(white);
            bitmap.Render(view);

            // The HITL record's picture of the page.
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "library-cleanup-settings.png")))
            {
                encoder.Save(file);
            }

            Assert.True(view.ActualHeight > 0);
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            PresentationTraceSources.DataBindingSource.Switch.Level = level;
        }

        Assert.DoesNotContain(errors.Messages, m => m.Contains("LibrarySettings", StringComparison.Ordinal));
    });

    private LibrarySettingsPage Create(CleanupSettings? cleanup = null)
    {
        if (cleanup is not null)
        {
            var global = SettingsDocument.Empty.Global.Add(Keys.Cleanup.Name, Keys.Cleanup.ToJson(cleanup));
            _engine.State = _engine.State! with { Settings = SettingsDocument.Empty with { Revision = 1, Global = global } };
        }

        var page = new MainViewModel(_engine, action => action()).LibrarySettings;
        page.Confirm = text =>
        {
            _prompts.Add(text);
            return _answer;
        };
        return page;
    }
}
