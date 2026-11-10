using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Core.Library;
using Encore.Engine.Sessions;

namespace Encore.Engine.Tests;

/// <summary>The Recordings a dead engine left open: one is trimmed, one with no fragment is deleted, each is reported.</summary>
public sealed class InterruptedRecordingsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("rb-interrupted-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void ATornRecordingIsTrimmedAnEmptyOneDeletedAndAMissingOneStillReported()
    {
        var header = Concat(Box("ftyp", 16), Box("moov", 40));
        var fragment = Concat(Box("moof", 24), Box("mdat", 100));
        var torn = Path.Combine(_folder, "torn.mp4");
        File.WriteAllBytes(torn, Concat(header, fragment, Box("moof", 24), Box("mdat", 100)[..50]));
        var empty = Path.Combine(_folder, "empty.mp4");
        File.WriteAllBytes(empty, header);
        var missing = Path.Combine(_folder, "missing.mp4");

        var messages = InterruptedRecordings.Recover([torn, empty, missing], NullLogger.Instance);

        Assert.Equal(3, messages.Count);
        Assert.All(messages.Zip([torn, empty, missing]), m => Assert.Contains(m.Second, m.First, StringComparison.Ordinal));
        Assert.Equal(header.Length + fragment.Length, new FileInfo(torn).Length);
        Assert.True(Mp4File.IsComplete(torn));
        Assert.False(File.Exists(empty));
    }

    private static byte[] Box(string type, int size)
    {
        var box = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)size);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        return box;
    }

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];
}
