using System;
using System.Collections.Generic;
using System.Linq;
using Tspi.Core.Math;
using Tspi.Sim.Engine;
using Tspi.Sim.Manifest;
using Tspi.Sim.Models;
using Xunit;

namespace Tspi.Tests;

/// <summary>
/// Analytic verification &amp; validation: each test checks the simulator against a
/// closed-form result, so the TSPI data is credible to people who didn't write the sim.
/// </summary>
public class PhysicsVandVTests
{
    private const double G = 9.80665;

    private static ScenarioManifest Scenario(double duration, double dt, string atmosphere = "none")
    {
        return new ScenarioManifest
        {
            Schema = ScenarioManifest.SchemaId, Name = "vv", Seed = 1,
            Scene = new SceneSpec
            {
                OriginLla = new OriginLla { LatDeg = 35, LonDeg = -117, AltM = 0 },
                DurationS = duration, DtS = dt,
                Environment = new EnvironmentSpec { Atmosphere = atmosphere },
            },
        };
    }

    private static ModelLibrary ModelsBuilt()
    {
        var lib = new ModelLibrary(Array.Empty<string>());
        lib.AddInMemory("fighter", new VehicleModel
        {
            Schema = VehicleModel.SchemaId, Kind = "aircraft", MassKg = 12000,
            GLimitMax = 9, AccelLongMaxMps2 = 40, AccelVertMaxMps2 = 60, SpeedMaxMps = 600,
        });
        lib.AddInMemory("ballistic", new VehicleModel
        {
            Schema = VehicleModel.SchemaId, Kind = "munition", MassKg = 500,
            GLimitMax = 40, DragCdaM2 = 0, PronavGainDefault = 4, FuzeRadiusM = 5, MaxFlightTimeS = 120,
        });
        return lib;
    }

    [Fact]
    public void StraightAndLevelMatchesConstantVelocity()
    {
        var m = Scenario(20, 0.01);
        m.Entities.Add(new EntitySpec
        {
            Id = "a", Team = "blue", Type = "aircraft", Model = "fighter",
            Initial = new InitialState { PosNedM = new[] { 0.0, 0, -6000 }, VelNedMps = new[] { 220.0, 0, 0 } },
        });
        var result = SceneEngine.RunScenario(m, ModelsBuilt());
        var traj = result.Entities[0].Traj;

        // Constant-velocity straight/level: pos.N = 220 t, altitude and cross-track hold.
        for (int i = 0; i < traj.Count; i += 100)
        {
            double t = i * 0.01;
            Assert.Equal(220.0 * t, traj.Pos[i].X, 2);
            Assert.Equal(0.0, traj.Pos[i].Y, 3);
            Assert.Equal(-6000.0, traj.Pos[i].Z, 2);
            Assert.Equal(220.0, traj.Vel[i].Length, 3);
        }
    }

    [Fact]
    public void VacuumBallisticMatchesParabola()
    {
        var m = Scenario(6, 0.005, atmosphere: "none");
        // Launcher tosses the munition up-and-forward; inherit its velocity at t=0.
        m.Entities.Add(new EntitySpec
        {
            Id = "launcher", Team = "blue", Model = "fighter",
            Initial = new InitialState { PosNedM = new[] { 0.0, 0, -5000 }, VelNedMps = new[] { 200.0, 0, -100 } },
            Munitions = new List<MunitionSpec>
            {
                new()
                {
                    Id = "bomb", Model = "ballistic", Target = "launcher",
                    Launch = new LaunchAtTime { AtS = 0.0 },
                    Guidance = new GuidanceSpec { Kind = "ballistic" },
                },
            },
        });
        var result = SceneEngine.RunScenario(m, ModelsBuilt());
        var bomb = result.Entities.First(e => e.Id == "bomb").Traj;

        var p0 = new Vec3d(0, 0, -5000);
        var v0 = new Vec3d(200, 0, -100);
        for (int i = 0; i < bomb.Count; i++)
        {
            double t = i * 0.005;
            // Closed form: p = p0 + v0 t + 0.5 g t^2 (down positive).
            double n = p0.X + v0.X * t;
            double d = p0.Z + v0.Z * t + 0.5 * G * t * t;
            Assert.Equal(n, bomb.Pos[i].X, 4);
            Assert.Equal(d, bomb.Pos[i].Z, 3);
        }
    }

