using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using Tspi.Core.IO;
using Tspi.Core.Json;

namespace Tspi.Core.Live
{
    /// <summary>
    /// A live stream of TSPI, presented as an <see cref="ITspiSource"/> so viewers render
    /// it through exactly the same path as a finished file (see
    /// tools/live-stream/PROTOCOL.md for the wire contract).
    ///
    /// A producer sends the container's own 64-byte layout-1 records with time implicit
    /// (t = t0 + i*dt), so nothing is re-serialized and nothing is re-interpolated: this
    /// class stores the records in growable arrays and defers to
    /// <see cref="TspiSampling"/>, the same math <see cref="TspiReader"/> uses. A live
    /// pose and the replayed pose of the same run are the same number.
    ///
    /// This is the C# sibling of <c>LiveTspiFile</c> in web/viewer/tspi.js and shares its
    /// consumer rules — records for unannounced entities dropped, duplicate/stale indices
    /// dropped, dropped frames padded so t = t0 + i*dt stays exact (counted, not hidden),
    /// and a mid-stream join rebased so samples keep their true sim time. Keep the three
    /// (plus <c>LiveRecorder</c>) in lockstep.
    ///
    /// Threading: not thread-safe. Ingest and sample from one thread — a Unity client
    /// queues raw frames off the socket thread and ingests them on the main thread.
    /// </summary>
    public sealed class LiveTspiSource : ITspiSource
    {
        public const int Protocol = 1;
        private const int RecordSize = TspiFormat.StrideSixDofV1;   // 64
        private const int WireItemSize = 8 + RecordSize;            // ord + index + record
        private const int InitialCapacity = 1024;

        private readonly Dictionary<uint, Block> _byOrd = new Dictionary<uint, Block>();
        private readonly List<Block> _blocks = new List<Block>();
        private readonly List<TspiEntityEntry> _entities = new List<TspiEntityEntry>();
        private readonly List<TspiEventEntry> _events = new List<TspiEventEntry>();

        private LiveTspiSource(TspiHeader header)
        {
            Header = header;
        }

        // ---- ITspiSource ----

        public TspiHeader Header { get; }
        public IReadOnlyList<TspiEntityEntry> Entities => _entities;
        public IReadOnlyList<TspiEventEntry> Events => _events;
        public double DtSec => Header.DtSec;
        public bool IsLive => !Ended;

        /// <summary>Name the producer gave the stream in its hello (may be empty).</summary>
        public string StreamName { get; private set; } = "";
        /// <summary>The producer's own fidelity tag — this library did not compute the motion.</summary>
        public string DynamicsTag { get; private set; } = "live stream (producer-authoritative)";
        /// <summary>Ground-grid / scene-scale hint in metres, 0 when the producer sent none.</summary>
        public double ExtentM { get; private set; }
        /// <summary>The producer said the run is over; no more records will arrive.</summary>
        public bool Ended { get; private set; }

        public long Received { get; private set; }
        /// <summary>Samples synthesized to cover frames that never arrived.</summary>
        public long GapsFilled { get; private set; }
        /// <summary>Records discarded because their entity was never announced.</summary>
        public long Dropped { get; private set; }
        /// <summary>Bumped whenever an entity is announced, so a viewer knows to add views.</summary>
        public int EntityGeneration { get; private set; }

        public TspiEntityEntry FindEntity(string id)
        {
            for (int i = 0; i < _entities.Count; i++)
                if (_entities[i].Id == id) return _entities[i];
            return null;
        }

        public double StartSec(TspiEntityEntry e) => e.T0Ns / 1e9;

        public double EndSec(TspiEntityEntry e) =>
            (e.T0Ns + (e.SampleCount - 1) * (long)Header.DtNs) / 1e9;

        public TspiRecord ReadSample(TspiEntityEntry e, long index)
        {
            Block b = BlockOf(e);
            if (index < 0 || index >= b.Count) throw new ArgumentOutOfRangeException(nameof(index));
            return b.Records[index];
        }

