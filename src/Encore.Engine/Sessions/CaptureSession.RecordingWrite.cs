using Microsoft.Extensions.Logging;
using Encore.Core.Sessions;
using Encore.Platform.FFmpeg;

namespace Encore.Engine.Sessions;

internal sealed partial class CaptureSession
{
    /// <summary>One Recording: Start attaches a writer to the tap and asks for a keyframe; the writer's thread does the rest.</summary>
    private sealed class RecordingWrite(CaptureSession session, EncoderChain chain, RecordingTap tap, VideoEncoder video, long qpc)
        : IRecordingWrite
    {
        private RecordingWriter? _writer;

        public void Start(string path, Action<Exception?> done)
        {
            ArgumentNullException.ThrowIfNull(path);
            ArgumentNullException.ThrowIfNull(done);
            if (_writer is not null)
            {
                throw new InvalidOperationException("The Recording has already started.");
            }

            // Set before the writer thread starts, which publishes it to the lambda.
            long ticket = 0;
            var writer = NewWriter(path, () => video.TryGetForcedKey(ticket, out var pts) ? pts : null, done);
            try
            {
                Attach(writer);
                ticket = video.RequestKeyframe(qpc);
                writer.Start();
            }
            catch
            {
                writer.Abandon();
                throw;
            }

            _writer = writer;
            session._log.LogInformation("Recording to {Path}", path);
        }

        /// <summary>Makes the writer over its own copies of the tap's streams, which a failure frees.</summary>
        private RecordingWriter NewWriter(string path, Func<long?> forcedKey, Action<Exception?> done)
        {
            var streams = tap.CopyStreams();
            try
            {
                return new RecordingWriter(
                    path,
                    streams,
                    forcedKey,
                    session.MicrosAt(qpc),
                    session._loggers.CreateLogger<RecordingWriter>(),
                    done);
            }
            catch
            {
                streams.Dispose();
                throw;
            }
        }

        /// <summary>Attaches the writer to the chain Record reserved, under the chain lock, so a size move can't switch it out.</summary>
        private void Attach(RecordingWriter writer)
        {
            lock (session._chainLock)
            {
                session._recordReservedUntil = 0;
                if (!ReferenceEquals(session._current, chain) || chain.Retiring || chain.IsDisposed)
                {
                    throw new InvalidOperationException(
                        "The game's video changed size just as the Recording started; press Record again.");
                }

                // Attached before the keyframe is asked for, so the forced key can't pass the tap unseen.
                tap.Attach(writer);
            }
        }

        public void Stop(long qpc) => _writer?.Stop(session.MicrosAt(qpc));
    }
}
