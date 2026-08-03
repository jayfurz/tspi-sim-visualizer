using System.Text.Json;
using Tspi.Sim.Manifest;
using Tspi.Sim.Models;
using Xunit;

namespace Tspi.Tests;

public class ManifestTests
{
    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, ManifestJson.Options)!;

    [Fact]
    public void MinimalScenarioUsesDefaults()
    {
        var m = Parse<ScenarioManifest>("""
        {
          "schema": "tspi-scenario/1",
          "name": "min",
          "scene": { "origin_lla": { "lat_deg": 34, "lon_deg": -117, "alt_m": 700 },
                     "duration_s": 10, "dt_s": 0.01 },
          "entities": [ { "id": "a", "model": "generic-fighter",
                          "initial": { "pos_ned_m": [0,0,-6000], "vel_ned_mps": [200,0,0] } } ]
        }
        """);
        Assert.Equal(0UL, m.Seed);
        Assert.Equal("exp8500", m.Scene.Environment.Atmosphere);
        Assert.Equal("gray", m.Entities[0].Team);
        Assert.Empty(m.Munitions);
        Assert.Null(m.Entities[0].Initial.AttYprDeg);
    }

    [Fact]
    public void UnknownMemberIsRejected()
    {
        var ex = Assert.Throws<JsonException>(() => Parse<ScenarioManifest>("""
        { "schema": "tspi-scenario/1", "name": "x", "typpo": 1,
          "scene": { "origin_lla": {"lat_deg":0,"lon_deg":0,"alt_m":0}, "duration_s": 1, "dt_s": 0.1 },
          "entities": [] }
        """));
        Assert.Contains("typpo", ex.Message);
    }

    [Fact]
    public void ManeuverChannelsParseAsDiscriminatedUnions()
    {
        var m = Parse<ScenarioManifest>("""
        {
          "schema": "tspi-scenario/1", "name": "m",
          "scene": { "origin_lla": {"lat_deg":0,"lon_deg":0,"alt_m":0}, "duration_s": 10, "dt_s": 0.1 },
          "entities": [ { "id": "a", "model": "f",
            "initial": { "pos_ned_m": [0,0,-1000], "vel_ned_mps": [200,0,0] },
            "maneuvers": [
              { "at_s": 1, "lateral": { "kind": "turn_to_heading", "heading_deg": 90, "g_limit": 5 },
                           "vertical": { "kind": "delta_alt", "delta_m": -500, "rate_mps": 30 },
                           "speed": { "kind": "set", "speed_mps": 250, "accel_mps2": 8 } }
            ] } ]
        }
        """);
        var seg = m.Entities[0].Maneuvers[0];
        var turn = Assert.IsType<LateralTurnToHeading>(seg.Lateral);
        Assert.Equal(90, turn.HeadingDeg);
        var climb = Assert.IsType<VerticalDeltaAlt>(seg.Vertical);
        Assert.Equal(-500, climb.DeltaM);
        Assert.IsType<SpeedSet>(seg.Speed);
    }

    [Fact]
    public void UnknownManeuverKindIsRejected()
    {
        Assert.ThrowsAny<JsonException>(() => Parse<ScenarioManifest>("""
        { "schema": "tspi-scenario/1", "name": "m",
          "scene": { "origin_lla": {"lat_deg":0,"lon_deg":0,"alt_m":0}, "duration_s": 1, "dt_s": 0.1 },
          "entities": [ { "id": "a", "model": "f",
            "initial": { "pos_ned_m": [0,0,-1000], "vel_ned_mps": [200,0,0] },
            "maneuvers": [ { "at_s": 1, "lateral": { "kind": "barrel_roll" } } ] } ] }
        """));
    }

    [Fact]
    public void LaunchConditionUnionParses()
    {
        var m = Parse<ScenarioManifest>("""
        {
          "schema": "tspi-scenario/1", "name": "m",
          "scene": { "origin_lla": {"lat_deg":0,"lon_deg":0,"alt_m":0}, "duration_s": 10, "dt_s": 0.1 },
          "entities": [ { "id": "a", "model": "f", "team": "blue",
            "initial": { "pos_ned_m": [0,0,-1000], "vel_ned_mps": [200,0,0] } },
            { "id": "b", "model": "f",
              "initial": { "pos_ned_m": [20000,0,-1000], "vel_ned_mps": [-200,0,0] } } ],
          "munitions": [
            { "id": "m1", "parent": "a", "model": "aam", "target": "b",
              "launch": { "when": "range_to_target", "less_than_m": 15000 } } ]
        }
        """);
        var launch = Assert.IsType<LaunchAtRange>(m.Munitions[0].Launch);
        Assert.Equal(15000, launch.LessThanM);
    }

    [Fact]
    public void ValidatorCatchesMissingTargetAndBadModelKind()
    {
        var models = new ModelLibrary(System.Array.Empty<string>());
        models.AddInMemory("fighter", new VehicleModel { Schema = VehicleModel.SchemaId, Kind = "aircraft", MassKg = 1000 });
        var m = Parse<ScenarioManifest>("""
        {
          "schema": "tspi-scenario/1", "name": "m",
          "scene": { "origin_lla": {"lat_deg":0,"lon_deg":0,"alt_m":0}, "duration_s": 10, "dt_s": 0.1 },
          "entities": [ { "id": "a", "model": "fighter",
            "initial": { "pos_ned_m": [0,0,-1000], "vel_ned_mps": [200,0,0] } } ],
          "munitions": [ { "id": "m1", "parent": "a", "model": "fighter", "target": "ghost",
                           "launch": { "when": "time", "at_s": 1 } } ]
        }
        """);
        var r = ManifestValidator.Validate(m, models);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("ghost"));
        Assert.Contains(r.Errors, e => e.Contains("expected 'munition'"));
    }

    [Theory]
    [InlineData("{name}-{seed:04}.tspi", "run", 42UL, "run-0042.tspi")]
    [InlineData("{name}.tspi", "x", 7UL, "x.tspi")]
    [InlineData("runs/{seed}.tspi", "n", 1234UL, "runs/1234.tspi")]
    public void TemplateRendering(string template, string name, ulong seed, string expected)
    {
        Assert.Equal(expected, ManifestJson.RenderTemplate(template, name, seed));
    }

    [Fact]
    public void UnknownTemplatePlaceholderThrows()
    {
        Assert.ThrowsAny<System.Exception>(() => ManifestJson.RenderTemplate("{bogus}.tspi", "n", 1));
    }
}
