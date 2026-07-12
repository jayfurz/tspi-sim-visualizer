using System.Collections.Generic;
using Tspi.Sim.Manifest;

namespace Tspi.Sim.Engine;

/// <summary>
/// Serializes the scenario environment (atmosphere + wind) to/from the opaque footer
/// dictionary, so a later `tspi append` reconstructs the same air mass the original
/// run flew in instead of silently flying in still air.
/// </summary>
public static class EnvironmentSerialization
{
    public static Dictionary<string, object> ToJson(EnvironmentSpec env)
    {
        var d = new Dictionary<string, object> { { "atmosphere", env.Atmosphere } };
        if (env.Wind is { } w)
        {
            var wind = new Dictionary<string, object>();
            if (w.ConstantNedMps is { Length: 3 } c)
                wind["constant_ned_mps"] = new List<object> { c[0], c[1], c[2] };
            if (w.Layers is { Count: > 0 })
            {
                var layers = new List<object>();
                foreach (var l in w.Layers)
                    layers.Add(new Dictionary<string, object>
                    {
                        { "alt_msl_m", l.AltMslM }, { "from_deg", l.FromDeg }, { "speed_mps", l.SpeedMps },
                    });
                wind["layers"] = layers;
            }
            if (w.Gusts is { } g)
                wind["gusts"] = new Dictionary<string, object>
                {
                    { "model", g.Model }, { "sigma_mps", g.SigmaMps }, { "tau_s", g.TauS },
                };
            d["wind"] = wind;
        }
        return d;
    }

    public static EnvironmentSpec FromJson(Dictionary<string, object> d)
    {
        var env = new EnvironmentSpec();
        if (d == null) return env;
        if (d.TryGetValue("atmosphere", out var atm) && atm is string s) env.Atmosphere = s;
        if (d.TryGetValue("wind", out var wo) && wo is Dictionary<string, object> wd)
        {
            var wind = new WindSpec();
            if (wd.TryGetValue("constant_ned_mps", out var co) && co is List<object> cl && cl.Count == 3)
                wind.ConstantNedMps = new[] { AsD(cl[0]), AsD(cl[1]), AsD(cl[2]) };
            if (wd.TryGetValue("layers", out var lo) && lo is List<object> ll)
            {
                wind.Layers = new List<WindLayer>();
                foreach (var item in ll)
                    if (item is Dictionary<string, object> ld)
                        wind.Layers.Add(new WindLayer
                        {
                            AltMslM = AsD(ld.GetValueOrDefault("alt_msl_m")),
                            FromDeg = AsD(ld.GetValueOrDefault("from_deg")),
                            SpeedMps = AsD(ld.GetValueOrDefault("speed_mps")),
                        });
            }
            if (wd.TryGetValue("gusts", out var go) && go is Dictionary<string, object> gd)
                wind.Gusts = new GustSpec
                {
                    Model = gd.GetValueOrDefault("model") as string ?? "gauss_markov",
                    SigmaMps = AsD(gd.GetValueOrDefault("sigma_mps")),
                    TauS = AsD(gd.GetValueOrDefault("tau_s")),
                };
            env.Wind = wind;
        }
        return env;
    }

    private static double AsD(object? o) => o switch
    {
        double d => d,
        long l => l,
        int i => i,
        _ => 0.0,
    };
}
