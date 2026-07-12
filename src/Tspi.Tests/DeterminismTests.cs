using System;
using System.Collections.Generic;
using System.IO;
using Tspi.Core.IO;
using Tspi.Sim.Engine;
using Tspi.Sim.Manifest;
using Tspi.Sim.Models;
using Xunit;

namespace Tspi.Tests;

/// <summary>
/// Same-platform determinism (always enforced) — distinct from the committed golden
/// byte-lock in <see cref="GoldenFileTests"/>, which also guards cross-version drift and
/// is only bit-exact on the reference platform. Cross-platform reproducibility is
/// tolerance-based (see docs/CONVENTIONS.md); here we assert the strong same-machine
/// guarantee that a Monte Carlo campaign relies on.
/// </summary>
public class DeterminismTests
{
    private static ModelLibrary Models()
    {
        var lib = new ModelLibrary(Array.Empty<string>());
        lib.AddInMemory("fighter", new VehicleModel
        {
            Schema = VehicleModel.SchemaId, Kind = "aircraft", MassKg = 12000,
            GLimitMax = 9, AccelLongMaxMps2 = 40, AccelVertMaxMps2 = 60, SpeedMaxMps = 600,
        });
        lib.AddInMemory("aam", new VehicleModel
        {
            Schema = VehicleModel.SchemaId, Kind = "munition", MassKg = 150,
            GLimitMax = 40, Boost = new BoostSpec { ThrustN = 22000, DurationS = 6 },
            DragCdaM2 = 0.012, PronavGainDefault = 4, FuzeRadiusM = 10, MaxFlightTimeS = 90,
        });
        return lib;
    }

    private static ScenarioManifest Engagement(ulong seed)
    {
        return new ScenarioManifest
        {
            Schema = ScenarioManifest.SchemaId, Name = "det", Seed = seed,
            Scene = new SceneSpec
            {
                OriginLla = new OriginLla { LatDeg = 34.9, LonDeg = -117.8, AltM = 700 },
                DurationS = 40, DtS = 0.01,
                Environment = new EnvironmentSpec
                {
                    Atmosphere = "exp8500",
                    Wind = new WindSpec
                    {
                        Layers = new List<WindLayer> { new() { AltMslM = 0, FromDeg = 270, SpeedMps = 6 } },
                        Gusts = new GustSpec { SigmaMps = 2, TauS = 4 },
                    },
                },
            },
            Entities = new List<EntitySpec>
            {
                new()
                {
                    Id = "s", Team = "blue", Model = "fighter",
                    Initial = new InitialState { PosNedM = new[] { 0.0, 0, -8000 }, VelNedMps = new[] { 300.0, 0, 0 } },
                    Munitions = new List<MunitionSpec>
                    {
                        new() { Id = "m", Model = "aam", Target = "b",
                                Launch = new LaunchAtTime { AtS = 1 }, Guidance = new GuidanceSpec { Kind = "pronav" } },
                    },
                },
                new()
                {
                    Id = "b", Team = "red", Model = "fighter",
                    Initial = new InitialState { PosNedM = new[] { 20000.0, 2000, -8000 }, VelNedMps = new[] { -250.0, 0, 0 } },
                    Dispersions = new DispersionSpec { PosNedSigmaM = new[] { 500.0, 500, 100 } },
                },
            },
        };
    }

    private string WriteRun(ScenarioManifest m)
    {
        string path = Path.Combine(Path.GetTempPath(), "det-" + Guid.NewGuid().ToString("N") + ".tspi");
        var result = SceneEngine.RunScenario(m, Models());
        SimWriter.WriteNew(path, result, new byte[32], "0", m.Seed, Models());
        return path;
    }

    [Fact]
    public void SameSeedProducesByteIdenticalFiles()
    {
        string a = WriteRun(Engagement(4211));
        string b = WriteRun(Engagement(4211));
        try
        {
            Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void StreamingAndBufferedWritersAgreeByteForByte()
    {
        // The streaming CLI path (RunScenarioToFile) and the in-memory SimWriter.WriteNew
        // must produce identical bytes — otherwise the golden lock depends on which path ran.
        var m = Engagement(4211);
        string streamed = Path.Combine(Path.GetTempPath(), "stream-" + Guid.NewGuid().ToString("N") + ".tspi");
        string buffered = WriteRun(m);
        try
        {
            SceneEngine.RunScenarioToFile(m, Models(), streamed, new byte[32], "0");
            Assert.Equal(File.ReadAllBytes(buffered), File.ReadAllBytes(streamed));
        }
        finally { File.Delete(streamed); File.Delete(buffered); }
    }

    [Fact]
    public void DifferentSeedChangesTheOutcome()
    {
        string a = WriteRun(Engagement(1));
        string b = WriteRun(Engagement(2));
        try
        {
            Assert.NotEqual(File.ReadAllBytes(a), File.ReadAllBytes(b));
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void EnvironmentRoundTripsThroughFooterOnAppend()
    {
        var m = Engagement(4211);
        string path = WriteRun(m);
        try
        {
            using (var r = TspiReader.Open(path))
            {
                Assert.NotNull(r.Footer.Environment);
                Assert.Equal("exp8500", r.Footer.Environment["atmosphere"]);
                var spec = EnvironmentSerialization.FromJson(r.Footer.Environment);
                Assert.Equal("exp8500", spec.Atmosphere);
                Assert.NotNull(spec.Wind);
                Assert.NotNull(spec.Wind!.Gusts);
                Assert.Equal(2.0, spec.Wind.Gusts!.SigmaMps, 6);
            }
        }
        finally { File.Delete(path); }
    }
}
