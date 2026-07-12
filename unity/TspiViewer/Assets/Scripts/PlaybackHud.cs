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
        private float _savedScale = 1f;

        private void Awake() => _pb = GetComponent<TspiPlaybackController>();

        private void OnGUI()
        {
            if (!_pb.Loaded) return;
            const float pad = 12f;
            float w = Screen.width - 2 * pad;
            var area = new Rect(pad, Screen.height - 96, w, 84);
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
            _pb.loop = GUILayout.Toggle(_pb.loop, "loop", GUILayout.Width(60));
            GUILayout.EndHorizontal();

            double t = GUILayout.HorizontalSlider((float)_pb.TimeSec, (float)_pb.MinTime, (float)_pb.MaxTime);
            if (!Mathf.Approximately((float)t, (float)_pb.TimeSec))
                _pb.Seek(t);

            GUILayout.EndArea();
        }
    }
}
