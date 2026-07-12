using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tspi.Core.IO;
using Tspi.Core.Math;

namespace Tspi.Cli.Commands;

/// <summary>
/// CSV export — the zero-dependency analysis bridge. (Parquet/Arrow export belongs in
/// the Python tool where the ecosystem lives; this keeps the C# side dependency-free.)
/// </summary>
public static class ExportCommand
{
    public static int Run(string[] args)
    {
        var p = new ArgParse(args, new HashSet<string>());
        string file = p.Positional(0, "file.tspi");
        CliCommon.RequireFile(file, "trajectory file");
        string format = p.Option("format", "csv");
        if (format != "csv")
            throw new CliError("only --format csv is supported by the CLI; use tools/tspi_py for parquet/arrow");

        using var reader = TspiReader.Open(file);
        string? onlyId = p.Option("entity");
        string outPath = p.OptionAny("o", "out") ?? Path.ChangeExtension(file, ".csv");

        using var writer = new StreamWriter(outPath, false, new UTF8Encoding(false));
        writer.WriteLine("entity,team,type,t_s,pos_n_m,pos_e_m,pos_d_m,vel_n,vel_e,vel_d,qw,qx,qy,qz,wx,wy,wz");
        long rows = 0;
        foreach (var e in reader.Entities)
        {
            if (onlyId != null && e.Id != onlyId) continue;
            if (e.Layout != TspiFormat.LayoutSixDofV1) continue;
            for (long i = 0; i < e.SampleCount; i++)
            {
                var rec = reader.ReadSample(e, i);
                double t = (e.T0Ns + i * (long)reader.Header.DtNs) / 1e9;
                writer.Write(e.Id); writer.Write(',');
                writer.Write(e.Team); writer.Write(',');
                writer.Write(e.Type); writer.Write(',');
                W(writer, t);
                W(writer, rec.PosN); W(writer, rec.PosE); W(writer, rec.PosD);
                W(writer, rec.VelN); W(writer, rec.VelE); W(writer, rec.VelD);
                W(writer, rec.QuatW); W(writer, rec.QuatX); W(writer, rec.QuatY); W(writer, rec.QuatZ);
                W(writer, rec.OmegaX); W(writer, rec.OmegaY);
                writer.Write(rec.OmegaZ.ToString("R", CultureInfo.InvariantCulture));
                writer.Write('\n');
                rows++;
            }
        }
        Console.WriteLine($"wrote {outPath} ({rows:N0} rows)");
        return 0;
    }

    private static void W(TextWriter w, double v)
    {
        w.Write(v.ToString("R", CultureInfo.InvariantCulture));
        w.Write(',');
    }
}
