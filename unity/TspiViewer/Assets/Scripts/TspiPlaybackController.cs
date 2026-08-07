using System.Collections.Generic;
using System.IO;
using Tspi.Core.IO;
using UnityEngine;

namespace TspiViewer
{
    /// <summary>
    /// Playback-only viewer: opens a memory-mapped .tspi and drives one GameObject per
    /// entity, sampling interpolated pose at the current playback time. All simulation
    /// happened offline in the headless engine — this class never integrates anything.
    ///
    /// Scrub, pause, and time-dilation are all just "set _timeSec": every pose is an
    /// O(1) interpolated lookup, so seeking anywhere in a million-sample run is free.
    /// </summary>
    public sealed class TspiPlaybackController : MonoBehaviour
    {
        [Tooltip("Path to a .tspi file: absolute, or relative to the repo root (e.g. runs/ship-to-air.tspi). On device, copy into persistentDataPath first.")]
        public string filePath = "";

        /// <summary>Why the last Load produced nothing ("" when loaded); shown by PlaybackHud.</summary>
        public string LoadError { get; private set; } = "";

        [Tooltip("Playback rate. 1 = real time, negative = reverse, 0 = paused.")]
        public float timeScale = 1.0f;

        public bool loop = true;

        [Header("Presentation")]
        public GameObject bluePrefab;
        public GameObject redPrefab;
        public GameObject neutralPrefab;
        public float trailSeconds = 8f;
        [Tooltip("Draw each entity's entire recorded trajectory as a dim line, like the web and Godot viewers.")]
        public bool showFullPaths = true;
        [Tooltip("Decimation cap per entity for the full-path line; the whole trajectory is still spanned.")]
        public int maxPathPoints = 2048;

        private TspiReader _reader;
        private double _timeSec;
        private double _minT, _maxT;
        private readonly List<EntityView> _views = new();

        public double TimeSec => _timeSec;
        public double MinTime => _minT;
        public double MaxTime => _maxT;
        public bool Loaded => _reader != null;
        public IReadOnlyList<EntityView> Views => _views;
        /// <summary>The open reader (null when nothing is loaded). Every sample of every
        /// entity is reachable through it — ReadSample(entity, i) is O(1) on the mmap —
        /// so overlays/analytics scripts are not limited to the current playback time.</summary>
        public TspiReader Reader => _reader;
        private static readonly List<TspiEventEntry> NoEvents = new();
        /// <summary>Footer event log of the loaded file (launch/cpa/intercept/...), for HUD timelines.</summary>
        public IReadOnlyList<TspiEventEntry> Events => _reader != null ? _reader.Events : NoEvents;

        private sealed class EntityViewImpl { }

        public struct EntityView
        {
            public TspiEntityEntry Entry;
            public Transform Transform;
            public TrailRenderer Trail;
            public LineRenderer Path;
        }

        private void OnEnable()
        {
            if (!string.IsNullOrEmpty(filePath))
                Load(filePath);
        }

        /// <summary>Open a file (replacing any loaded one). With keepTime, playback stays at
        /// the current time (clamped) — the editor's regenerate-and-resume path.</summary>
        public void Load(string path, bool keepTime = false)
        {
            double prevT = _timeSec;
            bool hadFile = _reader != null;
            Unload();
            string resolved = ResolvePath(path);
            if (!File.Exists(resolved))
            {
                LoadError = $"file not found: '{path}'";
                Debug.LogWarning($"TspiPlaybackController: {LoadError} — generate one from the repo root, " +
                                 "e.g. 'tspi run schemas/examples/ship-to-air.json -o runs/ship-to-air.tspi' " +
                                 "(docs/WALKTHROUGH.md), then press Play again.");
                return;
            }
            LoadError = "";
            _reader = TspiReader.Open(resolved);
            _minT = double.MaxValue;
            _maxT = double.MinValue;
            foreach (var e in _reader.Entities)
            {
                _minT = System.Math.Min(_minT, _reader.StartSec(e));
                _maxT = System.Math.Max(_maxT, _reader.EndSec(e));
                CreateView(e);
            }
            _timeSec = keepTime && hadFile
                ? System.Math.Max(_minT, System.Math.Min(_maxT, prevT))
                : _minT;
        }

