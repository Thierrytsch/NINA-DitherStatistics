namespace DitherStatistics.Plugin {
    /// <summary>
    /// Pure computation of the guider-to-main-camera pixel scale ratio used by the
    /// quality metrics. No logging/NINA dependencies - the caller decides what and
    /// when to log (e.g. one-time fallback messages).
    /// </summary>
    public static class PixelScaleService {
        public readonly struct Result {
            /// <summary>"manual" | "auto/NINA" | "auto/PHD2" | "fallback"</summary>
            public string Source { get; init; }
            public double Ratio { get; init; }
            /// <summary>Set when the computed ratio was outside the plausible range (0.01..100)</summary>
            public string ImplausibleWarning { get; init; }
            /// <summary>Set when falling back due to missing inputs (as opposed to an implausible ratio)</summary>
            public string FallbackReason { get; init; }
        }

        /// <summary>
        /// Main-camera pixels per guide-camera pixel. Manual override wins; otherwise
        /// the guider scale comes from NINA's GuiderInfo (primary) or PHD2 get_pixel_scale
        /// (secondary), and the main-camera scale from the active NINA profile
        /// (arcsec/px = 206.265 * pixelSize[µm] / focalLength[mm]). Falls back to 1.0.
        /// </summary>
        public static Result Calculate(double overrideRatio, double ninaGuiderPixelScale, double phd2GuiderPixelScale, double pixelSizeMicron, double focalLengthMm) {
            if (overrideRatio > 0) {
                return new Result { Ratio = overrideRatio, Source = "manual" };
            }

            double guiderScale = ninaGuiderPixelScale > 0 ? ninaGuiderPixelScale : phd2GuiderPixelScale;
            if (guiderScale > 0 && pixelSizeMicron > 0 && focalLengthMm > 0) {
                double mainScale = 206.265 * pixelSizeMicron / focalLengthMm;
                double ratio = guiderScale / mainScale;
                if (ratio > 0.01 && ratio < 100) {
                    return new Result { Ratio = ratio, Source = ninaGuiderPixelScale > 0 ? "auto/NINA" : "auto/PHD2" };
                }
                return new Result {
                    Ratio = 1.0,
                    Source = "fallback",
                    ImplausibleWarning = $"Implausible pixel scale ratio {ratio:F2} (guider {guiderScale:F2}\"/px, main {mainScale:F2}\"/px), using 1.0"
                };
            }

            return new Result {
                Ratio = 1.0,
                Source = "fallback",
                FallbackReason = $"guiderScale={(guiderScale > 0 ? guiderScale.ToString("F2") : "n/a (guider not connected in NINA?)")}, " +
                    $"pixelSize={(pixelSizeMicron > 0 ? pixelSizeMicron.ToString("F2") : "n/a (set camera pixel size in NINA options!)")}, " +
                    $"focalLength={(focalLengthMm > 0 ? focalLengthMm.ToString("F0") : "n/a (set telescope focal length in NINA options!)")}"
            };
        }
    }
}
