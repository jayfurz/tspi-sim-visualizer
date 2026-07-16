using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tspi.Core.Geo;
using Tspi.Core.IO;
using Tspi.Core.Math;
using Tspi.Sim.Engine;

namespace Tspi.Sim.Import;

/// <summary>Import failed for a user-fixable reason (bad columns, gaps, missing origin).</summary>
public sealed class ImportError : Exception
{
    public ImportError(string message) : base(message) { }
}

public sealed class ImportOptions
{
    /// <summary>Output sample period; null infers the median input spacing.</summary>
    public double? DtSec;
    /// <summary>NED origin. Required for NED input; defaults to the first sample for lat/lon/alt input.</summary>
    public double? OriginLatDeg, OriginLonDeg, OriginAltM;
    /// <summary>UTC of t=0 for relative-time (t_s) input; ignored when the input carries t_unix_s.</summary>
    public string Epoch = "2026-01-01T00:00:00Z";
    /// <summary>Largest input time gap the importer will interpolate across.</summary>
    public double MaxGapSec = 5.0;
    /// <summary>Added to input altitudes (lat/lon/alt input only): local geoid undulation for MSL data,
    /// since the format's altitudes are WGS84 ellipsoidal (docs/CONVENTIONS.md).</summary>
    public double GeoidOffsetM;
}

/// <summary>One imported track, resampled onto the fixed dt grid.</summary>
public sealed class ImportedEntity
{
    public string Id = "";
    public string Team = "gray";
    public string Type = "aircraft";
    public long T0Ns;
    public Trajectory Traj = new();
    public int InputSamples;
}

public sealed class ImportResult
{
    public readonly List<ImportedEntity> Entities = new();
    public ulong DtNs;
    public bool DtInferred;
    public long EpochUnixNs;
    public double OriginLatDeg, OriginLonDeg, OriginAltM;
    public bool OriginFromData;
    public double GeoidOffsetM;
    public string DynamicsTag = "";
    public readonly List<string> Warnings = new();
    public long InputRows;
    public double DtSec => DtNs / 1e9;
}

/// <summary>
/// Converts externally measured TSPI (CSV) into a .tspi file. Measured tracks become
/// first-class entities: the viewer plays them back and `tspi append` flies simulated
/// munitions against them — entities that were measured are never re-simulated.
///
/// The container mandates a fixed sample period with implicit time, so irregular
/// measured samples are resampled onto the dt grid: cubic Hermite position (knot
/// derivatives from input velocity when present, else estimated), slerped attitude,
/// linear body rates. All interval location is integer-nanosecond, so an input sample
/// that lands exactly on the grid is reproduced exactly. No RNG anywhere: the same
/// input and options produce byte-identical output.
///
/// Columns (header-mapped, case-insensitive; `tspi export` output imports directly):
///   entity|id, t_s|t_unix_s, pos_n_m/pos_e_m/pos_d_m or lat_deg/lon_deg/alt_m,
///   optional team, type, vel_n/vel_e/vel_d (NED m/s), qw/qx/qy/qz, wx/wy/wz.
/// </summary>
public static class TspiImporter
{
    /// <summary>Attitude columns came from the source data.</summary>
    public const string DynMeasuredInputAttitude = "measured+input-attitude";
    /// <summary>Source had no attitude; synthesized from the resampled flight path.</summary>
    public const string DynMeasuredSynthAttitude = "measured+synth-attitude";

