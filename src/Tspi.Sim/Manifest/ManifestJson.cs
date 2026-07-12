using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Tspi.Sim.Models;

namespace Tspi.Sim.Manifest;

/// <summary>
/// Manifest (de)serialization: snake_case, comments + trailing commas tolerated,
/// unknown members rejected (typos fail loudly instead of silently doing nothing).
/// The channel commands and launch conditions are discriminated unions on
/// "kind" / "when" handled by the converters below.
/// </summary>
public static class ManifestJson
{
    public static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers =
                {
                    static ti =>
                    {
                        if (ti.Kind == JsonTypeInfoKind.Object &&
                            ti.Type.Namespace is { } ns && ns.StartsWith("Tspi.Sim", StringComparison.Ordinal))
                        {
                            ti.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
                        }
                    }
                }
            },
        };
        options.Converters.Add(new LateralCmdConverter());
        options.Converters.Add(new VerticalCmdConverter());
        options.Converters.Add(new SpeedCmdConverter());
        options.Converters.Add(new LaunchSpecConverter());
        return options;
    }

    public static (ScenarioManifest Manifest, byte[] Raw, string ShaHex) LoadScenario(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        var manifest = JsonSerializer.Deserialize<ScenarioManifest>(raw, Options)
                       ?? throw new InvalidDataException("Manifest is JSON null: " + path);
        return (manifest, raw, Sha256Hex(raw));
    }

    public static (AddendumManifest Manifest, byte[] Raw, string ShaHex) LoadAddendum(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        var manifest = JsonSerializer.Deserialize<AddendumManifest>(raw, Options)
                       ?? throw new InvalidDataException("Addendum is JSON null: " + path);
        return (manifest, raw, Sha256Hex(raw));
    }

    public static string Sha256Hex(byte[] data)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
    }

    public static byte[] Sha256Bytes(byte[] data)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(data);
    }

    /// <summary>Render an output path template: {name}, {seed}, {seed:0N}. Throws on unknown placeholders.</summary>
    public static string RenderTemplate(string template, string name, ulong seed)
    {
        string result = Regex.Replace(template, @"\{seed:0(\d+)\}",
            m => seed.ToString().PadLeft(int.Parse(m.Groups[1].Value), '0'));
        result = result.Replace("{seed}", seed.ToString()).Replace("{name}", name);
        var leftover = Regex.Match(result, @"\{[^}]*\}");
        if (leftover.Success)
            throw new InvalidDataException("Unknown placeholder in output template: " + leftover.Value);
        return result;
    }
}

public sealed class ValidationResult
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool IsValid => Errors.Count == 0;
}

