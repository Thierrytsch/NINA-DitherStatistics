using System;
using System.Collections.Generic;
using Xunit;

namespace DitherStatistics.Plugin.Tests {
    public class DitherAnalysisTests {
        private static readonly DateTime Start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static List<DitherDataPoint> BuildSeries(int seriesId, DateTime start, params double[] rmsValues) {
            var points = new List<DitherDataPoint>();
            for (int i = 0; i < rmsValues.Length; i++) {
                points.Add(new DitherDataPoint {
                    DitherSeriesId = seriesId,
                    PairRMS = rmsValues[i],
                    Timestamp = start.AddSeconds(i + 1),
                    Exposure = 2.0
                });
            }
            return points;
        }

        private static DitherAnalysis.SeriesSettleAnalysis MakeAnalysis(int id, double[] thresholds, double?[] timeToStable, bool excluded, double measuredSettle) {
            return new DitherAnalysis.SeriesSettleAnalysis {
                DitherSeriesId = id,
                Info = new DitherSeriesInfo { DitherSeriesId = id, MeasuredSettleDuration = measuredSettle, SettleFailed = excluded },
                Excluded = excluded,
                Thresholds = thresholds,
                TimeToStable = timeToStable
            };
        }

        // --- AnalyzeSeries ---

        [Fact]
        public void AnalyzeSeries_StabilizesAfterNPoints_ReportsFirstStableTimestamp() {
            var points = BuildSeries(1, Start, 2.0, 2.0, 0.5, 0.4, 0.3);
            var infos = new Dictionary<int, DitherSeriesInfo> {
                [1] = new DitherSeriesInfo { DitherSeriesId = 1, DitherEventTime = Start }
            };
            var thresholds = new double[] { 1.0, 1.0, 1.0 };

            var result = DitherAnalysis.AnalyzeSeries(points, infos, thresholds);

            var analysis = Assert.Single(result);
            Assert.False(analysis.Excluded);
            Assert.Equal(3.0, analysis.TimeToStable[0]);
            Assert.Equal(3.0, analysis.TimeToStable[1]);
            Assert.Equal(3.0, analysis.TimeToStable[2]);
        }