    public static ImportResult Load(string csvPath, ImportOptions opt)
    {
        string[] lines = File.ReadAllLines(csvPath);
        int headerLine = 0;
        while (headerLine < lines.Length && string.IsNullOrWhiteSpace(lines[headerLine])) headerLine++;
        if (headerLine == lines.Length) throw new ImportError("empty input: " + csvPath);

        var index = new Dictionary<string, int>();
        var headerCols = lines[headerLine].Split(',');
        for (int i = 0; i < headerCols.Length; i++)
        {
            string name = headerCols[i].Trim().ToLowerInvariant();
            if (name.Length > 0 && !index.TryAdd(name, i))
                throw new ImportError($"duplicate column '{name}' in {csvPath}");
        }

        int idCol = ColAny(index, "entity", "id");
        if (idCol < 0) throw new ImportError("input needs an 'entity' (or 'id') column");
        int tRelCol = ColAny(index, "t_s"), tAbsCol = ColAny(index, "t_unix_s");
        if ((tRelCol >= 0) == (tAbsCol >= 0))
            throw new ImportError("input needs exactly one time column: 't_s' (relative seconds) or 't_unix_s' (absolute UTC)");
        int[]? ned = ColSet(index, "pos_n_m", "pos_e_m", "pos_d_m");
        int[]? lla = ColSet(index, "lat_deg", "lon_deg", "alt_m");
        if ((ned != null) == (lla != null))
            throw new ImportError("input needs exactly one position triplet: pos_n_m/pos_e_m/pos_d_m (NED) or lat_deg/lon_deg/alt_m");
        int[]? vel = ColSet(index, "vel_n", "vel_e", "vel_d");
        int[]? quat = ColSet(index, "qw", "qx", "qy", "qz");
        int[]? rates = ColSet(index, "wx", "wy", "wz");
        if (rates != null && quat == null)
            throw new ImportError("body-rate columns (wx/wy/wz) require attitude columns (qw/qx/qy/qz)");
        int teamCol = ColAny(index, "team"), typeCol = ColAny(index, "type");
        bool llaInput = lla != null;
        if (!llaInput && opt.GeoidOffsetM != 0)
            throw new ImportError("--geoid-offset-m applies to lat/lon/alt input only");

        // Parse rows grouped by entity id (interleaved rows are the common measured shape).
        var order = new List<RawEntity>();
        var byId = new Dictionary<string, RawEntity>();
        long inputRows = 0;
        for (int ln = headerLine + 1; ln < lines.Length; ln++)
        {
            if (string.IsNullOrWhiteSpace(lines[ln])) continue;
            var f = lines[ln].Split(',');
            string id = Str(f, idCol, csvPath, ln).Trim();
            if (id.Length == 0) throw new ImportError($"{csvPath}:{ln + 1}: empty entity id");
            if (!byId.TryGetValue(id, out var raw))
            {
                raw = new RawEntity { Id = id };
                if (teamCol >= 0) raw.Team = Str(f, teamCol, csvPath, ln).Trim();
                if (typeCol >= 0) raw.Type = Str(f, typeCol, csvPath, ln).Trim();
                byId[id] = raw;
                order.Add(raw);
            }
            raw.T.Add(Num(f, tRelCol >= 0 ? tRelCol : tAbsCol, csvPath, ln));
            var pos = new Vec3d(Num(f, (ned ?? lla!)[0], csvPath, ln),
                                Num(f, (ned ?? lla!)[1], csvPath, ln),
                                Num(f, (ned ?? lla!)[2], csvPath, ln));
            if (llaInput && (System.Math.Abs(pos.X) > 90 || System.Math.Abs(pos.Y) > 180))
                throw new ImportError($"{csvPath}:{ln + 1}: lat/lon out of range ({pos.X}, {pos.Y}) — columns swapped?");
            raw.P.Add(pos);
            if (vel != null)
                raw.V.Add(new Vec3d(Num(f, vel[0], csvPath, ln), Num(f, vel[1], csvPath, ln), Num(f, vel[2], csvPath, ln)));
            if (quat != null)
            {
                var q = new QuatD(Num(f, quat[0], csvPath, ln), Num(f, quat[1], csvPath, ln),
                                  Num(f, quat[2], csvPath, ln), Num(f, quat[3], csvPath, ln));
                if (q.Norm < 1e-6) throw new ImportError($"{csvPath}:{ln + 1}: zero-norm quaternion");
                raw.Q.Add(q.Normalized());
            }
            if (rates != null)
                raw.W.Add(new Vec3d(Num(f, rates[0], csvPath, ln), Num(f, rates[1], csvPath, ln), Num(f, rates[2], csvPath, ln)));
            inputRows++;
        }
        if (inputRows == 0) throw new ImportError("no data rows in " + csvPath);

        var result = new ImportResult
        {
            InputRows = inputRows,
            GeoidOffsetM = opt.GeoidOffsetM,
            DynamicsTag = quat != null ? DynMeasuredInputAttitude : DynMeasuredSynthAttitude,
        };

        // Time base: absolute input anchors the epoch at the earliest sample; relative
        // input takes the epoch option. Knot times become integer nanoseconds here and
        // stay integers through grid placement (CONVENTIONS.md: no floating-point time).
        double tBase;
        if (tAbsCol >= 0)
        {
            tBase = order.Min(r => r.T.Min());
            result.EpochUnixNs = (long)System.Math.Round(tBase * 1e9);
        }
        else
        {
            tBase = 0.0;
            if (!DateTimeOffset.TryParse(opt.Epoch, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dto))
                throw new ImportError($"--epoch is not a parseable ISO-8601 time: '{opt.Epoch}'");
            result.EpochUnixNs = dto.ToUnixTimeMilliseconds() * 1_000_000L;
        }

        var knots = new List<Knots>();
        foreach (var raw in order)
            knots.Add(SortAndDedupe(raw, tBase, result.Warnings));

        // Origin: explicit wins; lat/lon/alt input can default to its first sample.
        if (opt.OriginLatDeg is { } oLat)
        {
            if (opt.OriginLonDeg == null || opt.OriginAltM == null)
                throw new ImportError("--origin needs all of LAT,LON,ALT");
            result.OriginLatDeg = oLat;
            result.OriginLonDeg = opt.OriginLonDeg.Value;
            result.OriginAltM = opt.OriginAltM.Value;
        }
        else if (llaInput)
        {
            var first = knots[0].P[0];
            result.OriginLatDeg = first.X;
            result.OriginLonDeg = first.Y;
            result.OriginAltM = first.Z + opt.GeoidOffsetM;
            result.OriginFromData = true;
        }
        else throw new ImportError("NED input has no georeference — pass --origin LAT,LON,ALT");

        if (llaInput)
            foreach (var k in knots)
                for (int i = 0; i < k.P.Length; i++)
                    k.P[i] = Wgs84.LlaToNed(result.OriginLatDeg, result.OriginLonDeg, result.OriginAltM,
                        k.P[i].X, k.P[i].Y, k.P[i].Z + opt.GeoidOffsetM);

        result.DtNs = ResolveDt(opt, knots);
        result.DtInferred = opt.DtSec == null;

        long maxGapNs = (long)System.Math.Round(opt.MaxGapSec * 1e9);
        foreach (var k in knots)
        {
            CheckGaps(k, maxGapNs, (long)result.DtNs, opt.MaxGapSec, result.Warnings);
            result.Entities.Add(Resample(k, result.DtNs));
        }
        return result;
    }

