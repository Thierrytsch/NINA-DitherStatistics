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
        // RMS Standard Deviation Multipliers for analysis thresholds
        private static readonly double[] RMS_MULTIPLIERS = { 1.5, 2.0, 3.0 };
        private const double DEFAULT_MULTIPLIER = 2.0;  // Used for compatibility
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

        // Guiding session tracking
        private DateTime sessionStartTime;
        private readonly List<double> sessionDX = new List<double>();  // RA values (dx)
        private readonly List<double> sessionDY = new List<double>();  // DEC values (dy)
        private readonly List<double> sessionRMS = new List<double>(); // RMS values for stddev calculation
        private double currentGuideExposure = 0;  // Current guide exposure time in seconds
        private DateTime lastGuideStepTime = DateTime.MinValue;  // For calculating exposure from timing

        // Dither analysis tracking
        private class DitherDataPoint {
            public int DitherSeriesId { get; set; }  // Identifies which dither event this point belongs to
            public double DX { get; set; }
            public double DY { get; set; }
            public double PairRMS { get; set; }
            public double Exposure { get; set; }  // Guide exposure time in seconds
            public DateTime Timestamp { get; set; }
        }

        // Positive period tracking (bounded by negative values)
        private class PositivePeriod {
            public int DitherSeriesId { get; set; }
            public int StartIndex { get; set; }
            public int EndIndex { get; set; }
            public int NumPoints { get; set; }
            public double Duration { get; set; }
            public DateTime StartTimestamp { get; set; }
            public DateTime EndTimestamp { get; set; }
        }

        private readonly List<DitherDataPoint> allDitherData = new List<DitherDataPoint>();
        private List<DitherDataPoint> currentDitherSeries = new List<DitherDataPoint>();
        private DateTime ditherStartTime;
        private System.Timers.Timer ditherCollectionTimer;
        private bool isCollectingDitherData = false;
        private int ditherSeriesCounter = 0;  // Counter to identify dither series

        public event EventHandler<PHD2GuidingDitheredEventArgs> GuidingDithered;
        public event EventHandler<PHD2SettleDoneEventArgs> SettleDone;
        public event EventHandler<PHD2GuideStepEventArgs> GuideStep;
        public event EventHandler<string> ConnectionStatusChanged;

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

                // Query initial exposure time
                _ = Task.Run(async () => {
                    await Task.Delay(1000); // Wait for PHD2 to be ready
                    await QueryExposureTime();
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
                sessionDX.Clear();
                sessionDY.Clear();
                sessionRMS.Clear();

                // Stop and cleanup dither analysis timer
                if (ditherCollectionTimer != null) {
                    ditherCollectionTimer.Stop();
                    ditherCollectionTimer.Dispose();
                    ditherCollectionTimer = null;
                }
                isCollectingDitherData = false;
                currentDitherSeries.Clear();

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

                        // Important events to log
                        case "Paused":
                        case "Resumed":
                        case "StarLost":
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

                // Start dither data collection for 30 seconds
                StartDitherDataCollection();

            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error handling GuidingDithered: {ex.Message}");
            }
        }

        private void HandleSettleDone(JsonElement root) {
            try {
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
                sessionStartTime = DateTime.Now;
                sessionDX.Clear();
                sessionDY.Clear();
                sessionRMS.Clear();

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

                if (exposure == 0 && lastGuideStepTime != DateTime.MinValue) {
                    // Calculate from time between guide steps
                    exposure = (currentTime - lastGuideStepTime).TotalSeconds;

                    // Update currentGuideExposure with calculated value (log only first time)
                    if (currentGuideExposure == 0 && sessionDX.Count == 0) {
                        currentGuideExposure = exposure;
                        Logger.Info($"PHD2Client: Exposure time calculated from guide step timing: {exposure:F3} seconds");
                    } else if (currentGuideExposure == 0) {
                        currentGuideExposure = exposure;
                    }
                } else if (lastGuideStepTime == DateTime.MinValue) {
                    // First guide step - skip it since we have no timing reference yet
                    lastGuideStepTime = currentTime;
                    Logger.Debug("PHD2Client: Skipping first GuideStep (no timing reference)");
                    return;
                }

                lastGuideStepTime = currentTime;

                // During dithering/settling: mark RMS columns as NaN
                if (isDithering) {
                    var ditherArgs = new PHD2GuideStepEventArgs {
                        DX = dx,
                        DY = dy,
                        RunningRMS = double.NaN,
                        RMSStdDev = double.NaN,
                        Exposure = exposure,
                        Timestamp = DateTime.Now
                    };

                    GuideStep?.Invoke(this, ditherArgs);

                    // Collect dither data if within collection period
                    if (isCollectingDitherData) {
                        var dataPoint = new DitherDataPoint {
                            DitherSeriesId = ditherSeriesCounter,
                            DX = dx,
                            DY = dy,
                            PairRMS = Math.Sqrt(dx * dx + dy * dy),
                            Exposure = exposure,
                            Timestamp = DateTime.Now
                        };
                        currentDitherSeries.Add(dataPoint);
                    }

                    return;
                }

                // Normal operation: Add dx and dy to session tracking for RMS calculation (PHD2 method)
                sessionDX.Add(dx);
                sessionDY.Add(dy);

                // Calculate running RMS using PHD2's method:
                // 1. Calculate standard deviation for RA (dx) and DEC (dy) separately
                // 2. Combine: total_rms = sqrt(ra_stddev² + dec_stddev²)
                double runningRMS = 0;
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
                } else {
                    // For first point, use sqrt(dx² + dy²)
                    runningRMS = Math.Sqrt(dx * dx + dy * dy);
                }

                // Calculate point RMS for this single guide step (not cumulative)
                double pointRMS = Math.Sqrt(dx * dx + dy * dy);

                // Add point RMS to session tracking for RMS stddev calculation
                sessionRMS.Add(pointRMS);

                // Calculate RMS standard deviation over all point RMS values
                double rmsStdDev = 0;
                if (sessionRMS.Count > 1) {
                    double meanRMS = sessionRMS.Average();
                    double sumSquaredDeviationsRMS = sessionRMS.Sum(r => Math.Pow(r - meanRMS, 2));
                    rmsStdDev = Math.Sqrt(sumSquaredDeviationsRMS / (sessionRMS.Count - 1));
                }

                // Create event args
                var args = new PHD2GuideStepEventArgs {
                    DX = dx,
                    DY = dy,
                    RunningRMS = runningRMS,
                    RMSStdDev = rmsStdDev,
                    Exposure = exposure,
                    Timestamp = DateTime.Now
                };

                // Raise event (for UI updates if needed)
                GuideStep?.Invoke(this, args);

                // Collect dither data if within collection period (even after settling)
                if (isCollectingDitherData) {
                    var dataPoint = new DitherDataPoint {
                        DitherSeriesId = ditherSeriesCounter,
                        DX = dx,
                        DY = dy,
                        PairRMS = Math.Sqrt(dx * dx + dy * dy),
                        Exposure = exposure,
                        Timestamp = DateTime.Now
                    };
                    currentDitherSeries.Add(dataPoint);
                }

            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error handling GuideStep: {ex.Message}");
            }
        }

        /// <summary>
        /// Start collecting dither data for 30 seconds
        /// </summary>
        private void StartDitherDataCollection() {
            try {
                // Stop existing timer if running
                if (ditherCollectionTimer != null) {
                    ditherCollectionTimer.Stop();
                    ditherCollectionTimer.Dispose();
                }

                // Add current series to all data (if not empty)
                if (currentDitherSeries.Count > 0) {
                    allDitherData.AddRange(currentDitherSeries);
                }

                // Start new series
                currentDitherSeries = new List<DitherDataPoint>();
                ditherStartTime = DateTime.Now;
                isCollectingDitherData = true;
                ditherSeriesCounter++;  // Increment for new dither series

                // Create 30-second timer
                ditherCollectionTimer = new System.Timers.Timer(30000); // 30 seconds
                ditherCollectionTimer.Elapsed += OnDitherCollectionComplete;
                ditherCollectionTimer.AutoReset = false;
                ditherCollectionTimer.Start();

                Logger.Info("PHD2Client: Started dither data collection for 30 seconds");

            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error starting dither data collection: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when 30-second dither collection period is complete
        /// </summary>
        private void OnDitherCollectionComplete(object sender, System.Timers.ElapsedEventArgs e) {
            try {
                isCollectingDitherData = false;

                // Add current series to all data
                if (currentDitherSeries.Count > 0) {
                    allDitherData.AddRange(currentDitherSeries);
                    Logger.Info($"PHD2Client: Dither data collection complete. Collected {currentDitherSeries.Count} points in this series.");
                }

                // Get current running RMS and RMS StdDev for analysis
                double currentRunningRMS = 0;
                double currentRMSStdDev = 0;

                if (sessionDX.Count > 1) {
                    double meanDX = sessionDX.Average();
                    double sumSquaredDeviationsDX = sessionDX.Sum(d => Math.Pow(d - meanDX, 2));
                    double raStdDev = Math.Sqrt(sumSquaredDeviationsDX / (sessionDX.Count - 1));

                    double meanDY = sessionDY.Average();
                    double sumSquaredDeviationsDY = sessionDY.Sum(d => Math.Pow(d - meanDY, 2));
                    double decStdDev = Math.Sqrt(sumSquaredDeviationsDY / (sessionDY.Count - 1));

                    currentRunningRMS = Math.Sqrt(raStdDev * raStdDev + decStdDev * decStdDev);
                }

                if (sessionRMS.Count > 1) {
                    double meanRMS = sessionRMS.Average();
                    double sumSquaredDeviationsRMS = sessionRMS.Sum(r => Math.Pow(r - meanRMS, 2));
                    currentRMSStdDev = Math.Sqrt(sumSquaredDeviationsRMS / (sessionRMS.Count - 1));
                }

                // Recalculate analysis for ALL collected data and write to file
                WriteDitherAnalysisFile(currentRunningRMS, currentRMSStdDev);

                // Analyze positive periods bounded by negative values for each dither series
                AnalyzePositivePeriods(currentRunningRMS, currentRMSStdDev);

                // Clear current series
                currentDitherSeries.Clear();

            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error in dither collection complete: {ex.Message}");
            }
        }

        /// <summary>
        /// Write dither analysis file with all collected data
        /// Format: Header with running_RMS and RMS_StdDev, then data lines with dx, dy, PairRMS, and multiple AnalysisValues
        /// AnalysisValue_X = running_RMS + X*RMS_StdDev - PairRMS (for each multiplier)
        /// </summary>
        private void WriteDitherAnalysisFile(double runningRMS, double rmsStdDev) {
            try {
                if (allDitherData.Count == 0) {
                    Logger.Info("PHD2Client: No dither data to write");
                    return;
                }

                // Create directory path
                string dirPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NINA",
                    "DitherStatistics"
                );

                // Ensure directory exists
                if (!Directory.Exists(dirPath)) {
                    Directory.CreateDirectory(dirPath);
                }

                // Use session-specific filename for dither analysis
                string sessionTimestamp = sessionStartTime.ToString("yyyyMMdd_HHmmss");
                string fileName = $"{sessionTimestamp}_dither_analysis.txt";
                string filePath = Path.Combine(dirPath, fileName);

                // Write file (overwrite)
                using (StreamWriter writer = new StreamWriter(filePath, append: false)) {
                    // Write header with RMS values for verification
                    writer.WriteLine($"# Running_RMS: {runningRMS:F4}");
                    writer.WriteLine($"# RMS_StdDev: {rmsStdDev:F4}");

                    // Write thresholds for each multiplier
                    foreach (double mult in RMS_MULTIPLIERS) {
                        writer.WriteLine($"# Threshold_{mult:F1}: {(runningRMS + mult * rmsStdDev):F4} (Running_RMS + {mult:F1}*RMS_StdDev)");
                    }

                    writer.WriteLine("# Analysis_Value_X = Running_RMS + X*RMS_StdDev - PairRMS");
                    writer.WriteLine();

                    // Build header with all analysis value columns
                    string header = "DitherSeries,dx,dy,PairRMS";
                    foreach (double mult in RMS_MULTIPLIERS) {
                        header += $",Analysis_Value_{mult:F1}";
                    }
                    header += ",Exposure";
                    writer.WriteLine(header);

                    // Write all data points with analysis values for each multiplier
                    foreach (var point in allDitherData) {
                        string line = $"{point.DitherSeriesId},{point.DX:F4},{point.DY:F4},{point.PairRMS:F4}";

                        // Add analysis value for each multiplier
                        foreach (double mult in RMS_MULTIPLIERS) {
                            double threshold = runningRMS + mult * rmsStdDev;
                            double analysisValue = threshold - point.PairRMS;
                            line += $",{analysisValue:F4}";
                        }

                        line += $",{point.Exposure:F3}";
                        writer.WriteLine(line);
                    }
                }

                Logger.Info($"PHD2Client: Dither analysis file written with {allDitherData.Count} data points: {fileName}");

            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error writing dither analysis file: {ex.Message}");
            }
        }

        /// <summary>
        /// Analyze periods where Analysis_Value is positive (>= 0) for each dither series
        /// Analyzes for multiple RMS multipliers (1.5, 2.0, 3.0)
        /// Finds sequences of positive values that are bounded by negative values before AND after
        /// If series ends with positive values, those are NOT counted (no negative value after)
        /// For each dither series, only the longest valid period is kept
        /// </summary>
        private void AnalyzePositivePeriods(double runningRMS, double rmsStdDev) {
            try {
                if (allDitherData.Count == 0) {
                    Logger.Info("PHD2Client: No dither data to analyze for positive periods");
                    return;
                }

                // Group data by DitherSeriesId once
                var seriesGroups = allDitherData.GroupBy(p => p.DitherSeriesId).OrderBy(g => g.Key);

                // Store positive periods for each multiplier
                var allPositivePeriods = new Dictionary<double, List<PositivePeriod>>();

                // Analyze for each multiplier
                foreach (double multiplier in RMS_MULTIPLIERS) {
                    double threshold = runningRMS + multiplier * rmsStdDev;
                    var positivePeriods = new List<PositivePeriod>();
                    var seriesWithPeriods = new HashSet<int>();  // Track which series have valid periods
                    var periodsPerSeries = new Dictionary<int, List<PositivePeriod>>();  // Collect all periods per series

                    foreach (var series in seriesGroups) {
                        var points = series.OrderBy(p => p.Timestamp).ToList();
                        var seriesPeriods = new List<PositivePeriod>();

                        int i = 0;
                        while (i < points.Count) {
                            double analysisValue = threshold - points[i].PairRMS;

                            // Check if current point is positive (>= 0)
                            if (analysisValue >= 0) {
                                int startIndex = i;
                                double totalDuration = 0;

                                // Find end of positive sequence
                                while (i < points.Count && (threshold - points[i].PairRMS) >= 0) {
                                    totalDuration += points[i].Exposure;
                                    i++;
                                }

                                int endIndex = i - 1;

                                // MUST be bounded by negative values before AND after
                                bool validBefore = (startIndex > 0) && ((threshold - points[startIndex - 1].PairRMS) < 0);
                                bool validAfter = (i < points.Count) && ((threshold - points[i].PairRMS) < 0);

                                if (validBefore && validAfter) {
                                    seriesPeriods.Add(new PositivePeriod {
                                        DitherSeriesId = series.Key,
                                        StartIndex = startIndex,
                                        EndIndex = endIndex,
                                        NumPoints = endIndex - startIndex + 1,
                                        Duration = totalDuration,
                                        StartTimestamp = points[startIndex].Timestamp,
                                        EndTimestamp = points[endIndex].Timestamp
                                    });
                                }
                            } else {
                                i++;
                            }
                        }

                        // Store all periods found for this series
                        if (seriesPeriods.Count > 0) {
                            periodsPerSeries[series.Key] = seriesPeriods;
                            seriesWithPeriods.Add(series.Key);
                        }
                    }

                    // For each series with valid periods, keep only the longest one
                    foreach (var kvp in periodsPerSeries) {
                        var longestPeriod = kvp.Value.OrderByDescending(p => p.Duration).First();
                        positivePeriods.Add(longestPeriod);
                    }

                    // For dither series with no valid positive periods, create a dummy entry with first positive value
                    foreach (var series in seriesGroups) {
                        if (!seriesWithPeriods.Contains(series.Key)) {
                            var points = series.OrderBy(p => p.Timestamp).ToList();

                            // Find first positive analysis value
                            for (int i = 0; i < points.Count; i++) {
                                double analysisValue = threshold - points[i].PairRMS;
                                if (analysisValue >= 0) {
                                    // Create dummy period with StartIndex = EndIndex
                                    positivePeriods.Add(new PositivePeriod {
                                        DitherSeriesId = series.Key,
                                        StartIndex = i,
                                        EndIndex = i,
                                        NumPoints = 1,
                                        Duration = 1.0,  // 1 second
                                        StartTimestamp = points[i].Timestamp,
                                        EndTimestamp = points[i].Timestamp.AddSeconds(1)
                                    });
                                    break;  // Only add one dummy entry per series
                                }
                            }
                        }
                    }

                    allPositivePeriods[multiplier] = positivePeriods;
                }

                // Write to file
                WritePositivePeriodsFile(allPositivePeriods, runningRMS, rmsStdDev);

            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error analyzing positive periods: {ex.Message}");
            }
        }

        /// <summary>
        /// Write positive periods analysis to file for all multipliers
        /// </summary>
        private void WritePositivePeriodsFile(Dictionary<double, List<PositivePeriod>> allPositivePeriods, double runningRMS, double rmsStdDev) {
            try {
                // Create directory path
                string dirPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NINA",
                    "DitherStatistics"
                );

                // Ensure directory exists
                if (!Directory.Exists(dirPath)) {
                    Directory.CreateDirectory(dirPath);
                }

                // Use session-specific filename for positive periods analysis
                string sessionTimestamp = sessionStartTime.ToString("yyyyMMdd_HHmmss");
                string fileName = $"{sessionTimestamp}_positive_periods.txt";
                string filePath = Path.Combine(dirPath, fileName);

                // Write file (overwrite)
                using (StreamWriter writer = new StreamWriter(filePath, append: false)) {
                    // Write header with RMS values
                    writer.WriteLine($"# Running_RMS: {runningRMS:F4}");
                    writer.WriteLine($"# RMS_StdDev: {rmsStdDev:F4}");
                    writer.WriteLine("# Positive periods are sequences where Analysis_Value >= 0, bounded by negative values before AND after");
                    writer.WriteLine("# If series ends with positive values, those are NOT counted");
                    writer.WriteLine("# For each dither series, only the longest valid period is shown");
                    writer.WriteLine();

                    // Write sections for each multiplier
                    foreach (double multiplier in RMS_MULTIPLIERS) {
                        double threshold = runningRMS + multiplier * rmsStdDev;
                        var positivePeriods = allPositivePeriods[multiplier];

                        writer.WriteLine($"### Multiplier {multiplier:F1} (Threshold: {threshold:F4}) ###");
                        writer.WriteLine("DitherSeries,StartIndex,EndIndex,NumPoints,Duration_seconds,StartTime,EndTime");

                        // Write all positive periods for this multiplier (sorted by DitherSeriesId)
                        foreach (var period in positivePeriods.OrderBy(p => p.DitherSeriesId)) {
                            writer.WriteLine($"{period.DitherSeriesId},{period.StartIndex},{period.EndIndex},{period.NumPoints},{period.Duration:F3},{period.StartTimestamp:yyyy-MM-dd HH:mm:ss.fff},{period.EndTimestamp:yyyy-MM-dd HH:mm:ss.fff}");
                        }

                        // Write summary for this multiplier
                        writer.WriteLine($"# Total positive periods found: {positivePeriods.Count}");
                        if (positivePeriods.Count > 0) {
                            writer.WriteLine($"# Total duration of positive periods: {positivePeriods.Sum(p => p.Duration):F3} seconds");
                            writer.WriteLine($"# Average duration per period: {positivePeriods.Average(p => p.Duration):F3} seconds");
                        }
                        writer.WriteLine();
                    }
                }

                int totalPeriods = allPositivePeriods.Values.Sum(list => list.Count);
                Logger.Info($"PHD2Client: Positive periods analysis written with {totalPeriods} total periods across {RMS_MULTIPLIERS.Length} multipliers: {fileName}");

            } catch (Exception ex) {
                Logger.Error($"PHD2Client: Error writing positive periods file: {ex.Message}");
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

        public void Dispose() {
            if (ditherCollectionTimer != null) {
                ditherCollectionTimer.Stop();
                ditherCollectionTimer.Dispose();
            }
            Disconnect();
            cancellationTokenSource?.Dispose();
            reader?.Dispose();
            writer?.Dispose();
            client?.Dispose();
        }
    }

    /// <summary>
    /// Event args for GuidingDithered event (Dither START with pixel shift)
    /// </summary>
    public class PHD2GuidingDitheredEventArgs : EventArgs {
        public double DeltaX { get; set; }
        public double DeltaY { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Event args for SettleDone event (Dither END)
    /// </summary>
    public class PHD2SettleDoneEventArgs : EventArgs {
        public bool Success { get; set; }
        public int Status { get; set; }
        public int TotalFrames { get; set; }
        public int DroppedFrames { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Event args for GuideStep event (Guide corrections with RMS)
    /// </summary>
    public class PHD2GuideStepEventArgs : EventArgs {
        // Camera coordinates (pixels on guide chip)
        public double DX { get; set; }         // X offset from lock position in camera coordinates (pixels)
        public double DY { get; set; }         // Y offset from lock position in camera coordinates (pixels)

        // Running RMS using PHD2's method
        public double RunningRMS { get; set; } // Total RMS (pixels):
                                               // ra_stddev = sqrt(Σ(dx_i - mean_dx)² / (n-1))
                                               // dec_stddev = sqrt(Σ(dy_i - mean_dy)² / (n-1))
                                               // total_rms = sqrt(ra_stddev² + dec_stddev²)

        // RMS Standard Deviation
        public double RMSStdDev { get; set; }  // Standard deviation of RMS values:
                                               // sqrt(Σ(rms_i - mean_rms)² / (n-1))

        // Guide exposure time
        public double Exposure { get; set; }   // Guide exposure time in seconds

        public DateTime Timestamp { get; set; }
    }
}