public static class ManifestValidator
{
    private static readonly Regex SafeName = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    public static ValidationResult Validate(ScenarioManifest m, ModelLibrary models)
    {
        var r = new ValidationResult();
        void Err(string s) => r.Errors.Add(s);
        void Warn(string s) => r.Warnings.Add(s);

        if (m.Schema != ScenarioManifest.SchemaId)
            Err($"schema must be '{ScenarioManifest.SchemaId}' (got '{m.Schema}')");
        if (string.IsNullOrEmpty(m.Name) || !SafeName.IsMatch(m.Name))
            Err("name must be non-empty and match [A-Za-z0-9._-]+ (used in output filenames)");

        var s = m.Scene;
        if (s.DurationS <= 0) Err("scene.duration_s must be > 0");
        if (s.DtS <= 0 || s.DtS > 1.0) Err("scene.dt_s must be in (0, 1]");
        if (s.DurationS > 0 && s.DtS > 0 && s.DurationS / s.DtS > 100_000_000)
            Err("scene.duration_s / dt_s exceeds 100M steps; split the scenario");
        if (System.Math.Abs(s.OriginLla.LatDeg) > 90) Err("origin_lla.lat_deg out of [-90, 90]");
        if (System.Math.Abs(s.OriginLla.LonDeg) > 180) Err("origin_lla.lon_deg out of [-180, 180]");
        if (!DateTimeOffset.TryParse(s.Epoch, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out _))
            Err($"scene.epoch is not a parseable ISO-8601 time: '{s.Epoch}'");
        if (s.Environment.Atmosphere != "exp8500" && s.Environment.Atmosphere != "none")
            Err($"environment.atmosphere must be 'exp8500' or 'none' (got '{s.Environment.Atmosphere}')");
        if (s.Environment.Wind is { } wind)
        {
            if (wind.ConstantNedMps != null && wind.Layers != null)
                Err("environment.wind: constant_ned_mps and layers are mutually exclusive");
            if (wind.ConstantNedMps is { Length: not 3 })
                Err("environment.wind.constant_ned_mps must have 3 components");
            if (wind.Layers is { Count: 0 })
                Err("environment.wind.layers must be non-empty when present");
            if (wind.Gusts is { } g)
            {
                if (g.Model != "gauss_markov") Err($"wind.gusts.model must be 'gauss_markov' (got '{g.Model}')");
                if (g.SigmaMps < 0 || g.TauS <= 0) Err("wind.gusts: sigma_mps must be >= 0 and tau_s > 0");
            }
        }

        if (m.Entities.Count == 0) Err("entities must be non-empty");

        var ids = new HashSet<string>();
        var entityIds = new HashSet<string>(m.Entities.Select(e => e.Id));
        foreach (var e in m.Entities)
        {
            string where = $"entity '{e.Id}'";
            if (string.IsNullOrEmpty(e.Id)) Err("every entity needs a non-empty id");
            else if (!ids.Add(e.Id)) Err($"duplicate id '{e.Id}'");
            if (e.Type != "aircraft")
                Err($"{where}: only type 'aircraft' is supported at top level in v1 (got '{e.Type}')");
            if (e.Initial.PosNedM.Length != 3) Err($"{where}: initial.pos_ned_m must have 3 components");
            if (e.Initial.VelNedMps.Length != 3) Err($"{where}: initial.vel_ned_mps must have 3 components");
            if (e.Initial.AttYprDeg is { Length: not 3 }) Err($"{where}: initial.att_ypr_deg must have 3 components");
            if (e.Initial.VelNedMps.Length == 3 &&
                new Tspi.Core.Math.Vec3d(e.Initial.VelNedMps[0], e.Initial.VelNedMps[1], e.Initial.VelNedMps[2]).Length < 1.0)
                Warn($"{where}: near-zero initial velocity; attitude will default and channels need airspeed to act");
            if (e.Dispersions is { } disp)
            {
                if (disp.PosNedSigmaM is { Length: not 3 }) Err($"{where}: dispersions.pos_ned_sigma_m must have 3 components");
                if (disp.VelNedSigmaMps is { Length: not 3 }) Err($"{where}: dispersions.vel_ned_sigma_mps must have 3 components");
            }

            if (!models.TryResolve(e.Model, out var model, out _, out string mErr))
                Err($"{where}: model '{e.Model}': {mErr}");
            else if (model!.Kind != "aircraft")
                Err($"{where}: model '{e.Model}' has kind '{model.Kind}', expected 'aircraft'");

            foreach (var seg in e.Maneuvers)
            {
                if (seg.AtS < 0) Err($"{where}: maneuver at_s must be >= 0");
                if (seg.AtS > s.DurationS) Warn($"{where}: maneuver at t={seg.AtS}s starts after scenario end");
                // Commands activate on the dt sample grid; an off-grid at_s is snapped to the
                // next sample so RK4 never integrates across a mid-step command discontinuity.
                if (s.DtS > 0 && System.Math.Abs(seg.AtS / s.DtS - System.Math.Round(seg.AtS / s.DtS)) > 1e-6)
                    Warn($"{where}: maneuver at_s={seg.AtS}s is not a multiple of dt={s.DtS}s; it will snap to the next sample");
                if (seg.Lateral is null && seg.Vertical is null && seg.Speed is null)
                    Warn($"{where}: maneuver at t={seg.AtS}s commands no channel (no-op)");
                if (seg.Lateral is LateralTurnToHeading turn && turn.GLimit <= 0)
                    Err($"{where}: turn_to_heading g_limit must be > 0");
                if (seg.Vertical is VerticalHoldAlt ha && ha.RateMps <= 0)
                    Err($"{where}: hold_alt rate_mps must be > 0");
                if (seg.Vertical is VerticalDeltaAlt da && da.RateMps <= 0)
                    Err($"{where}: delta_alt rate_mps must be > 0");
                if (seg.Speed is SpeedSet sp && (sp.SpeedMps < 0 || sp.AccelMps2 <= 0))
                    Err($"{where}: speed set needs speed_mps >= 0 and accel_mps2 > 0");
            }

            foreach (var mun in e.Munitions)
            {
                string mwhere = $"munition '{mun.Id}' on '{e.Id}'";
                if (string.IsNullOrEmpty(mun.Id)) Err($"{where}: every munition needs a non-empty id");
                else if (!ids.Add(mun.Id)) Err($"duplicate id '{mun.Id}'");
                if (string.IsNullOrEmpty(mun.Target)) Err($"{mwhere}: target is required in v1 (pronav needs a track)");
                else if (!entityIds.Contains(mun.Target)) Err($"{mwhere}: target '{mun.Target}' is not a declared entity");
                else if (mun.Target == e.Id) Err($"{mwhere}: cannot target its own parent");
                if (!models.TryResolve(mun.Model, out var mm, out _, out string mmErr))
                    Err($"{mwhere}: model '{mun.Model}': {mmErr}");
                else if (mm!.Kind != "munition")
                    Err($"{mwhere}: model '{mun.Model}' has kind '{mm.Kind}', expected 'munition'");
                if (mun.Launch is null)
                    Warn($"{mwhere}: no launch condition; it will be carried but never employed");
                if (mun.Launch is LaunchAtTime lt && (lt.AtS < 0 || lt.AtS > s.DurationS))
                    Warn($"{mwhere}: launch at_s={lt.AtS}s is outside the scenario window");
                if (mun.Launch is LaunchAtRange lr && lr.LessThanM <= 0)
                    Err($"{mwhere}: launch less_than_m must be > 0");
                if (mun.Guidance is { } gd && gd.Kind != "pronav" && gd.Kind != "ballistic")
                    Err($"{mwhere}: guidance.kind must be 'pronav' or 'ballistic' (got '{gd.Kind}')");
            }
        }

        try { ManifestJson.RenderTemplate(m.Output.TrajectoryFile, m.Name, m.Seed); }
        catch (Exception ex) { Err("output.trajectory_file: " + ex.Message); }

        return r;
    }

