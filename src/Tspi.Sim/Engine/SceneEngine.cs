using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tspi.Core.IO;
using Tspi.Core.Math;
using Tspi.Sim.Manifest;
using Tspi.Sim.Models;

namespace Tspi.Sim.Engine;

public sealed class SimResult
{
    public List<EntityTrajectory> Entities = new();
    public List<TspiEventEntry> Events = new();
    public long EpochUnixNs;
    public double DtSec;
    public OriginLla Origin = new();
    public Dictionary<uint, string> OrdToId = new();
}

/// <summary>
/// The deterministic simulation core. Aircraft integrate first (independent, scripted);
/// munitions then fly proportional-navigation against the recorded target tracks. That
/// ordering makes munition guidance identical whether the target track is in memory
/// (full scenario) or read back from a .tspi (later addendum) — the O(T_new) append.
/// </summary>
public static class SceneEngine
{
    public static SimResult RunScenario(ScenarioManifest m, ModelLibrary models)
    {
        double dt = m.Scene.DtS;
        int steps = (int)System.Math.Round(m.Scene.DurationS / dt);
        var result = new SimResult
        {
            EpochUnixNs = ParseEpochNs(m.Scene.Epoch),
            DtSec = dt,
            Origin = m.Scene.OriginLla,
        };
        var env = new Environment(m.Scene);

        // Ord assignment: aircraft in declaration order, then munitions.
        uint nextOrd = 0;
        var aircraftTracks = new Dictionary<string, Trajectory>();
        var aircraftOrd = new Dictionary<string, uint>();

        foreach (var e in m.Entities)
        {
            models.TryResolve(e.Model, out var model, out _, out _);
            var traj = IntegrateAircraft(e, model!, env, m.Seed, dt, steps, m.Scene.OriginLla.AltM);
            uint ord = nextOrd++;
            aircraftTracks[e.Id] = traj;
            aircraftOrd[e.Id] = ord;
            result.OrdToId[ord] = e.Id;
            result.Entities.Add(new EntityTrajectory
            {
                Id = e.Id, Team = e.Team, Type = e.Type, Model = e.Model, ParentOrd = null, Traj = traj,
            });
        }

        // Munitions.
        foreach (var e in m.Entities)
        {
            foreach (var mun in e.Munitions)
            {
                if (mun.Launch == null) continue; // carried, never employed
                models.TryResolve(mun.Model, out var mmodel, out _, out _);
                var parentTrack = new MemTargetTrack(aircraftTracks[e.Id]);
                var targetTrack = new MemTargetTrack(aircraftTracks[mun.Target]);
                double? launchT = ResolveLaunchTime(mun.Launch, parentTrack, targetTrack, dt, 0.0, m.Scene.DurationS);
                if (launchT == null) continue; // condition never met within the scenario

                uint ord = nextOrd++;
                var (traj, events) = IntegrateMunition(
                    mun, mmodel!, env, m.Seed, dt, launchT.Value,
                    parentTrack, targetTrack,
                    ord, aircraftOrd[mun.Target], m.Scene.OriginLla.AltM, m.Scene.DurationS);
                result.OrdToId[ord] = mun.Id;
                result.Entities.Add(new EntityTrajectory
                {
                    Id = mun.Id, Team = e.Team, Type = "munition", Model = mun.Model,
                    ParentOrd = aircraftOrd[e.Id], Traj = traj,
                });
                result.Events.AddRange(events);
            }
        }

        result.Events = result.Events.OrderBy(ev => ev.TNs).ToList();
        return result;
    }

