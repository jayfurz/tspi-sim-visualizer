using Tspi.Core.IO;
using UnityEngine;

namespace TspiViewer
{
    /// <summary>
    /// Minimal IMGUI transport bar: scrub slider, play/pause, speed, and an event
    /// timeline. Intentionally dependency-free so the sample scene runs with no UI
    /// setup; a production viewer would swap this for UI Toolkit.
    /// </summary>
    [RequireComponent(typeof(TspiPlaybackController))]
    public sealed class PlaybackHud : MonoBehaviour
    {
        private TspiPlaybackController _pb;
        private TspiLiveClient _live;
        private float _savedScale = 1f;

        private void Awake()
        {
            _pb = GetComponent<TspiPlaybackController>();
            _live = GetComponent<TspiLiveClient>();
        }

        private void OnGUI()
        {
            if (!_pb.Loaded)
            {
                // Make "nothing loaded" self-explanatory instead of a blank screen.
                GUILayout.BeginArea(new Rect(12, 12, 680, 130), GUI.skin.box);
                if (_live != null)
                {
                    GUILayout.Label("TSPI viewer — live: " + _live.Status);
                    GUILayout.Label("Waiting for a producer at " + _live.url +
                                    " (see tools/live-stream/).");
                    GUILayout.EndArea();
                    return;
                }
                GUILayout.Label("TSPI viewer — no file loaded");
                GUILayout.Label(string.IsNullOrEmpty(_pb.LoadError)
                    ? "Set filePath on TspiPlaybackController (absolute, or relative to the repo root)."
                    : _pb.LoadError);
                GUILayout.Label("Generate the walkthrough run from the repo root:");
                GUILayout.Label("    tspi run schemas/examples/ship-to-air.json -o runs/ship-to-air.tspi");
                GUILayout.EndArea();
                return;
            }
            const float pad = 12f;
            float w = Screen.width - 2 * pad;
            var area = new Rect(pad, Screen.height - 108, w, 96);
            GUILayout.BeginArea(area, GUI.skin.box);

            GUILayout.BeginHorizontal();
            bool playing = !Mathf.Approximately(_pb.timeScale, 0f);
            if (GUILayout.Button(playing ? "Pause" : "Play", GUILayout.Width(70)))
            {
                if (playing) { _savedScale = _pb.timeScale; _pb.timeScale = 0f; }
                else _pb.timeScale = Mathf.Approximately(_savedScale, 0f) ? 1f : _savedScale;
            }
            foreach (float s in new[] { 0.25f, 1f, 4f, 16f })
                if (GUILayout.Button($"{s}x", GUILayout.Width(46)))
                    _pb.timeScale = s;
            GUILayout.Label($"t = {_pb.TimeSec,7:0.00} s  /  {_pb.MaxTime:0.00} s", GUILayout.Width(220));
            if (_pb.IsLive)
            {
                // A live stream has no end to loop back to; offer "rejoin the head" instead.
                GUI.color = _pb.followLive ? new Color(0.3f, 0.85f, 0.45f) : Color.white;
                if (GUILayout.Button("LIVE", GUILayout.Width(60)))
                {
                    _pb.ResumeLive();
                    if (Mathf.Approximately(_pb.timeScale, 0f)) _pb.timeScale = 1f;
                }
                GUI.color = Color.white;
                if (_live != null) GUILayout.Label(_live.Status, GUILayout.Width(260));
            }
            else
            {
                _pb.loop = GUILayout.Toggle(_pb.loop, "loop", GUILayout.Width(60));
            }
            GUILayout.EndHorizontal();

            double t = GUILayout.HorizontalSlider((float)_pb.TimeSec, (float)_pb.MinTime, (float)_pb.MaxTime);
            if (!Mathf.Approximately((float)t, (float)_pb.TimeSec))
                _pb.Seek(t);

            // Event ticks under the scrub bar (footer event log: launch/cpa/intercept/...).
            if (Event.current.type == EventType.Repaint && _pb.MaxTime > _pb.MinTime)
            {
                Rect r = GUILayoutUtility.GetLastRect();
                double span = _pb.MaxTime - _pb.MinTime;
                foreach (var ev in _pb.Events)
                {
                    float x = r.x + (float)((ev.TNs / 1e9 - _pb.MinTime) / span) * r.width;
                    GUI.color = EventColor(ev.Kind);
                    GUI.DrawTexture(new Rect(x - 1.5f, r.yMax + 2f, 3f, 8f), Texture2D.whiteTexture);
                }
                GUI.color = Color.white;
            }

            GUILayout.EndArea();
        }

        private static Color EventColor(string kind) => kind switch
        {
            "launch" => new Color(1f, 0.9f, 0.2f),
            "cpa" => new Color(0.2f, 0.9f, 1f),
            "intercept" => new Color(1f, 0.3f, 0.25f),
            "ground_impact" => new Color(0.7f, 0.45f, 0.2f),
            _ => Color.gray,
        };
    }
}
