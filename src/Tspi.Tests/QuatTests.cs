using System;
using Tspi.Core.Math;
using Xunit;

namespace Tspi.Tests;

public class QuatTests
{
    private const double D2R = System.Math.PI / 180.0;

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(30, 10, -15)]
    [InlineData(179, 40, 170)]
    [InlineData(-90, -30, 45)]
    public void YprRoundTrip(double yawDeg, double pitchDeg, double rollDeg)
    {
        var q = QuatD.FromYprNed(yawDeg * D2R, pitchDeg * D2R, rollDeg * D2R);
        q.ToYprNed(out double y, out double p, out double r);
        Assert.Equal(yawDeg * D2R, Wrap(y), 9);
        Assert.Equal(pitchDeg * D2R, p, 9);
        Assert.Equal(rollDeg * D2R, Wrap(r), 9);
    }

    [Fact]
    public void YawRotatesBodyForwardToNed()
    {
        // Body X (forward) under a 90 deg yaw should point east (NED +Y).
        var q = QuatD.FromYprNed(90 * D2R, 0, 0);
        var fwd = q.Rotate(new Vec3d(1, 0, 0));
        Assert.Equal(0, fwd.X, 9);
        Assert.Equal(1, fwd.Y, 9);
        Assert.Equal(0, fwd.Z, 9);
    }

    [Fact]
    public void PitchUpPointsNoseUp()
    {
        // +pitch should tilt body forward toward -down (up).
        var q = QuatD.FromYprNed(0, 30 * D2R, 0);
        var fwd = q.Rotate(new Vec3d(1, 0, 0));
        Assert.True(fwd.Z < 0); // up is negative down
        Assert.Equal(System.Math.Cos(30 * D2R), fwd.X, 9);
    }

    [Fact]
    public void RotationPreservesLength()
    {
        var q = QuatD.FromYprNed(1.1, -0.4, 2.0);
        var v = new Vec3d(3, -4, 12);
        Assert.Equal(v.Length, q.Rotate(v).Length, 9);
    }

    [Fact]
    public void SlerpEndpointsAndMidpoint()
    {
        var a = QuatD.FromYprNed(0, 0, 0);
        var b = QuatD.FromYprNed(90 * D2R, 0, 0);
        Assert.True(QuatD.Dot(a, QuatD.Slerp(a, b, 0)) > 0.9999999);
        var mid = QuatD.Slerp(a, b, 0.5);
        mid.ToYprNed(out double y, out _, out _);
        Assert.Equal(45 * D2R, y, 6);
    }

    [Fact]
    public void SlerpTakesShortPathAcrossSignFlip()
    {
        var a = QuatD.FromYprNed(0.1, 0.2, 0.3);
        var bNeg = a.Negated(); // same rotation, opposite sign
        var mid = QuatD.Slerp(a, bNeg, 0.5);
        // Shortest path between q and -q is q itself (zero angle), not a 360 sweep.
        Assert.True(System.Math.Abs(QuatD.Dot(a, mid)) > 0.9999);
    }

    [Fact]
    public void BodyRatesRecoverPureYawRate()
    {
        double dt = 0.01;
        double rate = 0.5; // rad/s about NED down => body z for level flight
        var q0 = QuatD.FromYprNed(1.0, 0, 0);
        var q1 = QuatD.FromYprNed(1.0 + rate * dt, 0, 0);
        var omega = QuatD.BodyRates(q0, q1, dt);
        Assert.Equal(rate, omega.Length, 4);
        Assert.True(System.Math.Abs(omega.Z) > System.Math.Abs(omega.X));
        Assert.True(System.Math.Abs(omega.Z) > System.Math.Abs(omega.Y));
    }

    private static double Wrap(double a)
    {
        while (a > System.Math.PI) a -= 2 * System.Math.PI;
        while (a <= -System.Math.PI) a += 2 * System.Math.PI;
        return a;
    }
}
