using Tspi.Core.Geo;
using Tspi.Core.Math;
using Xunit;

namespace Tspi.Tests;

public class GeoTests
{
    private const double LatEdwards = 34.9061;
    private const double LonEdwards = -117.8839;
    private const double AltEdwards = 700.0;

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(34.9061, -117.8839, 700)]
    [InlineData(-33.9, 151.2, 30)]
    [InlineData(89.9, 179.9, 12000)]
    public void LlaEcefRoundTrip(double lat, double lon, double alt)
    {
        var ecef = Wgs84.LlaToEcef(lat, lon, alt);
        Wgs84.EcefToLla(ecef, out double lat2, out double lon2, out double alt2);
        Assert.Equal(lat, lat2, 9);
        Assert.Equal(lon, lon2, 9);
        Assert.Equal(alt, alt2, 6); // sub-micrometer
    }

    [Fact]
    public void NedRoundTripThroughLla()
    {
        var ned = new Vec3d(45000, 3000, -8000);
        Wgs84.NedToLla(LatEdwards, LonEdwards, AltEdwards, ned, out double lat, out double lon, out double alt);
        var back = Wgs84.LlaToNed(LatEdwards, LonEdwards, AltEdwards, lat, lon, alt);
        Assert.Equal(ned.X, back.X, 6);
        Assert.Equal(ned.Y, back.Y, 6);
        Assert.Equal(ned.Z, back.Z, 6);
    }

    [Fact]
    public void OriginMapsToZeroNed()
    {
        var ned = Wgs84.LlaToNed(LatEdwards, LonEdwards, AltEdwards, LatEdwards, LonEdwards, AltEdwards);
        Assert.True(ned.Length < 1e-6);
    }

    [Fact]
    public void NedAxesPointCorrectly()
    {
        // Moving north (positive N) should increase latitude; down (positive D) should decrease altitude.
        // (Altitude rises ~8 cm at 1 km on the tangent plane as the ellipsoid curves away.)
        Wgs84.NedToLla(LatEdwards, LonEdwards, AltEdwards, new Vec3d(1000, 0, 0), out double latN, out _, out double altN);
        Assert.True(latN > LatEdwards);
        Assert.True(System.Math.Abs(altN - AltEdwards) < 1.0);

        Wgs84.NedToLla(LatEdwards, LonEdwards, AltEdwards, new Vec3d(0, 0, 1000), out _, out _, out double altD);
        Assert.True(altD < AltEdwards);

        Wgs84.NedToLla(LatEdwards, LonEdwards, AltEdwards, new Vec3d(0, 1000, 0), out _, out double lonE, out _);
        Assert.True(lonE > LonEdwards); // east
    }

    [Fact]
    public void FlatEarthAltitudeErrorGrowsQuadratically()
    {
        // The NED tangent plane diverges from the ellipsoid at range (error ~ d^2/2R).
        // At 100 km horizontal, a point at NED down=0 sits ~786 m ABOVE the true surface,
        // so its ellipsoidal altitude reads ~786 m higher than the origin.
        Wgs84.NedToLla(LatEdwards, LonEdwards, AltEdwards, new Vec3d(100000, 0, 0), out _, out _, out double alt);
        double rise = alt - AltEdwards;
        Assert.InRange(rise, 700, 850);
    }
}
