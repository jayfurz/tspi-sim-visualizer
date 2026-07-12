using System;
using Tspi.Core.Math;
using Tspi.Sim.Manifest;
using Tspi.Sim.Models;

namespace Tspi.Sim.Engine;

/// <summary>
/// Rigid-body rotational integrator: RK4 on the coupled quaternion/body-rate state
///   q̇ = ½ q ⊗ (0, ω)              (q: body->NED, Hamilton convention)
///   ω̇ = I⁻¹ (τ − ω × I ω)          (Euler's equations, principal-axis inertia)
/// Torque is a callback so the controller is evaluated at every RK4 stage. The
/// quaternion is treated as a point in R⁴ during stage combination and renormalized
/// once per step (drift per step is O(dt²) and does not accumulate).
/// </summary>
public static class RigidBodyRotation
{
    private struct RotDeriv
    {
        public double Qw, Qx, Qy, Qz;
        public Vec3d DOmega;
    }

    public static (QuatD Q, Vec3d Omega) Step(QuatD q, Vec3d omega, Vec3d inertia,
        Func<QuatD, Vec3d, Vec3d> torque, double dt)
    {
        RotDeriv D(double qw, double qx, double qy, double qz, Vec3d w)
        {
            var qi = new QuatD(qw, qx, qy, qz);
            Vec3d tau = torque(qi, w);
            var iw = new Vec3d(inertia.X * w.X, inertia.Y * w.Y, inertia.Z * w.Z);
            Vec3d gyro = Vec3d.Cross(w, iw);
            var dw = new Vec3d(
                (tau.X - gyro.X) / inertia.X,
                (tau.Y - gyro.Y) / inertia.Y,
                (tau.Z - gyro.Z) / inertia.Z);
            return new RotDeriv
            {
                Qw = 0.5 * (-qx * w.X - qy * w.Y - qz * w.Z),
                Qx = 0.5 * (qw * w.X + qy * w.Z - qz * w.Y),
                Qy = 0.5 * (qw * w.Y - qx * w.Z + qz * w.X),
                Qz = 0.5 * (qw * w.Z + qx * w.Y - qy * w.X),
                DOmega = dw,
            };
        }

        RotDeriv k1 = D(q.W, q.X, q.Y, q.Z, omega);
        double h = dt / 2;
        RotDeriv k2 = D(q.W + h * k1.Qw, q.X + h * k1.Qx, q.Y + h * k1.Qy, q.Z + h * k1.Qz,
            omega + k1.DOmega * h);
        RotDeriv k3 = D(q.W + h * k2.Qw, q.X + h * k2.Qx, q.Y + h * k2.Qy, q.Z + h * k2.Qz,
            omega + k2.DOmega * h);
        RotDeriv k4 = D(q.W + dt * k3.Qw, q.X + dt * k3.Qx, q.Y + dt * k3.Qy, q.Z + dt * k3.Qz,
            omega + k3.DOmega * dt);

        double s = dt / 6;
        var qNext = new QuatD(
            q.W + s * (k1.Qw + 2 * k2.Qw + 2 * k3.Qw + k4.Qw),
            q.X + s * (k1.Qx + 2 * k2.Qx + 2 * k3.Qx + k4.Qx),
            q.Y + s * (k1.Qy + 2 * k2.Qy + 2 * k3.Qy + k4.Qy),
            q.Z + s * (k1.Qz + 2 * k2.Qz + 2 * k3.Qz + k4.Qz)).Normalized();
        Vec3d wNext = omega + (k1.DOmega + k2.DOmega * 2 + k3.DOmega * 2 + k4.DOmega) * s;
        return (qNext, wNext);
    }
}

/// <summary>
/// Aircraft with rigid-body rotational dynamics. Translation stays on the kinematic
/// autopilot (this repo carries no aero data, deliberately); rotation is a true rigid
/// body: a torque-limited quaternion PD controller tracks the autopilot's flight-path
/// reference attitude, and (q, ω) are integrated from Euler's equations. Attitude now
/// lags and rate-limits like a real airframe instead of snapping with the flight path,
/// and the recorded body rates are the integrated ω, not finite differences.
/// </summary>
public sealed class RigidBodyAircraftDynamics : IAircraftDynamics
{
    private readonly AircraftDynamics _autopilot;
    private readonly Vec3d _inertia;
    private readonly Vec3d _maxTorque;
    private readonly double _kp;
    private readonly double _kd;

