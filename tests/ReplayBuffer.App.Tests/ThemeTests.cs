using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReplayBuffer.App.Theme;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App.Tests;

/// <summary>
/// The theme's logic (AD-23): the token keys, live switching on <c>settings.changed</c>, and every window
/// and page loading in both themes. Each test scopes the theme to its own window, since a process has one
/// <see cref="Application"/> and the other tests' views must not pick the theme up.
/// </summary>
[Collection(nameof(WpfCollection))]
public sealed partial class ThemeTests
{
    private static readonly AppTheme[] Both = [AppTheme.Fluent, AppTheme.GamingDark];

    [Fact]
    public void TheGamingDictionaryDefinesEveryTokenKeyAndFluentResolvesEachOne() => Sta.Run(() =>
    {
        var window = new Window();
        var theme = ThemeService.ForWindow(window);

        theme.Apply(AppTheme.Fluent);
        Assert.All(ThemeService.TokenKeys, key => Assert.True(window.TryFindResource(key) is not null, $"Fluent has no {key}"));
        Assert.All(new FluentTheme().Keys.Cast<object>(), key => Assert.True(theme.GamingDark.Contains(key), $"Gaming dark has no {key}"));
        Assert.All(ThemeService.TokenKeys, key => Assert.True(theme.GamingDark.Contains(key), $"Gaming dark has no {key}"));
    });

    [Fact]
    public void ASettingsChangedSwitchingTheThemeSwapsTheDictionaryInTheSameWindow() => Sta.Run(() =>
    {
        var engine = new FakeEngineSession();
        var button = new Button { Content = "Save" };
        var window = Offscreen(new Window { Content = button });
        var theme = ThemeService.ForWindow(window);
        theme.Apply(AppTheme.Fluent);
        var applied = new List<AppTheme>();
        theme.Follow(engine, action => action(), applied.Add);
        window.Show();
        Sta.Pump(TimeSpan.FromMilliseconds(100));

        engine.State = engine.State! with { Settings = WithTheme(AppTheme.GamingDark) };
        engine.RaiseChanged();
        Sta.Pump(TimeSpan.FromMilliseconds(100));

        Assert.Equal([AppTheme.GamingDark], applied);
        Assert.Contains(theme.GamingDark, window.Resources.MergedDictionaries);
        Assert.True(window.IsVisible);
        Assert.Same(button, window.Content);
        Assert.Equal(Color.FromRgb(0x1E, 0x22, 0x2A), ((SolidColorBrush)button.Background).Color);

        engine.State = engine.State with { Settings = WithTheme(AppTheme.Fluent) };
        engine.RaiseChanged();
        engine.RaiseChanged();

        Assert.Equal([AppTheme.GamingDark, AppTheme.Fluent], applied);
        Assert.DoesNotContain(theme.GamingDark, window.Resources.MergedDictionaries);
        Assert.Same(theme.ThemeDictionary, window.Resources.MergedDictionaries[^1]);
        window.Close();
    });

