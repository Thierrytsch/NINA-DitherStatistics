using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// Client for connecting to PHD2's JSON-RPC server and receiving dither events
    /// Connects to PHD2 on port 4400 (or 4401, 4402... for multiple instances)
    /// </summary>
    public class PHD2Client : IDisposable {
        // Rolling reference window of stable-guiding distances used for the quantiles;
        // bounded by count and age so the thresholds track current seeing conditions
        private const int REFERENCE_MAX_POINTS = 400;
        private static readonly TimeSpan REFERENCE_MAX_AGE = TimeSpan.FromMinutes(15);

        // Collection window per dither: until SettleDone + POST_SETTLE_STEPS guide steps,
        // with a hard cap in case SettleDone never arrives
        private const int POST_SETTLE_STEPS = 10;
        private const int COLLECTION_CAP_MS = 120000;

        private TcpClient client;
        private StreamReader reader;
        private StreamWriter writer;
        private CancellationTokenSource cancellationTokenSource;
        private Task readTask;
        private readonly string host;
        private readonly int port;
        private bool isConnected;
        private bool hasLoggedConnectionFailure;
        private bool isDithering = false;  // Flag to track dithering state

        // JSON-RPC request tracking
        private int jsonRpcId = 0;
        private readonly Dictionary<int, TaskCompletionSource<JsonElement>> pendingRequests = new Dictionary<int, TaskCompletionSource<JsonElement>>();
        private readonly object requestLock = new object();

        // Guiding session tracking (guarded by sessionLock: written on the read thread,
        // read from analysis tasks on timer/thread-pool threads)
        private readonly object sessionLock = new object();
        private DateTime sessionStartTime;
        private readonly List<double> sessionDX = new List<double>();  // RA values (dx)
        private readonly List<double> sessionDY = new List<double>();  // DEC values (dy)
        private readonly List<double> sessionRMS = new List<double>(); // point distances for stddev calculation
        private double currentGuideExposure = 0;  // Current guide exposure time in seconds
        private DateTime lastGuideStepTime = DateTime.MinValue;  // For calculating exposure from timing

        // Rolling window of stable-guiding distances (guarded by referenceLock)
        private readonly object referenceLock = new object();
        private readonly List<KeyValuePair<DateTime, double>> referenceWindow = new List<KeyValuePair<DateTime, double>>();

        // ditherDataLock guards allDitherData, seriesInfos, currentDitherSeries,
        // currentSeriesInfo and the collection state flags
        private readonly List<DitherDataPoint> allDitherData = new List<DitherDataPoint>();
        private readonly Dictionary<int, DitherSeriesInfo> seriesInfos = new Dictionary<int, DitherSeriesInfo>();
        private readonly object ditherDataLock = new object();
        private List<DitherDataPoint> currentDitherSeries = new List<DitherDataPoint>();
        private DitherSeriesInfo currentSeriesInfo;
        private System.Timers.Timer ditherCollectionTimer;  // hard cap in case SettleDone never arrives
        private bool isCollectingDitherData = false;
        private int postSettleStepsRemaining = -1;          // -1 = settle not done yet
        private int ditherSeriesCounter = 0;                // Counter to identify dither series

        // Name of the statistics profile the collected data belongs to; set by the VM
        // on profile switches, used to keep the diagnostic export files separate per profile
        public string CurrentProfileName { get; set; } = "Default";

        public event EventHandler<PHD2GuidingDitheredEventArgs> GuidingDithered;
        public event EventHandler<PHD2SettleDoneEventArgs> SettleDone;
        public event EventHandler<PHD2GuideStepEventArgs> GuideStep;
        public event EventHandler<string> ConnectionStatusChanged;
        public event EventHandler<DitherSettingsRecommendation> DitherRecommendationUpdated;

        public bool IsConnected => isConnected && client?.Connected == true;

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
        /// Disconnect from PHD2 server
        /// </summary>
        public void Disconnect() {
            try {
                Logger.Info("PHD2Client: Disconnecting...");
                isConnected = false;
                isDithering = false;

                cancellationTokenSource?.Cancel();
                reader?.Dispose();
                writer?.Dispose();
                client?.Close();

                // Reset session tracking on disconnect
                lock (sessionLock) {
                    sessionDX.Clear();
                    sessionDY.Clear();
                    sessionRMS.Clear();
                }
                lock (referenceLock) {
                    referenceWindow.Clear();
                }

                // Stop and cleanup the running collection window (collected points of the
                // aborted window are discarded; accumulated series stay for the analysis)
                lock (ditherDataLock) {
                    if (ditherCollectionTimer != null) {
                        ditherCollectionTimer.Stop();
                        ditherCollectionTimer.Dispose();
                        ditherCollectionTimer = null;
                    }
                    isCollectingDitherData = false;
                    postSettleStepsRemaining = -1;
                    currentDitherSeries.Clear();
                    currentSeriesInfo = null;
                }

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
                            // A lost star during the collection window invalidates the series
                            lock (ditherDataLock) {
                                if (isCollectingDitherData && currentSeriesInfo != null) {
                                    currentSeriesInfo.StarLost = true;
                                }
                            }
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
                            StartNewGuidingSession();
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
                // Set dithering flag to exclude guide steps during dithering/settling
                isDithering = true;

                double dx = root.GetProperty("dx").GetDouble();
                double dy = root.GetProperty("dy").GetDouble();

                Logger.Info($"PHD2Client: 🎯 GuidingDithered (DITHER START) - dx={dx:F2}, dy={dy:F2}");

                var args = new PHD2GuidingDitheredEventArgs {
                    DeltaX = dx,
                    DeltaY = dy,
                    Timestamp = DateTime.Now
                };

                GuidingDithered?.Invoke(this, args);

                // Start collecting guide steps for this dither
                StartDitherDataCollection(args.Timestamp);

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

                // Reset dithering flag to resume statistics calculation
                isDithering = false;

                Logger.Info($"PHD2Client: SettleDone - Status={status}, Success={success}, TotalFrames={totalFrames}, DroppedFrames={droppedFrames}");

                // Record settle outcome on the running series and arm the post-settle
                // countdown that ends the collection window
                lock (ditherDataLock) {
                    if (isCollectingDitherData && currentSeriesInfo != null) {
                        currentSeriesInfo.SettleReceived = true;
                        currentSeriesInfo.SettleFailed = !success;
                        currentSeriesInfo.MeasuredSettleDuration = Math.Max(0, (DateTime.Now - currentSeriesInfo.DitherEventTime).TotalSeconds);
                        postSettleStepsRemaining = POST_SETTLE_STEPS;
                    }
                }

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
        /// Start a new guiding session - resets session tracking
        /// </summary>
        private void StartNewGuidingSession() {
            try {
                lock (sessionLock) {
                    sessionStartTime = DateTime.Now;
                    sessionDX.Clear();
                    sessionDY.Clear();
                    sessionRMS.Clear();
                }

                Logger.Info($"PHD2Client: New guiding session started");

            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error starting new guiding session: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle GuideStep event
        /// PHD2 GuideStep provides:
        ///   - dx/dy: Camera coordinates (pixels on guide chip)
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

                double pairDistance = Math.Sqrt(dx * dx + dy * dy);

                // During dithering/settling: mark RMS columns as NaN
                if (isDithering) {
                    var ditherArgs = new PHD2GuideStepEventArgs {
                        DX = dx,
                        DY = dy,
                        RunningRMS = double.NaN,
                        RMSStdDev = double.NaN,
                        Exposure = exposure,
                        Timestamp = currentTime
                    };

                    GuideStep?.Invoke(this, ditherArgs);

                    CollectDitherPoint(dx, dy, pairDistance, exposure, currentTime);
                    return;
                }

                // Normal operation: track session statistics and the rolling reference window
                double runningRMS;
                double rmsStdDev;
                lock (sessionLock) {
                    sessionDX.Add(dx);
                    sessionDY.Add(dy);
                    sessionRMS.Add(pairDistance);
                    (runningRMS, rmsStdDev) = ComputeSessionStatsLocked();
                    if (sessionDX.Count == 1) {
                        // For the first point, use its distance as RMS placeholder
                        runningRMS = pairDistance;
                    }
                }

                lock (referenceLock) {
                    referenceWindow.Add(new KeyValuePair<DateTime, double>(currentTime, pairDistance));
                    TrimReferenceWindowLocked(currentTime);
                }

                // Create event args
                var args = new PHD2GuideStepEventArgs {
                    DX = dx,
                    DY = dy,
                    RunningRMS = runningRMS,
                    RMSStdDev = rmsStdDev,
                    Exposure = exposure,
                    Timestamp = currentTime
                };

                // Raise event (for UI updates if needed)
                GuideStep?.Invoke(this, args);

                // Collect post-settle data while the collection window is still open
                CollectDitherPoint(dx, dy, pairDistance, exposure, currentTime);

            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error handling GuideStep: {ex.Message}");
            }
        }

        /// <summary>
        /// Running session RMS using PHD2's method (sqrt(ra_stddev² + dec_stddev²))
        /// and the standard deviation of the point distances. Caller must hold sessionLock.
        /// </summary>
        private (double runningRMS, double rmsStdDev) ComputeSessionStatsLocked() {
            double runningRMS = 0;
            double rmsStdDev = 0;

            if (sessionDX.Count > 1) {
                // RA standard deviation: sqrt(Σ(dx_i - mean_dx)² / (n-1))
                double meanDX = sessionDX.Average();
                double sumSquaredDeviationsDX = sessionDX.Sum(d => Math.Pow(d - meanDX, 2));
                double raStdDev = Math.Sqrt(sumSquaredDeviationsDX / (sessionDX.Count - 1));

                // DEC standard deviation: sqrt(Σ(dy_i - mean_dy)² / (n-1))
                double meanDY = sessionDY.Average();
                double sumSquaredDeviationsDY = sessionDY.Sum(d => Math.Pow(d - meanDY, 2));
                double decStdDev = Math.Sqrt(sumSquaredDeviationsDY / (sessionDY.Count - 1));

                // Total RMS: sqrt(ra_stddev² + dec_stddev²)
                runningRMS = Math.Sqrt(raStdDev * raStdDev + decStdDev * decStdDev);
            }

            if (sessionRMS.Count > 1) {
                double meanRMS = sessionRMS.Average();
                double sumSquaredDeviationsRMS = sessionRMS.Sum(r => Math.Pow(r - meanRMS, 2));
                rmsStdDev = Math.Sqrt(sumSquaredDeviationsRMS / (sessionRMS.Count - 1));
            }

            return (runningRMS, rmsStdDev);
        }

        /// <summary>
        /// Drop reference points that exceed the window's age or count bounds.
        /// Caller must hold referenceLock.
        /// </summary>
        private void TrimReferenceWindowLocked(DateTime now) {
            DateTime cutoff = now - REFERENCE_MAX_AGE;
            referenceWindow.RemoveAll(p => p.Key < cutoff);
            if (referenceWindow.Count > REFERENCE_MAX_POINTS) {
                referenceWindow.RemoveRange(0, referenceWindow.Count - REFERENCE_MAX_POINTS);
            }
        }

        /// <summary>
        /// Current settle-tolerance thresholds (P90/P95/P99 of the reference window),
        /// or zeros while the window has too few points to be meaningful.
        /// </summary>
        private double[] GetReferenceThresholds() {
            List<double> values;
            lock (referenceLock) {
                TrimReferenceWindowLocked(DateTime.Now);
                values = referenceWindow.Select(p => p.Value).ToList();
            }

            return DitherAnalysis.CalculateThresholds(values);
        }

        /// <summary>
        /// Add a guide step to the running dither series (if a collection window is open)
        /// and count down the post-settle steps that end the window.
        /// </summary>
        private void CollectDitherPoint(double dx, double dy, double pairDistance, double exposure, DateTime timestamp) {
            bool runAnalysis = false;
            lock (ditherDataLock) {
                if (!isCollectingDitherData) return;

                currentDitherSeries.Add(new DitherDataPoint {
                    DitherSeriesId = ditherSeriesCounter,
                    DX = dx,
                    DY = dy,
                    PairRMS = pairDistance,
                    Exposure = exposure,
                    Timestamp = timestamp
                });

                if (postSettleStepsRemaining > 0) {
                    postSettleStepsRemaining--;
                    if (postSettleStepsRemaining == 0) {
                        runAnalysis = FinalizeCurrentSeriesLocked();
                    }
                }
            }
            if (runAnalysis) {
                _ = Task.Run(() => RunAnalysisAndRecommendation());
            }
        }

        /// <summary>
        /// Start collecting guide steps for a new dither series. The window stays open
        /// until SettleDone + POST_SETTLE_STEPS guide steps (hard cap COLLECTION_CAP_MS).
        /// </summary>
        private void StartDitherDataCollection(DateTime ditherEventTime) {
            try {
                bool runAnalysis = false;
                lock (ditherDataLock) {
                    // Rapid dithering: close the previous window before opening a new one
                    if (isCollectingDitherData) {
                        runAnalysis = FinalizeCurrentSeriesLocked();
                        if (runAnalysis) {
                            Logger.Info("PHD2Client: Previous dither series finalized early (next dither arrived before window closed)");
                        }
                    }

                    if (ditherCollectionTimer != null) {
                        ditherCollectionTimer.Stop();
                        ditherCollectionTimer.Dispose();
                    }

                    ditherSeriesCounter++;
                    currentSeriesInfo = new DitherSeriesInfo {
                        DitherSeriesId = ditherSeriesCounter,
                        DitherEventTime = ditherEventTime
                    };
                    currentDitherSeries = new List<DitherDataPoint>();
                    isCollectingDitherData = true;
                    postSettleStepsRemaining = -1;

                    ditherCollectionTimer = new System.Timers.Timer(COLLECTION_CAP_MS);
                    ditherCollectionTimer.Elapsed += OnCollectionCapElapsed;
                    ditherCollectionTimer.AutoReset = false;
                    ditherCollectionTimer.Start();

                    Logger.Info($"PHD2Client: Started dither data collection (series #{ditherSeriesCounter})");
                }

                if (runAnalysis) {
                    _ = Task.Run(() => RunAnalysisAndRecommendation());
                }

            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error starting dither data collection: {ex.Message}");
            }
        }

        /// <summary>
        /// Hard cap reached without the post-settle countdown finishing
        /// (SettleDone never arrived or guide steps stopped)
        /// </summary>
        private void OnCollectionCapElapsed(object sender, System.Timers.ElapsedEventArgs e) {
            try {
                bool runAnalysis;
                lock (ditherDataLock) {
                    // A stale timer whose window was already replaced by a newer dither
                    // (rapid dithering) must not close the new window
                    if (!ReferenceEquals(sender, ditherCollectionTimer)) return;
                    runAnalysis = FinalizeCurrentSeriesLocked();
                }
                if (runAnalysis) {
                    Logger.Info("PHD2Client: Dither data collection ended at hard cap");
                    RunAnalysisAndRecommendation();
                }
            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error in collection cap handler: {ex.Message}");
            }
        }

        /// <summary>
        /// Close the running collection window: capture the reference thresholds valid
        /// right now, move the collected points into the accumulated data and store the
        /// series metadata. Caller must hold ditherDataLock.
        /// Returns true when the finalized series contained data (analysis worthwhile).
        /// </summary>
        private bool FinalizeCurrentSeriesLocked() {
            if (!isCollectingDitherData) return false;
            isCollectingDitherData = false;
            postSettleStepsRemaining = -1;
            ditherCollectionTimer?.Stop();

            bool hadData = currentDitherSeries.Count > 0;
            if (hadData && currentSeriesInfo != null) {
                double[] thresholds = GetReferenceThresholds();
                currentSeriesInfo.ThresholdP90 = thresholds[0];
                currentSeriesInfo.ThresholdP95 = thresholds[1];
                currentSeriesInfo.ThresholdP99 = thresholds[2];

                allDitherData.AddRange(currentDitherSeries);
                seriesInfos[currentSeriesInfo.DitherSeriesId] = currentSeriesInfo;
                Logger.Info($"PHD2Client: Dither series #{currentSeriesInfo.DitherSeriesId} finalized " +
                    $"({currentDitherSeries.Count} points, settle {(currentSeriesInfo.SettleReceived ? $"{currentSeriesInfo.MeasuredSettleDuration:F1}s" : "not received")})");
            }

            currentDitherSeries = new List<DitherDataPoint>();
            currentSeriesInfo = null;
            return hadData;
        }

        /// <summary>
        /// Run the time-to-stable analysis over all accumulated dither series and fire
        /// an updated recommendation. Called whenever a collection window closes.
        /// </summary>
        private void RunAnalysisAndRecommendation() {
            try {
                List<DitherDataPoint> dataSnapshot;
                Dictionary<int, DitherSeriesInfo> infoSnapshot;
                lock (ditherDataLock) {
                    dataSnapshot = new List<DitherDataPoint>(allDitherData);
                    infoSnapshot = new Dictionary<int, DitherSeriesInfo>(seriesInfos);
                }
                // Capture together with the snapshot so the export file is labeled
                // with the profile the data actually belongs to
                string profileName = CurrentProfileName;

                // Series id 0 marks orphaned points from a collection window that spanned
                // a profile switch (data written before this safeguard existed); they have
                // no dither event and would skew the analysis
                dataSnapshot.RemoveAll(p => p.DitherSeriesId <= 0);

                if (dataSnapshot.Count == 0) {
                    Logger.Info("PHD2Client: RunAnalysisAndRecommendation - no data yet");
                    return;
                }

                double[] currentThresholds = GetReferenceThresholds();

                // Session RMS values for the info display and the analysis file
                double runningRMS;
                double rmsStdDev;
                lock (sessionLock) {
                    (runningRMS, rmsStdDev) = ComputeSessionStatsLocked();
                }

                var analyses = DitherAnalysis.AnalyzeSeries(dataSnapshot, infoSnapshot, currentThresholds);

                WriteDitherAnalysisFile(dataSnapshot, currentThresholds, runningRMS, rmsStdDev, profileName);
                WriteSettleAnalysisFile(analyses, currentThresholds, profileName);

                var recommendation = DitherAnalysis.CalculateRecommendation(analyses, currentThresholds, runningRMS, rmsStdDev, currentGuideExposure, GuiderPixelScaleArcsec);
                if (recommendation != null) {
                    Logger.Info($"PHD2Client: Dither recommendation - Events: {recommendation.DitherEventsAnalyzed} ({recommendation.ExcludedSeries} excluded), " +
                        $"Tolerance: {recommendation.SettlePixelTolerance_Quality:F2}/{recommendation.SettlePixelTolerance_Balanced:F2}/{recommendation.SettlePixelTolerance_Performance:F2} px, " +
                        $"ExpectedSettle: {recommendation.ExpectedSettleDuration_Quality:F1}/{recommendation.ExpectedSettleDuration_Balanced:F1}/{recommendation.ExpectedSettleDuration_Performance:F1} s, " +
                        $"Timeout: {recommendation.SettleTimeout_Quality:F0}/{recommendation.SettleTimeout_Balanced:F0}/{recommendation.SettleTimeout_Performance:F0} s, " +
                        $"MinSettle: {recommendation.MinSettleTime_Balanced:F1} s");
                    DitherRecommendationUpdated?.Invoke(this, recommendation);
                } else if (analyses.Count == 0) {
                    Logger.Info("PHD2Client: No dither series in data, skipping recommendation");
                } else {
                    Logger.Info("PHD2Client: No reference distribution available yet, skipping recommendation");
                }

                Logger.Info($"PHD2Client: RunAnalysisAndRecommendation completed - {dataSnapshot.Count} points, {analyses.Count} series");

            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error in RunAnalysisAndRecommendation: {ex.Message}");
            }
        }

        /// <summary>
        /// Write dither analysis file with all collected data
        /// Format: Header with session RMS and the reference thresholds, then data lines
        /// with dx, dy, PairRMS and Analysis_Value_PXX = Threshold_PXX - PairRMS
        /// </summary>
        private void WriteDitherAnalysisFile(List<DitherDataPoint> data, double[] thresholds, double runningRMS, double rmsStdDev, string profileName) {
            try {
                if (data.Count == 0) {
                    Logger.Info("PHD2Client: No dither data to write");
                    return;
                }

                string filePath = GetDiagnosticFilePath(profileName, "dither_analysis");

                // Write file (overwrite)
                using (StreamWriter writer = new StreamWriter(filePath, append: false)) {
                    // Write header with RMS values for verification
                    writer.WriteLine($"# Running_RMS: {runningRMS:F4}");
                    writer.WriteLine($"# RMS_StdDev: {rmsStdDev:F4}");

                    for (int p = 0; p < DitherAnalysis.PROFILE_COUNT; p++) {
                        writer.WriteLine($"# Threshold_{DitherAnalysis.PROFILE_LABELS[p]}: {thresholds[p]:F4} ({DitherAnalysis.PROFILE_QUANTILES[p]:P0} quantile of the stable-guiding reference window)");
                    }

                    writer.WriteLine("# Analysis_Value_PXX = Threshold_PXX - PairRMS");
                    writer.WriteLine();

                    // Build header with all analysis value columns
                    string header = "DitherSeries,dx,dy,PairRMS";
                    for (int p = 0; p < DitherAnalysis.PROFILE_COUNT; p++) {
                        header += $",Analysis_Value_{DitherAnalysis.PROFILE_LABELS[p]}";
                    }
                    header += ",Exposure";
                    writer.WriteLine(header);

                    // Write all data points with analysis values for each profile
                    foreach (var point in data) {
                        string line = $"{point.DitherSeriesId},{point.DX:F4},{point.DY:F4},{point.PairRMS:F4}";

                        for (int p = 0; p < DitherAnalysis.PROFILE_COUNT; p++) {
                            line += $",{(thresholds[p] - point.PairRMS):F4}";
                        }

                        line += $",{point.Exposure:F3}";
                        writer.WriteLine(line);
                    }
                }

                Logger.Info($"PHD2Client: Dither analysis file written with {data.Count} data points: {Path.GetFileName(filePath)}");

            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error writing dither analysis file: {ex.Message}");
            }
        }

        /// <summary>
        /// Write the per-series settle analysis (replaces the positive-periods file of
        /// versions before 1.6): one line per dither series with the settle outcome and
        /// the time-to-stable per profile ("-" = never stabilized within the window)
        /// </summary>
        private void WriteSettleAnalysisFile(List<DitherAnalysis.SeriesSettleAnalysis> analyses, double[] currentThresholds, string profileName) {
            try {
                string filePath = GetDiagnosticFilePath(profileName, "settle_analysis");

                using (StreamWriter writer = new StreamWriter(filePath, append: false)) {
                    for (int p = 0; p < DitherAnalysis.PROFILE_COUNT; p++) {
                        writer.WriteLine($"# Current_Threshold_{DitherAnalysis.PROFILE_LABELS[p]}: {currentThresholds[p]:F4}");
                    }
                    writer.WriteLine($"# TTS_PXX = time-to-stable in seconds: from the dither event until {DitherAnalysis.STABLE_CONSECUTIVE_POINTS} consecutive points stay below the threshold");
                    writer.WriteLine("# Thr_PXX = threshold the series was analyzed with (stored at collection time; current thresholds for legacy series)");
                    writer.WriteLine("# Excluded series (failed settle or star lost) are listed but not used for recommendations");
                    writer.WriteLine();

                    string header = "DitherSeries,DitherTime,Excluded,SettleFailed,StarLost,MeasuredSettle_s";
                    for (int p = 0; p < DitherAnalysis.PROFILE_COUNT; p++) {
                        header += $",Thr_{DitherAnalysis.PROFILE_LABELS[p]},TTS_{DitherAnalysis.PROFILE_LABELS[p]}";
                    }
                    writer.WriteLine(header);

                    foreach (var a in analyses.OrderBy(x => x.DitherSeriesId)) {
                        string line = $"{a.DitherSeriesId},{a.Info.DitherEventTime:yyyy-MM-dd HH:mm:ss.fff},{a.Excluded},{a.Info.SettleFailed},{a.Info.StarLost},{a.Info.MeasuredSettleDuration:F1}";
                        for (int p = 0; p < DitherAnalysis.PROFILE_COUNT; p++) {
                            string tts = a.TimeToStable[p].HasValue ? a.TimeToStable[p].Value.ToString("F1") : "-";
                            line += $",{a.Thresholds[p]:F4},{tts}";
                        }
                        writer.WriteLine(line);
                    }

                    writer.WriteLine();
                    for (int p = 0; p < DitherAnalysis.PROFILE_COUNT; p++) {
                        var delays = analyses
                            .Where(a => !a.Excluded && a.TimeToStable[p].HasValue)
                            .Select(a => a.TimeToStable[p].Value)
                            .ToList();
                        int censored = analyses.Count(a => !a.Excluded && a.Thresholds[p] > 0 && !a.TimeToStable[p].HasValue);
                        if (delays.Count > 0) {
                            writer.WriteLine($"# {DitherAnalysis.PROFILE_LABELS[p]}: used={delays.Count}, not_stabilized={censored}, " +
                                $"median={DitherStatistics.CalculateMedian(delays):F1}s, p95={DitherStatistics.CalculateQuantile(delays, 0.95):F1}s");
                        } else {
                            writer.WriteLine($"# {DitherAnalysis.PROFILE_LABELS[p]}: used=0, not_stabilized={censored}");
                        }
                    }
                }

                Logger.Info($"PHD2Client: Settle analysis file written with {analyses.Count} series: {Path.GetFileName(filePath)}");

            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error writing settle analysis file: {ex.Message}");
            }
        }

        /// <summary>
        /// Diagnostic file path in %LocalAppData%\NINA\DitherStatistics, one file per
        /// guiding session AND statistics profile (directory created if missing)
        /// </summary>
        private string GetDiagnosticFilePath(string profileName, string suffix) {
            string dirPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA",
                "DitherStatistics"
            );
            if (!Directory.Exists(dirPath)) {
                Directory.CreateDirectory(dirPath);
            }

            DateTime start;
            lock (sessionLock) {
                start = sessionStartTime;
            }
            string sessionTimestamp = start.ToString("yyyyMMdd_HHmmss");
            return Path.Combine(dirPath, $"{sessionTimestamp}_{SanitizeForFileName(profileName)}_{suffix}.txt");
        }

        /// <summary>
        /// Snapshot of the dither optimizer analysis state for multi-session persistence.
        /// Includes points from the still-running collection window so nothing is lost
        /// when NINA is closed before the window completes.
        /// </summary>
        public DitherAnalysisSnapshot GetDitherAnalysisSnapshot() {
            var snapshot = new DitherAnalysisSnapshot();
            lock (ditherDataLock) {
                snapshot.DitherData.AddRange(allDitherData);
                snapshot.DitherData.AddRange(currentDitherSeries);
                snapshot.SeriesInfos.AddRange(seriesInfos.Values);
                if (isCollectingDitherData && currentSeriesInfo != null && currentDitherSeries.Count > 0) {
                    // In-progress series: thresholds not captured yet (stay 0); the
                    // analysis falls back to the reference window of the next session
                    snapshot.SeriesInfos.Add(currentSeriesInfo);
                }
                snapshot.DitherSeriesCounter = ditherSeriesCounter;
            }
            return snapshot;
        }

        /// <summary>
        /// Restore the dither optimizer analysis state from a previous session.
        /// The series counter resumes above the highest restored id so new dither
        /// series accumulate on top without id collisions.
        /// </summary>
        public void RestoreDitherAnalysisData(DitherAnalysisSnapshot snapshot) {
            try {
                if (snapshot?.DitherData == null || snapshot.DitherData.Count == 0) return;

                lock (ditherDataLock) {
                    allDitherData.Clear();
                    allDitherData.AddRange(snapshot.DitherData);
                    seriesInfos.Clear();
                    foreach (var info in snapshot.SeriesInfos ?? new List<DitherSeriesInfo>()) {
                        if (info != null) {
                            seriesInfos[info.DitherSeriesId] = info;
                        }
                    }
                    ditherSeriesCounter = Math.Max(snapshot.DitherSeriesCounter, snapshot.DitherData.Max(p => p.DitherSeriesId));
                }

                int totalSeries = snapshot.DitherData.Select(p => p.DitherSeriesId).Distinct().Count();
                Logger.Info($"PHD2Client: Restored {snapshot.DitherData.Count} optimizer data points ({totalSeries} dither series, {snapshot.SeriesInfos?.Count ?? 0} with metadata) from previous session");
            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error restoring dither analysis data: {ex.Message}");
            }
        }

        /// <summary>
        /// Clear all dither optimizer analysis data (Clear Data button, profile switch)
        /// </summary>
        public void ClearDitherAnalysisData() {
            try {
                lock (ditherDataLock) {
                    // Stop a running collection window - otherwise guide steps arriving after
                    // the clear (e.g. on a profile switch mid-window) would be collected as
                    // an orphaned series in the new context
                    isCollectingDitherData = false;
                    postSettleStepsRemaining = -1;
                    if (ditherCollectionTimer != null) {
                        ditherCollectionTimer.Stop();
                        ditherCollectionTimer.Dispose();
                        ditherCollectionTimer = null;
                    }

                    allDitherData.Clear();
                    seriesInfos.Clear();
                    currentDitherSeries.Clear();
                    currentSeriesInfo = null;
                    ditherSeriesCounter = 0;
                }
                Logger.Info("PHD2Client: Dither analysis data cleared");
            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error clearing dither analysis data: {ex.Message}");
            }
        }

        /// <summary>
        /// Make a profile name safe for use in the diagnostic export file names
        /// </summary>
        private static string SanitizeForFileName(string name) {
            if (string.IsNullOrWhiteSpace(name)) return "Default";
            foreach (var c in Path.GetInvalidFileNameChars()) {
                name = name.Replace(c, '_');
            }
            return name;
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
            lock (ditherDataLock) {
                if (ditherCollectionTimer != null) {
                    ditherCollectionTimer.Stop();
                    ditherCollectionTimer.Dispose();
                    ditherCollectionTimer = null;
                }
            }
            Disconnect();
            cancellationTokenSource?.Dispose();
            reader?.Dispose();
            writer?.Dispose();
            client?.Dispose();
        }
    }
}
