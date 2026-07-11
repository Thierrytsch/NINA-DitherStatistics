using ScottPlot;
using ScottPlot.Plottable;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// Canonical chart accent colors, defined once so the ScottPlot renderers below
    /// and the XAML legend swatches (Views/DitherStatisticsView.xaml) cannot drift
    /// apart. XAML cannot bind to System.Drawing.Color, so the legend swatches
    /// mirror these as literal hex values with a comment pointing back here -
    /// update both sides together when changing a color.
    /// </summary>
    public static class ChartColorPalette {
        // Pixel Shift: the gradient encodes point age (oldest -> newest); endpoints
        // tuned to stay legible on both dark and light NINA themes.
        public static readonly System.Drawing.Color PixelShiftGradientOldest = System.Drawing.Color.FromArgb(255, 100, 0, 0);   // #640000
        public static readonly System.Drawing.Color PixelShiftGradientNewest = System.Drawing.Color.FromArgb(255, 220, 0, 0);   // #DC0000
        public static readonly System.Drawing.Color PixelShiftConnection = System.Drawing.Color.FromArgb(80, 100, 149, 237);    // CornflowerBlue, translucent

        // Shared "primary accent" - ties the Pixel Shift "Latest" marker and the
        // Settle Time data line into one visual system across both charts.
        public static readonly System.Drawing.Color AccentPrimary = System.Drawing.Color.FromArgb(255, 0, 191, 255);            // #00BFFF DeepSkyBlue

        // Settle Time: one data color (AccentPrimary above), one average color, one
        // threshold color - each a distinct hue from the Pixel Shift red family.
        public static readonly System.Drawing.Color SettleAverage = System.Drawing.Color.FromArgb(255, 255, 193, 7);            // #FFC107 Amber
        public static readonly System.Drawing.Color SettleThreshold = System.Drawing.Color.FromArgb(220, 171, 71, 188);         // #AB47BC Violet
    }

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

        /// <summary>
        /// Renders a centered "no data yet" placeholder with a small fixed axis
        /// range instead of ScottPlot's dense default grid on an empty dataset.
        /// </summary>
        public static void RenderEmptyState(ScottPlot.WpfPlot plot, double xMin, double xMax, double yMin, double yMax, System.Drawing.Color primaryColor) {
            var mutedColor = System.Drawing.Color.FromArgb(150, primaryColor.R, primaryColor.G, primaryColor.B);
            var text = plot.Plot.AddText("No dither data yet", (xMin + xMax) / 2, (yMin + yMax) / 2, size: 11, color: mutedColor);
            text.Alignment = Alignment.MiddleCenter;
            plot.Plot.SetAxisLimits(xMin, xMax, yMin, yMax);
            plot.Render();
        }

        /// <summary>
        /// Picks a "nice" tick step (from 0.1/0.2/0.5/1/2/5/... x 10^n) that yields
        /// roughly 6 ticks across the given range, instead of ScottPlot's default
        /// dense/fractional spacing.
        /// </summary>
        public static double ComputeNiceTickStep(double range) {
            if (range <= 0) return 0.5;
            double target = range / 6.0;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(target)));
            double[] steps = { 1, 2, 5, 10 };
            foreach (var step in steps) {
                double candidate = step * magnitude;
                if (candidate >= target) return candidate;
            }
            return 10 * magnitude;
        }

        /// <summary>
        /// Picks a "nice" integer tick step (1/2/5/10/20/50/...) that keeps roughly
        /// 10 or fewer ticks visible for the given integer dither count.
        /// </summary>
        public static int ComputeNiceIntegerTickStep(int count) {
            int[] steps = { 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000 };
            foreach (var step in steps) {
                if (count / step <= 10) return step;
            }
            return steps[steps.Length - 1];
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
            // True 1:1 spatial rendering - 1 px in X renders the same length as 1 px
            // in Y instead of the panel's aspect ratio distorting the dither pattern.
            plot.Plot.AxisScaleLock(true);

            if (pixelShiftValues.Count == 0) {
                ChartTheme.RenderEmptyState(plot, -2, 2, -2, 2, primaryColor);
                return;
            }

            double[] xData = pixelShiftValues.Select(p => p.X).ToArray();
            double[] yData = pixelShiftValues.Select(p => p.Y).ToArray();

            // Add connection line (thin, semi-transparent)
            if (pixelShiftValues.Count > 1) {
                var connectionLine = plot.Plot.AddScatter(xData, yData);
                connectionLine.Color = ChartColorPalette.PixelShiftConnection;
                connectionLine.LineWidth = 1;
                connectionLine.MarkerSize = 0;
                connectionLine.Label = "Connections";
            }

            // Add gradient-colored scatter points, batched into a single BubblePlot
            // (one plottable holding all points) instead of one AddScatter plottable
            // per point - with persistence enabled a session can have hundreds of
            // dithers, and re-adding hundreds of plottables on every render was the
            // bottleneck. BubblePlot's radius is in pixels (diameter/2) to match the
            // previous MarkerSize=6 filled-circle markers; edge color equals fill
            // color so no outline is visible.
            var gradientPoints = plot.Plot.AddBubblePlot();
            for (int i = 0; i < pixelShiftValues.Count; i++) {
                double ratio = pixelShiftValues.Count > 1 ? (double)i / (pixelShiftValues.Count - 1) : 1.0;
                var pointColor = System.Drawing.Color.FromArgb(
                    255,
                    (byte)(ChartColorPalette.PixelShiftGradientOldest.R + (ChartColorPalette.PixelShiftGradientNewest.R - ChartColorPalette.PixelShiftGradientOldest.R) * ratio),
                    0,
                    0);

                gradientPoints.Add(xData[i], yData[i], radius: 3, fillColor: pointColor, edgeWidth: 0, edgeColor: pointColor);
            }

            // Highlight the last point with the shared accent color (readable on
            // both themes, unlike the previous Lime)
            int lastIndex = pixelShiftValues.Count - 1;
            var lastPoint = plot.Plot.AddScatter(
                new double[] { xData[lastIndex] },
                new double[] { yData[lastIndex] }
            );
            lastPoint.Color = ChartColorPalette.AccentPrimary;
            lastPoint.MarkerSize = 8;
            lastPoint.MarkerShape = MarkerShape.filledCircle;
            lastPoint.LineWidth = 0;
            lastPoint.Label = "Latest";

            // Add crosshair at origin with dynamic color
            plot.Plot.AddVerticalLine(0, primaryColor, 2);
            plot.Plot.AddHorizontalLine(0, primaryColor, 2);

            plot.Plot.AxisAuto();

            // Sensible tick spacing (0.5/1/2/5 px steps) instead of ScottPlot's dense
            // default; same step on both axes since the scale is locked 1:1.
            double maxAbs = Math.Max(xData.Select(Math.Abs).Max(), yData.Select(Math.Abs).Max());
            double tickStep = ChartTheme.ComputeNiceTickStep(maxAbs * 2);
            plot.Plot.XAxis.ManualTickSpacing(tickStep);
            plot.Plot.YAxis.ManualTickSpacing(tickStep);

            plot.Render();
        }
    }

    /// <summary>
    /// Renders the settle time line chart (with average/stddev band and, when
    /// available, the optimizer's P90/P95/P99 settle-time thresholds). Pure
    /// function of the WpfPlot, the current data and the theme color - no VM access.
    /// </summary>
    public static class SettleTimeChartRenderer {
        public static void Render(
            ScottPlot.WpfPlot plot,
            IReadOnlyList<double> settleTimeValues,
            double averageSettleTime,
            double stdDevSettleTime,
            double p90SettleTime,
            double p95SettleTime,
            double p99SettleTime,
            System.Drawing.Color primaryColor) {
            plot.Plot.Clear();
            ChartTheme.ApplyColors(plot, primaryColor);

            if (settleTimeValues.Count == 0) {
                ChartTheme.RenderEmptyState(plot, 0, 5, 0, 2, primaryColor);
                return;
            }

            // X-axis: Dither numbers (1, 2, 3, ...)
            double[] xData = Enumerable.Range(1, settleTimeValues.Count).Select(i => (double)i).ToArray();
            double[] yData = settleTimeValues.ToArray();
            double maxY = yData.Max();

            // Add main settle time line
            var settleTimeLine = plot.Plot.AddScatter(xData, yData);
            settleTimeLine.Color = ChartColorPalette.AccentPrimary;
            settleTimeLine.LineWidth = 2;
            settleTimeLine.MarkerSize = 8;
            settleTimeLine.MarkerShape = MarkerShape.filledCircle;
            settleTimeLine.Label = "Settle Time";

            // Add average line + a single shaded Avg +/- StdDev band (replaces the
            // previous two dotted lines - visually calmer, one legend entry)
            if (averageSettleTime > 0) {
                double[] avgData = Enumerable.Repeat(averageSettleTime, settleTimeValues.Count).ToArray();
                var avgLine = plot.Plot.AddScatter(xData, avgData);
                avgLine.Color = ChartColorPalette.SettleAverage;
                avgLine.LineWidth = 2;
                avgLine.MarkerSize = 0;
                avgLine.LineStyle = LineStyle.Dash;
                avgLine.Label = "Average";
                maxY = Math.Max(maxY, averageSettleTime);

                if (stdDevSettleTime > 0) {
                    double lower = Math.Max(0, averageSettleTime - stdDevSettleTime);
                    double upper = averageSettleTime + stdDevSettleTime;
                    var band = plot.Plot.AddFill(
                        new double[] { xData[0], xData[xData.Length - 1] },
                        new double[] { lower, lower },
                        new double[] { upper, upper },
                        System.Drawing.Color.FromArgb(50, ChartColorPalette.SettleAverage.R, ChartColorPalette.SettleAverage.G, ChartColorPalette.SettleAverage.B));
                    band.Label = "Avg ± StdDev";
                    maxY = Math.Max(maxY, upper);
                }
            }

            // P90/P95/P99 settle-time thresholds from the Dither Settings Optimizer,
            // when available - thin, semi-transparent, one distinct color family.
            // The three values are frequently close together (the profiles differ in
            // tolerance, not much in time-to-settle), so labels are staggered along X
            // (instead of ScottPlot's built-in PositionLabel, which shows only the bare
            // Y value and would collapse into one unreadable number when lines overlap).
            // The label sits above its line by a data-space offset (not a pixel offset)
            // so "above" is unambiguous regardless of ScottPlot's text-alignment anchor.
            double thresholdLabelOffset = Math.Max(yData.Max(), averageSettleTime) * 0.04;
            void AddThreshold(double value, string label, double xFraction) {
                if (value <= 0) return;
                plot.Plot.AddHorizontalLine(value, ChartColorPalette.SettleThreshold, 1, LineStyle.Dot);
                double xPos = xData[0] + (xData[xData.Length - 1] - xData[0]) * xFraction;
                double yPos = value + thresholdLabelOffset;
                var text = plot.Plot.AddText($"{label} {value:F1}s", xPos, yPos, size: 9, color: ChartColorPalette.SettleThreshold);
                text.Alignment = Alignment.MiddleCenter;
                maxY = Math.Max(maxY, yPos);
            }
            AddThreshold(p90SettleTime, "P90", 0.15);
            AddThreshold(p95SettleTime, "P95", 0.5);
            AddThreshold(p99SettleTime, "P99", 0.85);

            // Disable built-in legend (shown below the chart in XAML)
            plot.Plot.Legend(enable: false);

            // Integer dither-number ticks and Y headroom above the highest series
            // (data, average band, thresholds) so points/lines never sit at the edge.
            int xStep = ChartTheme.ComputeNiceIntegerTickStep(settleTimeValues.Count);
            plot.Plot.XAxis.ManualTickSpacing(xStep);
            plot.Plot.SetAxisLimits(xMin: 0.5, xMax: settleTimeValues.Count + 0.5, yMin: 0, yMax: maxY * 1.15);

            plot.Render();
        }
    }
}
