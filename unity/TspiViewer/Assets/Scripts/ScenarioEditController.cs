using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Tspi.Core.Authoring;
using Tspi.Core.Math;
using UnityEngine;

namespace TspiViewer
{
    /// <summary>
    /// Scenario authoring on top of the playback viewer. The manifest JSON is the
    /// document (ScenarioDocument edits the parsed tree in place); previews come from
    /// shelling out to the real tspi CLI — Unity never simulates, so the preview IS
    /// the truth, not an approximation.
    ///
    /// The core loop: edit (drag an entity, add a maneuver at the current playback
    /// time) -> save manifest -> `tspi run` (~100 ms) -> reload the .tspi -> seek back
    /// to the same time. Determinism makes the resume seamless: command timelines and
    /// per-entity RNG streams guarantee everything before the edit time replays
    /// byte-identically, so a regenerate feels like branching the world "from now".
    ///
    /// Controls: Tab toggles edit mode. Edit mode: click a marker to select, left-drag
    /// to move it, right-drag to point its initial heading at the cursor. The maneuver
    /// panel always applies at the current playback time.
    /// </summary>
    [RequireComponent(typeof(TspiPlaybackController))]
    public sealed class ScenarioEditController : MonoBehaviour
    {
        [Header("Scenario")]
        [Tooltip("Path to a scenario.json (absolute, or relative to the project root). Saved in place on regenerate.")]
        public string scenarioPath = "";
        public bool regenerateOnLoad = true;
        public bool regenerateOnDragEnd = true;

        [Header("tspi CLI")]
        [Tooltip("Self-contained tspi executable, or 'dotnet'.")]
        public string tspiExecutable = "dotnet";
        [Tooltip("When the executable is 'dotnet': absolute path to tspi.dll.")]
        public string tspiDllPath = "";
        [Tooltip("CLI working directory — the repo root, so ./models resolves.")]
        public string workingDirectory = "";
        [Tooltip("Optional --models override.")]
        public string modelsDir = "";

        [Header("Edit markers")]
        public float markerScale = 60f;

        private TspiPlaybackController _pb;
        private ScenarioDocument _doc;
        private readonly TspiCliRunner _runner = new TspiCliRunner();

        private Transform _markerRoot;
        private readonly Dictionary<string, Transform> _markers = new();
        private string _selectedId;
        private bool _editMode;
        private bool _draggingPos, _draggingHeading;
        private float _dragPlaneY;

        private Task<CliResult> _run;
        private string _runPreviewPath = "";
        private bool _pendingRegen;
        private double _seekAfter;
        private int _previewFlip;
        private string _status = "no scenario loaded";

        // HUD field caches (committed with Apply / the maneuver buttons).
        private string _speedField = "", _headingField = "", _altField = "";
        private string _newModelField = "generic-fighter";
        private string _mnvHeadingField = "90", _mnvAltField = "5000", _mnvSpeedField = "250";
        private Vector2 _entityScroll;
        private Rect _panelRect;

        private void Awake() => _pb = GetComponent<TspiPlaybackController>();

        private void Start()
        {
            if (!string.IsNullOrEmpty(scenarioPath))
                LoadScenarioFile();
        }

        // ---------------- document lifecycle ----------------

        private string FullScenarioPath =>
            Path.IsPathRooted(scenarioPath)
                ? scenarioPath
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", scenarioPath));

        public void LoadScenarioFile()
        {
            try
            {
                _doc = ScenarioDocument.FromJson(File.ReadAllText(FullScenarioPath));
                _selectedId = null;
                RebuildMarkers();
                _status = $"loaded {Path.GetFileName(FullScenarioPath)} ({_doc.EntityCount} entities)";
                if (regenerateOnLoad) RequestRegenerate();
            }
            catch (Exception ex)
            {
                _doc = null;
                _status = "load failed: " + ex.Message;
            }
        }

        private void SaveScenarioFile() => File.WriteAllText(FullScenarioPath, _doc.ToJson());

        // ---------------- regenerate machinery ----------------

        /// <summary>Save the manifest, run tspi, and on success reload the preview at the
        /// time playback was at when the edit was made. Edits made while a run is in
        /// flight coalesce into one follow-up run.</summary>
        public void RequestRegenerate()
        {
            if (_doc == null) return;
            _seekAfter = _pb.Loaded ? _pb.TimeSec : 0.0;
            if (_run != null) { _pendingRegen = true; return; }
            StartRun();
        }

