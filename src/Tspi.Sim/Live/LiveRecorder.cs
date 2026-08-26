using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tspi.Core.IO;

namespace Tspi.Sim.Live;

/// <summary>
/// Records a live stream (tools/live-stream/PROTOCOL.md) into a .tspi file.
///
/// A streaming producer is authoritative for dynamics and keeps no history — a viewer
/// that joins late sees only what follows. This sink is the other half: it subscribes
/// like any viewer and lands the run in the container, so a live engagement ends up
/// replayable, diffable (<c>tspi diff</c>), analysable (<c>tools/tspi_py</c>) and
/// appendable-to, exactly like a simulated run.
///
/// Nothing is re-simulated and nothing is re-interpolated: the wire already carries the
/// format's own 64-byte layout-1 records, so recording is a copy plus bookkeeping. The
/// consumer rules match <c>LiveTspiFile</c> in web/viewer/tspi.js — unknown ords
/// dropped, duplicate/stale indices dropped, gaps padded (counted, never hidden), and a
/// mid-stream join rebased so every sample keeps its true sim time.
///
/// Memory is bounded: records spool to one temp file per entity and are streamed into
/// their blocks at the end, so an hour-long 100-entity run costs a chunk buffer, not a
/// heap the size of the file.
/// </summary>
public static class LiveRecorder
{
    public const int Protocol = 1;
    private const int RecordSize = TspiFormat.StrideSixDofV1;   // 64
    private const int WireItemSize = 8 + RecordSize;            // ord + index + record

    public static async Task<LiveRecordResult> RecordAsync(
        LiveRecordOptions options, CancellationToken cancel = default, Action<string>? progress = null)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (!Uri.TryCreate(options.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "ws" && uri.Scheme != "wss"))
            throw new LiveRecordError("stream URL must be ws:// or wss://, got '" + options.Url + "'");
        if (string.IsNullOrWhiteSpace(options.OutPath))
            throw new LiveRecordError("output path is required");

