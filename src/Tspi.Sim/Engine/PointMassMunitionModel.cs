using System;
using System.Collections.Generic;
using Tspi.Core.IO;
using Tspi.Core.Math;
using Tspi.Sim.Manifest;
using Tspi.Sim.Models;

namespace Tspi.Sim.Engine;

/// <summary>
/// The stock munition trajectory model (v1 fidelity): point-mass RK4 with boost
/// thrust, quadratic drag, gravity, optional launch kick, a guidance law behind
/// the IGuidanceLaw seam, and sub-dt endgame refinement. This file IS the model —
/// swapping in a different generator means writing a sibling file implementing
/// IMunitionTrajectoryModel, not editing this one (see IMunitionTrajectoryModel.cs).
/// </summary>
public sealed class PointMassMunitionModel : IMunitionTrajectoryModel
{
    public (Trajectory Trajectory, List<TspiEventEntry> Events) Flyout(MunitionFlyoutRequest r)
    {
        MunitionSpec mun = r.Spec;
        VehicleModel model = r.Model;
        Environment env = r.Environment;
        double dt = r.DtSec;
        double launchSec = r.LaunchSec;
        ITargetTrack targetTrack = r.TargetTrack;
        uint ord = r.Ord, targetOrd = r.TargetOrd;
        double originAltM = r.OriginAltM;

        var events = new List<TspiEventEntry>();
        var start = r.ParentTrack.SampleAt(launchSec);
        var state = new MotionState { Pos = start.Pos, Vel = start.Vel };
        // Optional separation/booster kick (VLS/rail model) — guarded so legacy
        // manifests (eject_mps absent/0) integrate byte-identically.
        if (mun.Launch is { EjectMps: > 0 } kick)
            state.Vel += EjectKick(kick, start.Pos, start.Vel, targetTrack.SampleAt(launchSec).Pos);
        var dyn = new MunitionDynamics(model, BuildGuidanceLaw(mun.Guidance, model, r.Models), launchSec);
        var windSampler = env.CreateSampler(new RngStream(r.Seed, "wind:" + mun.Id));

        var traj = new Trajectory { T0Sec = launchSec, DtSec = dt };
        events.Add(new TspiEventEntry
        {
            TNs = SceneEngine.SecToNs(launchSec), Kind = "launch", SrcOrd = ord, DstOrd = targetOrd,
        });

        double prevRange = double.MaxValue;
        string terminal = "expire";
        double endT = System.Math.Min(r.DurationS, launchSec + model.MaxFlightTimeS);

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
                    events.Add(new TspiEventEntry { TNs = SceneEngine.SecToNs(tStar), Kind = "intercept", SrcOrd = ord, DstOrd = targetOrd,
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
                events.Add(new TspiEventEntry { TNs = SceneEngine.SecToNs(tImpact), Kind = "ground_impact", SrcOrd = ord });
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
            state = SceneEngine.Rk4(state, t, dt, (tt, s) =>
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
            events.Add(new TspiEventEntry { TNs = SceneEngine.SecToNs(System.Math.Min(t, endT)), Kind = "expire", SrcOrd = ord });
        return (traj, events);
    }

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

    private static TspiEventEntry MakeCpa(uint ord, uint targetOrd, double tSec, double rangeM) => new()
    {
        TNs = SceneEngine.SecToNs(tSec), Kind = "cpa", SrcOrd = ord, DstOrd = targetOrd,
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
}

/// <summary>
/// Notional powered munition: boost thrust, quadratic drag, gravity, and a guidance
/// law behind the IGuidanceLaw seam (pronav by default, learned policies via
/// guidance.kind "nn"). The airframe g-limit clamp lives HERE, outside the law, so no
/// policy can command past the envelope. All parameters notional.
/// </summary>
public sealed class MunitionDynamics
{
    private readonly VehicleModel _model;
    private readonly IGuidanceLaw? _law; // null = ballistic/unguided
    private readonly double _launchTimeSec;
    private Vec3d _heldCmd;
    private bool _heldValid;

    public MunitionDynamics(VehicleModel model, IGuidanceLaw? law, double launchTimeSec)
    {
        _model = model;
        _law = law;
        _launchTimeSec = launchTimeSec;
    }

    public bool Guided => _law != null;
    /// <summary>True when the law is evaluated once per output sample (ZOH) instead of at every RK4 stage.</summary>
    public bool WantsHeldCommand => _law != null && _law.HoldAcrossStep;

    public double MassKg => _model.MassKg;
    public double FuzeRadiusM => _model.FuzeRadiusM;
    public double MaxFlightTimeS => _model.MaxFlightTimeS;

    /// <summary>ZOH refresh for hold-across-step laws: called once per output sample,
    /// before the RK4 step, with the air-relative state at the sample boundary.</summary>
    public void UpdateHeldCommand(double tSec, MotionState self, MotionState target)
    {
        if (!WantsHeldCommand) return;
        _heldValid = _law!.TryAccelCmd(tSec, self, target, out _heldCmd);
    }

    public Vec3d Acceleration(double tSec, MotionState self, MotionState target, double rho)
    {
        Vec3d v = self.Vel;
        double speed = v.Length;
        Vec3d accel = new Vec3d(0, 0, MathUtil.G0); // gravity (NED down positive)

        // Boost.
        if (_model.Boost is { } boost && (tSec - _launchTimeSec) < boost.DurationS && speed > 1e-3)
            accel += (v / speed) * (boost.ThrustN / _model.MassKg);

        // Quadratic drag: a = -(0.5 rho CdA / m) * |v| * v.
        if (rho > 0 && _model.DragCdaM2 > 0 && speed > 1e-6)
            accel += v * (-(0.5 * rho * _model.DragCdaM2 / _model.MassKg) * speed);

        // Guidance: the law commands, the airframe clamps.
        if (_law != null)
        {
            Vec3d aCmd;
            bool has;
            if (_law.HoldAcrossStep) { has = _heldValid; aCmd = _heldCmd; }
            else has = _law.TryAccelCmd(tSec, self, target, out aCmd);
            if (has)
            {
                double aMax = _model.GLimitMax * MathUtil.G0;
                double m = aCmd.Length;
                if (m > aMax) aCmd = aCmd * (aMax / m);
                accel += aCmd;
            }
        }
        return accel;
    }

    public QuatD Attitude(MotionState st)
    {
        Vec3d v = st.Vel;
        double vh = v.LengthHorizontal;
        double yaw = vh > 1e-3 ? System.Math.Atan2(v.Y, v.X) : 0.0;
        double speed = v.Length;
        double pitch = speed > 1e-3 ? System.Math.Asin(MathUtil.Clamp(-v.Z / speed, -1, 1)) : 0.0;
        return QuatD.FromYprNed(yaw, pitch, 0.0); // missiles roughly roll-stable about velocity
    }
}
