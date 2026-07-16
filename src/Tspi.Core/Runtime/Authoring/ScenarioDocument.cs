using System;
using System.Collections.Generic;
using System.Text;
using Tspi.Core.Json;
using Tspi.Core.Math;

namespace Tspi.Core.Authoring
{
    /// <summary>
    /// Editable scenario manifest backed by the parsed JSON tree itself (MiniJson CLR
    /// types: insertion-ordered dictionaries, lists, long/double/string/bool). An
    /// authoring UI edits the tree in place and never forks a parallel representation,
    /// so fields this class knows nothing about (dispersions, munitions, future schema
    /// additions) survive load -&gt; edit -&gt; save untouched. Semantic correctness stays
    /// the job of `tspi validate` — this class only keeps the tree structurally sound
    /// and serializes it back to schema-shaped JSON.
    ///
    /// Shared with Unity through com.tspi.core (netstandard2.1, C# 9, zero deps):
    /// the viewer's scenario editor authors manifests with this and shells out to the
    /// real `tspi` CLI to validate and simulate — Unity itself never simulates.
    /// </summary>
    public sealed class ScenarioDocument
    {
        /// <summary>The live JSON tree. Edits here are edits to the document.</summary>
        public Dictionary<string, object> Root { get; }

        private ScenarioDocument(Dictionary<string, object> root) { Root = root; }

        // ---------------- load / save ----------------

        public static ScenarioDocument FromJson(string json)
        {
            var root = MiniJson.Parse(json) as Dictionary<string, object>;
            if (root == null) throw new FormatException("scenario manifest root must be a JSON object");
            if (!root.ContainsKey("schema"))
                throw new FormatException("not a scenario manifest: missing 'schema' key");
            return new ScenarioDocument(root);
        }

        /// <summary>Minimal valid scenario: scene block + empty entity list.</summary>
        public static ScenarioDocument New(string name, double originLatDeg, double originLonDeg,
            double originAltM, double durationS, double dtS = 0.01)
        {
            var root = new Dictionary<string, object>
            {
                { "schema", "tspi-scenario/1" },
                { "name", name },
                { "seed", 0L },
                { "scene", new Dictionary<string, object>
                    {
                        { "origin_lla", new Dictionary<string, object>
                            {
                                { "lat_deg", originLatDeg }, { "lon_deg", originLonDeg }, { "alt_m", originAltM },
                            } },
                        { "duration_s", durationS },
                        { "dt_s", dtS },
                    } },
                { "entities", new List<object>() },
            };
            return new ScenarioDocument(root);
        }

