using System;
using System.Collections.Generic;
using System.Globalization;
using Tspi.Core.IO;
using Tspi.Core.Math;

namespace Tspi.Cli.Commands;

/// <summary>
/// Compare two .tspi files sample-by-sample. Used as a determinism/regression check:
/// same manifest + seed + sim version should produce byte-identical files, but this
/// gives a tolerance-based comparison for cross-platform or cross-version checks.
/// </summary>
public static class DiffCommand
{
    public static int Run(string[] args)
    {
        var p = new ArgParse(args, new HashSet<string> { "quiet" });
        string aPath = p.Positional(0, "a.tspi");
        string bPath = p.Positional(1, "b.tspi");
        CliCommon.RequireFile(aPath, "file a");
        CliCommon.RequireFile(bPath, "file b");
        double tolM = double.Parse(p.Option("tol-m", "0"), CultureInfo.InvariantCulture);

        using var a = TspiReader.Open(aPath);
        using var b = TspiReader.Open(bPath);

        var problems = new List<string>();
        if (a.Entities.Count != b.Entities.Count)
            problems.Add($"entity count differs: {a.Entities.Count} vs {b.Entities.Count}");

        double maxPos = 0;
        long compared = 0;
        int n = System.Math.Min(a.Entities.Count, b.Entities.Count);
        for (int k = 0; k < n; k++)
        {
            var ea = a.Entities[k];
            var eb = b.FindEntity(ea.Id);
            if (eb == null) { problems.Add($"entity '{ea.Id}' missing in b"); continue; }
            if (ea.SampleCount != eb.SampleCount)
            {
                problems.Add($"'{ea.Id}' sample count differs: {ea.SampleCount} vs {eb.SampleCount}");
                continue;
            }
            if (ea.Layout != TspiFormat.LayoutSixDofV1) continue;
            for (long i = 0; i < ea.SampleCount; i++)
            {
                var ra = a.ReadSample(ea, i);
                var rb = b.ReadSample(eb, i);
                double d = Vec3d.Distance(ra.Pos, rb.Pos);
                if (d > maxPos) maxPos = d;
                compared++;
            }
        }

        bool identical = ByteIdentical(aPath, bPath);
        if (!p.Switch("quiet"))
        {
            Console.WriteLine($"compared {compared:N0} samples across {n} entities");
            Console.WriteLine($"  bytewise identical: {(identical ? "yes" : "no")}");
            Console.WriteLine($"  max position delta: {maxPos:E3} m");
        }
        foreach (var pr in problems) Console.Error.WriteLine("diff: " + pr);

        bool pass = problems.Count == 0 && maxPos <= tolM;
        if (!pass)
        {
            Console.Error.WriteLine($"DIFFER (max {maxPos:E3} m > tol {tolM} m)");
            return 1;
        }
        Console.WriteLine(identical ? "IDENTICAL" : $"WITHIN TOLERANCE ({tolM} m)");
        return 0;
    }

    private static bool ByteIdentical(string a, string b)
    {
        var fa = new System.IO.FileInfo(a);
        var fb = new System.IO.FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sa = fa.OpenRead();
        using var sb = fb.OpenRead();
        var ba = new byte[65536];
        var bb = new byte[65536];
        int na;
        while ((na = sa.Read(ba, 0, ba.Length)) > 0)
        {
            int off = 0;
            while (off < na)
            {
                int nb = sb.Read(bb, off, na - off);
                if (nb <= 0) return false;
                off += nb;
            }
            for (int i = 0; i < na; i++) if (ba[i] != bb[i]) return false;
        }
        return true;
    }
}