    /// <summary>Write an ImportResult as a fresh .tspi. The header's manifest hash slot and the
    /// provenance record both carry the SHA-256 of the source data file.</summary>
    public static void Write(string outPath, ImportResult r, byte[] sourceSha256, string sourceShaHex, string sourceName)
    {
        var header = new TspiHeader
        {
            DtNs = r.DtNs,
            EpochUnixNs = r.EpochUnixNs,
            OriginLatDeg = r.OriginLatDeg,
            OriginLonDeg = r.OriginLonDeg,
            OriginAltM = r.OriginAltM,
            ManifestSha256 = sourceSha256,
        };
        using var w = new TspiStreamWriter(outPath, header);
        uint ord = 0;
        foreach (var e in r.Entities)
        {
            var meta = new TspiEntityEntry
            {
                Ord = ord++, Id = e.Id, Team = e.Team, Type = e.Type, Model = "measured", T0Ns = e.T0Ns,
            };
            w.WriteBlock(meta, e.Traj.EnumerateRecords(), e.Traj.Count);
        }
        var prov = new Dictionary<string, object>
        {
            { "op", "import" },
            { "sim_version", SimInfo.Version },
            { "dynamics", r.DynamicsTag },
            { "source", sourceName },
            { "source_sha256", sourceShaHex },
            { "dt_s", r.DtSec },
            { "origin", r.OriginFromData ? "first-sample" : "explicit" },
        };
        if (r.GeoidOffsetM != 0) prov["geoid_offset_m"] = r.GeoidOffsetM;
        w.AddProvenance(prov);
        w.Finish();
    }

