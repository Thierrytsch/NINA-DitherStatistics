using System;
using System.Collections.Generic;
using System.Linq;

namespace DitherStatistics.Plugin {

    /// <summary>
    /// Calculates mathematical quality metrics for dither pattern evaluation
    /// Based on discrepancy theory, drizzle weight simulation, and spatial statistics
    ///
    /// All metrics operate on the SUB-PIXEL PHASES of the cumulative dither positions
    /// (fractional part on the unit torus), expressed in MAIN CAMERA pixels. Positions
    /// arrive in guide camera pixels and are converted via pixelScaleRatio
    /// (main-camera pixels per guide-camera pixel).
    /// </summary>
    public class DitherQualityMetrics {

        /// <summary>
        /// Complete quality assessment result
        /// </summary>
        public class QualityResult {
            // Primary Metrics
            public double CenteredL2Discrepancy { get; set; }
            public double GapFillMetric_1x { get; set; }
            public double GapFillMetric_2x { get; set; }
            public double GapFillMetric_3x { get; set; }
            public double NearestNeighborIndex { get; set; }

            // Temporal pattern metrics (walking noise / stacking rejection)
            public double DriftRatio { get; set; }
            public double PatternSpreadPx { get; set; }

            // Combined Score (0-1, higher = better)
            public double CombinedScore { get; set; }

            // Quality Assessment
            public string QualityRating { get; set; }
            public string Recommendation { get; set; }

            // Context
            public int TotalDithers { get; set; }
            public double PixelScaleRatio { get; set; } = 1.0;
            public double Pixfrac { get; set; } = 0.6;
        }

        /// <summary>
        /// Centralized quality thresholds configuration.
        /// All thresholds were calibrated by QUANTILE-based Monte-Carlo simulation of
        /// RANDOM dither patterns (the kind PHD2 produces), 200-300 runs per N.
        /// The calibration constrains the upper quantiles so that lucky sessions do
        /// not overshoot: P(Excellent | N=50) ≈ 10%. Typical median ratings:
        ///   N=20 -> Fair, N=30 -> Acceptable, N=50 -> Good, N=80 -> Very Good, N=120+ -> Excellent
        /// Clustered patterns (small dither amplitude) score Poor regardless of N.
        /// </summary>
        public static class QualityThresholds {
            // Centered L₂ Discrepancy (CD) thresholds.
            // Monte-Carlo means for random points: N=10: 0.19, N=20: 0.14, N=30: 0.11,
            // N=50: 0.09, N=80: 0.07. (Values below ~0.05 require low-discrepancy
            // sequences which random dithering does not produce.)
            public const double CD_Excellent = 0.08;
            public const double CD_VeryGood = 0.10;
            public const double CD_Good = 0.13;
            public const double CD_Acceptable = 0.17;
            public const double CD_Fair = 0.22;
            // >= 0.22 = Poor

            // Combined Score thresholds
            public const double CombinedScore_Excellent = 0.85;
            public const double CombinedScore_VeryGood = 0.75;
            public const double CombinedScore_Good = 0.65;
            public const double CombinedScore_Acceptable = 0.55;
            public const double CombinedScore_Fair = 0.45;
            // < 0.45 = Poor

            // Nearest Neighbor Index (NNI) thresholds
            public const double NNI_Excellent = 1.5;      // almost regular grid
            public const double NNI_Good = 1.2;           // quasi-random
            public const double NNI_Acceptable = 0.9;     // random-like (fine!)
            public const double NNI_Fair = 0.7;           // some clustering
            // <= 0.7 = Poor

            // Gap-Fill / weight-uniformity targets (min/mean drizzle weight).
            // 1.0 = perfectly even coverage, 0.0 = at least one output pixel gets no flux.
            // Monte-Carlo (pixfrac 0.6, random dithers): 2x reaches 0.85 at ~N=30,
            // 3x reaches 0.85 at ~N=80 (P(met|N=50) ≈ 30%, P(met|N=80) ≈ 55%).
            // 1x is a single output cell per input pixel and is always fully covered.
            public const double GFM_Target_1x = 0.95;
            public const double GFM_Target_2x = 0.85;
            public const double GFM_Target_3x = 0.85;