    public static ValidationResult ValidateAddendum(AddendumManifest a, ModelLibrary models)
    {
        var r = new ValidationResult();
        if (a.Schema != AddendumManifest.SchemaId)
            r.Errors.Add($"schema must be '{AddendumManifest.SchemaId}' (got '{a.Schema}')");
        if (a.Munitions.Count == 0)
            r.Errors.Add("munitions must be non-empty");
        var ids = new HashSet<string>();
        foreach (var mun in a.Munitions)
        {
            string where = $"munition '{mun.Id}'";
            if (string.IsNullOrEmpty(mun.Id)) r.Errors.Add("every munition needs a non-empty id");
            else if (!ids.Add(mun.Id)) r.Errors.Add($"duplicate id '{mun.Id}'");
            if (string.IsNullOrEmpty(mun.Parent)) r.Errors.Add($"{where}: parent is required");
            if (string.IsNullOrEmpty(mun.Target)) r.Errors.Add($"{where}: target is required");
            if (mun.Launch is null) r.Errors.Add($"{where}: launch is required in an addendum");
            if (!models.TryResolve(mun.Model, out var mm, out _, out string err))
                r.Errors.Add($"{where}: model '{mun.Model}': {err}");
            else if (mm!.Kind != "munition")
                r.Errors.Add($"{where}: model '{mun.Model}' has kind '{mm.Kind}', expected 'munition'");
        }
        return r;
    }
}

// ---------------- discriminated-union converters ----------------

internal abstract class UnionConverter<T> : JsonConverter<T> where T : class
{
    protected abstract string DiscriminatorName { get; }
    protected abstract T FromElement(string kind, JsonElement obj);
    protected abstract (string Kind, Dictionary<string, object?> Fields) ToFields(T value);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var obj = doc.RootElement;
        if (obj.ValueKind != JsonValueKind.Object)
            throw new JsonException(typeToConvert.Name + " must be a JSON object");
        if (!obj.TryGetProperty(DiscriminatorName, out var kindEl) || kindEl.ValueKind != JsonValueKind.String)
            throw new JsonException(typeToConvert.Name + " requires a string '" + DiscriminatorName + "' property");
        return FromElement(kindEl.GetString()!, obj);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        var (kind, fields) = ToFields(value);
        writer.WriteStartObject();
        writer.WriteString(DiscriminatorName, kind);
        foreach (var (name, v) in fields)
        {
            switch (v)
            {
                case null: break;
                case double d: writer.WriteNumber(name, d); break;
                case string s: writer.WriteString(name, s); break;
                default: throw new JsonException("Unsupported union field type");
            }
        }
        writer.WriteEndObject();
    }

    /// <summary>Strict field reader: every property must be the discriminator or in the allowed set.</summary>
    protected static double GetDouble(JsonElement obj, string name, double? fallback = null)
    {
        if (obj.TryGetProperty(name, out var el))
        {
            if (el.ValueKind != JsonValueKind.Number) throw new JsonException("'" + name + "' must be a number");
            return el.GetDouble();
        }
        if (fallback.HasValue) return fallback.Value;
        throw new JsonException("missing required property '" + name + "'");
    }

    protected void RejectUnknown(JsonElement obj, params string[] allowed)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (p.Name == DiscriminatorName) continue;
            if (Array.IndexOf(allowed, p.Name) < 0)
                throw new JsonException("unknown property '" + p.Name + "' in " + GetType().Name.Replace("Converter", ""));
        }
    }
}

internal sealed class LateralCmdConverter : UnionConverter<LateralCmd>
{
    protected override string DiscriminatorName => "kind";

