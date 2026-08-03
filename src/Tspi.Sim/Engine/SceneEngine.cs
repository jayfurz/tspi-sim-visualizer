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
    /// <summary>Opaque environment descriptor persisted into the file footer (null if none).</summary>
    public Dictionary<string, object>? EnvironmentJson;
    /// <summary>Honesty tag for provenance: which attitude fidelity produced these records.</summary>
    public string DynamicsTag = SimWriter.DynSynthAttitude;
}

/// <summary>Lightweight result of a streamed run — no trajectories retained (they were written and dropped).</summary>
public sealed class RunSummary
{
    public int EntityCount;
    public long SampleCount;
    public List<TspiEventEntry> Events = new();
    public Dictionary<uint, string> OrdToId = new();
}

/// <summary>One produced entity: identity + trajectory + its events. The single source of
/// truth for ordering/ord assignment, consumed by both the in-memory and streaming paths.</summary>
internal struct Produced
{
    public string Id, Team, Type, Model;
    public uint Ord;
    public uint? ParentOrd;
    public Trajectory Traj;
    public List<TspiEventEntry> Events;
}

/// <summary>
/// The deterministic simulation core. Aircraft integrate first (independent, scripted);
/// munitions then fly proportional-navigation against the recorded target tracks. That
/// ordering makes munition guidance identical whether the target track is in memory
/// (full scenario) or read back from a .tspi (later addendum) — the O(T_new) append.
/// </summary>
public static class SceneEngine
{
    /// <summary>Run a scenario fully into memory (used by tests and small in-process consumers).</summary>
    public static SimResult RunScenario(ScenarioManifest m, ModelLibrary models)
    {
        double dt = m.Scene.DtS;
        int steps = (int)System.Math.Round(m.Scene.DurationS / dt);
        var result = new SimResult
        {
            EpochUnixNs = ParseEpochNs(m.Scene.Epoch),
            DtSec = dt,
            Origin = m.Scene.OriginLla,
            EnvironmentJson = EnvironmentSerialization.ToJson(m.Scene.Environment),
            DynamicsTag = DynamicsTag(m, models),
        };
        var env = new Environment(m.Scene);
        foreach (var p in Produce(m, models, env, dt, steps))
        {
            result.OrdToId[p.Ord] = p.Id;
            result.Entities.Add(new EntityTrajectory
            {
                Id = p.Id, Team = p.Team, Type = p.Type, Model = p.Model, ParentOrd = p.ParentOrd, Traj = p.Traj,
            });
            result.Events.AddRange(p.Events);
        }
        result.Events = result.Events.OrderBy(ev => ev.TNs).ToList();
        return result;
    }

    /// <summary>
    /// Run a scenario streaming each entity's block straight to <paramref name="path"/> as it
    /// is integrated, so munition trajectories are written and dropped rather than all held at
    /// once. Aircraft tracks stay resident (munitions guide against them); a fully-streaming
    /// sim would re-read them from the file via mmap, at the cost of a second footer.
    /// </summary>
    public static RunSummary RunScenarioToFile(ScenarioManifest m, ModelLibrary models, string path,
        byte[] manifestSha256Bytes, string manifestShaHex)
    {
        double dt = m.Scene.DtS;
        int steps = (int)System.Math.Round(m.Scene.DurationS / dt);
        var env = new Environment(m.Scene);
        var header = new TspiHeader
        {
            DtNs = (ulong)SecToNs(dt),
            EpochUnixNs = ParseEpochNs(m.Scene.Epoch),
            OriginLatDeg = m.Scene.OriginLla.LatDeg,
            OriginLonDeg = m.Scene.OriginLla.LonDeg,
            OriginAltM = m.Scene.OriginLla.AltM,
            ManifestSha256 = manifestSha256Bytes,
        };
        var summary = new RunSummary();
        using (var w = new TspiStreamWriter(path, header))
        {
            foreach (var p in Produce(m, models, env, dt, steps))
            {
                var meta = new TspiEntityEntry
                {
                    Ord = p.Ord, Id = p.Id, Team = p.Team, Type = p.Type, Model = p.Model,
                    ParentOrd = p.ParentOrd, T0Ns = SecToNs(p.Traj.T0Sec),
                };
                w.WriteBlock(meta, p.Traj.EnumerateRecords(), p.Traj.Count);
                w.AddEvents(p.Events);
                summary.OrdToId[p.Ord] = p.Id;
                summary.Events.AddRange(p.Events);
                summary.SampleCount += p.Traj.Count;
                summary.EntityCount++;
                // p.Traj is now GC-eligible if it was a munition; aircraft are retained by Produce.
            }
            w.SetEnvironment(EnvironmentSerialization.ToJson(m.Scene.Environment));
            w.AddProvenance(SimWriter.ProvenanceRecord(manifestShaHex, m.Seed, models, "run", DynamicsTag(m, models)));
            w.Finish();
        }
        summary.Events = summary.Events.OrderBy(ev => ev.TNs).ToList();
        return summary;
    }

