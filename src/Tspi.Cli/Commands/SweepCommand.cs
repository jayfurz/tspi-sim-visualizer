using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Tspi.Core.Json;
using Tspi.Sim.Engine;
using Tspi.Sim.Manifest;
using Tspi.Sim.Models;

namespace Tspi.Cli.Commands;

/// <summary>
/// Monte Carlo fan-out. Each seed is an independent, deterministic run — the
/// embarrassingly-parallel axis that maps onto an HPC box's core count. Writes one
/// .tspi per seed plus a campaign index (index.jsonl) so 10k runs become a query,
/// not a directory crawl.
/// </summary>
public static class SweepCommand
{
    public static int Run(string[] args)
    {
        var p = new ArgParse(args, new HashSet<string> { "quiet" });
        string path = p.Positional(0, "scenario.json");
        CliCommon.RequireFile(path, "scenario");

        string seedsSpec = p.Option("seeds") ?? throw new CliError("--seeds A:B is required");
        var (lo, hi) = ParseRange(seedsSpec);
        int jobs = int.TryParse(p.OptionAny("j", "jobs"), out int j) ? j : System.Math.Max(1, System.Environment.ProcessorCount - 2);

        var (baseManifest, raw, _) = ManifestJson.LoadScenario(path);
        var models = CliCommon.Models(p.Option("models"), path);
        var validation = ManifestValidator.Validate(baseManifest, models);
        if (!validation.IsValid)
        {
            foreach (var e in validation.Errors) Console.Error.WriteLine("error: " + e);
            throw new CliError($"scenario invalid ({validation.Errors.Count} error(s))");
        }

        string outDir = p.Option("out-dir") ?? Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", "sweep_" + baseManifest.Name);
        Directory.CreateDirectory(outDir);

        var seeds = new List<ulong>();
        for (ulong s = lo; s <= hi; s++) seeds.Add(s);

        // Cluster fan-out: emit a job script/list instead of running in-process, so the
        // campaign can span nodes (in-process Parallel.ForEach is single-node only).
        string? emit = p.Option("emit");
        if (emit != null)
            return EmitJobs(emit, path, baseManifest.Name, lo, hi, outDir, p.Option("models"));

        Console.WriteLine($"sweep '{baseManifest.Name}': {seeds.Count} runs on {jobs} workers -> {outDir}");

        var sw = Stopwatch.StartNew();
        var indexLines = new ConcurrentDictionary<ulong, string>();
        int done = 0;
        var shaBytes = ManifestJson.Sha256Bytes(raw);
        string shaHex = ManifestJson.Sha256Hex(raw);

        Parallel.ForEach(seeds, new ParallelOptions { MaxDegreeOfParallelism = jobs }, seed =>
        {
            // Each worker reloads the manifest to get an isolated mutable copy + its own model cache.
            var (manifest, _, _) = ManifestJson.LoadScenario(path);
            manifest.Seed = seed;
            var localModels = CliCommon.Models(p.Option("models"), path);
            string outPath = Path.Combine(outDir,
                ManifestJson.RenderTemplate("{name}-{seed:06}.tspi", manifest.Name, seed));
            var summary = SceneEngine.RunScenarioToFile(manifest, localModels, outPath, shaBytes, shaHex);
            indexLines[seed] = IndexLine(seed, outPath, summary);
            int d = System.Threading.Interlocked.Increment(ref done);
            if (!p.Switch("quiet") && (d % System.Math.Max(1, seeds.Count / 20) == 0 || d == seeds.Count))
                Console.WriteLine($"  {d}/{seeds.Count} ({100.0 * d / seeds.Count:0}%)");
        });
        sw.Stop();

        string indexPath = Path.Combine(outDir, "index.jsonl");
        using (var iw = new StreamWriter(indexPath, false))
            foreach (var seed in seeds)
                iw.WriteLine(indexLines[seed]);

        Console.WriteLine($"done: {seeds.Count} runs in {sw.Elapsed.TotalSeconds:N1}s " +
                          $"({seeds.Count / sw.Elapsed.TotalSeconds:N0} runs/s), index -> {indexPath}");
        return 0;
    }

    private static int EmitJobs(string kind, string manifestPath, string name, ulong lo, ulong hi,
        string outDir, string? modelsOpt)
    {
        string manifest = Path.GetFullPath(manifestPath);
        string models = modelsOpt != null ? $" --models {modelsOpt}" : "";
        string outTmpl = Path.Combine(outDir, $"{name}-$SEED.tspi");
        switch (kind)
        {
            case "list":
                // One `tspi run` invocation per seed for GNU parallel / xargs.
                for (ulong s = lo; s <= hi; s++)
                    Console.WriteLine($"tspi run {manifest} --seed {s} -o " +
                                      Path.Combine(outDir, $"{name}-{s:D6}.tspi") + models + " --quiet");
                return 0;
            case "slurm":
                Console.WriteLine("#!/bin/bash");
                Console.WriteLine($"#SBATCH --job-name=tspi-{name}");
                Console.WriteLine($"#SBATCH --array={lo}-{hi}");
                Console.WriteLine("#SBATCH --ntasks=1 --cpus-per-task=1");
                Console.WriteLine($"#SBATCH --output={Path.Combine(outDir, "slurm-%a.out")}");
                Console.WriteLine("SEED=$SLURM_ARRAY_TASK_ID");
                Console.WriteLine($"tspi run {manifest} --seed $SEED -o " +
                                  Path.Combine(outDir, $"{name}-$SEED.tspi") + models + " --quiet");
                return 0;
            default:
                throw new CliError("--emit must be 'list' or 'slurm'");
        }
    }

    private static string IndexLine(ulong seed, string path, RunSummary result)
    {
        // Outcome summary: best miss distance per intercept attempt.
        double? minMiss = null;
        var outcomes = new List<object>();
        foreach (var ev in result.Events)
        {
            if ((ev.Kind == "cpa" || ev.Kind == "intercept") && ev.Data.TryGetValue("miss_m", out var m))
            {
                double miss = Convert.ToDouble(m, CultureInfo.InvariantCulture);
                minMiss = minMiss.HasValue ? System.Math.Min(minMiss.Value, miss) : miss;
                outcomes.Add(new Dictionary<string, object>
                {
                    { "kind", ev.Kind },
                    { "miss_m", miss },
                    { "t_s", ev.TNs / 1e9 },
                });
            }
        }
        var record = new Dictionary<string, object?>
        {
            { "seed", (long)seed },
            { "file", Path.GetFileName(path) },
            { "entities", (long)result.EntityCount },
            { "min_miss_m", minMiss.HasValue ? (object)minMiss.Value : null },
            { "events", outcomes },
        };
        return MiniJson.Serialize(record);
    }

    private static (ulong, ulong) ParseRange(string spec)
    {
        var parts = spec.Split(':');
        if (parts.Length != 2 || !ulong.TryParse(parts[0], out ulong lo) || !ulong.TryParse(parts[1], out ulong hi) || hi < lo)
            throw new CliError("--seeds must be 'A:B' with A <= B (e.g. 1:1000)");
        if (hi - lo + 1 > 5_000_000) throw new CliError("that's over 5M runs; narrow the seed range");
        return (lo, hi);
    }
}
