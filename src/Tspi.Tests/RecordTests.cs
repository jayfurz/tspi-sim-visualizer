using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tspi.Core.IO;
using Tspi.Sim.Live;
using Xunit;

namespace Tspi.Tests;

/// <summary>
/// `tspi record` tests: a real WebSocket producer on a loopback port feeding the real
/// recorder, then the written file read back through TspiReader.
///
/// The producer is hand-rolled (TcpListener + RFC6455 handshake + unmasked server
/// frames) rather than mocked, so these exercise the actual wire path — the same bytes
/// tools/live-stream/cpp emits — and stay honest about framing.
/// </summary>
public sealed class RecordTests
{
    // ------------------------------------------------------------ test producer

    private sealed class FakeProducer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly TaskCompletionSource<NetworkStream> _connected = new();
        private NetworkStream? _stream;

        public FakeProducer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public string Url => $"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/stream";

        private async Task AcceptAsync()
        {
            var client = await _listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            // Minimal RFC6455 handshake: echo the accept key, ignore everything else.
            var buf = new byte[4096];
            int n = await stream.ReadAsync(buf);
            string request = Encoding.ASCII.GetString(buf, 0, n);
            string key = request.Split("\r\n")
                .First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                .Split(':')[1].Trim();
            string accept = Convert.ToBase64String(SHA1.HashData(
                Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            byte[] response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + accept + "\r\n\r\n");
            await stream.WriteAsync(response);
            _stream = stream;
            _connected.SetResult(stream);
        }

        private NetworkStream Stream() => _stream ?? _connected.Task.GetAwaiter().GetResult();

        public void SendText(string json) => Send(0x1, Encoding.UTF8.GetBytes(json));

        public void SendBatch(params (uint ord, uint index, TspiRecord rec)[] items)
        {
            var payload = new byte[4 + items.Length * 72];
            MemoryMarshal.Write(payload.AsSpan(0, 4), (uint)items.Length);
            for (int k = 0; k < items.Length; k++)
            {
                int b = 4 + k * 72;
                MemoryMarshal.Write(payload.AsSpan(b, 4), items[k].ord);
                MemoryMarshal.Write(payload.AsSpan(b + 4, 4), items[k].index);
                MemoryMarshal.Write(payload.AsSpan(b + 8, 64), in items[k].rec);
            }
            Send(0x2, payload);
        }

        private void Send(byte opcode, byte[] payload)
        {
            var s = Stream();
            var head = new List<byte> { (byte)(0x80 | opcode) };
            if (payload.Length < 126) head.Add((byte)payload.Length);
            else if (payload.Length < 65536)
            {
                head.Add(126);
                head.Add((byte)(payload.Length >> 8));
                head.Add((byte)(payload.Length & 0xFF));
            }
            else
            {
                head.Add(127);
                for (int i = 7; i >= 0; i--) head.Add((byte)((long)payload.Length >> (i * 8)));
            }
            try
            {
                lock (s)
                {
                    s.Write(head.ToArray());
                    s.Write(payload);
                    s.Flush();
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
            {
                // The recorder hung up (duration reached, Ctrl-C). A real producer keeps
                // running when a subscriber leaves, so the fake does too.
            }
        }

        public void Close()
        {
            try { _stream?.Dispose(); } catch (IOException) { }
        }

        public void Dispose()
        {
            Close();
            _listener.Stop();
        }
    }

    // ------------------------------------------------------------ helpers

    private const long DtNs = 10_000_000;   // 100 Hz

    private static string Hello(string entities, long dtNs = DtNs) =>
        "{\"type\":\"hello\",\"protocol\":1,\"name\":\"unit test\",\"dt_ns\":" + dtNs +
        ",\"epoch_unix_ns\":\"1750000000000000000\"," +
        "\"origin\":{\"lat_deg\":36.2,\"lon_deg\":-115.0,\"alt_m\":700}," +
        "\"dynamics\":\"test producer\",\"entities\":[" + entities + "]}";

    private static string Entity(uint ord, string id, string type = "aircraft",
        long t0Ns = 0, int? parent = null) =>
        "{\"ord\":" + ord + ",\"id\":\"" + id + "\",\"team\":\"blue\",\"type\":\"" + type +
        "\",\"model\":\"test\",\"parent\":" + (parent?.ToString() ?? "null") +
        ",\"t0_ns\":" + t0Ns + ",\"layout\":1}";

    /// <summary>A record whose fields all encode the sample index, so mix-ups are obvious.</summary>
    private static TspiRecord Rec(int i) => new()
    {
        PosN = 100.0 + i, PosE = 200.0 + i, PosD = -300.0 - i,
        VelN = 10f + i, VelE = 20f + i, VelD = -1f,
        QuatW = 1f, QuatX = 0f, QuatY = 0f, QuatZ = 0f,
        OmegaX = 0.1f, OmegaY = 0.2f, OmegaZ = 0.3f,
    };

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "tspi-record-test-" + Guid.NewGuid().ToString("N") + ".tspi");

    private static LiveRecordResult Record(FakeProducer producer, string outPath,
        Action<FakeProducer> script, double? durationSec = null)
    {
        var task = LiveRecorder.RecordAsync(new LiveRecordOptions
        {
            Url = producer.Url,
            OutPath = outPath,
            DurationSec = durationSec,
        });
        script(producer);
        return task.GetAwaiter().GetResult();
    }

    // ------------------------------------------------------------ tests

    [Fact]
    public void RecordsAStreamIntoAReadableFile()
    {
        string outPath = TempPath();
        using var producer = new FakeProducer();
        var result = Record(producer, outPath, p =>
        {
            p.SendText(Hello(Entity(0, "blue-01") + "," + Entity(1, "red-01", "aircraft")));
            for (int i = 0; i < 50; i++)
                p.SendBatch((0u, (uint)i, Rec(i)), (1u, (uint)i, Rec(i + 1000)));
            // A munition spawns mid-run and is announced late, as a real launch is.
            p.SendText("{\"type\":\"entity\",\"entity\":" + Entity(2, "sam-1", "munition", 50 * DtNs, 0) + "}");
            for (int i = 0; i < 20; i++)
                p.SendBatch((2u, (uint)i, Rec(i + 2000)));
            p.SendText("{\"type\":\"event\",\"t_ns\":" + 50 * DtNs +
                ",\"kind\":\"launch\",\"src\":0,\"dst\":1,\"data\":{\"miss_m\":2.5}}");
            p.SendText("{\"type\":\"end\"}");
        });

        Assert.Equal(3, result.Entities);
        Assert.Equal(120, result.Samples);
        Assert.Equal(0, result.GapsFilled);
        Assert.True(result.EndedByProducer);

        using var r = TspiReader.Open(outPath);
        Assert.Equal((ulong)DtNs, r.Header.DtNs);
        Assert.Equal(1750000000000000000L, r.Header.EpochUnixNs);
        Assert.Equal(36.2, r.Header.OriginLatDeg, 9);
        Assert.Equal(700.0, r.Header.OriginAltM, 9);
        Assert.Equal(3, r.Entities.Count);

        var blue = r.FindEntity("blue-01");
        Assert.Equal(50, blue.SampleCount);
        Assert.Equal(0, blue.T0Ns);
        for (int i = 0; i < 50; i++)
        {
            var got = r.ReadSample(blue, i);
            Assert.Equal(100.0 + i, got.PosN, 9);
            Assert.Equal(-300.0 - i, got.PosD, 9);
            Assert.Equal(10f + i, got.VelN);
        }

        // The late munition keeps its declared t0 and its parent link.
        var sam = r.FindEntity("sam-1");
        Assert.Equal(20, sam.SampleCount);
        Assert.Equal(50 * DtNs, sam.T0Ns);
        Assert.Equal(0u, sam.ParentOrd);
        Assert.Equal("munition", sam.Type);
        Assert.Equal(2000.0 + 100.0, r.ReadSample(sam, 0).PosN, 9);

        var ev = Assert.Single(r.Events);
        Assert.Equal("launch", ev.Kind);
        Assert.Equal(0u, ev.SrcOrd);
        Assert.Equal(1u, ev.DstOrd);
        Assert.Equal(2.5, Convert.ToDouble(ev.Data["miss_m"]), 9);

        // Provenance says a recording produced this, and carries the producer's own
        // dynamics tag rather than claiming this toolchain simulated it.
        var prov = Assert.Single(r.Footer.Provenance);
        Assert.Equal("record", prov["op"]);
        Assert.Equal("test producer", prov["dynamics"]);
        Assert.Equal(producer.Url, prov["source"]);
        File.Delete(outPath);
    }

    [Fact]
    public void PadsDroppedFramesAndCountsThem()
    {
        string outPath = TempPath();
        using var producer = new FakeProducer();
        var result = Record(producer, outPath, p =>
        {
            p.SendText(Hello(Entity(0, "blue-01")));
            p.SendBatch((0u, 0u, Rec(0)));
            p.SendBatch((0u, 1u, Rec(1)));
            p.SendBatch((0u, 5u, Rec(5)));      // frames 2..4 lost on the wire
            p.SendText("{\"type\":\"end\"}");
        });

        Assert.Equal(3, result.GapsFilled);
        using var r = TspiReader.Open(outPath);
        var e = r.FindEntity("blue-01");
        // t = t0 + i*dt must stay exact, so the hole is filled by repeating sample 1.
        Assert.Equal(6, e.SampleCount);
        for (long i = 2; i <= 4; i++) Assert.Equal(101.0, r.ReadSample(e, i).PosN, 9);
        Assert.Equal(105.0, r.ReadSample(e, 5).PosN, 9);
        File.Delete(outPath);
    }

    [Fact]
    public void RebasesWhenJoiningARunAlreadyInProgress()
    {
        string outPath = TempPath();
        using var producer = new FakeProducer();
        var result = Record(producer, outPath, p =>
        {
            p.SendText(Hello(Entity(0, "blue-01")));
            for (uint i = 400; i < 410; i++) p.SendBatch((0u, i, Rec((int)i)));
            p.SendText("{\"type\":\"end\"}");
        });

        Assert.Equal(0, result.GapsFilled);       // a late join is not a gap
        using var r = TspiReader.Open(outPath);
        var e = r.FindEntity("blue-01");
        Assert.Equal(10, e.SampleCount);
        Assert.Equal(400 * DtNs, e.T0Ns);         // samples keep their true sim time
        Assert.Equal(4.0, r.StartSec(e), 9);
        Assert.Equal(500.0, r.ReadSample(e, 0).PosN, 9);
        File.Delete(outPath);
    }

    [Fact]
    public void DropsDuplicateStaleAndUnknownRecords()
    {
        string outPath = TempPath();
        using var producer = new FakeProducer();
        var result = Record(producer, outPath, p =>
        {
            p.SendText(Hello(Entity(0, "blue-01")));
            p.SendBatch((0u, 0u, Rec(0)));
            p.SendBatch((0u, 1u, Rec(1)));
            p.SendBatch((0u, 1u, Rec(999)));      // duplicate: first write wins
            p.SendBatch((0u, 0u, Rec(998)));      // stale
            p.SendBatch((7u, 0u, Rec(0)));        // never announced
            p.SendText("{\"type\":\"end\"}");
        });

        Assert.Equal(2, result.Samples);
        Assert.Equal(1, result.RecordsDropped);   // only the unknown ord is a "drop"
        using var r = TspiReader.Open(outPath);
        var e = r.FindEntity("blue-01");
        Assert.Equal(2, e.SampleCount);
        Assert.Equal(101.0, r.ReadSample(e, 1).PosN, 9);
        File.Delete(outPath);
    }

    [Fact]
    public void EnforcesQuaternionSignContinuity()
    {
        string outPath = TempPath();
        using var producer = new FakeProducer();
        var result = Record(producer, outPath, p =>
        {
            p.SendText(Hello(Entity(0, "blue-01")));
            for (int i = 0; i < 8; i++)
            {
                var rec = Rec(i);
                // A producer is free to send q and -q alternately; the container is not.
                float s = i % 2 == 0 ? 1f : -1f;
                rec.QuatW = 0.7071f * s; rec.QuatX = 0.7071f * s;
                p.SendBatch((0u, (uint)i, rec));
            }
            p.SendText("{\"type\":\"end\"}");
        });

        Assert.Equal(4, result.QuatsSignFlipped);
        using var r = TspiReader.Open(outPath);
        var e = r.FindEntity("blue-01");
        for (long i = 1; i < e.SampleCount; i++)
        {
            var a = r.ReadSample(e, i - 1);
            var b = r.ReadSample(e, i);
            double dot = a.QuatW * b.QuatW + a.QuatX * b.QuatX + a.QuatY * b.QuatY + a.QuatZ * b.QuatZ;
            Assert.True(dot >= 0, $"samples {i - 1}..{i} are not sign-continuous (dot {dot})");
        }
        File.Delete(outPath);
    }

    [Fact]
    public void StopsAtTheRequestedDurationAndStillWritesTheFile()
    {
        string outPath = TempPath();
        using var producer = new FakeProducer();
        var result = Record(producer, outPath, p =>
        {
            p.SendText(Hello(Entity(0, "blue-01")));
            // Never sends "end": the producer is still running when we stop.
            for (uint i = 0; i < 40; i++)
            {
                p.SendBatch((0u, i, Rec((int)i)));
                Thread.Sleep(20);
            }
        }, durationSec: 0.35);

        Assert.False(result.EndedByProducer);
        Assert.Equal("duration reached", result.StopReason);
        Assert.True(result.Samples > 0, "expected some samples before the duration elapsed");
        using var r = TspiReader.Open(outPath);
        Assert.Equal(result.Samples, r.FindEntity("blue-01").SampleCount);
        File.Delete(outPath);
    }

    [Fact]
    public void RejectsAnUnsupportedProtocolVersion()
    {
        using var producer = new FakeProducer();
        var ex = Assert.Throws<LiveRecordError>(() => Record(producer, TempPath(), p =>
            p.SendText("{\"type\":\"hello\",\"protocol\":99,\"dt_ns\":1000000}")));
        Assert.Contains("unsupported live protocol 99", ex.Message);
    }

    [Fact]
    public void RejectsRecordsBeforeHello()
    {
        using var producer = new FakeProducer();
        var ex = Assert.Throws<LiveRecordError>(() => Record(producer, TempPath(), p =>
            p.SendBatch((0u, 0u, Rec(0)))));
        Assert.Contains("before hello", ex.Message);
    }

    [Fact]
    public void FailsLoudlyWhenNothingWasRecorded()
    {
        using var producer = new FakeProducer();
        var ex = Assert.Throws<LiveRecordError>(() => Record(producer, TempPath(), p =>
        {
            p.SendText(Hello(Entity(0, "blue-01")));
            p.SendText("{\"type\":\"end\"}");
        }));
        Assert.Contains("nothing to write", ex.Message);
    }

    [Fact]
    public void SurvivesAProducerThatDiesMidStream()
    {
        string outPath = TempPath();
        using var producer = new FakeProducer();
        var result = Record(producer, outPath, p =>
        {
            p.SendText(Hello(Entity(0, "blue-01")));
            for (uint i = 0; i < 10; i++) p.SendBatch((0u, i, Rec((int)i)));
            Thread.Sleep(100);
            p.Close();                    // link drops with no close frame and no "end"
        });

        Assert.False(result.EndedByProducer);
        Assert.Equal(10, result.Samples);
        using var r = TspiReader.Open(outPath);   // a complete, valid file all the same
        Assert.Equal(10, r.FindEntity("blue-01").SampleCount);
        File.Delete(outPath);
    }

    [Fact]
    public void LeavesNoSpoolDirectoryBehind()
    {
        string outPath = TempPath();
        string spool = Path.Combine(Path.GetTempPath(), "tspi-spool-" + Guid.NewGuid().ToString("N"));
        using var producer = new FakeProducer();
        LiveRecorder.RecordAsync(new LiveRecordOptions
        {
            Url = producer.Url, OutPath = outPath, SpoolDir = spool,
        }).ContinueWith(_ => { });
        producer.SendText(Hello(Entity(0, "blue-01")));
        for (uint i = 0; i < 5; i++) producer.SendBatch((0u, i, Rec((int)i)));
        producer.SendText("{\"type\":\"end\"}");

        // Give the recorder a moment to finish writing and sweep up.
        for (int i = 0; i < 100 && (!File.Exists(outPath) || Directory.Exists(spool)); i++) Thread.Sleep(50);
        Assert.True(File.Exists(outPath));
        Assert.False(Directory.Exists(spool), "spool directory should be swept after finishing");
        File.Delete(outPath);
    }

    [Fact]
    public void RejectsANonWebSocketUrl()
    {
        var ex = Assert.Throws<LiveRecordError>(() =>
            LiveRecorder.RecordAsync(new LiveRecordOptions { Url = "http://example.com", OutPath = TempPath() })
                .GetAwaiter().GetResult());
        Assert.Contains("must be ws:// or wss://", ex.Message);
    }
}
