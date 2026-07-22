using System.Collections.Generic;

namespace Tspi.Sim.Manifest;

// POCO model of the tspi-scenario/1 JSON manifest. Serialized with snake_case
// property names and strict unknown-member rejection (see ManifestJson).
// Optionality philosophy: everything beyond identity + initial state has a default.

public sealed class ScenarioManifest
{
    public string Schema { get; set; } = "";
    public string Name { get; set; } = "scenario";
    public ulong Seed { get; set; }
    public SceneSpec Scene { get; set; } = new();
    public OutputSpec Output { get; set; } = new();
    public List<EntitySpec> Entities { get; set; } = new();

    public const string SchemaId = "tspi-scenario/1";
}

public sealed class SceneSpec
{
    public OriginLla OriginLla { get; set; } = new();
    /// <summary>Absolute UTC time of sim t=0, ISO-8601.</summary>
    public string Epoch { get; set; } = "2026-01-01T00:00:00Z";
    public double DurationS { get; set; } = 100.0;
    public double DtS { get; set; } = 0.01;
    public EnvironmentSpec Environment { get; set; } = new();
}

public sealed class OriginLla
{
    public double LatDeg { get; set; }
    public double LonDeg { get; set; }
    public double AltM { get; set; }
}

public sealed class EnvironmentSpec
{
    /// <summary>"exp8500" (rho0 * exp(-h/8500 m)) or "none" (vacuum: no drag).</summary>
    public string Atmosphere { get; set; } = "exp8500";
    public WindSpec? Wind { get; set; }
}

public sealed class WindSpec
{
    /// <summary>Constant NED wind vector; mutually exclusive with layers.</summary>
    public double[]? ConstantNedMps { get; set; }
    /// <summary>Altitude-layered profile (interpolated); mutually exclusive with constant.</summary>
    public List<WindLayer>? Layers { get; set; }
    public GustSpec? Gusts { get; set; }
}

public sealed class WindLayer
{
    public double AltMslM { get; set; }
    /// <summary>Meteorological convention: direction the wind blows FROM, degrees true.</summary>
    public double FromDeg { get; set; }
    public double SpeedMps { get; set; }
}

public sealed class GustSpec
{
    /// <summary>"gauss_markov": first-order Gauss-Markov (Ornstein-Uhlenbeck) gusts per axis. Seeded, deterministic.</summary>
    public string Model { get; set; } = "gauss_markov";
    public double SigmaMps { get; set; } = 1.0;
    public double TauS { get; set; } = 5.0;
}

public sealed class OutputSpec
{
    /// <summary>Path template relative to the manifest directory. Placeholders: {name}, {seed}, {seed:0N}.</summary>
    public string TrajectoryFile { get; set; } = "runs/{name}-{seed:04}.tspi";
}

public sealed class EntitySpec
{
    public string Id { get; set; } = "";
    public string Team { get; set; } = "gray";
    public string Type { get; set; } = "aircraft";
    public string Model { get; set; } = "";
    public InitialState Initial { get; set; } = new();
    public DispersionSpec? Dispersions { get; set; }
    public List<ManeuverSegment> Maneuvers { get; set; } = new();
    public List<MunitionSpec> Munitions { get; set; } = new();
}

public sealed class InitialState
{
    public double[] PosNedM { get; set; } = new double[3];
    public double[] VelNedMps { get; set; } = new double[3];
    /// <summary>Optional yaw/pitch/roll degrees (3-2-1). Absent: velocity-aligned, wings level.</summary>
    public double[]? AttYprDeg { get; set; }
}

public sealed class DispersionSpec
{
    /// <summary>1-sigma Gaussian dispersion applied per NED axis at t=0 (Monte Carlo).</summary>
    public double[]? PosNedSigmaM { get; set; }
    public double[]? VelNedSigmaMps { get; set; }
}

/// <summary>
/// Maneuvers decompose into independent lateral/vertical/speed channels, the way
/// autopilot modes do. A segment overrides only the channels it names; unnamed
/// channels keep their previous command ("hold current").
/// </summary>
public sealed class ManeuverSegment
{
    public double AtS { get; set; }
    public LateralCmd? Lateral { get; set; }
    public VerticalCmd? Vertical { get; set; }
    public SpeedCmd? Speed { get; set; }
}

public abstract class LateralCmd { }
public sealed class LateralTurnToHeading : LateralCmd
{
    public double HeadingDeg { get; set; }
    public double GLimit { get; set; } = 3.0;
}

public abstract class VerticalCmd { }
public sealed class VerticalHoldAlt : VerticalCmd
{
    public double AltMslM { get; set; }
    public double RateMps { get; set; } = 20.0;
}
public sealed class VerticalDeltaAlt : VerticalCmd
{
    /// <summary>Altitude change, positive up, relative to altitude at command activation.</summary>
    public double DeltaM { get; set; }
    public double RateMps { get; set; } = 20.0;
}

public abstract class SpeedCmd { }
public sealed class SpeedSet : SpeedCmd
{
    public double SpeedMps { get; set; }
    public double AccelMps2 { get; set; } = 3.0;
}

public sealed class MunitionSpec
{
    public string Id { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>Target entity id. Required in v1 (pronav needs a track).</summary>
    public string Target { get; set; } = "";
    /// <summary>Absent = carried but never employed.</summary>
    public LaunchSpec? Launch { get; set; }
    public GuidanceSpec? Guidance { get; set; }
}

public abstract class LaunchSpec
{
    /// <summary>Separation/booster kick added to the inherited parent velocity at birth,
    /// m/s (VLS/rail model — lets surface launchers loft; 0 = legacy inherit-only).</summary>
    public double EjectMps { get; set; }

    /// <summary>Kick elevation above horizontal, degrees; kick azimuth is the
    /// launch->target bearing (falling back to the parent heading, then north).</summary>
    public double ElevationDeg { get; set; }
}
public sealed class LaunchAtTime : LaunchSpec
{
    public double AtS { get; set; }
}
public sealed class LaunchAtRange : LaunchSpec
{
    public double LessThanM { get; set; }
}

public sealed class GuidanceSpec
{
    /// <summary>"pronav" | "ballistic" | "nn"</summary>
    public string Kind { get; set; } = "pronav";
    /// <summary>Navigation constant N (pronav). Absent: model's pronav_gain_default.</summary>
    public double? Gain { get; set; }
    /// <summary>Learned-guidance policy name (kind "nn"), resolved as {policy}.json from
    /// the model search dirs like a vehicle model.</summary>
    public string? Policy { get; set; }
}

/// <summary>tspi-addendum/1: munitions simulated later against recorded trajectories and appended to an existing file.</summary>
public sealed class AddendumManifest
{
    public string Schema { get; set; } = "";
    public ulong Seed { get; set; }
    public List<AddendumMunition> Munitions { get; set; } = new();

    public const string SchemaId = "tspi-addendum/1";
}

public sealed class AddendumMunition
{
    /// <summary>Id of the recorded entity that carries/launches this munition.</summary>
    public string Parent { get; set; } = "";
    public string Id { get; set; } = "";
    public string Model { get; set; } = "";
    public string Target { get; set; } = "";
    public LaunchSpec? Launch { get; set; }
    public GuidanceSpec? Guidance { get; set; }
}
