using System.Buffers.Binary;
using System.Text;
using ReplayBuffer.Core.Library;

namespace ReplayBuffer.Core.Tests;

/// <summary>The mehd patch's layout check; the patch itself is checked on a real Recording in the FFmpeg tests.</summary>
public sealed class Mp4FileTests
{
    [Fact]
    public void AHeaderWhoseMvexIsNotFollowedByAUdtaPlansNoPatch()
    {
        // mvhd v0: version and flags, creation, modification, timescale 1000, duration 0.
        var mvhd = Box("mvhd", [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x03, 0xE8, 0, 0, 0, 0]);
        var mvex = Box("mvex", Box("trex", new byte[24]));
        byte[] Header(byte[] afterMvex) =>
            [.. Box("ftyp", [.. "isom"u8, 0, 0, 2, 0]), .. Box("moov", [.. mvhd, .. mvex, .. afterMvex])];

        Assert.Null(Mp4File.PlanFragmentDuration(Header(Box("trak", [])), 2_000_000));

        // The same header with a udta there does get one.
        Assert.NotNull(Mp4File.PlanFragmentDuration(Header(Box("udta", new byte[40])), 2_000_000));
    }

    private static byte[] Box(string type, byte[] body)
    {
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type, box.AsSpan(4));
        body.CopyTo(box, 8);
        return box;
    }
}