            // Warning thresholds
            public const double CD_Warning_High = 0.25;      // High clustering warning
            public const double CD_Warning_Moderate = 0.17;  // Moderate clustering note
            public const double GFM2x_Warning = 0.75;        // Insufficient 2× coverage
            public const int MinDithers_Good = 30;           // Typical count where random dithers reach "Good"
            public const double DriftRatio_Warning = 0.6;    // Mostly one-directional pattern -> walking noise risk
            public const double PatternSpread_Warning = 2.0; // Bounding-box diagonal (px); below this, hot-pixel rejection suffers

            // Combined-score component transforms (quantile-calibrated, see class summary).
            // The GFM component is the mean of the 2x and 3x weight uniformity so the
            // demanding 3x coverage influences the overall rating.
            public const double CD_ScoreCeiling = 0.30;   // cdScore = (0.30 - CD) / (0.30 - 0.04)
            public const double CD_ScoreFloor = 0.04;
            public const double GFM_ScoreFloor = 0.70;    // gfmScore = (meanGFM - 0.70) / (0.95 - 0.70)
            public const double GFM_ScoreCeiling = 0.95;
            public const double NNI_ScoreFloor = 0.4;     // nniScore = (NNI - 0.4) / 0.7
            public const double NNI_ScoreRange = 0.7;

            public const double Weight_GFM = 0.30;  // drizzle coverage uniformity (mean of 2x/3x)
            public const double Weight_CD = 0.45;   // global sub-pixel uniformity (least noisy component)
            public const double Weight_NNI = 0.25;  // clustering detector

            // Small-sample confidence margin: score = raw - Margin/sqrt(N).
            // Damps lucky draws at low dither counts so top ratings require evidence,
            // not luck (without it, ~36% of 50-dither sessions rated "Excellent").
            public const double SmallSampleMargin = 0.25;
        }

        /// <summary>
        /// Calculate all quality metrics for a set of dither positions
        /// </summary>
        /// <param name="ditherPositions">List of (X, Y) cumulative positions in guide camera pixels, in chronological order</param>
        /// <param name="pixfrac">Drizzle pixfrac parameter (default 0.6)</param>
        /// <param name="pixelScaleRatio">Main-camera pixels per guide-camera pixel (1.0 = no conversion)</param>
        /// <returns>Complete quality assessment</returns>
        public static QualityResult CalculateQualityMetrics(
            List<(double X, double Y)> ditherPositions,
            double pixfrac = 0.6,
            double pixelScaleRatio = 1.0) {

            if (ditherPositions == null || ditherPositions.Count < 4) {
                return new QualityResult {
                    QualityRating = "Insufficient Data",
                    Recommendation = "At least 4 dither positions required for quality assessment"
                };
            }

            if (pixelScaleRatio <= 0) pixelScaleRatio = 1.0;
            if (pixfrac <= 0 || pixfrac > 1.0) pixfrac = 0.6;

            // Convert to main-camera pixels; sub-pixel phases only make sense there
            var positions = ditherPositions
                .Select(p => (X: p.X * pixelScaleRatio, Y: p.Y * pixelScaleRatio))
                .ToList();

            var fracPositions = positions
                .Select(p => (X: Frac(p.X), Y: Frac(p.Y)))
                .ToList();

            var result = new QualityResult {
                TotalDithers = ditherPositions.Count,
                PixelScaleRatio = pixelScaleRatio,
                Pixfrac = pixfrac
            };

            // Primary metrics (all on sub-pixel phases)
            result.CenteredL2Discrepancy = CalculateCenteredL2Discrepancy(fracPositions);
            result.GapFillMetric_1x = CalculateGapFillMetric(fracPositions, 1, pixfrac);
            result.GapFillMetric_2x = CalculateGapFillMetric(fracPositions, 2, pixfrac);
            result.GapFillMetric_3x = CalculateGapFillMetric(fracPositions, 3, pixfrac);
            result.NearestNeighborIndex = CalculateNearestNeighborIndex(fracPositions);

            // Temporal metrics (on full positions, chronological order)
            result.DriftRatio = CalculateDriftRatio(positions);
            result.PatternSpreadPx = CalculatePatternSpread(positions);

            // Combined score: quantile-calibrated transforms, no double counting
            // (CD enters only here, not inside GFM)
            double cdScore = Clamp01((QualityThresholds.CD_ScoreCeiling - result.CenteredL2Discrepancy)
                / (QualityThresholds.CD_ScoreCeiling - QualityThresholds.CD_ScoreFloor));
            double meanGfm = (result.GapFillMetric_2x + result.GapFillMetric_3x) / 2.0;
            double gfmScore = Clamp01((meanGfm - QualityThresholds.GFM_ScoreFloor)
                / (QualityThresholds.GFM_ScoreCeiling - QualityThresholds.GFM_ScoreFloor));
            double nniScore = Clamp01((result.NearestNeighborIndex - QualityThresholds.NNI_ScoreFloor)
                / QualityThresholds.NNI_ScoreRange);

            double rawScore =
                QualityThresholds.Weight_GFM * gfmScore +
                QualityThresholds.Weight_CD * cdScore +
                QualityThresholds.Weight_NNI * nniScore;

            // Small-sample confidence margin: a lucky draw of few dithers must not
            // earn a top rating; the margin vanishes as evidence accumulates
            result.CombinedScore = Math.Max(0.0,
                rawScore - QualityThresholds.SmallSampleMargin / Math.Sqrt(result.TotalDithers));

            AssignQualityRating(result);

            return result;
        }

