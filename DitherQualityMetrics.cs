using System;
using System.Collections.Generic;
using System.Linq;

namespace DitherStatistics.Plugin {

    /// <summary>
    /// Calculates mathematical quality metrics for dither pattern evaluation
    /// Based on discrepancy theory, coverage analysis, and spatial statistics
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

            // Calculate combined score (weighted average)
            double w1 = 0.4; // Global uniformity (CD)
            double w2 = 0.4; // Drizzle-specific coverage (GFM)
            double w3 = 0.2; // Local uniformity (Voronoi CV)

            // Normalize metrics to [0, 1] range (higher = better)
            double cdScore = Math.Max(0, 1.0 - result.CenteredL2Discrepancy / 0.15);
            double gfmScore = result.GapFillMetric_2x; // Already in [0, 1]
            double voronoiScore = Math.Max(0, 1.0 - result.VoronoiCV / 0.5);

            result.CombinedScore = w1 * cdScore + w2 * gfmScore + w3 * voronoiScore;

            // Assign quality rating and recommendation
            AssignQualityRating(result);

            return result;
        }

        /// <summary>
        /// Calculate Centered L₂ Discrepancy - measures uniformity of distribution
        /// Lower values indicate better uniformity (< 0.02 excellent, < 0.05 good, < 0.08 acceptable)
        /// </summary>
        private static double CalculateCenteredL2Discrepancy(List<(double X, double Y)> positions) {
            int N = positions.Count;

            // Extract fractional positions (mod 1.0 for toroidal topology)
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
        /// CORRECTED VERSION: Higher scale requires MORE dithers for good coverage
        /// Returns value in [0, 1] where 1.0 = perfect coverage, no gaps
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

            // Effective drop area coverage per dither (normalized to 1 output pixel area)
            // At scale=1: each dither covers ~pixfrac² of the area
            // At scale=2: each dither covers ~(pixfrac/2)² = pixfrac²/4 of the area
            // At scale=3: each dither covers ~(pixfrac/3)² = pixfrac²/9 of the area
            double effectiveDropArea = dropSize * dropSize;

            // Total coverage = N dithers × effectiveDropArea, normalized by output area
            // We need to cover outputPixelCount pixels, each drop covers effectiveDropArea
            double rawCoverage = (N * effectiveDropArea) / outputPixelCount;

            // Apply uniformity penalty based on Centered L2 Discrepancy
            double cd = CalculateCenteredL2Discrepancy(positions);

            // Poor uniformity (high CD) means many gaps despite sufficient N
            // CD < 0.02: excellent (penalty 0%)
            // CD ~ 0.05: good (penalty ~5%)
            // CD ~ 0.10: poor (penalty ~15%)
            // CD > 0.15: terrible (penalty ~25%)
            double uniformityPenalty = Math.Min(0.25, cd * 1.5);

            // Effective coverage after uniformity penalty
            double effectiveCoverage = rawCoverage * (1.0 - uniformityPenalty);

            // Normalize to [0, 1] based on scale-specific requirements
            // These requirements reflect real-world drizzle needs:
            double requiredCoverage = scale switch {
                1.0 => 1.0,   // 1× drizzle: need ~100% of area covered (easy with few dithers)
                2.0 => 0.95,  // 2× drizzle: can tolerate 5% gaps (need more dithers)
                3.0 => 0.90,  // 3× drizzle: can tolerate 10% gaps (need many dithers)
                _ => 0.95
            };

            // Final score: how well we meet the requirement
            double score = effectiveCoverage / requiredCoverage;

            return Math.Min(1.0, Math.Max(0.0, score));
        }

        // ============================================================================
        // THEORETICAL MINIMUM DITHERS for 100% coverage
        // (assuming perfect uniform distribution, CD = 0)
        // ============================================================================
        /*
        For perfect coverage with drops of size d = pixfrac/scale:

        1× Drizzle (scale=1.0, pixfrac=1.0):
          - drop area: 1.0² = 1.0
          - output area: 1.0
          - minimum N ≈ 1.0 / 1.0 = 1 dither (but need ~4 for redundancy)

        2× Drizzle (scale=2.0, pixfrac=0.6):
          - drop area: 0.3² = 0.09
          - output area: 4.0
          - minimum N ≈ 4.0 / 0.09 = 44 dithers!
          - practical with CD penalty: 25-30 well-distributed dithers

        3× Drizzle (scale=3.0, pixfrac=0.5):
          - drop area: 0.17² = 0.029
          - output area: 9.0
          - minimum N ≈ 9.0 / 0.029 = 310 dithers (!)
          - practical with CD penalty: 80-120 well-distributed dithers

        This is why 3× drizzle is rarely practical!
        */

        /// <summary>
        /// Calculate Voronoi Cell Area Coefficient of Variation on SUB-PIXEL POSITIONS
        /// CORRECTED: Uses fractional positions (mod 1) with toroidal distance
        /// Measures local sub-pixel distribution uniformity
        /// Lower values indicate better uniformity (< 0.2 excellent, < 0.3 good, < 0.5 acceptable)
        /// </summary>
        private static double CalculateVoronoiCV(List<(double X, double Y)> positions) {
            int N = positions.Count;
            if (N < 3) return double.MaxValue;

            // CRITICAL FIX: Use fractional sub-pixel positions (mod 1), not cumulative positions!
            var fracPositions = positions.Select(p => (
                X: Math.Abs(p.X) % 1.0,
                Y: Math.Abs(p.Y) % 1.0
            )).ToList();

            // Calculate nearest neighbor distances with TOROIDAL distance (periodic boundaries)
            var distances = new List<double>();
            for (int i = 0; i < N; i++) {
                double minDist = double.MaxValue;
                for (int j = 0; j < N; j++) {
                    if (i == j) continue;

                    // Calculate distance with toroidal wrapping
                    // Positions near edges (e.g., 0.95 and 0.05) are actually close!
                    double dx = Math.Abs(fracPositions[i].X - fracPositions[j].X);
                    double dy = Math.Abs(fracPositions[i].Y - fracPositions[j].Y);

                    // Apply toroidal distance (wrap around at boundaries)
                    if (dx > 0.5) dx = 1.0 - dx;
                    if (dy > 0.5) dy = 1.0 - dy;

                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist < minDist) minDist = dist;
                }
                distances.Add(minDist);
            }

            // CV of nearest neighbor distances approximates Voronoi cell area CV
            // This is a computational shortcut - true Voronoi CV requires full tessellation
            double mean = distances.Average();
            double variance = distances.Select(d => Math.Pow(d - mean, 2)).Average();
            double stdDev = Math.Sqrt(variance);

            return mean > 0 ? stdDev / mean : double.MaxValue;
        }

        // ============================================================================
        // EXPECTED VALUES for different distributions:
        // ============================================================================
        /*
        In unit square [0,1)² with N points:

        PERFECT REGULAR GRID (e.g., 5×5 = 25 points):
          - All NN distances identical: d = 0.2
          - StdDev = 0
          - CV = 0 (theoretical minimum, impossible in practice)

        GOOD QUASI-RANDOM (e.g., Sobol, Halton):
          - NN distances fairly uniform: d ≈ 0.14-0.16 for N=25
          - CV ≈ 0.15-0.25 (low variance = good uniformity)

        PURE RANDOM:
          - NN distances have moderate variance
          - CV ≈ 0.35-0.45 (higher variance)

        CLUSTERED:
          - Some points very close, others far apart
          - CV > 0.6 (high variance = poor uniformity)

        For DRIZZLE QUALITY:
          < 0.2:  Excellent local uniformity (near-regular)
          < 0.3:  Good local uniformity (low-discrepancy)
          < 0.5:  Acceptable (random-like)
          > 0.6:  Poor (significant clustering/gaps)

        NOTE: Voronoi CV complements Centered L₂ Discrepancy:
        - CD measures GLOBAL uniformity
        - Voronoi CV measures LOCAL uniformity (neighbor distances)
        - Both should be low for optimal drizzle quality
        */

        /// <summary>
        /// Calculate Nearest Neighbor Index (Clark-Evans) on SUB-PIXEL POSITIONS
        /// CORRECTED: Uses fractional positions (mod 1) instead of cumulative positions
        /// This measures sub-pixel uniformity, which is what matters for drizzle quality
        /// R > 1.5 indicates regular/uniform sub-pixel distribution (excellent for drizzle)
        /// R ~ 1.0 indicates random sub-pixel distribution (good for drizzle)
        /// R < 0.8 indicates clustered sub-pixel distribution (poor for drizzle)
        /// </summary>
        private static double CalculateNearestNeighborIndex(List<(double X, double Y)> positions) {
            int N = positions.Count;
            if (N < 2) return 1.0;

            // CRITICAL FIX: Use fractional sub-pixel positions (mod 1), not cumulative positions!
            // For drizzle quality, we care about sub-pixel coverage uniformity
            var fracPositions = positions.Select(p => (
                X: Math.Abs(p.X) % 1.0,
                Y: Math.Abs(p.Y) % 1.0
            )).ToList();

            // Calculate observed mean nearest neighbor distance in sub-pixel space
            double sumMinDist = 0.0;
            for (int i = 0; i < N; i++) {
                double minDist = double.MaxValue;
                for (int j = 0; j < N; j++) {
                    if (i == j) continue;

                    // Calculate distance with toroidal wrapping (periodic boundaries)
                    // This accounts for positions near edges (e.g., 0.95 and 0.05 are actually close)
                    double dx = Math.Abs(fracPositions[i].X - fracPositions[j].X);
                    double dy = Math.Abs(fracPositions[i].Y - fracPositions[j].Y);

                    // Apply toroidal distance (wrap around at boundaries)
                    if (dx > 0.5) dx = 1.0 - dx;
                    if (dy > 0.5) dy = 1.0 - dy;

                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist < minDist) minDist = dist;
                }
                sumMinDist += minDist;
            }
            double observedMean = sumMinDist / N;

            // Calculate expected mean for random distribution in unit square [0,1)²
            // For a random distribution in area A with density ρ = N/A:
            // E[nearest neighbor distance] = 0.5 / sqrt(ρ)
            // 
            // In our case: A = 1.0 (unit square), ρ = N / 1.0 = N
            // Therefore: E[r] = 0.5 / sqrt(N)
            double density = N;  // In unit square, density = N / 1.0 = N
            double expectedMean = 0.5 / Math.Sqrt(density);

            // Clark-Evans NNI = observed / expected
            return expectedMean > 0 ? observedMean / expectedMean : 1.0;
        }

        // ============================================================================
        // EXPECTED VALUES for different N:
        // ============================================================================
        /*
        With N dithers uniformly distributed in [0,1)²:

        N = 4:   E[r] = 0.5/√4  = 0.25   → Perfect grid: NNI = 0.5/0.25 = 2.0
        N = 9:   E[r] = 0.5/√9  = 0.167  → Perfect grid: NNI = 0.33/0.167 = 2.0
        N = 16:  E[r] = 0.5/√16 = 0.125  → Perfect grid: NNI = 0.25/0.125 = 2.0
        N = 25:  E[r] = 0.5/√25 = 0.1    → Perfect grid: NNI = 0.2/0.1 = 2.0
        N = 50:  E[r] = 0.5/√50 = 0.071  → Random: NNI ≈ 1.0
        N = 100: E[r] = 0.5/√100= 0.05   → Random: NNI ≈ 1.0

        Interpretation for DRIZZLE QUALITY:
        - NNI > 1.5:  Excellent sub-pixel uniformity (almost regular grid)
        - NNI > 1.2:  Good sub-pixel uniformity
        - NNI > 0.9:  Acceptable sub-pixel coverage (random-like)
        - NNI < 0.8:  Poor - sub-pixels are clustered, gaps likely

        NOTE: For drizzle, even NNI ≈ 1.0 (random) is acceptable!
        We don't need a perfect grid, just good coverage without major gaps.
        */

        /// <summary>
        /// Assign quality rating and recommendation based on combined score
        /// </summary>
        private static void AssignQualityRating(QualityResult result) {
            if (result.CombinedScore >= 0.85) {
                result.QualityRating = "Excellent";
                result.Recommendation = "Professional-grade dither pattern. Suitable for all drizzle scales including 3×.";
            } else if (result.CombinedScore >= 0.75) {
                result.QualityRating = "Good";
                result.Recommendation = "High-quality pattern suitable for demanding astrophotography. Recommended for 2× drizzle.";
            } else if (result.CombinedScore >= 0.60) {
                result.QualityRating = "Acceptable";
                result.Recommendation = "Standard quality pattern. Suitable for 1× and moderate 2× drizzle.";
            } else if (result.CombinedScore >= 0.40) {
                result.QualityRating = "Fair";
                result.Recommendation = "Suboptimal pattern. Consider increasing dither count or improving distribution.";
            } else {
                result.QualityRating = "Poor";
                result.Recommendation = "Insufficient dither quality. Expect visible artifacts in drizzled output. Add more exposures.";
            }

            // Add specific warnings
            if (result.CenteredL2Discrepancy > 0.10) {
                result.Recommendation += " WARNING: High clustering detected (CD > 0.10).";
            }
            if (result.GapFillMetric_2x < 0.90) {
                result.Recommendation += " WARNING: Insufficient coverage for 2× drizzle.";
            }
            if (result.TotalDithers < 20 && result.CombinedScore < 0.75) {
                result.Recommendation += $" Consider increasing dither count (current: {result.TotalDithers}).";
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

Gap-Fill Coverage:
  • 1× Drizzle: {result.GapFillMetric_1x:P1} {GetGFMStatus(result.GapFillMetric_1x, 0.98)}
  • 2× Drizzle: {result.GapFillMetric_2x:P1} {GetGFMStatus(result.GapFillMetric_2x, 0.95)}
  • 3× Drizzle: {result.GapFillMetric_3x:P1} {GetGFMStatus(result.GapFillMetric_3x, 0.90)}

Voronoi CV: {result.VoronoiCV:F3}
  → {GetVoronoiRating(result.VoronoiCV)}

--- Supplementary Metrics ---
Nearest Neighbor Index: {result.NearestNeighborIndex:F2}
  → {GetNNIInterpretation(result.NearestNeighborIndex)}
Total Dithers: {result.TotalDithers}

--- Recommendation ---
{result.Recommendation}
";
        }

        private static string GetCDRating(double cd) {
            if (cd < 0.02) return "Excellent uniformity";
            if (cd < 0.05) return "Good uniformity";
            if (cd < 0.08) return "Acceptable uniformity";
            if (cd < 0.10) return "Fair uniformity";
            return "Poor uniformity - clustering detected";
        }

        private static string GetGFMStatus(double gfm, double threshold) {
            return gfm >= threshold ? "✓" : "⚠";
        }

        private static string GetVoronoiRating(double cv) {
            if (cv < 0.2) return "Excellent local uniformity";
            if (cv < 0.3) return "Good local uniformity";
            if (cv < 0.5) return "Acceptable local uniformity";
            return "Poor local uniformity - significant gaps/clusters";
        }

        private static string GetNNIInterpretation(double nni) {
            // UPDATED interpretation for sub-pixel NNI (not cumulative position NNI)
            if (nni > 1.5) return "Excellent sub-pixel uniformity (regular)";
            if (nni > 1.2) return "Good sub-pixel uniformity";
            if (nni > 0.9) return "Acceptable coverage (random-like)";
            if (nni > 0.7) return "Fair coverage with some clustering";
            return "Poor coverage - significant clustering";
        }
    }
}
