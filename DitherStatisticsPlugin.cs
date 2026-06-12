using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using NINA.Core.Utility;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// Main plugin class - Entry point for the NINA plugin system
    /// Migrated from LiveCharts to ScottPlot
    /// Filename: DitherStatisticsPlugin.cs
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class DitherStatisticsPlugin : PluginBase {
        [ImportingConstructor]
        public DitherStatisticsPlugin(IProfileService profileService) {
            // DataTemplates are now loaded in the ViewModel constructor
            // This is necessary because the assembly is not fully initialized during plugin load
        }
    }

    /// <summary>
    /// Represents a single dither event with timing and position information
    /// </summary>
    public class DitherEvent {
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public double? SettleTime { get; set; }
        public double? PixelShiftX { get; set; }
        public double? PixelShiftY { get; set; }
        public bool Success { get; set; }

        // Cumulative position tracking for CSV export
        public double CumulativeX { get; set; }
        public double CumulativeY { get; set; }

        public DitherEvent() {
            StartTime = DateTime.Now;
            Success = false;
            CumulativeX = 0.0;
            CumulativeY = 0.0;
        }
    }

    /// <summary>
    /// Model for pixel shift chart points
    /// X/Y = Cumulative absolute position (for chart display)
    /// DeltaX/DeltaY = Individual shift (for tooltip)
    /// </summary>
    public class PixelShiftPoint {
        // Chart position (cumulative)
        public double X { get; set; }
        public double Y { get; set; }

        // Delta values for tooltip
        public double DeltaX { get; set; }
        public double DeltaY { get; set; }

        // Parameterless constructor required for JSON deserialization (statistics persistence)
        public PixelShiftPoint() {
        }

        public PixelShiftPoint(double cumulativeX, double cumulativeY, double deltaX, double deltaY) {
            X = cumulativeX;
            Y = cumulativeY;
            DeltaX = deltaX;
            DeltaY = deltaY;
        }
    }

    /// <summary>
    /// Snapshot of all statistics data for multi-session persistence
    /// Serialized to %LocalAppData%\NINA\DitherStatistics\statistics_data.json
    /// </summary>
    public class PersistedStatisticsData {
        public List<DitherEvent> DitherEvents { get; set; } = new List<DitherEvent>();
        public List<double> SettleTimeValues { get; set; } = new List<double>();
        public List<PixelShiftPoint> PixelShiftValues { get; set; } = new List<PixelShiftPoint>();
        public double CumulativeX { get; set; }
        public double CumulativeY { get; set; }

        // Dither Settings Optimizer state (null in files written by older versions)
        public DitherAnalysisSnapshot OptimizerData { get; set; }
        public DitherSettingsRecommendation Recommendation { get; set; }
    }

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
