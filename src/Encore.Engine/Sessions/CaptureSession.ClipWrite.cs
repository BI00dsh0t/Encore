using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Core.Sessions;
using Encore.Platform.FFmpeg;

namespace Encore.Engine.Sessions;

internal sealed partial class CaptureSession
{
    /// <summary>One save's write, bound to one encoder chain so it never mixes two chains' PTS. See docs/code-notes.md.</summary>
    private sealed class ClipWrite(CaptureSession session, EncoderChain chain, VideoEncoder? video, long qpc, int windowSeconds) : IClipWrite
    {
        private EncoderChain _chain = chain;
        private VideoEncoder? _video = video;
        private int _windowSeconds = windowSeconds;

        // Where the written Clip ends, in µs since the anchor; set before done is called.
        private long _endUs = long.MinValue;

        // The keyframe request's ticket, taken in Write before the writer thread starts.
        private long _ticket;

        public SessionSpec? SwitchesTo { get; private set; }

        /// <summary>The session's next Clip starts at this one's end: the keyframe it stopped before.</summary>
        public void Reset()
        {
            var end = Volatile.Read(ref _endUs);
            if (end != long.MinValue)
            {
                session.ResetTo(end);
                session.ResizeAt(end);
            }
        }

        public void Write(string path, Action<Exception?> done)
        {
            ArgumentNullException.ThrowIfNull(path);
            ArgumentNullException.ThrowIfNull(done);
            lock (session._chainLock)
            {
                // Replaced and torn down since Save: the press cuts from the chain that replaced it.
                if (_chain.RetireStarted && session._current is { } current && !ReferenceEquals(current, _chain))
                {
                    _chain = current;
                    _video = session._outputFinished ? null : current.Video;
                    _windowSeconds = current.WindowSeconds;
                }

                _chain.Writers++;
            }

            var bound = _chain;
            var ring = bound.Ring!;
            if (!ring.Lease())
            {
                session.EndWriter(bound, null);
                throw new InvalidOperationException("Rewind has already been released.");
            }

            try
            {
                // A switch is armed first, so the keyframe is asked for at the instant the new chain takes frames from.
                var armed = session.TryArm(bound, qpc, out var armQpc);
                SwitchesTo = armed?.Spec;

                // Only a write that starts asks for its keyframe; starting the thread publishes the ticket to it.
                if (_video is not null)
                {
                    _ticket = _video.RequestKeyframe(armed is null ? qpc : armQpc);
                }

                new Thread(() => done(Run(path))) { Name = "ClipWriter", IsBackground = true }.Start();
            }
            catch
            {
                ring.Release();
                session.EndWriter(bound, SwitchesTo is null ? null : session.MicrosAt(qpc));
                throw;
            }
        }

        private Exception? Run(string path)
        {
            var chain = _chain;
            var ring = chain.Ring!;
            var progress = new WriteProgress();
            try
            {
                CutAndWrite(chain, ring, path, progress);
                return null;
            }
            catch (Exception e)
            {
                // Give the span back: a later write starts at the last end still claimed.
                if (progress.Claimed is { } toUs)
                {
                    session._claim.Failed(toUs);
                }

                DeleteFailed(path);
                return e;
            }
            finally
            {
                if (progress.Leased)
                {
                    ring.Release();
                }

                if (progress.Registered)
                {
                    EndRegistration(chain, progress);
                }
            }
        }

        /// <summary>Waits for the Clip's end and for every stream to pass it, then cuts the span and writes the file.</summary>
        private void CutAndWrite(EncoderChain chain, ReplayRingSink ring, string path, WriteProgress progress)
        {
            var log = session._log;
            var pressUs = session.MicrosAt(qpc);
            var toUs = WaitForKey(ring, pressUs);
            progress.End = toUs;

            // Claimed as soon as the end is known: a later write starts here, not back over this one.
            var previous = session._claim.Claim(toUs);
            progress.Claimed = toUs;
            WaitForStreams(ring, toUs);
            var fromUs = StartOf(toUs, previous);
            using var source = ring.Cut(chain.SnapStart(fromUs), toUs);
            ring.Release();
            progress.Leased = false;

            // The cut holds its own pins and parameters: the chain's encoders are no longer needed.
            EndRegistration(chain, progress);
            if (source is null)
            {
                throw new ErrorCodeException(
                    ErrorCodes.ClipTooShort,
                    "There's nothing to save yet: Encore has kept less than 1 s of gameplay since the game started or the last save.");
            }

            if (session._faults.ClipWrite(path) is { } fault)
            {
                throw fault;
            }

            ClipWriter.Write(source, path, log);

            // The cut's end: the press's keyframe, or a ring generation's boundary when the cut came from the one before it.
            var endUs = source.Cut.EndUs;
            session._claim.Succeeded(toUs, endUs);
            progress.Claimed = null;
            Volatile.Write(ref _endUs, endUs);
            log.LogInformation(
                "Clip of {Seconds:0.000} s, {Start:0.000} s to {End:0.000} s after the anchor ({Packets} packets), written to {Path}",
                (source.Cut.EndUs - source.Cut.StartUs) / 1e6, source.Cut.StartUs / 1e6, source.Cut.EndUs / 1e6, source.Cut.Packets.Count, path);
        }

