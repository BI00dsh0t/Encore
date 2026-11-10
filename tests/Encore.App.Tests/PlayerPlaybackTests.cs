using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Encore.App.Library;
using Encore.Contracts.Messages;

namespace Encore.App.Tests;

/// <summary>Heavy (nights only): real playback through Windows' media player on FFmpeg-made fixtures. Muted.</summary>
[Collection(nameof(WpfCollection))]
public sealed class PlayerPlaybackTests : IDisposable
{
    private const string Clip = @"Game\clip.mp4";

    private readonly string _folder = Directory.CreateTempSubdirectory("rb-player-playback-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A last handle closing late leaves the temp folder; it's cleaned with the rest of %TEMP%.
        }
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void AClipFixturePlaysIntoTheViewOnTrack1()
    {
        var absolute = MakeFixture("-f lavfi -i testsrc2=size=1280x720:rate=60:duration=2", "-c:v libx264 -preset ultrafast -g 60 -pix_fmt yuv420p", "-movflags +faststart");
        Run(absolute, (model, view) =>
        {
            var page = model.Library.Player!;
            Assert.Equal(["Game", "Mic", "Discord", "Desktop"], page.Media!.TrackNames);
            Assert.Equal(0, page.Player.SelectedTrack);

            page.PlayPauseCommand.Execute(null);
            Sta.Pump(TimeSpan.FromSeconds(1));
            Assert.True(PictureShare(view) > 0.5, "the picture is mostly video");

            // The engine's rename and delete never fail on a playing file.
            File.Move(absolute, absolute + ".renamed");
            File.Delete(absolute + ".renamed");
            Sta.Pump(TimeSpan.FromMilliseconds(300));
        });
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void A6400By2160HevcFixtureReachesTheViewScaledDown()
    {
        var absolute = MakeFixture("-f lavfi -i testsrc2=size=6400x2160:rate=60:duration=1", "-c:v libx265 -preset ultrafast -tag:v hvc1 -pix_fmt yuv420p -x265-params log-level=error", "-movflags +faststart");
        Run(absolute, (model, view) =>
        {
            Sta.PumpUntil(() => view.SurfaceSize is not null, "the surface");
            var (width, height) = view.SurfaceSize!.Value;
            Assert.True(width < 6400 && width <= view.ActualWidth * VisualTreeHelper.GetDpi(view).DpiScaleX + 1, $"surface {width}x{height}");
            Assert.True(PictureShare(view) > 0.3, "the picture shows the video");
        });
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void TwoHundredOpensAndClosesDoNotGrowMemory()
    {
        var absolute = MakeFixture("-f lavfi -i testsrc2=size=1280x720:rate=60:duration=2", "-c:v libx264 -preset ultrafast -g 60 -pix_fmt yuv420p", "-movflags +faststart");
        Run(absolute, (model, view) =>
        {
            long after10 = 0;
            for (var i = 1; i <= 200; i++)
            {
                model.Library.DrillIn.Close();
                model.Library.OpenFocused();
                Sta.PumpUntil(() => model.Library.Player?.State == PlayerState.Paused, "the player to open");
                Sta.PumpUntil(() => view.SurfaceSize is not null, "a frame");
                if (i == 10)
                {
                    after10 = PrivateBytes();
                }
            }

            model.Library.DrillIn.Close();
            var growth = PrivateBytes() - after10;
            Assert.True(growth < 50L << 20, $"private bytes grew {growth >> 20} MB over 190 opens");
        });
    }

    // At an inaudible volume, this process's own audio says which Track plays after each switch.
    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void TheChosenTrackIsTheOneHeardAndAPausedSwitchResumesInPlace()
    {
        double[] tones = [300, 600, 900, 1200];
        var absolute = MakeFixture("-f lavfi -i testsrc2=size=1280x720:rate=60:duration=12", "-c:v libx264 -preset ultrafast -g 60 -pix_fmt yuv420p", "-movflags +faststart", seconds: 12);
        Run(absolute, (model, view) =>
        {
            var page = model.Library.Player!;
            page.Volume = 0.001;
            page.IsMuted = false;
            using var loopback = ProcessLoopback.Start();
            page.PlayPauseCommand.Execute(null);
            Sta.Pump(TimeSpan.FromMilliseconds(500));

            var marks = new List<(TimeSpan At, int Track)>();
            foreach (var track in new[] { 2, 0, 1, 3, 0, 2, 0, 3, 1, 0 })
            {
                marks.Add((loopback.Now, track));
                page.SelectedTrack = page.Tracks[track];
                Sta.Pump(TimeSpan.FromMilliseconds(700));
            }

            var heard = marks.Select(m => loopback.Loudest(m.At + TimeSpan.FromMilliseconds(450), tones)).ToList();
            Assert.Equal(marks.Select(m => m.Track), heard);

            // Paused at 3 s, a switch, then Play: it plays on from 3 s, not from the start.
            page.PlayPauseCommand.Execute(null);
            page.SeekTo(TimeSpan.FromSeconds(3));
            Sta.Pump(TimeSpan.FromMilliseconds(300));
            page.SelectedTrack = page.Tracks[1];
            Sta.Pump(TimeSpan.FromMilliseconds(300));
            page.PlayPauseCommand.Execute(null);
            Sta.Pump(TimeSpan.FromMilliseconds(500));
            Assert.True(page.Player.Position > TimeSpan.FromSeconds(3), $"resumed at {page.Player.Position}");
        });
    }

    // A fragmented Recording plays, seeks and switches Track.
    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void ARecordingFixturePlaysSeeksAndSwitchesTrack()
    {
        var absolute = MakeFixture("-f lavfi -i testsrc2=size=1280x720:rate=60:duration=10", "-c:v libx264 -preset ultrafast -g 60 -pix_fmt yuv420p", "-movflags frag_keyframe+empty_moov+default_base_moof -frag_duration 1000000", seconds: 10);
        Run(absolute, (model, view) =>
        {
            var page = model.Library.Player!;
            page.SeekTo(TimeSpan.FromSeconds(5));
            page.SelectedTrack = page.Tracks[2];
            page.PlayPauseCommand.Execute(null);
            Sta.Pump(TimeSpan.FromMilliseconds(800));
            Assert.Equal(2, page.Player.SelectedTrack);
            Assert.True(page.Player.Position > TimeSpan.FromSeconds(5), $"played on from 5 s: {page.Player.Position}");
            Assert.True(PictureShare(view) > 0.5, "the picture is mostly video");
        });
    }

    // Opens the fixture's row in the Main App window, muted, paused on its first frame, then runs the checks and goes back.
    private void Run(string absolute, Action<MainViewModel, PlayerView> check) => Sta.Run(() =>
    {
        var engine = new FakeEngineSession();
        engine.State = engine.State! with
        {
            LibraryFolder = _folder,
            Library = [new LibraryItem(Clip, null, new FileInfo(absolute).Length, DateTimeOffset.UnixEpoch, false)],
        };
        var model = new MainViewModel(engine, action => action(), player: () => new WindowsMediaPlayer());
        var window = new MainWindow
        {
            DataContext = model,
            Width = 1280,
            Height = 800,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        try
        {
            window.Show();
            var view = (LibraryView)model.Library.View;
            Sta.PumpUntil(() => view.IsLoaded, "the Library to load");
            model.Library.Click(model.Library.Rows.Single(), ctrl: false, shift: false);
            model.Library.OpenFocused();
            Sta.PumpUntil(() => model.Library.Player?.State == PlayerState.Paused, "the player to open");

            // Muted through the page: it applies its own volume and mute to the player as it opens.
            model.Library.Player!.IsMuted = true;
            var player = (PlayerView)view.FindName("PlayerContent");
            Sta.PumpUntil(() => player.SurfaceSize is not null, "the first frame");

            check(model, player);

            model.CurrentDrillIn!.Close();
            Sta.Pump(TimeSpan.FromMilliseconds(200));
            Assert.Null(model.Library.Player);
        }
        finally
        {
            window.CloseForQuit();
        }
    });

    // How much of the picture WPF draws is video rather than black.
    private static double PictureShare(PlayerView view)
    {
        var image = (Image)view.FindName("Picture");
        var bitmap = new RenderTargetBitmap((int)image.ActualWidth, (int)image.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(image);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var lit = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 30)
            {
                lit++;
            }
        }

        return lit / (pixels.Length / 4.0);
    }

    private static long PrivateBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    // The fixture: the video and four AAC Tracks of different tones, only the first marked default.
    private string MakeFixture(string video, string encode, string mux, int seconds = 2)
    {
        var ffmpeg = FindFFmpeg();
        Assert.SkipWhen(ffmpeg is null, "native/ffmpeg/ffmpeg.exe isn't there.");
        var absolute = Path.Combine(_folder, Clip);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        var tones = string.Join(' ', new[] { 300, 600, 900, 1200 }.Select(f => $"-f lavfi -i sine=frequency={f}:duration={seconds}"));
        var names = string.Join(' ', new[] { "Game", "Mic", "Discord", "Desktop" }.Select((n, i) => $"-metadata:s:a:{i} title={n} -metadata:s:a:{i} handler_name={n} -disposition:a:{i} {(i == 0 ? "default" : "0")}"));
        var arguments = $"-hide_banner -loglevel error -y {video} {tones} -map 0:v -map 1:a -map 2:a -map 3:a -map 4:a -shortest {encode} -c:a aac -ac 2 {names} {mux} \"{absolute}\"";
        using var process = Process.Start(new ProcessStartInfo(ffmpeg!, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return absolute;
    }

    private static string? FindFFmpeg()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "native", "ffmpeg", "ffmpeg.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