    /// <summary>
    /// Fly addendum munitions against entities already recorded in an open .tspi, then
    /// return only the new munition trajectories/events (the caller appends them). Cost is
    /// O(sum of new munition samples) — recorded aircraft are read, never re-simulated.
    /// </summary>
    public static SimResult RunAddendum(TspiReader reader, AddendumManifest a, ModelLibrary models)
    {
        double dt = reader.DtSec;
        double durationS = 0.0;
        foreach (var e in reader.Entities)
            durationS = System.Math.Max(durationS, reader.EndSec(e));

        var result = new SimResult
        {
            EpochUnixNs = reader.Header.EpochUnixNs,
            DtSec = dt,
            Origin = new OriginLla
            {
                LatDeg = reader.Header.OriginLatDeg,
                LonDeg = reader.Header.OriginLonDeg,
                AltM = reader.Header.OriginAltM,
            },
        };
        var scene = new SceneSpec { DtS = dt, DurationS = durationS, OriginLla = result.Origin };
        var env = new Environment(scene); // recorded files carry no wind spec; addendum flies in still air unless extended

        uint nextOrd = 0;
        foreach (var e in reader.Entities) nextOrd = System.Math.Max(nextOrd, e.Ord + 1);

        foreach (var mun in a.Munitions)
        {
            var parent = reader.FindEntity(mun.Parent)
                ?? throw new InvalidOperationException($"addendum parent '{mun.Parent}' not found in file");
            var target = reader.FindEntity(mun.Target)
                ?? throw new InvalidOperationException($"addendum target '{mun.Target}' not found in file");
            models.TryResolve(mun.Model, out var mmodel, out _, out _);

            var parentTrack = new ReaderTargetTrack(reader, parent);
            var targetTrack = new ReaderTargetTrack(reader, target);
            double? launchT = ResolveLaunchTime(mun.Launch!, parentTrack, targetTrack, dt, 0.0, durationS);
            if (launchT == null) continue;

            uint ord = nextOrd++;
            var munSpec = new MunitionSpec
            {
                Id = mun.Id, Model = mun.Model, Target = mun.Target, Launch = mun.Launch, Guidance = mun.Guidance,
            };
            var (traj, events) = IntegrateMunition(
                munSpec, mmodel!, env, a.Seed, dt, launchT.Value,
                parentTrack, targetTrack, ord, target.Ord, result.Origin.AltM, durationS);
            result.OrdToId[ord] = mun.Id;
            result.Entities.Add(new EntityTrajectory
            {
                Id = mun.Id, Team = FindTeam(reader, mun.Parent), Type = "munition",
                Model = mun.Model, ParentOrd = parent.Ord, Traj = traj,
            });
            result.Events.AddRange(events);
        }

        result.Events = result.Events.OrderBy(ev => ev.TNs).ToList();
        return result;
    }

    private static string FindTeam(TspiReader reader, string id)
    {
        var e = reader.FindEntity(id);
        return e?.Team ?? "gray";
    }

    // ---------------- aircraft ----------------

    private static Trajectory IntegrateAircraft(EntitySpec e, VehicleModel model, Environment env,
        ulong seed, double dt, int steps, double originAltM)
    {
        var init = ApplyDispersions(e, seed);
        var state = new MotionState { Pos = init.Pos, Vel = init.Vel };
        double heading0 = state.Vel.LengthHorizontal > 1e-3
            ? System.Math.Atan2(state.Vel.Y, state.Vel.X) : 0.0;
        var dyn = new AircraftDynamics(model, state.Vel.Length, heading0);

        // Optional explicit initial attitude only affects the first sample's synthesized
        // attitude via bank; flight-path yaw/pitch come from velocity. Segments sorted by time.
        var segments = e.Maneuvers.OrderBy(s => s.AtS).ToList();
        int segIdx = 0;

        var traj = new Trajectory { T0Sec = 0.0, DtSec = dt };
        var windSampler = env.CreateSampler(new RngStream(seed, "wind:" + e.Id));

        for (int i = 0; i <= steps; i++)
        {
            double t = i * dt;
            while (segIdx < segments.Count && segments[segIdx].AtS <= t + 1e-9)
            {
                var seg = segments[segIdx++];
                double curHeading = state.Vel.LengthHorizontal > 1e-3
                    ? System.Math.Atan2(state.Vel.Y, state.Vel.X) : heading0;
                double curAlt = originAltM - state.Pos.Z;
                dyn.SetSpeed(seg.Speed);
                dyn.SetLateral(seg.Lateral, curHeading);
                dyn.SetVertical(seg.Vertical, curAlt);
            }

            dyn.Acceleration(state, originAltM); // sets LastBankRad for attitude synthesis
            traj.Add(state.Pos, state.Vel, dyn.Attitude(state));

            if (i == steps) break;
            state = Rk4(state, t, dt, (tt, s) =>
            {
                Vec3d wind = windSampler.Wind(originAltM - s.Pos.Z);
                // Autopilot commands airspeed relative to the air mass; wind biases ground track.
                var air = new MotionState { Pos = s.Pos, Vel = s.Vel - wind };
                return dyn.Acceleration(air, originAltM);
            });
            windSampler.Step(dt);
        }
        return traj;
    }

