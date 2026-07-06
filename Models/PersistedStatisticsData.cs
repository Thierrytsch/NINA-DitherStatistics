using System.Collections.Generic;

namespace DitherStatistics.Plugin {
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
}