        private void CreateView(TspiEntityEntry e)
        {
            GameObject prefab = e.Team switch
            {
                "blue" => bluePrefab,
                "red" => redPrefab,
                _ => neutralPrefab,
            };
            GameObject go = prefab != null
                ? Instantiate(prefab, transform)
                : GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = $"{e.Id} ({e.Team}/{e.Type})";
            go.transform.SetParent(transform, false);

            Color c = TeamColor(e.Team);
            var trail = go.GetComponent<TrailRenderer>();
            if (trail == null && trailSeconds > 0f)
            {
                trail = go.AddComponent<TrailRenderer>();
                trail.time = trailSeconds;
                trail.startWidth = e.Type == "munition" ? 3f : 8f;
                trail.endWidth = 0f;
                trail.material = new Material(Shader.Find("Sprites/Default"));
                trail.startColor = c;
                trail.endColor = new Color(c.r, c.g, c.b, 0f);
            }

            LineRenderer path = showFullPaths ? CreateFullPath(e, c) : null;

            _views.Add(new EntityView { Entry = e, Transform = go.transform, Trail = trail, Path = path });
        }

        private static Color TeamColor(string team) => team switch
        {
            "blue" => new Color(0.3f, 0.6f, 1f),
            "red" => new Color(1f, 0.35f, 0.3f),
            _ => Color.gray,
        };

        /// <summary>Dim polyline of the entity's entire recorded trajectory, built once at
        /// load from the sample block (decimated to maxPathPoints, endpoints kept), so the
        /// whole engagement geometry is visible at any playback time — parity with the web
        /// and Godot viewers' full-path rendering.</summary>
        private LineRenderer CreateFullPath(TspiEntityEntry e, Color teamColor)
        {
            if (e.SampleCount < 2 || e.Layout != TspiFormat.LayoutSixDofV1 || maxPathPoints < 2)
                return null;
            var go = new GameObject($"{e.Id} path");
            go.transform.SetParent(transform, false);
            long step = System.Math.Max(1, (e.SampleCount + maxPathPoints - 1) / maxPathPoints);
            var pts = new List<Vector3>();
            for (long i = 0; i < e.SampleCount; i += step)
                pts.Add(NedUnity.ToUnityPos(_reader.ReadSample(e, i).Pos));
            if ((e.SampleCount - 1) % step != 0)
                pts.Add(NedUnity.ToUnityPos(_reader.ReadSample(e, e.SampleCount - 1).Pos));

            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.positionCount = pts.Count;
            lr.SetPositions(pts.ToArray());
            lr.startWidth = lr.endWidth = 2f;
            lr.material = new Material(Shader.Find("Sprites/Default"));
            Color dim = new Color(teamColor.r, teamColor.g, teamColor.b, 0.22f);
            lr.startColor = dim;
            lr.endColor = dim;
            return lr;
        }

        /// <summary>Absolute paths pass through; relative paths resolve against the CWD
        /// and then the repo root (three levels above Assets/), so the committed sample
        /// scene can reference walkthrough outputs like runs/ship-to-air.tspi.</summary>
        public static string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path) || File.Exists(path))
                return path;
            string fromRepoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", path));
            return File.Exists(fromRepoRoot) ? fromRepoRoot : path;
        }

        private void Update()
        {
            if (_reader == null) return;
            Advance(Time.deltaTime * timeScale);
            ApplyPoses();
        }

        /// <summary>Advance (or rewind) playback time, honoring loop/clamp at the ends.</summary>
        public void Advance(double deltaSec)
        {
            _timeSec += deltaSec;
            if (_timeSec > _maxT)
                _timeSec = loop ? _minT + (_timeSec - _maxT) : _maxT;
            if (_timeSec < _minT)
                _timeSec = loop ? _maxT - (_minT - _timeSec) : _minT;
        }

        /// <summary>Jump directly to an absolute time (scrubbing). O(1) per entity.</summary>
        public void Seek(double timeSec)
        {
            _timeSec = System.Math.Max(_minT, System.Math.Min(_maxT, timeSec));
            ApplyPoses();
        }

        private void ApplyPoses()
        {
            foreach (var view in _views)
            {
                bool alive = _reader.TrySampleAt(view.Entry, _timeSec, out TspiState st);
                if (!alive)
                {
                    // Entity not yet spawned or already terminated: hide it.
                    if (view.Transform.gameObject.activeSelf)
                        view.Transform.gameObject.SetActive(false);
                    continue;
                }
                if (!view.Transform.gameObject.activeSelf)
                {
                    view.Transform.gameObject.SetActive(true);
                    if (view.Trail != null) view.Trail.Clear();
                }
                view.Transform.SetLocalPositionAndRotation(
                    NedUnity.ToUnityPos(st.PosNed),
                    NedUnity.ToUnityRot(st.AttBodyToNed));
            }
        }

        public void Unload()
        {
            foreach (var v in _views)
            {
                if (v.Transform != null) Destroy(v.Transform.gameObject);
                if (v.Path != null) Destroy(v.Path.gameObject);
            }
            _views.Clear();
            _reader?.Dispose();
            _reader = null;
        }

        private void OnDisable() => Unload();
    }
}