    // ---------------- munition ----------------

    private static (Trajectory, List<TspiEventEntry>) IntegrateMunition(
        MunitionSpec mun, VehicleModel model, Environment env, ulong seed, double dt, double launchSec,
        ITargetTrack parentTrack, ITargetTrack targetTrack, uint ord, uint targetOrd,
        double originAltM, double durationS)
    {
        var events = new List<TspiEventEntry>();
        var start = parentTrack.SampleAt(launchSec);
        var state = new MotionState { Pos = start.Pos, Vel = start.Vel };
        bool guided = (mun.Guidance?.Kind ?? "pronav") == "pronav";
        double navGain = mun.Guidance?.Gain ?? model.PronavGainDefault;
        var dyn = new MunitionDynamics(model, navGain, launchSec, guided);
        var windSampler = env.CreateSampler(new RngStream(seed, "wind:" + mun.Id));

        var traj = new Trajectory { T0Sec = launchSec, DtSec = dt };
        events.Add(new TspiEventEntry
        {
            TNs = SecToNs(launchSec), Kind = "launch", SrcOrd = ord, DstOrd = targetOrd,
        });

        double prevRange = double.MaxValue;
        double bestRange = double.MaxValue;
        double bestRangeT = launchSec;
        string terminal = "expire";
        double endT = System.Math.Min(durationS, launchSec + model.MaxFlightTimeS);

        double t = launchSec;
        int guard = 0;
        int maxSteps = (int)System.Math.Round((endT - launchSec) / dt) + 2;
        while (t <= endT + 1e-9 && guard++ <= maxSteps)
        {
            var tgtNow = targetTrack.SampleAt(t);
            double range = (tgtNow.Pos - state.Pos).Length;
            var att = dyn.Attitude(state);
            traj.Add(state.Pos, state.Vel, att);

            if (range < bestRange) { bestRange = range; bestRangeT = t; }

            // Endgame termination applies only to guided munitions; an unguided
            // projectile is not "intercepting" and flies until ground/expire.
            if (dyn.Guided)
            {
                // Fuze: intercept when inside lethal radius.
                if (range <= model.FuzeRadiusM)
                {
                    terminal = "intercept";
                    events.Add(MakeCpa(ord, targetOrd, t, range));
                    events.Add(new TspiEventEntry { TNs = SecToNs(t), Kind = "intercept", SrcOrd = ord, DstOrd = targetOrd,
                        Data = new Dictionary<string, object> { { "miss_m", System.Math.Round(range, 3) } } });
                    break;
                }
                // CPA passed (range began increasing after closing): record miss, terminate.
                if (range > prevRange && guard > 2)
                {
                    terminal = "miss";
                    events.Add(MakeCpa(ord, targetOrd, bestRangeT, bestRange));
                    break;
                }
                prevRange = range;
            }

            // Ground impact (flat-earth MSL at origin altitude).
            double altMsl = originAltM - state.Pos.Z;
            if (altMsl <= 0.0 && t > launchSec)
            {
                terminal = "ground_impact";
                events.Add(new TspiEventEntry { TNs = SecToNs(t), Kind = "ground_impact", SrcOrd = ord });
                break;
            }

            if (t >= endT - 1e-9) { terminal = "expire"; break; }

            double rho = env.Density(state.Pos.Z, originAltM);
            state = Rk4(state, t, dt, (tt, s) =>
            {
                Vec3d wind = windSampler.Wind(originAltM - s.Pos.Z);
                var air = new MotionState { Pos = s.Pos, Vel = s.Vel - wind };
                var tgt = targetTrack.SampleAt(tt);
                return dyn.Acceleration(tt, air, tgt, env.Density(s.Pos.Z, originAltM))
                       + wind * 0.0; // wind enters via air-relative drag; ground-frame accel unchanged
            });
            windSampler.Step(dt);
            t += dt;
        }

        if (terminal == "expire")
            events.Add(new TspiEventEntry { TNs = SecToNs(System.Math.Min(t, endT)), Kind = "expire", SrcOrd = ord });
        return (traj, events);
    }

