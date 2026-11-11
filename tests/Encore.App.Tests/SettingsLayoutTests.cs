using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Encore.App.Settings;
using Encore.App.Theme;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Tests;

/// <summary>The shared settings layout: sections as cards in both themes, and the page error info bar.</summary>
[Collection(nameof(WpfCollection))]
public sealed partial class SettingsLayoutTests
{
    private static readonly string[] Views =
        [
            "GeneralSettingsView", "RewindSettingsView", "FolderPickerView", "HotkeysView", "FeedbackSoundsView",
            "TrackLayoutView", "GameProfilesView", "LibrarySettingsView",
        ];

    [Fact]
    public void TheFirstFourPagesShowTheirSectionsAsCardsInBothThemes() => Sta.Run(() =>
    {
        var model = new MainViewModel(new FakeEngineSession(), action => action());
        var window = Offscreen(new SettingsWindow { DataContext = model.Settings });
        var theme = ThemeService.ForWindow(window);
        var expected = new (SettingsViewModel ViewModel, string Slug, string[] Sections)[]
        {
            (model.General, "general", ["Appearance", "Startup and Overlay", "Games", "About and updates"]),
            (model.Rewind, "clips", ["Clip length", "Where recent gameplay is kept", "Video quality — for Clips and Recordings"]),
            (model.Hotkeys, "hotkeys", ["Clips", "Recordings", "Monitor Recording", "Microphone"]),
            (model.FeedbackSounds, "sounds", ["Sounds"]),
        };
        try
        {
            foreach (var which in new[] { AppTheme.Fluent, AppTheme.GamingDark })
            {
                theme.Apply(which);
                window.Show();
                var card = (Style)window.FindResource("EncoreCardStyle");
                foreach (var (viewModel, slug, sections) in expected)
                {
                    model.Settings.Select(viewModel);
                    Sta.Pump(TimeSpan.FromMilliseconds(100));

                    Assert.True(viewModel.View.IsLoaded, $"{viewModel.Title} didn't load under {which}");
                    var found = Descendants<HeaderedContentControl>(viewModel.View).Where(s => s.Visibility == Visibility.Visible).ToList();
                    Assert.Equal(sections, found.Select(s => s.Header as string));
                    Assert.All(found, s => Assert.Contains(Descendants<Border>(s), b => b.Style == card));
                    Save(window, $"settings-{slug}-{(which == AppTheme.Fluent ? "fluent" : "gaming-dark")}.png");
                }
            }
        }
        finally
        {
            window.CloseForQuit();
        }
    });

