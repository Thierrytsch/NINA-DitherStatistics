using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DitherStatistics.Plugin;
using Xunit;

namespace DitherStatistics.Tests {
    /// <summary>
    /// Tests for the optimizer state machine extracted from PHD2Client in stage 6.
    /// The service is driven through its Handle* methods exactly like the VM wiring
    /// does with the client events - no network involved.
    /// </summary>
    public class DitherOptimizerServiceTests : IDisposable {
        private readonly string tempDir;

        public DitherOptimizerServiceTests() {
            tempDir = Path.Combine(Path.GetTempPath(), "DitherOptimizerServiceTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose() {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }

        private DitherOptimizerService CreateService(double guideExposure = 2.0, double? pixelScale = 1.5) {
            return new DitherOptimizerService(() => guideExposure, () => pixelScale, tempDir);
        }

        private static PHD2GuideStepEventArgs Step(double dx, double dy, DateTime timestamp, double exposure = 2.0) {
            return new PHD2GuideStepEventArgs { DX = dx, DY = dy, Exposure = exposure, Timestamp = timestamp };
        }

        /// <summary>
        /// Feed enough stable guide steps to give the reference window meaningful
        /// quantile thresholds (REFERENCE_MIN_POINTS = 20). Distances ~0.5 px.
        /// </summary>
        private static void FeedReferenceWindow(DitherOptimizerService service, DateTime start, int count = 30) {
            for (int i = 0; i < count; i++) {
                service.HandleGuideStep(Step(0.3 + (i % 5) * 0.05, 0.3, start.AddSeconds(i * 2)));
            }
        }

        /// <summary>
        /// Run one complete dither cycle: dither event, settling steps, SettleDone,
        /// then the 10 post-settle steps that close the collection window.
        /// Returns the timestamp after the last step.
        /// </summary>
        private static DateTime RunDitherCycle(DitherOptimizerService service, DateTime ditherTime, bool settleSuccess = true) {
            service.HandleGuidingDithered(new PHD2GuidingDitheredEventArgs { DeltaX = 5, DeltaY = 5, Timestamp = ditherTime });

            // Settling: distances decay from far away to below the reference level
            DateTime t = ditherTime;
            double[] distances = { 4.0, 2.0, 1.0, 0.3, 0.3, 0.3 };
            foreach (var d in distances) {
                t = t.AddSeconds(2);
                service.HandleGuideStep(Step(d, 0, t));
            }

            service.HandleSettleDone(new PHD2SettleDoneEventArgs { Success = settleSuccess, Status = settleSuccess ? 0 : 1 });

            // Post-settle countdown: exactly POST_SETTLE_STEPS (10) steps end the window
            for (int i = 0; i < 10; i++) {
                t = t.AddSeconds(2);
                service.HandleGuideStep(Step(0.3, 0.3, t));
            }
            return t;
        }

        [Fact]
        public void FullDitherCycle_FinalizesSeries_CapturesThresholds_AndFiresRecommendation() {
            using var service = CreateService();
            DitherSettingsRecommendation recommendation = null;
            using var recommendationFired = new ManualResetEventSlim(false);
            service.DitherRecommendationUpdated += (s, r) => { recommendation = r; recommendationFired.Set(); };

            var start = DateTime.Now.AddMinutes(-5);
            service.HandleGuidingStarted();
            FeedReferenceWindow(service, start);
            RunDitherCycle(service, start.AddSeconds(70));

            Assert.True(recommendationFired.Wait(TimeSpan.FromSeconds(10)), "DitherRecommendationUpdated was not fired");
            Assert.NotNull(recommendation);
            Assert.Equal(1, recommendation.DitherEventsAnalyzed);
            Assert.Equal(0, recommendation.ExcludedSeries);
            Assert.True(recommendation.SettlePixelTolerance_Balanced > 0);
            Assert.Equal(1.5, recommendation.GuiderPixelScaleArcsec);
            Assert.Equal(2.0, recommendation.GuideExposure);

            var snapshot = service.GetDitherAnalysisSnapshot();
            var info = Assert.Single(snapshot.SeriesInfos);
            Assert.True(info.SettleReceived);
            Assert.False(info.SettleFailed);
            // Thresholds captured from the reference window at finalize time
            Assert.True(info.ThresholdP90 > 0);
            Assert.True(info.ThresholdP95 >= info.ThresholdP90);
            Assert.True(info.ThresholdP99 >= info.ThresholdP95);
            // 6 settling + 10 post-settle steps
            Assert.Equal(16, snapshot.DitherData.Count);

            // Diagnostic files written with the session/profile naming scheme
            Assert.Single(Directory.GetFiles(tempDir, "*_Default_dither_analysis.txt"));
            Assert.Single(Directory.GetFiles(tempDir, "*_Default_settle_analysis.txt"));
        }

        /// <summary>
        /// The session RMS/StdDev (CurrentRunningRMS/CurrentRMSStdDev) are accumulated
        /// incrementally with Welford's algorithm instead of retained lists (unbounded
        /// growth over an all-day session) - this pins the result to the direct
        /// Σ(x-mean)² formula it replaced, over the exact guide steps that feed the
        /// session statistics for a normal dither cycle.
        /// </summary>
        [Fact]
        public void SessionRms_MatchesDirectCalculation_ForWelfordAccumulator() {
            using var service = CreateService();
            DitherSettingsRecommendation recommendation = null;
            using var recommendationFired = new ManualResetEventSlim(false);
            service.DitherRecommendationUpdated += (s, r) => { recommendation = r; recommendationFired.Set(); };

            var start = DateTime.Now.AddMinutes(-5);
            service.HandleGuidingStarted();
            FeedReferenceWindow(service, start, count: 30);
            RunDitherCycle(service, start.AddSeconds(70));

            Assert.True(recommendationFired.Wait(TimeSpan.FromSeconds(10)), "DitherRecommendationUpdated was not fired");
            Assert.NotNull(recommendation);

            // Reproduce the exact guide steps that feed the session statistics: the 30
            // reference-window steps (FeedReferenceWindow's formula) plus the 10 post-settle
            // steps RunDitherCycle sends after SettleDone (settling steps are excluded
            // while dithering, so they don't count).
            var dx = new List<double>();
            var dy = new List<double>();
            for (int i = 0; i < 30; i++) {
                dx.Add(0.3 + (i % 5) * 0.05);
                dy.Add(0.3);
            }
            for (int i = 0; i < 10; i++) {
                dx.Add(0.3);
                dy.Add(0.3);
            }

            double meanDx = dx.Average();
            double raStdDev = Math.Sqrt(dx.Sum(v => Math.Pow(v - meanDx, 2)) / (dx.Count - 1));
            double meanDy = dy.Average();
            double decStdDev = Math.Sqrt(dy.Sum(v => Math.Pow(v - meanDy, 2)) / (dy.Count - 1));
            double expectedRunningRms = Math.Sqrt(raStdDev * raStdDev + decStdDev * decStdDev);

            var pairDistances = dx.Zip(dy, (x, y) => Math.Sqrt(x * x + y * y)).ToList();
            double meanPd = pairDistances.Average();
            double expectedRmsStdDev = Math.Sqrt(pairDistances.Sum(v => Math.Pow(v - meanPd, 2)) / (pairDistances.Count - 1));

            Assert.Equal(expectedRunningRms, recommendation.CurrentRunningRMS, 9);
            Assert.Equal(expectedRmsStdDev, recommendation.CurrentRMSStdDev, 9);
        }

        [Fact]
        public void DiagnosticFile_HasPlausibleTimestamp_WhenGuidingStartedNeverFired() {
            // Simulates the plugin connecting while PHD2 is already guiding: no
            // GuidingStarted event ever arrives, so sessionStartTime must already be
            // initialized from the constructor instead of defaulting to DateTime.MinValue.
            using var service = CreateService();
            using var recommendationFired = new ManualResetEventSlim(false);
            service.DitherRecommendationUpdated += (s, r) => recommendationFired.Set();
            var start = DateTime.Now.AddMinutes(-5);

            FeedReferenceWindow(service, start);
            RunDitherCycle(service, start.AddSeconds(70));

            Assert.True(recommendationFired.Wait(TimeSpan.FromSeconds(10)), "DitherRecommendationUpdated was not fired");

            var files = Directory.GetFiles(tempDir, "*_Default_dither_analysis.txt");
            var file = Assert.Single(files);
            string sessionTimestamp = Path.GetFileName(file).Split('_')[0] + "_" + Path.GetFileName(file).Split('_')[1];
            var parsed = DateTime.ParseExact(sessionTimestamp, "yyyyMMdd_HHmmss", null);
            Assert.True((DateTime.Now - parsed) < TimeSpan.FromMinutes(1), $"Session timestamp {sessionTimestamp} is not plausible");
        }

        [Fact]
        public void GuideStepsDuringDithering_AreExcludedFromReferenceWindow() {
            using var service = CreateService();
            var start = DateTime.Now.AddMinutes(-5);

            // No pre-dither reference steps: the only non-dithering steps are the
            // 10 post-settle ones, which stay below REFERENCE_MIN_POINTS (20)
            RunDitherCycle(service, start);

            var snapshot = service.GetDitherAnalysisSnapshot();
            var info = Assert.Single(snapshot.SeriesInfos);
            // Had the 6 settling steps leaked into the reference window, it would
            // have held 16 points - still under the minimum, but assert explicitly
            // that no thresholds were derived
            Assert.Equal(0, info.ThresholdP90);
            Assert.Equal(0, info.ThresholdP95);
            Assert.Equal(0, info.ThresholdP99);
        }

        [Fact]
        public void RapidDithering_FinalizesPreviousSeriesEarly() {
            using var service = CreateService();
            var start = DateTime.Now.AddMinutes(-5);

            // First dither: window still open (no SettleDone) when the next dither arrives
            service.HandleGuidingDithered(new PHD2GuidingDitheredEventArgs { Timestamp = start });
            service.HandleGuideStep(Step(3.0, 0, start.AddSeconds(2)));
            service.HandleGuideStep(Step(1.0, 0, start.AddSeconds(4)));

            service.HandleGuidingDithered(new PHD2GuidingDitheredEventArgs { Timestamp = start.AddSeconds(6) });
            service.HandleGuideStep(Step(2.0, 0, start.AddSeconds(8)));

            var snapshot = service.GetDitherAnalysisSnapshot();
            Assert.Equal(2, snapshot.DitherSeriesCounter);
            Assert.Equal(new[] { 1, 1, 2 }, snapshot.DitherData.Select(p => p.DitherSeriesId).ToArray());
            // Series 1 finalized (metadata stored), series 2 still in progress
            Assert.Contains(snapshot.SeriesInfos, i => i.DitherSeriesId == 1);
            Assert.Contains(snapshot.SeriesInfos, i => i.DitherSeriesId == 2);
        }

        [Fact]
        public void StarLostDuringCollection_MarksSeriesExcluded() {
            using var service = CreateService();
            var start = DateTime.Now.AddMinutes(-5);

            service.HandleGuidingDithered(new PHD2GuidingDitheredEventArgs { Timestamp = start });
            service.HandleGuideStep(Step(3.0, 0, start.AddSeconds(2)));
            service.HandleStarLost();
            service.HandleSettleDone(new PHD2SettleDoneEventArgs { Success = true, Status = 0 });
            for (int i = 0; i < 10; i++) {
                service.HandleGuideStep(Step(0.3, 0, start.AddSeconds(4 + i * 2)));
            }

            var snapshot = service.GetDitherAnalysisSnapshot();
            var info = Assert.Single(snapshot.SeriesInfos);
            Assert.True(info.StarLost);
        }

        [Fact]
        public void FailedSettle_IsRecordedOnSeries() {
            using var service = CreateService();
            var start = DateTime.Now.AddMinutes(-5);

            RunDitherCycle(service, start, settleSuccess: false);

            var snapshot = service.GetDitherAnalysisSnapshot();
            var info = Assert.Single(snapshot.SeriesInfos);
            Assert.True(info.SettleReceived);
            Assert.True(info.SettleFailed);
        }

        [Fact]
        public void HandleDisconnected_DiscardsRunningWindow_KeepsAccumulatedSeries() {
            using var service = CreateService();
            var start = DateTime.Now.AddMinutes(-5);

            // Series 1 completes normally
            var t = RunDitherCycle(service, start);

            // Series 2 is still collecting when the disconnect arrives
            service.HandleGuidingDithered(new PHD2GuidingDitheredEventArgs { Timestamp = t.AddSeconds(2) });
            service.HandleGuideStep(Step(3.0, 0, t.AddSeconds(4)));
            service.HandleDisconnected();

            var snapshot = service.GetDitherAnalysisSnapshot();
            Assert.All(snapshot.DitherData, p => Assert.Equal(1, p.DitherSeriesId));
            Assert.Single(snapshot.SeriesInfos);
            // The counter is not reset - a later dither continues as series 3
            Assert.Equal(2, snapshot.DitherSeriesCounter);
        }

        [Fact]
        public void ClearDitherAnalysisData_ResetsEverythingIncludingCounter() {
            using var service = CreateService();
            var start = DateTime.Now.AddMinutes(-5);

            RunDitherCycle(service, start);
            // A second window is open mid-collection when the clear arrives
            service.HandleGuidingDithered(new PHD2GuidingDitheredEventArgs { Timestamp = start.AddSeconds(60) });
            service.HandleGuideStep(Step(3.0, 0, start.AddSeconds(62)));

            service.ClearDitherAnalysisData();

            var snapshot = service.GetDitherAnalysisSnapshot();
            Assert.Empty(snapshot.DitherData);
            Assert.Empty(snapshot.SeriesInfos);
            Assert.Equal(0, snapshot.DitherSeriesCounter);

            // Guide steps after the clear must not be collected as an orphaned series
            service.HandleGuideStep(Step(3.0, 0, start.AddSeconds(64)));
            Assert.Empty(service.GetDitherAnalysisSnapshot().DitherData);
        }

        [Fact]
        public void RestoreDitherAnalysisData_ResumesCounterAboveHighestId() {
            using var service = CreateService();
            var start = DateTime.Now.AddMinutes(-5);

            var persisted = new DitherAnalysisSnapshot { DitherSeriesCounter = 1 };
            persisted.DitherData.Add(new DitherDataPoint { DitherSeriesId = 3, DX = 1, DY = 1, PairRMS = 1.4, Exposure = 2, Timestamp = start });
            persisted.SeriesInfos.Add(new DitherSeriesInfo { DitherSeriesId = 3, DitherEventTime = start });

            service.RestoreDitherAnalysisData(persisted);
            Assert.Equal(3, service.GetDitherAnalysisSnapshot().DitherSeriesCounter);

            // New dither continues above the restored ids - no collision with series 3
            service.HandleGuidingDithered(new PHD2GuidingDitheredEventArgs { Timestamp = start.AddSeconds(10) });
            service.HandleGuideStep(Step(2.0, 0, start.AddSeconds(12)));

            var snapshot = service.GetDitherAnalysisSnapshot();
            Assert.Equal(new[] { 3, 4 }, snapshot.DitherData.Select(p => p.DitherSeriesId).OrderBy(id => id).ToArray());
        }

        [Fact]
        public void Snapshot_IncludesInProgressSeriesWithMetadata() {
            using var service = CreateService();
            var start = DateTime.Now.AddMinutes(-5);

            service.HandleGuidingDithered(new PHD2GuidingDitheredEventArgs { Timestamp = start });
            service.HandleGuideStep(Step(3.0, 0, start.AddSeconds(2)));
            service.HandleGuideStep(Step(2.0, 0, start.AddSeconds(4)));

            var snapshot = service.GetDitherAnalysisSnapshot();
            Assert.Equal(2, snapshot.DitherData.Count);
            var info = Assert.Single(snapshot.SeriesInfos);
            Assert.Equal(1, info.DitherSeriesId);
            // In-progress series: thresholds not captured yet
            Assert.Equal(0, info.ThresholdP90);
        }

        [Fact]
        public void EmptyOrNullSnapshot_DoesNotClearExistingData() {
            using var service = CreateService();
            var start = DateTime.Now.AddMinutes(-5);
            RunDitherCycle(service, start);

            service.RestoreDitherAnalysisData(null);
            service.RestoreDitherAnalysisData(new DitherAnalysisSnapshot());

            Assert.Equal(16, service.GetDitherAnalysisSnapshot().DitherData.Count);
        }

        [Fact]
        public void Constructor_PrunesDiagnosticFiles_KeepsNewest30() {
            // Create 50 old dither_analysis and 50 old settle_analysis files
            var now = DateTime.UtcNow;
            for (int i = 0; i < 50; i++) {
                var timestamp = now.AddDays(-i);
                string ditherFile = Path.Combine(tempDir, $"{timestamp:yyyyMMdd_HHmmss}_Default_dither_analysis.txt");
                string settleFile = Path.Combine(tempDir, $"{timestamp:yyyyMMdd_HHmmss}_Default_settle_analysis.txt");
                File.WriteAllText(ditherFile, "test");
                File.WriteAllText(settleFile, "test");
                File.SetLastWriteTimeUtc(ditherFile, timestamp);
                File.SetLastWriteTimeUtc(settleFile, timestamp);
            }

            Assert.Equal(50, Directory.GetFiles(tempDir, "*_dither_analysis.txt").Length);
            Assert.Equal(50, Directory.GetFiles(tempDir, "*_settle_analysis.txt").Length);

            // Create a new service - this should trigger pruning in the constructor
            using var service = CreateService();

            // Only the newest 30 should remain for each type
            int ditherCount = Directory.GetFiles(tempDir, "*_dither_analysis.txt").Length;
            int settleCount = Directory.GetFiles(tempDir, "*_settle_analysis.txt").Length;
            Assert.Equal(30, ditherCount);
            Assert.Equal(30, settleCount);
        }
    }
}
