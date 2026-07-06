using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// CSV/report export to Documents\N.I.N.A\DitherStatistics. I/O exceptions
    /// propagate; the VM keeps the try/catch and logging around these calls.
    /// </summary>
    public static class ExportService {
        private static string ExportDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "N.I.N.A",
            "DitherStatistics");

        public static string ExportDitherEventsCsv(IReadOnlyList<DitherEvent> events) {
            string filename = $"DitherEvents_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            string path = Path.Combine(ExportDirectory, filename);
            Directory.CreateDirectory(ExportDirectory);

            var csv = new StringBuilder();
            csv.AppendLine("DitherNumber,StartTime,EndTime,PixelShiftX,PixelShiftY,CumulativeX,CumulativeY,SettleTime,Success");
            for (int i = 0; i < events.Count; i++) {
                var evt = events[i];
                csv.AppendLine($"{i + 1}," +
                    $"{evt.StartTime:yyyy-MM-dd HH:mm:ss}," +
                    $"{evt.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A"}," +
                    $"{evt.PixelShiftX?.ToString("F4") ?? "N/A"}," +
                    $"{evt.PixelShiftY?.ToString("F4") ?? "N/A"}," +
                    $"{evt.CumulativeX:F4}," +
                    $"{evt.CumulativeY:F4}," +
                    $"{evt.SettleTime?.ToString("F2") ?? "N/A"}," +
                    $"{evt.Success}");
            }

            File.WriteAllText(path, csv.ToString());
            return path;
        }

        public static string ExportQualityReport(DitherQualityMetrics.QualityResult result) {
            string filename = $"DitherQuality_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string path = Path.Combine(ExportDirectory, filename);
            Directory.CreateDirectory(ExportDirectory);

            string report = DitherQualityMetrics.FormatMetricsReport(result);
            File.WriteAllText(path, report);
            return path;
        }
    }
}
