using System;
using System.IO;
using System.Linq;
using Tspi.Core.Authoring;
using Tspi.Core.Json;
using Tspi.Core.Math;
using Tspi.Sim.Engine;
using Tspi.Sim.Manifest;
using Tspi.Sim.Models;
using Xunit;

namespace Tspi.Tests;

/// <summary>
/// ScenarioDocument is the authoring seam the Unity editor uses: it must round-trip
/// real manifests through the real validator, and an edit at time t must leave the
/// simulation before t byte-identical — that property is what makes the editor's
/// "regenerate and resume at the same time" loop seamless.
/// </summary>
public class ScenarioDocumentTests : IDisposable
{
    private readonly string _dir;
    public ScenarioDocumentTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tspi-doc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string ExamplePath(string name) =>
        Path.Combine(GoldenFileTests.RepoRoot(), "schemas", "examples", name);

    private static ModelLibrary Models() =>
        new(new[] { Path.Combine(GoldenFileTests.RepoRoot(), "models") });

    /// <summary>Save the doc and push it through the real parser + validator.</summary>
    private (ScenarioManifest Manifest, ValidationResult Validation) SaveAndValidate(ScenarioDocument doc)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, doc.ToJson());
        var (manifest, _, _) = ManifestJson.LoadScenario(path);
        return (manifest, ManifestValidator.Validate(manifest, Models()));
    }

    [Fact]
    public void RoundTrip_InterceptExample_PreservesUnknownFieldsAndValidates()
    {
        var doc = ScenarioDocument.FromJson(File.ReadAllText(ExamplePath("intercept.json")));

        var (_, validation) = SaveAndValidate(doc);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));

        // Fields the document class has no helpers for must survive a pure round-trip.
        string emitted = doc.ToJson();
        Assert.Contains("munitions", emitted);
        Assert.Contains("environment", emitted);
    }

    [Fact]
    public void PrettyPrint_ParsesBackToTheSameTree()
    {
        var doc = ScenarioDocument.FromJson(File.ReadAllText(ExamplePath("intercept.json")));
        string compactBefore = MiniJson.Serialize(doc.Root);
        string compactAfter = MiniJson.Serialize(MiniJson.Parse(doc.ToJson()));
        Assert.Equal(compactBefore, compactAfter);
    }

    [Fact]
    public void EditOps_MoveEntity_ChangeVelocity_StillValidates()
    {
        var doc = ScenarioDocument.FromJson(File.ReadAllText(ExamplePath("intercept.json")));
        string id = doc.EntityIds.First();

        doc.SetInitialPosNed(id, new Vec3d(1234.5, -678.9, -4321.0));
        doc.SetInitialVelNed(id, new Vec3d(0, 250, -5));
        doc.Seed = 7;

        Assert.Equal(new Vec3d(1234.5, -678.9, -4321.0), doc.GetInitialPosNed(id));
        var (manifest, validation) = SaveAndValidate(doc);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
        Assert.Equal(7UL, manifest.Seed);
    }

    [Fact]
    public void ManeuverAtT_LeavesPrefixByteIdentical_AndChangesTheFuture()
    {
        string json = File.ReadAllText(ExamplePath("intercept.json"));
        var baseline = ScenarioDocument.FromJson(json);
        var edited = ScenarioDocument.FromJson(json);

        double tEdit = edited.SnapToGrid(30.0);
        edited.AddManeuver("blue-01", tEdit,
            lateral: ScenarioDocument.LateralTurnToHeading(270, 4.0),
            vertical: ScenarioDocument.VerticalDeltaAlt(1500),
            speed: ScenarioDocument.SpeedSet(150));

        var (mBase, vBase) = SaveAndValidate(baseline);
        var (mEdit, vEdit) = SaveAndValidate(edited);
        Assert.True(vBase.IsValid, string.Join("; ", vBase.Errors));
        Assert.True(vEdit.IsValid, string.Join("; ", vEdit.Errors));

        var models = Models();
        var trajBase = SceneEngine.RunScenario(mBase, models).Entities.First(e => e.Id == "blue-01").Traj;
        var trajEdit = SceneEngine.RunScenario(mEdit, models).Entities.First(e => e.Id == "blue-01").Traj;

        int editIdx = (int)System.Math.Round((tEdit - trajBase.T0Sec) / trajBase.DtSec);
        for (int i = 0; i <= editIdx; i++)
        {
            Assert.Equal(trajBase.Pos[i], trajEdit.Pos[i]); // exact — the editor's resume-at-t guarantee
            Assert.Equal(trajBase.Vel[i], trajEdit.Vel[i]);
        }
        bool diverged = false;
        int n = System.Math.Min(trajBase.Count, trajEdit.Count);
        for (int i = editIdx + 1; i < n && !diverged; i++)
            diverged = !trajBase.Pos[i].Equals(trajEdit.Pos[i]);
        Assert.True(diverged, "maneuver had no effect after its activation time");
    }

    [Fact]
    public void NewScenario_AddEntities_Validates()
    {
        var doc = ScenarioDocument.New("editor-draft", 34.9061, -117.8839, 700, 60);
        doc.AddEntity("blue-01", "blue", "generic-fighter",
            new Vec3d(0, 0, -5000), new Vec3d(250, 0, 0));
        doc.AddEntity("red-01", "red", "generic-transport",
            new Vec3d(40000, 5000, -6000), new Vec3d(-150, 0, 0));

        Assert.Equal(2, doc.EntityCount);
        Assert.Equal("blue", doc.GetTeam("blue-01"));
        var (_, validation) = SaveAndValidate(doc);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));

        Assert.Throws<InvalidOperationException>(() =>
            doc.AddEntity("blue-01", "blue", "generic-fighter", Vec3d.Zero, Vec3d.Zero));
    }

    [Fact]
    public void Maneuvers_SnapToGrid_AndSortedInsert()
    {
        var doc = ScenarioDocument.New("m", 0, 0, 0, 100, 0.01);
        doc.AddEntity("a", "blue", "generic-fighter", new Vec3d(0, 0, -3000), new Vec3d(200, 0, 0));

        doc.AddManeuver("a", 40.0, lateral: ScenarioDocument.LateralTurnToHeading(90.0));
        doc.AddManeuver("a", 20.0037, speed: ScenarioDocument.SpeedSet(180));

        Assert.Equal(2, doc.Maneuvers("a").Count);
        Assert.Equal(20.0, doc.ManeuverAtS("a", 0), 9); // snapped and sorted first
        Assert.Equal(40.0, doc.ManeuverAtS("a", 1), 9);

        doc.RemoveManeuver("a", 0);
        Assert.Equal(40.0, doc.ManeuverAtS("a", 0), 9);
    }

    [Fact]
    public void RemoveEntity_SemanticBreakage_IsCaughtByTheRealValidator()
    {
        var doc = ScenarioDocument.FromJson(File.ReadAllText(ExamplePath("intercept.json")));
        doc.RemoveEntity("red-01"); // blue-01's munition targets it

        var (_, validation) = SaveAndValidate(doc);
        Assert.False(validation.IsValid);
    }
}
