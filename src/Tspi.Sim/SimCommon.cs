using Tspi.Core.Math;

namespace Tspi.Sim;

public static class SimInfo
{
    // 0.2.0: scenario manifests moved munitions to a top-level section with a
    // `parent` field (breaking manifest change; golden regenerated).
    public const string Version = "0.2.0";
}

public static class MathUtil
{
    public const double G0 = 9.80665;
    public const double Deg2Rad = System.Math.PI / 180.0;
    public const double Rad2Deg = 180.0 / System.Math.PI;

    public static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

    /// <summary>Wrap an angle to (-pi, pi].</summary>
    public static double WrapPi(double a)
    {
        double twoPi = 2.0 * System.Math.PI;
        a %= twoPi;
        if (a <= -System.Math.PI) a += twoPi;
        else if (a > System.Math.PI) a -= twoPi;
        return a;
    }

    public static double Heading(Vec3d vel) => System.Math.Atan2(vel.Y, vel.X);
}

/// <summary>
/// Deterministic per-purpose random stream: SplitMix64 keyed by (seed, label).
/// Every consumer gets its own stream so adding an entity never perturbs another
/// entity's draws — a requirement for run-to-run comparability.
/// </summary>
public sealed class RngStream
{
    private ulong _state;
    private double _spare;
    private bool _hasSpare;

    public RngStream(ulong seed, string label)
    {
        _state = Mix(seed ^ 0x6A09E667F3BCC909UL, Fnv1a64(label));
    }

    private static ulong Fnv1a64(string s)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in s)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private static ulong Mix(ulong a, ulong b)
    {
        ulong z = a + 0x9E3779B97F4A7C15UL * (b | 1UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public ulong NextUInt64()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>Standard normal via Box-Muller.</summary>
    public double NextGaussian()
    {
        if (_hasSpare)
        {
            _hasSpare = false;
            return _spare;
        }
        double u1, u2;
        do { u1 = NextDouble(); } while (u1 <= 1e-300);
        u2 = NextDouble();
        double r = System.Math.Sqrt(-2.0 * System.Math.Log(u1));
        double theta = 2.0 * System.Math.PI * u2;
        _spare = r * System.Math.Sin(theta);
        _hasSpare = true;
        return r * System.Math.Cos(theta);
    }
}