    [Fact]
    public void CoordinatedTurnHoldsSpeedAndPullsCommandedG()
    {
        var m = Scenario(30, 0.005);
        m.Entities.Add(new EntitySpec
        {
            Id = "a", Model = "fighter",
            Initial = new InitialState { PosNedM = new[] { 0.0, 0, -6000 }, VelNedMps = new[] { 250.0, 0, 0 } },
            Maneuvers = new List<ManeuverSegment>
            {
                new() { AtS = 0.0, Lateral = new LateralTurnToHeading { HeadingDeg = 180, GLimit = 4 } },
            },
        });
        var result = SceneEngine.RunScenario(m, ModelsBuilt());
        var traj = result.Entities[0].Traj;

        // Mid-turn window (before rollout near the target heading): speed conserved and
        // horizontal acceleration magnitude ~ g_limit * g.
        double dt = 0.005;
        for (int i = 200; i < 600; i += 50)
        {
            double speed = traj.Vel[i].Length;
            Assert.Equal(250.0, speed, 0); // within 0.5 m/s
            var aVec = (traj.Vel[i + 1] - traj.Vel[i - 1]) / (2 * dt);
            double aHoriz = new Vec3d(aVec.X, aVec.Y, 0).Length;
            Assert.InRange(aHoriz, 4 * G - 1.0, 4 * G + 1.0);
        }
        // Altitude held during the level turn.
        Assert.Equal(-6000.0, traj.Pos[500].Z, 1);
    }

    [Fact]
    public void GuidedInterceptClosesInsideFuze()
    {
        var m = Scenario(60, 0.01);
        var models = ModelsBuilt();
        models.AddInMemory("aam", new VehicleModel
        {
            Schema = VehicleModel.SchemaId, Kind = "munition", MassKg = 150,
            GLimitMax = 40, Boost = new BoostSpec { ThrustN = 22000, DurationS = 6 },
            DragCdaM2 = 0.012, PronavGainDefault = 4, FuzeRadiusM = 10, MaxFlightTimeS = 90,
        });
        m.Entities.Add(new EntitySpec
        {
            Id = "shooter", Team = "blue", Model = "fighter",
            Initial = new InitialState { PosNedM = new[] { 0.0, 0, -8000 }, VelNedMps = new[] { 300.0, 0, 0 } },
            Munitions = new List<MunitionSpec>
            {
                new()
                {
                    Id = "aam1", Model = "aam", Target = "bandit",
                    Launch = new LaunchAtTime { AtS = 0.0 },
                    Guidance = new GuidanceSpec { Kind = "pronav", Gain = 4 },
                },
            },
        });
        m.Entities.Add(new EntitySpec
        {
            Id = "bandit", Team = "red", Model = "fighter",
            Initial = new InitialState { PosNedM = new[] { 25000.0, 4000, -8000 }, VelNedMps = new[] { -250.0, 0, 0 } },
            Maneuvers = new List<ManeuverSegment>
            {
                new() { AtS = 5.0, Lateral = new LateralTurnToHeading { HeadingDeg = 90, GLimit = 5 } },
            },
        });

        var result = SceneEngine.RunScenario(m, models);
        var intercept = result.Events.FirstOrDefault(e => e.Kind == "intercept");
        Assert.NotNull(intercept);
        double miss = Convert.ToDouble(intercept!.Data["miss_m"]);
        Assert.True(miss <= 10.0, $"miss {miss} m exceeded fuze radius");
    }

    [Fact]
    public void DispersionsAreDeterministicPerSeedAndVaryAcrossSeeds()
    {
        ScenarioManifest Make(ulong seed)
        {
            var m = Scenario(5, 0.02);
            m.Seed = seed;
            m.Entities.Add(new EntitySpec
            {
                Id = "a", Model = "fighter",
                Initial = new InitialState { PosNedM = new[] { 0.0, 0, -6000 }, VelNedMps = new[] { 220.0, 0, 0 } },
                Dispersions = new DispersionSpec { PosNedSigmaM = new[] { 50.0, 50, 50 } },
            });
            return m;
        }
        var r1 = SceneEngine.RunScenario(Make(1), ModelsBuilt()).Entities[0].Traj.Pos[0];
        var r1b = SceneEngine.RunScenario(Make(1), ModelsBuilt()).Entities[0].Traj.Pos[0];
        var r2 = SceneEngine.RunScenario(Make(2), ModelsBuilt()).Entities[0].Traj.Pos[0];
        Assert.Equal(r1.X, r1b.X, 12); // same seed -> identical
        Assert.NotEqual(r1.X, r2.X);   // different seed -> different draw
    }
}
