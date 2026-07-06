using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DitherStatistics.Tests {
    /// <summary>
    /// Minimal in-process stand-in for PHD2's event server: accepts one TCP client
    /// at a time on an ephemeral loopback port, sends the greeting events PHD2 emits
    /// on connect (Version, AppState), answers the only two JSON-RPC requests
    /// PHD2Client ever sends (get_exposure in milliseconds, get_pixel_scale in
    /// arcsec/px) and lets tests push scripted event lines over the real socket.
    /// Wire format matches PHD2's EventMonitoring protocol: one JSON object per line.
    /// </summary>
    public sealed class FakePhd2Server : IDisposable {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private readonly Task acceptTask;
        private readonly object writeLock = new object();

        private TcpClient connectedClient;
        private StreamWriter writer;

        public int Port { get; }

        /// <summary>Value returned for get_exposure (PHD2 reports milliseconds)</summary>
        public double ExposureMs { get; set; } = 2000;

        /// <summary>Value returned for get_pixel_scale (arcsec/px)</summary>
        public double PixelScaleArcsec { get; set; } = 1.5;

        /// <summary>Set once both get_exposure and get_pixel_scale have been answered</summary>
        public ManualResetEventSlim InitialQueriesAnswered { get; } = new ManualResetEventSlim(false);

        private bool exposureAnswered;
        private bool pixelScaleAnswered;

        public FakePhd2Server() {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            acceptTask = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop() {
            try {
                while (!cts.IsCancellationRequested) {
                    TcpClient client = await listener.AcceptTcpClientAsync(cts.Token);
                    var stream = client.GetStream();
                    // No BOM in our output; the client's StreamReader would treat a BOM
                    // mid-stream as content
                    var clientWriter = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    lock (writeLock) {
                        connectedClient = client;
                        writer = clientWriter;
                    }

                    SendEvent(new { Event = "Version", PHDVersion = "2.6.13", PHDSubver = "", OverlapSupport = true, MsgVersion = 1, Host = "fake", Inst = 1 });
                    SendEvent(new { Event = "AppState", State = "Guiding" });

                    // PHD2Client's StreamWriter(Encoding.UTF8) prefixes its very first
                    // request with a BOM; StreamReader strips it at stream start
                    using var requestReader = new StreamReader(stream, Encoding.UTF8);
                    await HandleRequests(requestReader);
                }
            } catch (OperationCanceledException) {
            } catch (ObjectDisposedException) {
            } catch (SocketException) {
            }
        }

        private async Task HandleRequests(StreamReader requestReader) {
            try {
                while (!cts.IsCancellationRequested) {
                    string line = await requestReader.ReadLineAsync();
                    if (line == null) return; // client gone, back to accepting

                    try {
                        using JsonDocument doc = JsonDocument.Parse(line);
                        JsonElement root = doc.RootElement;
                        if (!root.TryGetProperty("method", out JsonElement methodElement) ||
                            !root.TryGetProperty("id", out JsonElement idElement)) {
                            continue;
                        }

                        string method = methodElement.GetString();
                        int id = idElement.GetInt32();

                        switch (method) {
                            case "get_exposure":
                                SendRaw(JsonSerializer.Serialize(new { result = ExposureMs, id }));
                                exposureAnswered = true;
                                break;
                            case "get_pixel_scale":
                                SendRaw(JsonSerializer.Serialize(new { result = PixelScaleArcsec, id }));
                                pixelScaleAnswered = true;
                                break;
                            default:
                                SendRaw(JsonSerializer.Serialize(new { error = new { code = 1, message = $"unknown method {method}" }, id }));
                                break;
                        }

                        if (exposureAnswered && pixelScaleAnswered) {
                            InitialQueriesAnswered.Set();
                        }
                    } catch (JsonException) {
                        // Ignore unparseable request lines
                    }
                }
            } catch (IOException) {
            } catch (ObjectDisposedException) {
            }
        }

        // --- Scripted PHD2 events (field names exactly as on the real wire) ---

        public void SendStartGuiding() => SendEvent(new { Event = "StartGuiding" });

        public void SendGuideStep(double dx, double dy) =>
            SendEvent(new { Event = "GuideStep", Frame = 1, Mount = "fake", dx, dy, RADistanceRaw = dx, DECDistanceRaw = dy, StarMass = 1000, SNR = 30.0 });

        public void SendGuidingDithered(double dx, double dy) =>
            SendEvent(new { Event = "GuidingDithered", dx, dy });

        public void SendSettleDone(int status, int totalFrames = 5, int droppedFrames = 0) =>
            SendEvent(new { Event = "SettleDone", Status = status, TotalFrames = totalFrames, DroppedFrames = droppedFrames });

        public void SendStarLost() => SendEvent(new { Event = "StarLost", Frame = 1, Status = 1 });

        public void SendRaw(string line) {
            lock (writeLock) {
                writer?.WriteLine(line);
            }
        }

        private void SendEvent(object payload) => SendRaw(JsonSerializer.Serialize(payload));

        /// <summary>
        /// Close the current connection from the server side (graceful FIN so the
        /// client's read loop sees end-of-stream, i.e. "Connection lost").
        /// </summary>
        public void DropConnection() {
            lock (writeLock) {
                try { connectedClient?.Client?.Shutdown(SocketShutdown.Both); } catch { }
                try { connectedClient?.Close(); } catch { }
                connectedClient = null;
                writer = null;
            }
        }

        public void Dispose() {
            cts.Cancel();
            DropConnection();
            try { listener.Stop(); } catch { }
            try { acceptTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
            InitialQueriesAnswered.Dispose();
            cts.Dispose();
        }
    }
}
