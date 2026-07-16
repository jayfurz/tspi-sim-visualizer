using System;
using Tspi.Core.Math;
using Tspi.Sim.Manifest;
using Tspi.Sim.Models;

namespace Tspi.Sim.Engine;

/// <summary>Per-entity translational state carried through the ODE integrator.</summary>
public struct MotionState
{
    public Vec3d Pos;
    public Vec3d Vel;
    public static MotionState operator +(MotionState a, MotionState b) =>
        new() { Pos = a.Pos + b.Pos, Vel = a.Vel + b.Vel };
    public static MotionState operator *(MotionState a, double s) =>
        new() { Pos = a.Pos * s, Vel = a.Vel * s };
}

/// <summary>Derivative of a MotionState: d/dt pos = vel, d/dt vel = accel.</summary>
public struct MotionDeriv
{
    public Vec3d DPos;
    public Vec3d DVel;
}

/// <summary>
/// The swappable aircraft-dynamics seam (see docs/ARCHITECTURE.md, "Fidelity level").
/// Engine contract, per output sample at time t:
///   1. Acceleration(state, originAlt) refreshes channel outputs for the sample state
///      (it is also what RK4 evaluates at each stage);
///   2. Attitude(state) and BodyRates are recorded;
///   3. after the translational RK4 step, StepRotation(newState, t+dt, dt, originAlt)
///      advances any internal rotational state across the same interval.
/// Implementations with synthesized attitude return null BodyRates (the writer
/// finite-differences rates from attitude) and treat StepRotation as a no-op.
/// </summary>
public interface IAircraftDynamics
{
    void SetSpeed(SpeedCmd? cmd);
    void SetLateral(LateralCmd? cmd, double currentHeadingRad);
    void SetVertical(VerticalCmd? cmd, double currentAltMsl);
    Vec3d Acceleration(MotionState st, double originAltM);
    QuatD Attitude(MotionState st);
    /// <summary>Integrated body rates at the current sample, or null when attitude is synthesized.</summary>
    Vec3d? BodyRates { get; }
    void StepRotation(MotionState st, double tSec, double dt, double originAltM);
}

/// <summary>
/// Kinematic aircraft autopilot. Attitude is derived from the flight path plus a
/// coordinated-turn bank angle rather than integrated from aero moments — the right
/// altitude for a notional visualizer, and it needs no (export-sensitive) aero data.
/// The three manifest channels map to three acceleration components:
///   speed    -> longitudinal accel along horizontal velocity
///   lateral  -> heading-rate accel (bank-to-turn), g-limited
///   vertical -> vertical accel toward a commanded climb rate
/// Gravity is implicitly balanced by lift (the jet flies where commanded).
/// </summary>
public sealed class AircraftDynamics : IAircraftDynamics
{
    private readonly VehicleModel _model;

    // Active channel commands (updated as maneuver segments activate).
    private double _cmdSpeed;        // m/s, target airspeed
    private double _cmdAccel;        // m/s^2, speed-change authority
    private bool _speedHold = true;

    private double _cmdHeadingRad;   // target heading
    private double _gLimit;          // lateral load limit
    private bool _headingHold = true;

    private int _vertMode;           // 0 hold-vs (level), 1 hold-alt, 2 delta-alt
    private double _cmdAltMsl;
    private double _cmdRate;

    private const double KHeading = 1.2;   // heading error -> turn-rate gain (1/s)
    private const double KSpeed = 0.5;     // speed error -> accel gain (1/s)
    private const double KVert = 0.4;      // altitude error -> climb-rate gain (1/s)
    private const double KRate = 1.0;      // climb-rate error -> vert accel gain (1/s)

    public AircraftDynamics(VehicleModel model, double initialSpeed, double initialHeadingRad)
    {
        _model = model;
        _cmdSpeed = initialSpeed;
        _cmdAccel = model.AccelLongMaxMps2;
        _cmdHeadingRad = initialHeadingRad;
        _gLimit = 3.0;
    }

    public void SetSpeed(SpeedCmd? cmd)
    {
        switch (cmd)
        {
            case null: break;
            case SpeedSet s:
                _speedHold = false;
                _cmdSpeed = System.Math.Min(s.SpeedMps, _model.SpeedMaxMps);
                _cmdAccel = System.Math.Min(s.AccelMps2, _model.AccelLongMaxMps2);
                break;
        }
    }

