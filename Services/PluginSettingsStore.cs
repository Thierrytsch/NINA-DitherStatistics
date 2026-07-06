using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// Encapsulates the plain-text settings files under
    /// %LocalAppData%\NINA\DitherStatistics\. File formats are byte-identical
    /// to the pre-refactor VM code; only the load/save mechanics moved here.
    /// </summary>
    public class PluginSettingsStore {
        public const string QualityAssessmentFileName = "settings.txt";
        public const string OptimizerFileName = "optimizer_settings.txt";
        public const string PersistenceFileName = "persistence_settings.txt";
        public const string MultiProfileFileName = "multiprofile_settings.txt";
        public const string QualityMetricsFileName = "quality_settings.txt";
        public const string ProfileListFileName = "profiles_list.txt";

        private readonly string baseDirectory;

        public PluginSettingsStore(string baseDirectory = null) {
            this.baseDirectory = baseDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA", "DitherStatistics");
        }

        private string PathFor(string fileName) => Path.Combine(baseDirectory, fileName);

        private static void EnsureDirectory(string filePath) {
            var directory = Path.GetDirectoryName(filePath);
            if (!Directory.Exists(directory)) {
                Directory.CreateDirectory(directory);
            }
        }

        // ---- settings.txt / optimizer_settings.txt / persistence_settings.txt / multiprofile_settings.txt ----
        // Content is a single bool.ToString(). Returns null when the file is missing or unparsable.

        public bool? ReadBool(string fileName) {
            var filePath = PathFor(fileName);
            if (File.Exists(filePath)) {
                var content = File.ReadAllText(filePath).Trim();
                if (bool.TryParse(content, out bool value)) {
                    return value;
                }
            }
            return null;
        }

        public void WriteBool(string fileName, bool value) {
            var filePath = PathFor(fileName);
            EnsureDirectory(filePath);
            File.WriteAllText(filePath, value.ToString());
        }

        // ---- quality_settings.txt ----
        // "key=value" lines, InvariantCulture. Returns null when the file is missing;
        // otherwise returns the values found for the known keys (null per key if absent/unparsable).

        public (double? Pixfrac, double? ScaleRatioOverride)? ReadQualityMetricSettings() {
            var filePath = PathFor(QualityMetricsFileName);
            if (!File.Exists(filePath)) return null;

            double? pixfrac = null;
            double? scaleRatioOverride = null;

            foreach (var line in File.ReadAllLines(filePath)) {
                var parts = line.Split('=');
                if (parts.Length != 2) continue;
                if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) continue;

                switch (parts[0].Trim()) {
                    case "pixfrac":
                        pixfrac = value;
                        break;
                    case "scaleRatioOverride":
                        scaleRatioOverride = value;
                        break;
                }
            }

            return (pixfrac, scaleRatioOverride);
        }

        public void WriteQualityMetricSettings(double pixfrac, double scaleRatioOverride) {
            var filePath = PathFor(QualityMetricsFileName);
            EnsureDirectory(filePath);
            var ci = CultureInfo.InvariantCulture;
            File.WriteAllText(filePath, $"pixfrac={pixfrac.ToString(ci)}\nscaleRatioOverride={scaleRatioOverride.ToString(ci)}");
        }

        // ---- profiles_list.txt ----
        // Line 1 = selected profile, remaining lines = all profile names.
        // Returns (defaultSelected, empty list) when the file is missing or empty.

        public (string Selected, List<string> Names) ReadProfileList(string defaultSelected) {
            var filePath = PathFor(ProfileListFileName);
            if (File.Exists(filePath)) {
                var lines = File.ReadAllLines(filePath)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .ToList();
                if (lines.Count > 0) {
                    return (lines[0], lines.Skip(1).ToList());
                }
            }
            return (defaultSelected, new List<string>());
        }

        public void WriteProfileList(string selected, IEnumerable<string> names) {
            var filePath = PathFor(ProfileListFileName);
            EnsureDirectory(filePath);
            var lines = new List<string> { selected };
            lines.AddRange(names);
            File.WriteAllLines(filePath, lines);
        }
    }
}
