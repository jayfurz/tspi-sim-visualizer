using System;
using System.Collections.Generic;
using System.Diagnostics;
using Tspi.Core.IO;
using Tspi.Sim.Engine;
using Tspi.Sim.Manifest;

namespace Tspi.Cli.Commands;

public static class AppendCommand
{
    public static int Run(string[] args)
    {
        var p = new ArgParse(args, new HashSet<string> { "quiet" });
        string file = p.Positional(0, "file.tspi");
        string addPath = p.Positional(1, "addendum.json");
        CliCommon.RequireFile(file, "trajectory file");
        CliCommon.RequireFile(addPath, "addendum");
        var models = CliCommon.Models(p.Option("models"), addPath);

        var (addendum, raw, shaHex) = ManifestJson.LoadAddendum(addPath);
        if (p.Option("seed") is { } seedStr)
        {
            if (!ulong.TryParse(seedStr, out ulong s)) throw new CliError("--seed must be a non-negative integer");
            addendum.Seed = s;
        }

        var validation = ManifestValidator.ValidateAddendum(addendum, models);
        if (!validation.IsValid)
        {
            foreach (var e in validation.Errors) Console.Error.WriteLine("error: " + e);
            throw new CliError($"addendum invalid ({validation.Errors.Count} error(s))");
        }

        var sw = Stopwatch.StartNew();
        SimResult result;
        using (var reader = TspiReader.Open(file))
        {
            result = SceneEngine.RunAddendum(reader, addendum, models);
        } // release the mmap before appending
        SimWriter.Append(file, result, shaHex, addendum.Seed, models);
        sw.Stop();

        long samples = 0;
        foreach (var e in result.Entities) samples += e.Traj.Count;
        if (!p.Switch("quiet"))
        {
            Console.WriteLine($"appended {result.Entities.Count} munition(s) to {file}");
            Console.WriteLine($"  {samples:N0} new samples, {result.Events.Count} events, {sw.Elapsed.TotalMilliseconds:N0} ms");
            foreach (var ev in result.Events)
                Console.WriteLine($"    t={ev.TNs / 1e9,7:0.00}s  {ev.Kind}" +
                                  (ev.Data.TryGetValue("miss_m", out var m) ? $" (miss {m} m)" : ""));
        }
        return 0;
    }
}
