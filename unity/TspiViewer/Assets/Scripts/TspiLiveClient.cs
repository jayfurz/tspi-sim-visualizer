using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tspi.Core.Live;
using UnityEngine;

namespace TspiViewer
{
    /// <summary>
    /// Subscribes to a live TSPI producer (tools/live-stream/PROTOCOL.md) and feeds a
    /// <see cref="TspiPlaybackController"/> while the run is still flying.
    ///
    /// Unity keeps its playback-only contract: this component never simulates. The
    /// producer sends the .tspi format's own 64-byte records, <see cref="LiveTspiSource"/>
    /// stores them, and the controller samples them through the same interpolator it uses
    /// for a file — a live pose and the replayed pose of the same run are the same number.
    ///
    /// Threading: the socket is read on a background task, which only enqueues raw frames.
    /// All decoding and all Unity work happens on the main thread in <see cref="Update"/>,
    /// because <see cref="LiveTspiSource"/> is single-threaded by design.
    ///
    /// Platforms: ClientWebSocket needs a real socket stack — desktop/standalone/editor.
    /// WebGL builds cannot use it (use the browser viewer in web/viewer there, which
    /// speaks the same protocol).
    /// </summary>
    [RequireComponent(typeof(TspiPlaybackController))]
    public sealed class TspiLiveClient : MonoBehaviour
    {
        [Tooltip("Producer endpoint, e.g. ws://localhost:8787/stream (see tools/live-stream/).")]
        public string url = "ws://localhost:8787/stream";

        [Tooltip("Connect as soon as this component is enabled.")]
        public bool connectOnEnable = true;

        [Tooltip("Reconnect after the producer restarts or the link drops.")]
        public bool autoReconnect = true;

        public float reconnectSeconds = 2f;

        [Tooltip("Reject a single control message larger than this (protection against a broken producer).")]
        public int maxMessageBytes = 8 << 20;

        [Tooltip("Frames decoded per Update. Caps main-thread work when a fast producer runs ahead.")]
        public int maxFramesPerUpdate = 512;

        /// <summary>Human-readable link state for the HUD.</summary>
        public string Status { get; private set; } = "idle";

        /// <summary>The stream being received, or null before the first hello.</summary>
        public LiveTspiSource Source { get; private set; }

        public bool Connected => _socketOpen;

        private TspiPlaybackController _playback;
        private readonly ConcurrentQueue<Frame> _inbox = new ConcurrentQueue<Frame>();
        private CancellationTokenSource _cancel;
        private volatile bool _socketOpen;
        private volatile string _fault;

        private struct Frame
        {
            public bool IsText;
            public string Text;
            public byte[] Bytes;
            public int Length;
        }

        private void Awake() => _playback = GetComponent<TspiPlaybackController>();

        private void OnEnable()
        {
            if (connectOnEnable) Connect();
        }

        private void OnDisable() => Disconnect();

        /// <summary>(Re)connect to <see cref="url"/>, dropping any stream in progress.</summary>
        public void Connect()
        {
            Disconnect();
            _cancel = new CancellationTokenSource();
            Status = "connecting…";
            Task.Run(() => ReceiveLoopAsync(url, _cancel.Token));
        }

        public void Disconnect()
        {
            if (_cancel == null) return;
            _cancel.Cancel();
            _cancel = null;
            _socketOpen = false;
            Status = "disconnected";
            while (_inbox.TryDequeue(out _)) { }
        }

        // ---- background: socket only, no decoding, no Unity API ----

        private async Task ReceiveLoopAsync(string endpoint, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using (var ws = new ClientWebSocket())
                    {
                        await ws.ConnectAsync(new Uri(endpoint), ct).ConfigureAwait(false);
                        _socketOpen = true;
                        _fault = null;
                        var buffer = new byte[64 * 1024];
                        using (var message = new MemoryStream())
                        {
                            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                            {
                                message.SetLength(0);
                                WebSocketReceiveResult result;
                                do
                                {
                                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct)
                                        .ConfigureAwait(false);
                                    if (result.MessageType == WebSocketMessageType.Close) break;
                                    if (message.Length + result.Count > maxMessageBytes)
                                        throw new LiveProtocolError("control message exceeded " +
                                            maxMessageBytes + " bytes");
                                    message.Write(buffer, 0, result.Count);
                                } while (!result.EndOfMessage);
                                if (result.MessageType == WebSocketMessageType.Close) break;

                                if (result.MessageType == WebSocketMessageType.Text)
                                {
                                    _inbox.Enqueue(new Frame
                                    {
                                        IsText = true,
                                        Text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length),
                                    });
                                }
                                else
                                {
                                    // Copy out: the MemoryStream buffer is reused next loop.
                                    int len = (int)message.Length;
                                    var bytes = new byte[len];
                                    Buffer.BlockCopy(message.GetBuffer(), 0, bytes, 0, len);
                                    _inbox.Enqueue(new Frame { IsText = false, Bytes = bytes, Length = len });
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return;   // Disconnect() or scene teardown
                }
                catch (Exception ex)
                {
                    _fault = ex.Message;
                }

                _socketOpen = false;
                if (!autoReconnect || ct.IsCancellationRequested) return;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Mathf.Max(0.25f, reconnectSeconds)), ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
            }
        }

        // ---- main thread: decode, then hand to the viewer ----

        private void Update()
        {
            int budget = Mathf.Max(1, maxFramesPerUpdate);
            while (budget-- > 0 && _inbox.TryDequeue(out Frame frame))
            {
                try
                {
                    if (frame.IsText) HandleText(frame.Text);
                    else if (Source != null) Source.IngestBatch(frame.Bytes, 0, frame.Length);
                }
                catch (LiveProtocolError ex)
                {
                    // A producer that violates the protocol is a bug worth seeing, not a
                    // reason to tear down what has already been received.
                    Debug.LogWarning("TspiLiveClient: " + ex.Message);
                    Status = "protocol error: " + ex.Message;
                }
            }
            UpdateStatus();
        }

        private void HandleText(string json)
        {
            if (Source == null || IsHello(json))
            {
                Source = LiveTspiSource.FromHello(json);
                _playback.BindLive(Source);
                return;
            }
            Source.IngestText(json);
        }

        /// <summary>A fresh hello means the producer restarted: start a new stream.</summary>
        private static bool IsHello(string json) => json.Contains("\"hello\"");

        private void UpdateStatus()
        {
            if (_cancel == null) { Status = "idle"; return; }
            if (Source != null && Source.Ended)
            {
                Status = "ended · " + Source.Received + " records";
                return;
            }
            if (!_socketOpen)
            {
                Status = _fault == null ? "connecting…" : "offline (" + _fault + ")";
                return;
            }
            Status = "LIVE · " + (Source == null ? "waiting for hello" :
                Source.Received + " records" + (Source.GapsFilled > 0 ? ", " + Source.GapsFilled + " filled" : ""));
        }
    }
}
