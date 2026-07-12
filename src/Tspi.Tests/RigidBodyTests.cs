using System;
using System.Collections.Generic;
using System.IO;
using Tspi.Core.IO;
using Tspi.Core.Math;
using Tspi.Sim;
using Tspi.Sim.Engine;
using Tspi.Sim.Manifest;
using Tspi.Sim.Models;
using Xunit;

namespace Tspi.Tests;

/// <summary>
/// Analytic V&amp;V for the rigid-body rotational integrator, mirroring the philosophy of
/// <see cref="PhysicsVandVTests"/>: every claim is checked against a closed-form result
/// (torque-free conservation laws, axisymmetric precession, constant-torque spin-up)
/// or against the kinematic reference it must track (coordinated-turn bank).
/// </summary>
public class RigidBodyTests
{
    private const double G = 9.80665;

    // ---------------- pure integrator vs closed forms ----------------

    private static Vec3d ZeroTorque(QuatD q, Vec3d w) => Vec3d.Zero;

    [Fact]
    public void TorqueFreeConservesAngularMomentumAndEnergy()
    {
        var inertia = new Vec3d(30000, 80000, 100000); // fully asymmetric: tumbling exercises all coupling terms
        var q = QuatD.Identity;
        var w = new Vec3d(0.4, 0.2, -0.3);

        Vec3d L0 = q.Rotate(new Vec3d(inertia.X * w.X, inertia.Y * w.Y, inertia.Z * w.Z));
        double e0 = 0.5 * (inertia.X * w.X * w.X + inertia.Y * w.Y * w.Y + inertia.Z * w.Z * w.Z);

        const double dt = 0.002;
        for (int i = 0; i < 20000; i++) // 40 s of free tumble
            (q, w) = RigidBodyRotation.Step(q, w, inertia, ZeroTorque, dt);

        Vec3d L = q.Rotate(new Vec3d(inertia.X * w.X, inertia.Y * w.Y, inertia.Z * w.Z));
        double e = 0.5 * (inertia.X * w.X * w.X + inertia.Y * w.Y * w.Y + inertia.Z * w.Z * w.Z);

        // Angular momentum is a conserved NED-frame *vector* (direction and magnitude).
        Assert.True((L - L0).Length / L0.Length < 1e-9, $"L drift {(L - L0).Length / L0.Length:E2}");
        Assert.True(System.Math.Abs(e - e0) / e0 < 1e-9, $"energy drift {(e - e0) / e0:E2}");
        Assert.Equal(1.0, q.Norm, 12);
    }

    [Fact]
    public void TorqueFreeAxisymmetricPrecessionMatchesClosedForm()
    {
        // Axisymmetric body (It, It, Ia): the transverse rate vector rotates in the body
        // frame at Omega = ((Ia - It) / It) * wz; spin rate wz is constant.
        const double it = 20000, ia = 40000;
        var inertia = new Vec3d(it, it, ia);
        var q = QuatD.Identity;
        var w = new Vec3d(0.3, 0.0, 2.0);
        double omegaP = (ia - it) / it * w.Z; // 2.0 rad/s

        const double dt = 0.001;
        const int steps = 3000; // 3 s
        for (int i = 0; i < steps; i++)
            (q, w) = RigidBodyRotation.Step(q, w, inertia, ZeroTorque, dt);

        double t = steps * dt;
        Assert.Equal(0.3 * System.Math.Cos(omegaP * t), w.X, 8);
        Assert.Equal(0.3 * System.Math.Sin(omegaP * t), w.Y, 8);
        Assert.Equal(2.0, w.Z, 10);
    }

    [Fact]
    public void ConstantTorqueAboutPrincipalAxisSpinsUpExactly()
    {
        // Torque along a principal axis from rest keeps w on that axis, so the gyroscopic
        // term vanishes identically: w = (tau/I) t and roll angle = (tau/I) t^2 / 2.
        var inertia = new Vec3d(20000, 50000, 60000);
        var q = QuatD.Identity;
        var w = Vec3d.Zero;
        var tau = new Vec3d(4000, 0, 0);

        const double dt = 0.001;
        const int steps = 5000; // 5 s
        for (int i = 0; i < steps; i++)
            (q, w) = RigidBodyRotation.Step(q, w, inertia, (_, _) => tau, dt);

        double t = steps * dt;
        double alpha = tau.X / inertia.X;
        Assert.Equal(alpha * t, w.X, 10);
        Assert.Equal(0.0, w.Y, 12);
        Assert.Equal(0.0, w.Z, 12);

        var expected = QuatD.FromAxisAngle(new Vec3d(1, 0, 0), 0.5 * alpha * t * t);
        Assert.True(System.Math.Abs(QuatD.Dot(q, expected)) > 1.0 - 1e-9,
            $"attitude off by {2 * System.Math.Acos(System.Math.Min(1.0, System.Math.Abs(QuatD.Dot(q, expected))))} rad");
    }

