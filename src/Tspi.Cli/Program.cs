using System;
using Tspi.Cli.Commands;

namespace Tspi.Cli;

/// <summary>
/// tspi — headless CLI for the TSPI simulator. All simulation happens here, never in
/// Unity; the viewer is a pure playback client of the .tspi files this produces.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0) { PrintUsage(); return 1; }
        string verb = args[0];
        var rest = args[1..];
        try
        {
            return verb switch
            {
                "validate" => ValidateCommand.Run(rest),
                "run" => RunCommand.Run(rest),
                "sweep" => SweepCommand.Run(rest),
                "append" => AppendCommand.Run(rest),
                "import" => ImportCommand.Run(rest),
                "inspect" => InspectCommand.Run(rest),
                "recover" => RecoverCommand.Run(rest),
                "export" => ExportCommand.Run(rest),
                "diff" => DiffCommand.Run(rest),
                "-h" or "--help" or "help" => PrintUsageReturn(),
                _ => Unknown(verb),
            };
        }
        catch (CliError ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            Console.Error.WriteLine(ex.StackTrace);
            return 3;
        }
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine("error: unknown command '" + verb + "'");
        PrintUsage();
        return 1;
    }

    private static int PrintUsageReturn() { PrintUsage(); return 0; }

    private static void PrintUsage()
    {
        Console.WriteLine(@"tspi — aircraft/munitions TSPI simulator (v" + Tspi.Sim.SimInfo.Version + @")

usage: tspi <command> [options]

  validate <scenario.json>                  schema + semantic checks
  run <scenario.json> [-o out.tspi] [--seed N] [--models DIR]
  sweep <scenario.json> --seeds A:B [-j N] [--out-dir DIR] [--models DIR]
  append <file.tspi> <addendum.json> [--seed N] [--models DIR]
  import <data.csv> [-o out.tspi] [--dt S] [--origin LAT,LON,ALT] [--epoch ISO8601]
         [--max-gap-s S] [--geoid-offset-m M]  measured TSPI -> .tspi (resampled to fixed dt)
  inspect <file.tspi> [--events] [--provenance] [--chain]
  recover <file.tspi> [--apply]             repair a torn append (dry-run without --apply)
  export <file.tspi> --format csv [--entity ID] [-o out.csv]
  diff <a.tspi> <b.tspi> [--tol-m M]        compare two runs (determinism check)

Models are resolved as <name>.json from --models dirs (default: ./models).");
    }
}

public sealed class CliError : Exception
{
    public CliError(string message) : base(message) { }
}
