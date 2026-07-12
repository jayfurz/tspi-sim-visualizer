using System;
using System.Collections.Generic;
using Tspi.Core.IO;

namespace Tspi.Cli.Commands;

public static class InspectCommand
{
    public static int Run(string[] args)
    {
        var p = new ArgParse(args, new HashSet<string> { "events", "provenance", "chain" });
        string path = p.Positional(0, "file.tspi");
        CliCommon.RequireFile(path, "trajectory file");

        using var reader = TspiReader.Open(path);
        var h = reader.Header;
        Console.WriteLine($"file:    {path} ({reader.FileLength / 1024.0:N1} KiB)");
        Console.WriteLine($"format:  v{h.Version}  dt={h.DtSec * 1000:0.###} ms ({1.0 / h.DtSec:0} Hz)");
        Console.WriteLine($"origin:  lat {h.OriginLatDeg:0.######}  lon {h.OriginLonDeg:0.######}  alt {h.OriginAltM:0.#} m");
        Console.WriteLine($"epoch:   {DateTimeOffset.FromUnixTimeMilliseconds(h.EpochUnixNs / 1_000_000L):u}");
        Console.WriteLine($"manifest sha256: {Convert.ToHexString(h.ManifestSha256).ToLowerInvariant()[..16]}...");
        Console.WriteLine($"footer:  offset {reader.FooterOffset}  len {reader.FooterLen} B");
        Console.WriteLine();
        Console.WriteLine($"entities ({reader.Entities.Count}):");
        Console.WriteLine($"  {"ord",3}  {"id",-18} {"team",-5} {"type",-9} {"t0",8} {"end",8} {"samples",9}  parent");
        foreach (var e in reader.Entities)
        {
            string parent = e.ParentOrd.HasValue ? e.ParentOrd.Value.ToString() : "-";
            Console.WriteLine($"  {e.Ord,3}  {e.Id,-18} {e.Team,-5} {e.Type,-9} " +
                              $"{reader.StartSec(e),8:0.00} {reader.EndSec(e),8:0.00} {e.SampleCount,9:N0}  {parent}");
        }

        if (p.Switch("events") || reader.Events.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"events ({reader.Events.Count}):");
            foreach (var ev in reader.Events)
                Console.WriteLine($"  t={ev.TNs / 1e9,8:0.000}s  {ev.Kind,-14} {NameOf(reader, ev.SrcOrd)}" +
                                  (ev.DstOrd.HasValue ? " -> " + NameOf(reader, ev.DstOrd) : "") +
                                  DataStr(ev.Data));
        }

        if (p.Switch("provenance"))
        {
            Console.WriteLine();
            Console.WriteLine($"provenance ({reader.Footer.Provenance.Count} record(s)):");
            foreach (var rec in reader.Footer.Provenance)
                Console.WriteLine("  " + Tspi.Core.Json.MiniJson.Serialize(rec));
        }

        if (p.Switch("chain"))
        {
            var chain = reader.ReadFooterChain();
            Console.WriteLine();
            Console.WriteLine($"footer chain: {chain.Count} snapshot(s) (newest first)");
            for (int i = 0; i < chain.Count; i++)
                Console.WriteLine($"  [{i}] {chain[i].Entities.Count} entities, {chain[i].Events.Count} events");
        }
        return 0;
    }

    private static string NameOf(TspiReader r, uint? ord)
    {
        if (!ord.HasValue) return "";
        foreach (var e in r.Entities) if (e.Ord == ord.Value) return e.Id;
        return "ord" + ord.Value;
    }

    private static string DataStr(Dictionary<string, object> data)
    {
        if (data.Count == 0) return "";
        var parts = new List<string>();
        foreach (var kv in data) parts.Add($"{kv.Key}={kv.Value}");
        return "  {" + string.Join(", ", parts) + "}";
    }
}
