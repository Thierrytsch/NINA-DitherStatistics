using System;

namespace DitherStatistics.Plugin {
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
}
