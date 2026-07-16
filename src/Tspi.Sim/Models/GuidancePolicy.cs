using System;
using System.Collections.Generic;
using System.IO;

namespace Tspi.Sim.Models;

/// <summary>
/// Learned guidance policy (tspi-policy/1): a small feed-forward network with a fixed,
/// versioned observation contract. Inference is hand-rolled f64 — no ML runtime
/// dependency — so "same weights, same input → same command" holds to the same
/// standard as the rest of the sim (per-platform; tanh is transcendental, see
/// docs/CONVENTIONS.md). Policy files resolve like vehicle models and their SHA-256
/// lands in the provenance `models` map. All weights are NOTIONAL, like every model
/// in this repo.
/// </summary>
public sealed class GuidancePolicy
{
    public string Schema { get; set; } = "";
    /// <summary>"mlp" — the only v1 network shape.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Observation contract id; "los_v1" is the only v1 contract (see MlpGuidanceLaw).</summary>
    public string Obs { get; set; } = "";
    public PolicyNorm Norm { get; set; } = new();
    public List<PolicyLayer> Layers { get; set; } = new();

    public const string SchemaId = "tspi-policy/1";
    public const string ObsLosV1 = "los_v1";
    /// <summary>los_v1 observation: [range, closingSpeed, selfSpeed, |losRate|], each normalized.</summary>
    public const int ObsLosV1Size = 4;
    /// <summary>Output: accel command in the LOS frame [a_e1, a_e2, a_e3] × norm.accel_mps2.</summary>
    public const int OutputSize = 3;

    /// <summary>Plain dense forward pass, f64 throughout.</summary>
    public double[] Forward(double[] x)
    {
        double[] cur = x;
        foreach (var layer in Layers)
        {
            var next = new double[layer.B.Length];
            for (int i = 0; i < next.Length; i++)
            {
                double sum = layer.B[i];
                double[] row = layer.W[i];
                for (int j = 0; j < row.Length; j++) sum += row[j] * cur[j];
                next[i] = layer.Act switch
                {
                    "tanh" => System.Math.Tanh(sum),
                    "relu" => sum > 0.0 ? sum : 0.0,
                    _ => sum, // "linear"
                };
            }
            cur = next;
        }
        return cur;
    }

    /// <summary>Structural validation; throws InvalidDataException with a user-facing message.</summary>
    public void Validate()
    {
        if (Schema != SchemaId)
            throw new InvalidDataException($"schema must be '{SchemaId}' (got '{Schema}')");
        if (Kind != "mlp")
            throw new InvalidDataException($"kind must be 'mlp' (got '{Kind}')");
        if (Obs != ObsLosV1)
            throw new InvalidDataException($"obs must be '{ObsLosV1}' (got '{Obs}')");
        if (!(Norm.RangeM > 0) || !(Norm.SpeedMps > 0) || !(Norm.OmegaRps > 0) || !(Norm.AccelMps2 > 0))
            throw new InvalidDataException("norm values must all be > 0");
        if (Layers.Count == 0)
            throw new InvalidDataException("layers must be non-empty");

        int width = ObsLosV1Size;
        for (int li = 0; li < Layers.Count; li++)
        {
            var layer = Layers[li];
            if (layer.Act != "tanh" && layer.Act != "relu" && layer.Act != "linear")
                throw new InvalidDataException($"layers[{li}].act must be tanh|relu|linear (got '{layer.Act}')");
            if (layer.W.Length == 0 || layer.B.Length != layer.W.Length)
                throw new InvalidDataException($"layers[{li}]: w must have one row per bias entry");
            foreach (double b in layer.B)
                if (!double.IsFinite(b)) throw new InvalidDataException($"layers[{li}]: non-finite bias");
            foreach (double[] row in layer.W)
            {
                if (row == null || row.Length != width)
                    throw new InvalidDataException($"layers[{li}]: every w row needs {width} inputs (previous layer width)");
                foreach (double w in row)
                    if (!double.IsFinite(w)) throw new InvalidDataException($"layers[{li}]: non-finite weight");
            }
            width = layer.B.Length;
        }
        if (width != OutputSize)
            throw new InvalidDataException($"final layer must output {OutputSize} values [a_e1, a_e2, a_e3] (got {width})");
    }
}

public sealed class PolicyNorm
{
    public double RangeM { get; set; } = 20000;
    public double SpeedMps { get; set; } = 1000;
    public double OmegaRps { get; set; } = 1;
    public double AccelMps2 { get; set; } = 100;
}

public sealed class PolicyLayer
{
    /// <summary>Row-major weights: w[out][in].</summary>
    public double[][] W { get; set; } = Array.Empty<double[]>();
    public double[] B { get; set; } = Array.Empty<double>();
    public string Act { get; set; } = "tanh";
}