    // ---------------- aircraft-level behavior ----------------

    private static VehicleModel KinematicFighter() => new()
    {
        Schema = VehicleModel.SchemaId, Kind = "aircraft", MassKg = 12000,
        GLimitMax = 9, AccelLongMaxMps2 = 40, AccelVertMaxMps2 = 60, SpeedMaxMps = 600,
    };

    private static VehicleModel RigidFighter()
    {
        var m = KinematicFighter();
        m.Rotational = new RotationalSpec
        {
            InertiaKgm2 = new[] { 15000.0, 80000, 90000 },
            MaxTorqueNm = new[] { 80000.0, 150000, 120000 },
            AttitudeKp = 9.0, AttitudeKd = 6.0,
        };
        return m;
    }

    private static ModelLibrary Models()
    {
        var lib = new ModelLibrary(Array.Empty<string>());
        lib.AddInMemory("fighter", KinematicFighter());
        lib.AddInMemory("fighter-rb", RigidFighter());
        return lib;
    }

    private static ScenarioManifest TurnScenario(string model, double duration = 12, double dt = 0.005)
    {
        var m = new ScenarioManifest
        {
            Schema = ScenarioManifest.SchemaId, Name = "rb", Seed = 7,
            Scene = new SceneSpec
            {
                OriginLla = new OriginLla { LatDeg = 35, LonDeg = -117, AltM = 0 },
                DurationS = duration, DtS = dt,
                Environment = new EnvironmentSpec { Atmosphere = "none" },
            },
        };
        m.Entities.Add(new EntitySpec
        {
            Id = "a", Team = "blue", Model = model,
            Initial = new InitialState { PosNedM = new[] { 0.0, 0, -6000 }, VelNedMps = new[] { 250.0, 0, 0 } },
            Maneuvers = new List<ManeuverSegment>
            {
                new() { AtS = 0.0, Lateral = new LateralTurnToHeading { HeadingDeg = 180, GLimit = 4 } },
            },
        });
        return m;
    }

    [Fact]
    public void RigidAircraftRollsIntoCoordinatedBankWithLagThenTracks()
    {
        const double dt = 0.005;
        var traj = SceneEngine.RunScenario(TurnScenario("fighter-rb", dt: dt), Models()).Entities[0].Traj;
        Assert.True(traj.HasTrueRates);

        double RollAt(int i)
        {
            traj.Att[i].ToYprNed(out _, out _, out double roll);
            return roll;
        }
        double bankRef = System.Math.Atan(4.0); // coordinated 4 g bank, ~75.96 deg

        // t=0: wings level (integrated attitude starts from the initial state, it cannot snap).
        Assert.Equal(0.0, RollAt(0), 9);
        // Early in the roll-in the airframe lags the reference — the whole point of the change.
        Assert.True(RollAt((int)(0.15 / dt)) < 0.5 * bankRef, "attitude snapped instead of lagging");
        // Mid-turn (heading still saturated): converged onto the coordinated bank.
        int mid = (int)(6.0 / dt);
        Assert.True(System.Math.Abs(RollAt(mid) - bankRef) < 0.5 * MathUtil.Deg2Rad,
            $"bank {RollAt(mid) * MathUtil.Rad2Deg:F2} deg vs reference {bankRef * MathUtil.Rad2Deg:F2} deg");

        // Steady level coordinated turn: body rates are (p, q, r) = psidot * (-sin(theta),
        // sin(phi) cos(theta), cos(phi) cos(theta)) with theta ~ 0 — check the integrated,
        // recorded rates against that closed form.
        double psiDot = 4.0 * G / 250.0;
        var wMid = traj.OmegaBody[mid];
        Assert.InRange(wMid.X, -0.01, 0.01);
        Assert.InRange(wMid.Y, psiDot * System.Math.Sin(bankRef) - 0.01, psiDot * System.Math.Sin(bankRef) + 0.01);
        Assert.InRange(wMid.Z, psiDot * System.Math.Cos(bankRef) - 0.01, psiDot * System.Math.Cos(bankRef) + 0.01);

        // Integrated quaternions stay unit-norm through the whole run.
        for (int i = 0; i < traj.Count; i += 200)
            Assert.Equal(1.0, traj.Att[i].Norm, 9);
    }