    [Fact]
    public void EveryResourceKeyTheAppsXamlUsesResolvesInBothThemes() => Sta.Run(() =>
    {
        var keys = Directory.EnumerateFiles(Path.Combine(MainFolder(), "src", "ReplayBuffer.App"), "*.xaml", SearchOption.AllDirectories)
            .SelectMany(file => DynamicResource().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value))
            .Where(key => !key.StartsWith('{'))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("TextFillColorPrimaryBrush", keys);

        var window = new Window();
        var theme = ThemeService.ForWindow(window);
        foreach (var which in Both)
        {
            theme.Apply(which);
            Assert.All(keys, key => Assert.True(window.TryFindResource(key) is not null, $"{which} has no {key}"));
        }
    });

    [Fact]
    public void TheGalleryAndEveryPageLoadInBothThemesWithNoBindingError() => Sta.Run(() =>
    {
        var errors = new BindingErrors();
        var level = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            var gallery = Offscreen(new ControlGallery());
            var galleryTheme = ThemeService.ForWindow(gallery);
            var model = new MainViewModel(new FakeEngineSession(), action => action());
            var main = Offscreen(new MainWindow { DataContext = model });
            var mainTheme = ThemeService.ForWindow(main);
            foreach (var which in Both)
            {
                galleryTheme.Apply(which);
                mainTheme.Apply(which);
                gallery.Show();
                main.Show();
                Sta.Pump(TimeSpan.FromMilliseconds(200));
                Save(gallery, $"gallery-{(which == AppTheme.Fluent ? "fluent" : "gaming-dark")}.png");

                model.IsSettingsTabSelected = true;
                foreach (var page in model.Pages)
                {
                    model.SelectedPage = page;
                    Sta.Pump(TimeSpan.FromMilliseconds(50));
                    Assert.True(page.View.IsLoaded, $"{page.Title} didn't load under {which}");
                }

                ((TabControl)((DockPanel)main.Content).Children[1]).SelectedIndex = 0;
                Sta.Pump(TimeSpan.FromMilliseconds(50));
                Assert.True(model.Library.View.IsLoaded, $"The Library didn't load under {which}");
            }

            gallery.Close();
            main.CloseForQuit();
            // Fluent's own ListBoxItem style binds its content alignment to the ancestor ItemsControl, which
            // WPF traces as an error while a container is detached; that is the framework's, not the App's.
            var found = string.Join("", errors.Messages)
                .Split("System.Windows.Data Error", StringSplitOptions.RemoveEmptyEntries)
                .Where(e => !FluentContainerAlignment().IsMatch(e))
                .ToList();
            Assert.True(found.Count == 0, string.Join(Environment.NewLine, found));
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            PresentationTraceSources.DataBindingSource.Switch.Level = level;
        }
    });

    [Fact]
    public void MicaIsBehindTheMainAppUnderFluentOnlyAndNeverBehindOtherWindows() => Sta.Run(() =>
    {
        Assert.SkipUnless(WindowFrame.SupportsMica, "This Windows draws no system backdrops.");
        var main = Offscreen(new MainWindow { DataContext = new MainViewModel(new FakeEngineSession(), action => action()) });
        var gallery = Offscreen(new ControlGallery());
        var mainTheme = ThemeService.ForWindow(main);
        var galleryTheme = ThemeService.ForWindow(gallery);
        main.Show();
        gallery.Show();
        mainTheme.Apply(AppTheme.Fluent);
        galleryTheme.Apply(AppTheme.Fluent);
        Sta.Pump(TimeSpan.FromMilliseconds(100));

        Assert.True(WindowFrame.HasMica(new WindowInteropHelper(main).Handle));
        Assert.False(WindowFrame.HasMica(new WindowInteropHelper(gallery).Handle));

        mainTheme.Apply(AppTheme.GamingDark);
        Assert.False(WindowFrame.HasMica(new WindowInteropHelper(main).Handle));
        mainTheme.Apply(AppTheme.Fluent);
        Assert.True(WindowFrame.HasMica(new WindowInteropHelper(main).Handle));

        gallery.Close();
        main.CloseForQuit();
    });

    [Fact]
    public void FluentDimsADisabledTextBoxAndListItem() => Sta.Run(() =>
    {
        var box = new TextBox { Text = "Disabled", IsEnabled = false };
        var item = new ListBoxItem { Content = "Disabled item", IsEnabled = false };
        var window = Offscreen(new Window { Content = new StackPanel { Children = { box, new ListBox { Items = { item } } } } });
        var theme = ThemeService.ForWindow(window);
        theme.Apply(AppTheme.Fluent);
        window.Show();
        Sta.Pump(TimeSpan.FromMilliseconds(100));

        var disabled = ((SolidColorBrush)window.FindResource("TextFillColorDisabledBrush")).Color;
        Assert.Equal(disabled, ((SolidColorBrush)box.Foreground).Color);
        Assert.Equal(disabled, ((SolidColorBrush)item.Foreground).Color);
        window.Close();
    });

    [Fact]
    public void TheLastAppliedThemeIsReadBackForTheNextStart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rb-theme-{Guid.NewGuid():N}", "app-theme.json");
        try
        {
            Assert.Equal(AppTheme.Fluent, ThemeService.ReadLast(path));

            ThemeService.WriteLast(path, AppTheme.GamingDark);

            Assert.Equal(AppTheme.GamingDark, ThemeService.ReadLast(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void OnlyDevAndTestInstancesGetTheControlGalleryTrayItem() => Sta.Run(() =>
    {
        var model = new MainViewModel(new FakeEngineSession(), action => action());
        var opened = 0;

        var dev = ShellApp.CreateTrayMenu(model, () => { }, () => { }, gallery: () => opened++);
        var installed = ShellApp.CreateTrayMenu(model, () => { }, () => { });

        var item = dev.Items.OfType<MenuItem>().Single(i => i.Name == ShellApp.GalleryItemName);
        Assert.Equal("Control gallery", item.Header);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, opened);
        Assert.DoesNotContain(installed.Items.OfType<MenuItem>(), i => i.Name == ShellApp.GalleryItemName);
    });

    private static SettingsDocument WithTheme(AppTheme theme) => SettingsDocument.Empty with
    {
        Revision = 1,
        Global = SettingsMaps.EmptyValues.Add(Keys.AppTheme.Name, Keys.AppTheme.ToJson(theme)),
    };

    private static T Offscreen<T>(T window) where T : Window
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -30000;
        window.Top = -30000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        return window;
    }

    // The builder's hand check: the gallery's whole content, beside the test binary.
    private static void Save(ControlGallery gallery, string name)
    {
        var root = (FrameworkElement)gallery.FindName("Root");
        var size = new Size(root.ActualWidth + root.Margin.Left + root.Margin.Right, root.ActualHeight + root.Margin.Top + root.Margin.Bottom);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(gallery.Background is SolidColorBrush { Color.A: 255 } solid ? solid : (Brush)gallery.FindResource("SolidBackgroundFillColorBaseBrush"), null, new Rect(size));
            context.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null,
                new Rect(root.Margin.Left, root.Margin.Top, root.ActualWidth, root.ActualHeight));
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
            if (File.Exists(Path.Combine(dir.FullName, "ReplayBuffer.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("ReplayBuffer.slnx wasn't found above the test.");
    }

    [GeneratedRegex(@"AncestorType='System\.Windows\.Controls\.ItemsControl'.*Path=(Horizontal|Vertical)ContentAlignment;.*target element is '(ListBoxItem|ComboBoxItem|MenuItem)'")]
    private static partial Regex FluentContainerAlignment();

    [GeneratedRegex(@"\{DynamicResource\s+([^}\s]+)\s*\}")]
    private static partial Regex DynamicResource();
}
