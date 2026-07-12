using System;
using System.Collections.Generic;
using Tspi.Core.Json;

namespace Tspi.Core.IO
{
    /// <summary>Footer entity-table entry: where one entity's contiguous sample block lives.</summary>
    public sealed class TspiEntityEntry
    {
        public uint Ord;
        public string Id = "";
        public string Team = "gray";
        public string Type = "generic";
        public string Model = "";
        /// <summary>Launching parent's ord for munitions; null for top-level entities.</summary>
        public uint? ParentOrd;
        /// <summary>First-sample time, nanoseconds relative to the header epoch.</summary>
        public long T0Ns;
        public long SampleCount;
        /// <summary>Absolute byte offset of the first record (block header sits 32 bytes before this).</summary>
        public long DataOffset;
        public int Stride = TspiFormat.StrideSixDofV1;
        public int Layout = TspiFormat.LayoutSixDofV1;

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "ord", (long)Ord },
                { "id", Id },
                { "team", Team },
                { "type", Type },
                { "model", Model },
                { "parent", ParentOrd.HasValue ? (object)(long)ParentOrd.Value : null },
                { "t0_ns", T0Ns },
                { "samples", SampleCount },
                { "offset", DataOffset },
                { "stride", (long)Stride },
                { "layout", (long)Layout },
            };
        }

        public static TspiEntityEntry FromJson(Dictionary<string, object> d)
        {
            return new TspiEntityEntry
            {
                Ord = (uint)JsonUtil.GetLong(d, "ord"),
                Id = JsonUtil.GetString(d, "id"),
                Team = JsonUtil.GetString(d, "team"),
                Type = JsonUtil.GetString(d, "type"),
                Model = JsonUtil.GetString(d, "model"),
                ParentOrd = JsonUtil.TryGetLong(d, "parent", out long p) ? (uint?)p : null,
                T0Ns = JsonUtil.GetLong(d, "t0_ns"),
                SampleCount = JsonUtil.GetLong(d, "samples"),
                DataOffset = JsonUtil.GetLong(d, "offset"),
                Stride = (int)JsonUtil.GetLong(d, "stride"),
                Layout = (int)JsonUtil.GetLong(d, "layout"),
            };
        }
    }

    /// <summary>Discrete event: launch, intercept, cpa, ground_impact, expire, killed, ...</summary>
    public sealed class TspiEventEntry
    {
        public long TNs;
        public string Kind = "";
        public uint? SrcOrd;
        public uint? DstOrd;
        /// <summary>Small numeric/string payload, e.g. {"miss_m": 3.2}.</summary>
        public Dictionary<string, object> Data = new Dictionary<string, object>();

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "t_ns", TNs },
                { "kind", Kind },
                { "src", SrcOrd.HasValue ? (object)(long)SrcOrd.Value : null },
                { "dst", DstOrd.HasValue ? (object)(long)DstOrd.Value : null },
                { "data", Data },
            };
        }

        public static TspiEventEntry FromJson(Dictionary<string, object> d)
        {
            var ev = new TspiEventEntry
            {
                TNs = JsonUtil.GetLong(d, "t_ns"),
                Kind = JsonUtil.GetString(d, "kind"),
                SrcOrd = JsonUtil.TryGetLong(d, "src", out long s) ? (uint?)s : null,
                DstOrd = JsonUtil.TryGetLong(d, "dst", out long t) ? (uint?)t : null,
            };
            if (d.TryGetValue("data", out object data) && data is Dictionary<string, object> dd)
                ev.Data = dd;
            return ev;
        }
    }

    /// <summary>
    /// The JSON footer: entity table, event log, provenance chain, and a link to the
    /// previous footer (appends chain footers; every historical index stays readable).
    /// </summary>
    public sealed class TspiFooter
    {
        public int FormatVersion = (int)TspiFormat.Version;
        public List<TspiEntityEntry> Entities = new List<TspiEntityEntry>();
        public List<TspiEventEntry> Events = new List<TspiEventEntry>();
        /// <summary>Freeform provenance records; one per write/append (sim_version, seed, hashes...).</summary>
        public List<Dictionary<string, object>> Provenance = new List<Dictionary<string, object>>();
        /// <summary>
        /// Opaque environment descriptor (atmosphere + wind) the producing scenario used.
        /// Carried forward across appends so later munitions fly in the same air mass as
        /// the original run. Null if the producer recorded none.
        /// </summary>
        public Dictionary<string, object> Environment;
        public long? PrevFooterOffset;
        public long? PrevFooterLen;

        public string ToJsonString()
        {
            var entities = new List<object>();
            foreach (var e in Entities) entities.Add(e.ToJson());
            var events = new List<object>();
            foreach (var e in Events) events.Add(e.ToJson());
            var provenance = new List<object>();
            foreach (var p in Provenance) provenance.Add(p);
            var root = new Dictionary<string, object>
            {
                { "format", new Dictionary<string, object> { { "version", (long)FormatVersion } } },
                { "entities", entities },
                { "events", events },
                { "provenance", provenance },
                { "environment", Environment },
                { "prev_footer_offset", PrevFooterOffset.HasValue ? (object)PrevFooterOffset.Value : null },
                { "prev_footer_len", PrevFooterLen.HasValue ? (object)PrevFooterLen.Value : null },
            };
            return MiniJson.Serialize(root);
        }

        public static TspiFooter FromJsonString(string json)
        {
            if (!(MiniJson.Parse(json) is Dictionary<string, object> root))
                throw new FormatException("Footer JSON root must be an object");
            var f = new TspiFooter();
            if (root.TryGetValue("format", out object fmt) && fmt is Dictionary<string, object> fd)
                f.FormatVersion = (int)JsonUtil.GetLong(fd, "version");
            if (f.FormatVersion != (int)TspiFormat.Version)
                throw new FormatException("Unsupported footer format version " + f.FormatVersion);
            if (root.TryGetValue("entities", out object ents) && ents is List<object> el)
                foreach (var item in el)
                    f.Entities.Add(TspiEntityEntry.FromJson((Dictionary<string, object>)item));
            if (root.TryGetValue("events", out object evs) && evs is List<object> vl)
                foreach (var item in vl)
                    f.Events.Add(TspiEventEntry.FromJson((Dictionary<string, object>)item));
            if (root.TryGetValue("provenance", out object prov) && prov is List<object> pl)
                foreach (var item in pl)
                    f.Provenance.Add((Dictionary<string, object>)item);
            if (root.TryGetValue("environment", out object envv) && envv is Dictionary<string, object> ed)
                f.Environment = ed;
            f.PrevFooterOffset = JsonUtil.TryGetLong(root, "prev_footer_offset", out long po) ? (long?)po : null;
            f.PrevFooterLen = JsonUtil.TryGetLong(root, "prev_footer_len", out long pn) ? (long?)pn : null;
            return f;
        }
    }

    internal static class JsonUtil
    {
        public static long GetLong(Dictionary<string, object> d, string key)
        {
            if (!d.TryGetValue(key, out object v) || v == null)
                throw new FormatException("Footer missing required key '" + key + "'");
            if (v is long l) return l;
            if (v is double dbl) return checked((long)dbl);
            throw new FormatException("Footer key '" + key + "' is not a number");
        }

        public static bool TryGetLong(Dictionary<string, object> d, string key, out long value)
        {
            value = 0;
            if (!d.TryGetValue(key, out object v) || v == null) return false;
            if (v is long l) { value = l; return true; }
            if (v is double dbl) { value = checked((long)dbl); return true; }
            return false;
        }

        public static string GetString(Dictionary<string, object> d, string key)
        {
            if (d.TryGetValue(key, out object v) && v is string s) return s;
            throw new FormatException("Footer missing required string key '" + key + "'");
        }
    }
}
