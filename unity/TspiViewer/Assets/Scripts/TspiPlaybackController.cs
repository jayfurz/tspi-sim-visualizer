using System.Collections.Generic;
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
        [Tooltip("Absolute path to a .tspi file. On device, copy into persistentDataPath first.")]
        public string filePath = "";

        [Tooltip("Playback rate. 1 = real time, negative = reverse, 0 = paused.")]
        public float timeScale = 1.0f;

        public bool loop = true;

        [Header("Presentation")]
        public GameObject bluePrefab;
        public GameObject redPrefab;
        public GameObject neutralPrefab;
        public float trailSeconds = 8f;

        private TspiReader _reader;
        private double _timeSec;
        private double _minT, _maxT;
        private readonly List<EntityView> _views = new();

        public double TimeSec => _timeSec;
        public double MinTime => _minT;
        public double MaxTime => _maxT;
        public bool Loaded => _reader != null;
        public IReadOnlyList<EntityView> Views => _views;
        private static readonly List<TspiEventEntry> NoEvents = new();
        /// <summary>Footer event log of the loaded file (launch/cpa/intercept/...), for HUD timelines.</summary>
        public IReadOnlyList<TspiEventEntry> Events => _reader != null ? _reader.Events : NoEvents;

        private sealed class EntityViewImpl { }

        public struct EntityView
        {
            public TspiEntityEntry Entry;
            public Transform Transform;
            public TrailRenderer Trail;
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
            _reader = TspiReader.Open(path);
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

            var trail = go.GetComponent<TrailRenderer>();
            if (trail == null && trailSeconds > 0f)
            {
                trail = go.AddComponent<TrailRenderer>();
                trail.time = trailSeconds;
                trail.startWidth = e.Type == "munition" ? 3f : 8f;
                trail.endWidth = 0f;
                trail.material = new Material(Shader.Find("Sprites/Default"));
                Color c = e.Team == "blue" ? new Color(0.3f, 0.6f, 1f)
                        : e.Team == "red" ? new Color(1f, 0.35f, 0.3f)
                        : Color.gray;
                trail.startColor = c;
                trail.endColor = new Color(c.r, c.g, c.b, 0f);
            }

            _views.Add(new EntityView { Entry = e, Transform = go.transform, Trail = trail });
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
                if (v.Transform != null) Destroy(v.Transform.gameObject);
            _views.Clear();
            _reader?.Dispose();
            _reader = null;
        }

        private void OnDisable() => Unload();
    }
}
