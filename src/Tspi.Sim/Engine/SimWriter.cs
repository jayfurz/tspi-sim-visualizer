using System;
using System.Collections.Generic;
using System.Linq;
using Tspi.Core.IO;
using Tspi.Core.Math;
using Tspi.Sim.Manifest;
using Tspi.Sim.Models;

namespace Tspi.Sim.Engine;

/// <summary>Turns a SimResult into .tspi blocks and writes/append the file, stamping provenance.</summary>
public static class SimWriter
{
    public static void WriteNew(string path, SimResult result, byte[] manifestSha256Bytes,
        string manifestShaHex, ulong seed, ModelLibrary models)
    {
        var header = new TspiHeader
        {
            DtNs = (ulong)SceneEngine.SecToNs(result.DtSec),
            EpochUnixNs = result.EpochUnixNs,
            OriginLatDeg = result.Origin.LatDeg,
            OriginLonDeg = result.Origin.LonDeg,
            OriginAltM = result.Origin.AltM,
            ManifestSha256 = manifestSha256Bytes,
        };
        var blocks = BuildBlocks(result);
        var provenance = Provenance(manifestShaHex, seed, models, "run");
        TspiFile.WriteNew(path, header, blocks, result.Events, new[] { provenance });
    }

    public static void Append(string path, SimResult result, string addendumShaHex, ulong seed, ModelLibrary models)
    {
        var blocks = BuildBlocks(result);
        var provenance = Provenance(addendumShaHex, seed, models, "append");
        TspiFile.Append(path, blocks, result.Events, provenance);
    }

    private static List<TspiEntityBlock> BuildBlocks(SimResult result)
    {
        // Ord is assigned in the order entities were produced (aircraft first, then munitions),
        // matching SimResult.OrdToId.
        var idToOrd = result.OrdToId.ToDictionary(kv => kv.Value, kv => kv.Key);
        var blocks = new List<TspiEntityBlock>();
        foreach (var ent in result.Entities)
        {
            uint ord = idToOrd[ent.Id];
            var block = new TspiEntityBlock
            {
                Meta = new TspiEntityEntry
                {
                    Ord = ord,
                    Id = ent.Id,
                    Team = ent.Team,
                    Type = ent.Type,
                    Model = ent.Model,
                    ParentOrd = ent.ParentOrd,
                    T0Ns = SceneEngine.SecToNs(ent.Traj.T0Sec),
                },
                Records = ent.Traj.ToRecords(),
            };
            blocks.Add(block);
        }
        return blocks.OrderBy(b => b.Meta.Ord).ToList();
    }

    private static Dictionary<string, object> Provenance(string manifestShaHex, ulong seed, ModelLibrary models, string op)
    {
        var modelHashes = new Dictionary<string, object>();
        foreach (var kv in models.LoadedHashes) modelHashes[kv.Key] = kv.Value;
        return new Dictionary<string, object>
        {
            { "op", op },
            { "sim_version", SimInfo.Version },
            { "manifest_sha256", manifestShaHex },
            { "seed", (long)seed },
            { "models", modelHashes },
        };
    }
}