    [Fact]
    public void RigidAttitudeDoesNotPerturbTranslation()
    {
        // Rotation is driven by the flight path, never the reverse (translation stays on
        // the kinematic autopilot), so positions/velocities must be bit-identical.
        var rigid = SceneEngine.RunScenario(TurnScenario("fighter-rb"), Models()).Entities[0].Traj;
        var kin = SceneEngine.RunScenario(TurnScenario("fighter"), Models()).Entities[0].Traj;
        Assert.Equal(kin.Count, rigid.Count);
        for (int i = 0; i < kin.Count; i += 50)
        {
            Assert.Equal(kin.Pos[i], rigid.Pos[i]);
            Assert.Equal(kin.Vel[i], rigid.Vel[i]);
        }
        Assert.False(kin.HasTrueRates);
    }

    [Fact]
    public void RigidRunsAreDeterministic()
    {
        var a = SceneEngine.RunScenario(TurnScenario("fighter-rb"), Models()).Entities[0].Traj;
        var b = SceneEngine.RunScenario(TurnScenario("fighter-rb"), Models()).Entities[0].Traj;
        for (int i = 0; i < a.Count; i += 100)
        {
            Assert.Equal(a.Att[i].W, b.Att[i].W);
            Assert.Equal(a.Att[i].X, b.Att[i].X);
            Assert.Equal(a.OmegaBody[i], b.OmegaBody[i]);
        }
    }

    // ---------------- provenance honesty tag ----------------

    [Fact]
    public void DynamicsTagTracksAttitudeMode()
    {
        Assert.Equal(SimWriter.DynSynthAttitude,
            SceneEngine.RunScenario(TurnScenario("fighter", duration: 1), Models()).DynamicsTag);
        Assert.Equal(SimWriter.DynRigidAttitude,
            SceneEngine.RunScenario(TurnScenario("fighter-rb", duration: 1), Models()).DynamicsTag);

        var mixed = TurnScenario("fighter-rb", duration: 1);
        mixed.Entities.Add(new EntitySpec
        {
            Id = "b", Team = "red", Model = "fighter",
            Initial = new InitialState { PosNedM = new[] { 5000.0, 0, -6000 }, VelNedMps = new[] { -250.0, 0, 0 } },
        });
        Assert.Equal(SimWriter.DynMixedAttitude, SceneEngine.RunScenario(mixed, Models()).DynamicsTag);
    }

    [Fact]
    public void StreamedFileCarriesRigidTagAndIntegratedRates()
    {
        string path = Path.Combine(Path.GetTempPath(), "tspi-rb-" + Guid.NewGuid().ToString("N") + ".tspi");
        try
        {
            SceneEngine.RunScenarioToFile(TurnScenario("fighter-rb", duration: 8), Models(), path, new byte[32], "0");
            using var r = TspiReader.Open(path);
            var prov = r.Footer.Provenance[0];
            Assert.Equal(SimWriter.DynRigidAttitude, (string)prov["dynamics"]);

            // The recorded body rates are the integrated omega: mid-turn pitch rate matches
            // the coordinated-turn closed form (finite-differenced rates would too, but
            // this proves the true-rate path survives the streaming writer).
            var e = r.FindEntity("a");
            Assert.NotNull(e);
            Assert.True(r.TrySampleAt(e!, 6.0, out var s, clamp: false));
            double psiDot = 4.0 * G / 250.0, bank = System.Math.Atan(4.0);
            Assert.InRange(s.OmegaBody.Y, psiDot * System.Math.Sin(bank) - 0.02, psiDot * System.Math.Sin(bank) + 0.02);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---------------- model validation ----------------

    [Fact]
    public void RotationalSpecIsRejectedOnMunitionsAndBadAxes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tspi-rb-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "bad-kind.json"), """
                { "schema": "tspi-model/1", "kind": "munition", "mass_kg": 150,
                  "rotational": { "inertia_kgm2": [10, 10, 10], "max_torque_nm": [1, 1, 1] } }
                """);
            File.WriteAllText(Path.Combine(dir, "bad-inertia.json"), """
                { "schema": "tspi-model/1", "kind": "aircraft", "mass_kg": 12000,
                  "rotational": { "inertia_kgm2": [10, -10, 10], "max_torque_nm": [1, 1, 1] } }
                """);
            var lib = new ModelLibrary(new[] { dir });
            Assert.False(lib.TryResolve("bad-kind", out _, out _, out string e1));
            Assert.Contains("aircraft", e1);
            Assert.False(lib.TryResolve("bad-inertia", out _, out _, out string e2));
            Assert.Contains("inertia", e2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
