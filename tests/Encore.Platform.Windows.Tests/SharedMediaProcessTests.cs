using System.Diagnostics;
using Encore.Platform.Windows;

namespace Encore.Platform.Windows.Tests;

public sealed class SharedMediaProcessTests
{
    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("C:\\folder\\", "\"C:\\folder\\\\\"")]
    public void QuotesWindowsArgumentsIncludingTrailingBackslashes(string argument, string expected) => Assert.Equal(expected, SharedMediaProcess.Quote(argument));

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task SeekableInheritedSourceCanBeRenamedAndDeletedAndOwnedDecodeCanBeCancelled()
    {
        var tools = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "native", "ffmpeg"));
        var ffmpeg = Path.Combine(tools, "ffmpeg.exe");
        Assert.SkipUnless(File.Exists(ffmpeg), "Run fetch-ffmpeg first.");
        var folder = Directory.CreateTempSubdirectory("rb-shared-tool-").FullName;
        try
        {
            var source = Path.Combine(folder, "source.mp4");
            var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30:duration=2", "-c:v", "libx264", "-threads", "2", source }) { info.ArgumentList.Add(arg); }
            using (var generate = Process.Start(info)!)
            {
                var error = generate.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                await generate.WaitForExitAsync(TestContext.Current.CancellationToken);
                Assert.True(generate.ExitCode == 0, await error);
            }
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var run = SharedMediaProcess.RunAsync(ffmpeg,
                ["-v", "error", "-nostdin", "-re", "-stream_loop", "-1", "-ss", "0.5", "-i", "fd:", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1"], source,
                async (stream, token) =>
                {
                    var buffer = new byte[8192];
                    while (await stream.ReadAsync(buffer, token) != 0) { read.TrySetResult(); }
                }, cancel.Token);
            await read.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            File.Move(source, source + ".renamed");
            File.Delete(source + ".renamed");
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        finally { Directory.Delete(folder, true); }
    }
}
