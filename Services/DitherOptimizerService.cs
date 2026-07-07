using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// Dither Settings Optimizer state machine, extracted from PHD2Client: consumes the
    /// client's parsed events through the Handle* methods (wired by the VM), maintains
    /// the rolling reference window of stable-guiding distances, collects post-dither
    /// guide steps per series, runs the analysis (DitherAnalysis) and writes the
    /// per-session diagnostic files.
    ///
    /// Lock discipline (unchanged from PHD2Client before the split):
    /// - ditherDataLock guards allDitherData, seriesInfos, currentDitherSeries,
    ///   currentSeriesInfo, ditherCollectionTimer and the collection state flags
    /// - referenceLock guards referenceWindow
    /// - sessionLock guards sessionStartTime and the session statistic lists
    /// The only permitted nesting is ditherDataLock → referenceLock (via
    /// FinalizeCurrentSeriesLocked → GetReferenceThresholds); never take the locks
    /// in any other combination.
    /// </summary>
    public class DitherOptimizerService : IDisposable {
        // Rolling reference window of stable-guiding distances used for the quantiles;
        // bounded by count and age so the thresholds track current seeing conditions
        private const int REFERENCE_MAX_POINTS = 400;
        private static readonly TimeSpan REFERENCE_MAX_AGE = TimeSpan.FromMinutes(15);

        // Collection window per dither: until SettleDone + POST_SETTLE_STEPS guide steps,
        // with a hard cap in case SettleDone never arrives
        private const int POST_SETTLE_STEPS = 10;
        private const int COLLECTION_CAP_MS = 120000;

        // Transport-level values owned by the PHD2 client, read lazily at analysis time
        private readonly Func<double> getGuideExposure;
        private readonly Func<double?> getGuiderPixelScale;
        private readonly string diagnosticsDirectory;

        // Guide steps between GuidingDithered and SettleDone are excluded from the
        // session statistics and the reference window; they only feed the running series
        private bool isDithering = false;

        // Guiding session tracking (guarded by sessionLock: written on the read thread,
        // read from analysis tasks on timer/thread-pool threads). "Session" here means the
        // whole guiding session since the last GuidingStarted/Disconnected reset (unlike the
        // 15-minute reference window) - Welford's online algorithm keeps that unbounded-count
        // semantics without retaining every point (≈43000 points per 24h at 2s exposures).
        private readonly object sessionLock = new object();
        private DateTime sessionStartTime;
        private readonly WelfordAccumulator sessionDX = new WelfordAccumulator();  // RA values (dx)
        private readonly WelfordAccumulator sessionDY = new WelfordAccumulator();  // DEC values (dy)
        private readonly WelfordAccumulator sessionRMS = new WelfordAccumulator(); // point distances for stddev calculation

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

        public event EventHandler<DitherSettingsRecommendation> DitherRecommendationUpdated;

        public DitherOptimizerService(Func<double> getGuideExposure, Func<double?> getGuiderPixelScale, string diagnosticsDirectory = null) {
            this.getGuideExposure = getGuideExposure ?? (() => 0);
            this.getGuiderPixelScale = getGuiderPixelScale ?? (() => null);
            this.diagnosticsDirectory = diagnosticsDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA",
                "DitherStatistics"
            );

            // Covers the case where PHD2 is already guiding when the plugin connects
            // (no GuidingStarted event ever arrives): without this, diagnostic files
            // would be named "00010101_000000_..." and every such session would
            // overwrite the same file.
            sessionStartTime = DateTime.Now;

            // Prune old diagnostic files to keep storage bounded
            PruneDiagnosticFiles();
        }

        /// <summary>
        /// A dither started: exclude the following guide steps from the session
        /// statistics / reference window and open a new collection window.
        /// </summary>
        public void HandleGuidingDithered(PHD2GuidingDitheredEventArgs e) {
            isDithering = true;
            StartDitherDataCollection(e.Timestamp);
        }

        /// <summary>
        /// Settling finished: resume statistics collection, record the settle outcome
        /// on the running series and arm the post-settle countdown that ends the
        /// collection window.
        /// </summary>
        public void HandleSettleDone(PHD2SettleDoneEventArgs e) {
            isDithering = false;

            lock (ditherDataLock) {
                if (isCollectingDitherData && currentSeriesInfo != null) {
                    currentSeriesInfo.SettleReceived = true;
                    currentSeriesInfo.SettleFailed = !e.Success;
                    currentSeriesInfo.MeasuredSettleDuration = Math.Max(0, (DateTime.Now - currentSeriesInfo.DitherEventTime).TotalSeconds);
                    postSettleStepsRemaining = POST_SETTLE_STEPS;
                }
            }
        }

        /// <summary>
        /// A guide step arrived: during dithering/settling it only feeds the running
        /// series; otherwise it feeds the session statistics, the reference window
        /// and (while the post-settle window is still open) the running series.
        /// </summary>
        public void HandleGuideStep(PHD2GuideStepEventArgs e) {
            try {
                double pairDistance = Math.Sqrt(e.DX * e.DX + e.DY * e.DY);

                if (isDithering) {
                    CollectDitherPoint(e.DX, e.DY, pairDistance, e.Exposure, e.Timestamp);
                    return;
                }

                // Normal operation: track session statistics and the rolling reference
                // window (the running RMS is computed lazily at analysis time)
                lock (sessionLock) {
                    sessionDX.Add(e.DX);
                    sessionDY.Add(e.DY);
                    sessionRMS.Add(pairDistance);
                }

                lock (referenceLock) {
                    referenceWindow.Add(new KeyValuePair<DateTime, double>(e.Timestamp, pairDistance));
                    TrimReferenceWindowLocked(e.Timestamp);
                }

                // Collect post-settle data while the collection window is still open
                CollectDitherPoint(e.DX, e.DY, pairDistance, e.Exposure, e.Timestamp);

            } catch (Exception ex) {
                Logger.Error($"DitherOptimizer: Error handling GuideStep: {ex.Message}");
            }
        }

        /// <summary>
        /// A lost star during the collection window invalidates the series
        /// </summary>
        public void HandleStarLost() {
            lock (ditherDataLock) {
                if (isCollectingDitherData && currentSeriesInfo != null) {
                    currentSeriesInfo.StarLost = true;
                }
            }
        }

        /// <summary>
        /// Start a new guiding session - resets session tracking
        /// </summary>
        public void HandleGuidingStarted() {
            try {
                lock (sessionLock) {
                    sessionStartTime = DateTime.Now;
                    sessionDX.Reset();
                    sessionDY.Reset();
                    sessionRMS.Reset();
                }

                Logger.Info($"DitherOptimizer: New guiding session started");

            } catch (Exception ex) {
                Logger.Error($"DitherOptimizer: Error starting new guiding session: {ex.Message}");
            }
        }

        /// <summary>
        /// Explicit disconnect from PHD2: reset session tracking and the reference
        /// window, stop and clean up the running collection window (collected points
        /// of the aborted window are discarded; accumulated series stay for analysis).
        /// </summary>
        public void HandleDisconnected() {
            isDithering = false;

            lock (sessionLock) {
                sessionDX.Reset();
                sessionDY.Reset();
                sessionRMS.Reset();
            }
            lock (referenceLock) {
                referenceWindow.Clear();
            }

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
        }

        /// <summary>
        /// Running session RMS using PHD2's method (sqrt(ra_stddev² + dec_stddev²))
        /// and the standard deviation of the point distances, both over every guide step
        /// since the last session reset (GuidingStarted/Disconnected) - not the 15-minute
        /// reference window. Caller must hold sessionLock.
        /// </summary>
        private (double runningRMS, double rmsStdDev) ComputeSessionStatsLocked() {
            double runningRMS = 0;
            double rmsStdDev = 0;

            if (sessionDX.Count > 1) {
                // Total RMS: sqrt(ra_stddev² + dec_stddev²)
                double raStdDev = sessionDX.SampleStdDev;
                double decStdDev = sessionDY.SampleStdDev;
                runningRMS = Math.Sqrt(raStdDev * raStdDev + decStdDev * decStdDev);
            }

            if (sessionRMS.Count > 1) {
                rmsStdDev = sessionRMS.SampleStdDev;
            }

            return (runningRMS, rmsStdDev);
        }

        /// <summary>
        /// Welford's online algorithm for mean/sample-variance in O(1) memory and per-update
        /// time, used for the session statistics above so an all-day session (≈43000 guide
        /// steps at 2s exposures) doesn't retain every point just to compute a standard
        /// deviation. Mathematically equivalent to the direct Σ(x-mean)² formula.
        /// </summary>
        private sealed class WelfordAccumulator {
            public int Count { get; private set; }
            private double mean;
            private double sumSquaredDeviations; // M2

            public void Add(double value) {
                Count++;
                double delta = value - mean;
                mean += delta / Count;
                sumSquaredDeviations += delta * (value - mean);
            }

            public double SampleStdDev => Count > 1 ? Math.Sqrt(sumSquaredDeviations / (Count - 1)) : 0;

            public void Reset() {
                Count = 0;
                mean = 0;
                sumSquaredDeviations = 0;
            }
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
                            Logger.Info("DitherOptimizer: Previous dither series finalized early (next dither arrived before window closed)");
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

                    Logger.Info($"DitherOptimizer: Started dither data collection (series #{ditherSeriesCounter})");
                }

                if (runAnalysis) {
                    _ = Task.Run(() => RunAnalysisAndRecommendation());
                }

            } catch (Exception ex) {
                Logger.Error($"DitherOptimizer: Error starting dither data collection: {ex.Message}");
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
                    Logger.Info("DitherOptimizer: Dither data collection ended at hard cap");
                    RunAnalysisAndRecommendation();
                }
            } catch (Exception ex) {
                Logger.Error($"DitherOptimizer: Error in collection cap handler: {ex.Message}");
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
                Logger.Info($"DitherOptimizer: Dither series #{currentSeriesInfo.DitherSeriesId} finalized " +
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
                    Logger.Info("DitherOptimizer: RunAnalysisAndRecommendation - no data yet");
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

                var recommendation = DitherAnalysis.CalculateRecommendation(analyses, currentThresholds, runningRMS, rmsStdDev, getGuideExposure(), getGuiderPixelScale());
                if (recommendation != null) {
                    Logger.Info($"DitherOptimizer: Dither recommendation - Events: {recommendation.DitherEventsAnalyzed} ({recommendation.ExcludedSeries} excluded), " +
                        $"Tolerance: {recommendation.SettlePixelTolerance_Quality:F2}/{recommendation.SettlePixelTolerance_Balanced:F2}/{recommendation.SettlePixelTolerance_Performance:F2} px, " +
                        $"ExpectedSettle: {recommendation.ExpectedSettleDuration_Quality:F1}/{recommendation.ExpectedSettleDuration_Balanced:F1}/{recommendation.ExpectedSettleDuration_Performance:F1} s, " +
                        $"Timeout: {recommendation.SettleTimeout_Quality:F0}/{recommendation.SettleTimeout_Balanced:F0}/{recommendation.SettleTimeout_Performance:F0} s, " +
                        $"MinSettle: {recommendation.MinSettleTime_Balanced:F1} s");
                    DitherRecommendationUpdated?.Invoke(this, recommendation);
                } else if (analyses.Count == 0) {
                    Logger.Info("DitherOptimizer: No dither series in data, skipping recommendation");
                } else {
                    Logger.Info("DitherOptimizer: No reference distribution available yet, skipping recommendation");
                }

                Logger.Info($"DitherOptimizer: RunAnalysisAndRecommendation completed - {dataSnapshot.Count} points, {analyses.Count} series");

            } catch (Exception ex) {
                Logger.Error($"DitherOptimizer: Error in RunAnalysisAndRecommendation: {ex.Message}");
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
                    Logger.Info("DitherOptimizer: No dither data to write");
                    return;
                }

                string filePath = GetDiagnosticFilePath(profileName, "dither_analysis");

                // Write file (overwrite)
                using (StreamWriter writer = new StreamWriter(filePath, append: false)) {
                    // Write header with RMS values for verification
                    writer.WriteLine("# Running_RMS/RMS_StdDev cover every guide step since the last session reset");
                    writer.WriteLine("# (GuidingStarted/Disconnected), not just the 15-minute reference window below.");
                    writer.WriteLine($"# Running_RMS: {runningRMS:F4} (sqrt(RA_stddev² + DEC_stddev²), PHD2's method)");
                    writer.WriteLine($"# RMS_StdDev: {rmsStdDev:F4} (stddev of the per-step pair distances)");

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

                Logger.Info($"DitherOptimizer: Dither analysis file written with {data.Count} data points: {Path.GetFileName(filePath)}");

            } catch (Exception ex) {
                Logger.Error($"DitherOptimizer: Error writing dither analysis file: {ex.Message}");
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

                Logger.Info($"DitherOptimizer: Settle analysis file written with {analyses.Count} series: {Path.GetFileName(filePath)}");

            } catch (Exception ex) {
                Logger.Error($"DitherOptimizer: Error writing settle analysis file: {ex.Message}");
            }
        }

        /// <summary>
        /// Prune old diagnostic files: keep only the newest ~30 dither_analysis and
        /// settle_analysis files, delete the rest to prevent unbounded storage growth.
        /// </summary>
        private void PruneDiagnosticFiles() {
            try {
                if (!Directory.Exists(diagnosticsDirectory)) return;

                var ditherFiles = Directory.GetFiles(diagnosticsDirectory, "*_dither_analysis.txt")
                    .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                    .ToList();

                if (ditherFiles.Count > 30) {
                    foreach (var file in ditherFiles.Skip(30)) {
                        File.Delete(file);
                    }
                }

                var settleFiles = Directory.GetFiles(diagnosticsDirectory, "*_settle_analysis.txt")
                    .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                    .ToList();

                if (settleFiles.Count > 30) {
                    foreach (var file in settleFiles.Skip(30)) {
                        File.Delete(file);
                    }
                }

                if (ditherFiles.Count > 30 || settleFiles.Count > 30) {
                    Logger.Info($"DitherOptimizer: Pruned diagnostic files (kept 30 newest dither_analysis, kept 30 newest settle_analysis)");
                }
            } catch (Exception ex) {
                Logger.Error($"DitherOptimizer: Error pruning diagnostic files: {ex.Message}");
            }
        }

        /// <summary>
        /// Diagnostic file path in the diagnostics directory (default
        /// %LocalAppData%\NINA\DitherStatistics), one file per guiding session AND
        /// statistics profile (directory created if missing)
        /// </summary>
        private string GetDiagnosticFilePath(string profileName, string suffix) {
            if (!Directory.Exists(diagnosticsDirectory)) {
                Directory.CreateDirectory(diagnosticsDirectory);
            }

            DateTime start;
            lock (sessionLock) {
                start = sessionStartTime;
            }
            string sessionTimestamp = start.ToString("yyyyMMdd_HHmmss");
            return Path.Combine(diagnosticsDirectory, $"{sessionTimestamp}_{SanitizeForFileName(profileName)}_{suffix}.txt");
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
                Logger.Info($"DitherOptimizer: Restored {snapshot.DitherData.Count} optimizer data points ({totalSeries} dither series, {snapshot.SeriesInfos?.Count ?? 0} with metadata) from previous session");
            } catch (Exception ex) {
                Logger.Error($"DitherOptimizer: Error restoring dither analysis data: {ex.Message}");
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
                Logger.Info("DitherOptimizer: Dither analysis data cleared");
            } catch (Exception ex) {
                Logger.Error($"DitherOptimizer: Error clearing dither analysis data: {ex.Message}");
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

        public void Dispose() {
            lock (ditherDataLock) {
                if (ditherCollectionTimer != null) {
                    ditherCollectionTimer.Stop();
                    ditherCollectionTimer.Dispose();
                    ditherCollectionTimer = null;
                }
            }
        }
    }
}
