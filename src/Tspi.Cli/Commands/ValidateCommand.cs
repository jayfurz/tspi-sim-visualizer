using System;
using System.Collections.Generic;
using Tspi.Sim.Manifest;

namespace Tspi.Cli.Commands;

public static class ValidateCommand
{
    public static int Run(string[] args)
    {
        var p = new ArgParse(args, new HashSet<string> { "quiet" });
        string path = p.Positional(0, "scenario.json");
        CliCommon.RequireFile(path, "scenario");
        var models = CliCommon.Models(p.Option("models"), path);

        var (manifest, _, _) = ManifestJson.LoadScenario(path);
        var result = ManifestValidator.Validate(manifest, models);

        foreach (var w in result.Warnings) Console.WriteLine("warning: " + w);
        foreach (var e in result.Errors) Console.Error.WriteLine("error: " + e);

        if (result.IsValid)
        {
            if (!p.Switch("quiet"))
                Console.WriteLine($"ok: '{manifest.Name}' valid — {manifest.Entities.Count} entities, " +
                                  $"{manifest.Scene.DurationS}s @ {1.0 / manifest.Scene.DtS:0} Hz" +
                                  (result.Warnings.Count > 0 ? $" ({result.Warnings.Count} warning(s))" : ""));
            return 0;
        }
        Console.Error.WriteLine($"invalid: {result.Errors.Count} error(s)");
        return 2;
    }
}