    [Fact]
    public void TheLastThreePagesShowTheirSectionsAsCardsInBothThemes() => Sta.Run(() =>
    {
        // Speakers is connected; Headset isn't, so its Note shows. Both outputs on Everything double count.
        var speakers = new LayoutDevice(Guid.NewGuid(), "{out-1}", "Speakers", "USB DAC", AudioFlow.Output, IsMic: false);
        var headset = new LayoutDevice(Guid.NewGuid(), "{out-2}", "Headset", "Arctis", AudioFlow.Output, IsMic: false);
        var layout = new TrackLayout(
            [speakers, headset],
            [new Track(Keys.EverythingTrackId, "Everything", [speakers.Key, headset.Key], MuteAffected: true), new Track(Guid.NewGuid(), "Chat", [], MuteAffected: false)]);
        var engine = new FakeEngineSession { Reply = (_, _) => Empty.Value };
        engine.State = engine.State! with
        {
            Devices = new AudioDeviceList(
            [
                new AudioDevice("{out-1}", "Speakers", "USB DAC", AudioFlow.Output, true, IsDefault: true),
                new AudioDevice("{in-1}", "Microphone", "Yeti", AudioFlow.Input, true, IsDefault: true),
            ]),
            Settings = SettingsDocument.Empty with
            {
                Revision = 1,
                Global = SettingsMaps.EmptyValues.Add(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(layout)),
                Profiles = SettingsMaps.EmptyProfiles
                    .Add(@"c:\a\a.exe", new GameProfile { Name = "Alpha", Ignored = true })
                    .Add(@"c:\b\b.exe", new GameProfile { Name = "Beta" }),
            },
        };
        var model = new MainViewModel(engine, action => action());
        var window = Offscreen(new SettingsWindow { DataContext = model.Settings });
        var theme = ThemeService.ForWindow(window);
        var expected = new (SettingsViewModel ViewModel, string Slug, string[] Sections)[]
        {
            (model.TrackLayout, "audio-tracks", []),
            (model.GameProfiles, "games", []),
            (model.LibrarySettings, "storage-and-cleanup", ["Clips folder", "Cleanup"]),
        };
        try
        {
            foreach (var which in new[] { AppTheme.Fluent, AppTheme.GamingDark })
            {
                theme.Apply(which);
                window.Show();
                var card = (Style)window.FindResource("EncoreCardStyle");
                var meter = (Style)window.FindResource("EncoreLevelMeterStyle");
                var accent = window.FindResource("AccentButtonStyle");
                foreach (var (viewModel, slug, sections) in expected)
                {
                    model.Settings.Select(viewModel);
                    Sta.Pump(TimeSpan.FromMilliseconds(100));
                    if (viewModel == model.TrackLayout)
                    {
                        // An unsaved draft (the save bar), fabricated levels, and the picker open.
                        var tracks = model.TrackLayout.Tracks;
                        if (tracks[1].Sources.Count == 0)
                        {
                            model.TrackLayout.AddSound(tracks[1], engine.State.Devices.Devices[1]);
                        }

                        tracks[0].ShowPeak(1f);
                        tracks[1].ShowPeak(0.0316f);
                        tracks[0].Sources[0].ShowPeak(0.3f);
                        Sta.Pump(TimeSpan.FromMilliseconds(50));
                        Assert.True(tracks[0].HasWarning);
                        Assert.True(((FrameworkElement)viewModel.View.FindName("SaveBar")).IsVisible);
                        var meters = Descendants<ProgressBar>(viewModel.View).ToList();
                        Assert.Equal(5, meters.Count);
                        Assert.All(meters, m => Assert.Same(meter, m.Style));

                        var picker = (System.Windows.Controls.Primitives.Popup)viewModel.View.FindName("Picker");
                        picker.PlacementTarget = viewModel.View;
                        model.TrackLayout.Picker.Open(tracks[1]);
                        Sta.Pump(TimeSpan.FromMilliseconds(100));
                        Assert.True(picker.IsOpen);
                        var flyout = (FrameworkElement)picker.Child;
                        Assert.Equal(2, Descendants<ListBoxItem>(flyout).Count());
                        Assert.All(Descendants<ProgressBar>(flyout), m => Assert.Same(meter, m.Style));
                        SaveElement(flyout, $"settings-audio-tracks-picker-{(which == AppTheme.Fluent ? "fluent" : "gaming-dark")}.png", window);
                        model.TrackLayout.Picker.Close();
                        tracks[0].ShowPeak(1f);
                        tracks[1].ShowPeak(0.0316f);
                        tracks[0].Sources[0].ShowPeak(0.3f);
                        tracks[1].Sources[0].ShowPeak(0.1f);
                        Sta.Pump(TimeSpan.FromMilliseconds(50));
                        foreach (var key in ViewResourceKeys())
                        {
                            Assert.True(window.TryFindResource(key) is not null, $"Missing resource {key} under {which}");
                        }
                    }

                    Assert.True(viewModel.View.IsLoaded, $"{viewModel.Title} didn't load under {which}");
                    var found = Descendants<HeaderedContentControl>(viewModel.View).Where(s => s.Visibility == Visibility.Visible).ToList();
                    Assert.Equal(sections, found.Select(s => s.Header as string));
                    Assert.All(found, s => Assert.Contains(Descendants<Border>(s), b => b.Style == card));

                    // A bug that bit: a keyed local style hid Gaming dark's implicit Button style. Save changes is the one accent button.
                    Assert.All(Descendants<Button>(viewModel.View).Where(b => b.Style != accent), b => Assert.Equal(DependencyProperty.UnsetValue, b.ReadLocalValue(FrameworkElement.StyleProperty)));
                    Save(window, $"settings-{slug}-{(which == AppTheme.Fluent ? "fluent" : "gaming-dark")}.png");

                    // A game's page, with the breadcrumb in the page header.
                    if (viewModel == model.GameProfiles)
                    {
                        model.GameProfiles.Open(model.GameProfiles.Profiles[0]);
                        Sta.Pump(TimeSpan.FromMilliseconds(150));
                        var game = model.GameProfiles.OpenGame!.View;
                        Assert.True(game.IsLoaded, $"A game's page didn't load under {which}");
                        Assert.True(((FrameworkElement)window.FindName("Breadcrumb")).IsVisible);
                        Assert.False(((FrameworkElement)window.FindName("PageTitle")).IsVisible);
                        var gameSections = Descendants<HeaderedContentControl>(game).Where(s => s.Visibility == Visibility.Visible).ToList();
                        Assert.Equal(["Capture", "Recordings", "Files"], gameSections.Select(s => s.Header as string));
                        Assert.All(gameSections, s => Assert.Contains(Descendants<Border>(s), b => b.Style == card));
                        Save(window, $"settings-game-page-{(which == AppTheme.Fluent ? "fluent" : "gaming-dark")}.png");
                        model.GameProfiles.DrillIn.Close();
                        Sta.Pump(TimeSpan.FromMilliseconds(50));
                    }
                }
            }
        }
        finally
        {
            window.CloseForQuit();
        }
    });

