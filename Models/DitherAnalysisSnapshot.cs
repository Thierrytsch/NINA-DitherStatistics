using System.Collections.Generic;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// Serializable snapshot of the dither optimizer analysis state (multi-session persistence)
    /// </summary>
    public class DitherAnalysisSnapshot {
        public List<DitherDataPoint> DitherData { get; set; } = new List<DitherDataPoint>();
        public int DitherSeriesCounter { get; set; }

        // Per-series metadata; empty in files written by versions before 1.6
        public List<DitherSeriesInfo> SeriesInfos { get; set; } = new List<DitherSeriesInfo>();
    }
}
