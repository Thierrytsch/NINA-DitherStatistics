using System;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// Per-dither-series metadata. Public with settable properties for JSON
    /// serialization (statistics persistence); absent in files written by
    /// versions before 1.6 (analysis then falls back to the first data point
    /// as time zero and the current reference thresholds).
    /// </summary>
    public class DitherSeriesInfo {
        public int DitherSeriesId { get; set; }
        public DateTime DitherEventTime { get; set; }   // GuidingDithered event time (time zero for settle delays)
        public bool SettleReceived { get; set; }        // SettleDone arrived within the collection window
        public bool SettleFailed { get; set; }          // SettleDone.Status != 0 -> excluded from analysis
        public bool StarLost { get; set; }              // StarLost during the window -> excluded from analysis
        public double MeasuredSettleDuration { get; set; }  // seconds from dither to SettleDone, 0 if unknown
        // Reference thresholds captured when the series' collection window closed;
        // keeps multi-session persisted series self-consistent even when the
        // current session's guiding conditions differ
        public double ThresholdP90 { get; set; }
        public double ThresholdP95 { get; set; }
        public double ThresholdP99 { get; set; }
    }
}