        public bool TrySampleAt(TspiEntityEntry e, double tSec, out TspiState state, bool clamp = false)
        {
            state = default;
            if (e.SampleCount <= 0 || e.Layout != TspiFormat.LayoutSixDofV1) return false;
            if (!TspiSampling.TryLocate(tSec, StartSec(e), EndSec(e), e.SampleCount, DtSec,
                    clamp, out long i, out double u))
                return false;
            Block b = BlockOf(e);
            if (e.SampleCount == 1)
            {
                state = TspiSampling.Single(b.Records[0]);
                return true;
            }
            state = TspiSampling.Interpolate(b.Records[i], b.Records[i + 1], u, DtSec);
            return true;
        }

        /// <summary>Time span across every entity that has samples, or (0,0) when empty.</summary>
        public void TimeSpan(out double minSec, out double maxSec)
        {
            minSec = 0;
            maxSec = 0;
            bool any = false;
            for (int i = 0; i < _entities.Count; i++)
            {
                var e = _entities[i];
                if (e.SampleCount <= 0) continue;
                double s = StartSec(e), t = EndSec(e);
                if (!any) { minSec = s; maxSec = t; any = true; continue; }
                if (s < minSec) minSec = s;
                if (t > maxSec) maxSec = t;
            }
        }

        // ---- ingest ----

        /// <summary>
        /// Build a source from the producer's opening <c>hello</c>. Throws
        /// <see cref="LiveProtocolError"/> if it is not a usable hello.
        /// </summary>
        public static LiveTspiSource FromHello(string json)
        {
            Dictionary<string, object> msg = ParseObject(json);
            if (Str(msg, "type") != "hello")
                throw new LiveProtocolError("first message must be hello, got '" + (Str(msg, "type") ?? "?") + "'");
            long protocol = Int64(msg, "protocol") ?? Protocol;
            if (protocol != Protocol)
                throw new LiveProtocolError("unsupported live protocol " + protocol +
                    " (this build speaks " + Protocol + ")");

            long dtNs = Int64(msg, "dt_ns") ?? throw new LiveProtocolError("hello is missing dt_ns");
            if (dtNs <= 0) throw new LiveProtocolError("hello dt_ns must be positive, got " + dtNs);

            double lat = 0, lon = 0, alt = 0;
            if (msg.TryGetValue("origin", out object o) && o is Dictionary<string, object> origin)
            {
                lat = Double(origin, "lat_deg") ?? 0;
                lon = Double(origin, "lon_deg") ?? 0;
                alt = Double(origin, "alt_m") ?? 0;
            }

            var source = new LiveTspiSource(new TspiHeader
            {
                DtNs = (ulong)dtNs,
                EpochUnixNs = Int64(msg, "epoch_unix_ns") ?? 0,
                OriginLatDeg = lat,
                OriginLonDeg = lon,
                OriginAltM = alt,
            });
            source.StreamName = Str(msg, "name") ?? "";
            string dynamics = Str(msg, "dynamics");
            if (!string.IsNullOrEmpty(dynamics)) source.DynamicsTag = dynamics;
            source.ExtentM = Double(msg, "extent_m") ?? 0;

            if (msg.TryGetValue("entities", out object list) && list is List<object> entities)
                foreach (object e in entities)
                    if (e is Dictionary<string, object> ed)
                        source.AddEntity(ed);
            return source;
        }

        /// <summary>Ingest one JSON control message (entity / event / end).</summary>
        public LiveMessage IngestText(string json)
        {
            Dictionary<string, object> msg = ParseObject(json);
            switch (Str(msg, "type"))
            {
                case "hello":
                    throw new LiveProtocolError("duplicate hello on an open stream");
                case "entity":
                {
                    // The descriptor is nested so an entity's own `type`
                    // (aircraft/ship/munition) cannot collide with the envelope's `type`.
                    Dictionary<string, object> d = msg.TryGetValue("entity", out object nested) &&
                        nested is Dictionary<string, object> nd ? nd : msg;
                    TspiEntityEntry added = AddEntity(d);
                    return new LiveMessage { Kind = LiveMessageKind.Entity, Entity = added };
                }
                case "event":
                {
                    var ev = new TspiEventEntry
                    {
                        TNs = Int64(msg, "t_ns") ?? 0,
                        Kind = Str(msg, "kind") ?? "event",
                        SrcOrd = Ord(msg, "src"),
                        DstOrd = Ord(msg, "dst"),
                    };
                    if (msg.TryGetValue("data", out object data) && data is Dictionary<string, object> dd)
                        foreach (var kv in dd)
                            if (kv.Value is long || kv.Value is double || kv.Value is string || kv.Value is bool)
                                ev.Data[kv.Key] = kv.Value;
                    _events.Add(ev);
                    return new LiveMessage { Kind = LiveMessageKind.Event, Event = ev };
                }
                case "end":
                    Ended = true;
                    return new LiveMessage { Kind = LiveMessageKind.End };
                default:
                    // Forward compatibility: a newer producer may send messages this build
                    // predates. Ignoring them beats abandoning a good stream.
                    return new LiveMessage { Kind = LiveMessageKind.Unknown };
            }
        }