        private void StartRun()
        {
            try
            {
                SaveScenarioFile();
            }
            catch (Exception ex)
            {
                _status = "save failed: " + ex.Message;
                return;
            }
            _runner.Executable = tspiExecutable;
            _runner.DllPath = tspiDllPath;
            _runner.WorkingDirectory = workingDirectory;
            _runner.ModelsDir = modelsDir;

            // Alternate preview files: the currently mmap'd one is never overwritten.
            _runPreviewPath = Path.Combine(Application.temporaryCachePath, $"preview-{_previewFlip}.tspi");
            _previewFlip ^= 1;
            _run = _runner.RunScenario(FullScenarioPath, _runPreviewPath);
            _status = "running tspi…";
        }

        private void PollRun()
        {
            if (_run == null || !_run.IsCompleted) return;
            CliResult r = _run.Result;
            _run = null;
            if (r.Ok)
            {
                _pb.Load(_runPreviewPath);
                _pb.Seek(_seekAfter);
                _status = $"regenerated in {r.ElapsedMs:0} ms";
            }
            else
            {
                string err = string.IsNullOrEmpty(r.Stderr) ? r.Stdout : r.Stderr;
                _status = "tspi failed: " + Tail(err, 3);
                Debug.LogError($"tspi failed (exit {r.ExitCode}): {r.Command}\n{err}");
            }
            if (_pendingRegen) { _pendingRegen = false; StartRun(); }
        }

        private static string Tail(string s, int lines)
        {
            string[] all = (s ?? "").Trim().Split('\n');
            int start = Mathf.Max(0, all.Length - lines);
            return string.Join(" | ", all, start, all.Length - start).Trim();
        }

        // ---------------- edit markers ----------------

        private void RebuildMarkers()
        {
            if (_markerRoot != null) Destroy(_markerRoot.gameObject);
            _markers.Clear();
            _markerRoot = new GameObject("ScenarioEditMarkers").transform;
            _markerRoot.SetParent(transform, false);
            _markerRoot.gameObject.SetActive(_editMode);
            if (_doc == null) return;
            foreach (string id in _doc.EntityIds)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "edit:" + id;
                go.transform.SetParent(_markerRoot, false);
                go.transform.localScale = Vector3.one * markerScale;
                go.GetComponent<Renderer>().material.color = TeamColor(_doc.GetTeam(id));

                var arrow = go.AddComponent<LineRenderer>();
                arrow.useWorldSpace = true;
                arrow.positionCount = 2;
                arrow.startWidth = markerScale * 0.15f;
                arrow.endWidth = 0f;
                arrow.material = new Material(Shader.Find("Sprites/Default"));
                arrow.startColor = arrow.endColor = TeamColor(_doc.GetTeam(id));

                _markers[id] = go.transform;
                SyncMarker(id);
            }
        }

        /// <summary>Marker pose from the document (initial position + heading arrow).</summary>
        private void SyncMarker(string id)
        {
            if (!_markers.TryGetValue(id, out Transform m)) return;
            Vec3d pos = _doc.GetInitialPosNed(id);
            Vec3d vel = _doc.GetInitialVelNed(id);
            m.position = NedUnity.ToUnityPos(pos);
            var arrow = m.GetComponent<LineRenderer>();
            float len = Mathf.Max(3f * markerScale, (float)vel.Length * 3f);
            Vector3 dir = NedUnity.ToUnityDir(vel.Normalized());
            arrow.SetPosition(0, m.position);
            arrow.SetPosition(1, m.position + dir * len);
            m.localScale = Vector3.one * (id == _selectedId ? markerScale * 1.5f : markerScale);
        }

        private static Color TeamColor(string team) => team switch
        {
            "blue" => new Color(0.3f, 0.6f, 1f),
            "red" => new Color(1f, 0.35f, 0.3f),
            _ => Color.gray,
        };

        private static Vec3d UnityToNed(Vector3 u) => new Vec3d(u.z, u.x, -u.y);

        // ---------------- mouse editing ----------------

