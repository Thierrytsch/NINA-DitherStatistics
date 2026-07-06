using NINA.Core.Utility;
using System;
using System.Windows;
using System.Windows.Media;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// Polls the NINA theme's PrimaryBrush color every 500 ms and raises an event
    /// when it changes, so the VM can refresh chart colors without owning the
    /// DispatcherTimer itself. Timer must run on the UI thread (started via
    /// Application.Current.Dispatcher.BeginInvoke, same as before the split).
    /// </summary>
    public class NinaThemeWatcher : IDisposable {
        private System.Windows.Threading.DispatcherTimer themeColorTimer;
        private System.Drawing.Color lastPrimaryColor = System.Drawing.Color.White;

        public event EventHandler<System.Drawing.Color> PrimaryColorChanged;

        /// <summary>
        /// Get NINA theme color from a dynamic resource, checked as Brush or Color,
        /// falling back to MainWindow resources. Converts WPF colors to
        /// System.Drawing.Color for ScottPlot.
        /// </summary>
        public static System.Drawing.Color GetThemeColor(string resourceKey, System.Drawing.Color fallback) {
            try {
                if (Application.Current?.Resources[resourceKey] is SolidColorBrush brush) {
                    var wpfColor = brush.Color;
                    var color = System.Drawing.Color.FromArgb(wpfColor.A, wpfColor.R, wpfColor.G, wpfColor.B);
                    Logger.Debug($"GetThemeColor('{resourceKey}'): Found Brush - R:{color.R} G:{color.G} B:{color.B} A:{color.A}");
                    return color;
                }

                if (Application.Current?.Resources[resourceKey] is Color wpfColor2) {
                    var color = System.Drawing.Color.FromArgb(wpfColor2.A, wpfColor2.R, wpfColor2.G, wpfColor2.B);
                    Logger.Debug($"GetThemeColor('{resourceKey}'): Found Color - R:{color.R} G:{color.G} B:{color.B} A:{color.A}");
                    return color;
                }

                if (Application.Current?.MainWindow?.Resources[resourceKey] is SolidColorBrush brush2) {
                    var wpfColor = brush2.Color;
                    var color = System.Drawing.Color.FromArgb(wpfColor.A, wpfColor.R, wpfColor.G, wpfColor.B);
                    Logger.Debug($"GetThemeColor('{resourceKey}'): Found in MainWindow Brush - R:{color.R} G:{color.G} B:{color.B} A:{color.A}");
                    return color;
                }

                Logger.Warning($"GetThemeColor('{resourceKey}'): Resource not found, using fallback R:{fallback.R} G:{fallback.G} B:{fallback.B}");
            } catch (Exception ex) {
                Logger.Error($"Failed to get theme color '{resourceKey}': {ex.Message}");
            }
            return fallback;
        }

        /// <summary>Starts 500 ms polling of PrimaryBrush; the timer itself is created on the UI thread.</summary>
        public void Start() {
            try {
                lastPrimaryColor = GetThemeColor("PrimaryBrush", System.Drawing.Color.White);
                Logger.Info($"Initial PrimaryBrush color: R:{lastPrimaryColor.R} G:{lastPrimaryColor.G} B:{lastPrimaryColor.B}");

                Application.Current.Dispatcher.BeginInvoke(new Action(() => {
                    try {
                        themeColorTimer = new System.Windows.Threading.DispatcherTimer();
                        themeColorTimer.Interval = TimeSpan.FromMilliseconds(500);
                        themeColorTimer.Tick += OnTimerTick;
                        themeColorTimer.Start();
                        Logger.Info("Theme color monitoring timer started on UI thread");
                    } catch (Exception ex) {
                        Logger.Error($"Failed to start timer on UI thread: {ex.Message}");
                    }
                }));
            } catch (Exception ex) {
                Logger.Error($"Failed to start theme color monitoring: {ex.Message}");
            }
        }

        private void OnTimerTick(object sender, EventArgs e) {
            try {
                var currentPrimaryColor = GetThemeColor("PrimaryBrush", System.Drawing.Color.White);

                if (currentPrimaryColor.ToArgb() != lastPrimaryColor.ToArgb()) {
                    Logger.Info($"Theme color CHANGED! Old: R:{lastPrimaryColor.R} G:{lastPrimaryColor.G} B:{lastPrimaryColor.B} -> New: R:{currentPrimaryColor.R} G:{currentPrimaryColor.G} B:{currentPrimaryColor.B}");
                    lastPrimaryColor = currentPrimaryColor;
                    PrimaryColorChanged?.Invoke(this, currentPrimaryColor);
                }
            } catch (Exception ex) {
                Logger.Error($"Error in theme color monitoring tick: {ex.Message}");
            }
        }

        public void Dispose() {
            if (themeColorTimer != null) {
                themeColorTimer.Tick -= OnTimerTick;
                themeColorTimer.Stop();
                themeColorTimer = null;
                Logger.Info("Theme color monitoring timer stopped");
            }
        }
    }
}