    /// <summary>
    /// Ordering + ord assignment, shared by both run paths: aircraft in declaration order
    /// (ords 0..n-1), then munitions (ords n..). Aircraft tracks are retained internally so
    /// munitions can guide against them regardless of what the consumer does with each yield.
    /// </summary>
    private static IEnumerable<Produced> Produce(ScenarioManifest m, ModelLibrary models, Environment env,
        double dt, int steps)
    {
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
            yield return new Produced
            {
                Id = e.Id, Team = e.Team, Type = e.Type, Model = e.Model, Ord = ord, ParentOrd = null,
                Traj = traj, Events = new List<TspiEventEntry>(),
            };
        }

        var entityById = m.Entities.ToDictionary(e => e.Id);
        foreach (var mun in m.Munitions)
        {
            if (mun.Launch == null) continue; // carried, never employed
            models.TryResolve(mun.Model, out var mmodel, out _, out _);
            var parentTrack = new MemTargetTrack(aircraftTracks[mun.Parent]);
            var targetTrack = new MemTargetTrack(aircraftTracks[mun.Target]);
            double? launchT = ResolveLaunchTime(mun.Launch, parentTrack, targetTrack, dt, 0.0, m.Scene.DurationS);
            if (launchT == null) continue; // condition never met within the scenario

            uint ord = nextOrd++;
            var (traj, events) = IntegrateMunition(
                mun, mmodel!, models, env, m.Seed, dt, launchT.Value,
                parentTrack, targetTrack,
                ord, aircraftOrd[mun.Target], m.Scene.OriginLla.AltM, m.Scene.DurationS);
            yield return new Produced
            {
                Id = mun.Id, Team = entityById[mun.Parent].Team, Type = "munition", Model = mun.Model, Ord = ord,
                ParentOrd = aircraftOrd[mun.Parent], Traj = traj, Events = events,
            };
        }
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
        // Reconstruct the air mass the original run flew in (persisted in the footer),
        // so appended munitions feel the same wind/atmosphere rather than still air.
        var envSpec = EnvironmentSerialization.FromJson(reader.Footer.Environment);
        var scene = new SceneSpec { DtS = dt, DurationS = durationS, OriginLla = result.Origin, Environment = envSpec };
        var env = new Environment(scene);
        result.EnvironmentJson = reader.Footer.Environment;

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
                munSpec, mmodel!, models, env, a.Seed, dt, launchT.Value,
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

    /// <summary>
    /// The provenance honesty tag, decided by the aircraft models in play: a `rotational`
    /// block means integrated rigid-body attitude, otherwise flight-path synthesis.
    /// Munition attitude is always velocity-aligned in v1 and does not enter the tag.
    /// </summary>
    private static string DynamicsTag(ScenarioManifest m, ModelLibrary models)
    {
        bool anyRigid = false, anySynth = false;
        foreach (var e in m.Entities)
        {
            models.TryResolve(e.Model, out var model, out _, out _);
            if (model?.Rotational != null) anyRigid = true;
            else anySynth = true;
        }
        if (!anyRigid) return SimWriter.DynSynthAttitude;
        return anySynth ? SimWriter.DynMixedAttitude : SimWriter.DynRigidAttitude;
    }

    // ---------------- aircraft ----------------