        [Fact]
        public void AnalyzeSeries_NeverBelowThreshold_IsCensored() {
            var points = BuildSeries(2, Start, 2.0, 2.0, 2.0, 2.0);
            var infos = new Dictionary<int, DitherSeriesInfo> {
                [2] = new DitherSeriesInfo { DitherSeriesId = 2, DitherEventTime = Start }
            };
            var thresholds = new double[] { 1.0, 1.0, 1.0 };

            var result = DitherAnalysis.AnalyzeSeries(points, infos, thresholds);

            Assert.Null(result[0].TimeToStable[0]);
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void AnalyzeSeries_SettleFailedOrStarLost_ExcludesSeries(bool settleFailed, bool starLost) {
            var points = BuildSeries(3, Start, 0.5, 0.5, 0.5);
            var infos = new Dictionary<int, DitherSeriesInfo> {
                [3] = new DitherSeriesInfo { DitherSeriesId = 3, DitherEventTime = Start, SettleFailed = settleFailed, StarLost = starLost }
            };

            var result = DitherAnalysis.AnalyzeSeries(points, infos, new double[] { 1.0, 1.0, 1.0 });

            Assert.True(result[0].Excluded);
        }

        [Fact]
        public void AnalyzeSeries_LegacySeriesWithoutInfo_FallsBackToFirstPointAsTimeZero() {
            var points = BuildSeries(4, Start, 2.0, 2.0, 0.5, 0.4, 0.3);
            var infos = new Dictionary<int, DitherSeriesInfo>(); // no metadata for series 4

            var result = DitherAnalysis.AnalyzeSeries(points, infos, new double[] { 1.0, 1.0, 1.0 });

            // DitherEventTime falls back to points[0].Timestamp = Start+1s;
            // stable at points[2].Timestamp = Start+3s -> delta 2s
            Assert.Equal(2.0, result[0].TimeToStable[0]);
        }

        [Fact]
        public void AnalyzeSeries_SingleDipBelowThreshold_DoesNotCountAsStable() {
            var points = BuildSeries(5, Start, 2.0, 2.0, 0.5, 2.0, 0.5, 0.5, 0.5);
            var infos = new Dictionary<int, DitherSeriesInfo> {
                [5] = new DitherSeriesInfo { DitherSeriesId = 5, DitherEventTime = Start }
            };

            var result = DitherAnalysis.AnalyzeSeries(points, infos, new double[] { 1.0, 1.0, 1.0 });

            // The single dip at index 2 (t=3s) is followed by an above-threshold point,
            // so the debounce only accepts the run starting at index 4 (t=5s)
            Assert.Equal(5.0, result[0].TimeToStable[0]);
        }

        [Fact]
        public void AnalyzeSeries_StoredThresholdTakesPrecedenceOverCurrentThreshold() {
            // Stored threshold (0.3) is stricter than the current one (1.0): with the
            // stored threshold the series never stabilizes even though it would with
            // the current one
            var points = BuildSeries(6, Start, 0.5, 0.5, 0.5);
            var infos = new Dictionary<int, DitherSeriesInfo> {
                [6] = new DitherSeriesInfo { DitherSeriesId = 6, DitherEventTime = Start, ThresholdP90 = 0.3, ThresholdP95 = 0.3, ThresholdP99 = 0.3 }
            };

            var result = DitherAnalysis.AnalyzeSeries(points, infos, new double[] { 1.0, 1.0, 1.0 });

            Assert.Null(result[0].TimeToStable[0]);
        }

        // --- CalculateThresholds ---

        [Fact]
        public void CalculateThresholds_BelowMinimumPoints_ReturnsZeros() {
            var values = new List<double>();
            for (int i = 0; i < DitherAnalysis.REFERENCE_MIN_POINTS - 1; i++) values.Add(1.0);

            var thresholds = DitherAnalysis.CalculateThresholds(values);

            Assert.All(thresholds, t => Assert.Equal(0, t));
        }

        [Fact]
        public void CalculateThresholds_EnoughPoints_ReturnsQuantiles() {
            var values = new List<double>();
            for (int i = 1; i <= DitherAnalysis.REFERENCE_MIN_POINTS; i++) values.Add(i);

            var thresholds = DitherAnalysis.CalculateThresholds(values);

            Assert.Equal(DitherStatistics.CalculateQuantile(values, 0.90), thresholds[0]);
            Assert.Equal(DitherStatistics.CalculateQuantile(values, 0.95), thresholds[1]);
            Assert.Equal(DitherStatistics.CalculateQuantile(values, 0.99), thresholds[2]);
        }

        // --- CalculateRecommendation ---

        [Fact]
        public void CalculateRecommendation_NoAnalyses_ReturnsNull() {
            var rec = DitherAnalysis.CalculateRecommendation(new List<DitherAnalysis.SeriesSettleAnalysis>(), new double[] { 0, 0, 0 }, 0, 0, 2.0, null);
            Assert.Null(rec);
        }

        [Fact]
        public void CalculateRecommendation_NoToleranceAvailable_ReturnsNull() {
            var analyses = new List<DitherAnalysis.SeriesSettleAnalysis> {
                MakeAnalysis(1, new double[] { 0, 0, 0 }, new double?[] { null, null, null }, false, 0)
            };

            var rec = DitherAnalysis.CalculateRecommendation(analyses, new double[] { 0, 0, 0 }, 0, 0, 2.0, null);

            Assert.Null(rec);
        }

        [Fact]
        public void CalculateRecommendation_FallsBackToMedianOfStoredThresholds_WhenNoReferenceWindow() {
            var analyses = new List<DitherAnalysis.SeriesSettleAnalysis> {
                MakeAnalysis(1, new double[] { 1.0, 1.0, 1.0 }, new double?[] { 10, 10, 10 }, false, 15),
                MakeAnalysis(2, new double[] { 2.0, 2.0, 2.0 }, new double?[] { 20, 20, 20 }, false, 20),
                MakeAnalysis(3, new double[] { 3.0, 3.0, 3.0 }, new double?[] { 30, 30, 30 }, false, 25),
            };

            var rec = DitherAnalysis.CalculateRecommendation(analyses, new double[] { 0, 0, 0 }, 1.0, 0.1, 2.0, null);

            Assert.NotNull(rec);
            Assert.Equal(2.0, rec.SettlePixelTolerance_Quality);
            Assert.Equal(2.0, rec.SettlePixelTolerance_Balanced);
            Assert.Equal(2.0, rec.SettlePixelTolerance_Performance);
        }

        [Fact]
        public void CalculateRecommendation_ComputesExpectedTimeoutAndSpread() {
            var analyses = new List<DitherAnalysis.SeriesSettleAnalysis> {
                MakeAnalysis(1, new double[] { 2, 2, 2 }, new double?[] { 10.0, 10.0, 10.0 }, false, 15),
                MakeAnalysis(2, new double[] { 2, 2, 2 }, new double?[] { 20.0, 20.0, 20.0 }, false, 20),
                MakeAnalysis(3, new double[] { 2, 2, 2 }, new double?[] { 30.0, 30.0, 30.0 }, false, 25),
                MakeAnalysis(4, new double[] { 2, 2, 2 }, new double?[] { 40.0, 40.0, 40.0 }, false, 10),
            };
            var currentThresholds = new double[] { 2, 2, 2 };

            var rec = DitherAnalysis.CalculateRecommendation(analyses, currentThresholds, 1.0, 0.1, 2.0, 1.5);

            Assert.NotNull(rec);
            // guideExposure=2.0 -> minSettle = ceil(max(4,5)/2)*2 = 6.0
            Assert.Equal(6.0, rec.MinSettleTime_Quality);
            Assert.Equal(2.0, rec.SettlePixelTolerance_Quality);
            // median([10,20,30,40])=25 -> ceil(25/2)*2 = 26.0
            Assert.Equal(26.0, rec.ExpectedSettleDuration_Quality);
            // p95=(38.5), t=(38.5+6.0)*1.5=66.75 -> ceil(66.75/10)*10 = 70.0
            Assert.Equal(70.0, rec.SettleTimeout_Quality);
            Assert.Equal(4, rec.SeriesUsed_Quality);
            Assert.Equal(0, rec.Unstabilized_Quality);
            // IQR = Q75(32.5) - Q25(17.5) = 15.0
            Assert.Equal(15.0, rec.SettleDelaySpread_Quality);
            Assert.Equal(4, rec.DitherEventsAnalyzed);
            Assert.Equal(0, rec.ExcludedSeries);
            Assert.Equal(1.5, rec.GuiderPixelScaleArcsec);
        }

        [Fact]
        public void CalculateRecommendation_ExcludedSeriesAreIgnoredForTimingButCountedInTotals() {
            var analyses = new List<DitherAnalysis.SeriesSettleAnalysis> {
                MakeAnalysis(1, new double[] { 2, 2, 2 }, new double?[] { 10.0, 10.0, 10.0 }, false, 15),
                MakeAnalysis(2, new double[] { 2, 2, 2 }, new double?[] { 999.0, 999.0, 999.0 }, true, 999),
            };

            var rec = DitherAnalysis.CalculateRecommendation(analyses, new double[] { 2, 2, 2 }, 1.0, 0.1, 2.0, null);

            Assert.NotNull(rec);
            Assert.Equal(2, rec.DitherEventsAnalyzed);
            Assert.Equal(1, rec.ExcludedSeries);
            Assert.Equal(1, rec.SeriesUsed_Quality);
        }
    }
}