    protected override LateralCmd FromElement(string kind, JsonElement obj) => kind switch
    {
        "hold" => Done(obj, new LateralHold()),
        "turn_to_heading" => TurnFrom(obj),
        _ => throw new JsonException($"unknown lateral kind '{kind}' (hold | turn_to_heading)"),
    };

    private LateralCmd Done(JsonElement obj, LateralCmd cmd) { RejectUnknown(obj); return cmd; }

    private LateralCmd TurnFrom(JsonElement obj)
    {
        RejectUnknown(obj, "heading_deg", "g_limit");
        return new LateralTurnToHeading
        {
            HeadingDeg = GetDouble(obj, "heading_deg"),
            GLimit = GetDouble(obj, "g_limit", 3.0),
        };
    }

    protected override (string, Dictionary<string, object?>) ToFields(LateralCmd value) => value switch
    {
        LateralHold => ("hold", new Dictionary<string, object?>()),
        LateralTurnToHeading t => ("turn_to_heading", new Dictionary<string, object?>
            { { "heading_deg", t.HeadingDeg }, { "g_limit", t.GLimit } }),
        _ => throw new JsonException("unknown LateralCmd"),
    };
}

internal sealed class VerticalCmdConverter : UnionConverter<VerticalCmd>
{
    protected override string DiscriminatorName => "kind";

    protected override VerticalCmd FromElement(string kind, JsonElement obj)
    {
        switch (kind)
        {
            case "hold":
                RejectUnknown(obj);
                return new VerticalHold();
            case "hold_alt":
                RejectUnknown(obj, "alt_msl_m", "rate_mps");
                return new VerticalHoldAlt { AltMslM = GetDouble(obj, "alt_msl_m"), RateMps = GetDouble(obj, "rate_mps", 20.0) };
            case "delta_alt":
                RejectUnknown(obj, "delta_m", "rate_mps");
                return new VerticalDeltaAlt { DeltaM = GetDouble(obj, "delta_m"), RateMps = GetDouble(obj, "rate_mps", 20.0) };
            default:
                throw new JsonException($"unknown vertical kind '{kind}' (hold | hold_alt | delta_alt)");
        }
    }

    protected override (string, Dictionary<string, object?>) ToFields(VerticalCmd value) => value switch
    {
        VerticalHold => ("hold", new Dictionary<string, object?>()),
        VerticalHoldAlt h => ("hold_alt", new Dictionary<string, object?>
            { { "alt_msl_m", h.AltMslM }, { "rate_mps", h.RateMps } }),
        VerticalDeltaAlt d => ("delta_alt", new Dictionary<string, object?>
            { { "delta_m", d.DeltaM }, { "rate_mps", d.RateMps } }),
        _ => throw new JsonException("unknown VerticalCmd"),
    };
}

internal sealed class SpeedCmdConverter : UnionConverter<SpeedCmd>
{
    protected override string DiscriminatorName => "kind";

    protected override SpeedCmd FromElement(string kind, JsonElement obj)
    {
        switch (kind)
        {
            case "hold":
                RejectUnknown(obj);
                return new SpeedHold();
            case "set":
                RejectUnknown(obj, "speed_mps", "accel_mps2");
                return new SpeedSet { SpeedMps = GetDouble(obj, "speed_mps"), AccelMps2 = GetDouble(obj, "accel_mps2", 3.0) };
            default:
                throw new JsonException($"unknown speed kind '{kind}' (hold | set)");
        }
    }

    protected override (string, Dictionary<string, object?>) ToFields(SpeedCmd value) => value switch
    {
        SpeedHold => ("hold", new Dictionary<string, object?>()),
        SpeedSet s => ("set", new Dictionary<string, object?>
            { { "speed_mps", s.SpeedMps }, { "accel_mps2", s.AccelMps2 } }),
        _ => throw new JsonException("unknown SpeedCmd"),
    };
}

internal sealed class LaunchSpecConverter : UnionConverter<LaunchSpec>
{
    protected override string DiscriminatorName => "when";

    protected override LaunchSpec FromElement(string kind, JsonElement obj)
    {
        switch (kind)
        {
            case "time":
                RejectUnknown(obj, "at_s");
                return new LaunchAtTime { AtS = GetDouble(obj, "at_s") };
            case "range_to_target":
                RejectUnknown(obj, "less_than_m");
                return new LaunchAtRange { LessThanM = GetDouble(obj, "less_than_m") };
            default:
                throw new JsonException($"unknown launch condition '{kind}' (time | range_to_target)");
        }
    }

    protected override (string, Dictionary<string, object?>) ToFields(LaunchSpec value) => value switch
    {
        LaunchAtTime t => ("time", new Dictionary<string, object?> { { "at_s", t.AtS } }),
        LaunchAtRange r => ("range_to_target", new Dictionary<string, object?> { { "less_than_m", r.LessThanM } }),
        _ => throw new JsonException("unknown LaunchSpec"),
    };
}
