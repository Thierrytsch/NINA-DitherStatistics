using System;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// Connection state changes raised by PHD2Client.ConnectionStatusChanged.
    /// The distinction matters for the consumers: Phd2ConnectionManager reconnects
    /// only after ConnectionLost (involuntary loss detected by the read loop),
    /// while Disconnected is the explicit shutdown (Disconnect/Dispose) that the
    /// optimizer uses to abort its running collection window - never reconnect on it.
    /// </summary>
    public enum Phd2ConnectionStatus {
        Connected,
        ConnectionFailed,
        ConnectionLost,
        Disconnected
    }

    /// <summary>
    /// Event args for GuidingDithered event (Dither START with pixel shift)
    /// </summary>
    public class PHD2GuidingDitheredEventArgs : EventArgs {
        public double DeltaX { get; set; }
        public double DeltaY { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Event args for SettleDone event (Dither END)
    /// </summary>
    public class PHD2SettleDoneEventArgs : EventArgs {
        public bool Success { get; set; }
        public int Status { get; set; }
        public int TotalFrames { get; set; }
        public int DroppedFrames { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Event args for GuideStep event (Guide corrections with RMS)
    /// </summary>
    public class PHD2GuideStepEventArgs : EventArgs {
        // Camera coordinates (pixels on guide chip)
        public double DX { get; set; }         // X offset from lock position in camera coordinates (pixels)
        public double DY { get; set; }         // Y offset from lock position in camera coordinates (pixels)

        // Running RMS using PHD2's method
        public double RunningRMS { get; set; } // Total RMS (pixels):
                                               // ra_stddev = sqrt(Σ(dx_i - mean_dx)² / (n-1))
                                               // dec_stddev = sqrt(Σ(dy_i - mean_dy)² / (n-1))
                                               // total_rms = sqrt(ra_stddev² + dec_stddev²)

        // RMS Standard Deviation
        public double RMSStdDev { get; set; }  // Standard deviation of RMS values:
                                               // sqrt(Σ(rms_i - mean_rms)² / (n-1))

        // Guide exposure time
        public double Exposure { get; set; }   // Guide exposure time in seconds

        public DateTime Timestamp { get; set; }
    }
}