        /// <summary>
        /// Fractional part on the unit torus. Unlike Abs(x) % 1, this wraps negative
        /// coordinates correctly: -0.3 -> 0.7 (Abs would mirror it to 0.3).
        /// </summary>
        private static double Frac(double v) {
            return ((v % 1.0) + 1.0) % 1.0;
        }

        private static double Clamp01(double v) {
            return Math.Max(0.0, Math.Min(1.0, v));
        }

        /// <summary>
        /// Calculate Centered L₂ Discrepancy on the sub-pixel phases - measures how
        /// uniformly the phases fill the unit square. Lower = more uniform.
        /// Thresholds in QualityThresholds are calibrated for RANDOM dithering.
        /// </summary>
        private static double CalculateCenteredL2Discrepancy(List<(double X, double Y)> fracPos) {
            int N = fracPos.Count;

            // Term 1: Constant
            double term1 = Math.Pow(13.0 / 12.0, 2);

            // Term 2: Single point sum
            double term2 = 0.0;
            foreach (var pos in fracPos) {
                double prodX = 1.0 + 0.5 * Math.Abs(pos.X - 0.5) - 0.5 * Math.Pow(Math.Abs(pos.X - 0.5), 2);
                double prodY = 1.0 + 0.5 * Math.Abs(pos.Y - 0.5) - 0.5 * Math.Pow(Math.Abs(pos.Y - 0.5), 2);
                term2 += prodX * prodY;
            }
            term2 *= (2.0 / N);

            // Term 3: Pairwise sum
            double term3 = 0.0;
            for (int i = 0; i < N; i++) {
                for (int j = 0; j < N; j++) {
                    double prodX = 1.0 + 0.5 * Math.Abs(fracPos[i].X - 0.5)
                                      + 0.5 * Math.Abs(fracPos[j].X - 0.5)
                                      - 0.5 * Math.Abs(fracPos[i].X - fracPos[j].X);
                    double prodY = 1.0 + 0.5 * Math.Abs(fracPos[i].Y - 0.5)
                                      + 0.5 * Math.Abs(fracPos[j].Y - 0.5)
                                      - 0.5 * Math.Abs(fracPos[i].Y - fracPos[j].Y);
                    term3 += prodX * prodY;
                }
            }
            term3 /= (N * N);

            double cdSquared = term1 - term2 + term3;
            return Math.Sqrt(Math.Max(0, cdSquared));
        }

        /// <summary>
        /// Drizzle weight-uniformity simulation ("Gap-Fill Metric").
        ///
        /// Simulates actual drizzle geometry: the unit input pixel is divided into
        /// scale × scale output cells; each exposure deposits a square drop of side
        /// pixfrac (input px) at its sub-pixel phase (toroidal). The accumulated
        /// overlap area per cell is the drizzle weight; noise per output pixel
        /// scales with 1/sqrt(weight).
        ///
        /// Returns min(weight)/mean(weight) in [0, 1]:
        ///   1.0 = perfectly even coverage,
        ///   0.0 = at least one output pixel receives no flux (a real gap).
        /// </summary>
        private static double CalculateGapFillMetric(
            List<(double X, double Y)> fracPos,
            int scale,
            double pixfrac) {

            double half = pixfrac / 2.0;
            double cellSize = 1.0 / scale;
            var weights = new double[scale, scale];

            foreach (var pos in fracPos) {
                for (int i = 0; i < scale; i++) {
                    double ox = ToroidalOverlap1D(pos.X, half, i * cellSize, (i + 1) * cellSize);
                    if (ox <= 0) continue;
                    for (int j = 0; j < scale; j++) {
                        double oy = ToroidalOverlap1D(pos.Y, half, j * cellSize, (j + 1) * cellSize);
                        if (oy > 0) weights[i, j] += ox * oy;
                    }
                }
            }

            double min = double.MaxValue;
            double sum = 0.0;
            foreach (double w in weights) {
                sum += w;
                if (w < min) min = w;
            }

            double mean = sum / (scale * scale);
            return mean > 0 ? min / mean : 0.0;
        }