    // ---- parsing helpers -------------------------------------------------------------

    private sealed class RawEntity
    {
        public string Id = "";
        public string Team = "gray";
        public string Type = "aircraft";
        public readonly List<double> T = new();
        public readonly List<Vec3d> P = new();
        public readonly List<Vec3d> V = new();
        public readonly List<QuatD> Q = new();
        public readonly List<Vec3d> W = new();
    }

    /// <summary>Time-sorted, duplicate-free knot arrays for one entity (times in relative ns).</summary>
    private sealed class Knots
    {
        public string Id = "", Team = "", Type = "";
        public int InputSamples;
        public long[] TNs = Array.Empty<long>();
        public Vec3d[] P = Array.Empty<Vec3d>();
        public Vec3d[]? V;
        public QuatD[]? Q;
        public Vec3d[]? W;
    }

    private static int ColAny(Dictionary<string, int> index, params string[] names)
    {
        foreach (var n in names)
            if (index.TryGetValue(n, out int i)) return i;
        return -1;
    }

    private static int[]? ColSet(Dictionary<string, int> index, params string[] names)
    {
        int found = names.Count(index.ContainsKey);
        if (found == 0) return null;
        if (found != names.Length)
            throw new ImportError("columns " + string.Join("/", names) + " must appear together");
        return names.Select(n => index[n]).ToArray();
    }

    private static string Str(string[] fields, int col, string path, int ln)
    {
        if (col >= fields.Length) throw new ImportError($"{path}:{ln + 1}: row has too few columns");
        return fields[col];
    }

    private static double Num(string[] fields, int col, string path, int ln)
    {
        string s = Str(fields, col, path, ln).Trim();
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
            throw new ImportError($"{path}:{ln + 1}: '{s}' is not a finite number");
        return v;
    }

    private static Knots SortAndDedupe(RawEntity raw, double tBase, List<string> warnings)
    {
        int n = raw.T.Count;
        var idx = Enumerable.Range(0, n).OrderBy(i => raw.T[i]).ToArray(); // stable
        bool resorted = false;
        for (int i = 0; i < n; i++) if (idx[i] != i) { resorted = true; break; }
        if (resorted) warnings.Add($"entity '{raw.Id}': input rows were not time-ordered; sorted");

        var k = new Knots { Id = raw.Id, Team = raw.Team, Type = raw.Type, InputSamples = n };
        var tNs = new List<long>(n);
        var p = new List<Vec3d>(n);
        var v = raw.V.Count > 0 ? new List<Vec3d>(n) : null;
        var q = raw.Q.Count > 0 ? new List<QuatD>(n) : null;
        var w = raw.W.Count > 0 ? new List<Vec3d>(n) : null;
        int dupes = 0;
        foreach (int i in idx)
        {
            long t = (long)System.Math.Round((raw.T[i] - tBase) * 1e9);
            if (tNs.Count > 0 && t == tNs[^1]) { dupes++; continue; }
            tNs.Add(t);
            p.Add(raw.P[i]);
            v?.Add(raw.V[i]);
            q?.Add(raw.Q[i]);
            w?.Add(raw.W[i]);
        }
        if (dupes > 0) warnings.Add($"entity '{raw.Id}': dropped {dupes} duplicate-timestamp row(s)");
        if (tNs.Count < 2)
            throw new ImportError($"entity '{raw.Id}' has fewer than 2 usable samples — nothing to interpolate");
        k.TNs = tNs.ToArray();
        k.P = p.ToArray();
        k.V = v?.ToArray();
        k.Q = q?.ToArray();
        k.W = w?.ToArray();
        return k;
    }