        string spoolDir = options.SpoolDir ??
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.OutPath)) ?? ".",
                ".tspi-record-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(spoolDir);

        var session = new Session(options, spoolDir, progress);
        try
        {
            await session.RunAsync(uri, cancel).ConfigureAwait(false);
            return session.Finish();
        }
        finally
        {
            session.DisposeSpools();
            try { Directory.Delete(spoolDir, recursive: true); }
            catch (IOException) { /* best effort: the file is already written */ }
        }
    }

    // ---------------------------------------------------------------- session

    private sealed class Session
    {
        private readonly LiveRecordOptions _opt;
        private readonly string _spoolDir;
        private readonly Action<string>? _progress;
        private readonly Dictionary<uint, Spool> _byOrd = new();
        private readonly List<Spool> _order = new();
        private readonly List<TspiEventEntry> _events = new();
        private readonly List<string> _warnings = new();

        private TspiHeader? _header;
        private string _dynamics = "live stream (producer-authoritative)";
        private string _streamName = "";
        private long _samples, _gaps, _dropped;
        private bool _endedByProducer;
        private string _stopReason = "socket closed";
        private DateTime _lastProgress = DateTime.UtcNow;

        public Session(LiveRecordOptions opt, string spoolDir, Action<string>? progress)
        {
            _opt = opt;
            _spoolDir = spoolDir;
            _progress = progress;
        }

        public async Task RunAsync(Uri uri, CancellationToken cancel)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            if (_opt.DurationSec is { } d && d > 0)
                linked.CancelAfter(TimeSpan.FromSeconds(d));
            var ct = linked.Token;

            using var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try
            {
                await ws.ConnectAsync(uri, cancel).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                throw new LiveRecordError("could not connect to " + uri + ": " + ex.Message);
            }
            _progress?.Invoke("connected to " + uri);

            var buffer = new byte[1 << 16];
            using var message = new MemoryStream();
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    message.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _stopReason = "producer closed the connection";
                            return;
                        }
                        message.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        if (HandleText(message.GetBuffer().AsSpan(0, (int)message.Length))) return;
                    }
                    else
                    {
                        HandleBatch(message.GetBuffer().AsSpan(0, (int)message.Length));
                    }
                    ReportProgress(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Duration elapsed or Ctrl-C: a deliberate stop, not a failure. Everything
                // received so far is already spooled, so the file is written normally.
                _stopReason = cancel.IsCancellationRequested ? "interrupted" : "duration reached";
            }
            catch (WebSocketException ex)
            {
                _stopReason = "link lost: " + ex.Message;
                _warnings.Add("stream ended abnormally (" + ex.Message + "); recorded what arrived before that");
            }
            finally
            {
                if (ws.State == WebSocketState.Open)
                {
                    try
                    {
                        using var closeCt = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "recorded", closeCt.Token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception) { /* the file matters, the handshake does not */ }
                }
            }
        }

        /// <summary>Returns true when the producer said the run is over.</summary>
        private bool HandleText(ReadOnlySpan<byte> utf8)
        {
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(utf8.ToArray());
                root = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new LiveRecordError("malformed control message from producer: " + ex.Message);
            }
            if (root.ValueKind != JsonValueKind.Object)
                throw new LiveRecordError("control message must be a JSON object");

            string type = Str(root, "type") ?? "";
            switch (type)
            {
                case "hello":
                    if (_header != null) throw new LiveRecordError("duplicate hello from producer");
                    Hello(root, utf8);
                    return false;
                case "entity":
                    RequireHello();
                    AddEntity(root.TryGetProperty("entity", out var nested) ? nested : root);
                    return false;
                case "event":
                    RequireHello();
                    AddEvent(root);
                    return false;
                case "end":
                    _endedByProducer = true;
                    _stopReason = "producer sent end";
                    return true;
                default:
                    // Forward compatibility: a newer producer may send messages this
                    // build predates. Ignore them rather than abandoning the recording.
                    return false;
            }
        }

        private void RequireHello()
        {
            if (_header == null) throw new LiveRecordError("producer sent data before hello");
        }

        private void Hello(JsonElement root, ReadOnlySpan<byte> raw)
        {
            long protocol = Num(root, "protocol") is { } p ? (long)p : Protocol;
            if (protocol != Protocol)
                throw new LiveRecordError($"unsupported live protocol {protocol} (this build speaks {Protocol})");

            long dtNs = Int64(root, "dt_ns") ??
                throw new LiveRecordError("hello is missing dt_ns");
            if (dtNs <= 0) throw new LiveRecordError("hello dt_ns must be positive, got " + dtNs);

            double lat = 0, lon = 0, alt = 0;
            if (root.TryGetProperty("origin", out var origin) && origin.ValueKind == JsonValueKind.Object)
            {
                lat = Num(origin, "lat_deg") ?? 0;
                lon = Num(origin, "lon_deg") ?? 0;
                alt = Num(origin, "alt_m") ?? 0;
            }

            _streamName = Str(root, "name") ?? "";
            _dynamics = Str(root, "dynamics") ?? _dynamics;
            _header = new TspiHeader
            {
                DtNs = (ulong)dtNs,
                EpochUnixNs = Int64(root, "epoch_unix_ns") ?? 0,
                OriginLatDeg = lat,
                OriginLonDeg = lon,
                OriginAltM = alt,
                // No manifest exists for a live feed, so the slot carries the hash of the
                // hello that configured the stream — a stable id for "this stream setup".
                ManifestSha256 = SHA256.HashData(raw.ToArray()),
            };

            if (root.TryGetProperty("entities", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var e in list.EnumerateArray()) AddEntity(e);

            _progress?.Invoke($"recording {(_streamName.Length > 0 ? _streamName + " " : "")}" +
                $"at dt {dtNs / 1e6:0.###} ms, {_order.Count} entities announced");
        }

        private void AddEntity(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return;
            long? ordRaw = Int64(e, "ord");
            if (ordRaw is not { } ordL || ordL < 0 || ordL > uint.MaxValue)
            {
                _warnings.Add("entity announced without a usable ord; ignored");
                return;
            }
            uint ord = (uint)ordL;
            if (_byOrd.ContainsKey(ord)) return;   // re-announcement is harmless

            long layout = Int64(e, "layout") ?? TspiFormat.LayoutSixDofV1;
            if (layout != TspiFormat.LayoutSixDofV1)
            {
                _warnings.Add($"entity ord {ord} declares record layout {layout}; this build records layout " +
                    TspiFormat.LayoutSixDofV1 + " only, so it is not recorded");
                return;
            }

            long? parent = Int64(e, "parent");
            var spool = new Spool(Path.Combine(_spoolDir, ord + ".rec"))
            {
                Meta = new TspiEntityEntry
                {
                    Ord = ord,
                    Id = Str(e, "id") ?? ("ord" + ord),
                    Team = Str(e, "team") ?? "white",
                    Type = Str(e, "type") ?? "aircraft",
                    Model = Str(e, "model") ?? "live",
                    ParentOrd = parent is { } pv && pv >= 0 ? (uint)pv : null,
                    T0Ns = Int64(e, "t0_ns") ?? 0,
                },
            };
            _byOrd[ord] = spool;
            _order.Add(spool);
        }

        private void AddEvent(JsonElement e)
        {
            var ev = new TspiEventEntry
            {
                TNs = Int64(e, "t_ns") ?? 0,
                Kind = Str(e, "kind") ?? "event",
                SrcOrd = Int64(e, "src") is { } s && s >= 0 ? (uint)s : null,
                DstOrd = Int64(e, "dst") is { } d && d >= 0 ? (uint)d : null,
            };
            if (e.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                foreach (var f in data.EnumerateObject())
                {
                    object? v = f.Value.ValueKind switch
                    {
                        JsonValueKind.Number => f.Value.TryGetInt64(out long l) ? l : f.Value.GetDouble(),
                        JsonValueKind.String => f.Value.GetString(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null,   // nested objects/arrays are not part of the event vocabulary
                    };
                    if (v != null) ev.Data[f.Name] = v;
                }
            }
            _events.Add(ev);
        }

        /// <summary>[u32 count] then count * ([u32 ord][u32 sample_index][64-byte record]).</summary>
        private void HandleBatch(ReadOnlySpan<byte> frame)
        {
            RequireHello();
            if (frame.Length < 4) throw new LiveRecordError("short record batch frame");
            uint count = MemoryMarshal.Read<uint>(frame);
            long need = 4L + (long)count * WireItemSize;
            if (need > frame.Length)
                throw new LiveRecordError($"truncated record batch: {count} records need {need} bytes, frame has {frame.Length}");

            for (uint k = 0; k < count; k++)
            {
                int b = 4 + (int)k * WireItemSize;
                uint ord = MemoryMarshal.Read<uint>(frame.Slice(b, 4));
                uint index = MemoryMarshal.Read<uint>(frame.Slice(b + 4, 4));
                if (!_byOrd.TryGetValue(ord, out var spool)) { _dropped++; continue; }
                var record = MemoryMarshal.Read<TspiRecord>(frame.Slice(b + 8, RecordSize));
                Append(spool, index, record);
            }
        }

        private void Append(Spool s, uint wireIndex, TspiRecord record)
        {
            if (!IsFinite(record))
            {
                _dropped++;
                if (!s.WarnedNonFinite)
                {
                    s.WarnedNonFinite = true;
                    _warnings.Add($"entity '{s.Meta.Id}' sent non-finite values; those records were dropped " +
                        "(the gap is padded and counted)");
                }
                return;
            }

            long index = (long)wireIndex - s.IndexOffset;
            if (index < s.Count) return;                 // duplicate or stale: the file keeps the first
            if (index > s.Count)
            {
                if (s.Count == 0)
                {
                    // Joining a run in progress: rebase storage to this record and move t0
                    // to the record's true time, so samples keep their real sim timestamps.
                    s.IndexOffset += index;
                    s.Meta.T0Ns += index * (long)_header!.DtNs;
                    index = 0;
                }
                else
                {
                    // A dropped frame. Repeat the last sample so t = t0 + i*dt stays exact;
                    // padding is counted, never silently blended into the trajectory.
                    while (s.Count < index)
                    {
                        s.Write(s.Last);
                        _gaps++;
                        _samples++;
                    }
                }
            }

            // The container requires sign-continuous quaternions so playback slerp never
            // takes the long way round; a producer is not obliged to, so enforce it here.
            if (s.Count > 0 &&
                record.QuatW * s.Last.QuatW + record.QuatX * s.Last.QuatX +
                record.QuatY * s.Last.QuatY + record.QuatZ * s.Last.QuatZ < 0)
            {
                record.QuatW = -record.QuatW; record.QuatX = -record.QuatX;
                record.QuatY = -record.QuatY; record.QuatZ = -record.QuatZ;
                s.FlippedQuats++;
            }

            s.Write(record);
            _samples++;
        }

        private static bool IsFinite(in TspiRecord r) =>
            double.IsFinite(r.PosN) && double.IsFinite(r.PosE) && double.IsFinite(r.PosD) &&
            float.IsFinite(r.VelN) && float.IsFinite(r.VelE) && float.IsFinite(r.VelD) &&
            float.IsFinite(r.QuatW) && float.IsFinite(r.QuatX) &&
            float.IsFinite(r.QuatY) && float.IsFinite(r.QuatZ) &&
            float.IsFinite(r.OmegaX) && float.IsFinite(r.OmegaY) && float.IsFinite(r.OmegaZ);

        private void ReportProgress(bool force)
        {
            if (_progress == null) return;
            var now = DateTime.UtcNow;
            if (!force && (now - _lastProgress).TotalSeconds < 2) return;
            _lastProgress = now;
            _progress($"  {_samples:N0} samples, {_order.Count(s => s.Count > 0)} entities, " +
                $"{_events.Count} events" + (_gaps > 0 ? $", {_gaps:N0} filled" : ""));
        }

        public LiveRecordResult Finish()
        {
            if (_header == null)
                throw new LiveRecordError("producer never sent hello; nothing recorded");
            var recorded = _order.Where(s => s.Count > 0).ToList();
            if (recorded.Count == 0)
                throw new LiveRecordError("no records arrived before the stream ended; nothing to write");
            foreach (var s in _order.Where(s => s.Count == 0))
                _warnings.Add($"entity '{s.Meta.Id}' was announced but sent no records; it is not in the file");

            foreach (var s in recorded) s.Flush();

            long dtNs = (long)_header.DtNs;
            double startSec = recorded.Min(s => s.Meta.T0Ns) / 1e9;
            double endSec = recorded.Max(s => s.Meta.T0Ns + (s.Count - 1) * dtNs) / 1e9;
            int flipped = recorded.Sum(s => s.FlippedQuats);

            using (var w = new TspiStreamWriter(_opt.OutPath, _header))
            {
                foreach (var s in recorded)
                    w.WriteBlock(s.Meta, s.ReadRecords(), s.Count);
                w.AddEvents(_events);
                var prov = new Dictionary<string, object>
                {
                    { "op", "record" },
                    { "sim_version", SimInfo.Version },
                    { "dynamics", _dynamics },
                    { "source", _opt.Url },
                    { "protocol", (long)Protocol },
                    { "stream_name", _streamName },
                    { "dt_s", dtNs / 1e9 },
                    { "recorded_utc", DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture) },
                    { "samples", _samples },
                    { "gaps_filled", _gaps },
                    { "records_dropped", _dropped },
                    { "stop_reason", _stopReason },
                };
                if (flipped > 0) prov["quats_sign_flipped"] = (long)flipped;
                w.AddProvenance(prov);
                w.Finish();
            }

            ReportProgress(true);
            return new LiveRecordResult
            {
                OutPath = _opt.OutPath,
                StreamName = _streamName,
                DynamicsTag = _dynamics,
                Entities = recorded.Count,
                Samples = _samples,
                Events = _events.Count,
                GapsFilled = _gaps,
                RecordsDropped = _dropped,
                QuatsSignFlipped = flipped,
                DtSec = dtNs / 1e9,
                SpanStartSec = startSec,
                SpanEndSec = endSec,
                EndedByProducer = _endedByProducer,
                StopReason = _stopReason,
                Warnings = _warnings,
            };
        }

        public void DisposeSpools()
        {
            foreach (var s in _order) s.Dispose();
        }

        // ---- JSON helpers: producers may send numbers as strings (epoch_unix_ns must be,
        // since absolute nanoseconds overflow a JS number). ----

        private static string? Str(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static double? Num(JsonElement o, string name)
        {
            if (!o.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return d;
            return null;
        }

        private static long? Int64(JsonElement o, string name)
        {
            if (!o.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number)
                return v.TryGetInt64(out long l) ? l : (long)v.GetDouble();
            if (v.ValueKind == JsonValueKind.String &&
                long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long s))
                return s;
            return null;
        }
    }

    /// <summary>One entity's records, spooled to disk so memory stays flat.</summary>
    private sealed class Spool : IDisposable
    {
        private readonly string _path;
        private readonly FileStream _fs;
        private readonly byte[] _scratch = new byte[RecordSize];

        public Spool(string path)
        {
            _path = path;
            _fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        }

        public TspiEntityEntry Meta = new();
        public long Count;
        public long IndexOffset;
        public TspiRecord Last;
        public int FlippedQuats;
        public bool WarnedNonFinite;

        public void Write(in TspiRecord r)
        {
            MemoryMarshal.Write(_scratch, in r);
            _fs.Write(_scratch, 0, RecordSize);
            Last = r;
            Count++;
        }

        public void Flush() => _fs.Flush();

        public IEnumerable<TspiRecord> ReadRecords()
        {
            _fs.Position = 0;
            var buf = new byte[RecordSize];
            for (long i = 0; i < Count; i++)
            {
                _fs.ReadExactly(buf, 0, RecordSize);
                yield return MemoryMarshal.Read<TspiRecord>(buf);
            }
        }

        public void Dispose()
        {
            _fs.Dispose();
            try { File.Delete(_path); } catch (IOException) { /* swept with the spool dir */ }
        }
    }
}

public sealed class LiveRecordOptions
{
    /// <summary>Producer endpoint, ws:// or wss://.</summary>
    public string Url = "";
    public string OutPath = "";
    /// <summary>Stop and write the file after this many wall seconds. Null records until the producer ends.</summary>
    public double? DurationSec;
    /// <summary>Where per-entity spool files go; defaults to a temp dir beside the output.</summary>
    public string? SpoolDir;
}

public sealed class LiveRecordResult
{
    public string OutPath = "";
    public string StreamName = "";
    public string DynamicsTag = "";
    public int Entities;
    public long Samples;
    public int Events;
    public long GapsFilled;
    public long RecordsDropped;
    public int QuatsSignFlipped;
    public double DtSec;
    public double SpanStartSec;
    public double SpanEndSec;
    public bool EndedByProducer;
    public string StopReason = "";
    public List<string> Warnings = new();
}

public sealed class LiveRecordError : Exception
{
    public LiveRecordError(string message) : base(message) { }
}
