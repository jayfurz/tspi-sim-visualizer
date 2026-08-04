using System.Collections.Generic;
using Tspi.Core.IO;
using Tspi.Sim.Manifest;
using Tspi.Sim.Models;

namespace Tspi.Sim.Engine;

/// <summary>
/// THE swap seam for munition fly-out generation. Everything that turns a launch
/// into a trajectory — dynamics, guidance binding, endgame/fuzing — lives behind
/// this interface, in its own file. To replace the generator (higher-fidelity
/// 6-DoF, an externally trained NN fly-out producer, a hardware-in-the-loop
/// proxy), add ONE new file (or plugin assembly) implementing this interface and
/// set <see cref="MunitionTrajectoryModels.Default"/> at startup; nothing else in
/// the engine changes. For guidance-only changes, don't swap this at all — keep
/// <see cref="PointMassMunitionModel"/> and implement the inner
/// <see cref="IGuidanceLaw"/> seam instead.
///
/// Contract every implementation must honor:
/// - The returned trajectory is on the fixed dt grid: T0Sec == LaunchSec,
///   DtSec == request DtSec, gap-free; positions/velocities in scene NED,
///   attitude body->NED per sample (docs/CONVENTIONS.md).
/// - Events start with a `launch` entry at LaunchSec (SrcOrd/DstOrd =
///   Ord/TargetOrd) and end with the terminal story: `intercept` (+ its `cpa`),
///   a lone `cpa` on a miss, `ground_impact`, or `expire`; `miss_m` rides in
///   event Data, rounded to mm.
/// - Never integrate past min(DurationS, LaunchSec + Model.MaxFlightTimeS).
/// - Determinism: identical request => identical output. All randomness must
///   come from RngStream(Seed, label) with labels keyed by Spec.Id (e.g.
///   "wind:" + Spec.Id) — never ambient RNG, time, or shared mutable state —
///   so adding a munition never perturbs another's draws.
/// </summary>
public interface IMunitionTrajectoryModel
{
    (Trajectory Trajectory, List<TspiEventEntry> Events) Flyout(MunitionFlyoutRequest request);
}

/// <summary>Everything a trajectory model may draw on. Carried as one object so the
/// interface stays stable when inputs grow (new fields are additive).</summary>
public sealed class MunitionFlyoutRequest
{
    /// <summary>Manifest spec: ids, launch condition (incl. eject kick), guidance binding.</summary>
    public required MunitionSpec Spec { get; init; }
    public required VehicleModel Model { get; init; }
    /// <summary>For resolving guidance policy files (tspi-policy/1).</summary>
    public required ModelLibrary Models { get; init; }
    public required Environment Environment { get; init; }
    public required ulong Seed { get; init; }
    public required double DtSec { get; init; }
    public required double LaunchSec { get; init; }
    /// <summary>Launcher state over time; the munition inherits its velocity at LaunchSec.</summary>
    public required ITargetTrack ParentTrack { get; init; }
    public required ITargetTrack TargetTrack { get; init; }
    public required uint Ord { get; init; }
    public required uint TargetOrd { get; init; }
    public required double OriginAltM { get; init; }
    public required double DurationS { get; init; }
}

/// <summary>The composition root's single selection point. Swap the whole munition
/// trajectory generator here; leave it alone to fly the stock point-mass model.</summary>
public static class MunitionTrajectoryModels
{
    public static IMunitionTrajectoryModel Default { get; set; } = new PointMassMunitionModel();
}
