using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tspi.Core.Geo;
using Tspi.Core.IO;
using Tspi.Core.Math;
using Tspi.Sim.Import;
using Tspi.Sim.Manifest;
using Xunit;

namespace Tspi.Tests;

public class ImportTests : IDisposable
{
    private readonly string _dir;
    public ImportTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tspi-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Csv(string header, IEnumerable<string> rows)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, header + "\n" + string.Join("\n", rows) + "\n", new UTF8Encoding(false));
        return path;
    }

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static ImportOptions NedOrigin() => new()
    {
        OriginLatDeg = 34.9061, OriginLonDeg = -117.8839, OriginAltM = 700,
    };

    // ---- grid-aligned input is reproduced, not merely approximated ---------------------

    [Fact]
    public void NedInput_KnotsOnGrid_ReproducedExactly()
    {
        var rows = new List<string>();
        var pos = new List<Vec3d>();
        var vel = new List<Vec3d>();
        for (int i = 0; i <= 100; i++)
        {
            var p = new Vec3d(100 + 3.7 * i, -50 + 1.9 * i, -5000 - 0.4 * i);
            var v = new Vec3d(370, 190, -40);
            pos.Add(p); vel.Add(v);
            rows.Add($"a1,{R(0.01 * i)},{R(p.X)},{R(p.Y)},{R(p.Z)},{R(v.X)},{R(v.Y)},{R(v.Z)}");
        }
        string csv = Csv("entity,t_s,pos_n_m,pos_e_m,pos_d_m,vel_n,vel_e,vel_d", rows);

        var r = TspiImporter.Load(csv, NedOrigin());

        Assert.Single(r.Entities);
        Assert.True(r.DtInferred);
        Assert.Equal(10_000_000UL, r.DtNs);
        var e = r.Entities[0];
        Assert.Equal(0L, e.T0Ns);
        Assert.Equal(101, e.Traj.Count);
        for (int i = 0; i <= 100; i++)
        {
            Assert.Equal(pos[i].X, e.Traj.Pos[i].X); // exact: knot on grid takes the knot value
            Assert.Equal(pos[i].Y, e.Traj.Pos[i].Y);
            Assert.Equal(pos[i].Z, e.Traj.Pos[i].Z);
            Assert.Equal(vel[i].X, e.Traj.Vel[i].X);
        }
        Assert.Equal(TspiImporter.DynMeasuredSynthAttitude, r.DynamicsTag);
    }

    [Fact]
    public void InputQuatAndRates_CarriedThrough()
    {
        var q = QuatD.FromYprNed(1.0, 0.2, -0.1);
        var rows = new List<string>();
        for (int i = 0; i < 10; i++)
            rows.Add($"a1,{R(0.05 * i)},{R(i * 10.0)},0,-1000,{R(q.W)},{R(q.X)},{R(q.Y)},{R(q.Z)},0.01,0.02,0.03");
        string csv = Csv("entity,t_s,pos_n_m,pos_e_m,pos_d_m,qw,qx,qy,qz,wx,wy,wz", rows);

        var r = TspiImporter.Load(csv, NedOrigin());

        Assert.Equal(TspiImporter.DynMeasuredInputAttitude, r.DynamicsTag);
        var traj = r.Entities[0].Traj;
        Assert.True(traj.HasTrueRates);
        for (int i = 0; i < traj.Count; i++)
        {
            Assert.Equal(q.W, traj.Att[i].W, 12);
            Assert.Equal(0.02, traj.OmegaBody[i].Y, 12);
        }
    }

    [Fact]
    public void RatesWithoutQuat_Throws()
    {
        string csv = Csv("entity,t_s,pos_n_m,pos_e_m,pos_d_m,wx,wy,wz",
            new[] { "a1,0,0,0,0,0,0,0", "a1,0.1,1,0,0,0,0,0" });
        var ex = Assert.Throws<ImportError>(() => TspiImporter.Load(csv, NedOrigin()));
        Assert.Contains("wx/wy/wz", ex.Message);
    }

    // ---- irregular-rate resampling ------------------------------------------------------

    [Fact]
    public void IrregularInput_WithVelocities_ReconstructsCubicExactly()
    {
        // Cubic Hermite with exact knot derivatives reproduces cubics exactly.
        Vec3d P(double t) => new(5 * t * t * t - 2 * t * t + 3 * t + 10, -t * t * t + 4 * t, -1000 + t * t);
        Vec3d V(double t) => new(15 * t * t - 4 * t + 3, -3 * t * t + 4, 2 * t);
        var rows = new List<string>();
        double t0 = 0;
        for (int i = 0; t0 < 2.0; i++)
        {
            var p = P(t0); var v = V(t0);
            rows.Add($"a1,{R(t0)},{R(p.X)},{R(p.Y)},{R(p.Z)},{R(v.X)},{R(v.Y)},{R(v.Z)}");
            t0 += 0.007 + 0.011 * ((i * 7) % 5) / 5.0; // deterministic jitter
        }
        string csv = Csv("entity,t_s,pos_n_m,pos_e_m,pos_d_m,vel_n,vel_e,vel_d", rows);

        var opt = NedOrigin(); opt.DtSec = 0.01;
        var r = TspiImporter.Load(csv, opt);

        var e = r.Entities[0];
        for (int i = 0; i < e.Traj.Count; i++)
        {
            double t = (e.T0Ns + i * 10_000_000L) / 1e9;
            Assert.True(Vec3d.Distance(e.Traj.Pos[i], P(t)) < 1e-6,
                $"pos error {Vec3d.Distance(e.Traj.Pos[i], P(t)):E2} m at t={t}");
            Assert.True(Vec3d.Distance(e.Traj.Vel[i], V(t)) < 1e-5);
        }
    }

    [Fact]
    public void IrregularInput_NoVelocities_EstimatedDerivativesStayAccurate()
    {
        Vec3d P(double t) => new(200 * t, 3 * t * t, -2000 - 1.5 * t * t); // gentle quadratic
        var rows = new List<string>();
        double t0 = 0;
        for (int i = 0; t0 < 3.0; i++)
        {
            var p = P(t0);
            rows.Add($"a1,{R(t0)},{R(p.X)},{R(p.Y)},{R(p.Z)}");
            t0 += 0.03 + 0.02 * ((i * 3) % 4) / 4.0;
        }
        string csv = Csv("entity,t_s,pos_n_m,pos_e_m,pos_d_m", rows);

        var opt = NedOrigin(); opt.DtSec = 0.01;
        var r = TspiImporter.Load(csv, opt);

        var e = r.Entities[0];
        for (int i = 0; i < e.Traj.Count; i++)
        {
            double t = (e.T0Ns + i * 10_000_000L) / 1e9;
            Assert.True(Vec3d.Distance(e.Traj.Pos[i], P(t)) < 0.01,
                $"pos error {Vec3d.Distance(e.Traj.Pos[i], P(t)):E2} m at t={t}");
        }
    }

    // ---- frames, origin, altitude reference ---------------------------------------------

    [Fact]
    public void LlaInput_ConvertsThroughWgs84()
    {
        const double lat0 = 35.0, lon0 = -117.0, alt0 = 700.0;
        var truth = new List<Vec3d>();
        var rows = new List<string>();
        for (int i = 0; i <= 20; i++)
        {
            var ned = new Vec3d(i * 50.0, i * 20.0, -i * 10.0);
            truth.Add(ned);
            Wgs84.NedToLla(lat0, lon0, alt0, ned, out double la, out double lo, out double al);
            rows.Add($"a1,{R(0.05 * i)},{R(la)},{R(lo)},{R(al)}");
        }
        string csv = Csv("entity,t_s,lat_deg,lon_deg,alt_m", rows);

        var r = TspiImporter.Load(csv, new ImportOptions { OriginLatDeg = lat0, OriginLonDeg = lon0, OriginAltM = alt0 });

        var e = r.Entities[0];
        Assert.Equal(21, e.Traj.Count);
        for (int i = 0; i <= 20; i++)
            Assert.True(Vec3d.Distance(e.Traj.Pos[i], truth[i]) < 1e-5,
                $"NED error {Vec3d.Distance(e.Traj.Pos[i], truth[i]):E2} m at sample {i}");
    }

    [Fact]
    public void LlaInput_DefaultsOriginToFirstSample_AndAppliesGeoidOffset()
    {
        var rows = new List<string>();
        for (int i = 0; i <= 5; i++)
            rows.Add($"a1,{R(0.1 * i)},{R(35.0 + i * 1e-4)},{R(-117.0)},{R(700.0)}");
        string csv = Csv("entity,t_s,lat_deg,lon_deg,alt_m", rows);

        var r = TspiImporter.Load(csv, new ImportOptions { GeoidOffsetM = -30.0 });

        Assert.True(r.OriginFromData);
        Assert.Equal(35.0, r.OriginLatDeg, 12);
        Assert.Equal(670.0, r.OriginAltM, 9); // MSL 700 + geoid offset -30 -> ellipsoidal
        Assert.True(r.Entities[0].Traj.Pos[0].Length < 1e-6); // first sample sits at the origin
    }

    [Fact]
    public void NedInput_WithoutOrigin_Throws()
    {
        string csv = Csv("entity,t_s,pos_n_m,pos_e_m,pos_d_m", new[] { "a1,0,0,0,0", "a1,0.1,1,0,0" });
        var ex = Assert.Throws<ImportError>(() => TspiImporter.Load(csv, new ImportOptions()));
        Assert.Contains("--origin", ex.Message);
    }

    // ---- time handling ------------------------------------------------------------------

    [Fact]
    public void AbsoluteTime_AnchorsEpochAtEarliestSample()
    {
        var rows = new List<string>();
        for (int i = 0; i <= 10; i++)
            rows.Add($"a1,{R(1_700_000_000.0 + 0.05 * i)},{R(i * 10.0)},0,-1000");
        string csv = Csv("entity,t_unix_s,pos_n_m,pos_e_m,pos_d_m", rows);

        var r = TspiImporter.Load(csv, NedOrigin());

        Assert.Equal(1_700_000_000_000_000_000L, r.EpochUnixNs);
        Assert.Equal(0L, r.Entities[0].T0Ns);
    }

    [Fact]
    public void RelativeTime_UsesEpochOption()
    {
        string csv = Csv("entity,t_s,pos_n_m,pos_e_m,pos_d_m", new[] { "a1,0,0,0,0", "a1,0.1,1,0,0" });
        var opt = NedOrigin(); opt.Epoch = "2025-06-15T12:00:00Z";
        var r = TspiImporter.Load(csv, opt);
        Assert.Equal(DateTimeOffset.Parse("2025-06-15T12:00:00Z").ToUnixTimeMilliseconds() * 1_000_000L,
            r.EpochUnixNs);

        opt.Epoch = "not-a-time";
        Assert.Throws<ImportError>(() => TspiImporter.Load(csv, opt));
    }

    [Fact]
    public void DtInference_UsesMedianSpacing()
    {
        // Deltas: 0.05 x3, then a 0.10 dropout -> median stays 0.05.
        string csv = Csv("entity,t_s,pos_n_m,pos_e_m,pos_d_m", new[]
        {
            "a1,0,0,0,0", "a1,0.05,1,0,0", "a1,0.1,2,0,0", "a1,0.15,3,0,0", "a1,0.25,5,0,0",
        });
        var r = TspiImporter.Load(csv, NedOrigin());
        Assert.Equal(50_000_000UL, r.DtNs);
        Assert.Equal(6, r.Entities[0].Traj.Count); // 0..0.25 inclusive
    }

    [Fact]
    public void UnsortedAndDuplicateRows_AreSortedAndDeduped()
    {
        string csv = Csv("entity,t_s,pos_n_m,pos_e_m,pos_d_m", new[]
        {
            "a1,0.02,2,0,0", "a1,0,0,0,0", "a1,0.01,1,0,0", "a1,0.01,999,0,0",
        });
        var r = TspiImporter.Load(csv, NedOrigin());
        var e = r.Entities[0];
        Assert.Equal(3, e.Traj.Count);
        Assert.Equal(1.0, e.Traj.Pos[1].X); // first row wins the duplicate timestamp
        Assert.Contains(r.Warnings, w => w.Contains("not time-ordered"));
        Assert.Contains(r.Warnings, w => w.Contains("duplicate-timestamp"));
    }

    [Fact]
    public void GapPolicy_ErrorsOverMax_WarnsUnder()
    {
        string csv = Csv("entity,t_s,pos_n_m,pos_e_m,pos_d_m", new[]
        {
            "a1,0,0,0,0", "a1,0.1,1,0,0", "a1,6.1,2,0,0",
        });
        var ex = Assert.Throws<ImportError>(() => TspiImporter.Load(csv, NedOrigin())); // default max 5 s
        Assert.Contains("--max-gap-s", ex.Message);

        var opt = NedOrigin(); opt.MaxGapSec = 10; opt.DtSec = 0.1;
        var r = TspiImporter.Load(csv, opt);
        Assert.Contains(r.Warnings, w => w.Contains("gap"));
    }

    // ---- attitude synthesis --------------------------------------------------------------

    [Fact]
    public void SynthAttitude_FollowsVelocity()
    {
        var rows = new List<string>();
        for (int i = 0; i <= 20; i++)
            rows.Add($"a1,{R(0.05 * i)},0,{R(100.0 * 0.05 * i)},{R(-1000 - 5.0 * 0.05 * i)}"); // east at 100, climbing 5
        string csv = Csv("entity,t_s,pos_n_m,pos_e_m,pos_d_m", rows);

        var r = TspiImporter.Load(csv, NedOrigin());
        var traj = r.Entities[0].Traj;
        traj.Att[10].ToYprNed(out double yaw, out double pitch, out double roll);
        Assert.Equal(System.Math.PI / 2, yaw, 3);            // due east
        Assert.Equal(System.Math.Asin(5.0 / System.Math.Sqrt(100 * 100 + 25)), pitch, 3);
        Assert.Equal(0.0, roll, 3);                          // straight line -> no bank
        Assert.False(traj.HasTrueRates);                     // rates finite-differenced at write
    }

    // ---- end-to-end file writing -----------------------------------------------------------

    [Fact]
    public void WrittenFile_HeaderProvenanceAndSamples()
    {
        var rows = new List<string>();
        for (int i = 0; i <= 50; i++)
            rows.Add($"m-01,blue,aircraft,{R(0.02 * i)},{R(i * 8.0)},{R(i * 2.0)},{R(-3000.0)}");
        string csv = Csv("entity,team,type,t_s,pos_n_m,pos_e_m,pos_d_m", rows);

        var r = TspiImporter.Load(csv, NedOrigin());
        byte[] raw = File.ReadAllBytes(csv);
        string outPath = Path.Combine(_dir, "imported.tspi");
        TspiImporter.Write(outPath, r, ManifestJson.Sha256Bytes(raw), ManifestJson.Sha256Hex(raw), "source.csv");

        using var reader = TspiReader.Open(outPath);
        Assert.Equal(20_000_000UL, reader.Header.DtNs);
        Assert.Equal(34.9061, reader.Header.OriginLatDeg, 12);
        Assert.Equal(ManifestJson.Sha256Bytes(raw), reader.Header.ManifestSha256);

        var e = Assert.Single(reader.Entities);
        Assert.Equal("m-01", e.Id);
        Assert.Equal("blue", e.Team);
        Assert.Equal("aircraft", e.Type);
        Assert.Equal("measured", e.Model);
        Assert.Equal(51, e.SampleCount);
        var s25 = reader.ReadSample(e, 25);
        Assert.Equal(200.0, s25.PosN, 9);
        Assert.Equal(400.0, s25.VelN, 4); // 8 m per 0.02 s sample

        var prov = Assert.Single(reader.Footer.Provenance);
        Assert.Equal("import", prov["op"]);
        Assert.Equal(TspiImporter.DynMeasuredSynthAttitude, prov["dynamics"]);
        Assert.Equal("source.csv", prov["source"]);
        Assert.Equal(ManifestJson.Sha256Hex(raw), prov["source_sha256"]);
        Assert.Null(reader.Footer.Environment);
    }

    [Fact]
    public void Import_IsDeterministic()
    {
        var rows = new List<string>();
        for (int i = 0; i <= 30; i++)
            rows.Add($"a1,{R(0.033 * i)},{R(i * 12.5)},{R(i * -3.0)},{R(-2000 + i)}");
        string csv = Csv("entity,t_s,pos_n_m,pos_e_m,pos_d_m", rows);
        byte[] raw = File.ReadAllBytes(csv);

        string a = Path.Combine(_dir, "a.tspi");
        string b = Path.Combine(_dir, "b.tspi");
        TspiImporter.Write(a, TspiImporter.Load(csv, NedOrigin()),
            ManifestJson.Sha256Bytes(raw), ManifestJson.Sha256Hex(raw), "s.csv");
        TspiImporter.Write(b, TspiImporter.Load(csv, NedOrigin()),
            ManifestJson.Sha256Bytes(raw), ManifestJson.Sha256Hex(raw), "s.csv");
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
    }

    [Fact]
    public void InterleavedEntities_GroupedById()
    {
        string csv = Csv("entity,t_s,pos_n_m,pos_e_m,pos_d_m", new[]
        {
            "a1,0,0,0,0", "b2,0,100,0,0", "a1,0.1,1,0,0", "b2,0.1,101,0,0", "a1,0.2,2,0,0",
        });
        var r = TspiImporter.Load(csv, NedOrigin());
        Assert.Equal(2, r.Entities.Count);
        Assert.Equal("a1", r.Entities[0].Id); // first-appearance order -> ord 0
        Assert.Equal(3, r.Entities[0].Traj.Count);
        Assert.Equal(2, r.Entities[1].Traj.Count);
    }
}
