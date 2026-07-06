namespace DitherStatistics.Plugin {
    /// <summary>
    /// Model for pixel shift chart points
    /// X/Y = Cumulative absolute position (for chart display)
    /// DeltaX/DeltaY = Individual shift (for tooltip)
    /// </summary>
    public class PixelShiftPoint {
        // Chart position (cumulative)
        public double X { get; set; }
        public double Y { get; set; }

        // Delta values for tooltip
        public double DeltaX { get; set; }
        public double DeltaY { get; set; }

        // Parameterless constructor required for JSON deserialization (statistics persistence)
        public PixelShiftPoint() {
        }

        public PixelShiftPoint(double cumulativeX, double cumulativeY, double deltaX, double deltaY) {
            X = cumulativeX;
            Y = cumulativeY;
            DeltaX = deltaX;
            DeltaY = deltaY;
        }
    }
}