    private static ulong ResolveDt(ImportOptions opt, List<Knots> knots)
    {
        if (opt.DtSec is { } dt)
        {
            if (!(dt > 0)) throw new ImportError("--dt must be > 0");
            ulong ns = (ulong)System.Math.Round(dt * 1e9);
            if (ns == 0) throw new ImportError("--dt rounds to 0 ns");
            return ns;
        }
        var deltas = new List<long>();
        foreach (var k in knots)
            for (int i = 1; i < k.TNs.Length; i++)
                deltas.Add(k.TNs[i] - k.TNs[i - 1]);
        deltas.Sort();
        int m = deltas.Count / 2;
        long median = deltas.Count % 2 == 1 ? deltas[m] : (deltas[m - 1] + deltas[m]) / 2;
        if (median <= 0) throw new ImportError("cannot infer a sample period — provide --dt");
        return (ulong)median;
    }

    private static void CheckGaps(Knots k, long maxGapNs, long dtNs, double maxGapSec, List<string> warnings)
    {
        int gaps = 0;
        long largest = 0;
        for (int i = 1; i < k.TNs.Length; i++)
        {
            long d = k.TNs[i] - k.TNs[i - 1];
            if (d > maxGapNs)
                throw new ImportError($"entity '{k.Id}': {d / 1e9:0.###} s gap at t={k.TNs[i - 1] / 1e9:0.###} s " +
                                      $"exceeds --max-gap-s {maxGapSec} — split the data or raise the limit");
            if (d > 2 * dtNs) { gaps++; if (d > largest) largest = d; }
        }
        if (gaps > 0)
            warnings.Add($"entity '{k.Id}': interpolated across {gaps} gap(s) wider than 2*dt (largest {largest / 1e9:0.###} s)");
    }

    // ---- resampling ------------------------------------------------------------------

    private static ImportedEntity Resample(Knots k, ulong dtNsU)
    {
        long dtNs = (long)dtNsU;
        int n = k.TNs.Length;
        Vec3d[] dv = k.V ?? EstimateDerivatives(k.TNs, k.P);

        long kStart = CeilDiv(k.TNs[0], dtNs);
        long kEnd = FloorDiv(k.TNs[n - 1], dtNs);
        if (kEnd < kStart)
            throw new ImportError($"entity '{k.Id}': span {(k.TNs[n - 1] - k.TNs[0]) / 1e9:0.###} s contains no dt grid point");
        long countL = kEnd - kStart + 1;
        if (countL > int.MaxValue)
            throw new ImportError($"entity '{k.Id}': {countL:N0} output samples exceeds the per-entity limit");
        int count = (int)countL;

        var pos = new Vec3d[count];
        var velOut = new Vec3d[count];
        var att = k.Q != null ? new QuatD[count] : null;
        var om = k.W != null ? new Vec3d[count] : null;
        int seg = 0;
        for (int i = 0; i < count; i++)
        {
            long t = (kStart + i) * dtNs;
            while (seg < n - 2 && k.TNs[seg + 1] <= t) seg++;
            if (t == k.TNs[seg] || t == k.TNs[seg + 1])
            {
                int j = t == k.TNs[seg] ? seg : seg + 1; // exact knot: reproduce, don't interpolate
                pos[i] = k.P[j];
                velOut[i] = dv[j];
                if (att != null) att[i] = k.Q![j];
                if (om != null) om[i] = k.W![j];
                continue;
            }
            double h = (k.TNs[seg + 1] - k.TNs[seg]) / 1e9;
            double u = (t - k.TNs[seg]) / (double)(k.TNs[seg + 1] - k.TNs[seg]);
            Hermite(k.P[seg], dv[seg], k.P[seg + 1], dv[seg + 1], h, u, out pos[i], out velOut[i]);
            if (att != null) att[i] = QuatD.Slerp(k.Q![seg], k.Q![seg + 1], u);
            if (om != null) om[i] = k.W![seg] + (k.W![seg + 1] - k.W![seg]) * u;
        }
        att ??= SynthAttitude(velOut, dtNs / 1e9);

        var ent = new ImportedEntity
        {
            Id = k.Id, Team = k.Team, Type = k.Type, T0Ns = kStart * dtNs, InputSamples = k.InputSamples,
        };
        ent.Traj.T0Sec = kStart * dtNs / 1e9;
        ent.Traj.DtSec = dtNs / 1e9;
        for (int i = 0; i < count; i++)
            ent.Traj.Add(pos[i], velOut[i], att[i], om != null ? om[i] : null);
        return ent;
    }

