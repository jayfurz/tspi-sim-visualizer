using System;
using Tspi.Core.Math;

namespace Tspi.Core.Geo
{
    /// <summary>
    /// WGS84 geodesy: LLA &lt;-&gt; ECEF &lt;-&gt; local NED tangent plane.
    /// The scenario NED frame is the tangent plane at the scene origin LLA; it is a
    /// flat-earth approximation whose altitude error grows ~d^2/2R with distance d
    /// (about 78 m at 100 km). Long-range scenarios should convert through ECEF.
    /// Altitudes here are ellipsoidal (no geoid model).
    /// </summary>
    public static class Wgs84
    {
        public const double SemiMajorM = 6378137.0;
        public const double Flattening = 1.0 / 298.257223563;
        public static readonly double E2 = Flattening * (2.0 - Flattening);
        public const double Deg2Rad = System.Math.PI / 180.0;
        public const double Rad2Deg = 180.0 / System.Math.PI;

        public static Vec3d LlaToEcef(double latDeg, double lonDeg, double altM)
        {
            double lat = latDeg * Deg2Rad, lon = lonDeg * Deg2Rad;
            double sLat = System.Math.Sin(lat), cLat = System.Math.Cos(lat);
            double sLon = System.Math.Sin(lon), cLon = System.Math.Cos(lon);
            double n = SemiMajorM / System.Math.Sqrt(1.0 - E2 * sLat * sLat);
            return new Vec3d(
                (n + altM) * cLat * cLon,
                (n + altM) * cLat * sLon,
                (n * (1.0 - E2) + altM) * sLat);
        }

        public static void EcefToLla(Vec3d ecef, out double latDeg, out double lonDeg, out double altM)
        {
            double x = ecef.X, y = ecef.Y, z = ecef.Z;
            double lon = System.Math.Atan2(y, x);
            double p = System.Math.Sqrt(x * x + y * y);
            if (p < 1e-9)
            {
                // Pole.
                latDeg = z >= 0.0 ? 90.0 : -90.0;
                lonDeg = 0.0;
                double b = SemiMajorM * (1.0 - Flattening);
                altM = System.Math.Abs(z) - b;
                return;
            }
            double lat = System.Math.Atan2(z, p * (1.0 - E2));
            double alt = 0.0;
            for (int i = 0; i < 6; i++)
            {
                double sLat = System.Math.Sin(lat);
                double n = SemiMajorM / System.Math.Sqrt(1.0 - E2 * sLat * sLat);
                alt = p / System.Math.Cos(lat) - n;
                lat = System.Math.Atan2(z, p * (1.0 - E2 * n / (n + alt)));
            }
            latDeg = lat * Rad2Deg;
            lonDeg = lon * Rad2Deg;
            altM = alt;
        }

        /// <summary>Unit north/east/down vectors of the local tangent plane, expressed in ECEF.</summary>
        public static void NedBasis(double latDeg, double lonDeg, out Vec3d north, out Vec3d east, out Vec3d down)
        {
            double lat = latDeg * Deg2Rad, lon = lonDeg * Deg2Rad;
            double sLat = System.Math.Sin(lat), cLat = System.Math.Cos(lat);
            double sLon = System.Math.Sin(lon), cLon = System.Math.Cos(lon);
            north = new Vec3d(-sLat * cLon, -sLat * sLon, cLat);
            east = new Vec3d(-sLon, cLon, 0.0);
            down = new Vec3d(-cLat * cLon, -cLat * sLon, -sLat);
        }

        public static Vec3d NedToEcef(double originLatDeg, double originLonDeg, double originAltM, Vec3d ned)
        {
            Vec3d o = LlaToEcef(originLatDeg, originLonDeg, originAltM);
            NedBasis(originLatDeg, originLonDeg, out Vec3d n, out Vec3d e, out Vec3d d);
            return o + n * ned.X + e * ned.Y + d * ned.Z;
        }

        public static Vec3d EcefToNed(double originLatDeg, double originLonDeg, double originAltM, Vec3d ecef)
        {
            Vec3d o = LlaToEcef(originLatDeg, originLonDeg, originAltM);
            NedBasis(originLatDeg, originLonDeg, out Vec3d n, out Vec3d e, out Vec3d d);
            Vec3d r = ecef - o;
            return new Vec3d(Vec3d.Dot(r, n), Vec3d.Dot(r, e), Vec3d.Dot(r, d));
        }

        public static void NedToLla(double originLatDeg, double originLonDeg, double originAltM, Vec3d ned,
            out double latDeg, out double lonDeg, out double altM)
        {
            EcefToLla(NedToEcef(originLatDeg, originLonDeg, originAltM, ned), out latDeg, out lonDeg, out altM);
        }

        public static Vec3d LlaToNed(double originLatDeg, double originLonDeg, double originAltM,
            double latDeg, double lonDeg, double altM)
        {
            return EcefToNed(originLatDeg, originLonDeg, originAltM, LlaToEcef(latDeg, lonDeg, altM));
        }
    }
}
