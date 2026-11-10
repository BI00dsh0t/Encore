using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Encore.Bench;

/// <summary>What a scripted player check saw; Plays is null when the check itself could not run.</summary>
internal sealed record ScriptedPlayback(
    string Player,
    bool? Plays,
    double? PlayedSeconds,
    double? WallSeconds,
    int? Width,
    int? Height,
    double? DurationSeconds,
    int? AudioTracks,
    IReadOnlyList<string>? TrackLabels,
    long? FramesDecoded,
    long? FramesDropped,
    string? Error);

/// <summary>The scripted players: headless Chrome and Windows Media Player; they don't measure A/V sync.</summary>
internal static class PlayerScripts
{
    public const string Chrome = "Chrome";
    public const string WindowsMediaPlayer = "Windows Media Player";
    public const string Scripted = "scripted";
    public const string Person = "person";

    private const string MediaFailure = "media";

    public static bool IsScripted(string player) =>
        player.Equals(Chrome, StringComparison.OrdinalIgnoreCase) || player.Equals(WindowsMediaPlayer, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a script's JSON result; every field is optional, and errorKind <c>media</c> means the file was refused.</summary>
    internal static ScriptedPlayback Parse(string player, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failed(player, "the result is not a JSON object");
            }

            var played = Number(root, "playedSeconds");
            var wall = Number(root, "wallSeconds");
            var error = Text(root, "error");
            var kind = Text(root, "errorKind");
            bool? plays = error is not null
                ? kind == MediaFailure ? false : null
                : played is { } p && wall is > 0 ? p >= wall.Value * 0.5 : null;
            var labels = root.TryGetProperty("trackLabels", out var l) && l.ValueKind == JsonValueKind.Array
                ? l.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString()).ToList()
                : null;
            return new ScriptedPlayback(
                player,
                plays,
                Round(played),
                Round(wall),
                (int?)Number(root, "width"),
                (int?)Number(root, "height"),
                Round(Number(root, "durationSeconds")),
                (int?)Number(root, "audioTracks"),
                labels,
                (long?)Number(root, "framesDecoded"),
                (long?)Number(root, "framesDropped"),
                error ?? (plays is null ? "the script reported no playback figures" : null));
        }
        catch (JsonException e)
        {
            return Failed(player, $"unreadable result ({e.Message}): {Shorten(json)}");
        }
    }

    /// <summary>The checklist row for a scripted check.</summary>
    internal static PlayerCheck ToCheck(ScriptedPlayback result)
    {
        if (result.Plays is null)
        {
            return new PlayerCheck(result.Player, "not-checked", null, null, null, $"scripted check could not run: {result.Error}", Scripted);
        }

        var tracks = result.AudioTracks is { } n
            ? n.ToString(CultureInfo.InvariantCulture) + " listed" + (result.TrackLabels is { Count: > 0 } labels ? $" ({string.Join(", ", labels)})" : "")
            : "not listed by the player";
        var notes = new List<string>();
        if (result.Plays == true)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"advanced {result.PlayedSeconds:0.##} s in {result.WallSeconds:0.##} s"));
        }
        else
        {
            notes.Add("refused the file: " + (result.Error ?? "playback did not advance"));
        }

        if (result.Width is > 0 && result.Height is > 0)
        {
            notes.Add($"{result.Width}x{result.Height}");
        }

        if (result.FramesDecoded is { } decoded)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{decoded} frames decoded, {result.FramesDropped ?? 0} dropped"));
        }

        notes.Add("A/V sync needs a person");
        return new PlayerCheck(result.Player, "checked", result.Plays, null, tracks, "scripted: " + string.Join("; ", notes), Scripted);
    }

    /// <summary>The checklist row for a player only a person can check.</summary>
    internal static PlayerCheck NeedsPerson(string player) => new(player, "not-checked", null, null, null, "needs a person", Person);

    /// <summary><c>--chrome</c>, or Chrome where its installers put it.</summary>
    public static string? FindChrome(string? given)
    {
        if (given is not null)
        {
            return File.Exists(given) ? Path.GetFullPath(given) : null;
        }

        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ];
        return roots.Where(r => r.Length > 0)
            .Select(r => Path.Combine(r, "Google", "Chrome", "Application", "chrome.exe"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Plays the clip in headless Chrome from a loopback HTTP server, and takes the page's report.</summary>
    public static ScriptedPlayback RunChrome(string chrome, string clip, string work, int playSeconds, TimeSpan timeout)
    {
        var profile = Path.Combine(work, "playback-chrome-profile");
        var port = FreePort();
        var prefix = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/";
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException e)
        {
            return Failed(Chrome, $"could not listen on {prefix}: {e.Message}");
        }

        var page = Encoding.UTF8.GetBytes(ChromePage.Replace("PLAY_MS", (playSeconds * 1000).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        var server = Task.Run(() => Serve(listener, page, clip, result));
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo(chrome)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in new[]
            {
                "--headless=new", $"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check", "--disable-extensions",
                "--disable-sync", "--disable-background-networking", "--disable-component-update", "--no-pings",
                "--autoplay-policy=no-user-gesture-required", "--mute-audio", "--enable-blink-features=AudioVideoTracks",
                prefix,
            })
            {
                start.ArgumentList.Add(arg);
            }

            process = Process.Start(start);
            if (process is null)
            {
                return Failed(Chrome, $"could not start {chrome}");
            }

            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            var done = Task.WaitAny([result.Task, process.WaitForExitAsync()], timeout);
            return done == 0
                ? Parse(Chrome, result.Task.Result)
                : done == 1
                    ? Failed(Chrome, $"Chrome exited (code {process.ExitCode}) before the page reported")
                    : Failed(Chrome, $"no report from the page within {timeout.TotalSeconds:0} s");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return Failed(Chrome, $"could not start {chrome}: {e.Message}");
        }
        finally
        {
            Kill(process);
            listener.Stop();
            server.Wait(TimeSpan.FromSeconds(5));
            DeleteQuietly(profile);
        }
    }

    /// <summary>Plays the clip through Windows Media Player's engine, from Windows PowerShell on an STA thread.</summary>
    public static ScriptedPlayback RunWindowsMediaPlayer(string clip, string work, int playSeconds, TimeSpan timeout)
    {
        var script = Path.Combine(work, "playback-wmp.ps1");
        File.WriteAllText(script, WmpScript);
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
        {
            return Failed(WindowsMediaPlayer, $"Windows PowerShell is not at {powershell}");
        }

        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-STA", "-File", script,
            "-Clip", clip, "-PlaySeconds", playSeconds.ToString(CultureInfo.InvariantCulture),
        })
        {
            start.ArgumentList.Add(arg);
        }

        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null)
            {
                return Failed(WindowsMediaPlayer, $"could not start {powershell}");
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                return Failed(WindowsMediaPlayer, $"the script did not finish within {timeout.TotalSeconds:0} s");
            }

            var line = stdout.Result.Split('\n').Select(s => s.Trim()).LastOrDefault(s => s.StartsWith('{'));
            return line is not null
                ? Parse(WindowsMediaPlayer, line)
                : Failed(WindowsMediaPlayer, $"the script printed no result (exit {process.ExitCode}): {Shorten(stderr.Result.Trim())}");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return Failed(WindowsMediaPlayer, $"could not start {powershell}: {e.Message}");
        }
        finally
        {
            Kill(process);
        }
    }

    /// <summary><c>--vlc</c> when that file exists, or VLC where its installers put it; null when there is none.</summary>
    public static string? FindVlc(string? given)
    {
        if (given is not null)
        {
            return File.Exists(given) ? Path.GetFullPath(given) : null;
        }

        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];
        return roots.Where(r => r.Length > 0)
            .Select(r => Path.Combine(r, "VideoLAN", "VLC", "vlc.exe"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Opens the clip in VLC with only its HTTP interface and returns each audio stream's title; VLC is always stopped.</summary>
    public static VlcListing RunVlc(string vlc, string clip, int audioStreams, TimeSpan timeout)
    {
        var port = FreePort();
        var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var start = new ProcessStartInfo(vlc)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "--ignore-config", "--no-one-instance", "-I", "http", "--http-host", "127.0.0.1",
            "--http-port", port.ToString(CultureInfo.InvariantCulture), "--http-password", password,
            "--aout", "adummy", "--vout", "vdummy", "--play-and-pause", "--no-loop", "--no-repeat", clip,
        })
        {
            start.ArgumentList.Add(arg);
        }

        // VLC names its info categories in its interface language.
        start.Environment["LANG"] = "en";

        var status = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/requests/status.json");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + password)));

        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null)
            {
                return new VlcListing(true, null, $"could not start {vlc}");
            }

            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            var clock = Stopwatch.StartNew();
            List<string>? labels = null;
            string? lastError = null;
            while (clock.Elapsed < timeout)
            {
                if (process.HasExited)
                {
                    return new VlcListing(true, labels, $"VLC exited (code {process.ExitCode}) before it listed the streams");
                }

                try
                {
                    labels = AudioLabels(http.GetStringAsync(status).GetAwaiter().GetResult());
                    lastError = null;
                    if (labels.Count >= audioStreams)
                    {
                        break;
                    }
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
                {
                    lastError = e.Message;
                }

                Thread.Sleep(250);
            }

            try
            {
                http.GetStringAsync(new Uri(status + "?command=pl_stop")).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                // It is killed next anyway.
            }

            return labels is { Count: >= 1 } && labels.Count >= audioStreams
                ? new VlcListing(true, labels, null)
                : new VlcListing(true, labels, $"VLC listed {labels?.Count ?? 0} of {audioStreams} audio streams within {timeout.TotalSeconds:0} s" + (lastError is null ? "" : $" ({lastError})"));
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return new VlcListing(true, null, $"could not start {vlc}: {e.Message}");
        }
        finally
        {
            Kill(process);
        }
    }

    /// <summary>The description of each audio stream in VLC's status JSON, in stream order; an untitled one is an empty string.</summary>
    internal static List<string> AudioLabels(string statusJson)
    {
        using var document = JsonDocument.Parse(statusJson);
        var streams = new List<(int N, string Label)>();
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("information", out var information) && information.ValueKind == JsonValueKind.Object
            && information.TryGetProperty("category", out var category) && category.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in category.EnumerateObject())
            {
                if (!entry.Name.StartsWith("Stream ", StringComparison.Ordinal)
                    || !int.TryParse(entry.Name.AsSpan("Stream ".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                    || entry.Value.ValueKind != JsonValueKind.Object
                    || Text(entry.Value, "Type") != "Audio")
                {
                    continue;
                }

                streams.Add((n, entry.Value.TryGetProperty("Description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : ""));
            }
        }

        return [.. streams.OrderBy(s => s.N).Select(s => s.Label)];
    }

    private static ScriptedPlayback Failed(string player, string error) =>
        new(player, null, null, null, null, null, null, null, null, null, null, error);

    private static async Task Serve(HttpListener listener, byte[] page, string clip, TaskCompletionSource<string> result)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => Answer(context, page, clip, result));
        }
    }

    private static async Task Answer(HttpListenerContext context, byte[] page, string clip, TaskCompletionSource<string> result)
    {
        var response = context.Response;
        try
        {
            switch (context.Request.Url?.AbsolutePath)
            {
                case "/":
                    response.ContentType = "text/html; charset=utf-8";
                    response.ContentLength64 = page.Length;
                    await response.OutputStream.WriteAsync(page).ConfigureAwait(false);
                    break;
                case "/clip.mp4":
                    await SendClip(context, clip).ConfigureAwait(false);
                    break;
                case "/result" when context.Request.HttpMethod == "POST":
                    using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    {
                        result.TrySetResult(await reader.ReadToEndAsync().ConfigureAwait(false));
                    }

                    response.StatusCode = 204;
                    break;
                default:
                    response.StatusCode = 404;
                    break;
            }
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Chrome dropped a media request it no longer needs.
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // Already gone.
            }
        }
    }

    /// <summary>The clip, or the byte range the request names (<c>bytes=a-b</c> or <c>bytes=a-</c>).</summary>
    private static async Task SendClip(HttpListenerContext context, string clip)
    {
        var response = context.Response;
        await using var file = new FileStream(clip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = file.Length;
        long from = 0, to = length - 1;
        response.ContentType = "video/mp4";
        response.AddHeader("Accept-Ranges", "bytes");
        if (ParseRange(context.Request.Headers["Range"], length) is { } range)
        {
            (from, to) = range;
            response.StatusCode = 206;
            response.AddHeader("Content-Range", $"bytes {from}-{to}/{length}");
        }

        response.ContentLength64 = to - from + 1;
        file.Position = from;
        var buffer = new byte[64 * 1024];
        var left = to - from + 1;
        while (left > 0)
        {
            var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left))).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await response.OutputStream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            left -= read;
        }
    }

    internal static (long From, long To)? ParseRange(string? header, long length)
    {
        if (header is null || !header.StartsWith("bytes=", StringComparison.Ordinal) || length <= 0)
        {
            return null;
        }

        var parts = header["bytes=".Length..].Split(',')[0].Split('-');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var from) || from >= length)
        {
            return null;
        }

        var to = parts[1].Length == 0 ? length - 1
            : long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var end) ? Math.Min(end, length - 1)
            : -1;
        return to >= from ? (from, to) : null;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static void Kill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It exited in between.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void DeleteQuietly(string folder)
    {
        for (var attempt = 0; attempt < 5 && Directory.Exists(folder); attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Chrome's children may still hold a file for a moment.
                Thread.Sleep(500);
            }
        }
    }

    private static double? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static double? Round(double? value) => value is { } v ? Math.Round(v, 3) : null;

    private static string Shorten(string text) => text.Length > 300 ? text[..300] + " ..." : text;

    /// <summary>The page Chrome loads: it plays the clip muted for PLAY_MS and posts what it saw to <c>/result</c>.</summary>
    private const string ChromePage =
        """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Encore Bench playback</title></head>
        <body>
        <video id="v" src="/clip.mp4" muted playsinline preload="auto"></video>
        <script>
        const v = document.getElementById('v');
        const r = {};
        let sent = false;
        const send = () => {
          if (sent) return;
          sent = true;
          fetch('/result', { method: 'POST', body: JSON.stringify(r) });
        };
        const tracks = () => {
          r.width = v.videoWidth;
          r.height = v.videoHeight;
          r.durationSeconds = isFinite(v.duration) ? v.duration : null;
          if (v.audioTracks) {
            r.audioTracks = v.audioTracks.length;
            r.trackLabels = Array.from(v.audioTracks).map(t => t.label || t.language || t.id || '');
          }
        };
        v.addEventListener('error', () => {
          r.error = 'media error ' + (v.error ? v.error.code + ': ' + v.error.message : '');
          r.errorKind = 'media';
          send();
        });
        v.addEventListener('loadedmetadata', tracks);
        v.play().then(async () => {
          tracks();
          const t0 = performance.now();
          const c0 = v.currentTime;
          await new Promise(done => setTimeout(done, PLAY_MS));
          r.playedSeconds = v.currentTime - c0;
          r.wallSeconds = (performance.now() - t0) / 1000;
          const q = v.getVideoPlaybackQuality();
          r.framesDecoded = q.totalVideoFrames;
          r.framesDropped = q.droppedVideoFrames;
          tracks();
          send();
        }).catch(e => {
          r.error = 'play() was refused: ' + e;
          r.errorKind = (e && e.name === 'NotSupportedError') ? 'media' : 'setup';
          send();
        });
        </script>
        </body></html>
        """;

    /// <summary>The Windows Media Player check: the OCX plays the clip muted and the script prints one JSON line.</summary>
    private const string WmpScript =
        """
        param([string] $Clip, [int] $PlaySeconds = 5)
        $ErrorActionPreference = 'Stop'
        $r = [ordered]@{}
        $wmp = $null

        # The OCX only moves on while this STA thread pumps its messages.
        Add-Type -AssemblyName System.Windows.Forms
        function Wait-Pumped([double] $Seconds, [scriptblock] $Until = { $false }) {
            $clock = [Diagnostics.Stopwatch]::StartNew()
            while ($clock.Elapsed.TotalSeconds -lt $Seconds -and -not (& $Until)) {
                [System.Windows.Forms.Application]::DoEvents()
                Start-Sleep -Milliseconds 20
            }
        }

        try {
            try { $wmp = New-Object -ComObject WMPlayer.OCX }
            catch { $r.error = "WMPlayer.OCX is not available: $($_.Exception.Message)"; $r.errorKind = 'setup'; throw }

            $wmp.settings.autoStart = $false
            $wmp.settings.mute = $true
            $wmp.URL = $Clip
            $wmp.controls.play()

            # playState 3 is Playing.
            Wait-Pumped 20 { $wmp.playState -eq 3 }
            if ($wmp.playState -ne 3) {
                # Only an error the OCX reports means it refused the file; a plain timeout leaves it not checked.
                if ($wmp.error.errorCount -gt 0) {
                    $r.error = "it never started playing: $($wmp.error.item(0).errorDescription)"
                    $r.errorKind = 'media'
                }
                else {
                    $r.error = "it did not start playing within 20 s (playState $($wmp.playState))"
                    $r.errorKind = 'timeout'
                }
            }
            else {
                $p0 = $wmp.controls.currentPosition
                $clock = [Diagnostics.Stopwatch]::StartNew()
                Wait-Pumped $PlaySeconds
                $r.playedSeconds = $wmp.controls.currentPosition - $p0
                $r.wallSeconds = $clock.Elapsed.TotalSeconds
            }

            $media = $wmp.currentMedia
            if ($media) {
                $r.width = $media.imageSourceWidth
                $r.height = $media.imageSourceHeight
                $r.durationSeconds = $media.duration
            }

            try { $r.audioTracks = [int] $wmp.controls.audioLanguageCount } catch { }
        }
        catch {
            if (-not $r.Contains('error')) { $r.error = "$($_.Exception.Message)"; $r.errorKind = 'setup' }
        }
        finally {
            if ($wmp) { try { $wmp.controls.stop(); $wmp.close() } catch { } }
        }

        $r | ConvertTo-Json -Compress
        """;
}