    private static TspiEventEntry MakeCpa(uint ord, uint targetOrd, double tSec, double rangeM) => new()
    {
        TNs = SecToNs(tSec), Kind = "cpa", SrcOrd = ord, DstOrd = targetOrd,
        Data = new Dictionary<string, object> { { "miss_m", System.Math.Round(rangeM, 3) } },
    };

    // ---------------- launch resolution ----------------

    private static double? ResolveLaunchTime(LaunchSpec launch, ITargetTrack parent, ITargetTrack target,
        double dt, double t0, double t1)
    {
        // Only consider times where both tracks exist.
        double lo = System.Math.Max(t0, System.Math.Max(parent.StartSec, target.StartSec));
        double hi = System.Math.Min(t1, System.Math.Min(parent.EndSec, target.EndSec));
        switch (launch)
        {
            case LaunchAtTime lt:
                return lt.AtS >= lo - 1e-9 && lt.AtS <= hi + 1e-9 ? lt.AtS : (double?)null;
            case LaunchAtRange lr:
                int n = (int)System.Math.Round((hi - lo) / dt);
                for (int i = 0; i <= n; i++)
                {
                    double t = lo + i * dt;
                    double range = (target.SampleAt(t).Pos - parent.SampleAt(t).Pos).Length;
                    if (range <= lr.LessThanM) return t;
                }
                return null;
            default:
                return null;
        }
    }

    // ---------------- helpers ----------------

    private static (Vec3d Pos, Vec3d Vel) ApplyDispersions(EntitySpec e, ulong seed)
    {
        var pos = new Vec3d(e.Initial.PosNedM[0], e.Initial.PosNedM[1], e.Initial.PosNedM[2]);
        var vel = new Vec3d(e.Initial.VelNedMps[0], e.Initial.VelNedMps[1], e.Initial.VelNedMps[2]);
        if (e.Dispersions is { } d)
        {
            var rng = new RngStream(seed, "disp:" + e.Id);
            if (d.PosNedSigmaM is { Length: 3 } ps)
                pos += new Vec3d(ps[0] * rng.NextGaussian(), ps[1] * rng.NextGaussian(), ps[2] * rng.NextGaussian());
            if (d.VelNedSigmaMps is { Length: 3 } vs)
                vel += new Vec3d(vs[0] * rng.NextGaussian(), vs[1] * rng.NextGaussian(), vs[2] * rng.NextGaussian());
        }
        return (pos, vel);
    }

    /// <summary>Classic RK4 for pos/vel with acceleration a(t, state).</summary>
    private static MotionState Rk4(MotionState s, double t, double dt, Func<double, MotionState, Vec3d> accel)
    {
        MotionState D(double tt, MotionState st) => new() { Pos = st.Vel, Vel = accel(tt, st) };
        MotionState k1 = D(t, s);
        MotionState k2 = D(t + dt / 2, s + k1 * (dt / 2));
        MotionState k3 = D(t + dt / 2, s + k2 * (dt / 2));
        MotionState k4 = D(t + dt, s + k3 * dt);
        return s + (k1 + k2 * 2 + k3 * 2 + k4) * (dt / 6);
    }

    public static long ParseEpochNs(string iso)
    {
        var dto = DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return dto.ToUnixTimeMilliseconds() * 1_000_000L;
    }

    public static long SecToNs(double sec) => (long)System.Math.Round(sec * 1e9);
}