        /// <summary>Indented by default so saved manifests stay hand-diffable.</summary>
        public string ToJson(bool indented = true)
        {
            if (!indented) return MiniJson.Serialize(Root);
            var sb = new StringBuilder(1024);
            WritePretty(sb, Root, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        // ---------------- scene ----------------

        public string Name
        {
            get { return GetString(Root, "name"); }
            set { Root["name"] = value; }
        }

        public long Seed
        {
            get { object v; return Root.TryGetValue("seed", out v) ? (long)AsNum(v) : 0L; }
            set { Root["seed"] = value; }
        }

        public double DtS
        {
            get { object v; return Scene.TryGetValue("dt_s", out v) ? AsNum(v) : 0.01; }
        }

        public double DurationS
        {
            get { return AsNum(Scene["duration_s"]); }
            set { Scene["duration_s"] = value; }
        }

        public double OriginLatDeg { get { return AsNum(OriginLla["lat_deg"]); } }
        public double OriginLonDeg { get { return AsNum(OriginLla["lon_deg"]); } }
        public double OriginAltM { get { return AsNum(OriginLla["alt_m"]); } }

        /// <summary>Nearest dt-grid time: commands activate on the sample grid, so snapping
        /// here avoids the validator's off-grid warning.</summary>
        public double SnapToGrid(double tSec)
        {
            double dt = DtS;
            return System.Math.Round(tSec / dt) * dt;
        }

        // ---------------- entities ----------------

        public int EntityCount { get { return Entities.Count; } }

        public IEnumerable<string> EntityIds
        {
            get
            {
                foreach (object e in Entities)
                {
                    var d = e as Dictionary<string, object>;
                    if (d != null) yield return GetString(d, "id");
                }
            }
        }

        public bool HasEntity(string id)
        {
            foreach (string e in EntityIds)
                if (e == id) return true;
            return false;
        }

        /// <summary>The entity's raw JSON object — full access to fields this class has no
        /// helper for (dispersions, munitions, ...).</summary>
        public Dictionary<string, object> Entity(string id)
        {
            foreach (object e in Entities)
            {
                var d = e as Dictionary<string, object>;
                if (d != null && GetString(d, "id") == id) return d;
            }
            throw new InvalidOperationException("entity '" + id + "' not found in scenario");
        }

        public string GetTeam(string id)
        {
            object v;
            return Entity(id).TryGetValue("team", out v) ? (string)v : "gray";
        }

        public void SetTeam(string id, string team) { Entity(id)["team"] = team; }

        public string GetModel(string id) { return GetString(Entity(id), "model"); }
        public void SetModel(string id, string model) { Entity(id)["model"] = model; }

        public Vec3d GetInitialPosNed(string id) { return ToVec3(Initial(id)["pos_ned_m"]); }
        public void SetInitialPosNed(string id, Vec3d posNed) { Initial(id)["pos_ned_m"] = FromVec3(posNed); }

        public Vec3d GetInitialVelNed(string id) { return ToVec3(Initial(id)["vel_ned_mps"]); }
        public void SetInitialVelNed(string id, Vec3d velNed) { Initial(id)["vel_ned_mps"] = FromVec3(velNed); }

        public void AddEntity(string id, string team, string model, Vec3d posNed, Vec3d velNed)
        {
            if (HasEntity(id)) throw new InvalidOperationException("entity '" + id + "' already exists");
            Entities.Add(new Dictionary<string, object>
            {
                { "id", id },
                { "team", team },
                { "type", "aircraft" },
                { "model", model },
                { "initial", new Dictionary<string, object>
                    {
                        { "pos_ned_m", FromVec3(posNed) },
                        { "vel_ned_mps", FromVec3(velNed) },
                    } },
            });
        }

        /// <summary>Removes the entity only; munitions elsewhere that target it are left for
        /// `tspi validate` to flag, keeping one source of semantic truth.</summary>
        public void RemoveEntity(string id)
        {
            var list = Entities;
            for (int i = 0; i < list.Count; i++)
            {
                var d = list[i] as Dictionary<string, object>;
                if (d != null && GetString(d, "id") == id) { list.RemoveAt(i); return; }
            }
            throw new InvalidOperationException("entity '" + id + "' not found in scenario");
        }

        // ---------------- maneuvers ----------------

        /// <summary>Live maneuver list for the entity (created on first use). Each element is
        /// a raw segment object: { at_s, lateral?, vertical?, speed? }.</summary>
        public List<object> Maneuvers(string id)
        {
            var e = Entity(id);
            object v;
            if (!e.TryGetValue("maneuvers", out v) || !(v is List<object>))
            {
                v = new List<object>();
                e["maneuvers"] = v;
            }
            return (List<object>)v;
        }

        public double ManeuverAtS(string id, int index)
        {
            return AsNum(((Dictionary<string, object>)Maneuvers(id)[index])["at_s"]);
        }

        /// <summary>Insert a segment at (grid-snapped) atS, kept sorted by activation time.
        /// Channel dicts come from the Lateral*/Vertical*/Speed* builders; null channels hold.</summary>
        public void AddManeuver(string id, double atS,
            Dictionary<string, object> lateral = null,
            Dictionary<string, object> vertical = null,
            Dictionary<string, object> speed = null)
        {
            if (lateral == null && vertical == null && speed == null)
                throw new ArgumentException("maneuver needs at least one channel");
            var seg = new Dictionary<string, object> { { "at_s", SnapToGrid(atS) } };
            if (lateral != null) seg["lateral"] = lateral;
            if (vertical != null) seg["vertical"] = vertical;
            if (speed != null) seg["speed"] = speed;

            var list = Maneuvers(id);
            int at = list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                if (AsNum(((Dictionary<string, object>)list[i])["at_s"]) > AsNum(seg["at_s"])) { at = i; break; }
            }
            list.Insert(at, seg);
        }

        public void RemoveManeuver(string id, int index) { Maneuvers(id).RemoveAt(index); }

        public static Dictionary<string, object> LateralTurnToHeading(double headingDeg, double gLimit = 3.0)
        {
            return new Dictionary<string, object>
            {
                { "kind", "turn_to_heading" }, { "heading_deg", headingDeg }, { "g_limit", gLimit },
            };
        }

        public static Dictionary<string, object> LateralHold()
        {
            return new Dictionary<string, object> { { "kind", "hold" } };
        }

        public static Dictionary<string, object> VerticalHoldAlt(double altMslM, double rateMps = 20.0)
        {
            return new Dictionary<string, object>
            {
                { "kind", "hold_alt" }, { "alt_msl_m", altMslM }, { "rate_mps", rateMps },
            };
        }

        public static Dictionary<string, object> VerticalDeltaAlt(double deltaM, double rateMps = 20.0)
        {
            return new Dictionary<string, object>
            {
                { "kind", "delta_alt" }, { "delta_m", deltaM }, { "rate_mps", rateMps },
            };
        }

        public static Dictionary<string, object> SpeedSet(double speedMps, double accelMps2 = 3.0)
        {
            return new Dictionary<string, object>
            {
                { "kind", "set" }, { "speed_mps", speedMps }, { "accel_mps2", accelMps2 },
            };
        }

        // ---------------- tree plumbing ----------------

        private Dictionary<string, object> Scene { get { return GetDict(Root, "scene"); } }
        private Dictionary<string, object> OriginLla { get { return GetDict(Scene, "origin_lla"); } }
        private Dictionary<string, object> Initial(string id) { return GetDict(Entity(id), "initial"); }

        private List<object> Entities
        {
            get
            {
                object v;
                if (!Root.TryGetValue("entities", out v) || !(v is List<object>))
                    throw new InvalidOperationException("scenario has no 'entities' array");
                return (List<object>)v;
            }
        }

        private static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || !(v is Dictionary<string, object>))
                throw new InvalidOperationException("manifest is missing object '" + key + "'");
            return (Dictionary<string, object>)v;
        }

