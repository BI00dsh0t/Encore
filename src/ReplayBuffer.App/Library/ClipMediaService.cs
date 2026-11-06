using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ReplayBuffer.Contracts.Messages;

namespace ReplayBuffer.App.Library;

internal interface IClipMediaService
{
    Task<ClipMediaResponse> ExecuteAsync(ClipMediaRequest request, IProgress<double>? progress, CancellationToken token);
}

/// <summary>Private worker pipe, independent of the capture service and its supervision.</summary>
internal sealed class ClipMediaService : IClipMediaService
{
    private readonly SemaphoreSlim _operation = new(1, 1);

    public async Task<ClipMediaResponse> ExecuteAsync(ClipMediaRequest request, IProgress<double>? progress, CancellationToken token)
    {
        await _operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ReplayBuffer.Engine.exe"))
                {
                    Arguments = "clip-media-worker", CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                },
            };
            process.Start();
            var drain = DrainAsync(process.StandardError);
            using var completed = new CancellationTokenSource();
            async Task StopUnresponsiveWorkerAsync()
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), completed.Token).ConfigureAwait(false);
                    if (!process.HasExited) { process.Kill(entireProcessTree: true); }
                }
                catch (OperationCanceledException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            using var stop = token.Register(() =>
            {
                try { process.StandardInput.Close(); }
                catch (InvalidOperationException) { }
                _ = StopUnresponsiveWorkerAsync();
            });
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, ClipMediaJsonContext.Default.ClipMediaRequest)).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
                ClipMediaResponse? result = null;
                while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (line.Length > 4 * 1024 * 1024) { throw new IOException("The media worker returned too much data."); }
                    var response = JsonSerializer.Deserialize(line, ClipMediaJsonContext.Default.ClipMediaResponse)
                        ?? throw new IOException("The media worker returned an invalid response.");
                    if (response.Kind == "progress") { progress?.Report(response.Progress); }
                    else { result = response; }
                }
                await process.WaitForExitAsync().ConfigureAwait(false);
                await drain.ConfigureAwait(false);
                if (result?.Output is null) { token.ThrowIfCancellationRequested(); }
                if (result is null || result.Kind == "error" || process.ExitCode != 0)
                { throw new IOException(result?.Error ?? "The media worker stopped unexpectedly."); }
                return result;
            }
            finally
            {
                process.StandardInput.Close();
                // The entire tree belongs to this creator invocation; no live service process is touched.
                if (!process.HasExited)
                {
                    using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try { await process.WaitForExitAsync(grace.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
                }
                await drain.ConfigureAwait(false);
                completed.Cancel();
            }
        }
        finally { _operation.Release(); }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer).ConfigureAwait(false) != 0) { }
    }
}
