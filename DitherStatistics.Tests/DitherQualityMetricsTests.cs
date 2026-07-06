using System.Collections.Generic;
using Xunit;

namespace DitherStatistics.Plugin.Tests {
    /// <summary>
    /// Golden-value tests for DitherQualityMetrics.CalculateQualityMetrics: the expected numbers were
    /// captured from the current (pre-refactor) implementation against these fixed point sets and are
    /// frozen here as a regression net (see REFACTORING_PLAN.md Etappe 2). A failing assertion means the
    /// math changed, not necessarily that it's wrong - if the change is intentional, recapture the
    /// golden values, don't just loosen the tolerance.
    /// </summary>
    public class DitherQualityMetricsTests {
        private static readonly List<(double X, double Y)> EightPointPattern = new List<(double, double)> {
            (0.1, 0.1), (1.4, 0.6), (2.2, 1.3), (0.7, 2.1),
            (1.9, 0.4), (0.3, 1.7), (2.6, 2.4), (1.1, 1.0)
        };

        [Fact]
        public void CalculateQualityMetrics_InsufficientData_ReturnsInsufficientDataRating() {
            var result = DitherQualityMetrics.CalculateQualityMetrics(new List<(double X, double Y)> {
                (0, 0), (1, 1), (2, 2)
            });

            Assert.Equal("Insufficient Data", result.QualityRating);
        }

        [Fact]
        public void CalculateQualityMetrics_NullInput_ReturnsInsufficientDataRating() {
            var result = DitherQualityMetrics.CalculateQualityMetrics(null);
            Assert.Equal("Insufficient Data", result.QualityRating);
        }

        [Fact]
        public void CalculateQualityMetrics_EightPointPattern_MatchesGoldenValues() {
            var result = DitherQualityMetrics.CalculateQualityMetrics(EightPointPattern);

            Assert.Equal(8, result.TotalDithers);
            Assert.Equal(1.0, result.PixelScaleRatio, 10);
            Assert.Equal(0.6, result.Pixfrac, 10);

            Assert.Equal(0.24555775514349076, result.CenteredL2Discrepancy, 10);
            Assert.Equal(1.0, result.GapFillMetric_1x, 10);
            Assert.Equal(0.69444444444444431, result.GapFillMetric_2x, 10);
            Assert.Equal(0.60416666666666641, result.GapFillMetric_3x, 10);
            Assert.Equal(1.1352740713516716, result.NearestNeighborIndex, 10);
            Assert.Equal(0.10548335184279746, result.DriftRatio, 10);
            Assert.Equal(3.3970575502926055, result.PatternSpreadPx, 10);
            Assert.Equal(0.25583861460333218, result.CombinedScore, 10);
            Assert.Equal("Poor", result.QualityRating);
        }

        [Fact]
        public void CalculateQualityMetrics_PixelScaleRatioZeroOrNegative_FallsBackToOne() {
            var withZero = DitherQualityMetrics.CalculateQualityMetrics(EightPointPattern, pixelScaleRatio: 0.0);
            var withDefault = DitherQualityMetrics.CalculateQualityMetrics(EightPointPattern, pixelScaleRatio: 1.0);

            Assert.Equal(withDefault.CenteredL2Discrepancy, withZero.CenteredL2Discrepancy, 10);
            Assert.Equal(1.0, withZero.PixelScaleRatio, 10);
        }

        [Fact]
        public void CalculateQualityMetrics_PixfracOutOfRange_FallsBackToDefault() {
            var withInvalid = DitherQualityMetrics.CalculateQualityMetrics(EightPointPattern, pixfrac: 1.5);
            var withDefault = DitherQualityMetrics.CalculateQualityMetrics(EightPointPattern, pixfrac: 0.6);

            Assert.Equal(withDefault.GapFillMetric_2x, withInvalid.GapFillMetric_2x, 10);
            Assert.Equal(0.6, withInvalid.Pixfrac, 10);
        }
    }
}
