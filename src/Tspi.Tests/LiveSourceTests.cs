using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tspi.Core.IO;
using Tspi.Core.Live;
using Xunit;

namespace Tspi.Tests;

/// <summary>
/// LiveTspiSource tests: the same file, once through TspiReader and once through the
/// wire encoding into the live source, must sample *identically*. That equivalence is
/// the whole justification for streaming the container's own records — if it ever
/// stops holding, a live view and its recording disagree.
///
/// Mirrors web/viewer/tests/live.test.mjs so the C# and JS consumers stay in lockstep.
/// </summary>
public sealed class LiveSourceTests
{
    private static string GoldenPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "schemas")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "tools", "tspi_py", "tests", "data", "golden-v1.tspi");
    }

    private static string EntityJson(TspiEntityEntry e) =>
        "{\"ord\":" + e.Ord + ",\"id\":\"" + e.Id + "\",\"team\":\"" + e.Team +
        "\",\"type\":\"" + e.Type + "\",\"model\":\"" + e.Model + "\",\"parent\":" +
        (e.ParentOrd.HasValue ? e.ParentOrd.Value.ToString() : "null") +
        ",\"t0_ns\":" + e.T0Ns + ",\"layout\":" + e.Layout + "}";

    private static string HelloJson(TspiReader r, IEnumerable<TspiEntityEntry> entities) =>
        "{\"type\":\"hello\",\"protocol\":1,\"name\":\"golden\",\"dt_ns\":" + r.Header.DtNs +
        ",\"epoch_unix_ns\":\"" + r.Header.EpochUnixNs + "\"," +
        "\"origin\":{\"lat_deg\":" + r.Header.OriginLatDeg.ToString("R") +
        ",\"lon_deg\":" + r.Header.OriginLonDeg.ToString("R") +
        ",\"alt_m\":" + r.Header.OriginAltM.ToString("R") + "}," +
        "\"dynamics\":\"golden replay\",\"entities\":[" +
        string.Join(",", entities.Select(EntityJson)) + "]}";

    private static byte[] Batch(params (uint ord, uint index, TspiRecord rec)[] items)
    {
        var buf = new byte[4 + items.Length * 72];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)items.Length);
        for (int k = 0; k < items.Length; k++)
        {
            int b = 4 + k * 72;
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(b), items[k].ord);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(b + 4), items[k].index);
            var r = items[k].rec;
            var s = buf.AsSpan(b + 8);
            BinaryPrimitives.WriteInt64LittleEndian(s, BitConverter.DoubleToInt64Bits(r.PosN));
            BinaryPrimitives.WriteInt64LittleEndian(s.Slice(8), BitConverter.DoubleToInt64Bits(r.PosE));
            BinaryPrimitives.WriteInt64LittleEndian(s.Slice(16), BitConverter.DoubleToInt64Bits(r.PosD));
            WriteF(s.Slice(24), r.VelN); WriteF(s.Slice(28), r.VelE); WriteF(s.Slice(32), r.VelD);
            WriteF(s.Slice(36), r.QuatW); WriteF(s.Slice(40), r.QuatX);
            WriteF(s.Slice(44), r.QuatY); WriteF(s.Slice(48), r.QuatZ);
            WriteF(s.Slice(52), r.OmegaX); WriteF(s.Slice(56), r.OmegaY); WriteF(s.Slice(60), r.OmegaZ);
        }
        return buf;
    }

    private static void WriteF(Span<byte> s, float v) =>
        BinaryPrimitives.WriteInt32LittleEndian(s, BitConverter.SingleToInt32Bits(v));

    /// <summary>Stream a whole file through the wire encoding, tick by tick, as a producer would.</summary>
    private static LiveTspiSource StreamWholeFile(TspiReader r, out List<TspiEntityEntry> sampleable)
    {
        sampleable = r.Entities.Where(e => e.Layout == TspiFormat.LayoutSixDofV1 && e.SampleCount > 0).ToList();
        double minT = sampleable.Min(e => r.StartSec(e));
        long FirstTick(TspiEntityEntry e) => (long)System.Math.Round((r.StartSec(e) - minT) / r.DtSec);

        var atT0 = sampleable.Where(e => FirstTick(e) == 0).ToList();
        var live = LiveTspiSource.FromHello(HelloJson(r, atT0));

        long ticks = sampleable.Max(e => FirstTick(e) + e.SampleCount);
        for (long n = 0; n < ticks; n++)
        {
            var items = new List<(uint, uint, TspiRecord)>();
            foreach (var e in sampleable)
            {
                long i = n - FirstTick(e);
                if (i < 0 || i >= e.SampleCount) continue;
                if (i == 0 && !atT0.Contains(e))
                    live.IngestText("{\"type\":\"entity\",\"entity\":" + EntityJson(e) + "}");
                items.Add((e.Ord, (uint)i, r.ReadSample(e, i)));
            }
            if (items.Count > 0) live.IngestBatch(Batch(items.ToArray()));
        }
        return live;
    }

    [Fact]
    public void SamplesIdenticallyToTheFileReader()
    {
        using var r = TspiReader.Open(GoldenPath());
        var live = StreamWholeFile(r, out var sampleable);

        Assert.Equal(sampleable.Count, live.Entities.Count);
        Assert.Equal(r.Header.DtNs, live.Header.DtNs);
        Assert.Equal(r.Header.EpochUnixNs, live.Header.EpochUnixNs);
        Assert.Equal(0, live.GapsFilled);
        Assert.Equal(0, live.Dropped);

        foreach (var fe in sampleable)
        {
            var le = live.FindEntity(fe.Id);
            Assert.NotNull(le);
            Assert.Equal(fe.SampleCount, le.SampleCount);
            Assert.Equal(fe.T0Ns, le.T0Ns);
            Assert.Equal(fe.ParentOrd, le.ParentOrd);

            // Raw records: the wire is an exact f64/f32 round trip.
            foreach (long i in new[] { 0L, 1L, fe.SampleCount / 2, fe.SampleCount - 1 }.Distinct())
            {
                var a = r.ReadSample(fe, i);
                var b = live.ReadSample(le, i);
                Assert.Equal(a.PosN, b.PosN);
                Assert.Equal(a.PosD, b.PosD);
                Assert.Equal(a.VelE, b.VelE);
                Assert.Equal(a.QuatW, b.QuatW);
                Assert.Equal(a.OmegaZ, b.OmegaZ);
            }

            // Interpolated poses: one shared TspiSampling implementation, so bit-equal.
            double t0 = r.StartSec(fe), t1 = r.EndSec(fe);
            for (int k = 0; k <= 97; k++)
            {
                double t = t0 + (t1 - t0) * k / 97.0;
                bool fileAlive = r.TrySampleAt(fe, t, out TspiState want);
                bool liveAlive = live.TrySampleAt(le, t, out TspiState got);
                Assert.Equal(fileAlive, liveAlive);
                if (!fileAlive) continue;
                Assert.Equal(want.PosNed.X, got.PosNed.X);
                Assert.Equal(want.PosNed.Y, got.PosNed.Y);
                Assert.Equal(want.PosNed.Z, got.PosNed.Z);
                Assert.Equal(want.VelNed.X, got.VelNed.X);
                Assert.Equal(want.AttBodyToNed.W, got.AttBodyToNed.W);
                Assert.Equal(want.AttBodyToNed.Z, got.AttBodyToNed.Z);
                Assert.Equal(want.OmegaBody.Y, got.OmegaBody.Y);
            }
        }
    }

    [Fact]
    public void AnnouncesLateEntitiesAndTracksTheGrowingSpan()
    {
        using var r = TspiReader.Open(GoldenPath());
        var live = StreamWholeFile(r, out var sampleable);
        live.TimeSpan(out double minSec, out double maxSec);

        Assert.Equal(sampleable.Min(e => r.StartSec(e)), minSec, 9);
        Assert.Equal(sampleable.Max(e => r.EndSec(e)), maxSec, 9);
        Assert.True(live.EntityGeneration >= sampleable.Count);
        Assert.True(live.IsLive);

        live.IngestText("{\"type\":\"end\"}");
        Assert.True(live.Ended);
        Assert.False(live.IsLive);
    }

    [Fact]
    public void DropsDuplicateStaleAndUnknownRecordsAndPadsGaps()
    {
        using var r = TspiReader.Open(GoldenPath());
        var e0 = r.Entities.First(e => e.Layout == TspiFormat.LayoutSixDofV1 && e.SampleCount > 8);
        var live = LiveTspiSource.FromHello(HelloJson(r, new[] { e0 }));
        var le = live.FindEntity(e0.Id);

        void Put(uint i) => live.IngestBatch(Batch((e0.Ord, i, r.ReadSample(e0, i))));

        Put(0); Put(1);
        Assert.Equal(2, le.SampleCount);
        Put(1);                       // duplicate
        Put(0);                       // stale
        Assert.Equal(2, le.SampleCount);
        Put(4);                       // frames 2..3 lost
        Assert.Equal(5, le.SampleCount);
        Assert.Equal(2, live.GapsFilled);
        Assert.Equal(r.ReadSample(e0, 1).PosN, live.ReadSample(le, 2).PosN);   // padding repeats
        Assert.Equal(r.ReadSample(e0, 4).PosN, live.ReadSample(le, 4).PosN);

        live.IngestBatch(Batch((9999u, 0u, r.ReadSample(e0, 0))));
        Assert.Equal(1, live.Dropped);
        Assert.Equal(5, le.SampleCount);
    }

    [Fact]
    public void RebasesWhenJoiningARunAlreadyInProgress()
    {
        using var r = TspiReader.Open(GoldenPath());
        var e0 = r.Entities.First(e => e.Layout == TspiFormat.LayoutSixDofV1 && e.SampleCount > 20);
        const uint join = 11;
        var live = LiveTspiSource.FromHello(HelloJson(r, new[] { e0 }));
        var le = live.FindEntity(e0.Id);

        for (uint i = join; i < e0.SampleCount; i++)
            live.IngestBatch(Batch((e0.Ord, i, r.ReadSample(e0, i))));

        Assert.Equal(0, live.GapsFilled);                       // a late join is not a gap
        Assert.Equal(e0.SampleCount - join, le.SampleCount);
        Assert.Equal(r.StartSec(e0) + join * r.DtSec, live.StartSec(le), 9);
        Assert.Equal(r.EndSec(e0), live.EndSec(le), 9);
        Assert.Equal(r.ReadSample(e0, join).PosN, live.ReadSample(le, 0).PosN);

        // Absolute sim time is preserved, so poses still agree with the file over the
        // overlap (to well under a micron; rebasing changes the last ulp of u).
        double t0 = live.StartSec(le), t1 = live.EndSec(le);
        for (int k = 0; k <= 50; k++)
        {
            double t = t0 + (t1 - t0) * k / 50.0;
            Assert.True(r.TrySampleAt(e0, t, out TspiState want));
            Assert.True(live.TrySampleAt(le, t, out TspiState got));
            Assert.Equal(want.PosNed.X, got.PosNed.X, 9);
            Assert.Equal(want.PosNed.Z, got.PosNed.Z, 9);
        }
    }

    [Fact]
    public void ReadsEventsIncludingTheirDataPayload()
    {
        using var r = TspiReader.Open(GoldenPath());
        var live = LiveTspiSource.FromHello(HelloJson(r, r.Entities.Take(1)));
        var msg = live.IngestText(
            "{\"type\":\"event\",\"t_ns\":12500000000,\"kind\":\"intercept\",\"src\":0,\"dst\":1," +
            "\"data\":{\"miss_m\":3.25}}");

        Assert.Equal(LiveMessageKind.Event, msg.Kind);
        var ev = Assert.Single(live.Events);
        Assert.Equal("intercept", ev.Kind);
        Assert.Equal(12500000000L, ev.TNs);
        Assert.Equal(0u, ev.SrcOrd);
        Assert.Equal(1u, ev.DstOrd);
        Assert.Equal(3.25, Convert.ToDouble(ev.Data["miss_m"]), 9);
    }

    [Fact]
    public void IgnoresMessagesFromANewerProducerRatherThanFailing()
    {
        using var r = TspiReader.Open(GoldenPath());
        var live = LiveTspiSource.FromHello(HelloJson(r, r.Entities.Take(1)));
        var msg = live.IngestText("{\"type\":\"weather\",\"wind_kt\":12}");
        Assert.Equal(LiveMessageKind.Unknown, msg.Kind);
    }

    [Theory]
    [InlineData("{\"type\":\"records\"}", "first message must be hello")]
    [InlineData("{\"type\":\"hello\",\"protocol\":7,\"dt_ns\":1000000}", "unsupported live protocol 7")]
    [InlineData("{\"type\":\"hello\",\"protocol\":1}", "missing dt_ns")]
    [InlineData("{\"type\":\"hello\",\"protocol\":1,\"dt_ns\":0}", "dt_ns must be positive")]
    [InlineData("not json at all", "malformed control message")]
    [InlineData("[1,2,3]", "control message must be a JSON object")]
    public void RejectsABadHello(string json, string expected)
    {
        var ex = Assert.Throws<LiveProtocolError>(() => LiveTspiSource.FromHello(json));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void RejectsATruncatedBatchFrame()
    {
        using var r = TspiReader.Open(GoldenPath());
        var e0 = r.Entities.First(e => e.SampleCount > 0);
        var live = LiveTspiSource.FromHello(HelloJson(r, new[] { e0 }));
        byte[] good = Batch((e0.Ord, 0u, r.ReadSample(e0, 0)));

        var ex = Assert.Throws<LiveProtocolError>(() => live.IngestBatch(good, 0, good.Length - 8));
        Assert.Contains("truncated record batch", ex.Message);
        Assert.Throws<LiveProtocolError>(() => live.IngestBatch(new byte[2]));
    }

    [Fact]
    public void AcceptsStringEncodedNumbersFromJavaScriptProducers()
    {
        // epoch_unix_ns must be a string on the wire: absolute ns overflow 2^53.
        var live = LiveTspiSource.FromHello(
            "{\"type\":\"hello\",\"protocol\":1,\"dt_ns\":\"20000000\"," +
            "\"epoch_unix_ns\":\"1787452800123456789\",\"entities\":[]}");
        Assert.Equal(20000000UL, live.Header.DtNs);
        Assert.Equal(1787452800123456789L, live.Header.EpochUnixNs);
    }
}
