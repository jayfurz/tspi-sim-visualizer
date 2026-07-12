using System;
using System.Collections.Generic;
using System.IO;
using Tspi.Core.IO;
using Tspi.Core.Json;
using Tspi.Core.Math;
using Xunit;

namespace Tspi.Tests;

public class FormatTests : IDisposable
{
    private readonly string _dir;
    public FormatTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tspi-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static TspiHeader Header(double dt = 0.01) => new()
    {
        DtNs = (ulong)(dt * 1e9),
        EpochUnixNs = 1_700_000_000_000_000_000L,
        OriginLatDeg = 34.9061, OriginLonDeg = -117.8839, OriginAltM = 700,
        ManifestSha256 = new byte[32],
    };

    private static TspiEntityBlock Block(uint ord, string id, long t0Ns, int n, Func<int, Vec3d> pos)
    {
        var b = new TspiEntityBlock { Meta = new TspiEntityEntry { Ord = ord, Id = id, T0Ns = t0Ns } };
        for (int i = 0; i < n; i++)
            b.Records.Add(TspiRecord.From(pos(i), new Vec3d(1, 0, 0), QuatD.Identity, Vec3d.Zero));
        return b;
    }

    [Fact]
    public void WriteReadRoundTrip()
    {
        string path = Path.Combine(_dir, "rt.tspi");
        var block = Block(0, "e0", 0, 100, i => new Vec3d(i * 10.0, i * -2.0, -5000 + i));
        TspiFile.WriteNew(path, Header(), new[] { block }, Array.Empty<TspiEventEntry>(),
            new[] { new Dictionary<string, object> { { "sim_version", "test" } } });

        using var r = TspiReader.Open(path);
        Assert.Single(r.Entities);
        var e = r.Entities[0];
        Assert.Equal("e0", e.Id);
        Assert.Equal(100, e.SampleCount);
        var s50 = r.ReadSample(e, 50);
        Assert.Equal(500.0, s50.PosN, 9);
        Assert.Equal(-100.0, s50.PosE, 9);
        Assert.Equal(-4950.0, s50.PosD, 9);
    }

    [Fact]
    public void HermiteInterpolationHitsSamplesExactly()
    {
        string path = Path.Combine(_dir, "interp.tspi");
        // Constant velocity (1 m/s at dt=1 s, consistent with stored vel) => Hermite is exact everywhere.
        var block = Block(0, "e0", 0, 50, i => new Vec3d(i * 1.0, 0, 0));
        TspiFile.WriteNew(path, Header(1.0), new[] { block }, null, null);
        using var r = TspiReader.Open(path);
        var e = r.Entities[0];
        Assert.True(r.TrySampleAt(e, 2.5, out var mid)); // between samples 2 and 3
        Assert.Equal(2.5, mid.PosNed.X, 6);
        Assert.Equal(1.0, mid.VelNed.X, 6);
    }

    [Fact]
    public void SampleOutsideWindowFailsUnlessClamped()
    {
        string path = Path.Combine(_dir, "window.tspi");
        var block = Block(0, "e0", 5_000_000_000L, 10, i => new Vec3d(i, 0, 0)); // t0 = 5s
        TspiFile.WriteNew(path, Header(0.1), new[] { block }, null, null);
        using var r = TspiReader.Open(path);
        var e = r.Entities[0];
        Assert.False(r.TrySampleAt(e, 0.0, out _));
        Assert.True(r.TrySampleAt(e, 0.0, out var clamped, clamp: true));
        Assert.Equal(0.0, clamped.PosNed.X, 9); // clamped to first sample
    }

    [Fact]
    public void AppendPreservesOriginalBytesAndChainsFooters()
    {
        string path = Path.Combine(_dir, "append.tspi");
        var a = Block(0, "a", 0, 20, i => new Vec3d(i, 0, 0));
        TspiFile.WriteNew(path, Header(), new[] { a }, null, null);
        byte[] before = File.ReadAllBytes(path);
        long originalLen = before.Length;

        var b = Block(1, "b", 0, 30, i => new Vec3d(0, i, 0));
        TspiFile.Append(path, new[] { b }, null, new Dictionary<string, object> { { "op", "append" } });

        byte[] after = File.ReadAllBytes(path);
        // Original region byte-for-byte unchanged (append never rewrites old bytes).
        for (long i = 0; i < originalLen; i++) Assert.Equal(before[i], after[i]);

        using var r = TspiReader.Open(path);
        Assert.Equal(2, r.Entities.Count);
        Assert.NotNull(r.FindEntity("a"));
        Assert.NotNull(r.FindEntity("b"));
        var chain = r.ReadFooterChain();
        Assert.Equal(2, chain.Count);
        Assert.Single(chain[1].Entities); // pre-append snapshot had only "a"
    }

    [Fact]
    public void DuplicateOrdRejected()
    {
        string path = Path.Combine(_dir, "dup.tspi");
        var a = Block(0, "a", 0, 5, i => new Vec3d(i, 0, 0));
        var b = Block(0, "b", 0, 5, i => new Vec3d(i, 0, 0));
        Assert.Throws<InvalidOperationException>(() =>
            TspiFile.WriteNew(path, Header(), new[] { a, b }, null, null));
    }

    [Fact]
    public void TornAppendRecoversToPreviousState()
    {
        string path = Path.Combine(_dir, "torn.tspi");
        var a = Block(0, "a", 0, 20, i => new Vec3d(i, 0, 0));
        TspiFile.WriteNew(path, Header(), new[] { a }, null, null);
        var b = Block(1, "b", 0, 30, i => new Vec3d(0, i, 0));
        TspiFile.Append(path, new[] { b }, null, null);

        // Chop the tail to simulate a torn append.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
            fs.SetLength(fs.Length - 24);

        // Reader refuses the corrupted file.
        Assert.ThrowsAny<Exception>(() => TspiReader.Open(path));

        var report = TspiRecovery.Recover(path);
        Assert.True(report.Truncated);

        using var r = TspiReader.Open(path);
        Assert.Single(r.Entities); // rolled back to the pre-append state
        Assert.Equal("a", r.Entities[0].Id);
    }

    [Fact]
    public void Crc32MatchesKnownVector()
    {
        // zlib.crc32(b"123456789") == 0xCBF43926
        Assert.Equal(0xCBF43926u, Crc32.Compute(System.Text.Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public void MiniJsonRoundTripsFooterTypes()
    {
        var obj = new Dictionary<string, object>
        {
            { "s", "hi\n\"quote\"" },
            { "i", 42L },
            { "d", 3.5 },
            { "b", true },
            { "n", null! },
            { "arr", new List<object> { 1L, 2L, 3L } },
        };
        var parsed = (Dictionary<string, object>)MiniJson.Parse(MiniJson.Serialize(obj));
        Assert.Equal("hi\n\"quote\"", parsed["s"]);
        Assert.Equal(42L, parsed["i"]);
        Assert.Equal(3.5, (double)parsed["d"], 12);
        Assert.Equal(true, parsed["b"]);
        Assert.Null(parsed["n"]);
        Assert.Equal(3, ((List<object>)parsed["arr"]).Count);
    }
}
