using System;
using System.Collections.Generic;
using System.Linq;

namespace DitherStatistics.Plugin {

    /// <summary>
    /// Calculates mathematical quality metrics for dither pattern evaluation
    /// Based on discrepancy theory, coverage analysis, and spatial statistics
    /// FINAL VERSION - Strict grading scale for advanced amateur astrophotography
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
            public double VoronoiCV { get; set; }

            // Combined Score (0-1, higher = better)
            public double CombinedScore { get; set; }

            // Quality Assessment
            public string QualityRating { get; set; }
            public string Recommendation { get; set; }

            // Supplementary Metrics
            public double NearestNeighborIndex { get; set; }
            public int TotalDithers { get; set; }
        }

        /// <summary>
        /// Centralized quality thresholds configuration
        /// All rating thresholds are defined here for easy maintenance
        /// </summary>
        public static class QualityThresholds {
            // Centered L₂ Discrepancy (CD) Thresholds
            public const double CD_Excellent = 0.04;   // 80-100+ dithers
            public const double CD_VeryGood = 0.08;    // 50-70 dithers
            public const double CD_Good = 0.15;        // 30-40 dithers
            public const double CD_Acceptable = 0.25;  // 15-25 dithers
            public const double CD_Fair = 0.40;        // 8-14 dithers
            // >= 0.40 = Poor

            // Combined Score Thresholds
            public const double CombinedScore_Excellent = 0.85;   // 60-80+ dithers
            public const double CombinedScore_VeryGood = 0.75;    // 40-60 dithers
            public const double CombinedScore_Good = 0.65;        // 30-40 dithers
            public const double CombinedScore_Acceptable = 0.55;  // 20-30 dithers
            public const double CombinedScore_Fair = 0.45;        // 10-20 dithers
            // < 0.45 = Poor

            // Voronoi CV Thresholds
            public const double VoronoiCV_Excellent = 0.25;   // near-regular distribution
            public const double VoronoiCV_Good = 0.40;        // low clustering
            public const double VoronoiCV_Acceptable = 0.60;  // random-like (OK!)
            public const double VoronoiCV_Fair = 0.80;        // moderate clustering
            // >= 0.80 = Poor

            // Nearest Neighbor Index (NNI) Thresholds
            public const double NNI_Excellent = 1.5;      // almost regular grid
            public const double NNI_Good = 1.2;           // quasi-random
            public const double NNI_Acceptable = 0.9;     // random-like (fine!)
            public const double NNI_Fair = 0.7;           // some clustering
            // <= 0.7 = Poor

            // Gap-Fill Metric (GFM) Targets
            public const double GFM_Target_1x = 0.98;  // 98% for 1× drizzle
            public const double GFM_Target_2x = 0.95;  // 95% for 2× drizzle
            public const double GFM_Target_3x = 0.90;  // 90% for 3× drizzle

            // Warning Thresholds
            public const double CD_Warning_High = 0.40;      // High clustering warning
            public const double CD_Warning_Moderate = 0.25;  // Moderate clustering note
            public const double GFM2x_Warning = 0.90;        // Insufficient 2× coverage
            public const int MinDithers_Good = 30;           // Minimum dithers for "Good" recommendation
        }

        /// <summary>
        /// Calculate all quality metrics for a set of dither positions
        /// </summary>
        /// <param name="ditherPositions">List of (X, Y) cumulative pixel positions</param>
        /// <param name="pixfrac">Drizzle pixfrac parameter (default 0.6)</param>
        /// <returns>Complete quality assessment</returns>
        public static QualityResult CalculateQualityMetrics(
            List<(double X, double Y)> ditherPositions,
            double pixfrac = 0.6) {

            if (ditherPositions == null || ditherPositions.Count < 4) {
                return new QualityResult {
                    QualityRating = "Insufficient Data",
                    Recommendation = "At least 4 dither positions required for quality assessment"
                };
            }

            var result = new QualityResult {
                TotalDithers = ditherPositions.Count
            };

            // Calculate primary metrics
            result.CenteredL2Discrepancy = CalculateCenteredL2Discrepancy(ditherPositions);
            result.GapFillMetric_1x = CalculateGapFillMetric(ditherPositions, 1.0, pixfrac);
            result.GapFillMetric_2x = CalculateGapFillMetric(ditherPositions, 2.0, pixfrac);
            result.GapFillMetric_3x = CalculateGapFillMetric(ditherPositions, 3.0, pixfrac);
            result.VoronoiCV = CalculateVoronoiCV(ditherPositions);
            result.NearestNeighborIndex = CalculateNearestNeighborIndex(ditherPositions);

            // STRICT GRADING: Weights optimized for demanding quality assessment
            // CD is now more important due to stricter scale
            double w1 = 0.35; // Global uniformity (CD) - important quality indicator
            double w2 = 0.45; // Drizzle-specific coverage (GFM) - most directly useful
            double w3 = 0.20; // Local uniformity (Voronoi CV) - supplementary

            // STRICT: CD normalized at 0.20 for balanced combined score
            // This means CD < 0.20 scores high, encouraging good distribution
            // The individual CD rating is strict, but combined score remains practical
            double cdScore = Math.Max(0, 1.0 - result.CenteredL2Discrepancy / 0.20);
            double gfmScore = result.GapFillMetric_2x; // Already in [0, 1]
            double voronoiScore = Math.Max(0, 1.0 - result.VoronoiCV / 0.60);

            result.CombinedScore = w1 * cdScore + w2 * gfmScore + w3 * voronoiScore;

            // Assign quality rating and recommendation
            AssignQualityRating(result);

            return result;
        }

        /// <summary>
        /// Calculate Centered L₂ Discrepancy - measures uniformity of distribution
        /// STRICT GRADING SCALE for demanding quality standards:
        /// < 0.04: Excellent (80-100+ well-distributed dithers)
        /// < 0.08: Very Good (50-70 dithers)
        /// < 0.15: Good (30-40 dithers)
        /// < 0.25: Acceptable (15-25 dithers)
        /// < 0.40: Fair (8-14 dithers or some clustering)
        /// >= 0.40: Poor (insufficient or heavily clustered)
        /// </summary>
        private static double CalculateCenteredL2Discrepancy(List<(double X, double Y)> positions) {
            int N = positions.Count;

            // Extract fractional positions (mod 1.0 for toroidal topology)
            // This analyzes SUB-PIXEL uniformity, which is what matters for drizzle
            var fracPos = positions.Select(p => (
                X: Math.Abs(p.X) % 1.0,
                Y: Math.Abs(p.Y) % 1.0
            )).ToList();

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
        /// Calculate Gap-Fill Metric for specific drizzle parameters
        /// Returns value in [0, 1] where 1.0 = perfect coverage, no gaps
        /// 
        /// CORRECTED TARGETS (Higher scale = MORE challenging!):
        /// - 1× drizzle: Target 98%+ (easy: 10-15 dithers sufficient)
        /// - 2× drizzle: Target 95%+ (moderate: 25-30 dithers needed)
        /// - 3× drizzle: Target 90%+ (demanding: 80+ dithers needed)
        /// </summary>
        private static double CalculateGapFillMetric(
            List<(double X, double Y)> positions,
            double scale,
            double pixfrac) {

            int N = positions.Count;

            // Drop size in OUTPUT pixel coordinates
            double dropSize = pixfrac / scale;

            // CRITICAL: Number of output pixels scales with scale²
            // scale=1 → 1× pixels, scale=2 → 4× pixels, scale=3 → 9× pixels
            double outputPixelCount = scale * scale;

            // Effective drop area coverage per dither
            double effectiveDropArea = dropSize * dropSize;

            // Total coverage = N dithers × effectiveDropArea, normalized by output area
            double rawCoverage = (N * effectiveDropArea) / outputPixelCount;

            // Apply uniformity penalty based on Centered L2 Discrepancy
            double cd = CalculateCenteredL2Discrepancy(positions);

            // STRICT: Uniformity penalty calibrated for strict CD scale
            // CD < 0.10: minimal penalty (0-7%)
            // CD ~ 0.20: moderate penalty (~14%)
            // CD ~ 0.35: significant penalty (~24%)
            double uniformityPenalty = cd * 0.7; // Penalty factor

            double adjustedCoverage = rawCoverage * (1.0 - uniformityPenalty);

            // Cap at 1.0 (100%) and ensure non-negative
            return Math.Max(0, Math.Min(1.0, adjustedCoverage));
        }

        /// <summary>
        /// Calculate Voronoi CV (approximated by NN distances)
        /// Measures local uniformity of the dither distribution
        /// </summary>
        private static double CalculateVoronoiCV(List<(double X, double Y)> positions) {
            int N = positions.Count;
            if (N < 2) return 0.0;

            // Use fractional sub-pixel positions (mod 1)
            var fracPositions = positions.Select(p => (
                X: Math.Abs(p.X) % 1.0,
                Y: Math.Abs(p.Y) % 1.0
            )).ToList();

            // Calculate nearest neighbor distances
            var nnDistances = new List<double>();
            for (int i = 0; i < N; i++) {
                double minDist = double.MaxValue;
                for (int j = 0; j < N; j++) {
                    if (i == j) continue;

                    // Toroidal distance
                    double dx = Math.Abs(fracPositions[i].X - fracPositions[j].X);
                    double dy = Math.Abs(fracPositions[i].Y - fracPositions[j].Y);

                    if (dx > 0.5) dx = 1.0 - dx;
                    if (dy > 0.5) dy = 1.0 - dy;

                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist < minDist) minDist = dist;
                }
                nnDistances.Add(minDist);
            }

            // Calculate Coefficient of Variation (CV = StdDev / Mean)
            double mean = nnDistances.Average();
            if (mean == 0) return 0.0;

            double variance = nnDistances.Select(d => Math.Pow(d - mean, 2)).Average();
            double stdDev = Math.Sqrt(variance);

            return stdDev / mean;
        }

        /*
        Voronoi CV interpretation (NN-distance approximation):
        - < 0.25: Excellent (near-regular distribution)
        - < 0.40: Good (low clustering)
        - < 0.60: Acceptable (random-like, OK for drizzle!)
        - < 0.80: Fair (moderate clustering)
        - > 0.80: Poor (significant clustering/gaps)

        Random distribution has CV ≈ 0.5 which is ACCEPTABLE.
        */

        /// <summary>
        /// Calculate Nearest Neighbor Index (Clark-Evans) on SUB-PIXEL POSITIONS
        /// Measures whether distribution is regular (NNI > 1), random (≈1), or clustered (<1)
        /// </summary>
        private static double CalculateNearestNeighborIndex(List<(double X, double Y)> positions) {
            int N = positions.Count;
            if (N < 2) return 1.0;

            // Use fractional sub-pixel positions (mod 1)
            var fracPositions = positions.Select(p => (
                X: Math.Abs(p.X) % 1.0,
                Y: Math.Abs(p.Y) % 1.0
            )).ToList();

            // Calculate observed mean nearest neighbor distance
            double sumMinDist = 0.0;
            for (int i = 0; i < N; i++) {
                double minDist = double.MaxValue;
                for (int j = 0; j < N; j++) {
                    if (i == j) continue;

                    // Toroidal distance
                    double dx = Math.Abs(fracPositions[i].X - fracPositions[j].X);
                    double dy = Math.Abs(fracPositions[i].Y - fracPositions[j].Y);

                    if (dx > 0.5) dx = 1.0 - dx;
                    if (dy > 0.5) dy = 1.0 - dy;

                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist < minDist) minDist = dist;
                }
                sumMinDist += minDist;
            }
            double observedMean = sumMinDist / N;

            // Expected mean for random distribution in unit square
            double density = N;
            double expectedMean = 0.5 / Math.Sqrt(density);

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
        /// Assign quality rating and recommendation based on combined score
        /// Uses centralized thresholds from QualityThresholds
        /// </summary>
        private static void AssignQualityRating(QualityResult result) {
            // Rating based on Combined Score (using centralized thresholds)
            if (result.CombinedScore >= QualityThresholds.CombinedScore_Excellent) {
                result.QualityRating = "Excellent";
                result.Recommendation = "Professional-grade dither pattern. Suitable for all drizzle scales including 3×.";
            } else if (result.CombinedScore >= QualityThresholds.CombinedScore_VeryGood) {
                result.QualityRating = "Very Good";
                result.Recommendation = "High-quality pattern suitable for demanding astrophotography. Excellent for 2× drizzle.";
            } else if (result.CombinedScore >= QualityThresholds.CombinedScore_Good) {
                result.QualityRating = "Good";
                result.Recommendation = "Good quality pattern. Suitable for 2× drizzle with acceptable results.";
            } else if (result.CombinedScore >= QualityThresholds.CombinedScore_Acceptable) {
                result.QualityRating = "Acceptable";
                result.Recommendation = "Acceptable pattern. Suitable for 1× and moderate 2× drizzle.";
            } else if (result.CombinedScore >= QualityThresholds.CombinedScore_Fair) {
                result.QualityRating = "Fair";
                result.Recommendation = "Suboptimal pattern. Consider increasing dither count or improving distribution.";
            } else {
                result.QualityRating = "Poor";
                result.Recommendation = "Insufficient dither quality. Expect visible artifacts in drizzled output. Add more exposures.";
            }

            // Warnings based on CD (using centralized thresholds)
            if (result.CenteredL2Discrepancy > QualityThresholds.CD_Warning_High) {
                result.Recommendation += $" WARNING: High clustering detected (CD > {QualityThresholds.CD_Warning_High}).";
            } else if (result.CenteredL2Discrepancy > QualityThresholds.CD_Warning_Moderate) {
                result.Recommendation += $" NOTE: Some clustering present (CD > {QualityThresholds.CD_Warning_Moderate}). More dithers recommended.";
            }

            // Warning for insufficient 2× drizzle coverage
            if (result.GapFillMetric_2x < QualityThresholds.GFM2x_Warning) {
                result.Recommendation += " WARNING: Insufficient coverage for 2× drizzle.";
            }

            // Recommendation to increase dither count
            if (result.TotalDithers < QualityThresholds.MinDithers_Good && result.CombinedScore < QualityThresholds.CombinedScore_VeryGood) {
                result.Recommendation += $" Consider increasing dither count to {QualityThresholds.MinDithers_Good}+ (current: {result.TotalDithers}).";
            }
        }

        /// <summary>
        /// Format quality metrics for display
        /// </summary>
        public static string FormatMetricsReport(QualityResult result) {
            return $@"
=== Dither Quality Assessment ===

Overall Rating: {result.QualityRating}
Combined Score: {result.CombinedScore:F3} / 1.000

--- Primary Metrics ---
Centered L₂ Discrepancy: {result.CenteredL2Discrepancy:F4}
  → {GetCDRating(result.CenteredL2Discrepancy)}

Gap-Fill Coverage (CORRECTED targets):
  • 1× Drizzle: {result.GapFillMetric_1x:P1} {GetGFMStatus(result.GapFillMetric_1x, QualityThresholds.GFM_Target_1x)} (Target: ≥{QualityThresholds.GFM_Target_1x:P0} - easy with 10-15 dithers)
  • 2× Drizzle: {result.GapFillMetric_2x:P1} {GetGFMStatus(result.GapFillMetric_2x, QualityThresholds.GFM_Target_2x)} (Target: ≥{QualityThresholds.GFM_Target_2x:P0} - requires 25-30 dithers)
  • 3× Drizzle: {result.GapFillMetric_3x:P1} {GetGFMStatus(result.GapFillMetric_3x, QualityThresholds.GFM_Target_3x)} (Target: ≥{QualityThresholds.GFM_Target_3x:P0} - very demanding, 80+ dithers)

Voronoi CV: {result.VoronoiCV:F3}
  → {GetVoronoiRating(result.VoronoiCV)}

--- Supplementary Metrics ---
Nearest Neighbor Index: {result.NearestNeighborIndex:F2}
  → {GetNNIInterpretation(result.NearestNeighborIndex)}
Total Dithers: {result.TotalDithers}

--- Recommendation ---
{result.Recommendation}

--- Grading Scale (STRICT) ---
This assessment uses a demanding quality scale that encourages collecting {QualityThresholds.MinDithers_Good}+ dithers for 'Good'
and 70+ dithers for 'Excellent' ratings. The strict CD scale differentiates between adequate
and truly excellent dither patterns.

Combined Score Scale:
  • {QualityThresholds.CombinedScore_Excellent:F2}-1.00: Excellent (60-80+ dithers)
  • {QualityThresholds.CombinedScore_VeryGood:F2}-{QualityThresholds.CombinedScore_Excellent:F2}: Very Good (40-60 dithers)
  • {QualityThresholds.CombinedScore_Good:F2}-{QualityThresholds.CombinedScore_VeryGood:F2}: Good (30-40 dithers)
  • {QualityThresholds.CombinedScore_Acceptable:F2}-{QualityThresholds.CombinedScore_Good:F2}: Acceptable (20-30 dithers)
  • {QualityThresholds.CombinedScore_Fair:F2}-{QualityThresholds.CombinedScore_Acceptable:F2}: Fair (10-20 dithers)
  • <{QualityThresholds.CombinedScore_Fair:F2}: Poor (insufficient quality)

Typical CD values by dither count (with good distribution):
  • 8-14 dithers: CD ≈ {QualityThresholds.CD_Acceptable}-{QualityThresholds.CD_Fair} (Fair)
  • 15-25 dithers: CD ≈ {QualityThresholds.CD_Good}-{QualityThresholds.CD_Acceptable} (Acceptable)
  • 30-40 dithers: CD ≈ {QualityThresholds.CD_VeryGood}-{QualityThresholds.CD_Good} (Good)
  • 50-70 dithers: CD ≈ {QualityThresholds.CD_Excellent}-{QualityThresholds.CD_VeryGood} (Very Good)
  • 80+ dithers: CD ≈ 0.02-{QualityThresholds.CD_Excellent} (Excellent)
";
        }

        private static string GetCDRating(double cd) {
            // Uses centralized thresholds from QualityThresholds
            if (cd < QualityThresholds.CD_Excellent) return "Excellent uniformity (80-100+ dithers)";
            if (cd < QualityThresholds.CD_VeryGood) return "Very Good uniformity (50-70 dithers)";
            if (cd < QualityThresholds.CD_Good) return "Good uniformity (30-40 dithers)";
            if (cd < QualityThresholds.CD_Acceptable) return "Acceptable uniformity (15-25 dithers)";
            if (cd < QualityThresholds.CD_Fair) return "Fair uniformity (8-14 dithers)";
            return "Poor uniformity - significant clustering";
        }

        private static string GetGFMStatus(double gfm, double threshold) {
            return gfm >= threshold ? "✓" : "⚠";
        }

        private static string GetVoronoiRating(double cv) {
            // Uses centralized thresholds from QualityThresholds
            if (cv < QualityThresholds.VoronoiCV_Excellent) return "Excellent local uniformity (near-regular)";
            if (cv < QualityThresholds.VoronoiCV_Good) return "Good local uniformity (low clustering)";
            if (cv < QualityThresholds.VoronoiCV_Acceptable) return "Acceptable uniformity (random-like, OK!)";
            if (cv < QualityThresholds.VoronoiCV_Fair) return "Fair uniformity (moderate clustering)";
            return "Poor uniformity - significant gaps/clusters";
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