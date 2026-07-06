using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// Protocol client for PHD2's JSON-RPC server: TCP connection, read loop,
    /// request/response tracking and event parsing. Raises parsed PHD2 events;
    /// the Dither Settings Optimizer state machine that consumes them lives in
    /// DitherOptimizerService (wired by the VM).
    /// Connects to PHD2 on port 4400 (or 4401, 4402... for multiple instances)
    /// </summary>
    public class PHD2Client : IDisposable {
        private TcpClient client;
        private StreamReader reader;
        private StreamWriter writer;
        private CancellationTokenSource cancellationTokenSource;
        private Task readTask;
        private readonly string host;
        private readonly int port;
        private bool isConnected;
        private bool hasLoggedConnectionFailure;

        // JSON-RPC request tracking
        private int jsonRpcId = 0;
        private readonly Dictionary<int, TaskCompletionSource<JsonElement>> pendingRequests = new Dictionary<int, TaskCompletionSource<JsonElement>>();
        private readonly object requestLock = new object();

        // Guide exposure: from the get_exposure query, or measured from the
        // spacing of the guide steps when the query is unavailable
        private double currentGuideExposure = 0;  // Current guide exposure time in seconds
        private DateTime lastGuideStepTime = DateTime.MinValue;  // For calculating exposure from timing

        public event EventHandler<PHD2GuidingDitheredEventArgs> GuidingDithered;
        public event EventHandler<PHD2SettleDoneEventArgs> SettleDone;
        public event EventHandler<PHD2GuideStepEventArgs> GuideStep;
        public event EventHandler StarLost;
        public event EventHandler GuidingStarted;
        public event EventHandler<string> ConnectionStatusChanged;

        public bool IsConnected => isConnected && client?.Connected == true;

        /// <summary>
        /// Current guide exposure time in seconds (0 while unknown). Read by the
        /// optimizer service when it builds a recommendation.
        /// </summary>
        public double CurrentGuideExposure => currentGuideExposure;

        public PHD2Client(string host = "127.0.0.1", int port = 4400) {
            this.host = host;
            this.port = port;
        }

        /// <summary>
        /// Connect to PHD2 server
        /// </summary>
        public async Task<bool> ConnectAsync() {
            try {
                if (IsConnected) {
                    Logger.Info("PHD2Client: Already connected");
                    return true;
                }

                // Only log connection attempt the first time
                if (!hasLoggedConnectionFailure) {
                    Logger.Info($"PHD2Client: Connecting to PHD2 at {host}:{port}...");
                }

                client = new TcpClient();
                await client.ConnectAsync(host, port);

                var stream = client.GetStream();
                reader = new StreamReader(stream, Encoding.UTF8);
                writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                cancellationTokenSource = new CancellationTokenSource();
                isConnected = true;
                hasLoggedConnectionFailure = false; // Reset flag on successful connection

                // Start reading events in background
                readTask = Task.Run(() => ReadEventsAsync(cancellationTokenSource.Token));

                Logger.Info("PHD2Client: Connected successfully!");
                ConnectionStatusChanged?.Invoke(this, "Connected");

                // Query initial exposure time and guider pixel scale
                _ = Task.Run(async () => {
                    await Task.Delay(1000); // Wait for PHD2 to be ready
                    await QueryExposureTime();
                    await QueryPixelScale();
                });

                return true;

            } catch (Exception ex) {
                // Only log error the first time
                if (!hasLoggedConnectionFailure) {
                    Logger.Error($"PHD2Client: Connection failed: {ex.Message}");
                    hasLoggedConnectionFailure = true;
                }
                isConnected = false;
                ConnectionStatusChanged?.Invoke(this, $"Connection failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Disconnect from PHD2 server. The "Disconnected" status event is the signal
        /// for the optimizer service to abort its running collection window (while a
        /// mere connection loss from the read loop deliberately does not clean up).
        /// </summary>
        public void Disconnect() {
            try {
                Logger.Info("PHD2Client: Disconnecting...");
                isConnected = false;

                cancellationTokenSource?.Cancel();
                reader?.Dispose();
                writer?.Dispose();
                client?.Close();

                ConnectionStatusChanged?.Invoke(this, "Disconnected");
                Logger.Info("PHD2Client: Disconnected");

            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error during disconnect: {ex.Message}");
            }
        }

        /// <summary>
        /// Continuously read events from PHD2
        /// </summary>
        private async Task ReadEventsAsync(CancellationToken cancellationToken) {
            Logger.Info("PHD2Client: Started reading events");

            try {
                while (!cancellationToken.IsCancellationRequested && IsConnected) {
                    string line = await reader.ReadLineAsync();

                    if (string.IsNullOrEmpty(line)) {
                        // Connection closed
                        Logger.Warning("PHD2Client: Connection closed by server");
                        isConnected = false;
                        ConnectionStatusChanged?.Invoke(this, "Connection lost");
                        break;
                    }

                    ProcessEvent(line);
                }
            } catch (Exception ex) {
                if (!cancellationToken.IsCancellationRequested) {
                    Logger.Error($"PHD2Client: Error reading events: {ex.Message}");
                    isConnected = false;
                    ConnectionStatusChanged?.Invoke(this, $"Error: {ex.Message}");
                }
            }

            Logger.Info("PHD2Client: Stopped reading events");
        }

        /// <summary>
        /// Process a JSON event line from PHD2
        /// </summary>
        private void ProcessEvent(string jsonLine) {
            try {
                using (JsonDocument doc = JsonDocument.Parse(jsonLine)) {
                    JsonElement root = doc.RootElement;

                    // Check if this is a JSON-RPC response
                    if (root.TryGetProperty("id", out JsonElement idElement) && !root.TryGetProperty("Event", out _)) {
                        HandleJsonRpcResponse(root);
                        return;
                    }

                    if (!root.TryGetProperty("Event", out JsonElement eventElement)) {
                        return; // Not an event message
                    }

                    string eventName = eventElement.GetString();

                    switch (eventName) {
                        case "GuidingDithered":
                            HandleGuidingDithered(root);
                            break;

                        case "SettleDone":
                            HandleSettleDone(root);
                            break;

                        case "Version":
                            HandleVersion(root);
                            break;

                        case "AppState":
                            HandleAppState(root);
                            break;

                        case "StarLost":
                            Logger.Warning($"PHD2: {eventName}");
                            StarLost?.Invoke(this, EventArgs.Empty);
                            break;

                        // Important events to log
                        case "Paused":
                        case "Resumed":
                        case "LockPositionLost":
                        case "CalibrationFailed":
                        case "Alert":
                            Logger.Warning($"PHD2: {eventName}");
                            break;

                        // Informational events (log once)
                        case "CalibrationComplete":
                            Logger.Info($"PHD2: {eventName}");
                            break;

                        case "StartGuiding":
                            Logger.Info($"PHD2: {eventName}");
                            GuidingStarted?.Invoke(this, EventArgs.Empty);
                            // Query exposure time when guiding starts
                            _ = Task.Run(async () => await QueryExposureTime());
                            break;

                        case "ConfigurationChange":
                            Logger.Info($"PHD2: {eventName}");
                            // Re-query exposure time when configuration changes
                            _ = Task.Run(async () => await QueryExposureTime());
                            break;

                        // Guide step events - log to file
                        case "GuideStep":
                            HandleGuideStep(root);
                            break;

                        // Frequent events - don't log to reduce clutter
                        case "Settling":
                        case "SettleBegin":
                        case "LockPositionSet":
                        case "LockPositionShift":
                        case "LoopingExposures":
                        case "LoopingExposuresStopped":
                        case "StarSelected":
                            // Silently ignore these frequent/unimportant events
                            break;

                        // Log any unknown events for debugging
                        default:
                            Logger.Debug($"PHD2Client: Unknown event: {eventName}");
                            break;
                    }
                }
            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error processing event: {ex.Message}");
                Logger.Error($"PHD2Client: JSON: {jsonLine}");
            }
        }

        private void HandleGuidingDithered(JsonElement root) {
            try {
                double dx = root.GetProperty("dx").GetDouble();
                double dy = root.GetProperty("dy").GetDouble();

                Logger.Info($"PHD2Client: 🎯 GuidingDithered (DITHER START) - dx={dx:F2}, dy={dy:F2}");

                var args = new PHD2GuidingDitheredEventArgs {
                    DeltaX = dx,
                    DeltaY = dy,
                    Timestamp = DateTime.Now
                };

                GuidingDithered?.Invoke(this, args);

            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error handling GuidingDithered: {ex.Message}");
            }
        }

        private void HandleSettleDone(JsonElement root) {
            try {
                // The pixel scale query right after connect fails if PHD2 was not yet
                // calibrated; keep retrying at each settled dither until we have it
                if (GuiderPixelScaleArcsec == null && IsConnected) {
                    _ = Task.Run(QueryPixelScale);
                }

                int status = root.GetProperty("Status").GetInt32();
                int totalFrames = root.GetProperty("TotalFrames").GetInt32();
                int droppedFrames = root.GetProperty("DroppedFrames").GetInt32();

                string error = null;
                if (root.TryGetProperty("Error", out JsonElement errorElement)) {
                    error = errorElement.GetString();
                }

                bool success = status == 0;

                Logger.Info($"PHD2Client: SettleDone - Status={status}, Success={success}, TotalFrames={totalFrames}, DroppedFrames={droppedFrames}");

                var args = new PHD2SettleDoneEventArgs {
                    Success = success,
                    Status = status,
                    TotalFrames = totalFrames,
                    DroppedFrames = droppedFrames,
                    Error = error
                };

                SettleDone?.Invoke(this, args);

            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error handling SettleDone: {ex.Message}");
            }
        }

        private void HandleVersion(JsonElement root) {
            try {
                string phdVersion = root.GetProperty("PHDVersion").GetString();
                string phdSubver = root.GetProperty("PHDSubver").GetString();
                int msgVersion = root.GetProperty("MsgVersion").GetInt32();

                Logger.Info($"PHD2Client: Connected to PHD2 version {phdVersion}.{phdSubver} (Message Protocol v{msgVersion})");

            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error handling Version: {ex.Message}");
            }
        }

        private void HandleAppState(JsonElement root) {
            try {
                string state = root.GetProperty("State").GetString();
                Logger.Info($"PHD2Client: PHD2 AppState: {state}");

            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error handling AppState: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle GuideStep event
        /// PHD2 GuideStep provides:
        ///   - dx/dy: Camera coordinates (pixels on guide chip)
        /// RunningRMS/RMSStdDev in the args are deliberately no longer filled here:
        /// the session statistics moved to DitherOptimizerService (the fields stay
        /// on the args class for compatibility).
        /// </summary>
        private void HandleGuideStep(JsonElement root) {
            try {
                // Extract guide step data
                double dx = 0;
                double dy = 0;

                // Get dx/dy (camera coordinates - pixels on guide chip)
                if (root.TryGetProperty("dx", out JsonElement dxElement)) {
                    dx = dxElement.GetDouble();
                }
                if (root.TryGetProperty("dy", out JsonElement dyElement)) {
                    dy = dyElement.GetDouble();
                }

                // Calculate exposure time from timing between guide steps if API query failed
                double exposure = currentGuideExposure;
                DateTime currentTime = DateTime.Now;

                if (exposure == 0) {
                    if (lastGuideStepTime == DateTime.MinValue) {
                        // First guide step - skip it since we have no timing reference yet
                        lastGuideStepTime = currentTime;
                        Logger.Debug("PHD2Client: Skipping first GuideStep (no timing reference)");
                        return;
                    }
                    exposure = (currentTime - lastGuideStepTime).TotalSeconds;
                    currentGuideExposure = exposure;
                    Logger.Info($"PHD2Client: Exposure time calculated from guide step timing: {exposure:F3} seconds");
                }
                lastGuideStepTime = currentTime;

                var args = new PHD2GuideStepEventArgs {
                    DX = dx,
                    DY = dy,
                    Exposure = exposure,
                    Timestamp = currentTime
                };

                GuideStep?.Invoke(this, args);

            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error handling GuideStep: {ex.Message}");
            }
        }

        /// <summary>
        /// Send a JSON-RPC request to PHD2 and wait for response
        /// </summary>
        private async Task<JsonElement> SendJsonRpcRequest(string method, object parameters = null) {
            if (!IsConnected) {
                throw new InvalidOperationException("Not connected to PHD2");
            }

            int requestId;
            TaskCompletionSource<JsonElement> tcs = new TaskCompletionSource<JsonElement>();

            lock (requestLock) {
                requestId = ++jsonRpcId;
                pendingRequests[requestId] = tcs;
            }

            try {
                // Build JSON-RPC request (only include params if provided)
                string jsonRequest;
                if (parameters != null) {
                    var requestWithParams = new {
                        method = method,
                        id = requestId,
                        @params = parameters
                    };
                    jsonRequest = JsonSerializer.Serialize(requestWithParams);
                } else {
                    var requestNoParams = new {
                        method = method,
                        id = requestId
                    };
                    jsonRequest = JsonSerializer.Serialize(requestNoParams);
                }

                // Send request
                await writer.WriteLineAsync(jsonRequest);
                Logger.Debug($"PHD2Client: Sent JSON-RPC request: {jsonRequest}");

                // Wait for response with timeout
                var timeoutTask = Task.Delay(5000);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == timeoutTask) {
                    throw new TimeoutException($"PHD2 JSON-RPC request '{method}' timed out");
                }

                return await tcs.Task;

            } finally {
                lock (requestLock) {
                    pendingRequests.Remove(requestId);
                }
            }
        }

        /// <summary>
        /// Handle JSON-RPC response from PHD2
        /// </summary>
        private void HandleJsonRpcResponse(JsonElement root) {
            try {
                if (!root.TryGetProperty("id", out JsonElement idElement)) {
                    return;
                }

                // Check if id is null
                if (idElement.ValueKind == JsonValueKind.Null) {
                    Logger.Debug("PHD2Client: Received JSON-RPC response with null id, ignoring");
                    return;
                }

                int id = idElement.GetInt32();

                lock (requestLock) {
                    if (pendingRequests.TryGetValue(id, out TaskCompletionSource<JsonElement> tcs)) {
                        if (root.TryGetProperty("error", out JsonElement errorElement)) {
                            string errorMsg = errorElement.ToString();
                            Logger.Warning($"PHD2Client: JSON-RPC error for request {id}: {errorMsg}");
                            tcs.SetException(new Exception($"PHD2 RPC error: {errorMsg}"));
                        } else if (root.TryGetProperty("result", out JsonElement resultElement)) {
                            Logger.Debug($"PHD2Client: JSON-RPC response received for id {id}");
                            tcs.SetResult(resultElement);
                        } else {
                            tcs.SetException(new Exception("Invalid JSON-RPC response"));
                        }
                    }
                }
            } catch (Exception ex) {
                Logger.Warning($"PHD2Client: Error handling JSON-RPC response: {ex.Message}");
            }
        }

        /// <summary>
        /// Query exposure time from PHD2 via JSON-RPC
        /// </summary>
        private async Task QueryExposureTime() {
            try {
                JsonElement result = await SendJsonRpcRequest("get_exposure");

                // Check if result is null
                if (result.ValueKind == JsonValueKind.Null) {
                    Logger.Debug("PHD2Client: Exposure time query returned null - will use timing calculation");
                    return;
                }

                // PHD2 returns exposure time in milliseconds
                double exposureMs = result.GetDouble();
                currentGuideExposure = exposureMs / 1000.0;  // Convert to seconds

                Logger.Info($"PHD2Client: Exposure time from PHD2 API: {currentGuideExposure:F3} seconds ({exposureMs} ms)");

            } catch (Exception ex) {
                Logger.Debug($"PHD2Client: Could not query exposure time from PHD2 API ({ex.Message}), will use timing calculation");
            }
        }

        /// <summary>
        /// Guider image scale in arcsec/pixel as reported by PHD2 (get_pixel_scale),
        /// null until successfully queried. Used to convert dither offsets from
        /// guide-camera pixels to main-camera pixels for the quality metrics.
        /// </summary>
        public double? GuiderPixelScaleArcsec { get; private set; }

        /// <summary>
        /// Query guider pixel scale from PHD2 via JSON-RPC
        /// </summary>
        private async Task QueryPixelScale() {
            try {
                JsonElement result = await SendJsonRpcRequest("get_pixel_scale");

                if (result.ValueKind == JsonValueKind.Null) {
                    Logger.Debug("PHD2Client: Pixel scale query returned null (no calibration?)");
                    return;
                }

                double scale = result.GetDouble();
                if (scale > 0) {
                    GuiderPixelScaleArcsec = scale;
                    Logger.Info($"PHD2Client: Guider pixel scale from PHD2 API: {scale:F2} arcsec/px");
                }

            } catch (Exception ex) {
                Logger.Debug($"PHD2Client: Could not query pixel scale from PHD2 API ({ex.Message})");
            }
        }

        public void Dispose() {
            Disconnect();
            cancellationTokenSource?.Dispose();
            reader?.Dispose();
            writer?.Dispose();
            client?.Dispose();
        }
    }
}