    private QuatD _q;        // body->NED, integrated
    private Vec3d _omega;    // body rates rad/s, integrated
    private QuatD _qRefPrev; // previous reference, for feedforward rate
    private bool _hasRefPrev;

    public RigidBodyAircraftDynamics(VehicleModel model, double initialSpeed, double initialHeadingRad,
        QuatD initialAttitude)
    {
        var rot = model.Rotational
            ?? throw new ArgumentException("model has no rotational spec", nameof(model));
        _autopilot = new AircraftDynamics(model, initialSpeed, initialHeadingRad);
        _inertia = new Vec3d(rot.InertiaKgm2[0], rot.InertiaKgm2[1], rot.InertiaKgm2[2]);
        _maxTorque = new Vec3d(rot.MaxTorqueNm[0], rot.MaxTorqueNm[1], rot.MaxTorqueNm[2]);
        _kp = rot.AttitudeKp;
        _kd = rot.AttitudeKd;
        _q = initialAttitude.Normalized();
        _omega = Vec3d.Zero;
    }

    public void SetSpeed(SpeedCmd? cmd) => _autopilot.SetSpeed(cmd);
    public void SetLateral(LateralCmd? cmd, double currentHeadingRad) => _autopilot.SetLateral(cmd, currentHeadingRad);
    public void SetVertical(VerticalCmd? cmd, double currentAltMsl) => _autopilot.SetVertical(cmd, currentAltMsl);

    public Vec3d Acceleration(MotionState st, double originAltM) => _autopilot.Acceleration(st, originAltM);

    /// <summary>The integrated attitude — independent of the instantaneous flight path.</summary>
    public QuatD Attitude(MotionState st) => _q;

    public Vec3d? BodyRates => _omega;

    /// <summary>
    /// Advance (q, ω) across [t-dt, t]. <paramref name="st"/> is the post-step
    /// translational state; the reference attitude is held constant over the step, i.e.
    /// the controller is chasing where the flight path is now — a discrete autopilot at
    /// the sim rate.
    /// </summary>
    public void StepRotation(MotionState st, double tSec, double dt, double originAltM)
    {
        _autopilot.Acceleration(st, originAltM); // refresh coordinated-turn bank for st
        QuatD qRef = _autopilot.Attitude(st);
        Vec3d wRef = _hasRefPrev ? QuatD.BodyRates(_qRefPrev, qRef, dt) : Vec3d.Zero;
        (_q, _omega) = RigidBodyRotation.Step(_q, _omega, _inertia,
            (q, w) => ControlTorque(q, w, qRef, wRef), dt);
        _qRefPrev = qRef;
        _hasRefPrev = true;
    }

    /// <summary>
    /// Quaternion-error PD with per-axis torque saturation. The gyroscopic term
    /// ω × I ω is left to the plant (not cancelled by feedforward), so inertia
    /// coupling is felt honestly; the rate term damps it.
    /// </summary>
    private Vec3d ControlTorque(QuatD q, Vec3d w, QuatD qRef, Vec3d wRef)
    {
        QuatD qe = (q.Conjugate() * qRef).Normalized();
        if (qe.W < 0) qe = qe.Negated(); // shortest path
        double cw = qe.W > 1.0 ? 1.0 : qe.W;
        double sin = System.Math.Sqrt(System.Math.Max(0.0, 1.0 - cw * cw));
        Vec3d rotVec = sin < 1e-9
            ? Vec3d.Zero
            : new Vec3d(qe.X / sin, qe.Y / sin, qe.Z / sin) * (2.0 * System.Math.Acos(cw));
        Vec3d aCmd = rotVec * _kp + (wRef - w) * _kd; // rad/s², body frame
        return new Vec3d(
            MathUtil.Clamp(_inertia.X * aCmd.X, -_maxTorque.X, _maxTorque.X),
            MathUtil.Clamp(_inertia.Y * aCmd.Y, -_maxTorque.Y, _maxTorque.Y),
            MathUtil.Clamp(_inertia.Z * aCmd.Z, -_maxTorque.Z, _maxTorque.Z));
    }
}
