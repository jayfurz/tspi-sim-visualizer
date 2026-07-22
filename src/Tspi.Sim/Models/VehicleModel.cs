using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Tspi.Sim.Models;

/// <summary>
/// Data-driven vehicle model (tspi-model/1). All parameters are NOTIONAL —
/// keep them generic; real aero/performance data does not belong in this repo.
/// Aircraft use the g/accel limits; munitions use boost/drag/guidance/endgame.
/// </summary>
public sealed class VehicleModel
{
    public string Schema { get; set; } = "";
    /// <summary>"aircraft" | "munition"</summary>
    public string Kind { get; set; } = "";
    public double MassKg { get; set; } = 1000;

    // aircraft envelope
    public double GLimitMax { get; set; } = 9.0;
    public double AccelLongMaxMps2 { get; set; } = 6.0;
    public double AccelVertMaxMps2 { get; set; } = 30.0;
    public double SpeedMaxMps { get; set; } = 600.0;

    // munition
    public BoostSpec? Boost { get; set; }
    public double DragCdaM2 { get; set; }
    public double PronavGainDefault { get; set; } = 4.0;
    public double FuzeRadiusM { get; set; } = 10.0;
    public double MaxFlightTimeS { get; set; } = 120.0;

    /// <summary>
    /// Optional rigid-body rotational dynamics (aircraft only). Presence selects the
    /// torque-integrated attitude path; absence keeps flight-path attitude synthesis.
    /// </summary>
    public RotationalSpec? Rotational { get; set; }

    public const string SchemaId = "tspi-model/1";
}

/// <summary>
/// Rigid-body rotational parameters, all NOTIONAL. Inertia is the principal-axis
/// (diagonal) tensor in body axes; control torque models aggregate surface authority.
/// </summary>
public sealed class RotationalSpec
{
    /// <summary>Principal moments of inertia [Ixx, Iyy, Izz], body axes, kg·m².</summary>
    public double[] InertiaKgm2 { get; set; } = new double[3];
    /// <summary>Control torque authority per body axis [roll, pitch, yaw], N·m.</summary>
    public double[] MaxTorqueNm { get; set; } = new double[3];
    /// <summary>Attitude-error proportional gain, 1/s² (default: ~3 rad/s bandwidth).</summary>
    public double AttitudeKp { get; set; } = 9.0;
    /// <summary>Body-rate damping gain, 1/s (default: critically damped with Kp=9).</summary>
    public double AttitudeKd { get; set; } = 6.0;
}

public sealed class BoostSpec
{
    public double ThrustN { get; set; }
    public double DurationS { get; set; }
}

/// <summary>
/// Resolves model names to model files ({name}.json across search dirs, first hit wins)
/// and records each loaded file's SHA-256 for the provenance chain: "same manifest,
/// same seed" only implies "same output" if the models are pinned too.
/// </summary>
public sealed class ModelLibrary
{
    private readonly List<string> _searchDirs;
    private readonly Dictionary<string, (VehicleModel Model, string ShaHex)> _cache = new();
    private readonly Dictionary<string, string> _errors = new();
    private readonly Dictionary<string, (GuidancePolicy Policy, string ShaHex)> _policyCache = new();
    private readonly Dictionary<string, string> _policyErrors = new();

    public ModelLibrary(IEnumerable<string> searchDirs)
    {
        _searchDirs = new List<string>();
        foreach (var d in searchDirs)
            if (!string.IsNullOrEmpty(d) && Directory.Exists(d))
                _searchDirs.Add(Path.GetFullPath(d));
    }

    public IReadOnlyList<string> SearchDirs => _searchDirs;

    /// <summary>Hashes of every model AND guidance policy resolved so far (name -> sha256
    /// hex), for provenance: "same manifest, same seed" only pins the output if the
    /// weights that flew the munitions are pinned too.</summary>
    public IReadOnlyDictionary<string, string> LoadedHashes
    {
        get
        {
            var d = new Dictionary<string, string>();
            foreach (var kv in _cache) d[kv.Key] = kv.Value.ShaHex;
            foreach (var kv in _policyCache) d[kv.Key] = kv.Value.ShaHex;
            return d;
        }
    }

    /// <summary>Register an in-memory model (tests, generated models). Hash is of its canonical JSON.</summary>
    public void AddInMemory(string name, VehicleModel model)
    {
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(model, Manifest.ManifestJson.Options);
        _cache[name] = (model, Manifest.ManifestJson.Sha256Hex(canonical));
    }