        /// <summary>
        /// Overlap length of the interval [center-half, center+half] with [a, b]
        /// on the unit circle (torus).
        /// </summary>
        private static double ToroidalOverlap1D(double center, double half, double a, double b) {
            double overlap = 0.0;
            for (int wrap = -1; wrap <= 1; wrap++) {
                double lo = Math.Max(center - half + wrap, a);
                double hi = Math.Min(center + half + wrap, b);
                if (hi > lo) overlap += hi - lo;
            }
            return overlap;
        }

        /// <summary>
        /// Calculate Nearest Neighbor Index (Clark-Evans) on the sub-pixel phases.
        /// Measures whether the distribution is regular (NNI > 1), random (≈1), or clustered (&lt;1)
        /// </summary>
        private static double CalculateNearestNeighborIndex(List<(double X, double Y)> fracPos) {
            int N = fracPos.Count;
            if (N < 2) return 1.0;

            double sumMinDist = 0.0;
            for (int i = 0; i < N; i++) {
                double minDist = double.MaxValue;
                for (int j = 0; j < N; j++) {
                    if (i == j) continue;

                    // Toroidal distance
                    double dx = Math.Abs(fracPos[i].X - fracPos[j].X);
                    double dy = Math.Abs(fracPos[i].Y - fracPos[j].Y);

                    if (dx > 0.5) dx = 1.0 - dx;
                    if (dy > 0.5) dy = 1.0 - dy;

                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist < minDist) minDist = dist;
                }
                sumMinDist += minDist;
            }
            double observedMean = sumMinDist / N;

            // Expected mean for random distribution in unit square
            double expectedMean = 0.5 / Math.Sqrt(N);

            return expectedMean > 0 ? observedMean / expectedMean : 1.0;
        }

        /*
        NNI interpretation:
        - > 1.5: Excellent (almost regular grid)
        - > 1.2: Good (quasi-random)
        - > 0.9: Acceptable (random-like, fine for drizzle!)
        - > 0.7: Fair (some clustering)
        - < 0.7: Poor (significant clustering)
        */

        /// <summary>
        /// Drift ratio = |net displacement| / total path length of the dither sequence.
        /// 0 = pattern keeps returning near its origin (good),
        /// 1 = every step moves in the same direction (walking noise risk).
        /// </summary>
        private static double CalculateDriftRatio(List<(double X, double Y)> positions) {
            if (positions.Count < 2) return 0.0;

            double pathLength = 0.0;
            for (int i = 1; i < positions.Count; i++) {
                double dx = positions[i].X - positions[i - 1].X;
                double dy = positions[i].Y - positions[i - 1].Y;
                pathLength += Math.Sqrt(dx * dx + dy * dy);
            }
            if (pathLength <= 0) return 0.0;

            double netX = positions[positions.Count - 1].X - positions[0].X;
            double netY = positions[positions.Count - 1].Y - positions[0].Y;
            double netDisplacement = Math.Sqrt(netX * netX + netY * netY);

            return Math.Min(1.0, netDisplacement / pathLength);
        }

        /// <summary>
        /// Bounding-box diagonal of the cumulative positions (main-camera px).
        /// A very small spread means hot pixels land on nearly the same spot in
        /// every frame, defeating outlier rejection during stacking.
        /// </summary>
        private static double CalculatePatternSpread(List<(double X, double Y)> positions) {
            if (positions.Count < 2) return 0.0;

            double minX = positions.Min(p => p.X);
            double maxX = positions.Max(p => p.X);
            double minY = positions.Min(p => p.Y);
            double maxY = positions.Max(p => p.Y);

            double w = maxX - minX;
            double h = maxY - minY;
            return Math.Sqrt(w * w + h * h);
        }

