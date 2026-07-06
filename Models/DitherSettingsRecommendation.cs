namespace DitherStatistics.Plugin {
    /// <summary>
    /// Recommended dither settle settings computed from guiding data analysis.
    /// Profile mapping (property suffixes kept from v1.x for persisted-JSON compatibility):
    ///   Quality     = Strict   (P90 of the stable-guiding distance distribution)
    ///   Balanced    = Standard (P95)
    ///   Performance = Fast     (P99)
    /// A lower quantile demands more confidence before imaging resumes - it does not
    /// improve image quality, it only lengthens settling.
    /// </summary>
    public class DitherSettingsRecommendation {
        // Settle pixel tolerance in guide-camera pixels
        public double SettlePixelTolerance_Quality { get; set; }
        public double SettlePixelTolerance_Balanced { get; set; }
        public double SettlePixelTolerance_Performance { get; set; }

        // Minimum settle time = debounce only (time the star must STAY within tolerance);
        // since v1.6 identical for all profiles and intentionally small
        public double MinSettleTime_Quality { get; set; }
        public double MinSettleTime_Balanced { get; set; }
        public double MinSettleTime_Performance { get; set; }

        // v1.6+: median time from dither until guiding is stable (info, per profile)
        public double ExpectedSettleDuration_Quality { get; set; }
        public double ExpectedSettleDuration_Balanced { get; set; }
        public double ExpectedSettleDuration_Performance { get; set; }

        // v1.6+: recommended settle timeout per profile
        public double SettleTimeout_Quality { get; set; }
        public double SettleTimeout_Balanced { get; set; }
        public double SettleTimeout_Performance { get; set; }

        // v1.6+: confidence info per profile
        public int SeriesUsed_Quality { get; set; }
        public int SeriesUsed_Balanced { get; set; }
        public int SeriesUsed_Performance { get; set; }
        public int Unstabilized_Quality { get; set; }
        public int Unstabilized_Balanced { get; set; }
        public int Unstabilized_Performance { get; set; }
        public double SettleDelaySpread_Quality { get; set; }      // IQR of the settle delays in seconds
        public double SettleDelaySpread_Balanced { get; set; }
        public double SettleDelaySpread_Performance { get; set; }
        public int ExcludedSeries { get; set; }                    // failed settle / star lost
        public double GuiderPixelScaleArcsec { get; set; }         // 0 = unknown

        public int DitherEventsAnalyzed { get; set; }
        public double CurrentRunningRMS { get; set; }
        public double CurrentRMSStdDev { get; set; }
        public double GuideExposure { get; set; }
    }
}
