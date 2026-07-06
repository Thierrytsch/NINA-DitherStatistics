using System;
using System.Collections.Generic;
using System.Linq;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// Statistical calculations for dither events
    /// </summary>
    public static class DitherStatistics {
        public static double CalculateAverage(IEnumerable<double> values) {
            var valueList = new List<double>(values);
            return valueList.Count > 0 ? valueList.Average() : 0;
        }

        public static double CalculateMedian(IEnumerable<double> values) {
            var sortedValues = new List<double>(values);
            sortedValues.Sort();
            int count = sortedValues.Count;
            if (count == 0) return 0;
            if (count % 2 == 1)
                return sortedValues[count / 2];
            return (sortedValues[count / 2 - 1] + sortedValues[count / 2]) / 2.0;
        }

        /// <summary>
        /// Linear-interpolated empirical quantile (q in 0..1) of the given values
        /// </summary>
        public static double CalculateQuantile(IEnumerable<double> values, double q) {
            var sortedValues = new List<double>(values);
            sortedValues.Sort();
            if (sortedValues.Count == 0) return 0;

            double position = (sortedValues.Count - 1) * q;
            int lower = (int)Math.Floor(position);
            int upper = (int)Math.Ceiling(position);
            if (lower == upper) return sortedValues[lower];
            return sortedValues[lower] + (position - lower) * (sortedValues[upper] - sortedValues[lower]);
        }

        public static double CalculateStdDev(IEnumerable<double> values) {
            var valueList = new List<double>(values);
            if (valueList.Count == 0) return 0;

            double avg = valueList.Average();
            double sumSquaredDiff = 0;
            foreach (var value in valueList) {
                sumSquaredDiff += Math.Pow(value - avg, 2);
            }
            return Math.Sqrt(sumSquaredDiff / valueList.Count);
        }
    }
}
