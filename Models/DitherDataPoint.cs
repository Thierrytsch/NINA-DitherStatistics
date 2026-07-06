using System;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// Dither analysis tracking. Public with settable properties for JSON
    /// serialization (statistics persistence)
    /// </summary>
    public class DitherDataPoint {
        public int DitherSeriesId { get; set; }  // Identifies which dither event this point belongs to
        public double DX { get; set; }
        public double DY { get; set; }
        public double PairRMS { get; set; }
        public double Exposure { get; set; }  // Guide exposure time in seconds
        public DateTime Timestamp { get; set; }
    }
}
