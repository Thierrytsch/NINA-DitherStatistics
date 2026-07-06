using ScottPlot;
using ScottPlot.Plottable;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// Shared axis/grid theming for both charts. Not VM-aware: takes a WpfPlot
    /// and a resolved color, nothing else.
    /// </summary>
    public static class ChartTheme {
        public static void ApplyColors(ScottPlot.WpfPlot plot, System.Drawing.Color primaryColor) {
            plot.Plot.XAxis.LabelStyle(color: primaryColor);
            plot.Plot.YAxis.LabelStyle(color: primaryColor);
            plot.Plot.XAxis.Color(primaryColor);
            plot.Plot.YAxis.Color(primaryColor);
            plot.Plot.XAxis.TickLabelStyle(color: primaryColor);
            plot.Plot.YAxis.TickLabelStyle(color: primaryColor);
            plot.Plot.Grid(color: System.Drawing.Color.FromArgb(50, primaryColor.R, primaryColor.G, primaryColor.B));
        }
    }

    /// <summary>
    /// Renders the X/Y pixel drift scatter plot. Pure function of the WpfPlot,
    /// the current data and the theme color - no VM access.
    /// </summary>
    public static class PixelShiftChartRenderer {
        public static void Render(ScottPlot.WpfPlot plot, IReadOnlyList<PixelShiftPoint> pixelShiftValues, System.Drawing.Color primaryColor) {
            plot.Plot.Clear();
            ChartTheme.ApplyColors(plot, primaryColor);

            if (pixelShiftValues.Count == 0) {
                plot.Render();
                return;
            }

            double[] xData = pixelShiftValues.Select(p => p.X).ToArray();
            double[] yData = pixelShiftValues.Select(p => p.Y).ToArray();

            // Add connection line (thin, semi-transparent)
            if (pixelShiftValues.Count > 1) {
                var connectionLine = plot.Plot.AddScatter(xData, yData);
                connectionLine.Color = System.Drawing.Color.FromArgb(80, 100, 149, 237);
                connectionLine.LineWidth = 1;
                connectionLine.MarkerSize = 0;
                connectionLine.Label = "Connections";
            }

            // Add gradient-colored scatter points
            for (int i = 0; i < pixelShiftValues.Count; i++) {
                double ratio = pixelShiftValues.Count > 1 ? (double)i / (pixelShiftValues.Count - 1) : 1.0;
                byte red = (byte)(60 + (200 - 60) * ratio);
                var pointColor = System.Drawing.Color.FromArgb(255, red, 0, 0);

                var scatter = plot.Plot.AddScatter(
                    new double[] { xData[i] },
                    new double[] { yData[i] }
                );
                scatter.Color = pointColor;
                scatter.MarkerSize = 6;
                scatter.MarkerShape = MarkerShape.filledCircle;
                scatter.LineWidth = 0;
            }

            // Highlight last point in lime green
            int lastIndex = pixelShiftValues.Count - 1;
            var lastPoint = plot.Plot.AddScatter(
                new double[] { xData[lastIndex] },
                new double[] { yData[lastIndex] }
            );
            lastPoint.Color = System.Drawing.Color.Lime;
            lastPoint.MarkerSize = 8;
            lastPoint.MarkerShape = MarkerShape.filledCircle;
            lastPoint.LineWidth = 0;
            lastPoint.Label = "Latest";

            // Add crosshair at origin with dynamic color
            plot.Plot.AddVerticalLine(0, primaryColor, 2);
            plot.Plot.AddHorizontalLine(0, primaryColor, 2);

            plot.Plot.AxisAuto();
            plot.Render();
        }
    }

    /// <summary>
    /// Renders the settle time line chart (with average/stddev bands). Pure
    /// function of the WpfPlot, the current data and the theme color - no VM access.
    /// </summary>
    public static class SettleTimeChartRenderer {
        public static void Render(
            ScottPlot.WpfPlot plot,
            IReadOnlyList<double> settleTimeValues,
            double averageSettleTime,
            double stdDevSettleTime,
            System.Drawing.Color primaryColor) {
            plot.Plot.Clear();
            ChartTheme.ApplyColors(plot, primaryColor);

            if (settleTimeValues.Count == 0) {
                plot.Render();
                return;
            }

            // X-axis: Dither numbers (1, 2, 3, ...)
            double[] xData = Enumerable.Range(1, settleTimeValues.Count).Select(i => (double)i).ToArray();
            double[] yData = settleTimeValues.ToArray();

            // Add main settle time line
            var settleTimeLine = plot.Plot.AddScatter(xData, yData);
            settleTimeLine.Color = System.Drawing.Color.DodgerBlue;
            settleTimeLine.LineWidth = 2;
            settleTimeLine.MarkerSize = 8;
            settleTimeLine.MarkerShape = MarkerShape.filledCircle;
            settleTimeLine.Label = "Settle Time";

            // Add average line if we have statistics
            if (averageSettleTime > 0 && settleTimeValues.Count > 0) {
                double[] avgData = Enumerable.Repeat(averageSettleTime, settleTimeValues.Count).ToArray();
                var avgLine = plot.Plot.AddScatter(xData, avgData);
                avgLine.Color = System.Drawing.Color.Red;
                avgLine.LineWidth = 2;
                avgLine.MarkerSize = 0;
                avgLine.LineStyle = LineStyle.Dash;
                avgLine.Label = "Average";

                // Add Avg +/- StdDev lines
                if (stdDevSettleTime > 0) {
                    double[] lowerData = Enumerable.Repeat(Math.Max(0, averageSettleTime - stdDevSettleTime), settleTimeValues.Count).ToArray();
                    double[] upperData = Enumerable.Repeat(averageSettleTime + stdDevSettleTime, settleTimeValues.Count).ToArray();

                    var lowerLine = plot.Plot.AddScatter(xData, lowerData);
                    lowerLine.Color = System.Drawing.Color.FromArgb(120, 255, 0, 0);
                    lowerLine.LineWidth = 2;
                    lowerLine.MarkerSize = 0;
                    lowerLine.LineStyle = LineStyle.Dot;
                    lowerLine.Label = "Avg - StdDev";

                    var upperLine = plot.Plot.AddScatter(xData, upperData);
                    upperLine.Color = System.Drawing.Color.FromArgb(120, 255, 0, 0);
                    upperLine.LineWidth = 2;
                    upperLine.MarkerSize = 0;
                    upperLine.LineStyle = LineStyle.Dot;
                    upperLine.Label = "Avg + StdDev";
                }
            }

            // Disable built-in legend (shown below the chart in XAML)
            plot.Plot.Legend(enable: false);

            // Auto-scale (will respect 0 as minimum) and refresh
            plot.Plot.AxisAuto();
            plot.Plot.SetAxisLimits(yMin: 0);
            plot.Render();
        }
    }
}
