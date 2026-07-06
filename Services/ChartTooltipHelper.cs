using NINA.Core.Utility;
using System;
using System.Collections.Generic;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// Attaches MouseMove/MouseLeave tooltip behavior to a WpfPlot. Decoupled from
    /// the VM: data is read through a delegate (so it reflects the live, mutable
    /// list at move-time) and the tooltip text/visibility are pushed back through
    /// delegates rather than a VM reference, so this stays independently testable/reusable.
    /// </summary>
    public static class ChartTooltipHelper {
        /// <summary>
        /// Tooltip shows the delta of the nearest point: "dX: 2.71 px  dY: -3.29 px".
        /// Nearest point within 5% of the plot diagonal is considered a hit.
        /// </summary>
        public static void AttachPixelShiftTooltip(
            ScottPlot.WpfPlot plot,
            Func<IReadOnlyList<PixelShiftPoint>> getPixelShiftValues,
            Action<string> setTooltipText,
            Action<bool> setTooltipVisible) {
            plot.MouseMove += (s, e) => {
                try {
                    var pixelShiftValues = getPixelShiftValues();
                    if (pixelShiftValues.Count == 0) {
                        setTooltipVisible(false);
                        return;
                    }

                    var mouseCoords = plot.GetMouseCoordinates();

                    int nearestIndex = -1;
                    double minDistance = double.MaxValue;

                    for (int i = 0; i < pixelShiftValues.Count; i++) {
                        var point = pixelShiftValues[i];
                        double distance = Math.Sqrt(
                            Math.Pow(point.X - mouseCoords.x, 2) +
                            Math.Pow(point.Y - mouseCoords.y, 2)
                        );

                        if (distance < minDistance) {
                            minDistance = distance;
                            nearestIndex = i;
                        }
                    }

                    if (nearestIndex >= 0) {
                        var axisLimits = plot.Plot.GetAxisLimits();
                        double xRange = axisLimits.XMax - axisLimits.XMin;
                        double yRange = axisLimits.YMax - axisLimits.YMin;
                        double threshold = Math.Sqrt(xRange * xRange + yRange * yRange) * 0.05; // 5% of diagonal

                        if (minDistance < threshold) {
                            var point = pixelShiftValues[nearestIndex];
                            setTooltipText($"ΔX: {point.DeltaX:F2} px  ΔY: {point.DeltaY:F2} px");
                            setTooltipVisible(true);
                        } else {
                            setTooltipVisible(false);
                        }
                    } else {
                        setTooltipVisible(false);
                    }
                } catch (Exception ex) {
                    Logger.Error($"Error in PixelShift tooltip: {ex.Message}");
                }
            };

            plot.MouseLeave += (s, e) => setTooltipVisible(false);
        }

        /// <summary>
        /// Tooltip shows "Dither #5: 12.34s". Hit test uses the X axis as the
        /// (1-based) dither number and a 10% of Y-range threshold.
        /// </summary>
        public static void AttachSettleTimeTooltip(
            ScottPlot.WpfPlot plot,
            Func<IReadOnlyList<double>> getSettleTimeValues,
            Action<string> setTooltipText,
            Action<bool> setTooltipVisible) {
            plot.MouseMove += (s, e) => {
                try {
                    var settleTimeValues = getSettleTimeValues();
                    if (settleTimeValues.Count == 0) {
                        setTooltipVisible(false);
                        return;
                    }

                    var mouseCoords = plot.GetMouseCoordinates();

                    int ditherNumber = (int)Math.Round(mouseCoords.x);
                    int index = ditherNumber - 1; // Convert to 0-based index

                    if (index >= 0 && index < settleTimeValues.Count) {
                        double settleTime = settleTimeValues[index];

                        double yValue = settleTime;
                        double distance = Math.Abs(mouseCoords.y - yValue);

                        var axisLimits = plot.Plot.GetAxisLimits();
                        double yRange = axisLimits.YMax - axisLimits.YMin;
                        double threshold = yRange * 0.1; // 10% of Y range

                        if (distance < threshold) {
                            setTooltipText($"Dither #{ditherNumber}: {settleTime:F2}s");
                            setTooltipVisible(true);
                        } else {
                            setTooltipVisible(false);
                        }
                    } else {
                        setTooltipVisible(false);
                    }
                } catch (Exception ex) {
                    Logger.Error($"Error in SettleTime tooltip: {ex.Message}");
                }
            };

            plot.MouseLeave += (s, e) => setTooltipVisible(false);
        }
    }
}
