using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tspi.Sim.Import;
using Tspi.Sim.Manifest;

namespace Tspi.Cli.Commands;

/// <summary>
/// Convert externally measured TSPI (CSV) into a .tspi. Measured tracks then compose
/// with the rest of the toolchain: the viewer plays them back and `tspi append` flies
/// simulated munitions against them, so measured entities are never re-simulated.
/// </summary>
public static class ImportCommand
{
    public static int Run(string[] args)
    {
        var p = new ArgParse(args, new HashSet<string> { "quiet" });
        string inPath = p.Positional(0, "data.csv");
        CliCommon.RequireFile(inPath, "input");

        var opt = new ImportOptions();
        if (p.Option("dt") is { } dt) opt.DtSec = ParseNum(dt, "--dt");
        if (p.Option("epoch") is { } ep) opt.Epoch = ep;
        if (p.Option("max-gap-s") is { } mg) opt.MaxGapSec = ParseNum(mg, "--max-gap-s");
        if (p.Option("geoid-offset-m") is { } go) opt.GeoidOffsetM = ParseNum(go, "--geoid-offset-m");
        if (p.Option("origin") is { } origin)
        {
            var parts = origin.Split(',');
            if (parts.Length != 3) throw new CliError("--origin must be LAT,LON,ALT (deg, deg, ellipsoidal m)");
            opt.OriginLatDeg = ParseNum(parts[0], "--origin lat");
            opt.OriginLonDeg = ParseNum(parts[1], "--origin lon");
            opt.OriginAltM = ParseNum(parts[2], "--origin alt");
        }
        string outPath = p.OptionAny("o", "out") ?? Path.ChangeExtension(inPath, ".tspi");

        ImportResult result;
        try
        {
            result = TspiImporter.Load(inPath, opt);
            byte[] raw = File.ReadAllBytes(inPath);
            TspiImporter.Write(outPath, result, ManifestJson.Sha256Bytes(raw), ManifestJson.Sha256Hex(raw),
                Path.GetFileName(inPath));
        }
        catch (ImportError ex)
        {
            throw new CliError(ex.Message);
        }

        if (!p.Switch("quiet"))
        {
            foreach (var w in result.Warnings) Console.WriteLine("warning: " + w);
            long samples = 0;
            foreach (var e in result.Entities) samples += e.Traj.Count;
            Console.WriteLine($"wrote {outPath}");
            Console.WriteLine($"  {result.Entities.Count} entities, {samples:N0} samples resampled from " +
                              $"{result.InputRows:N0} input rows, {new FileInfo(outPath).Length / 1024.0:N1} KiB");
            Console.WriteLine($"  dt {result.DtSec * 1000:0.###} ms ({(result.DtInferred ? "inferred" : "explicit")}), " +
                              $"origin {result.OriginLatDeg:0.######}, {result.OriginLonDeg:0.######}, " +
                              $"{result.OriginAltM:0.#} m ({(result.OriginFromData ? "first sample" : "explicit")})");
            Console.WriteLine($"  epoch {DateTimeOffset.FromUnixTimeMilliseconds(result.EpochUnixNs / 1_000_000L):u}, " +
                              $"dynamics {result.DynamicsTag}");
        }
        return 0;
    }

    private static double ParseNum(string s, string what)
    {
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            throw new CliError(what + " must be a number, got '" + s + "'");
        return v;
    }
}
