using System;
using System.IO;
using Tspi.Core.IO;
using Tspi.Sim.Engine;
using Tspi.Sim.Manifest;
using Xunit;

namespace Tspi.Tests;

/// <summary>
/// Format lock: regenerating the golden scenario must produce a byte-identical file to
/// the committed fixture. If this fails you either broke determinism, changed the
/// simulation, or changed the format — all of which must be deliberate (regenerate the
/// golden and note it in docs/FORMAT.md).
/// </summary>
public class GoldenFileTests
{
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "schemas")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void GoldenRegenerationIsByteIdentical()
    {
        string root = RepoRoot();
        string manifestPath = Path.Combine(root, "schemas", "examples", "golden.json");
        string goldenPath = Path.Combine(root, "tools", "tspi_py", "tests", "data", "golden-v1.tspi");
        Assert.True(File.Exists(goldenPath), "committed golden fixture missing");

        var (manifest, raw, shaHex) = ManifestJson.LoadScenario(manifestPath);
        var models = new Tspi.Sim.Models.ModelLibrary(new[] { Path.Combine(root, "models") });
        var validation = ManifestValidator.Validate(manifest, models);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));

        string tmp = Path.Combine(Path.GetTempPath(), "golden-regen-" + Guid.NewGuid().ToString("N") + ".tspi");
        try
        {
            var result = SceneEngine.RunScenario(manifest, models);
            SimWriter.WriteNew(tmp, result, ManifestJson.Sha256Bytes(raw), shaHex, manifest.Seed, models);
            byte[] expected = File.ReadAllBytes(goldenPath);
            byte[] actual = File.ReadAllBytes(tmp);
            Assert.Equal(expected.Length, actual.Length);
            Assert.Equal(expected, actual);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void GoldenFileReadsBackCorrectly()
    {
        string goldenPath = Path.Combine(RepoRoot(), "tools", "tspi_py", "tests", "data", "golden-v1.tspi");
        using var r = TspiReader.Open(goldenPath);
        Assert.Equal(3, r.Entities.Count);
        Assert.Equal(0.1, r.DtSec, 12);
        var dart = r.FindEntity("dart-01");
        Assert.NotNull(dart);
        Assert.Equal("munition", dart!.Type);
        Assert.Equal(r.FindEntity("blue-01")!.Ord, dart.ParentOrd!.Value);
        Assert.Equal(2, r.Events.Count);
    }
}