        private static string GetString(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || !(v is string))
                throw new InvalidOperationException("manifest is missing string '" + key + "'");
            return (string)v;
        }

        /// <summary>MiniJson numbers arrive as long when integral, double otherwise.</summary>
        private static double AsNum(object v)
        {
            if (v is double) return (double)v;
            if (v is long) return (long)v;
            throw new InvalidOperationException("expected a JSON number, got " + (v == null ? "null" : v.GetType().Name));
        }

        private static Vec3d ToVec3(object v)
        {
            var list = v as List<object>;
            if (list == null || list.Count != 3)
                throw new InvalidOperationException("expected a 3-element JSON array");
            return new Vec3d(AsNum(list[0]), AsNum(list[1]), AsNum(list[2]));
        }

        private static List<object> FromVec3(Vec3d v)
        {
            return new List<object> { v.X, v.Y, v.Z };
        }

        // ---------------- pretty printer ----------------

        /// <summary>Two-space indent; arrays of primitives stay inline (vec3s read as vectors).
        /// Leaf formatting is delegated to MiniJson so numbers/strings round-trip identically
        /// to the compact form.</summary>
        private static void WritePretty(StringBuilder sb, object value, int depth)
        {
            var dict = value as Dictionary<string, object>;
            if (dict != null)
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                int i = 0;
                foreach (KeyValuePair<string, object> kv in dict)
                {
                    Indent(sb, depth + 1);
                    sb.Append(MiniJson.Serialize(kv.Key)).Append(": ");
                    WritePretty(sb, kv.Value, depth + 1);
                    if (++i < dict.Count) sb.Append(',');
                    sb.Append('\n');
                }
                Indent(sb, depth);
                sb.Append('}');
                return;
            }
            var list = value as List<object>;
            if (list != null)
            {
                if (list.Count == 0) { sb.Append("[]"); return; }
                bool allPrimitive = true;
                foreach (object item in list)
                    if (item is Dictionary<string, object> || item is List<object>) { allPrimitive = false; break; }
                if (allPrimitive)
                {
                    sb.Append('[');
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(MiniJson.Serialize(list[i]));
                    }
                    sb.Append(']');
                    return;
                }
                sb.Append("[\n");
                for (int i = 0; i < list.Count; i++)
                {
                    Indent(sb, depth + 1);
                    WritePretty(sb, list[i], depth + 1);
                    if (i < list.Count - 1) sb.Append(',');
                    sb.Append('\n');
                }
                Indent(sb, depth);
                sb.Append(']');
                return;
            }
            sb.Append(MiniJson.Serialize(value));
        }

        private static void Indent(StringBuilder sb, int depth)
        {
            for (int i = 0; i < depth; i++) sb.Append("  ");
        }
    }
}