    private static Trajectory IntegrateAircraft(EntitySpec e, VehicleModel model, Environment env,
        ulong seed, double dt, int steps, double originAltM)
    {
        var init = ApplyDispersions(e, seed);
        var state = new MotionState { Pos = init.Pos, Vel = init.Vel };
        double heading0 = state.Vel.LengthHorizontal > 1e-3
            ? System.Math.Atan2(state.Vel.Y, state.Vel.X) : 0.0;
        IAircraftDynamics dyn = CreateAircraftDynamics(e, model, state, heading0);

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

            dyn.Acceleration(state, originAltM); // refresh channel outputs for this sample's state
            traj.Add(state.Pos, state.Vel, dyn.Attitude(state), dyn.BodyRates);

            if (i == steps) break;
            state = Rk4(state, t, dt, (tt, s) =>
            {
                Vec3d wind = windSampler.Wind(originAltM - s.Pos.Z);
                // Autopilot commands airspeed relative to the air mass; wind biases ground track.
                var air = new MotionState { Pos = s.Pos, Vel = s.Vel - wind };
                return dyn.Acceleration(air, originAltM);
            });
            // Rigid-body attitude advances in lock-step, chasing the post-step flight path
            // (no-op for synthesized attitude).
            dyn.StepRotation(state, t + dt, dt, originAltM);
            windSampler.Step(dt);
        }
        return traj;
    }

    /// <summary>
    /// The swappable-dynamics seam: a model with a `rotational` block flies with true
    /// rigid-body rotational dynamics; otherwise attitude is synthesized from the flight
    /// path (the v1 default, byte-identical to before this seam existed).
    /// </summary>
    private static IAircraftDynamics CreateAircraftDynamics(EntitySpec e, VehicleModel model,
        MotionState state, double heading0)
    {
        double speed0 = state.Vel.Length;
        if (model.Rotational is null)
            return new AircraftDynamics(model, speed0, heading0);

        QuatD q0;
        if (e.Initial.AttYprDeg is { Length: 3 } ypr)
        {
            q0 = QuatD.FromYprNed(ypr[0] * MathUtil.Deg2Rad, ypr[1] * MathUtil.Deg2Rad, ypr[2] * MathUtil.Deg2Rad);
        }
        else
        {
            // Velocity-aligned, wings level — same convention as the synthesized attitude.
            double pitch0 = speed0 > 1e-3
                ? System.Math.Asin(MathUtil.Clamp(-state.Vel.Z / speed0, -1, 1)) : 0.0;
            q0 = QuatD.FromYprNed(heading0, pitch0, 0.0);
        }
        return new RigidBodyAircraftDynamics(model, speed0, heading0, q0);
    }

    // ---------------- munition ----------------

    /// <summary>Manifest guidance spec -> law. The validator has already vetted kinds and
    /// policy resolvability; failures here are hard errors, not silent fallbacks.</summary>
    private static IGuidanceLaw? BuildGuidanceLaw(GuidanceSpec? g, VehicleModel model, ModelLibrary models)
    {
        switch (g?.Kind ?? "pronav")
        {
            case "ballistic":
                return null;
            case "pronav":
                return new PronavLaw(g?.Gain ?? model.PronavGainDefault);
            case "nn":
                if (string.IsNullOrEmpty(g!.Policy))
                    throw new InvalidOperationException("guidance.kind 'nn' requires guidance.policy");
                if (!models.TryResolvePolicy(g.Policy!, out var policy, out _, out string err))
                    throw new InvalidOperationException($"guidance policy '{g.Policy}': {err}");
                return new MlpGuidanceLaw(policy!);
            default:
                throw new InvalidOperationException($"unknown guidance.kind '{g!.Kind}'");
        }
    }

    /// <summary>Birth-velocity kick: elevation above horizontal, azimuth along the
    /// launch->target bearing (parent heading, then north, when degenerate).</summary>
    private static Vec3d EjectKick(LaunchSpec kick, Vec3d fromPos, Vec3d fromVel, Vec3d targetPos)
    {
        Vec3d toTarget = targetPos - fromPos;
        var bearing = new Vec3d(toTarget.X, toTarget.Y, 0);
        if (bearing.Length < 1e-6) bearing = new Vec3d(fromVel.X, fromVel.Y, 0);
        if (bearing.Length < 1e-6) bearing = new Vec3d(1, 0, 0);
        bearing /= bearing.Length;
        double el = kick.ElevationDeg * System.Math.PI / 180.0;
        return new Vec3d(
            System.Math.Cos(el) * bearing.X,
            System.Math.Cos(el) * bearing.Y,
            -System.Math.Sin(el)) * kick.EjectMps;
    }

    private static (Trajectory, List<TspiEventEntry>) IntegrateMunition(
        MunitionSpec mun, VehicleModel model, ModelLibrary models, Environment env, ulong seed, double dt,
        double launchSec, ITargetTrack parentTrack, ITargetTrack targetTrack, uint ord, uint targetOrd,
        double originAltM, double durationS)
    {
        var events = new List<TspiEventEntry>();
        var start = parentTrack.SampleAt(launchSec);
        var state = new MotionState { Pos = start.Pos, Vel = start.Vel };
        // Optional separation/booster kick (VLS/rail model) — guarded so legacy
        // manifests (eject_mps absent/0) integrate byte-identically.
        if (mun.Launch is { EjectMps: > 0 } kick)
            state.Vel += EjectKick(kick, start.Pos, start.Vel, targetTrack.SampleAt(launchSec).Pos);
        var dyn = new MunitionDynamics(model, BuildGuidanceLaw(mun.Guidance, model, models), launchSec);
        var windSampler = env.CreateSampler(new RngStream(seed, "wind:" + mun.Id));

        var traj = new Trajectory { T0Sec = launchSec, DtSec = dt };
        events.Add(new TspiEventEntry
        {
            TNs = SecToNs(launchSec), Kind = "launch", SrcOrd = ord, DstOrd = targetOrd,
        });

        double prevRange = double.MaxValue;
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

            // Endgame termination applies only to guided munitions; an unguided
            // projectile is not "intercepting" and flies until ground/expire.
            if (dyn.Guided)
            {
                // Fuze: intercept when inside lethal radius. Refine the closest point to
                // sub-dt precision — CPA almost never lands on a sample boundary, and the
                // reported miss distance is the number the whole campaign turns on.
                if (range <= model.FuzeRadiusM)
                {
                    terminal = "intercept";
                    var (tStar, missStar) = RefineCpa(traj, targetTrack, System.Math.Max(launchSec, t - dt), t);
                    events.Add(MakeCpa(ord, targetOrd, tStar, missStar));
                    events.Add(new TspiEventEntry { TNs = SecToNs(tStar), Kind = "intercept", SrcOrd = ord, DstOrd = targetOrd,
                        Data = new Dictionary<string, object> { { "miss_m", System.Math.Round(missStar, 3) } } });
                    break;
                }
                // CPA passed (range began increasing after closing): the minimum lies in the
                // last two intervals; refine it sub-dt and record the miss.
                if (range > prevRange && guard > 2)
                {
                    terminal = "miss";
                    var (tStar, missStar) = RefineCpa(traj, targetTrack, System.Math.Max(launchSec, t - 2 * dt), t);
                    events.Add(MakeCpa(ord, targetOrd, tStar, missStar));
                    break;
                }
                prevRange = range;
            }

            // Ground impact (flat-earth MSL at origin altitude). Interpolate the crossing
            // time between the previous (above-ground) and current (at/below) samples.
            double altMsl = originAltM - state.Pos.Z;
            if (altMsl <= 0.0 && t > launchSec)
            {
                double tImpact = t;
                int k = traj.Count - 1;
                if (k >= 1)
                {
                    double prevAlt = originAltM - traj.Pos[k - 1].Z;
                    double denom = prevAlt - altMsl;
                    if (denom > 1e-9) tImpact = (t - dt) + dt * (prevAlt / denom);
                }
                terminal = "ground_impact";
                events.Add(new TspiEventEntry { TNs = SecToNs(tImpact), Kind = "ground_impact", SrcOrd = ord });
                break;
            }

            if (t >= endT - 1e-9) { terminal = "expire"; break; }

            double rho = env.Density(state.Pos.Z, originAltM);
            // Hold-across-step laws (learned policies): evaluate once per sample against
            // the air-relative state, then RK4 integrates the held command (ZOH) so the
            // policy never sees mid-step states. Pronav skips this and stays per-stage.
            if (dyn.WantsHeldCommand)
            {
                Vec3d wind0 = windSampler.Wind(originAltM - state.Pos.Z);
                dyn.UpdateHeldCommand(t, new MotionState { Pos = state.Pos, Vel = state.Vel - wind0 }, tgtNow);
            }
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

    /// <summary>
    /// Sub-dt closest point of approach over [tA, tB]. Both tracks are smoothly
    /// interpolable (missile via its recorded Hermite segment, target via its track),
    /// so a fine scan plus a parabolic polish recovers the true minimum range and its
    /// time to well under a millimeter for typical closing speeds.
    /// </summary>
    private static (double tStar, double missM) RefineCpa(Trajectory mun, ITargetTrack tgt, double tA, double tB)
    {
        if (tB <= tA) { double r = (tgt.SampleAt(tB).Pos - mun.SampleAt(tB).Pos).Length; return (tB, r); }
        const int n = 64;
        double h = (tB - tA) / n;
        double Range(double t) => (tgt.SampleAt(t).Pos - mun.SampleAt(t).Pos).Length;
        double bestT = tA, best = double.MaxValue;
        int bestI = 0;
        for (int i = 0; i <= n; i++)
        {
            double t = tA + h * i;
            double r = Range(t);
            if (r < best) { best = r; bestT = t; bestI = i; }
        }
        // Parabolic polish using the neighbors of the best grid point.
        if (bestI > 0 && bestI < n)
        {
            double r0 = Range(bestT - h), r1 = best, r2 = Range(bestT + h);
            double denom = r0 - 2 * r1 + r2;
            if (denom > 1e-12)
            {
                double x = 0.5 * (r0 - r2) / denom;             // fractional offset in [-1,1]
                if (x > -1 && x < 1)
                {
                    double tStar = bestT + x * h;
                    double missStar = r1 - 0.125 * (r0 - r2) * (r0 - r2) / denom;
                    return (tStar, System.Math.Max(0.0, missStar));
                }
            }
        }
        return (bestT, best);
    }

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
