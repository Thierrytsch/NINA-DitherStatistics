using System.Collections.Generic;
using Xunit;

namespace DitherStatistics.Plugin.Tests {
    public class StatisticsTests {
        [Fact]
        public void CalculateAverage_EmptyList_ReturnsZero() {
            Assert.Equal(0, DitherStatistics.CalculateAverage(new List<double>()));
        }

        [Fact]
        public void CalculateAverage_SingleElement_ReturnsElement() {
            Assert.Equal(5.0, DitherStatistics.CalculateAverage(new List<double> { 5.0 }));
        }

        [Fact]
        public void CalculateAverage_MultipleElements_ReturnsMean() {
            Assert.Equal(3.0, DitherStatistics.CalculateAverage(new List<double> { 1, 2, 3, 4, 5 }));
        }

        [Fact]
        public void CalculateMedian_EmptyList_ReturnsZero() {
            Assert.Equal(0, DitherStatistics.CalculateMedian(new List<double>()));
        }

        [Fact]
        public void CalculateMedian_SingleElement_ReturnsElement() {
            Assert.Equal(7.0, DitherStatistics.CalculateMedian(new List<double> { 7.0 }));
        }

        [Fact]
        public void CalculateMedian_OddCount_ReturnsMiddleElement() {
            Assert.Equal(3.0, DitherStatistics.CalculateMedian(new List<double> { 5, 1, 3, 2, 4 }));
        }

        [Fact]
        public void CalculateMedian_EvenCount_ReturnsAverageOfMiddleTwo() {
            Assert.Equal(2.5, DitherStatistics.CalculateMedian(new List<double> { 1, 2, 3, 4 }));
        }

        [Fact]
        public void CalculateQuantile_EmptyList_ReturnsZero() {
            Assert.Equal(0, DitherStatistics.CalculateQuantile(new List<double>(), 0.5));
        }

        [Fact]
        public void CalculateQuantile_SingleElement_ReturnsElementRegardlessOfQuantile() {
            Assert.Equal(9.0, DitherStatistics.CalculateQuantile(new List<double> { 9.0 }, 0.9));
        }

        [Fact]
        public void CalculateQuantile_Median_MatchesCalculateMedian_OddCount() {
            var values = new List<double> { 1, 2, 3, 4, 5 };
            Assert.Equal(3.0, DitherStatistics.CalculateQuantile(values, 0.5));
        }

        [Fact]
        public void CalculateQuantile_Interpolates_BetweenNeighboringRanks() {
            // sorted [1,2,3,4,5], q=0.9 -> position=(5-1)*0.9=3.6 -> lerp(values[3]=4, values[4]=5, 0.6)=4.6
            var values = new List<double> { 3, 1, 5, 2, 4 };
            Assert.Equal(4.6, DitherStatistics.CalculateQuantile(values, 0.9), 10);
        }

        [Fact]
        public void CalculateQuantile_ZeroQuantile_ReturnsMinimum() {
            Assert.Equal(1.0, DitherStatistics.CalculateQuantile(new List<double> { 3, 1, 5, 2, 4 }, 0.0));
        }

        [Fact]
        public void CalculateQuantile_OneQuantile_ReturnsMaximum() {
            Assert.Equal(5.0, DitherStatistics.CalculateQuantile(new List<double> { 3, 1, 5, 2, 4 }, 1.0));
        }

        [Fact]
        public void CalculateStdDev_EmptyList_ReturnsZero() {
            Assert.Equal(0, DitherStatistics.CalculateStdDev(new List<double>()));
        }

        [Fact]
        public void CalculateStdDev_SingleElement_ReturnsZero() {
            Assert.Equal(0, DitherStatistics.CalculateStdDev(new List<double> { 42.0 }));
        }

        [Fact]
        public void CalculateStdDev_KnownDataset_ReturnsPopulationStdDev() {
            // classic textbook example: population stddev of [2,4,4,4,5,5,7,9] is exactly 2
            var values = new List<double> { 2, 4, 4, 4, 5, 5, 7, 9 };
            Assert.Equal(2.0, DitherStatistics.CalculateStdDev(values), 10);
        }

        [Fact]
        public void Aggregate_NoEvents_ReturnsAllZeros() {
            var summary = DitherStatistics.Aggregate(new List<DitherEvent>(), new List<PixelShiftPoint>());

            Assert.Equal(0, summary.TotalDithers);
            Assert.Equal(0, summary.SuccessfulDithers);
            Assert.Equal(0, summary.SuccessRate);
            Assert.Equal(0, summary.AverageSettleTime);
            Assert.Equal(0, summary.TotalDriftX);
            Assert.Equal(0, summary.TotalDriftY);
        }

        [Fact]
        public void Aggregate_MixedSuccessAndFailure_OnlyCountsSuccessfulSettleTimes() {
            var events = new List<DitherEvent> {
                new DitherEvent { Success = true, SettleTime = 2.0 },
                new DitherEvent { Success = true, SettleTime = 4.0 },
                new DitherEvent { Success = false, SettleTime = 99.0 },
                new DitherEvent { Success = true, SettleTime = null }, // excluded: no SettleTime
            };

            var summary = DitherStatistics.Aggregate(events, new List<PixelShiftPoint>());

            Assert.Equal(4, summary.TotalDithers);
            Assert.Equal(2, summary.SuccessfulDithers);
            Assert.Equal(50.0, summary.SuccessRate);
            Assert.Equal(3.0, summary.AverageSettleTime);
            Assert.Equal(3.0, summary.MedianSettleTime);
            Assert.Equal(2.0, summary.MinSettleTime);
            Assert.Equal(4.0, summary.MaxSettleTime);
        }

        [Fact]
        public void Aggregate_PixelShiftValues_DriftIsRangeOfCumulativePositions() {
            var points = new List<PixelShiftPoint> {
                new PixelShiftPoint(0, 0, 0, 0),
                new PixelShiftPoint(5, -3, 5, -3),
                new PixelShiftPoint(-2, 4, -7, 7),
            };

            var summary = DitherStatistics.Aggregate(new List<DitherEvent>(), points);

            Assert.Equal(7.0, summary.TotalDriftX); // max(5) - min(-2)
            Assert.Equal(7.0, summary.TotalDriftY); // max(4) - min(-3)
        }
    }
}