        private void Update()
        {
            PollRun();
            if (Input.GetKeyDown(KeyCode.Tab)) SetEditMode(!_editMode);
            if (_editMode && _doc != null) HandleMouse();
        }

        public void SetEditMode(bool on)
        {
            _editMode = on;
            if (_markerRoot != null) _markerRoot.gameObject.SetActive(on);
        }

        private bool MouseIsOverGui()
        {
            var gui = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            return _panelRect.Contains(gui) || gui.y > Screen.height - 120f; // side panel / transport bar
        }

        private void HandleMouse()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            if (Input.GetMouseButtonDown(0) && !MouseIsOverGui())
            {
                Ray ray = cam.ScreenPointToRay(Input.mousePosition);
                if (Physics.Raycast(ray, out RaycastHit hit, float.MaxValue))
                {
                    foreach (KeyValuePair<string, Transform> kv in _markers)
                    {
                        if (kv.Value != hit.transform) continue;
                        Select(kv.Key);
                        _draggingPos = true;
                        _dragPlaneY = kv.Value.position.y;
                        break;
                    }
                }
            }
            if (Input.GetMouseButtonDown(1) && _selectedId != null && !MouseIsOverGui())
            {
                _draggingHeading = true;
                _dragPlaneY = _markers[_selectedId].position.y;
            }

            if (_draggingPos && Input.GetMouseButton(0) && _selectedId != null)
            {
                if (TryGroundPoint(cam, _dragPlaneY, out Vector3 p))
                {
                    // Horizontal drag: altitude (unity y == -D) is preserved by the plane.
                    _doc.SetInitialPosNed(_selectedId, UnityToNed(new Vector3(p.x, _dragPlaneY, p.z)));
                    SyncMarker(_selectedId);
                }
            }
            if (_draggingHeading && Input.GetMouseButton(1) && _selectedId != null)
            {
                if (TryGroundPoint(cam, _dragPlaneY, out Vector3 p))
                {
                    Vector3 m = _markers[_selectedId].position;
                    Vec3d ned = UnityToNed(p - m); // horizontal pointer from entity to cursor
                    if (ned.LengthHorizontal > 1e-3)
                    {
                        Vec3d vel = _doc.GetInitialVelNed(_selectedId);
                        double heading = System.Math.Atan2(ned.Y, ned.X);
                        double vh = vel.LengthHorizontal;
                        _doc.SetInitialVelNed(_selectedId, new Vec3d(
                            vh * System.Math.Cos(heading), vh * System.Math.Sin(heading), vel.Z));
                        SyncMarker(_selectedId);
                    }
                }
            }

