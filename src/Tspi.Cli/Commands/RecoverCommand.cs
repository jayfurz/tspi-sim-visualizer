using System;
using System.Collections.Generic;
using Tspi.Core.IO;

namespace Tspi.Cli.Commands;

public static class RecoverCommand
{
    public static int Run(string[] args)
    {
        var p = new ArgParse(args, new HashSet<string> { "apply" });
        string file = p.Positional(0, "file.tspi");
        CliCommon.RequireFile(file, "trajectory file");

        var report = p.Switch("apply") ? TspiRecovery.Recover(file) : TspiRecovery.Inspect(file);
        Console.WriteLine(report.Message);
        Console.WriteLine($"  file length:      {report.FileLength:N0} B");
        Console.WriteLine($"  recovered length: {report.RecoveredLength:N0} B");
        if (report.TrailerValid) return 0;
        if (report.RecoveredLength == 0)
        {
            Console.Error.WriteLine("unrecoverable");
            return 2;
        }
        if (!p.Switch("apply"))
            Console.WriteLine("  (dry run — re-run with --apply to truncate torn bytes)");
        return 0;
    }
}
