using System.IO;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Waveforms;

namespace ReplayBuffer.App.Library;

/// <summary>Creator-owned, source-bound peak files. All validation and viewport IO runs off-thread.</summary>
internal sealed class ClipWaveformCache : IDisposable
{
    private readonly string _folder;
    private readonly ClipWaveformDescriptor _descriptor;
    private readonly object _gate = new();
    private bool _disposed;

    internal ClipWaveformCache(string folder, ClipWaveformDescriptor descriptor, ClipSource source, ClipMetadata metadata)
    {
        _folder = Path.GetFullPath(folder);
        _descriptor = descriptor;
        if (descriptor.Version != ClipPeakFile.Version || descriptor.Source != source || descriptor.Duration != metadata.Duration
            || !descriptor.Tracks.Select(t => t.StreamIndex).SequenceEqual(metadata.Tracks.Select(t => t.StreamIndex)))
        { throw new IOException("The waveform cache does not match this source. Reopen the clip creator."); }
        foreach (var track in descriptor.Tracks)
        {
            var count = checked((long)Math.Ceiling(metadata.Duration * 1000));
            long stride = 1;
            if (track.Levels.Length == 0) { throw new IOException("The waveform cache is incomplete."); }
            foreach (var level in track.Levels)
            {
                if (level.MillisecondsPerPeak != stride || level.Count != count)
                { throw new IOException("The waveform cache levels are inconsistent."); }
                using var reader = Open(track.StreamIndex, level);
                stride = checked(stride * 16);
                count = (count + 15) / 16;
            }
            if (track.Levels[^1].Count != 1) { throw new IOException("The waveform cache is incomplete."); }
        }
    }

    private BinaryReader Open(int track, ClipPeakLevel level)
    {
        if (level.FileName != Path.GetFileName(level.FileName) || string.IsNullOrWhiteSpace(level.FileName))
        { throw new IOException("The waveform cache path is invalid."); }
        var reader = new BinaryReader(File.OpenRead(Path.Combine(_folder, level.FileName)));
        try { ClipPeakFile.Validate(reader, track, level.MillisecondsPerPeak, level.Count); return reader; }
        catch { reader.Dispose(); throw; }
    }

    internal Task<ClipWaveform[]> ReadAsync(double start, double end, int[] tracks, int bins, CancellationToken token) => Task.Run(() =>
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            bins = Math.Clamp(bins, 1, 4096);
            var result = new List<ClipWaveform>();
            foreach (var track in _descriptor.Tracks.Where(t => tracks.Contains(t.StreamIndex)))
            {
                // Choose the finest level that keeps reads bounded by roughly sixteen peaks per output bin.
                var target = (end - start) * 1000 / bins;
                var level = track.Levels.LastOrDefault(l => l.MillisecondsPerPeak <= target) ?? track.Levels[0];
                var first = Math.Clamp((long)Math.Floor(start * 1000 / level.MillisecondsPerPeak), 0, level.Count - 1);
                var last = Math.Clamp((long)Math.Ceiling(end * 1000 / level.MillisecondsPerPeak), first + 1, level.Count);
                var outputCount = (int)Math.Min(bins, last - first);
                var peaks = new float[outputCount];
                using var reader = Open(track.StreamIndex, level);
                reader.BaseStream.Position = ClipPeakFile.HeaderSize + first * 4;
                for (var index = first; index < last; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var time = (index + 0.5) * level.MillisecondsPerPeak / 1000;
                    var bin = (int)Math.Clamp((time - start) / (end - start) * outputCount, 0, outputCount - 1);
                    peaks[bin] = Math.Max(peaks[bin], ClipPeakFile.ReadPeak(reader));
                }
                result.Add(new(start, end, track.StreamIndex, peaks));
            }
            return result.ToArray();
        }
    }, token);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            DeleteOwnedFolder(_folder);
        }
    }

    internal static void DeleteOwnedFolder(string folder)
    {
        try { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
