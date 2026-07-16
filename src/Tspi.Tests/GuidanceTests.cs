using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tspi.Core.IO;
using Tspi.Core.Math;
using Tspi.Sim;
using Tspi.Sim.Engine;
using Tspi.Sim.Manifest;
using Tspi.Sim.Models;
using Xunit;

namespace Tspi.Tests;

/// <summary>
/// The IGuidanceLaw seam: pronav's extraction is guarded by the golden byte-lock
/// (GoldenFileTests); these tests cover the seam's own contracts — MLP inference,
/// ZOH evaluation cadence, the envelope clamp living outside the law, validator
/// checks, and the nn end-to-end path including weights-in-provenance.
/// </summary>
public class GuidanceTests : IDisposable
{
    private readonly string _dir;
    public GuidanceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tspi-guidance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ModelLibrary RepoModels() =>
        new(new[] { Path.Combine(GoldenFileTests.RepoRoot(), "models") });

    // ---- MLP inference -------------------------------------------------------------

    [Fact]
    public void MlpForward_MatchesHandComputation()
    {
        var policy = new GuidancePolicy
        {
            Layers =
            {
                new PolicyLayer
                {
                    W = new[] { new[] { 0.5, -0.25 }, new[] { 1.0, 2.0 } },
                    B = new[] { 0.1, -0.2 },
                    Act = "tanh",
                },
                new PolicyLayer
                {
                    W = new[] { new[] { 1.0, -1.0 }, new[] { 0.5, 0.5 }, new[] { 2.0, 0.0 } },
                    B = new[] { 0.0, 0.25, -1.0 },
                    Act = "linear",
                },
            },
        };
        double[] y = policy.Forward(new[] { 0.3, -0.7 });

        double h1 = System.Math.Tanh(0.1 + 0.5 * 0.3 + -0.25 * -0.7);
        double h2 = System.Math.Tanh(-0.2 + 1.0 * 0.3 + 2.0 * -0.7);
        Assert.Equal(h1 - h2, y[0], 15);
        Assert.Equal(0.25 + 0.5 * h1 + 0.5 * h2, y[1], 15);
        Assert.Equal(-1.0 + 2.0 * h1, y[2], 15);
    }

    [Fact]
    public void PolicyValidation_RejectsBadShapes()
    {
        var lib = RepoModels();
        GuidancePolicy Valid() => new()
        {
            Schema = GuidancePolicy.SchemaId, Kind = "mlp", Obs = GuidancePolicy.ObsLosV1,
            Layers = { new PolicyLayer
            {
                W = new[] { new[] { 0.0, 0, 0, 0 }, new[] { 0.0, 0, 0, 0 }, new[] { 0.0, 0, 0, -30 } },
                B = new[] { 0.0, 0, 0 },
                Act = "linear",
            } },
        };
        lib.AddInMemoryPolicy("ok", Valid()); // sanity: the base shape is accepted

        var wrongIn = Valid();
        wrongIn.Layers[0].W = new[] { new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 0 } };
        Assert.Throws<InvalidDataException>(() => lib.AddInMemoryPolicy("bad-in", wrongIn));

        var wrongOut = Valid();
        wrongOut.Layers[0].W = new[] { new[] { 0.0, 0, 0, 0 }, new[] { 0.0, 0, 0, 0 } };
        wrongOut.Layers[0].B = new[] { 0.0, 0 };
        Assert.Throws<InvalidDataException>(() => lib.AddInMemoryPolicy("bad-out", wrongOut));

