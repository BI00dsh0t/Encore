using System.IO;

namespace Encore.Waveforms;

// Compiled into both the App and the worker, so they agree on the file layout without sharing an assembly.
internal static class ClipPeakFile
{
    internal const int Version = 1;
    internal const int HeaderSize = 32;
    internal static void WriteHeader(BinaryWriter writer, int track, long milliseconds, long count)
    {
        writer.Write(0x4B504252); // RBPK, little endian
        writer.Write(Version);
        writer.Write(track);
        writer.Write(0);
        writer.Write(milliseconds);
        writer.Write(count);
    }

    internal static void Validate(BinaryReader reader, int track, long milliseconds, long count)
    {
        if (count <= 0 || milliseconds <= 0 || reader.BaseStream.Length != checked(HeaderSize + count * 4)
            || reader.ReadInt32() != 0x4B504252 || reader.ReadInt32() != Version
            || reader.ReadInt32() != track || reader.ReadInt32() != 0
            || reader.ReadInt64() != milliseconds || reader.ReadInt64() != count)
        { throw new IOException("The waveform cache is incomplete or corrupt. Reopen the clip creator."); }
    }

    internal static float ReadPeak(BinaryReader reader)
    {
        var peak = reader.ReadSingle();
        if (!float.IsFinite(peak) || peak < 0 || peak > 1)
        { throw new IOException("The waveform cache is corrupt. Reopen the clip creator."); }
        return peak;
    }
}
