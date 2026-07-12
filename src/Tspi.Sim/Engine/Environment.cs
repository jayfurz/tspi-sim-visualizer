using System;
using System.Collections.Generic;
using System.Linq;
using Tspi.Core.Math;
using Tspi.Sim.Manifest;

namespace Tspi.Sim.Engine;

/// <summary>
/// Atmosphere + wind providers, selected by the manifest. Wind fidelity ladder:
/// constant vector -> altitude-layered profile -> + first-order Gauss-Markov gusts.
/// Gusts draw from a per-entity RNG stream so entity count never shifts another
/// entity's turbulence sequence.
/// </summary>
public sealed class Environment
{
    private readonly string _atmosphere;
    private readonly IWindField _wind;

    public Environment(SceneSpec scene)
    {
        _atmosphere = scene.Environment.Atmosphere;
        _wind = BuildWind(scene.Environment.Wind);
    }

    /// <summary>Air density (kg/m^3) at down-coordinate posD, given origin MSL altitude originAltM.</summary>
    public double Density(double posD, double originAltM)
    {
        if (_atmosphere == "none") return 0.0;
        double altMsl = originAltM - posD; // NED down is negative-up
        const double rho0 = 1.225;
        return rho0 * System.Math.Exp(-System.Math.Max(0.0, altMsl) / 8500.0);
    }

    /// <summary>Create a per-entity wind sampler (holds gust state); safe to hold one per entity.</summary>
    public WindSampler CreateSampler(RngStream gustRng) => new WindSampler(_wind, gustRng);

    private static IWindField BuildWind(WindSpec? spec)
    {
        if (spec == null) return new NoWind();
        IWindField baseField;
        if (spec.Layers is { Count: > 0 })
            baseField = new LayeredWind(spec.Layers);
        else if (spec.ConstantNedMps is { Length: 3 } c)
            baseField = new ConstantWind(new Vec3d(c[0], c[1], c[2]));
        else
            baseField = new NoWind();
        return spec.Gusts is { } g ? new GustyWind(baseField, g) : baseField;
    }
}

public interface IWindField
{
    Vec3d MeanWind(double altMslM);
    GustSpec? Gusts { get; }
}

public sealed class NoWind : IWindField
{
    public Vec3d MeanWind(double altMslM) => Vec3d.Zero;
    public GustSpec? Gusts => null;
}

public sealed class ConstantWind : IWindField
{
    private readonly Vec3d _w;
    public ConstantWind(Vec3d w) { _w = w; }
    public Vec3d MeanWind(double altMslM) => _w;
    public GustSpec? Gusts => null;
}

/// <summary>Altitude-interpolated wind. Meteorological "from" direction -> NED vector the air moves toward.</summary>
public sealed class LayeredWind : IWindField
{
    private readonly double[] _alt;
    private readonly Vec3d[] _vec;

    public LayeredWind(List<WindLayer> layers)
    {
        var ordered = layers.OrderBy(l => l.AltMslM).ToList();
        _alt = new double[ordered.Count];
        _vec = new Vec3d[ordered.Count];
        for (int i = 0; i < ordered.Count; i++)
        {
            var l = ordered[i];
            _alt[i] = l.AltMslM;
            double toRad = (l.FromDeg + 180.0) * MathUtil.Deg2Rad;
            _vec[i] = new Vec3d(System.Math.Cos(toRad) * l.SpeedMps, System.Math.Sin(toRad) * l.SpeedMps, 0.0);
        }
    }

    public Vec3d MeanWind(double altMslM)
    {
        if (_alt.Length == 1 || altMslM <= _alt[0]) return _vec[0];
        if (altMslM >= _alt[^1]) return _vec[^1];
        for (int i = 1; i < _alt.Length; i++)
        {
            if (altMslM <= _alt[i])
            {
                double f = (altMslM - _alt[i - 1]) / (_alt[i] - _alt[i - 1]);
                return _vec[i - 1] + (_vec[i] - _vec[i - 1]) * f;
            }
        }
        return _vec[^1];
    }

    public GustSpec? Gusts => null;
}

public sealed class GustyWind : IWindField
{
    private readonly IWindField _mean;
    public GustSpec? Gusts { get; }
    public GustyWind(IWindField mean, GustSpec gusts) { _mean = mean; Gusts = gusts; }
    public Vec3d MeanWind(double altMslM) => _mean.MeanWind(altMslM);
}

/// <summary>
/// Per-entity wind sampler. Advances a first-order Gauss-Markov gust state each step
/// (exact discretization: phi = exp(-dt/tau); stationary sigma preserved). Must be
/// stepped once per integrator step with the same dt to stay deterministic.
/// </summary>
public sealed class WindSampler
{
    private readonly IWindField _field;
    private readonly RngStream _rng;
    private Vec3d _gust = Vec3d.Zero;
    private readonly bool _hasGust;
    private readonly double _sigma;
    private readonly double _tau;

    public WindSampler(IWindField field, RngStream rng)
    {
        _field = field;
        _rng = rng;
        if (field.Gusts is { } g) { _hasGust = true; _sigma = g.SigmaMps; _tau = g.TauS; }
    }

    public void Step(double dt)
    {
        if (!_hasGust) return;
        double phi = System.Math.Exp(-dt / _tau);
        double q = _sigma * System.Math.Sqrt(1.0 - phi * phi);
        _gust = new Vec3d(
            phi * _gust.X + q * _rng.NextGaussian(),
            phi * _gust.Y + q * _rng.NextGaussian(),
            phi * _gust.Z + q * _rng.NextGaussian());
    }

    public Vec3d Wind(double altMslM) => _field.MeanWind(altMslM) + (_hasGust ? _gust : Vec3d.Zero);
}
