using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Encore.Contracts.Messages;

namespace Encore.App.Library;

/// <summary>One run of the engine's clip media worker: a process that takes one request on its input and answers on its output.</summary>
internal sealed class ClipMediaWorkerProcess : IDisposable
{
    private const int MaxLineLength = 4 * 1024 * 1024;

    /// <summary>How long the worker gets to exit by itself, after its input closes, before it is killed.</summary>
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(3);

    private readonly Process _process;
    private readonly Task _errorDrain;
    private readonly CancellationTokenSource _completed = new();

    private ClipMediaWorkerProcess(Process process)
    {
        _process = process;
        _errorDrain = DrainAsync(process.StandardError);
    }

    /// <summary>Starts the worker from the engine image beside the App.</summary>
    public static ClipMediaWorkerProcess Start()
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Encore.Engine.exe"))
            {
                Arguments = "clip-media-worker",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        return new ClipMediaWorkerProcess(process);
    }

    /// <summary>Sends the request and returns the worker's result; cancelling closes its input, then kills it if it lingers.</summary>
    public async Task<ClipMediaResponse> RunAsync(ClipMediaRequest request, IProgress<double>? progress, CancellationToken token)
    {
        using var stop = token.Register(StopOnCancel);
        try
        {
            _process.PriorityClass = ProcessPriorityClass.BelowNormal;
            await WriteAsync(request, token).ConfigureAwait(false);
            var result = await ReadResultAsync(progress).ConfigureAwait(false);
            await _process.WaitForExitAsync().ConfigureAwait(false);
            await _errorDrain.ConfigureAwait(false);
            if (result?.Output is null)
            {
                token.ThrowIfCancellationRequested();
            }

            if (result is null || result.Kind == "error" || _process.ExitCode != 0)
            {
                throw new IOException(result?.Error ?? "The media worker stopped unexpectedly.");
            }

            return result;
        }
        finally
        {
            await EndAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _completed.Dispose();
        _process.Dispose();
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer).ConfigureAwait(false) != 0)
        {
        }
    }

    private async Task WriteAsync(ClipMediaRequest request, CancellationToken token)
    {
        var json = JsonSerializer.Serialize(request, ClipMediaJsonContext.Default.ClipMediaRequest);
        await _process.StandardInput.WriteLineAsync(json).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(token).ConfigureAwait(false);
    }

    // Reads the worker's lines to the end: progress lines are reported, and the last other line is the result.
    private async Task<ClipMediaResponse?> ReadResultAsync(IProgress<double>? progress)
    {
        ClipMediaResponse? result = null;
        while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.Length > MaxLineLength)
            {
                throw new IOException("The media worker returned too much data.");
            }

            var response = JsonSerializer.Deserialize(line, ClipMediaJsonContext.Default.ClipMediaResponse)
                ?? throw new IOException("The media worker returned an invalid response.");
            if (response.Kind == "progress")
            {
                progress?.Report(response.Progress);
            }
            else
            {
                result = response;
            }
        }

        return result;
    }

    // The caller cancelled: closing the input asks the worker to stop, and a worker that doesn't is killed.
    private void StopOnCancel()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (InvalidOperationException)
        {
        }

        _ = KillIfUnresponsiveAsync();
    }

    private async Task KillIfUnresponsiveAsync()
    {
        try
        {
            await Task.Delay(ExitGrace, _completed.Token).ConfigureAwait(false);
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    // Closes the input and waits for the exit; the whole tree belongs to this run, so no live service process is touched.
    private async Task EndAsync()
    {
        _process.StandardInput.Close();
        if (!_process.HasExited)
        {
            using var grace = new CancellationTokenSource(ExitGrace);
            try
            {
                await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }

        await _errorDrain.ConfigureAwait(false);
        _completed.Cancel();
    }
}
