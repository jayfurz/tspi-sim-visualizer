using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Tspi.Core.IO;
using Tspi.Core.Math;
using Xunit;

namespace Tspi.Tests;

public class RobustnessTests : IDisposable
{
    private readonly string _dir;
    public RobustnessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tspi-robust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static TspiHeader Header() => new()
    {
        DtNs = 10_000_000, EpochUnixNs = 1_700_000_000_000_000_000L,
        OriginLatDeg = 34.9, OriginLonDeg = -117.8, OriginAltM = 700, ManifestSha256 = new byte[32],
    };

    private static TspiEntityBlock Block(uint ord, string id, int n)
    {
        var b = new TspiEntityBlock { Meta = new TspiEntityEntry { Ord = ord, Id = id, T0Ns = 0 } };
        for (int i = 0; i < n; i++)
            b.Records.Add(TspiRecord.From(new Vec3d(i, ord, -1000), new Vec3d(1, 0, 0), QuatD.Identity, Vec3d.Zero));
        return b;
    }

    /// <summary>
    /// Fuzz the torn-write recovery scanner: truncate the file at every byte offset and
    /// assert recovery never throws and always leaves the file either openable or empty.
    /// This is exactly the code (backward magic scan with chunk overlap) most prone to a
    /// boundary off-by-one.
    /// </summary>
    [Fact]
    public void RecoveryIsRobustAtEveryTruncationOffset()
    {
        string master = Path.Combine(_dir, "master.tspi");
        TspiFile.WriteNew(master, Header(), new[] { Block(0, "a", 8), Block(1, "b", 8) }, null, null);
        TspiFile.Append(master, new[] { Block(2, "c", 8) }, null,
            new Dictionary<string, object> { { "op", "append" } });
        byte[] full = File.ReadAllBytes(master);

        for (int cut = 1; cut < full.Length; cut++)
        {
            string victim = Path.Combine(_dir, "v.tspi");
            File.WriteAllBytes(victim, full[..(full.Length - cut)]);

            // Neither inspection nor recovery may ever throw, whatever the truncation.
            var report = TspiRecovery.Recover(victim);
            if (report.RecoveredLength > 0)
            {
                // A recovered file must open and list a coherent subset of entities.
                using var r = TspiReader.Open(victim);
                Assert.InRange(r.Entities.Count, 1, 3);
            }
            else
            {
                // Unrecoverable: the reader must refuse it rather than read garbage.
                Assert.ThrowsAny<Exception>(() => TspiReader.Open(victim));
            }
            File.Delete(victim);
        }
    }

    /// <summary>
    /// Forward compatibility: a file mixing a known layout-1 block with an unknown layout-2
    /// block (wider stride, same 64-byte prefix) must open, read the layout-1 entity
    /// normally, and skip the layout-2 entity gracefully rather than crashing.
    /// </summary>
    [Fact]
    public void UnknownLayoutIsSkippedNotFatal()
    {
        string path = Path.Combine(_dir, "layout2.tspi");
        const int layout2Stride = 96; // layout-1 64-byte prefix + 32 bytes of (future) covariance

        long b1DataOffset, b2DataOffset;
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            Header().WriteTo(fs);

            // Block 0: layout 1, 3 samples.
            b1DataOffset = WriteBlockHeader(fs, ord: 0, layout: 1, stride: 64, t0Ns: 0, count: 3);
            for (int i = 0; i < 3; i++)
                WriteRecordBytes(fs, TspiRecord.From(new Vec3d(i, 0, -1000), new Vec3d(1, 0, 0), QuatD.Identity, Vec3d.Zero), 64);

            // Block 1: layout 2, 2 samples (64-byte prefix + 32 pad each).
            b2DataOffset = WriteBlockHeader(fs, ord: 1, layout: 2, stride: layout2Stride, t0Ns: 0, count: 2);
            for (int i = 0; i < 2; i++)
                WriteRecordBytes(fs, TspiRecord.From(new Vec3d(100 + i, 0, -2000), new Vec3d(2, 0, 0), QuatD.Identity, Vec3d.Zero), layout2Stride);

            var footer = new TspiFooter();
            footer.Entities.Add(new TspiEntityEntry { Ord = 0, Id = "known", T0Ns = 0, SampleCount = 3, DataOffset = b1DataOffset, Stride = 64, Layout = 1 });
            footer.Entities.Add(new TspiEntityEntry { Ord = 1, Id = "future", T0Ns = 0, SampleCount = 2, DataOffset = b2DataOffset, Stride = layout2Stride, Layout = 2 });
            byte[] json = Encoding.UTF8.GetBytes(footer.ToJsonString());
            long footerOffset = fs.Position;
            fs.Write(json, 0, json.Length);
            WriteTrailer(fs, footerOffset, json.Length, Crc32.Compute(json, 0, json.Length));
        }

        using var r = TspiReader.Open(path);
        Assert.Equal(2, r.Entities.Count);

        var known = r.FindEntity("known")!;
        Assert.Equal(1, known.Layout);
        Assert.True(r.TrySampleAt(known, 0.0, out var s));
        Assert.Equal(0.0, s.PosNed.X, 6);

        var future = r.FindEntity("future")!;
        Assert.Equal(2, future.Layout);
        Assert.Equal(layout2Stride, future.Stride);
        // Unknown layout: sampling declines gracefully (no throw, returns false).
        Assert.False(r.TrySampleAt(future, 0.0, out _));
    }

    // ---- low-level writers (mirror the format; kept in the test to avoid exposing internals) ----

    private static long WriteBlockHeader(FileStream fs, uint ord, ushort layout, ushort stride, long t0Ns, ulong count)
    {
        var w = new BinaryWriter(fs);
        w.Write(TspiFormat.BlockMagic);
        w.Write(ord);
        w.Write(layout);
        w.Write(stride);
        w.Write(0u);
        w.Write(t0Ns);
        w.Write(count);
        w.Flush();
        return fs.Position;
    }

    private static void WriteRecordBytes(FileStream fs, TspiRecord rec, int stride)
    {
        Span<byte> buf = stackalloc byte[stride];
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref rec, 1)).CopyTo(buf[..64]);
        fs.Write(buf);
    }

    private static void WriteTrailer(FileStream fs, long footerOffset, long footerLen, uint crc)
    {
        var w = new BinaryWriter(fs);
        w.Write((ulong)footerOffset);
        w.Write((ulong)footerLen);
        w.Write(crc);
        w.Write(0u);
        w.Write(TspiFormat.TrailerMagic);
        w.Flush();
    }
}