        var badAct = Valid();
        badAct.Layers[0].Act = "softmax";
        Assert.Throws<InvalidDataException>(() => lib.AddInMemoryPolicy("bad-act", badAct));
    }

    [Fact]
    public void RepoPolicy_ResolvesWithHash()
    {
        var lib = RepoModels();
        Assert.True(lib.TryResolvePolicy("generic-nn-losrate", out var policy, out string sha, out string err), err);
        Assert.NotNull(policy);
        Assert.Equal(64, sha.Length);
        Assert.True(lib.LoadedHashes.ContainsKey("generic-nn-losrate"));
    }

    // ---- pronav law ----------------------------------------------------------------

    [Fact]
    public void PronavLaw_MatchesClosedForm_AndIgnoresRecedingTargets()
    {
        var law = new PronavLaw(4.0);
        var self = new MotionState { Pos = new Vec3d(0, 0, -5000), Vel = new Vec3d(400, 0, 0) };
        var target = new MotionState { Pos = new Vec3d(10000, 2000, -5200), Vel = new Vec3d(-200, 100, 10) };

        Assert.True(law.TryAccelCmd(0, self, target, out Vec3d a));
        Vec3d r = target.Pos - self.Pos;
        Vec3d vRel = target.Vel - self.Vel;
        double range = r.Length;
        Vec3d omega = Vec3d.Cross(r, vRel) / (range * range);
        double closing = -Vec3d.Dot(r, vRel) / range;
        Vec3d expected = 4.0 * closing * Vec3d.Cross(omega, r / range);
        Assert.Equal(expected.X, a.X, 12);
        Assert.Equal(expected.Y, a.Y, 12);
        Assert.Equal(expected.Z, a.Z, 12);
        Assert.True(System.Math.Abs(Vec3d.Dot(a, r / range)) < 1e-9); // ⊥ line of sight

        var receding = new MotionState { Pos = new Vec3d(10000, 0, -5000), Vel = new Vec3d(600, 0, 0) };
        Assert.False(law.TryAccelCmd(0, self, receding, out _));
    }

    // ---- ZOH cadence + envelope clamp ----------------------------------------------

    private sealed class CountingLaw : IGuidanceLaw
    {
        public int Calls;
        public Vec3d Command = new(5, 0, 0);
        public bool HoldAcrossStep { get; set; }
        public bool TryAccelCmd(double tSec, MotionState self, MotionState target, out Vec3d accelCmd)
        {
            Calls++;
            accelCmd = Command;
            return true;
        }
    }

    [Fact]
    public void HoldAcrossStep_EvaluatesOncePerSample_NotPerRk4Stage()
    {
        var model = new VehicleModel { GLimitMax = 40 };
        var st = new MotionState { Pos = Vec3d.Zero, Vel = new Vec3d(100, 0, 0) };

        var held = new CountingLaw { HoldAcrossStep = true };
        var dyn = new MunitionDynamics(model, held, 0);
        Assert.True(dyn.WantsHeldCommand);
        dyn.UpdateHeldCommand(0, st, st);
        for (int stage = 0; stage < 4; stage++) dyn.Acceleration(0, st, st, 0);
        Assert.Equal(1, held.Calls); // one policy inference per output sample, not four

        var perStage = new CountingLaw { HoldAcrossStep = false };
        var dyn2 = new MunitionDynamics(model, perStage, 0);
        for (int stage = 0; stage < 4; stage++) dyn2.Acceleration(0, st, st, 0);
        Assert.Equal(4, perStage.Calls); // pronav cadence: every RK4 stage

        // A held law that was never refreshed commands nothing.
        var stale = new CountingLaw { HoldAcrossStep = true };
        var dyn3 = new MunitionDynamics(model, stale, 0);
        Vec3d accel = dyn3.Acceleration(0, st, st, 0);
        Assert.Equal(0.0, accel.X, 12);
    }

    [Fact]
    public void GLimitClamp_AppliesOutsideTheLaw()
    {
        var model = new VehicleModel { GLimitMax = 2.0 }; // 2 g envelope
        var wild = new CountingLaw { HoldAcrossStep = false, Command = new Vec3d(10000, 0, 0) };
        var dyn = new MunitionDynamics(model, wild, 0);
        var st = new MotionState { Pos = Vec3d.Zero, Vel = new Vec3d(100, 0, 0) };

        Vec3d accel = dyn.Acceleration(0, st, st, 0);
        Vec3d guidance = accel - new Vec3d(0, 0, MathUtil.G0); // subtract gravity
        Assert.Equal(2.0 * MathUtil.G0, guidance.Length, 9); // no policy exceeds the airframe
    }

    // ---- nn end-to-end ---------------------------------------------------------------

    [Fact]
    public void NnGuidance_Intercepts_Deterministically_WithWeightsInProvenance()
    {
        string root = GoldenFileTests.RepoRoot();
        string manifestPath = Path.Combine(root, "schemas", "examples", "nn-intercept.json");
        var (manifest, raw, shaHex) = ManifestJson.LoadScenario(manifestPath);
        var models = RepoModels();
        var validation = ManifestValidator.Validate(manifest, models);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));

        string a = Path.Combine(_dir, "a.tspi");
        string b = Path.Combine(_dir, "b.tspi");
        SceneEngine.RunScenarioToFile(manifest, models, a, ManifestJson.Sha256Bytes(raw), shaHex);
        SceneEngine.RunScenarioToFile(manifest, models, b, ManifestJson.Sha256Bytes(raw), shaHex);
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b)); // nn path is deterministic

        using var reader = TspiReader.Open(a);
        var cpa = reader.Events.First(e => e.Kind == "cpa");
        double miss = Convert.ToDouble(cpa.Data["miss_m"]);
        Assert.True(miss < 100, $"nn guidance missed by {miss} m"); // observed ~6 m on the reference platform

        var prov = reader.Footer.Provenance[0];
        var hashes = (Dictionary<string, object>)prov["models"];
        Assert.True(hashes.ContainsKey("generic-nn-losrate"), "policy weights hash missing from provenance");
        Assert.True(hashes.ContainsKey("generic-aam"));
    }

    // ---- validator -------------------------------------------------------------------

    [Fact]
    public void Validator_ChecksNnGuidance()
    {
        string manifestPath = Path.Combine(GoldenFileTests.RepoRoot(), "schemas", "examples", "nn-intercept.json");
        var (manifest, _, _) = ManifestJson.LoadScenario(manifestPath);
        var models = RepoModels();
        var gd = manifest.Entities.First(e => e.Munitions.Count > 0).Munitions[0].Guidance!;

        gd.Policy = null;
        var v = ManifestValidator.Validate(manifest, models);
        Assert.Contains(v.Errors, e => e.Contains("requires guidance.policy"));

        gd.Policy = "no-such-policy";
        v = ManifestValidator.Validate(manifest, models);
        Assert.Contains(v.Errors, e => e.Contains("no-such-policy"));

        gd.Kind = "pronav";
        gd.Policy = "generic-nn-losrate";
        v = ManifestValidator.Validate(manifest, models);
        Assert.True(v.IsValid, string.Join("; ", v.Errors));
        Assert.Contains(v.Warnings, w => w.Contains("ignored"));
    }
}