    /// <summary>Second-order knot derivatives on irregular spacing (one-sided at the ends).</summary>
    private static Vec3d[] EstimateDerivatives(long[] tNs, Vec3d[] p)
    {
        int n = p.Length;
        var d = new Vec3d[n];
        for (int i = 1; i < n - 1; i++)
        {
            double h0 = (tNs[i] - tNs[i - 1]) / 1e9, h1 = (tNs[i + 1] - tNs[i]) / 1e9;
            Vec3d s0 = (p[i] - p[i - 1]) / h0, s1 = (p[i + 1] - p[i]) / h1;
            d[i] = (s1 * h0 + s0 * h1) / (h0 + h1);
        }
        d[0] = (p[1] - p[0]) / ((tNs[1] - tNs[0]) / 1e9);
        d[n - 1] = (p[n - 1] - p[n - 2]) / ((tNs[n - 1] - tNs[n - 2]) / 1e9);
        return d;
    }

    /// <summary>Cubic Hermite position and its exact derivative on one irregular interval.</summary>
    private static void Hermite(Vec3d p0, Vec3d v0, Vec3d p1, Vec3d v1, double h, double u,
        out Vec3d pos, out Vec3d vel)
    {
        double h00 = (2 * u - 3) * u * u + 1;
        double h10 = ((u - 2) * u + 1) * u;
        double h01 = (3 - 2 * u) * u * u;
        double h11 = (u - 1) * u * u;
        pos = h00 * p0 + (h10 * h) * v0 + h01 * p1 + (h11 * h) * v1;
        double g00 = 6 * u * u - 6 * u, g10 = 3 * u * u - 4 * u + 1, g11 = 3 * u * u - 2 * u;
        vel = (g00 / h) * p0 + g10 * v0 + (-g00 / h) * p1 + g11 * v1;
    }

    /// <summary>Yaw/pitch from the resampled velocity plus coordinated-turn bank — the same
    /// convention as the sim's synthesized aircraft attitude. Body rates are left to the
    /// writer's finite differencing.</summary>
    private static QuatD[] SynthAttitude(Vec3d[] vel, double dt)
    {
        int n = vel.Length;
        var q = new QuatD[n];
        double lastYaw = 0, lastPitch = 0;
        for (int i = 0; i < n; i++)
        {
            Vec3d v = vel[i];
            double vh = v.LengthHorizontal, speed = v.Length;
            double yaw = vh > 1e-3 ? System.Math.Atan2(v.Y, v.X) : lastYaw;
            double pitch = speed > 1e-3 ? System.Math.Asin(MathUtil.Clamp(-v.Z / speed, -1, 1)) : lastPitch;
            double bank = 0;
            if (vh > 1e-3 && n > 1)
            {
                Vec3d a = i == 0 ? (vel[1] - vel[0]) / dt
                    : i == n - 1 ? (vel[n - 1] - vel[n - 2]) / dt
                    : (vel[i + 1] - vel[i - 1]) / (2 * dt);
                double aLat = (v.X * a.Y - v.Y * a.X) / vh; // signed, + to the right of track
                bank = System.Math.Atan2(aLat, MathUtil.G0);
            }
            q[i] = QuatD.FromYprNed(yaw, pitch, bank);
            lastYaw = yaw;
            lastPitch = pitch;
        }
        return q;
    }

    private static long FloorDiv(long a, long b)
    {
        long q = a / b;
        if (a % b != 0 && (a < 0) != (b < 0)) q--;
        return q;
    }

    private static long CeilDiv(long a, long b) => -FloorDiv(-a, b);
}
