using System.Runtime.InteropServices;
using Tspi.Core.Math;

namespace Tspi.Core.IO
{
    /// <summary>
    /// One TSPI sample, record layout 1 (stride 64 bytes, exactly one cache line).
    /// Time is implicit: t = block.t0 + index * header.dt. No per-record flags:
    /// discrete happenings live in the footer event log.
    /// Quaternion rotates body->NED (Hamilton, W-first) and is written
    /// sign-continuous (dot(q_i, q_i+1) >= 0) so playback slerp never long-ways.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TspiRecord
    {
        public double PosN;
        public double PosE;
        public double PosD;
        public float VelN;
        public float VelE;
        public float VelD;
        public float QuatW;
        public float QuatX;
        public float QuatY;
        public float QuatZ;
        public float OmegaX;
        public float OmegaY;
        public float OmegaZ;

        public Vec3d Pos => new Vec3d(PosN, PosE, PosD);
        public Vec3d Vel => new Vec3d(VelN, VelE, VelD);
        public QuatD Quat => new QuatD(QuatW, QuatX, QuatY, QuatZ);
        public Vec3d Omega => new Vec3d(OmegaX, OmegaY, OmegaZ);

        public static TspiRecord From(Vec3d posNed, Vec3d velNed, QuatD quatBodyToNed, Vec3d omegaBody)
        {
            return new TspiRecord
            {
                PosN = posNed.X, PosE = posNed.Y, PosD = posNed.Z,
                VelN = (float)velNed.X, VelE = (float)velNed.Y, VelD = (float)velNed.Z,
                QuatW = (float)quatBodyToNed.W, QuatX = (float)quatBodyToNed.X,
                QuatY = (float)quatBodyToNed.Y, QuatZ = (float)quatBodyToNed.Z,
                OmegaX = (float)omegaBody.X, OmegaY = (float)omegaBody.Y, OmegaZ = (float)omegaBody.Z,
            };
        }
    }

    /// <summary>Fully-typed interpolated state produced by TspiReader.TrySampleAt.</summary>
    public struct TspiState
    {
        public Vec3d PosNed;
        public Vec3d VelNed;
        public QuatD AttBodyToNed;
        public Vec3d OmegaBody;
    }
}