            bool posUp = _draggingPos && Input.GetMouseButtonUp(0);
            bool hdgUp = _draggingHeading && Input.GetMouseButtonUp(1);
            if (posUp) _draggingPos = false;
            if (hdgUp) _draggingHeading = false;
            if ((posUp || hdgUp) && regenerateOnDragEnd) RequestRegenerate();
        }

        private static bool TryGroundPoint(Camera cam, float planeY, out Vector3 point)
        {
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            point = default;
            if (Mathf.Abs(ray.direction.y) < 1e-4f) return false;
            float t = (planeY - ray.origin.y) / ray.direction.y;
            if (t <= 0f) return false;
            point = ray.origin + ray.direction * t;
            return true;
        }

        private void Select(string id)
        {
            string prev = _selectedId;
            _selectedId = id;
            if (prev != null) SyncMarker(prev);
            SyncMarker(id);
            RefreshFields();
        }

        private void RefreshFields()
        {
            if (_selectedId == null || _doc == null) return;
            Vec3d vel = _doc.GetInitialVelNed(_selectedId);
            Vec3d pos = _doc.GetInitialPosNed(_selectedId);
            _speedField = vel.Length.ToString("0.#", CultureInfo.InvariantCulture);
            _headingField = (System.Math.Atan2(vel.Y, vel.X) * 180.0 / System.Math.PI).ToString("0.#", CultureInfo.InvariantCulture);
            _altField = (_doc.OriginAltM - pos.Z).ToString("0.#", CultureInfo.InvariantCulture);
        }

        private void ApplyFields()
        {
            if (_selectedId == null) return;
            if (!TryNum(_speedField, out double speed) || !TryNum(_headingField, out double headingDeg) ||
                !TryNum(_altField, out double altMsl))
            {
                _status = "speed/heading/alt must be numbers";
                return;
            }
            Vec3d vel = _doc.GetInitialVelNed(_selectedId);
            double h = headingDeg * System.Math.PI / 180.0;
            _doc.SetInitialVelNed(_selectedId, new Vec3d(speed * System.Math.Cos(h), speed * System.Math.Sin(h), vel.Z));
            Vec3d pos = _doc.GetInitialPosNed(_selectedId);
            _doc.SetInitialPosNed(_selectedId, new Vec3d(pos.X, pos.Y, _doc.OriginAltM - altMsl));
            SyncMarker(_selectedId);
            RequestRegenerate();
        }

        private static bool TryNum(string s, out double v) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        // ---------------- entity add/remove ----------------

        private void AddEntityAtViewCenter()
        {
            Camera cam = Camera.main;
            if (cam == null || _doc == null) return;
            Vector3 fwd = cam.transform.forward; fwd.y = 0;
            Vector3 at = cam.transform.position + (fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward) * 5000f;
            at.y = (float)(5000.0 - _doc.OriginAltM); // 5 km MSL default

            string id = NextId("blue");
            _doc.AddEntity(id, "blue", _newModelField.Trim(), UnityToNed(at), new Vec3d(200, 0, 0));
            RebuildMarkers();
            Select(id);
            RequestRegenerate();
        }

        private string NextId(string team)
        {
            for (int i = 1; ; i++)
            {
                string id = $"{team}-{i:00}";
                if (!_doc.HasEntity(id)) return id;
            }
        }

        private void DeleteSelected()
        {
            if (_selectedId == null) return;
            _doc.RemoveEntity(_selectedId);
            _selectedId = null;
            RebuildMarkers();
            RequestRegenerate();
        }

        private void CycleTeam()
        {
            if (_selectedId == null) return;
            string next = _doc.GetTeam(_selectedId) switch
            {
                "blue" => "red",
                "red" => "gray",
                _ => "blue",
            };
            _doc.SetTeam(_selectedId, next);
            RebuildMarkers();
            Select(_selectedId);
            RequestRegenerate();
        }

        // ---------------- maneuvers at the current playback time ----------------

        private void AddManeuverNow(Dictionary<string, object> lateral,
            Dictionary<string, object> vertical, Dictionary<string, object> speed)
        {
            if (_selectedId == null || _doc == null) return;
            double t = _pb.Loaded ? _pb.TimeSec : 0.0;
            _doc.AddManeuver(_selectedId, t, lateral, vertical, speed);
            RequestRegenerate(); // reloads and resumes at t: the past is identical by determinism
        }

        private string ManeuverSummary(Dictionary<string, object> seg)
        {
            var parts = new List<string>();
            foreach (string ch in new[] { "lateral", "vertical", "speed" })
            {
                if (!seg.TryGetValue(ch, out object v) || !(v is Dictionary<string, object> d)) continue;
                string kind = d.TryGetValue("kind", out object k) ? (string)k : "?";
                string detail = d.TryGetValue("heading_deg", out object hv) ? $"→{hv}°"
                    : d.TryGetValue("alt_msl_m", out object av) ? $"→{av} m"
                    : d.TryGetValue("delta_m", out object dv) ? $"Δ{dv} m"
                    : d.TryGetValue("speed_mps", out object sv) ? $"→{sv} m/s"
                    : "";
                parts.Add($"{ch}:{kind}{detail}");
            }
            return string.Join("  ", parts);
        }

        // ---------------- HUD ----------------

        private void OnGUI()
        {
            const float pad = 12f;
            GUILayout.BeginArea(new Rect(pad, pad, 360, Screen.height - 140));
            GUILayout.BeginVertical(GUI.skin.box);

            GUILayout.Label($"scenario: {(_doc == null ? "-" : _doc.Name)}    {_status}");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_editMode ? "Edit mode: ON (Tab)" : "Edit mode: off (Tab)"))
                SetEditMode(!_editMode);
            GUI.enabled = _doc != null && _run == null;
            if (GUILayout.Button("Regenerate")) RequestRegenerate();
            GUI.enabled = _doc != null;
            if (GUILayout.Button("Reload file")) LoadScenarioFile();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (_doc == null)
            {
                GUILayout.BeginHorizontal();
                scenarioPath = GUILayout.TextField(scenarioPath);
                if (GUILayout.Button("Load", GUILayout.Width(60))) LoadScenarioFile();
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Space(4);
                _entityScroll = GUILayout.BeginScrollView(_entityScroll, GUILayout.Height(88));
                foreach (string id in _doc.EntityIds)
                {
                    GUI.color = id == _selectedId ? Color.cyan : Color.white;
                    if (GUILayout.Button($"{id}  [{_doc.GetTeam(id)}] {_doc.GetModel(id)}", GUI.skin.label))
                        Select(id);
                }
                GUI.color = Color.white;
                GUILayout.EndScrollView();

                if (_editMode)
                {
                    GUILayout.BeginHorizontal();
                    _newModelField = GUILayout.TextField(_newModelField, GUILayout.Width(160));
                    if (GUILayout.Button("Add entity")) AddEntityAtViewCenter();
                    GUILayout.EndHorizontal();
                }

                if (_selectedId != null)
                {
                    GUILayout.Space(4);
                    GUILayout.Label($"selected: {_selectedId}");
                    if (_editMode)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label("spd", GUILayout.Width(30));
                        _speedField = GUILayout.TextField(_speedField, GUILayout.Width(60));
                        GUILayout.Label("hdg", GUILayout.Width(30));
                        _headingField = GUILayout.TextField(_headingField, GUILayout.Width(60));
                        GUILayout.Label("alt", GUILayout.Width(25));
                        _altField = GUILayout.TextField(_altField, GUILayout.Width(60));
                        if (GUILayout.Button("Apply", GUILayout.Width(56))) ApplyFields();
                        GUILayout.EndHorizontal();
                        GUILayout.BeginHorizontal();
                        if (GUILayout.Button($"team: {_doc.GetTeam(_selectedId)}")) CycleTeam();
                        if (GUILayout.Button("Delete entity")) DeleteSelected();
                        GUILayout.EndHorizontal();
                    }

                    GUILayout.Space(4);
                    GUILayout.Label($"maneuvers @ t = {(_pb.Loaded ? _pb.TimeSec : 0):0.00} s   (added at current time)");
                    GUILayout.BeginHorizontal();
                    _mnvHeadingField = GUILayout.TextField(_mnvHeadingField, GUILayout.Width(52));
                    if (GUILayout.Button("Turn to hdg°") && TryNum(_mnvHeadingField, out double hdg))
                        AddManeuverNow(ScenarioDocument.LateralTurnToHeading(hdg), null, null);
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    _mnvAltField = GUILayout.TextField(_mnvAltField, GUILayout.Width(52));
                    if (GUILayout.Button("Hold alt (MSL m)") && TryNum(_mnvAltField, out double alt))
                        AddManeuverNow(null, ScenarioDocument.VerticalHoldAlt(alt), null);
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    _mnvSpeedField = GUILayout.TextField(_mnvSpeedField, GUILayout.Width(52));
                    if (GUILayout.Button("Set speed (m/s)") && TryNum(_mnvSpeedField, out double spd))
                        AddManeuverNow(null, null, ScenarioDocument.SpeedSet(spd));
                    GUILayout.EndHorizontal();

                    List<object> mnv = _doc.Maneuvers(_selectedId);
                    for (int i = 0; i < mnv.Count; i++)
                    {
                        var seg = (Dictionary<string, object>)mnv[i];
                        GUILayout.BeginHorizontal();
                        GUILayout.Label($"t={_doc.ManeuverAtS(_selectedId, i):0.00}s  {ManeuverSummary(seg)}");
                        if (GUILayout.Button("x", GUILayout.Width(22)))
                        {
                            _doc.RemoveManeuver(_selectedId, i);
                            RequestRegenerate();
                            GUILayout.EndHorizontal();
                            break;
                        }
                        GUILayout.EndHorizontal();
                    }
                }
            }

            GUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint)
            {
                Rect used = GUILayoutUtility.GetLastRect();
                _panelRect = new Rect(used.x + pad, used.y + pad, used.width, used.height);
            }
            GUILayout.EndArea();
        }
    }
}
