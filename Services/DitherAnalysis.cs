using System;
using System.Collections.Generic;
using System.Linq;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// Pure math for the Dither Settings Optimizer: time-to-stable analysis per dither
    /// series and profile, and the recommendation derived from it. No NINA dependencies,
    /// no logging, no I/O - callers (PHD2Client) own state, locking and diagnostics.
    /// </summary>
    public static class DitherAnalysis {
        // Recommendation profiles. The settle tolerance is an empirical quantile of the
        // distance-from-lock distribution during stable guiding: a lower quantile demands
        // more confidence that guiding is back to normal (longer settling), it does NOT
        // improve image quality. Index order: 0=Strict(P90), 1=Standard(P95), 2=Fast(P99).
        public const int PROFILE_COUNT = 3;
        public static readonly double[] PROFILE_QUANTILES = { 0.90, 0.95, 0.99 };
        public static readonly string[] PROFILE_LABELS = { "P90", "P95", "P99" };

        // Minimum number of reference-window points before quantile thresholds are
        // considered meaningful
        public const int REFERENCE_MIN_POINTS = 20;

        // A dither series counts as "stable" at the first point from which this many
        // consecutive points stay below the threshold (debounce against single dips)
        public const int STABLE_CONSECUTIVE_POINTS = 3;

        // Per-series result of the time-to-stable analysis
        public class SeriesSettleAnalysis {
            public int DitherSeriesId;
            public DitherSeriesInfo Info;
            public bool Excluded;                                             // settle failed or star lost
            public double[] Thresholds = new double[PROFILE_COUNT];
            public double?[] TimeToStable = new double?[PROFILE_COUNT];       // null = never stabilized in window
        }

        /// <summary>
        /// Per-profile quantile thresholds (P90/P95/P99) of the given reference values,
        /// or zeros while there are too few points to be meaningful.
        /// </summary>
        public static double[] CalculateThresholds(List<double> referenceValues) {
            var thresholds = new double[PROFILE_COUNT];
            if (referenceValues.Count < REFERENCE_MIN_POINTS) {
                return thresholds;
            }
            for (int p = 0; p < PROFILE_COUNT; p++) {
                thresholds[p] = DitherStatistics.CalculateQuantile(referenceValues, PROFILE_QUANTILES[p]);
            }
            return thresholds;
        }

        /// <summary>
        /// Compute time-to-stable per dither series and profile: the elapsed time from the
        /// dither event to the first point from which STABLE_CONSECUTIVE_POINTS consecutive
        /// points stay below the profile's threshold. Series prefer their stored (at
        /// collection time) thresholds; legacy series without metadata fall back to the
        /// current thresholds and to their first point as time zero.
        /// </summary>
        public static List<SeriesSettleAnalysis> AnalyzeSeries(List<DitherDataPoint> data, Dictionary<int, DitherSeriesInfo> infos, double[] currentThresholds) {
            var result = new List<SeriesSettleAnalysis>();

            foreach (var series in data.GroupBy(p => p.DitherSeriesId).OrderBy(g => g.Key)) {
                var points = series.OrderBy(p => p.Timestamp).ToList();

                if (!infos.TryGetValue(series.Key, out DitherSeriesInfo info) || info == null) {
                    info = new DitherSeriesInfo {
                        DitherSeriesId = series.Key,
                        DitherEventTime = points[0].Timestamp
                    };
                }

                var analysis = new SeriesSettleAnalysis {
                    DitherSeriesId = series.Key,
                    Info = info,
                    Excluded = info.SettleFailed || info.StarLost
                };

                double[] storedThresholds = { info.ThresholdP90, info.ThresholdP95, info.ThresholdP99 };

                for (int p = 0; p < PROFILE_COUNT; p++) {
                    double threshold = storedThresholds[p] > 0 ? storedThresholds[p] : currentThresholds[p];
                    analysis.Thresholds[p] = threshold;
                    if (threshold <= 0) continue;  // no usable reference distribution

                    for (int i = 0; i + STABLE_CONSECUTIVE_POINTS <= points.Count; i++) {
                        bool stable = true;
                        for (int k = 0; k < STABLE_CONSECUTIVE_POINTS; k++) {
                            if (points[i + k].PairRMS > threshold) {
                                stable = false;
                                break;
                            }
                        }
                        if (stable) {
                            analysis.TimeToStable[p] = Math.Max(0, (points[i].Timestamp - info.DitherEventTime).TotalSeconds);
                            break;
                        }
                    }
                }

                result.Add(analysis);
            }

            return result;
        }

        /// <summary>
        /// Build the recommendation from the per-series analyses:
        /// - Settle tolerance per profile = current reference quantile (fallback: median
        ///   of the per-series stored thresholds when no reference window exists yet)
        /// - Min settle time = debounce only (time within tolerance, NOT time to reach it)
        /// - Expected settle duration per profile = median time-to-stable
        /// - Settle timeout per profile = (P95 time-to-stable + min settle) × 1.5 safety,
        ///   at least the longest actually measured settle, rounded up to 10 s
        /// </summary>
        public static DitherSettingsRecommendation CalculateRecommendation(List<SeriesSettleAnalysis> analyses, double[] currentThresholds, double runningRMS, double rmsStdDev, double currentGuideExposure, double? guiderPixelScaleArcsec) {
            if (analyses.Count == 0) {
                return null;
            }

            double guideExposure = currentGuideExposure > 0 ? currentGuideExposure : 2.0;

            var tolerance = new double[PROFILE_COUNT];
            for (int p = 0; p < PROFILE_COUNT; p++) {
                tolerance[p] = currentThresholds[p];
                if (tolerance[p] <= 0) {
                    var stored = analyses.Select(a => a.Thresholds[p]).Where(t => t > 0).ToList();
                    if (stored.Count > 0) {
                        tolerance[p] = DitherStatistics.CalculateMedian(stored);
                    }
                }
            }
            if (tolerance.All(t => t <= 0)) {
                return null;
            }

            // Min settle time is only a debounce against transient dips - the time PHD2
            // requires the star to STAY within tolerance, not the time to reach it
            double minSettle = Math.Max(2 * guideExposure, 5.0);
            minSettle = Math.Round(Math.Ceiling(minSettle / guideExposure) * guideExposure, 1);

            double maxMeasuredSettle = analyses
                .Where(a => !a.Excluded)
                .Select(a => a.Info.MeasuredSettleDuration)
                .DefaultIfEmpty(0)
                .Max();

            var expected = new double[PROFILE_COUNT];
            var timeout = new double[PROFILE_COUNT];
            var used = new int[PROFILE_COUNT];
            var unstabilized = new int[PROFILE_COUNT];
            var spread = new double[PROFILE_COUNT];

            for (int p = 0; p < PROFILE_COUNT; p++) {
                var delays = analyses
                    .Where(a => !a.Excluded && a.TimeToStable[p].HasValue)
                    .Select(a => a.TimeToStable[p].Value)
                    .ToList();

                used[p] = delays.Count;
                unstabilized[p] = analyses.Count(a => !a.Excluded && a.Thresholds[p] > 0 && !a.TimeToStable[p].HasValue);

                if (delays.Count == 0) continue;

                double median = DitherStatistics.CalculateMedian(delays);
                expected[p] = Math.Round(Math.Ceiling(median / guideExposure) * guideExposure, 1);

                double p95Delay = DitherStatistics.CalculateQuantile(delays, 0.95);
                double t = (p95Delay + minSettle) * 1.5;
                t = Math.Max(t, maxMeasuredSettle);
                timeout[p] = Math.Ceiling(t / 10.0) * 10.0;

                if (delays.Count >= 4) {
                    spread[p] = Math.Round(
                        DitherStatistics.CalculateQuantile(delays, 0.75) - DitherStatistics.CalculateQuantile(delays, 0.25), 1);
                }
            }

            return new DitherSettingsRecommendation {
                SettlePixelTolerance_Quality = Math.Round(tolerance[0], 2),
                SettlePixelTolerance_Balanced = Math.Round(tolerance[1], 2),
                SettlePixelTolerance_Performance = Math.Round(tolerance[2], 2),

                MinSettleTime_Quality = minSettle,
                MinSettleTime_Balanced = minSettle,
                MinSettleTime_Performance = minSettle,

                ExpectedSettleDuration_Quality = expected[0],
                ExpectedSettleDuration_Balanced = expected[1],
                ExpectedSettleDuration_Performance = expected[2],

                SettleTimeout_Quality = timeout[0],
                SettleTimeout_Balanced = timeout[1],
                SettleTimeout_Performance = timeout[2],

                SeriesUsed_Quality = used[0],
                SeriesUsed_Balanced = used[1],
                SeriesUsed_Performance = used[2],

                Unstabilized_Quality = unstabilized[0],
                Unstabilized_Balanced = unstabilized[1],
                Unstabilized_Performance = unstabilized[2],

                SettleDelaySpread_Quality = spread[0],
                SettleDelaySpread_Balanced = spread[1],
                SettleDelaySpread_Performance = spread[2],

                DitherEventsAnalyzed = analyses.Count,
                ExcludedSeries = analyses.Count(a => a.Excluded),
                CurrentRunningRMS = runningRMS,
                CurrentRMSStdDev = rmsStdDev,
                GuideExposure = guideExposure,
                GuiderPixelScaleArcsec = guiderPixelScaleArcsec ?? 0
            };
        }
    }
}
