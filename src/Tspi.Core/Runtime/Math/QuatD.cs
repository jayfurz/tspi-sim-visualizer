using System;

namespace Tspi.Core.Math
{
    /// <summary>
    /// Double-precision quaternion, Hamilton convention, W-first component order.
    /// Throughout the TSPI format a quaternion rotates BODY-frame vectors into NED:
    /// v_ned = q * (0, v_body) * conj(q). Body axes: X forward, Y right, Z down.
    /// Euler angles are the aerospace 3-2-1 sequence: yaw (about NED down), then
    /// pitch (about intermediate east/right), then roll (about body forward).
    /// </summary>
    public readonly struct QuatD
    {
        public readonly double W;
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public QuatD(double w, double x, double y, double z) { W = w; X = x; Y = y; Z = z; }

        public static readonly QuatD Identity = new QuatD(1.0, 0.0, 0.0, 0.0);

        public double Norm => System.Math.Sqrt(W * W + X * X + Y * Y + Z * Z);

        public QuatD Normalized()
        {
            double n = Norm;
            if (n < 1e-300) return Identity;
            return new QuatD(W / n, X / n, Y / n, Z / n);
        }

        public QuatD Conjugate() => new QuatD(W, -X, -Y, -Z);

        public QuatD Negated() => new QuatD(-W, -X, -Y, -Z);

        public static double Dot(QuatD a, QuatD b) => a.W * b.W + a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        /// <summary>Hamilton product: composition a then... apply b first, then a (a*b rotates by b, then a).</summary>
        public static QuatD operator *(QuatD a, QuatD b) => new QuatD(
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W);

        /// <summary>Rotate a vector by this quaternion (body -> NED when q is a body->NED attitude).</summary>
        public Vec3d Rotate(Vec3d v)
        {
            var qv = new Vec3d(X, Y, Z);
            var t = 2.0 * Vec3d.Cross(qv, v);
            return v + W * t + Vec3d.Cross(qv, t);
        }

        public static QuatD FromAxisAngle(Vec3d axis, double angleRad)
        {
            var a = axis.Normalized();
            double h = angleRad * 0.5;
            double s = System.Math.Sin(h);
            return new QuatD(System.Math.Cos(h), a.X * s, a.Y * s, a.Z * s);
        }

        /// <summary>Aerospace 3-2-1 (yaw-pitch-roll) to quaternion, NED frame, radians.</summary>
        public static QuatD FromYprNed(double yawRad, double pitchRad, double rollRad)
        {
            double cy = System.Math.Cos(yawRad * 0.5), sy = System.Math.Sin(yawRad * 0.5);
            double cp = System.Math.Cos(pitchRad * 0.5), sp = System.Math.Sin(pitchRad * 0.5);
            double cr = System.Math.Cos(rollRad * 0.5), sr = System.Math.Sin(rollRad * 0.5);
            return new QuatD(
                cr * cp * cy + sr * sp * sy,
                sr * cp * cy - cr * sp * sy,
                cr * sp * cy + sr * cp * sy,
                cr * cp * sy - sr * sp * cy);
        }

        /// <summary>Quaternion to aerospace 3-2-1 yaw/pitch/roll, NED frame, radians. Pitch clamped at +/-90 deg.</summary>
        public void ToYprNed(out double yawRad, out double pitchRad, out double rollRad)
        {
            double sinp = 2.0 * (W * Y - Z * X);
            if (sinp > 1.0) sinp = 1.0;
            if (sinp < -1.0) sinp = -1.0;
            pitchRad = System.Math.Asin(sinp);
            rollRad = System.Math.Atan2(2.0 * (W * X + Y * Z), 1.0 - 2.0 * (X * X + Y * Y));
            yawRad = System.Math.Atan2(2.0 * (W * Z + X * Y), 1.0 - 2.0 * (Y * Y + Z * Z));
        }

        /// <summary>
        /// Spherical linear interpolation with shortest-path sign handling.
        /// Safe for t in [0,1]; falls back to normalized lerp for nearly-parallel inputs.
        /// </summary>
        public static QuatD Slerp(QuatD a, QuatD b, double t)
        {
            double dot = Dot(a, b);
            if (dot < 0.0) { b = b.Negated(); dot = -dot; }
            if (dot > 0.9995)
            {
                var l = new QuatD(
                    a.W + t * (b.W - a.W),
                    a.X + t * (b.X - a.X),
                    a.Y + t * (b.Y - a.Y),
                    a.Z + t * (b.Z - a.Z));
                return l.Normalized();
            }
            double theta0 = System.Math.Acos(dot);
            double theta = theta0 * t;
            double sin0 = System.Math.Sin(theta0);
            double sA = System.Math.Sin(theta0 - theta) / sin0;
            double sB = System.Math.Sin(theta) / sin0;
            return new QuatD(
                sA * a.W + sB * b.W,
                sA * a.X + sB * b.X,
                sA * a.Y + sB * b.Y,
                sA * a.Z + sB * b.Z);
        }

        /// <summary>
        /// Body angular rate (rad/s) that carries qPrev to qCur over dtSec, expressed in the body frame.
        /// </summary>
        public static Vec3d BodyRates(QuatD qPrev, QuatD qCur, double dtSec)
        {
            if (dtSec <= 0.0) return Vec3d.Zero;
            if (Dot(qPrev, qCur) < 0.0) qCur = qCur.Negated();
            QuatD dq = qPrev.Conjugate() * qCur;
            dq = dq.Normalized();
            double w = dq.W;
            if (w > 1.0) w = 1.0;
            if (w < -1.0) w = -1.0;
            double angle = 2.0 * System.Math.Acos(w);
            double s = System.Math.Sqrt(1.0 - w * w);
            if (s < 1e-12 || angle < 1e-12) return Vec3d.Zero;
            var axis = new Vec3d(dq.X / s, dq.Y / s, dq.Z / s);
            return axis * (angle / dtSec);
        }

        public override string ToString() =>
            "(w=" + W.ToString("G6") + ", " + X.ToString("G6") + ", " + Y.ToString("G6") + ", " + Z.ToString("G6") + ")";
    }
}
