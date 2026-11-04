using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

public static class Program
{
    static bool Gaming;
    static string U(string s) => Gaming ? s.ToUpperInvariant() : s;
    [STAThread]
    public static void Main(string[] args)
    {
        Gaming = args.Length > 0 && args[0] == "gaming";
        var outPath = args.Length > 1 ? args[1] : "out.png";
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (Gaming)
        {
            using var s = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "gaming-theme.xml"));
            app.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Load(s));
        }
        else
        {
            app.ThemeMode = args.Length > 0 && args[0] == "light" ? ThemeMode.Light : ThemeMode.Dark;
        }
        app.Startup += (_, _) =>
        {
            var root = BuildRoot();
            var w = new Window
            {
                Content = root, Width = 1180, Height = 720, Left = -30000, Top = -30000,
                WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false,
                Title = "ReplayBuffer",
            };
            w.ContentRendered += (_, _) =>
            {
                w.Dispatcher.InvokeAsync(() =>
                {
                    var rtb = new RenderTargetBitmap((int)root.ActualWidth * 2, (int)root.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
                    rtb.Render(root);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = File.Create(outPath)) enc.Save(fs);
                    app.Shutdown();
                }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            };
            w.Show();
        };
        app.Run();
    }

    static T Res<T>(T e, DependencyProperty p, string key) where T : FrameworkElement { e.SetResourceReference(p, key); return e; }

    static TextBlock Text(string s, double size = 14, FontWeight? weight = null, string brush = "TextFillColorPrimaryBrush")
        => Res(new TextBlock { Text = s, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, TextBlock.ForegroundProperty, brush);

    static Border Card(UIElement child, double pad = 16)
    {
        var b = new Border { Child = child, Padding = new Thickness(pad), CornerRadius = new CornerRadius(Gaming ? 4 : 8), BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return b;
    }

    static FrameworkElement BuildRoot()
    {
        var root = new Grid();
        root.SetResourceReference(Panel.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        var nav = new StackPanel { Margin = new Thickness(12, 16, 8, 16) };
        nav.Children.Add(Text(U("ReplayBuffer"), 15, Gaming ? FontWeights.Black : FontWeights.SemiBold));
        nav.Children.Add(new Border { Height = 14 });
        var list = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        foreach (var n in new[] { "Library", "Recordings", "Game profiles", "Hotkeys", "Audio tracks", "Feedback sounds", "Settings" })
            list.Items.Add(new ListBoxItem { Content = U(n) });
        list.SelectedIndex = 0;
        nav.Children.Add(list);
        root.Children.Add(nav);

        var main = new DockPanel { Margin = new Thickness(8, 16, 20, 16), LastChildFill = true };
        Grid.SetColumn(main, 1);
        root.Children.Add(main);

        var status = new DockPanel();
        var save = new Button { Content = U("Save clip   Alt+F10"), Padding = new Thickness(14, 6, 14, 6), VerticalAlignment = VerticalAlignment.Center };
        save.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        DockPanel.SetDock(save, Dock.Right);
        status.Children.Add(save);
        var dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Gaming ? Color.FromRgb(0xFF, 0x46, 0x55) : Color.FromRgb(0x6C, 0xCB, 0x5F)), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(dot);
        var st = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        st.Children.Add(Text(U("Buffering · Valorant"), 14, Gaming ? FontWeights.Bold : FontWeights.SemiBold));
        st.Children.Add(Text("Last 2:00 in memory · 1440p60 · NVENC HEVC", 12, null, "TextFillColorSecondaryBrush"));
        status.Children.Add(st);
        var statusCard = Card(status, 14);
        statusCard.Margin = new Thickness(0, 0, 0, 16);
        DockPanel.SetDock(statusCard, Dock.Top);
        main.Children.Add(statusCard);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var filters = new StackPanel { Orientation = Orientation.Horizontal };
        var search = new TextBox { Width = 200, Text = "Search clips", Margin = new Thickness(0, 0, 8, 0) };
        var games = new ComboBox { Width = 150, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var g in new[] { "All games", "Valorant", "Elden Ring", "Rocket League" }) games.Items.Add(g);
        games.SelectedIndex = 0;
        var fav = new ToggleButton { Content = U("★ Favourites"), Padding = new Thickness(12, 5, 12, 5) };
        filters.Children.Add(search); filters.Children.Add(games); filters.Children.Add(fav);
        DockPanel.SetDock(filters, Dock.Right);
        header.Children.Add(filters);
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(Text(U("Library"), 26, Gaming ? FontWeights.Black : FontWeights.SemiBold));
        var count = Text("   128 clips · 41 GB", 13, null, "TextFillColorSecondaryBrush");
        count.Margin = new Thickness(0, 6, 0, 0);
        title.Children.Add(count);
        header.Children.Add(title);
        DockPanel.SetDock(header, Dock.Top);
        main.Children.Add(header);

        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        strip.Children.Add(new CheckBox { Content = "Cleanup: delete clips older than 30 days", IsChecked = true, Margin = new Thickness(0, 0, 24, 0) });
        strip.Children.Add(Text("Sound volume  ", 13, null, "TextFillColorSecondaryBrush"));
        strip.Children.Add(new Slider { Width = 160, Minimum = 0, Maximum = 100, Value = 70, Margin = new Thickness(0, 0, 24, 0) });
        strip.Children.Add(new RadioButton { Content = "Memory", IsChecked = true, GroupName = "m", Margin = new Thickness(0, 0, 12, 0) });
        strip.Children.Add(new RadioButton { Content = "Disk", GroupName = "m" });
        foreach (FrameworkElement c in strip.Children) c.VerticalAlignment = VerticalAlignment.Center;
        var stripCard = Card(strip, 12);
        stripCard.Margin = new Thickness(0, 4, 12, 0);
        DockPanel.SetDock(stripCard, Dock.Bottom);
        main.Children.Add(stripCard);

        var grid = new UniformGrid { Columns = 3 };
        var clips = new (string Name, string When, string Len, Color A, Color B, bool Fav)[]
        {
            ("Valorant clutch", "Today 21:14 · 182 MB", "0:30", Color.FromRgb(0xC8,0x3B,0x4A), Color.FromRgb(0x3A,0x1C,0x4F), true),
            ("Elden Ring boss", "Today 19:02 · 364 MB", "1:00", Color.FromRgb(0x8A,0x6A,0x2E), Color.FromRgb(0x24,0x22,0x1C), false),
            ("Rocket League goal", "Yesterday · 181 MB", "0:30", Color.FromRgb(0x1F,0x7A,0xC4), Color.FromRgb(0x1B,0x2B,0x45), false),
            ("Valorant ace", "Yesterday · 240 MB", "0:45", Color.FromRgb(0xB0,0x33,0x55), Color.FromRgb(0x2A,0x1A,0x38), true),
            ("Monitor recording", "Mon 22:40 · 2.1 GB", "12:08", Color.FromRgb(0x3E,0x6E,0x5A), Color.FromRgb(0x1A,0x2A,0x25), false),
            ("Elden Ring invasion", "Mon 20:11 · 300 MB", "0:50", Color.FromRgb(0x6E,0x5A,0x30), Color.FromRgb(0x20,0x1E,0x1A), false),
        };
        foreach (var c in clips)
        {
            var sp = new StackPanel();
            var thumb = new Grid { Height = 118 };
            thumb.Children.Add(new Border { CornerRadius = new CornerRadius(Gaming ? 2 : 6), Background = new LinearGradientBrush(c.A, c.B, 35) });
            thumb.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8),
                Child = new TextBlock { Text = c.Len, Foreground = Brushes.White, FontSize = 12 },
            });
            sp.Children.Add(thumb);
            var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var star = new ToggleButton { Content = "★", IsChecked = c.Fav, Padding = new Thickness(8, 2, 8, 3), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(star, Dock.Right);
            row.Children.Add(star);
            var t = new StackPanel();
            t.Children.Add(Text(c.Name, 14, FontWeights.SemiBold));
            t.Children.Add(Text(c.When, 12, null, "TextFillColorSecondaryBrush"));
            row.Children.Add(t);
            sp.Children.Add(row);
            var card = Card(sp, 10);
            card.Margin = new Thickness(0, 0, 12, 12);
            grid.Children.Add(card);
        }
        main.Children.Add(grid);
        return root;
    }
}