        /// <summary>Waits until each stream is an AAC frame past the key: every packet centred before it is then in the ring.</summary>
        private void WaitForStreams(ReplayRingSink ring, long toUs)
        {
            var deadline = Deadline();
            while (!ring.AllStreamsPast(toUs + AacFrameUs) && !session._outputFinished && Stopwatch.GetTimestamp() < deadline)
            {
                Thread.Sleep(10);
            }
        }

        /// <summary>Where the cut starts: the window back from its end, no earlier than the last Reset or an earlier write's claim.</summary>
        private long StartOf(long toUs, long previous)
        {
            var windowUs = _windowSeconds * 1_000_000L;

            // The window back from the Clip's end, the key after the press, so the Clip never exceeds it.
            var ownFromUs = Math.Max(toUs - windowUs, Volatile.Read(ref session._resetUs));
            var fromUs = Math.Max(ownFromUs, previous);

            // An empty cut, whatever set its start, makes no Clip.
            if (toUs <= fromUs)
            {
                throw new ErrorCodeException(ErrorCodes.ClipTooShort, "The cut is empty: its start is at or after its end.");
            }

            // An earlier write still in flight has claimed all but under 1 s of the span.
            if (previous > ownFromUs && toUs - fromUs < 1_000_000)
            {
                throw new ErrorCodeException(ErrorCodes.ClipTooShort, "An earlier Clip still being written leaves less than 1 s to save.");
            }

            return fromUs;
        }

        /// <summary>The chain may retire once its last save has cut; the switching save first records where its Clip ends.</summary>
        private void EndRegistration(EncoderChain chain, WriteProgress progress)
        {
            progress.Registered = false;
            session.EndWriter(chain, SwitchesTo is null ? null : progress.End ?? session.MicrosAt(qpc));
        }

        /// <summary>A failed write leaves no file behind: neither the part nor the reserved one.</summary>
        private void DeleteFailed(string path)
        {
            foreach (var file in new[] { path + ClipWriter.PartExtension, path })
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception deleteFailure) when (deleteFailure is IOException or UnauthorizedAccessException)
                {
                    session._log.LogWarning("Could not delete {Path}: {Message}", file, deleteFailure.Message);
                }
            }
        }

        /// <summary>The forced keyframe's time in µs since the anchor; the press itself when it takes too long.</summary>
        private long WaitForKey(ReplayRingSink ring, long pressUs)
        {
            if (_video is not { } video)
            {
                // Drained: the ring already holds everything it will.
                return pressUs;
            }

            var deadline = Deadline();
            while (true)
            {
                if (video.TryGetForcedKey(_ticket, out var pts))
                {
                    return ring.VideoMicros(pts);
                }

                if (session._outputFinished || Stopwatch.GetTimestamp() >= deadline)
                {
                    session._log.LogInformation("No keyframe was encoded after the press within {Seconds:0.0} s; the Clip ends at the press", SaveWait.TotalSeconds);
                    return pressUs;
                }

                Thread.Sleep(5);
            }
        }

        private long Deadline() => Stopwatch.GetTimestamp() + (long)(SaveWait.TotalSeconds * session._qpcFrequency);

        /// <summary>How far one write has got, so its end gives back only what it still holds.</summary>
        private sealed class WriteProgress
        {
            /// <summary>Whether the write still holds its lease on the ring.</summary>
            public bool Leased { get; set; } = true;

            /// <summary>Whether the write is still registered on its chain.</summary>
            public bool Registered { get; set; } = true;

            /// <summary>The end the write has claimed and not yet settled; null for none.</summary>
            public long? Claimed { get; set; }

            /// <summary>Where the Clip ends, once that is known.</summary>
            public long? End { get; set; }
        }
    }
}
