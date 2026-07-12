using System;
using System.Collections.Generic;
using Tspi.Core.IO;
using Tspi.Core.Math;

namespace Tspi.Sim.Engine;

/// <summary>
/// Dense in-memory trajectory sampled at the scenario's fixed dt. Used both as the
/// simulation output staging area and as a target track that munitions fly against
/// (so the live and append paths share one guidance code path).
/// </summary>
public sealed class Trajectory
{
    public double T0Sec;
    public double DtSec;
    public readonly List<Vec3d> Pos = new();
    public readonly List<Vec3d> Vel = new();
    public readonly List<QuatD> Att = new();
    /// <summary>Integrated body rates, populated only by rigid-body dynamics (all samples or none).</summary>
    public readonly List<Vec3d> OmegaBody = new();

    public int Count => Pos.Count;
    public double EndSec => T0Sec + (Count - 1) * DtSec;
    /// <summary>True when every sample carries an integrated body rate (vs finite-differenced on write).</summary>
    public bool HasTrueRates => Count > 0 && OmegaBody.Count == Count;

    public void Add(Vec3d pos, Vec3d vel, QuatD att, Vec3d? omegaBody = null)
    {
        bool consistent = omegaBody != null ? OmegaBody.Count == Pos.Count : OmegaBody.Count == 0;
        if (!consistent)
            throw new InvalidOperationException("trajectory cannot mix integrated and synthesized body rates");
        Pos.Add(pos);
        Vel.Add(vel);
        Att.Add(att);
        if (omegaBody is { } w) OmegaBody.Add(w);
    }

    /// <summary>Hermite-interpolated pos/vel at tSec (clamped to the trajectory span).</summary>
    public MotionState SampleAt(double tSec)
    {
        if (Count == 0) return default;
        if (Count == 1) return new MotionState { Pos = Pos[0], Vel = Vel[0] };
        double u = (tSec - T0Sec) / DtSec;
        long i = (long)System.Math.Floor(u);
        if (i < 0) i = 0;
        if (i > Count - 2) i = Count - 2;
        u -= i;
        Vec3d p0 = Pos[(int)i], p1 = Pos[(int)i + 1];
        Vec3d v0 = Vel[(int)i], v1 = Vel[(int)i + 1];
        double dt = DtSec;
        double h00 = (2 * u - 3) * u * u + 1;
        double h10 = ((u - 2) * u + 1) * u;
        double h01 = (3 - 2 * u) * u * u;
        double h11 = (u - 1) * u * u;
        Vec3d pos = h00 * p0 + (h10 * dt) * v0 + h01 * p1 + (h11 * dt) * v1;
        double g00 = 6 * u * u - 6 * u, g10 = 3 * u * u - 4 * u + 1;
        double g01 = -6 * u * u + 6 * u, g11 = 3 * u * u - 2 * u;
        Vec3d vel = (g00 / dt) * p0 + g10 * v0 + (g01 / dt) * p1 + g11 * v1;
        return new MotionState { Pos = pos, Vel = vel };
    }

    /// <summary>Build fixed-stride records; body rates are the integrated ω when present,
    /// otherwise finite-differenced from attitude.</summary>
    public List<TspiRecord> ToRecords()
    {
        var records = new List<TspiRecord>(Count);
        for (int i = 0; i < Count; i++)
            records.Add(TspiRecord.From(Pos[i], Vel[i], Att[i].Normalized(), OmegaAt(i)));
        EnforceQuatSignContinuity(records);
        return records;
    }

    private Vec3d OmegaAt(int i)
    {
        if (HasTrueRates) return OmegaBody[i];
        if (Count == 1) return Vec3d.Zero;
        return i < Count - 1
            ? QuatD.BodyRates(Att[i], Att[i + 1], DtSec)
            : QuatD.BodyRates(Att[i - 1], Att[i], DtSec);
    }

    /// <summary>
    /// Lazily yield fixed-stride records with sign continuity applied on the fly, so the
    /// streaming writer never materializes a second full copy of the trajectory.
    /// Equivalent output to <see cref="ToRecords"/>.
    /// </summary>
    public IEnumerable<TspiRecord> EnumerateRecords()
    {
        bool hasPrev = false;
        float pw = 0, px = 0, py = 0, pz = 0;
        for (int i = 0; i < Count; i++)
        {
            var rec = TspiRecord.From(Pos[i], Vel[i], Att[i].Normalized(), OmegaAt(i));
            if (hasPrev)
            {
                double dot = pw * rec.QuatW + px * rec.QuatX + py * rec.QuatY + pz * rec.QuatZ;
                if (dot < 0)
                {
                    rec.QuatW = -rec.QuatW; rec.QuatX = -rec.QuatX;
                    rec.QuatY = -rec.QuatY; rec.QuatZ = -rec.QuatZ;
                }
            }
            pw = rec.QuatW; px = rec.QuatX; py = rec.QuatY; pz = rec.QuatZ;
            hasPrev = true;
            yield return rec;
        }
    }

    /// <summary>Flip signs so dot(q_i, q_i+1) >= 0 — playback slerp then never takes the long path.</summary>
    private static void EnforceQuatSignContinuity(List<TspiRecord> recs)
    {
        for (int i = 1; i < recs.Count; i++)
        {
            var prev = recs[i - 1];
            var cur = recs[i];
            double dot = prev.QuatW * cur.QuatW + prev.QuatX * cur.QuatX +
                         prev.QuatY * cur.QuatY + prev.QuatZ * cur.QuatZ;
            if (dot < 0)
            {
                cur.QuatW = -cur.QuatW; cur.QuatX = -cur.QuatX;
                cur.QuatY = -cur.QuatY; cur.QuatZ = -cur.QuatZ;
                recs[i] = cur;
            }
        }
    }
}

/// <summary>A simulated entity: identity + its dense trajectory.</summary>
public sealed class EntityTrajectory
{
    public string Id = "";
    public string Team = "gray";
    public string Type = "aircraft";
    public string Model = "";
    public uint? ParentOrd;
    public Trajectory Traj = new();
}

/// <summary>Target track a munition flies against — backed by memory (live) or a file (append).</summary>
public interface ITargetTrack
{
    MotionState SampleAt(double tSec);
    double StartSec { get; }
    double EndSec { get; }
}

public sealed class MemTargetTrack : ITargetTrack
{
    private readonly Trajectory _t;
    public MemTargetTrack(Trajectory t) { _t = t; }
    public MotionState SampleAt(double tSec) => _t.SampleAt(tSec);
    public double StartSec => _t.T0Sec;
    public double EndSec => _t.EndSec;
}

/// <summary>Target track backed by a recorded entity in a .tspi (the append path).</summary>
public sealed class ReaderTargetTrack : ITargetTrack
{
    private readonly TspiReader _reader;
    private readonly TspiEntityEntry _entity;
    public ReaderTargetTrack(TspiReader reader, TspiEntityEntry entity) { _reader = reader; _entity = entity; }
    public double StartSec => _reader.StartSec(_entity);
    public double EndSec => _reader.EndSec(_entity);
    public MotionState SampleAt(double tSec)
    {
        _reader.TrySampleAt(_entity, tSec, out var s, clamp: true);
        return new MotionState { Pos = s.PosNed, Vel = s.VelNed };
    }
}