        /// <summary>
        /// Ingest one binary batch frame:
        /// [u32 count] then count * ([u32 ord][u32 sample_index][64-byte record]).
        /// Returns the number of records stored.
        /// </summary>
        public int IngestBatch(byte[] frame, int offset, int length)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (offset < 0 || length < 0 || offset + length > frame.Length)
                throw new ArgumentOutOfRangeException(nameof(length));
            var span = new ReadOnlySpan<byte>(frame, offset, length);
            if (span.Length < 4) throw new LiveProtocolError("short record batch frame");
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(span);
            long need = 4L + (long)count * WireItemSize;
            if (need > span.Length)
                throw new LiveProtocolError("truncated record batch: " + count + " records need " +
                    need + " bytes, frame has " + span.Length);

            int stored = 0;
            for (uint k = 0; k < count; k++)
            {
                int b = 4 + (int)k * WireItemSize;
                uint ord = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(b, 4));
                uint index = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(b + 4, 4));
                if (!_byOrd.TryGetValue(ord, out Block block)) { Dropped++; continue; }
                if (Append(block, index, DecodeRecord(span.Slice(b + 8, RecordSize)))) stored++;
            }
            return stored;
        }

        /// <summary>Convenience overload for a whole frame buffer.</summary>
        public int IngestBatch(byte[] frame) => IngestBatch(frame, 0, frame == null ? 0 : frame.Length);

        // ---- internals ----

        private sealed class Block
        {
            public TspiEntityEntry Meta;
            public TspiRecord[] Records = new TspiRecord[InitialCapacity];
            public LiveIndexTracker Index;
            public long Count => Index.Count;
        }

        private Block BlockOf(TspiEntityEntry e)
        {
            if (!_byOrd.TryGetValue(e.Ord, out Block b))
                throw new ArgumentException("entity ord " + e.Ord + " is not part of this stream");
            return b;
        }

        private TspiEntityEntry AddEntity(Dictionary<string, object> d)
        {
            long? ordRaw = Int64(d, "ord");
            if (ordRaw == null || ordRaw.Value < 0 || ordRaw.Value > uint.MaxValue)
                throw new LiveProtocolError("entity announced without a usable ord");
            uint ord = (uint)ordRaw.Value;
            if (_byOrd.TryGetValue(ord, out Block existing)) return existing.Meta;   // re-announce is harmless

            long? parent = Int64(d, "parent");
            var meta = new TspiEntityEntry
            {
                Ord = ord,
                Id = Str(d, "id") ?? ("ord" + ord),
                Team = Str(d, "team") ?? "white",
                Type = Str(d, "type") ?? "aircraft",
                Model = Str(d, "model") ?? "live",
                ParentOrd = parent.HasValue && parent.Value >= 0 ? (uint?)parent.Value : null,
                T0Ns = Int64(d, "t0_ns") ?? 0,
                SampleCount = 0,
                Layout = (int)(Int64(d, "layout") ?? TspiFormat.LayoutSixDofV1),
                Stride = TspiFormat.StrideSixDofV1,
            };
            var block = new Block { Meta = meta };
            _byOrd[ord] = block;
            _blocks.Add(block);
            _entities.Add(meta);
            EntityGeneration++;
            return meta;
        }

        private bool Append(Block b, uint wireIndex, TspiRecord record)
        {
            long t0 = b.Meta.T0Ns;
            if (!b.Index.Accept(wireIndex, (long)Header.DtNs, ref t0, out long padCount)) return false;
            b.Meta.T0Ns = t0;   // moved only when a late join rebased this entity

            long at = b.Count;
            Grow(b, at + padCount + 1);
            // A dropped frame repeats the last sample so t = t0 + i*dt stays exact; the
            // fill is counted rather than blended invisibly into the trajectory.
            for (long k = 0; k < padCount; k++)
            {
                b.Records[at + k] = b.Records[at - 1];
                GapsFilled++;
            }
            b.Records[at + padCount] = record;
            b.Index.Stored(padCount);
            b.Meta.SampleCount = b.Count;
            Received++;
            return true;
        }

        private static void Grow(Block b, long need)
        {
            if (b.Records.LongLength >= need) return;
            long cap = b.Records.LongLength;
            while (cap < need) cap *= 2;
            if (cap > int.MaxValue) throw new LiveProtocolError("live buffer for one entity exceeded 2^31 samples");
            var bigger = new TspiRecord[cap];
            Array.Copy(b.Records, bigger, b.Count);
            b.Records = bigger;
        }

        private static TspiRecord DecodeRecord(ReadOnlySpan<byte> s)
        {
            return new TspiRecord
            {
                PosN = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(s)),
                PosE = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(s.Slice(8))),
                PosD = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(s.Slice(16))),
                VelN = ReadSingle(s.Slice(24)),
                VelE = ReadSingle(s.Slice(28)),
                VelD = ReadSingle(s.Slice(32)),
                QuatW = ReadSingle(s.Slice(36)),
                QuatX = ReadSingle(s.Slice(40)),
                QuatY = ReadSingle(s.Slice(44)),
                QuatZ = ReadSingle(s.Slice(48)),
                OmegaX = ReadSingle(s.Slice(52)),
                OmegaY = ReadSingle(s.Slice(56)),
                OmegaZ = ReadSingle(s.Slice(60)),
            };
        }

        // netstandard2.1 has no BinaryPrimitives.ReadSingleLittleEndian.
        private static float ReadSingle(ReadOnlySpan<byte> s) =>
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(s));

        // ---- JSON helpers (MiniJson: producers may send numbers as strings, and
        // epoch_unix_ns must be one — absolute ns overflow a JS number) ----

        private static Dictionary<string, object> ParseObject(string json)
        {
            if (string.IsNullOrEmpty(json)) throw new LiveProtocolError("empty control message");
            object parsed;
            try
            {
                parsed = MiniJson.Parse(json);
            }
            catch (Exception ex)
            {
                throw new LiveProtocolError("malformed control message: " + ex.Message);
            }
            if (!(parsed is Dictionary<string, object> obj))
                throw new LiveProtocolError("control message must be a JSON object");
            return obj;
        }

        private static string Str(Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out object v) && v is string s ? s : null;

        private static double? Double(Dictionary<string, object> d, string key)
        {
            if (!d.TryGetValue(key, out object v)) return null;
            if (v is double dv) return dv;
            if (v is long lv) return lv;
            if (v is string sv && double.TryParse(sv, NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
                return p;
            return null;
        }

        private static long? Int64(Dictionary<string, object> d, string key)
        {
            if (!d.TryGetValue(key, out object v)) return null;
            if (v is long lv) return lv;
            if (v is double dv) return (long)dv;
            if (v is string sv && long.TryParse(sv, NumberStyles.Integer, CultureInfo.InvariantCulture, out long p))
                return p;
            return null;
        }

        private static uint? Ord(Dictionary<string, object> d, string key)
        {
            long? v = Int64(d, key);
            return v.HasValue && v.Value >= 0 && v.Value <= uint.MaxValue ? (uint?)v.Value : null;
        }
    }

    public enum LiveMessageKind
    {
        Unknown = 0,
        Entity,
        Event,
        End,
    }

    /// <summary>What a control message turned out to be, for a viewer's UI bookkeeping.</summary>
    public struct LiveMessage
    {
        public LiveMessageKind Kind;
        public TspiEntityEntry Entity;
        public TspiEventEntry Event;
    }

    /// <summary>The producer violated tools/live-stream/PROTOCOL.md.</summary>
    public sealed class LiveProtocolError : Exception
    {
        public LiveProtocolError(string message) : base(message) { }
    }
}
