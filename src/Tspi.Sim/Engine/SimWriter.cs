using System;
using System.Collections.Generic;
using System.Linq;
using Tspi.Core.IO;
using Tspi.Sim.Models;

namespace Tspi.Sim.Engine;

/// <summary>Writes/append a SimResult to a .tspi, streaming records and stamping provenance.</summary>
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
        using var w = new TspiStreamWriter(path, header);
        foreach (var ent in result.Entities.OrderBy(e => Ord(result, e.Id)))
        {
            var meta = new TspiEntityEntry
            {
                Ord = Ord(result, ent.Id), Id = ent.Id, Team = ent.Team, Type = ent.Type,
                Model = ent.Model, ParentOrd = ent.ParentOrd, T0Ns = SceneEngine.SecToNs(ent.Traj.T0Sec),
            };
            w.WriteBlock(meta, ent.Traj.EnumerateRecords(), ent.Traj.Count);
        }
        w.AddEvents(result.Events);
        w.SetEnvironment(result.EnvironmentJson);
        w.AddProvenance(ProvenanceRecord(manifestShaHex, seed, models, "run", result.DynamicsTag));
        w.Finish();
    }

    public static void Append(string path, SimResult result, string addendumShaHex, ulong seed, ModelLibrary models)
    {
        var idToOrd = result.OrdToId.ToDictionary(kv => kv.Value, kv => kv.Key);
        var blocks = new List<TspiEntityBlock>();
        foreach (var ent in result.Entities)
        {
            blocks.Add(new TspiEntityBlock
            {
                Meta = new TspiEntityEntry
                {
                    Ord = idToOrd[ent.Id], Id = ent.Id, Team = ent.Team, Type = ent.Type, Model = ent.Model,
                    ParentOrd = ent.ParentOrd, T0Ns = SceneEngine.SecToNs(ent.Traj.T0Sec),
                },
                Records = ent.Traj.ToRecords(),
            });
        }
        // Append preserves the file's existing environment footer field automatically.
        TspiFile.Append(path, blocks.OrderBy(b => b.Meta.Ord).ToList(), result.Events,
            ProvenanceRecord(addendumShaHex, seed, models, "append"));
    }

    private static uint Ord(SimResult result, string id)
    {
        foreach (var kv in result.OrdToId) if (kv.Value == id) return kv.Key;
        throw new InvalidOperationException("no ord for entity '" + id + "'");
    }

    /// <summary>All aircraft attitude synthesized from the flight path (v1 default).</summary>
    public const string DynSynthAttitude = "kinematic-3dof+synth-attitude";
    /// <summary>All aircraft attitude integrated from rigid-body rotational EOM.</summary>
    public const string DynRigidAttitude = "kinematic-3dof+rigid-attitude";
    /// <summary>Scenario mixes synthesized- and rigid-attitude aircraft.</summary>
    public const string DynMixedAttitude = "kinematic-3dof+mixed-attitude";

    /// <summary>
    /// One provenance record per write/append. The `dynamics` tag is an honesty marker:
    /// translation is always kinematic point-mass; the attitude fragment says whether
    /// aircraft attitude was synthesized from the flight path or integrated from
    /// rigid-body rotational EOM (munition attitude is always velocity-aligned in v1),
    /// so consumers know what the stored 6-DoF-shaped records actually represent.
    /// </summary>
    public static Dictionary<string, object> ProvenanceRecord(string manifestShaHex, ulong seed,
        ModelLibrary models, string op, string dynamicsTag = DynSynthAttitude)
    {
        var modelHashes = new Dictionary<string, object>();
        foreach (var kv in models.LoadedHashes) modelHashes[kv.Key] = kv.Value;
        return new Dictionary<string, object>
        {
            { "op", op },
            { "sim_version", SimInfo.Version },
            { "dynamics", dynamicsTag },
            { "manifest_sha256", manifestShaHex },
            { "seed", (long)seed },
            { "models", modelHashes },
        };
    }
}