        /// <summary>
        /// Assign quality rating and recommendation.
        /// The rating describes PATTERN QUALITY; drizzle coverage sufficiency is
        /// reported separately per scale so the two questions stay distinct.
        /// </summary>
        private static void AssignQualityRating(QualityResult result) {
            if (result.CombinedScore >= QualityThresholds.CombinedScore_Excellent) {
                result.QualityRating = "Excellent";
                result.Recommendation = "Excellent sub-pixel distribution.";
            } else if (result.CombinedScore >= QualityThresholds.CombinedScore_VeryGood) {
                result.QualityRating = "Very Good";
                result.Recommendation = "Very good sub-pixel distribution.";
            } else if (result.CombinedScore >= QualityThresholds.CombinedScore_Good) {
                result.QualityRating = "Good";
                result.Recommendation = "Good sub-pixel distribution.";
            } else if (result.CombinedScore >= QualityThresholds.CombinedScore_Acceptable) {
                result.QualityRating = "Acceptable";
                result.Recommendation = "Acceptable sub-pixel distribution.";
            } else if (result.CombinedScore >= QualityThresholds.CombinedScore_Fair) {
                result.QualityRating = "Fair";
                result.Recommendation = "Suboptimal sub-pixel distribution.";
            } else {
                result.QualityRating = "Poor";
                result.Recommendation = "Poor sub-pixel distribution - expect uneven drizzle weights.";
            }

            // Coverage sufficiency, decoupled from the pattern rating
            result.Recommendation += $" Drizzle coverage: 1× {CoverageMark(result.GapFillMetric_1x, QualityThresholds.GFM_Target_1x)}, " +
                $"2× {CoverageMark(result.GapFillMetric_2x, QualityThresholds.GFM_Target_2x)}, " +
                $"3× {CoverageMark(result.GapFillMetric_3x, QualityThresholds.GFM_Target_3x)}.";

            if (result.GapFillMetric_2x < QualityThresholds.GFM_Target_2x) {
                result.Recommendation += $" 2× target is typically reached with ~{QualityThresholds.MinDithers_Good} well-distributed dithers (current: {result.TotalDithers}).";
            } else if (result.GapFillMetric_3x < QualityThresholds.GFM_Target_3x) {
                result.Recommendation += $" 3× target typically needs ~80+ dithers (current: {result.TotalDithers}).";
            } else {
                result.Recommendation += " Note: coverage is not the limiting factor for 3× drizzle - it also requires plenty of total integration time for SNR (9× smaller output pixel area).";
            }

            // Warnings
            if (result.CenteredL2Discrepancy > QualityThresholds.CD_Warning_High) {
                result.Recommendation += $" WARNING: High sub-pixel clustering (CD > {QualityThresholds.CD_Warning_High}) - check that the dither amplitude is not too small.";
            } else if (result.CenteredL2Discrepancy > QualityThresholds.CD_Warning_Moderate) {
                result.Recommendation += $" NOTE: Some sub-pixel clustering (CD > {QualityThresholds.CD_Warning_Moderate}). More dithers will even this out.";
            }

            if (result.DriftRatio > QualityThresholds.DriftRatio_Warning && result.TotalDithers >= 8) {
                result.Recommendation += $" WARNING: Pattern drifts mostly in one direction (drift ratio {result.DriftRatio:F2}) - risk of walking noise.";
            }

            if (result.PatternSpreadPx < QualityThresholds.PatternSpread_Warning && result.TotalDithers >= 8) {
                result.Recommendation += $" WARNING: Total pattern spread is only {result.PatternSpreadPx:F1} px - too small for effective hot-pixel rejection.";
            }
        }

        private static string CoverageMark(double gfm, double target) {
            return gfm >= target ? "OK" : "insufficient";
        }

