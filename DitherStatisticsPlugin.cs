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
    /// Dateiname: DitherStatisticsPlugin.cs
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class DitherStatisticsPlugin : PluginBase {
        [ImportingConstructor]
        public DitherStatisticsPlugin(IProfileService profileService) {
            // CRITICAL: Register assembly resolver BEFORE loading DataTemplates
            RegisterAssemblyResolver();

            // Load DataTemplates for UI rendering
            LoadDataTemplates();
        }

        private void RegisterAssemblyResolver() {
            try {
                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
                Logger.Info("Assembly resolver registered for DitherStatistics plugin");
            } catch (Exception ex) {
                Logger.Error($"Failed to register assembly resolver: {ex.Message}");
            }
        }

        private Assembly OnAssemblyResolve(object sender, ResolveEventArgs args) {
            var assemblyName = new AssemblyName(args.Name);

            // Only handle LiveCharts assemblies
            if (!assemblyName.Name.StartsWith("LiveCharts")) {
                return null;
            }

            try {
                // Get plugin directory
                var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var assemblyPath = Path.Combine(pluginDir, assemblyName.Name + ".dll");

                if (File.Exists(assemblyPath)) {
                    Logger.Info($"Loading assembly from plugin directory: {assemblyPath}");
                    return Assembly.LoadFrom(assemblyPath);
                }
            } catch (Exception ex) {
                Logger.Error($"Failed to resolve assembly {assemblyName.Name}: {ex.Message}");
            }

            return null;
        }

        private void LoadDataTemplates() {
            try {
                var resourceDict = new ResourceDictionary {
                    Source = new Uri("pack://application:,,,/ThierryTschanz.NINA.Ditherstatistics;component/DitherStatisticsDataTemplates.xaml", UriKind.Absolute)
                };
                Application.Current?.Resources.MergedDictionaries.Add(resourceDict);
                Logger.Info("DitherStatistics DataTemplates loaded successfully");
            } catch (Exception ex) {
                Logger.Error($"Failed to load DataTemplates: {ex.Message}");
            }
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

        public DitherEvent() {
            StartTime = DateTime.Now;
            Success = false;
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

        public PixelShiftPoint(double cumulativeX, double cumulativeY, double deltaX, double deltaY) {
            X = cumulativeX;
            Y = cumulativeY;
            DeltaX = deltaX;
            DeltaY = deltaY;
        }
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
