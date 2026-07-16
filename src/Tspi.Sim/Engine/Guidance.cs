using System;
using Tspi.Core.Math;
using Tspi.Sim.Models;

namespace Tspi.Sim.Engine;

/// <summary>
/// The swappable guidance-law seam. A law maps (t, self, target) to a commanded
/// acceleration; the airframe's g-limit clamp is applied OUTSIDE the law by
/// MunitionDynamics, so no policy — analytic or learned — can exceed the envelope.
///
/// Evaluation cadence: pronav (cheap, smooth) is evaluated at every RK4 stage, exactly
/// as before the seam existed. A law with HoldAcrossStep=true is instead evaluated once
/// per output sample and zero-order-held across the step — learned policies never see
/// mid-step states, and their commands change only on the dt grid, matching the
/// convention that RK4 never integrates across a command discontinuity.
///
/// Self state is air-mass-relative (velocity minus wind), the same view pronav has
/// always had.
/// </summary>
public interface IGuidanceLaw
{
    bool HoldAcrossStep { get; }
    /// <summary>Commanded acceleration (NED m/s², pre-clamp). Return false to command nothing.</summary>
    bool TryAccelCmd(double tSec, MotionState self, MotionState target, out Vec3d accelCmd);
}

/// <summary>True proportional navigation: a = N · closing · (ω × r̂). Arithmetic is
/// bit-identical to the pre-seam inline implementation (golden-locked).</summary>
public sealed class PronavLaw : IGuidanceLaw
{
    private readonly double _navGain;
    public PronavLaw(double navGain) { _navGain = navGain; }

    public bool HoldAcrossStep => false;

    public bool TryAccelCmd(double tSec, MotionState self, MotionState target, out Vec3d accelCmd)
    {
        accelCmd = Vec3d.Zero;
        Vec3d r = target.Pos - self.Pos;
        double range = r.Length;
        if (range <= 1e-3) return false;
        Vec3d vRel = target.Vel - self.Vel;
        Vec3d omega = Vec3d.Cross(r, vRel) / (range * range);      // LOS angular rate
        double closing = -Vec3d.Dot(r, vRel) / range;              // + when closing
        if (closing <= 0) return false;
        Vec3d rHat = r / range;
        accelCmd = _navGain * closing * Vec3d.Cross(omega, rHat);
        return true;
    }
}

/// <summary>
/// Learned guidance: a feed-forward policy over the versioned "los_v1" observation.
///
/// The engagement is presented in the line-of-sight frame so the policy is independent
/// of compass heading and engagement plane:
///   e1 = r̂ (toward target), e2 = ω̂ (LOS-rate direction; deterministic perpendicular
///   of r̂ when the LOS is not rotating), e3 = e1 × e2.
/// Observation (normalized by the policy's `norm` block):
///   [ range, closingSpeed, selfSpeed, |ω| ]
/// Output: [a_e1, a_e2, a_e3] × norm.accel_mps2, re-expressed in NED.
/// Pronav in this space is a_e3 = −N·closing·|ω|, so an analytic law, a distilled
/// surrogate, and a trained policy all see the same world.
/// </summary>
public sealed class MlpGuidanceLaw : IGuidanceLaw
{
    private readonly GuidancePolicy _policy;
    public MlpGuidanceLaw(GuidancePolicy policy) { _policy = policy; }

    public bool HoldAcrossStep => true;

    public bool TryAccelCmd(double tSec, MotionState self, MotionState target, out Vec3d accelCmd)
    {
        accelCmd = Vec3d.Zero;
        Vec3d r = target.Pos - self.Pos;
        double range = r.Length;
        if (range <= 1e-3) return false;
        Vec3d rHat = r / range;
        Vec3d vRel = target.Vel - self.Vel;
        Vec3d omega = Vec3d.Cross(r, vRel) / (range * range);
        double omegaMag = omega.Length;
        double closing = -Vec3d.Dot(r, vRel) / range;

        Vec3d e2 = omegaMag > 1e-9 ? omega / omegaMag : PerpendicularOf(rHat);
        Vec3d e3 = Vec3d.Cross(rHat, e2);

        var n = _policy.Norm;
        double[] obs =
        {
            range / n.RangeM,
            closing / n.SpeedMps,
            self.Vel.Length / n.SpeedMps,
            omegaMag / n.OmegaRps,
        };
        double[] a = _policy.Forward(obs);
        accelCmd = (a[0] * rHat + a[1] * e2 + a[2] * e3) * n.AccelMps2;
        return true;
    }

    /// <summary>Deterministic unit perpendicular: cross with the axis least aligned with u.</summary>
    private static Vec3d PerpendicularOf(Vec3d u)
    {
        double ax = System.Math.Abs(u.X), ay = System.Math.Abs(u.Y), az = System.Math.Abs(u.Z);
        Vec3d axis = ax <= ay && ax <= az ? new Vec3d(1, 0, 0)
            : ay <= az ? new Vec3d(0, 1, 0)
            : new Vec3d(0, 0, 1);
        return Vec3d.Cross(u, axis).Normalized();
    }
}
