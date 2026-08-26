using System.Collections.Generic;
using System.IO;
using Tspi.Core.IO;
using Tspi.Core.Live;
using UnityEngine;

namespace TspiViewer
{
    /// <summary>
    /// Playback-only viewer: drives one GameObject per entity, sampling interpolated
    /// pose at the current playback time. All simulation happened elsewhere — offline in
    /// the headless engine, or in a live producer on the wire — and this class never
    /// integrates anything.
    ///
    /// The source is an <see cref="ITspiSource"/>: either a memory-mapped .tspi
    /// (<see cref="TspiReader"/>) or a live stream (<see cref="LiveTspiSource"/>, fed by
    /// <see cref="TspiLiveClient"/>). Both sample through the same interpolator, so a
    /// live pose and the replayed pose of the same run are the same number; only the
    /// "does it keep growing?" bookkeeping below differs.
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
        [Tooltip("Vertex cap per full-path line. Generous enough that realistic runs render " +
                 "every sample; it exists only so a pathological file (hours at high rate) " +
                 "can't create a multi-million-vertex LineRenderer.")]
        public int maxPathPoints = 16384;

        private ITspiSource _source;
        private double _timeSec;
        private double _minT, _maxT;
        private readonly List<EntityView> _views = new();
        private int _syncedEntities;      // entities already given a view (live sources grow)
        private int _syncedEvents;

        public double TimeSec => _timeSec;
        public double MinTime => _minT;
        public double MaxTime => _maxT;
        public bool Loaded => _source != null;
        public IReadOnlyList<EntityView> Views => _views;

        /// <summary>True while the source is a stream that may still grow.</summary>
        public bool IsLive => _source != null && _source.IsLive;

        /// <summary>
        /// Ride the head of a live stream instead of running the clock freely. Set false
        /// by any Seek (scrubbing back through what has arrived); set true again to
        /// rejoin the head. Ignored for file sources.
        /// </summary>
        public bool followLive = true;
        /// <summary>The open reader (null when nothing is loaded). Every sample of every
        /// entity is reachable through it — ReadSample(entity, i) is O(1) on the mmap —
        /// so overlays/analytics scripts are not limited to the current playback time.</summary>
        public TspiReader Reader => _source as TspiReader;

        /// <summary>The active source, file or live.</summary>
        public ITspiSource Source => _source;
        private static readonly List<TspiEventEntry> NoEvents = new();
        /// <summary>Event log of the source (launch/cpa/intercept/...), for HUD timelines.
        /// A live stream appends to it as events arrive.</summary>
        public IReadOnlyList<TspiEventEntry> Events => _source != null ? _source.Events : NoEvents;

        private sealed class EntityViewImpl { }

        public struct EntityView
        {
            public TspiEntityEntry Entry;
            public Transform Transform;
            public TrailRenderer Trail;
            public LineRenderer Path;
            /// <summary>Samples already plotted into Path, and the decimation in use (live growth).</summary>
            public long PlottedSamples;
            public long PathStep;
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
            bool hadFile = _source != null;
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
            _source = TspiReader.Open(resolved);
            _minT = double.MaxValue;
            _maxT = double.MinValue;
            foreach (var e in _source.Entities)
            {
                _minT = System.Math.Min(_minT, _source.StartSec(e));
                _maxT = System.Math.Max(_maxT, _source.EndSec(e));
                CreateView(e);
            }
            _syncedEntities = _source.Entities.Count;
            _syncedEvents = _source.Events.Count;
            _timeSec = keepTime && hadFile
                ? System.Math.Max(_minT, System.Math.Min(_maxT, prevT))
                : _minT;
        }

        /// <summary>
        /// Play a live stream instead of a file (see <see cref="TspiLiveClient"/>).
        /// Nothing exists yet at bind time — entities, samples and events arrive over the
        /// wire — so views are created as the producer announces them and the time span
        /// grows under the transport. Playback starts following the head.
        /// </summary>
        public void BindLive(LiveTspiSource live)
        {
            if (live == null) return;
            Unload();
            LoadError = "";
            _source = live;
            _minT = 0;
            _maxT = 0;
            _timeSec = 0;
            _syncedEntities = 0;
            _syncedEvents = 0;
            followLive = true;
            SyncLive();
        }

        /// <summary>
        /// Catch up with everything that arrived since the last frame: views for newly
        /// announced entities, path geometry for new samples, and the growing time span.
        /// </summary>
        private void SyncLive()
        {
            var entities = _source.Entities;
            for (int i = _syncedEntities; i < entities.Count; i++) CreateView(entities[i]);
            _syncedEntities = entities.Count;
            _syncedEvents = _source.Events.Count;

            bool any = false;
            for (int i = 0; i < entities.Count; i++)
            {
                var e = entities[i];
                if (e.SampleCount <= 0) continue;
                double s0 = _source.StartSec(e), s1 = _source.EndSec(e);
                if (!any) { _minT = s0; _maxT = s1; any = true; }
                else
                {
                    if (s0 < _minT) _minT = s0;
                    if (s1 > _maxT) _maxT = s1;
                }
            }
            for (int i = 0; i < _views.Count; i++)
            {
                EntityView v = _views[i];
                if (ExtendLivePath(ref v)) _views[i] = v;
            }
        }