        /// <summary>
        /// Format quality metrics for display
        /// </summary>
        public static string FormatMetricsReport(QualityResult result) {
            return $@"
=== Dither Quality Assessment ===

Overall Rating: {result.QualityRating}
Combined Score: {result.CombinedScore:F3} / 1.000

--- Primary Metrics (sub-pixel phases, main-camera px) ---
Centered L₂ Discrepancy: {result.CenteredL2Discrepancy:F4}
  → {GetCDRating(result.CenteredL2Discrepancy)}

Drizzle Weight Uniformity (min/mean drizzle weight per output pixel):
  • 1× Drizzle: {result.GapFillMetric_1x:P1} {GetGFMStatus(result.GapFillMetric_1x, QualityThresholds.GFM_Target_1x)} (Target: ≥{QualityThresholds.GFM_Target_1x:P0})
  • 2× Drizzle: {result.GapFillMetric_2x:P1} {GetGFMStatus(result.GapFillMetric_2x, QualityThresholds.GFM_Target_2x)} (Target: ≥{QualityThresholds.GFM_Target_2x:P0} - typically ~30 dithers)
  • 3× Drizzle: {result.GapFillMetric_3x:P1} {GetGFMStatus(result.GapFillMetric_3x, QualityThresholds.GFM_Target_3x)} (Target: ≥{QualityThresholds.GFM_Target_3x:P0} - typically ~80+ dithers)
  (1.0 = perfectly even coverage; 0.0 = an output pixel receives no flux)

Nearest Neighbor Index: {result.NearestNeighborIndex:F2}
  → {GetNNIInterpretation(result.NearestNeighborIndex)}

--- Temporal Pattern ---
Drift Ratio: {result.DriftRatio:F2} (0 = returns to origin, 1 = one-directional drift / walking noise risk)
Pattern Spread: {result.PatternSpreadPx:F1} px (bounding-box diagonal; larger = better hot-pixel rejection)

--- Context ---
Total Dithers: {result.TotalDithers}
Pixel Scale Ratio (main-cam px per guider px): {result.PixelScaleRatio:F2}
Drizzle pixfrac: {result.Pixfrac:F2}

--- Recommendation ---
{result.Recommendation}

--- Grading Scale ---
Thresholds are quantile-calibrated by Monte-Carlo simulation of random dithering
(the kind PHD2 produces). Typical median ratings by dither count:
  • ~20 dithers: Fair
  • ~30 dithers: Acceptable
  • ~50 dithers: Good
  • ~80 dithers: Very Good
  • ~120+ dithers: Excellent
A small-sample confidence margin ({QualityThresholds.SmallSampleMargin:F2}/√N) is subtracted from the raw score,
so lucky sessions with few dithers cannot reach the top ratings.

Combined Score = {QualityThresholds.Weight_GFM:F2}·GFM(mean of 2×,3×) + {QualityThresholds.Weight_CD:F2}·CD + {QualityThresholds.Weight_NNI:F2}·NNI − {QualityThresholds.SmallSampleMargin:F2}/√N (components normalized to [0,1])
  • {QualityThresholds.CombinedScore_Excellent:F2}-1.00: Excellent
  • {QualityThresholds.CombinedScore_VeryGood:F2}-{QualityThresholds.CombinedScore_Excellent:F2}: Very Good
  • {QualityThresholds.CombinedScore_Good:F2}-{QualityThresholds.CombinedScore_VeryGood:F2}: Good
  • {QualityThresholds.CombinedScore_Acceptable:F2}-{QualityThresholds.CombinedScore_Good:F2}: Acceptable
  • {QualityThresholds.CombinedScore_Fair:F2}-{QualityThresholds.CombinedScore_Acceptable:F2}: Fair
  • <{QualityThresholds.CombinedScore_Fair:F2}: Poor
";
        }

        private static string GetCDRating(double cd) {
            // Uses centralized thresholds from QualityThresholds
            if (cd < QualityThresholds.CD_Excellent) return "Excellent uniformity (typical for ~80+ random dithers)";
            if (cd < QualityThresholds.CD_VeryGood) return "Very good uniformity (typical for ~50 random dithers)";
            if (cd < QualityThresholds.CD_Good) return "Good uniformity (typical for ~30 random dithers)";
            if (cd < QualityThresholds.CD_Acceptable) return "Acceptable uniformity (typical for ~20 random dithers)";
            if (cd < QualityThresholds.CD_Fair) return "Fair uniformity (typical for ~10 random dithers)";
            return "Poor uniformity - significant sub-pixel clustering";
        }

        private static string GetGFMStatus(double gfm, double threshold) {
            return gfm >= threshold ? "✓" : "⚠";
        }

        private static string GetNNIInterpretation(double nni) {
            // Uses centralized thresholds from QualityThresholds
            if (nni > QualityThresholds.NNI_Excellent) return "Excellent sub-pixel uniformity (regular)";
            if (nni > QualityThresholds.NNI_Good) return "Good sub-pixel uniformity (quasi-random)";
            if (nni > QualityThresholds.NNI_Acceptable) return "Acceptable coverage (random-like, fine!)";
            if (nni > QualityThresholds.NNI_Fair) return "Fair coverage with some clustering";
            return "Poor coverage - significant clustering";
        }
    }
}
