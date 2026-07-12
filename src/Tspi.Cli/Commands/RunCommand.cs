using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Tspi.Sim.Engine;
using Tspi.Sim.Manifest;

namespace Tspi.Cli.Commands;

public static class RunCommand
{
    public static int Run(string[] args)
    {
        var p = new ArgParse(args, new HashSet<string> { "quiet" });
        string path = p.Positional(0, "scenario.json");
        CliCommon.RequireFile(path, "scenario");
        var models = CliCommon.Models(p.Option("models"), path);

        var (manifest, raw, shaHex) = ManifestJson.LoadScenario(path);
        if (p.Option("seed") is { } seedStr)
        {
            if (!ulong.TryParse(seedStr, out ulong seedOverride))
                throw new CliError("--seed must be a non-negative integer");
            manifest.Seed = seedOverride;
        }

        var validation = ManifestValidator.Validate(manifest, models);
        foreach (var w in validation.Warnings) Console.WriteLine("warning: " + w);
        if (!validation.IsValid)
        {
            foreach (var e in validation.Errors) Console.Error.WriteLine("error: " + e);
            throw new CliError($"scenario invalid ({validation.Errors.Count} error(s))");
        }

        string outPath = p.OptionAny("o", "out")
                         ?? RelativeToManifest(path, ManifestJson.RenderTemplate(manifest.Output.TrajectoryFile, manifest.Name, manifest.Seed));

        var sw = Stopwatch.StartNew();
        var result = SceneEngine.RunScenario(manifest, models);
        var shaBytes = ManifestJson.Sha256Bytes(raw);
        SimWriter.WriteNew(outPath, result, shaBytes, shaHex, manifest.Seed, models);
        sw.Stop();

        long samples = 0;
        foreach (var e in result.Entities) samples += e.Traj.Count;
        double simSecPerWall = manifest.Scene.DurationS / System.Math.Max(1e-6, sw.Elapsed.TotalSeconds);
        if (!p.Switch("quiet"))
        {
            Console.WriteLine($"wrote {outPath}");
            Console.WriteLine($"  {result.Entities.Count} entities, {samples:N0} samples, " +
                              $"{new FileInfo(outPath).Length / 1024.0:N1} KiB");
            Console.WriteLine($"  {result.Events.Count} events, seed {manifest.Seed}, " +
                              $"{sw.Elapsed.TotalMilliseconds:N0} ms ({simSecPerWall:N0}x real-time)");
            foreach (var ev in result.Events)
                Console.WriteLine($"    t={ev.TNs / 1e9,7:0.00}s  {ev.Kind}" + EventDetail(ev, result));
        }
        return 0;
    }

    private static string EventDetail(Tspi.Core.IO.TspiEventEntry ev, SimResult result)
    {
        string src = ev.SrcOrd.HasValue && result.OrdToId.TryGetValue(ev.SrcOrd.Value, out var s) ? s : "";
        string dst = ev.DstOrd.HasValue && result.OrdToId.TryGetValue(ev.DstOrd.Value, out var d) ? d : "";
        string who = src;
        if (!string.IsNullOrEmpty(dst)) who += " -> " + dst;
        string extra = ev.Data.TryGetValue("miss_m", out var m) ? $" (miss {m} m)" : "";
        return string.IsNullOrEmpty(who) ? extra : "  " + who + extra;
    }

    private static string RelativeToManifest(string manifestPath, string outRel)
    {
        if (Path.IsPathRooted(outRel)) return outRel;
        string? dir = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
        return dir == null ? outRel : Path.Combine(dir, outRel);
    }
}