    [Fact]
    public void ARefusedUpdateShowsTheDangerBarUntilAnUpdateCommits() => Sta.Run(() =>
    {
        var engine = new FakeEngineSession();
        var model = new MainViewModel(engine, action => action());
        var window = Offscreen(new SettingsWindow { DataContext = model.Settings });
        ThemeService.ForWindow(window).Apply(AppTheme.Fluent);
        var message = (ContentControl)window.FindName("PageMessage");
        try
        {
            window.Show();
            model.Settings.Select(model.General);
            Sta.Pump(TimeSpan.FromMilliseconds(100));
            Assert.False(message.IsVisible);

            engine.Reply = (_, _) => throw new ErrorCodeException(ErrorCodes.SettingsWriteFailed, "Disk full.");
            model.General.StartWithWindows = true;
            Sta.Pump(TimeSpan.FromMilliseconds(50));

            Assert.True(message.IsVisible);
            Assert.Equal("Not saved (settings-write-failed): Disk full.", message.Content);
            var danger = ((SolidColorBrush)window.FindResource("SystemFillColorCriticalBrush")).Color;
            Assert.Contains(Descendants<Border>(message), b => b.BorderBrush is SolidColorBrush brush && brush.Color == danger);

            engine.Reply = (_, _) => new SettingsUpdateResult(1);
            model.General.StartWithWindows = true;
            Sta.Pump(TimeSpan.FromMilliseconds(50));
            Assert.False(message.IsVisible);
        }
        finally
        {
            window.CloseForQuit();
        }
    });

    [Fact]
    public void TheSettingsViewsHaveNoLiteralColourAndNoKeylessStyle()
    {
        foreach (var view in Views)
        {
            var xaml = File.ReadAllText(Path.Combine(MainFolder(), "src", "Encore.App", "Settings", view + ".xaml"));
            Assert.False(LiteralColour().IsMatch(xaml), $"{view} has a literal colour");
            Assert.False(KeylessStyle().IsMatch(xaml), $"{view} has a key-less style");
        }
    }

    /// <summary>Every theme resource key the settings views look up; keys the files define themselves are left out.</summary>
    private static IEnumerable<string> ViewResourceKeys()
    {
        var folder = Path.Combine(MainFolder(), "src", "Encore.App");
        var xaml = string.Concat(
            ((string[])[@"Settings\TrackLayoutView.xaml", @"Theme\AudioTracksTheme.xaml", @"Settings\FolderPickerView.xaml", @"Settings\RewindSettingsView.xaml", @"Settings\HotkeysView.xaml", @"Settings\LibrarySettingsView.xaml", @"Settings\GameProfilesView.xaml", @"Settings\GamePageView.xaml", @"Settings\GameClipsEditor.xaml", @"Settings\GameVideoEditor.xaml", @"Theme\GamesTheme.xaml", "DrillInHeader.xaml"])
            .Select(f => File.ReadAllText(Path.Combine(folder, f))));
        var local = LocalKey().Matches(xaml).Select(m => m.Groups[1].Value).ToHashSet();
        return ResourceKey().Matches(xaml).Select(m => m.Groups[1].Value).Where(k => !local.Contains(k)).Distinct();
    }

    private static void SaveElement(FrameworkElement element, string name, Window window)
    {
        var size = new Size(Math.Max(1, element.ActualWidth), Math.Max(1, element.ActualHeight));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle((Brush)window.FindResource("SolidBackgroundFillColorBaseBrush"), null, new Rect(size));
            context.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(size));
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name));
        encoder.Save(file);
    }

    private static SettingsWindow Offscreen(SettingsWindow window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -30000;
        window.Top = -30000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        return window;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var below in Descendants<T>(child))
            {
                yield return below;
            }
        }
    }

    // The builder's hand check against the renders, beside the test binary.
    private static void Save(Window window, string name)
    {
        var root = (FrameworkElement)window.Content;
        var size = new Size(root.ActualWidth, root.ActualHeight);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle((Brush)window.FindResource("SolidBackgroundFillColorBaseBrush"), null, new Rect(size));
            context.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(size));
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name));
        encoder.Save(file);
    }

    private static string MainFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Encore.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Encore.slnx wasn't found above the test.");
    }

    [GeneratedRegex(@"""(Gray|DarkRed|DarkOrange)""|#[0-9A-Fa-f]{3,8}\b")]
    private static partial Regex LiteralColour();

    [GeneratedRegex(@"<Style(?![^>]*x:Key)[^>]*TargetType=")]
    private static partial Regex KeylessStyle();

    [GeneratedRegex(@"\{(?:Dynamic|Static)Resource (\w+)\}")]
    private static partial Regex ResourceKey();

    [GeneratedRegex(@"x:Key=""(\w+)""")]
    private static partial Regex LocalKey();
}