    public bool TryResolve(string name, out VehicleModel? model, out string shaHex, out string error)
    {
        model = null;
        shaHex = "";
        error = "";
        if (string.IsNullOrEmpty(name))
        {
            error = "empty model name";
            return false;
        }
        if (_cache.TryGetValue(name, out var hit))
        {
            model = hit.Model;
            shaHex = hit.ShaHex;
            return true;
        }
        if (_errors.TryGetValue(name, out string? cachedError))
        {
            error = cachedError;
            return false;
        }
        foreach (var dir in _searchDirs)
        {
            string path = Path.Combine(dir, name + ".json");
            if (!File.Exists(path)) continue;
            try
            {
                byte[] raw = File.ReadAllBytes(path);
                var m = JsonSerializer.Deserialize<VehicleModel>(raw, Manifest.ManifestJson.Options)
                        ?? throw new InvalidDataException("model file is JSON null");
                if (m.Schema != VehicleModel.SchemaId)
                    throw new InvalidDataException($"schema must be '{VehicleModel.SchemaId}' (got '{m.Schema}')");
                if (m.Kind is not ("aircraft" or "ship" or "munition"))
                    throw new InvalidDataException($"kind must be 'aircraft', 'ship', or 'munition' (got '{m.Kind}')");
                if (m.MassKg <= 0)
                    throw new InvalidDataException("mass_kg must be > 0");
                ValidateRotational(m);
                _cache[name] = (m, Manifest.ManifestJson.Sha256Hex(raw));
                model = m;
                shaHex = _cache[name].ShaHex;
                return true;
            }
            catch (Exception ex)
            {
                error = path + ": " + ex.Message;
                _errors[name] = error;
                return false;
            }
        }
        error = "no '" + name + ".json' in search dirs [" + string.Join(", ", _searchDirs) + "]";
        _errors[name] = error;
        return false;
    }

    /// <summary>Register an in-memory guidance policy (tests, generated policies).</summary>
    public void AddInMemoryPolicy(string name, GuidancePolicy policy)
    {
        policy.Validate();
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(policy, Manifest.ManifestJson.Options);
        _policyCache[name] = (policy, Manifest.ManifestJson.Sha256Hex(canonical));
    }

    /// <summary>Resolve a guidance policy ({name}.json in the same search dirs as vehicle
    /// models); the file's SHA-256 joins LoadedHashes for provenance.</summary>
    public bool TryResolvePolicy(string name, out GuidancePolicy? policy, out string shaHex, out string error)
    {
        policy = null;
        shaHex = "";
        error = "";
        if (string.IsNullOrEmpty(name))
        {
            error = "empty policy name";
            return false;
        }
        if (_policyCache.TryGetValue(name, out var hit))
        {
            policy = hit.Policy;
            shaHex = hit.ShaHex;
            return true;
        }
        if (_policyErrors.TryGetValue(name, out string? cachedError))
        {
            error = cachedError;
            return false;
        }
        foreach (var dir in _searchDirs)
        {
            string path = Path.Combine(dir, name + ".json");
            if (!File.Exists(path)) continue;
            try
            {
                byte[] raw = File.ReadAllBytes(path);
                var p = JsonSerializer.Deserialize<GuidancePolicy>(raw, Manifest.ManifestJson.Options)
                        ?? throw new InvalidDataException("policy file is JSON null");
                p.Validate();
                _policyCache[name] = (p, Manifest.ManifestJson.Sha256Hex(raw));
                policy = p;
                shaHex = _policyCache[name].ShaHex;
                return true;
            }
            catch (Exception ex)
            {
                error = path + ": " + ex.Message;
                _policyErrors[name] = error;
                return false;
            }
        }
        error = "no '" + name + ".json' in search dirs [" + string.Join(", ", _searchDirs) + "]";
        _policyErrors[name] = error;
        return false;
    }

    private static void ValidateRotational(VehicleModel m)
    {
        if (m.Rotational is not { } rot) return;
        if (m.Kind != "aircraft")
            throw new InvalidDataException("rotational requires kind 'aircraft' (munition attitude is velocity-aligned in v1)");
        CheckAxes(rot.InertiaKgm2, "rotational.inertia_kgm2");
        CheckAxes(rot.MaxTorqueNm, "rotational.max_torque_nm");
        if (rot.AttitudeKp <= 0)
            throw new InvalidDataException("rotational.attitude_kp must be > 0");
        if (rot.AttitudeKd < 0)
            throw new InvalidDataException("rotational.attitude_kd must be >= 0");
    }

    private static void CheckAxes(double[]? v, string field)
    {
        if (v is not { Length: 3 })
            throw new InvalidDataException(field + " must be a 3-element array");
        foreach (double x in v)
            if (!(x > 0))
                throw new InvalidDataException(field + " components must be > 0");
    }
}
