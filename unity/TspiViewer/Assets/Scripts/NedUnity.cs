using Tspi.Core.Math;
using UnityEngine;

namespace TspiViewer
{
    /// <summary>
    /// NED (right-handed, x-north / y-east / z-down) &lt;-&gt; Unity (left-handed, y-up,
    /// z-forward) conversion. Convention: Unity +Z = north, +X = east, +Y = up, so
    /// unityPos = (ned.E, -ned.D, ned.N). Attitude converts by mapping the body axes
    /// through NED into Unity and rebuilding with LookRotation — one code path, no
    /// hand-derived quaternion basis change to get subtly wrong.
    /// </summary>
    public static class NedUnity
    {
        public static Vector3 ToUnityPos(Vec3d ned)
        {
            return new Vector3((float)ned.Y, (float)(-ned.Z), (float)ned.X);
        }

        public static Vector3 ToUnityDir(Vec3d ned)
        {
            return new Vector3((float)ned.Y, (float)(-ned.Z), (float)ned.X);
        }

        public static Quaternion ToUnityRot(QuatD bodyToNed)
        {
            // Body axes in NED: forward = +X_body, down = +Z_body.
            Vec3d fwdNed = bodyToNed.Rotate(new Vec3d(1, 0, 0));
            Vec3d downNed = bodyToNed.Rotate(new Vec3d(0, 0, 1));
            Vector3 fwd = ToUnityDir(fwdNed);
            Vector3 up = -ToUnityDir(downNed);
            if (fwd.sqrMagnitude < 1e-10f) return Quaternion.identity;
            return Quaternion.LookRotation(fwd, up);
        }
    }
}