        /// <summary>
        /// Append the samples that arrived since the last frame to the entity's full-path
        /// line. Beyond maxPathPoints the line is rebuilt at twice the decimation, so a
        /// long-running stream costs a bounded number of vertices instead of growing
        /// without limit.
        /// </summary>
        private bool ExtendLivePath(ref EntityView v)
        {
            if (v.Path == null || v.Entry.SampleCount <= v.PlottedSamples) return false;
            long count = v.Entry.SampleCount;
            if (v.PathStep < 1) v.PathStep = 1;

            if (count / v.PathStep > maxPathPoints)
            {
                while (count / v.PathStep > maxPathPoints) v.PathStep *= 2;
                RebuildPath(ref v);
                return true;
            }
            int plotted = v.Path.positionCount;
            for (long i = ((v.PlottedSamples + v.PathStep - 1) / v.PathStep) * v.PathStep; i < count; i += v.PathStep)
            {
                v.Path.positionCount = plotted + 1;
                v.Path.SetPosition(plotted, NedUnity.ToUnityPos(_source.ReadSample(v.Entry, i).Pos));
                plotted++;
            }
            v.PlottedSamples = count;
            return true;
        }

        private void RebuildPath(ref EntityView v)
        {
            long count = v.Entry.SampleCount;
            var pts = new List<Vector3>();
            for (long i = 0; i < count; i += v.PathStep)
                pts.Add(NedUnity.ToUnityPos(_source.ReadSample(v.Entry, i).Pos));
            v.Path.positionCount = pts.Count;
            v.Path.SetPositions(pts.ToArray());
            v.PlottedSamples = count;
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

            long step = System.Math.Max(1, (e.SampleCount + maxPathPoints - 1) / maxPathPoints);
            LineRenderer path = showFullPaths ? CreateFullPath(e, c, step) : null;

            _views.Add(new EntityView
            {
                Entry = e, Transform = go.transform, Trail = trail, Path = path,
                PlottedSamples = path != null ? e.SampleCount : 0, PathStep = step,
            });
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
        private LineRenderer CreateFullPath(TspiEntityEntry e, Color teamColor, long step)
        {
            // A live entity is announced before its first record arrives, so an empty
            // path is legal here — SyncLive fills it in as samples land.
            if (e.Layout != TspiFormat.LayoutSixDofV1 || maxPathPoints < 2) return null;
            if (e.SampleCount < 2 && !IsLive) return null;
            var go = new GameObject($"{e.Id} path");
            go.transform.SetParent(transform, false);
            var pts = new List<Vector3>();
            for (long i = 0; i < e.SampleCount; i += step)
                pts.Add(NedUnity.ToUnityPos(_source.ReadSample(e, i).Pos));
            if (e.SampleCount > 0 && (e.SampleCount - 1) % step != 0)
                pts.Add(NedUnity.ToUnityPos(_source.ReadSample(e, e.SampleCount - 1).Pos));

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
            if (_source == null) return;
            if (_source.IsLive)
            {
                SyncLive();
                if (followLive)
                {
                    // Ride the head one sample interval back, so there is always a
                    // bracketing pair of records to interpolate between.
                    _timeSec = System.Math.Max(_minT, _maxT - _source.DtSec * 1.001);
                    ApplyPoses();
                    return;
                }
            }
            Advance(Time.deltaTime * timeScale);
            ApplyPoses();
        }

        /// <summary>Advance (or rewind) playback time, honoring loop/clamp at the ends.</summary>
        public void Advance(double deltaSec)
        {
            _timeSec += deltaSec;
            // A live stream has no end to wrap around to: clamp at the head instead.
            bool wrap = loop && !IsLive;
            if (_timeSec > _maxT)
                _timeSec = wrap ? _minT + (_timeSec - _maxT) : _maxT;
            if (_timeSec < _minT)
                _timeSec = wrap ? _maxT - (_minT - _timeSec) : _minT;
        }

        /// <summary>Jump directly to an absolute time (scrubbing). O(1) per entity.</summary>
        public void Seek(double timeSec)
        {
            // Scrubbing means "show me this moment", so stop chasing the live head.
            if (IsLive) followLive = false;
            _timeSec = System.Math.Max(_minT, System.Math.Min(_maxT, timeSec));
            ApplyPoses();
        }

        /// <summary>Rejoin the head of a live stream after scrubbing back through it.</summary>
        public void ResumeLive() => followLive = true;

        private void ApplyPoses()
        {
            foreach (var view in _views)
            {
                bool alive = _source.TrySampleAt(view.Entry, _timeSec, out TspiState st);
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
            _syncedEntities = 0;
            _syncedEvents = 0;
            // Only a file owns disposable resources; a live source is owned by its client.
            (_source as TspiReader)?.Dispose();
            _source = null;
        }

        private void OnDisable() => Unload();
    }
}
