using Tspi.Core.Math;

namespace Tspi.Core.IO
{
    /// <summary>
    /// The interpolation half of playback, factored out so every source of records —
    /// a memory-mapped file (<see cref="TspiReader"/>) or a live stream
    /// (<see cref="Tspi.Core.Live.LiveTspiSource"/>) — samples through one
    /// implementation. A live pose and the replayed pose of the same run must be the
    /// same number; that is only true if there is exactly one copy of this math.
    ///
    /// (The JS/GDScript viewers port these same formulas; keep them in lockstep.)
    /// </summary>
    public static class TspiSampling
    {
        /// <summary>
        /// Resolve a time to the bracketing sample index and the fraction between it and
        /// the next sample. Returns false when t is outside [t0, t1] and clamp is false.
        /// </summary>
        public static bool TryLocate(double tSec, double t0Sec, double t1Sec, long sampleCount,
            double dtSec, bool clamp, out long index, out double u)
        {
            index = 0;
            u = 0;
            if (sampleCount <= 0) return false;
            if (tSec < t0Sec || tSec > t1Sec)
            {
                if (!clamp) return false;
                tSec = tSec < t0Sec ? t0Sec : t1Sec;
            }
            if (sampleCount == 1) return true;

            double raw = (tSec - t0Sec) / dtSec;
            long i = (long)System.Math.Floor(raw);
            if (i < 0) i = 0;
            if (i > sampleCount - 2) i = sampleCount - 2;
            index = i;
            u = raw - i;
            return true;
        }

        /// <summary>The degenerate one-sample block: the record itself, attitude normalized.</summary>
        public static TspiState Single(in TspiRecord only)
        {
            return new TspiState
            {
                PosNed = only.Pos,
                VelNed = only.Vel,
                AttBodyToNed = only.Quat.Normalized(),
                OmegaBody = only.Omega,
            };
        }

        /// <summary>
        /// Cubic Hermite position using the stored velocities as tangents, the Hermite
        /// derivative for velocity, shortest-path slerp for attitude, and lerped body
        /// rates — the contract every viewer in this repo renders.
        /// </summary>
        public static TspiState Interpolate(in TspiRecord a, in TspiRecord b, double u, double dtSec)
        {
            double h00 = (2 * u - 3) * u * u + 1;
            double h10 = ((u - 2) * u + 1) * u;
            double h01 = (3 - 2 * u) * u * u;
            double h11 = (u - 1) * u * u;
            Vec3d pos = h00 * a.Pos + (h10 * dtSec) * a.Vel + h01 * b.Pos + (h11 * dtSec) * b.Vel;

            double g00 = 6 * u * u - 6 * u;
            double g10 = 3 * u * u - 4 * u + 1;
            double g01 = -6 * u * u + 6 * u;
            double g11 = 3 * u * u - 2 * u;
            Vec3d vel = (g00 / dtSec) * a.Pos + g10 * a.Vel + (g01 / dtSec) * b.Pos + g11 * b.Vel;

            return new TspiState
            {
                PosNed = pos,
                VelNed = vel,
                AttBodyToNed = QuatD.Slerp(a.Quat.Normalized(), b.Quat.Normalized(), u),
                OmegaBody = a.Omega + u * (b.Omega - a.Omega),
            };
        }
    }

    /// <summary>
    /// What a viewer needs from a source of TSPI: an entity table, an event log, and
    /// O(1) interpolated poses. Implemented by <see cref="TspiReader"/> (a finished
    /// file) and <see cref="Tspi.Core.Live.LiveTspiSource"/> (a running stream), so a
    /// viewer renders both through one code path and only the "does it keep growing?"
    /// parts differ.
    /// </summary>
    public interface ITspiSource
    {
        TspiHeader Header { get; }
        System.Collections.Generic.IReadOnlyList<TspiEntityEntry> Entities { get; }
        System.Collections.Generic.IReadOnlyList<TspiEventEntry> Events { get; }
        double DtSec { get; }
        /// <summary>True while more samples may still arrive (a live stream that has not ended).</summary>
        bool IsLive { get; }
        TspiEntityEntry FindEntity(string id);
        double StartSec(TspiEntityEntry e);
        double EndSec(TspiEntityEntry e);
        TspiRecord ReadSample(TspiEntityEntry e, long index);
        bool TrySampleAt(TspiEntityEntry e, double tSec, out TspiState state, bool clamp = false);
    }
}
