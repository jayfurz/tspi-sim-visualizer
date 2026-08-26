using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Tspi.Sim.Live;

namespace Tspi.Cli.Commands;

/// <summary>
/// Record a live stream (tools/live-stream/PROTOCOL.md) into a .tspi.
///
/// Streaming producers keep no history, so without this a live engagement is gone the
/// moment it scrolls past. `tspi record` subscribes like any other viewer and lands the
/// run in the container, where the rest of the toolchain already works: play it back,
/// `tspi diff` it, `tspi append` munitions against it, analyse it with tools/tspi_py.
/// Nothing is re-simulated — the stream already carries the file format's own records.
/// </summary>
public static class RecordCommand
{
    public static int Run(string[] args)
    {
        var p = new ArgParse(args, new HashSet<string> { "quiet" });
        string url = p.Positional(0, "ws://host:port/stream");
        bool quiet = p.Switch("quiet");

        var opt = new LiveRecordOptions
        {
            Url = url,
            OutPath = p.OptionAny("o", "out") ??
                Path.Combine("runs", "live-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".tspi"),
        };
        if (p.Option("duration") is { } d) opt.DurationSec = ParseNum(d, "--duration");
        if (p.Option("spool-dir") is { } sd) opt.SpoolDir = sd;

        // Ctrl-C is the normal way to stop an open-ended recording: cancel the receive
        // loop so the file is finished and closed rather than left as a torn write.
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;                     // we handle the stop; don't kill the process
            if (!cts.IsCancellationRequested)
            {
                if (!quiet) Console.Error.WriteLine("stopping — writing the file…");
                cts.Cancel();
            }
        };
        Console.CancelKeyPress += onCancel;

        LiveRecordResult result;
        try
        {
            result = LiveRecorder.RecordAsync(opt, cts.Token,
                quiet ? null : msg => Console.Error.WriteLine(msg)).GetAwaiter().GetResult();
        }
        catch (LiveRecordError ex)
        {
            throw new CliError(ex.Message);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        foreach (var w in result.Warnings) Console.Error.WriteLine("warning: " + w);
        if (!quiet)
        {
            Console.WriteLine($"wrote {result.OutPath}");
            Console.WriteLine($"  {result.Entities} entities, {result.Samples:N0} samples, " +
                              $"{result.Events} events, {new FileInfo(result.OutPath).Length / 1024.0:N1} KiB");
            Console.WriteLine($"  dt {result.DtSec * 1000:0.###} ms, " +
                              $"t=[{result.SpanStartSec:0.00}, {result.SpanEndSec:0.00}]s, " +
                              $"dynamics {result.DynamicsTag}");
            Console.WriteLine($"  stopped: {result.StopReason}" +
                              (result.GapsFilled > 0 ? $" · {result.GapsFilled:N0} samples filled over dropped frames" : "") +
                              (result.RecordsDropped > 0 ? $" · {result.RecordsDropped:N0} records dropped" : ""));
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