    public void SetLateral(LateralCmd? cmd, double currentHeadingRad)
    {
        switch (cmd)
        {
            case null: break;
            case LateralTurnToHeading t:
                _headingHold = false;
                _cmdHeadingRad = t.HeadingDeg * MathUtil.Deg2Rad;
                _gLimit = System.Math.Min(t.GLimit, _model.GLimitMax);
                break;
        }
    }

    public void SetVertical(VerticalCmd? cmd, double currentAltMsl)
    {
        switch (cmd)
        {
            case null: break;
            case VerticalHoldAlt h: _vertMode = 1; _cmdAltMsl = h.AltMslM; _cmdRate = h.RateMps; break;
            case VerticalDeltaAlt d: _vertMode = 2; _cmdAltMsl = currentAltMsl + d.DeltaM; _cmdRate = d.RateMps; break;
        }
    }

    /// <summary>Signed bank angle (rad) for the last-evaluated command, for attitude synthesis.</summary>
    public double LastBankRad { get; private set; }

    public Vec3d Acceleration(MotionState st, double originAltM)
    {
        Vec3d v = st.Vel;
        double vh = v.LengthHorizontal;
        double heading = vh > 1e-3 ? System.Math.Atan2(v.Y, v.X) : _cmdHeadingRad;

        // Longitudinal (speed) channel.
        double speed = v.Length;
        Vec3d along = speed > 1e-3 ? v / speed : new Vec3d(System.Math.Cos(heading), System.Math.Sin(heading), 0);
        double aLong = 0.0;
        if (!_speedHold)
            aLong = MathUtil.Clamp(KSpeed * (_cmdSpeed - speed), -_cmdAccel, _cmdAccel);

        // Lateral (heading) channel: bank-to-turn, g-limited.
        double aLat = 0.0;
        double bank = 0.0;
        if (!_headingHold && vh > 1.0)
        {
            double aLatMax = _gLimit * MathUtil.G0;
            double psiDotMax = aLatMax / vh;
            double psiDot = MathUtil.Clamp(KHeading * MathUtil.WrapPi(_cmdHeadingRad - heading), -psiDotMax, psiDotMax);
            aLat = vh * psiDot;                 // signed: + turns toward increasing heading (right)
            bank = System.Math.Atan2(aLat, MathUtil.G0);
        }
        LastBankRad = bank;
        // Left-perpendicular of horizontal velocity in the N-E plane is (-sinψ, cosψ);
        // +aLat (right turn) needs centripetal accel to the right, hence the negative sign.
        Vec3d latDir = new Vec3d(-System.Math.Sin(heading), System.Math.Cos(heading), 0.0);
        Vec3d aLatVec = latDir * aLat;

        // Vertical channel: command a climb rate, then accelerate toward it.
        double climbUp = -v.Z; // NED down -> up
        double wCmd = 0.0;
        if (_vertMode == 0)
            wCmd = 0.0; // level off
        else
        {
            double altMsl = originAltM - st.Pos.Z;
            wCmd = MathUtil.Clamp(KVert * (_cmdAltMsl - altMsl), -_cmdRate, _cmdRate);
        }
        double aUp = MathUtil.Clamp(KRate * (wCmd - climbUp), -_model.AccelVertMaxMps2, _model.AccelVertMaxMps2);

        return along * aLong + aLatVec + new Vec3d(0, 0, -aUp);
    }

    /// <summary>Body->NED attitude from flight path + coordinated bank.</summary>
    public QuatD Attitude(MotionState st)
    {
        Vec3d v = st.Vel;
        double vh = v.LengthHorizontal;
        double yaw = vh > 1e-3 ? System.Math.Atan2(v.Y, v.X) : _cmdHeadingRad;
        double speed = v.Length;
        double pitch = speed > 1e-3 ? System.Math.Asin(MathUtil.Clamp(-v.Z / speed, -1, 1)) : 0.0;
        return QuatD.FromYprNed(yaw, pitch, LastBankRad);
    }

    /// <summary>Synthesized attitude has no integrated rates; the writer finite-differences them.</summary>
    public Vec3d? BodyRates => null;

    public void StepRotation(MotionState st, double tSec, double dt, double originAltM) { }
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
