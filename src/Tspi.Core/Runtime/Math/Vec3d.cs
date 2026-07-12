using System;

namespace Tspi.Core.Math
{
    /// <summary>Double-precision 3-vector. In NED usage: X=north, Y=east, Z=down (meters).</summary>
    public readonly struct Vec3d : IEquatable<Vec3d>
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public Vec3d(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static readonly Vec3d Zero = new Vec3d(0.0, 0.0, 0.0);

        public static Vec3d operator +(Vec3d a, Vec3d b) => new Vec3d(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3d operator -(Vec3d a, Vec3d b) => new Vec3d(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3d operator -(Vec3d a) => new Vec3d(-a.X, -a.Y, -a.Z);
        public static Vec3d operator *(Vec3d a, double s) => new Vec3d(a.X * s, a.Y * s, a.Z * s);
        public static Vec3d operator *(double s, Vec3d a) => a * s;
        public static Vec3d operator /(Vec3d a, double s) => new Vec3d(a.X / s, a.Y / s, a.Z / s);

        public static double Dot(Vec3d a, Vec3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vec3d Cross(Vec3d a, Vec3d b) => new Vec3d(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

        public double LengthSquared => X * X + Y * Y + Z * Z;
        public double Length => System.Math.Sqrt(X * X + Y * Y + Z * Z);

        /// <summary>Horizontal (north/east plane) magnitude.</summary>
        public double LengthHorizontal => System.Math.Sqrt(X * X + Y * Y);

        public Vec3d Normalized()
        {
            double len = Length;
            return len > 1e-300 ? this / len : Zero;
        }

        public static double Distance(Vec3d a, Vec3d b) => (a - b).Length;

        public bool Equals(Vec3d other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is Vec3d v && Equals(v);
        public override int GetHashCode() => X.GetHashCode() ^ (Y.GetHashCode() << 2) ^ (Z.GetHashCode() >> 2);
        public override string ToString() =>
            "(" + X.ToString("G6") + ", " + Y.ToString("G6") + ", " + Z.ToString("G6") + ")";
    }
}
