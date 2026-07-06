using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.ViewModel;
using ScottPlot;
using ScottPlot.Plottable;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// ViewModel for Dither Statistics Panel with PHD2 direct integration
    /// Migrated from LiveCharts to ScottPlot 4.1.x for NINA 3.1 + 3.2 compatibility
    /// Filename: DitherStatisticsVM.cs
    /// </summary>
    [Export(typeof(IDockableVM))]
    public partial class DitherStatisticsVM : BaseINPC, IDockableVM, IGuiderConsumer {
        private readonly ObservableCollection<DitherEvent> ditherEvents = new ObservableCollection<DitherEvent>();
        private readonly IGuiderMediator guiderMediator;
        private readonly IProfileService profileService;
        private readonly PHD2Client phd2Client;
        private readonly DitherOptimizerService optimizerService;
        private readonly Phd2ConnectionManager phd2ConnectionManager;
        private readonly PluginSettingsStore settingsStore = new PluginSettingsStore();
        private readonly StatisticsProfileService profileDataService = new StatisticsProfileService();
        private DitherEvent currentDither = null;
        private readonly Random random = new Random();

        // Theme color monitoring
        private System.Windows.Threading.DispatcherTimer themeColorTimer;
        private System.Drawing.Color lastPrimaryColor = System.Drawing.Color.White;

        // Cumulative position tracking for absolute chart display
        private double cumulativeX = 0.0;
        private double cumulativeY = 0.0;

        [ImportingConstructor]
        public DitherStatisticsVM(IGuiderMediator guiderMediator, IProfileService profileService) {
            this.guiderMediator = guiderMediator;
            this.profileService = profileService;

            Title = "Dither Statistics";
            ContentId = "DitherStatistics_Panel";

            // Set Icon Geometry for the toggle button in NINA's Image area
            // Must be created on UI thread
            if (Application.Current != null) {
                Application.Current.Dispatcher.Invoke(() => {
                    ImageGeometry = CreatePluginIcon();
                });
            }

            // ✅ Load DataTemplates HIER im ViewModel (nicht im Plugin!)
            LoadDataTemplates();

            // ✅ NO MORE InitializePlots() call here!
            // Plots are now created lazily via Properties when XAML accesses them

            // Initialize commands
            InitializeCommands();

            // Initialize quality metrics
            InitializeQualityMetrics();

            // Load quality assessment toggle setting
            LoadQualityAssessmentSetting();

            // Load pixfrac / pixel scale ratio settings for the quality metrics
            LoadQualityMetricSettings();

            // Load dither optimizer toggle setting
            LoadDitherOptimizerSetting();

            // Load statistics persistence toggle setting
            LoadStatisticsPersistenceSetting();

            // Load multi-profile toggle and profile list, then migrate the legacy
            // v1.4 single data file into the new per-profile layout
            LoadMultiProfileSetting();
            LoadProfileListSetting();
            MigrateLegacyStatisticsFile();

            // Subscribe to NINA guider events (optional, for connection monitoring)
            SubscribeToGuiderEvents();

            // Re-evaluate the quality metrics (incl. pixel scale ratio) when the
            // NINA profile changes - focal length / pixel size differ per profile
            profileService.ProfileChanged += (s, e) =>
                Application.Current?.Dispatcher.BeginInvoke(new Action(UpdateQualityMetrics));

            // Initialize PHD2 protocol client and the optimizer service that consumes
            // its events; exposure and pixel scale are transport values owned by the
            // client and read lazily at analysis time
            phd2Client = new PHD2Client("127.0.0.1", 4400);
            optimizerService = new DitherOptimizerService(
                () => phd2Client.CurrentGuideExposure,
                () => phd2Client.GuiderPixelScaleArcsec);

            // Optimizer wiring first, so the series bookkeeping runs before the VM
            // handlers (same order as when the state machine lived inside PHD2Client)
            phd2Client.GuidingDithered += (s, e) => optimizerService.HandleGuidingDithered(e);
            phd2Client.SettleDone += (s, e) => optimizerService.HandleSettleDone(e);
            phd2Client.GuideStep += (s, e) => optimizerService.HandleGuideStep(e);
            phd2Client.StarLost += (s, e) => optimizerService.HandleStarLost();
            phd2Client.GuidingStarted += (s, e) => optimizerService.HandleGuidingStarted();
            // Only an explicit disconnect (Dispose) aborts the running collection
            // window; a mere connection loss deliberately does not clean up
            phd2Client.ConnectionStatusChanged += (s, status) => {
                if (status == "Disconnected") optimizerService.HandleDisconnected();
            };

            phd2Client.GuidingDithered += OnPHD2GuidingDithered;
            phd2Client.SettleDone += OnPHD2SettleDone;
            optimizerService.DitherRecommendationUpdated += OnDitherRecommendationUpdated;

            // The optimizer labels its diagnostic export files with the active profile
            optimizerService.CurrentProfileName = selectedProfileName;

            // Restore statistics from the previous session if persistence is enabled
            // (after service creation - optimizer data is restored into the service)
            RestoreStatisticsData();

            // Auto-connect to PHD2 (2 s initial delay and the retry/reconnect
            // policy live in the connection manager)
            phd2ConnectionManager = new Phd2ConnectionManager(phd2Client);
            phd2ConnectionManager.Start();

            // Start theme color monitoring for dynamic chart updates
            StartThemeColorMonitoring();

            Logger.Info("DitherStatisticsVM initialized successfully with ScottPlot 4.1 (Lazy Loading)!");
        }

        #region Properties - Chart Data

        // ScottPlot data storage (Lists instead of ChartValues)
        private readonly List<double> settleTimeValues = new List<double>();
        private readonly List<PixelShiftPoint> pixelShiftValues = new List<PixelShiftPoint>();

        // ✅ LAZY LOADING PROPERTY - Creates plot on first access (on UI thread via XAML binding)
        private ScottPlot.WpfPlot pixelShiftPlot;
        public ScottPlot.WpfPlot PixelShiftPlot {
            get {
                if (pixelShiftPlot == null) {
                    // Create plot on first access (happens on UI thread via XAML binding)
                    pixelShiftPlot = new ScottPlot.WpfPlot();
                    pixelShiftPlot.Plot.Style(
                        figureBackground: System.Drawing.Color.Transparent,
                        dataBackground: System.Drawing.Color.Transparent
                    );
                    pixelShiftPlot.Plot.XLabel("X Pixels");
                    pixelShiftPlot.Plot.YLabel("Y Pixels");

                    // Get NINA theme colors
                    var primaryColor = GetThemeColor("PrimaryBrush", System.Drawing.Color.White);

                    // Set axis label colors (same as chart title)
                    pixelShiftPlot.Plot.XAxis.LabelStyle(color: primaryColor);
                    pixelShiftPlot.Plot.YAxis.LabelStyle(color: primaryColor);

                    // Set axis, grid, and tick colors to PrimaryBrush
                    pixelShiftPlot.Plot.XAxis.Color(primaryColor);
                    pixelShiftPlot.Plot.YAxis.Color(primaryColor);
                    pixelShiftPlot.Plot.XAxis.TickLabelStyle(color: primaryColor);
                    pixelShiftPlot.Plot.YAxis.TickLabelStyle(color: primaryColor);
                    pixelShiftPlot.Plot.Grid(color: System.Drawing.Color.FromArgb(50, primaryColor.R, primaryColor.G, primaryColor.B));

                    // ✅ Attach tooltip event handlers
                    AttachPixelShiftTooltip(pixelShiftPlot);

                    Logger.Info("PixelShiftPlot created (lazy loading)");
                }
                return pixelShiftPlot;
            }
            set {
                pixelShiftPlot = value;
                RaisePropertyChanged();
            }
        }

        // ✅ LAZY LOADING PROPERTY - Creates plot on first access (on UI thread via XAML binding)
        private ScottPlot.WpfPlot settleTimePlot;
        public ScottPlot.WpfPlot SettleTimePlot {
            get {
                if (settleTimePlot == null) {
                    // Create plot on first access (happens on UI thread via XAML binding)
                    settleTimePlot = new ScottPlot.WpfPlot();
                    settleTimePlot.Plot.Style(
                        figureBackground: System.Drawing.Color.Transparent,
                        dataBackground: System.Drawing.Color.Transparent
                    );
                    settleTimePlot.Plot.XLabel("Dither #");
                    settleTimePlot.Plot.YLabel("Settle Time (s)");

                    // Get NINA theme colors
                    var primaryColor = GetThemeColor("PrimaryBrush", System.Drawing.Color.White);

                    // Set axis label colors (same as chart title)
                    settleTimePlot.Plot.XAxis.LabelStyle(color: primaryColor);
                    settleTimePlot.Plot.YAxis.LabelStyle(color: primaryColor);

                    // Set axis, grid, and tick colors to PrimaryBrush
                    settleTimePlot.Plot.XAxis.Color(primaryColor);
                    settleTimePlot.Plot.YAxis.Color(primaryColor);
                    settleTimePlot.Plot.XAxis.TickLabelStyle(color: primaryColor);
                    settleTimePlot.Plot.YAxis.TickLabelStyle(color: primaryColor);
                    settleTimePlot.Plot.Grid(color: System.Drawing.Color.FromArgb(50, primaryColor.R, primaryColor.G, primaryColor.B));

                    // ✅ Attach tooltip event handlers
                    AttachSettleTimeTooltip(settleTimePlot);

                    Logger.Info("SettleTimePlot created (lazy loading)");
                }
                return settleTimePlot;
            }
            set {
                settleTimePlot = value;
                RaisePropertyChanged();
            }
        }

        // Formatter functions (kept for potential UI binding)
        public Func<double, string> XFormatter { get; set; } = value => value.ToString("F1");
        public Func<double, string> YFormatter { get; set; } = value => value.ToString("F1");
        public Func<double, string> SettleTimeFormatter { get; set; } = value => value.ToString("F2");

        #endregion

        #region Properties - Statistics

        private double averageSettleTime;
        public double AverageSettleTime {
            get => averageSettleTime;
            set {
                averageSettleTime = value;
                RaisePropertyChanged();
            }
        }

        private double medianSettleTime;
        public double MedianSettleTime {
            get => medianSettleTime;
            set {
                medianSettleTime = value;
                RaisePropertyChanged();
            }
        }

        private double minSettleTime;
        public double MinSettleTime {
            get => minSettleTime;
            set {
                minSettleTime = value;
                RaisePropertyChanged();
            }
        }

        private double maxSettleTime;
        public double MaxSettleTime {
            get => maxSettleTime;
            set {
                maxSettleTime = value;
                RaisePropertyChanged();
            }
        }

        private double stdDevSettleTime;
        public double StdDevSettleTime {
            get => stdDevSettleTime;
            set {
                stdDevSettleTime = value;
                RaisePropertyChanged();
            }
        }

        private int totalDithers;
        public int TotalDithers {
            get => totalDithers;
            set {
                totalDithers = value;
                RaisePropertyChanged();
            }
        }

        private int successfulDithers;
        public int SuccessfulDithers {
            get => successfulDithers;
            set {
                successfulDithers = value;
                RaisePropertyChanged();
            }
        }

        private double successRate;
        public double SuccessRate {
            get => successRate;
            set {
                successRate = value;
                RaisePropertyChanged();
            }
        }

        // Total Drift Properties - Range of distribution
        private double totalDriftX;
        public double TotalDriftX {
            get => totalDriftX;
            set {
                totalDriftX = value;
                RaisePropertyChanged();
            }
        }

        private double totalDriftY;
        public double TotalDriftY {
            get => totalDriftY;
            set {
                totalDriftY = value;
                RaisePropertyChanged();
            }
        }

        #endregion

        #region Properties - Tooltip Display

        private string pixelShiftTooltipText = "";
        public string PixelShiftTooltipText {
            get => pixelShiftTooltipText;
            set {
                pixelShiftTooltipText = value;
                RaisePropertyChanged();
            }
        }

        private bool pixelShiftTooltipVisible = false;
        public bool PixelShiftTooltipVisible {
            get => pixelShiftTooltipVisible;
            set {
                pixelShiftTooltipVisible = value;
                RaisePropertyChanged();
            }
        }

        private string settleTimeTooltipText = "";
        public string SettleTimeTooltipText {
            get => settleTimeTooltipText;
            set {
                settleTimeTooltipText = value;
                RaisePropertyChanged();
            }
        }

        private bool settleTimeTooltipVisible = false;
        public bool SettleTimeTooltipVisible {
            get => settleTimeTooltipVisible;
            set {
                settleTimeTooltipVisible = value;
                RaisePropertyChanged();
            }
        }

        #endregion

        #region Properties - Quality Assessment Toggle

        private bool isQualityAssessmentEnabled;
        public bool IsQualityAssessmentEnabled {
            get => isQualityAssessmentEnabled;
            set {
                isQualityAssessmentEnabled = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(ShowQualityAssessment));
                RaisePropertyChanged(nameof(ShowQualityDisabledMessage));

                // Save to profile settings
                SaveQualityAssessmentSetting();
            }
        }

        // Computed property: Show quality assessment only if enabled AND has data
        public bool ShowQualityAssessment => IsQualityAssessmentEnabled && HasQualityData;

        // Show disabled message when toggle is OFF (regardless of data availability)
        public bool ShowQualityDisabledMessage => !IsQualityAssessmentEnabled;

        private void LoadQualityAssessmentSetting() {
            try {
                var value = settingsStore.ReadBool(PluginSettingsStore.QualityAssessmentFileName);
                if (value.HasValue) {
                    IsQualityAssessmentEnabled = value.Value;
                    Logger.Info($"Quality Assessment setting loaded from file: {IsQualityAssessmentEnabled}");
                    return;
                }

                // Default: OFF
                IsQualityAssessmentEnabled = false;
                Logger.Info("Quality Assessment setting file not found, using default: OFF");
            } catch (Exception ex) {
                Logger.Error($"Error loading Quality Assessment setting: {ex.Message}");
                IsQualityAssessmentEnabled = false;
            }
        }

        private void SaveQualityAssessmentSetting() {
            try {
                settingsStore.WriteBool(PluginSettingsStore.QualityAssessmentFileName, IsQualityAssessmentEnabled);
                Logger.Info($"Quality Assessment setting saved to file: {IsQualityAssessmentEnabled}");
            } catch (Exception ex) {
                Logger.Error($"Error saving Quality Assessment setting: {ex.Message}");
            }
        }

        // Drizzle pixfrac and guider->main-camera pixel scale conversion for the
        // quality metrics; persisted as key=value lines (same folder as settings.txt)
        private double qualityPixfrac = 0.6;
        public double QualityPixfrac {
            get => qualityPixfrac;
            set {
                if (value > 0 && value <= 1.0) {
                    qualityPixfrac = value;
                }
                RaisePropertyChanged();
                SaveQualityMetricSettings();
                UpdateQualityMetrics();
            }
        }

        private double pixelScaleRatioOverride = 0.0;
        /// <summary>Manual main-cam-px per guider-px factor; 0 = derive automatically</summary>
        public double PixelScaleRatioOverride {
            get => pixelScaleRatioOverride;
            set {
                if (value >= 0) {
                    pixelScaleRatioOverride = value;
                }
                RaisePropertyChanged();
                SaveQualityMetricSettings();
                UpdateQualityMetrics();
            }
        }

        // How the effective ratio was determined; shown in the panel so a fallback
        // value of 1.00 is not mistaken for a measured ratio
        private string pixelScaleRatioSource = "fallback";
        private bool hasLoggedRatioFallback = false;

        /// <summary>
        /// Main-camera pixels per guide-camera pixel, evaluated fresh on every
        /// calculation so NINA profile switches and reconnects are picked up.
        /// Manual override wins; otherwise the guider scale comes from NINA's
        /// GuiderInfo (pushed via IGuiderConsumer, primary) or PHD2 get_pixel_scale
        /// (secondary), and the main-camera scale from the active NINA profile
        /// (arcsec/px = 206.265 * pixelSize[µm] / focalLength[mm]). Falls back to 1.0.
        /// </summary>
        private double GetPixelScaleRatio() {
            if (pixelScaleRatioOverride > 0) {
                pixelScaleRatioSource = "manual";
                return pixelScaleRatioOverride;
            }

            try {
                double guiderScale = ninaGuiderPixelScale > 0
                    ? ninaGuiderPixelScale
                    : (phd2Client?.GuiderPixelScaleArcsec ?? 0);
                double pixelSize = profileService?.ActiveProfile?.CameraSettings?.PixelSize ?? 0;
                double focalLength = profileService?.ActiveProfile?.TelescopeSettings?.FocalLength ?? 0;

                if (guiderScale > 0 && pixelSize > 0 && focalLength > 0) {
                    double mainScale = 206.265 * pixelSize / focalLength;
                    double ratio = guiderScale / mainScale;
                    if (ratio > 0.01 && ratio < 100) {
                        pixelScaleRatioSource = ninaGuiderPixelScale > 0 ? "auto/NINA" : "auto/PHD2";
                        hasLoggedRatioFallback = false;
                        return ratio;
                    }
                    Logger.Warning($"Implausible pixel scale ratio {ratio:F2} (guider {guiderScale:F2}\"/px, main {mainScale:F2}\"/px), using 1.0");
                } else if (!hasLoggedRatioFallback) {
                    Logger.Info($"Pixel scale ratio fallback (1.0). Missing: " +
                        $"guiderScale={(guiderScale > 0 ? guiderScale.ToString("F2") : "n/a (guider not connected in NINA?)")}, " +
                        $"pixelSize={(pixelSize > 0 ? pixelSize.ToString("F2") : "n/a (set camera pixel size in NINA options!)")}, " +
                        $"focalLength={(focalLength > 0 ? focalLength.ToString("F0") : "n/a (set telescope focal length in NINA options!)")}");
                    hasLoggedRatioFallback = true;
                }
            } catch (Exception ex) {
                Logger.Warning($"Could not derive pixel scale ratio: {ex.Message}");
            }
            pixelScaleRatioSource = "fallback";
            return 1.0;
        }

        private void LoadQualityMetricSettings() {
            try {
                var parsed = settingsStore.ReadQualityMetricSettings();
                if (parsed == null) return;

                if (parsed.Value.Pixfrac.HasValue && parsed.Value.Pixfrac > 0 && parsed.Value.Pixfrac <= 1.0) {
                    qualityPixfrac = parsed.Value.Pixfrac.Value;
                }
                if (parsed.Value.ScaleRatioOverride.HasValue && parsed.Value.ScaleRatioOverride >= 0) {
                    pixelScaleRatioOverride = parsed.Value.ScaleRatioOverride.Value;
                }
                Logger.Info($"Quality metric settings loaded: pixfrac={qualityPixfrac:F2}, scaleRatioOverride={pixelScaleRatioOverride:F2}");
            } catch (Exception ex) {
                Logger.Error($"Error loading quality metric settings: {ex.Message}");
            }
        }

        private void SaveQualityMetricSettings() {
            try {
                settingsStore.WriteQualityMetricSettings(qualityPixfrac, pixelScaleRatioOverride);
            } catch (Exception ex) {
                Logger.Error($"Error saving quality metric settings: {ex.Message}");
            }
        }

        #endregion

        #region Properties - Dither Settings Optimizer

        private bool isDitherOptimizerEnabled;
        public bool IsDitherOptimizerEnabled {
            get => isDitherOptimizerEnabled;
            set {
                isDitherOptimizerEnabled = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(ShowDitherOptimizer));
                RaisePropertyChanged(nameof(ShowDitherOptimizerDisabledMessage));
                SaveDitherOptimizerSetting();
            }
        }

        private DitherSettingsRecommendation recommendation;
        public DitherSettingsRecommendation Recommendation {
            get => recommendation;
            set {
                recommendation = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasRecommendationData));
                RaisePropertyChanged(nameof(ShowDitherOptimizer));
                RaisePropertyChanged(nameof(SettlePixelQuality));
                RaisePropertyChanged(nameof(SettlePixelBalanced));
                RaisePropertyChanged(nameof(SettlePixelPerformance));
                RaisePropertyChanged(nameof(SettleArcsecQuality));
                RaisePropertyChanged(nameof(SettleArcsecBalanced));
                RaisePropertyChanged(nameof(SettleArcsecPerformance));
                RaisePropertyChanged(nameof(ExpectedSettleQuality));
                RaisePropertyChanged(nameof(ExpectedSettleBalanced));
                RaisePropertyChanged(nameof(ExpectedSettlePerformance));
                RaisePropertyChanged(nameof(SettleTimeoutQuality));
                RaisePropertyChanged(nameof(SettleTimeoutBalanced));
                RaisePropertyChanged(nameof(SettleTimeoutPerformance));
                RaisePropertyChanged(nameof(RecommendationInfo));
                RaisePropertyChanged(nameof(RecommendationWarning));
                RaisePropertyChanged(nameof(HasRecommendationWarning));
            }
        }

        public bool HasRecommendationData => Recommendation != null && Recommendation.DitherEventsAnalyzed >= 3;
        public bool ShowDitherOptimizer => IsDitherOptimizerEnabled && HasRecommendationData;
        public bool ShowDitherOptimizerDisabledMessage => !IsDitherOptimizerEnabled;

        public string SettlePixelQuality => Recommendation != null
            ? $"{Recommendation.SettlePixelTolerance_Quality:F2}"
            : "N/A";

        public string SettlePixelBalanced => Recommendation != null
            ? $"{Recommendation.SettlePixelTolerance_Balanced:F2}"
            : "N/A";

        public string SettlePixelPerformance => Recommendation != null
            ? $"{Recommendation.SettlePixelTolerance_Performance:F2}"
            : "N/A";

        // Tolerance in arcsec next to the pixel value (empty while the guider pixel scale is unknown)
        private string FormatArcsec(double tolerancePx) =>
            Recommendation != null && Recommendation.GuiderPixelScaleArcsec > 0
                ? $"≈ {tolerancePx * Recommendation.GuiderPixelScaleArcsec:F2}\""
                : "";

        public string SettleArcsecQuality => Recommendation != null ? FormatArcsec(Recommendation.SettlePixelTolerance_Quality) : "";
        public string SettleArcsecBalanced => Recommendation != null ? FormatArcsec(Recommendation.SettlePixelTolerance_Balanced) : "";
        public string SettleArcsecPerformance => Recommendation != null ? FormatArcsec(Recommendation.SettlePixelTolerance_Performance) : "";

        // Median time from dither until guiding stayed below the tolerance (info per profile)
        public string ExpectedSettleQuality => Recommendation != null && Recommendation.ExpectedSettleDuration_Quality > 0
            ? $"{Recommendation.ExpectedSettleDuration_Quality:F1}"
            : "N/A";

        public string ExpectedSettleBalanced => Recommendation != null && Recommendation.ExpectedSettleDuration_Balanced > 0
            ? $"{Recommendation.ExpectedSettleDuration_Balanced:F1}"
            : "N/A";

        public string ExpectedSettlePerformance => Recommendation != null && Recommendation.ExpectedSettleDuration_Performance > 0
            ? $"{Recommendation.ExpectedSettleDuration_Performance:F1}"
            : "N/A";

        public string SettleTimeoutQuality => Recommendation != null && Recommendation.SettleTimeout_Quality > 0
            ? $"{Recommendation.SettleTimeout_Quality:F0}"
            : "N/A";

        public string SettleTimeoutBalanced => Recommendation != null && Recommendation.SettleTimeout_Balanced > 0
            ? $"{Recommendation.SettleTimeout_Balanced:F0}"
            : "N/A";

        public string SettleTimeoutPerformance => Recommendation != null && Recommendation.SettleTimeout_Performance > 0
            ? $"{Recommendation.SettleTimeout_Performance:F0}"
            : "N/A";

        public string RecommendationInfo {
            get {
                if (Recommendation == null) return "";
                string excluded = Recommendation.ExcludedSeries > 0 ? $" ({Recommendation.ExcludedSeries} excluded)" : "";
                string minSettle = Recommendation.MinSettleTime_Balanced > 0
                    ? $" | Min settle time: {Recommendation.MinSettleTime_Balanced:F1}s (all profiles)"
                    : "";
                return $"Based on {Recommendation.DitherEventsAnalyzed} dither events{excluded}{minSettle} | Guide exposure: {Recommendation.GuideExposure:F1}s";
            }
        }

        public string RecommendationWarning {
            get {
                var rec = Recommendation;
                if (rec == null) return "";
                var parts = new List<string>();
                if (rec.SeriesUsed_Balanced > 0 && rec.SeriesUsed_Balanced < 5) {
                    parts.Add($"only {rec.SeriesUsed_Balanced} usable dither events — values are preliminary");
                }
                if (rec.Unstabilized_Quality > 0) {
                    parts.Add($"{rec.Unstabilized_Quality} dither(s) never stabilized at the strict tolerance");
                }
                if (rec.SettleDelaySpread_Balanced > 0 && rec.ExpectedSettleDuration_Balanced > 0
                    && rec.SettleDelaySpread_Balanced > rec.ExpectedSettleDuration_Balanced) {
                    parts.Add("settle times vary a lot between dithers");
                }
                if (rec.ExcludedSeries > 0) {
                    parts.Add($"{rec.ExcludedSeries} dither(s) excluded (failed settle / star lost)");
                }
                return parts.Count > 0 ? "⚠ " + string.Join("; ", parts) : "";
            }
        }

        public bool HasRecommendationWarning => !string.IsNullOrEmpty(RecommendationWarning);

        private void LoadDitherOptimizerSetting() {
            try {
                var value = settingsStore.ReadBool(PluginSettingsStore.OptimizerFileName);
                if (value.HasValue) {
                    IsDitherOptimizerEnabled = value.Value;
                    Logger.Info($"Dither Optimizer setting loaded from file: {IsDitherOptimizerEnabled}");
                    return;
                }
                IsDitherOptimizerEnabled = false;
                Logger.Info("Dither Optimizer setting file not found, using default: OFF");
            } catch (Exception ex) {
                Logger.Error($"Error loading Dither Optimizer setting: {ex.Message}");
                IsDitherOptimizerEnabled = false;
            }
        }

        private void SaveDitherOptimizerSetting() {
            try {
                settingsStore.WriteBool(PluginSettingsStore.OptimizerFileName, IsDitherOptimizerEnabled);
                Logger.Info($"Dither Optimizer setting saved to file: {IsDitherOptimizerEnabled}");
            } catch (Exception ex) {
                Logger.Error($"Error saving Dither Optimizer setting: {ex.Message}");
            }
        }

        private void OnDitherRecommendationUpdated(object sender, DitherSettingsRecommendation e) {
            try {
                Application.Current?.Dispatcher.Invoke(() => {
                    Recommendation = e;
                    Logger.Info($"Dither recommendation updated on UI thread - Events: {e.DitherEventsAnalyzed}");
                });

                // The optimizer analysis completes ~30s after the dither settles,
                // so persist again to capture the new data points and recommendation
                SaveStatisticsData();
            } catch (Exception ex) {
                Logger.Error($"Error updating dither recommendation on UI: {ex.Message}");
            }
        }

        #endregion

        #region Properties - Statistics Persistence

        private bool isStatisticsPersistenceEnabled;
        public bool IsStatisticsPersistenceEnabled {
            get => isStatisticsPersistenceEnabled;
            set {
                if (isStatisticsPersistenceEnabled == value) return;
                isStatisticsPersistenceEnabled = value;
                RaisePropertyChanged();
                SaveStatisticsPersistenceSetting();

                if (value) {
                    // Snapshot the current state immediately so a restart right after enabling restores it
                    SaveStatisticsData();
                    // Flush the inactive profiles too so ALL profiles land on disk
                    foreach (var entry in profileDataService.GetInMemoryProfiles()) {
                        SaveProfileDataToFile(entry.Key, entry.Value);
                    }
                } else {
                    // Delete all files; in-memory data (including inactive profiles) is kept
                    DeleteStatisticsData();
                }
            }
        }

        private void LoadStatisticsPersistenceSetting() {
            try {
                var value = settingsStore.ReadBool(PluginSettingsStore.PersistenceFileName);
                if (value.HasValue) {
                    // Set the backing field directly - going through the setter would
                    // overwrite the data file with the still-empty statistics before restore
                    isStatisticsPersistenceEnabled = value.Value;
                    RaisePropertyChanged(nameof(IsStatisticsPersistenceEnabled));
                    Logger.Info($"Statistics Persistence setting loaded from file: {isStatisticsPersistenceEnabled}");
                    return;
                }
                isStatisticsPersistenceEnabled = false;
                Logger.Info("Statistics Persistence setting file not found, using default: OFF");
            } catch (Exception ex) {
                Logger.Error($"Error loading Statistics Persistence setting: {ex.Message}");
                isStatisticsPersistenceEnabled = false;
            }
        }

        private void SaveStatisticsPersistenceSetting() {
            try {
                settingsStore.WriteBool(PluginSettingsStore.PersistenceFileName, isStatisticsPersistenceEnabled);
                Logger.Info($"Statistics Persistence setting saved to file: {isStatisticsPersistenceEnabled}");
            } catch (Exception ex) {
                Logger.Error($"Error saving Statistics Persistence setting: {ex.Message}");
            }
        }

        /// <summary>
        /// Persist the complete statistics state of the ACTIVE profile to disk.
        /// Called after every completed dither, on Clear Data and on shutdown,
        /// so the data file always mirrors the current view.
        /// </summary>
        private void SaveStatisticsData() {
            if (!isStatisticsPersistenceEnabled) return;
            SaveProfileDataToFile(selectedProfileName, BuildCurrentSnapshot());
            Logger.Debug($"Statistics data persisted ({ditherEvents.Count} dither events, profile '{selectedProfileName}')");
        }

        /// <summary>
        /// Restore the statistics state from the previous session (constructor only).
        /// Chart updates are deferred to the UI thread because the WpfPlot
        /// instances must not be created from the VM constructor.
        /// </summary>
        private void RestoreStatisticsData() {
            if (!isStatisticsPersistenceEnabled) return;
            try {
                if (!profileDataService.ProfileDataFileExists(selectedProfileName)) {
                    Logger.Info("Statistics persistence enabled but no data file found - starting with empty statistics");
                    return;
                }

                var data = profileDataService.LoadProfileDataFromFile(selectedProfileName);
                if (data == null) return;

                foreach (var evt in data.DitherEvents ?? new List<DitherEvent>()) {
                    ditherEvents.Add(evt);
                }
                settleTimeValues.AddRange(data.SettleTimeValues ?? new List<double>());
                pixelShiftValues.AddRange(data.PixelShiftValues ?? new List<PixelShiftPoint>());
                cumulativeX = data.CumulativeX;
                cumulativeY = data.CumulativeY;

                // Restore optimizer raw data so new dithers accumulate on the analysis
                optimizerService?.RestoreDitherAnalysisData(data.OptimizerData);

                var restoredRecommendation = data.Recommendation;
                Application.Current?.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(() => {
                        if (restoredRecommendation != null) {
                            Recommendation = restoredRecommendation;
                        }
                        UpdateSettleTimeChart();
                        UpdatePixelShiftChart();
                        UpdateStatistics();
                        UpdateQualityMetrics();
                    }));

                Logger.Info($"Restored {ditherEvents.Count} dither events from previous session");
            } catch (Exception ex) {
                Logger.Error($"Error restoring statistics data: {ex.Message}");
            }
        }

        private void DeleteStatisticsData() {
            try {
                // Includes the legacy single data file (pre-1.5)
                profileDataService.DeleteAllStatisticsDataFiles();
                Logger.Info("Persisted statistics data deleted (all profiles)");
            } catch (Exception ex) {
                Logger.Error($"Error deleting statistics data: {ex.Message}");
            }
        }

        #endregion

        #region Properties - Statistics Profiles

        public const string DefaultProfileName = StatisticsProfileService.DefaultProfileName;

        public ObservableCollection<string> ProfileNames { get; } =
            new ObservableCollection<string> { DefaultProfileName };

        private bool isMultiProfileEnabled;
        public bool IsMultiProfileEnabled {
            get => isMultiProfileEnabled;
            set {
                if (isMultiProfileEnabled == value) return;
                isMultiProfileEnabled = value;
                RaisePropertyChanged();
                SaveMultiProfileSetting();

                if (!value && !string.Equals(selectedProfileName, DefaultProfileName, StringComparison.OrdinalIgnoreCase)) {
                    // Turning the feature off returns to the Default profile;
                    // other profiles keep their data in the store/files for re-enabling
                    SelectedProfileName = DefaultProfileName;
                }
            }
        }

        private string selectedProfileName = DefaultProfileName;
        public string SelectedProfileName {
            get => selectedProfileName;
            set {
                // An editable ComboBox can push null or partial text while the user types
                if (string.IsNullOrWhiteSpace(value)) return;
                var known = ProfileNames.FirstOrDefault(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase));
                if (known == null) return;
                if (string.Equals(selectedProfileName, known, StringComparison.OrdinalIgnoreCase)) return;

                SwitchToProfile(known);
                RaisePropertyChanged();
                SaveProfileListSetting();
                ProfileNameInput = known;
            }
        }

        private string profileNameInput = DefaultProfileName;
        public string ProfileNameInput {
            get => profileNameInput;
            set {
                profileNameInput = value;
                RaisePropertyChanged();
            }
        }

        private void LoadMultiProfileSetting() {
            try {
                var value = settingsStore.ReadBool(PluginSettingsStore.MultiProfileFileName);
                if (value.HasValue) {
                    // Set the backing field directly - the setter would trigger a profile switch
                    isMultiProfileEnabled = value.Value;
                    RaisePropertyChanged(nameof(IsMultiProfileEnabled));
                    Logger.Info($"Multi-profile setting loaded from file: {isMultiProfileEnabled}");
                    return;
                }
                isMultiProfileEnabled = false;
                Logger.Info("Multi-profile setting file not found, using default: OFF");
            } catch (Exception ex) {
                Logger.Error($"Error loading multi-profile setting: {ex.Message}");
                isMultiProfileEnabled = false;
            }
        }

        private void SaveMultiProfileSetting() {
            try {
                settingsStore.WriteBool(PluginSettingsStore.MultiProfileFileName, isMultiProfileEnabled);
                Logger.Info($"Multi-profile setting saved to file: {isMultiProfileEnabled}");
            } catch (Exception ex) {
                Logger.Error($"Error saving multi-profile setting: {ex.Message}");
            }
        }

        private void LoadProfileListSetting() {
            try {
                var (selected, names) = settingsStore.ReadProfileList(DefaultProfileName);

                // Self-heal from existing profile data files when persistence is on
                if (isStatisticsPersistenceEnabled) {
                    names.AddRange(profileDataService.GetProfileNamesFromDataFiles());
                }

                ProfileNames.Clear();
                ProfileNames.Add(DefaultProfileName);
                foreach (var name in names) {
                    if (!ProfileNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) {
                        ProfileNames.Add(name);
                    }
                }

                var match = ProfileNames.FirstOrDefault(n => string.Equals(n, selected, StringComparison.OrdinalIgnoreCase));
                // Backing field only - no switch during construction; force Default when the feature is off
                selectedProfileName = isMultiProfileEnabled ? (match ?? DefaultProfileName) : DefaultProfileName;
                profileNameInput = selectedProfileName;
                Logger.Info($"Profile list loaded: {ProfileNames.Count} profile(s), selected '{selectedProfileName}'");
            } catch (Exception ex) {
                Logger.Error($"Error loading profile list: {ex.Message}");
                ProfileNames.Clear();
                ProfileNames.Add(DefaultProfileName);
                selectedProfileName = DefaultProfileName;
                profileNameInput = DefaultProfileName;
            }
        }

        private void SaveProfileListSetting() {
            try {
                settingsStore.WriteProfileList(selectedProfileName, ProfileNames);
            } catch (Exception ex) {
                Logger.Error($"Error saving profile list: {ex.Message}");
            }
        }

        /// <summary>
        /// One-time migration of the v1.4 single data file into the per-profile layout.
        /// </summary>
        private void MigrateLegacyStatisticsFile() {
            try {
                switch (profileDataService.MigrateLegacyStatisticsFile()) {
                    case StatisticsProfileService.LegacyMigrationResult.Migrated:
                        Logger.Info("Migrated legacy statistics_data.json to profiles\\Default.json");
                        break;
                    case StatisticsProfileService.LegacyMigrationResult.LegacyDeleted:
                        Logger.Info("Deleted legacy statistics_data.json (Default profile file already exists)");
                        break;
                }
            } catch (Exception ex) {
                Logger.Error($"Error migrating legacy statistics data file: {ex.Message}");
            }
        }

        private PersistedStatisticsData BuildCurrentSnapshot() {
            return new PersistedStatisticsData {
                DitherEvents = ditherEvents.ToList(),
                SettleTimeValues = settleTimeValues.ToList(),
                PixelShiftValues = pixelShiftValues.ToList(),
                CumulativeX = cumulativeX,
                CumulativeY = cumulativeY,
                OptimizerData = optimizerService?.GetDitherAnalysisSnapshot(),
                Recommendation = Recommendation
            };
        }

        /// <summary>
        /// Switch the live statistics to another profile. UI thread only - called
        /// from property setters and commands, which WPF bindings run on the UI thread.
        /// A dither in flight (currentDither) deliberately survives the switch and
        /// lands in the newly selected profile.
        /// </summary>
        private void SwitchToProfile(string newName) {
            var snapshot = BuildCurrentSnapshot();
            profileDataService.StoreInMemory(selectedProfileName, snapshot);
            if (isStatisticsPersistenceEnabled) {
                SaveProfileDataToFile(selectedProfileName, snapshot);
            }

            selectedProfileName = newName;
            if (optimizerService != null) {
                optimizerService.CurrentProfileName = newName;
            }

            if (!profileDataService.TryGetFromMemory(newName, out var data)) {
                data = null;
                if (isStatisticsPersistenceEnabled) {
                    try {
                        data = profileDataService.LoadProfileDataFromFile(newName);
                    } catch (Exception ex) {
                        Logger.Error($"Error loading profile '{newName}' from file: {ex.Message}");
                        data = null;
                    }
                }
                data ??= new PersistedStatisticsData();
            }

            ditherEvents.Clear();
            foreach (var evt in data.DitherEvents ?? new List<DitherEvent>()) {
                ditherEvents.Add(evt);
            }
            settleTimeValues.Clear();
            settleTimeValues.AddRange(data.SettleTimeValues ?? new List<double>());
            pixelShiftValues.Clear();
            pixelShiftValues.AddRange(data.PixelShiftValues ?? new List<PixelShiftPoint>());
            cumulativeX = data.CumulativeX;
            cumulativeY = data.CumulativeY;

            // Clear first: RestoreDitherAnalysisData returns early on empty snapshots and
            // never clears, so the previous profile's optimizer data would leak otherwise
            optimizerService?.ClearDitherAnalysisData();
            optimizerService?.RestoreDitherAnalysisData(data.OptimizerData);
            Recommendation = data.Recommendation;

            UpdateSettleTimeChart();
            UpdatePixelShiftChart();
            UpdateStatistics();
            UpdateQualityMetrics();

            Logger.Info($"Switched to statistics profile '{newName}' ({ditherEvents.Count} dither events)");
        }

        private void SaveProfileDataToFile(string profileName, PersistedStatisticsData data) {
            try {
                profileDataService.SaveProfileDataToFile(profileName, data);
            } catch (Exception ex) {
                Logger.Error($"Error saving profile '{profileName}' data: {ex.Message}");
            }
        }

        private void CreateProfile() {
            var name = StatisticsProfileService.SanitizeProfileName(ProfileNameInput);
            if (name == null) {
                Logger.Warning("Cannot create profile: name is empty or invalid");
                return;
            }

            // Compare sanitized names so two different names cannot collide on the same file
            var existing = ProfileNames.FirstOrDefault(n =>
                string.Equals(StatisticsProfileService.SanitizeProfileName(n), name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) {
                SelectedProfileName = existing;
                return;
            }

            ProfileNames.Add(name);
            SelectedProfileName = name;
            Logger.Info($"Created statistics profile '{name}'");
        }

        private void DeleteProfile() {
            if (string.Equals(selectedProfileName, DefaultProfileName, StringComparison.OrdinalIgnoreCase)) {
                Logger.Warning("The Default profile cannot be deleted");
                return;
            }

            var victim = selectedProfileName;
            SelectedProfileName = DefaultProfileName;
            profileDataService.RemoveFromMemory(victim);
            ProfileNames.Remove(victim);
            try {
                profileDataService.DeleteProfileDataFile(victim);
            } catch (Exception ex) {
                Logger.Error($"Error deleting data file of profile '{victim}': {ex.Message}");
            }
            SaveProfileListSetting();
            Logger.Info($"Deleted statistics profile '{victim}'");
        }

        #endregion

        #region Properties - Quality Metrics

        private DitherQualityMetrics.QualityResult _qualityResult;
        public DitherQualityMetrics.QualityResult QualityResult {
            get => _qualityResult;
            set {
                _qualityResult = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasQualityData));
                RaisePropertyChanged(nameof(ShowQualityAssessment));
                RaisePropertyChanged(nameof(QualityRatingColor));
                RaisePropertyChanged(nameof(CDValue));
                RaisePropertyChanged(nameof(CDRating));
                RaisePropertyChanged(nameof(GFM1xValue));
                RaisePropertyChanged(nameof(GFM2xValue));
                RaisePropertyChanged(nameof(GFM3xValue));
                RaisePropertyChanged(nameof(CombinedScoreValue));
                RaisePropertyChanged(nameof(NNIValue));
                RaisePropertyChanged(nameof(NNIRating));
                RaisePropertyChanged(nameof(DriftValue));
                RaisePropertyChanged(nameof(DriftRating));
                RaisePropertyChanged(nameof(EffectiveScaleRatioText));
            }
        }

        public bool HasQualityData => QualityResult != null && pixelShiftValues.Count >= 4;

        public string QualityRatingColor => QualityResult?.QualityRating switch {
            "Excellent" => "#4CAF50",
            "Very Good" => "#66BB6A",
            "Good" => "#8BC34A",
            "Acceptable" => "#FFC107",
            "Fair" => "#FF9800",
            "Poor" => "#F44336",
            _ => "#9E9E9E"
        };

        public string CDValue => QualityResult != null
            ? $"{QualityResult.CenteredL2Discrepancy:F4}"
            : "N/A";

        public string CDRating => QualityResult != null
            ? GetCDRatingShort(QualityResult.CenteredL2Discrepancy)
            : "";

        public string GFM1xValue => QualityResult != null
            ? $"{QualityResult.GapFillMetric_1x:P1}"
            : "N/A";

        public string GFM2xValue => QualityResult != null
            ? $"{QualityResult.GapFillMetric_2x:P1}"
            : "N/A";

        public string GFM3xValue => QualityResult != null
            ? $"{QualityResult.GapFillMetric_3x:P1}"
            : "N/A";

        // Target Coverage for Gap-Fill Metrics (using centralized thresholds)
        public string GFM1xTarget => $"Target: {DitherQualityMetrics.QualityThresholds.GFM_Target_1x:P0}";
        public string GFM2xTarget => $"Target: {DitherQualityMetrics.QualityThresholds.GFM_Target_2x:P0}";
        public string GFM3xTarget => $"Target: {DitherQualityMetrics.QualityThresholds.GFM_Target_3x:P0}";

        public string DriftValue => QualityResult != null
            ? $"{QualityResult.DriftRatio:F2}"
            : "N/A";

        public string DriftRating => QualityResult != null
            ? GetDriftRatingShort(QualityResult.DriftRatio)
            : "";

        public string EffectiveScaleRatioText => QualityResult != null
            ? $"scale ratio {QualityResult.PixelScaleRatio:F2} ({pixelScaleRatioSource}{(pixelScaleRatioSource == "fallback" ? " - connect guider in NINA & set focal length + camera pixel size in the profile options" : "")}), pixfrac {QualityResult.Pixfrac:F2}"
            : "";

        public string CombinedScoreValue => QualityResult != null
            ? $"{QualityResult.CombinedScore:F3}"
            : "N/A";

        public string NNIValue => QualityResult != null
            ? $"{QualityResult.NearestNeighborIndex:F2}"
            : "N/A";

        public string NNIRating => QualityResult != null
            ? GetNNIRatingShort(QualityResult.NearestNeighborIndex)
            : "";

        #endregion

        #region Commands

        public ICommand ClearDataCommand { get; private set; }
        public ICommand ExportDitherEventsCsvCommand { get; private set; }
        public ICommand RecalculateMetricsCommand { get; private set; }
        public ICommand ExportMetricsCommand { get; private set; }
        public ICommand CreateProfileCommand { get; private set; }
        public ICommand DeleteProfileCommand { get; private set; }

        private void InitializeCommands() {
            ClearDataCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ClearData);
            ExportDitherEventsCsvCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ExportDitherEventsCsv);
            RecalculateMetricsCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(RecalculateQualityMetrics);
            ExportMetricsCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ExportQualityMetrics);
            CreateProfileCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(CreateProfile);
            DeleteProfileCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(DeleteProfile);
        }

        private void ClearData() {
            settleTimeValues.Clear();
            pixelShiftValues.Clear();
            ditherEvents.Clear();

            cumulativeX = 0.0;
            cumulativeY = 0.0;
            currentDither = null;

            // Clear dither optimizer data and recommendation
            optimizerService?.ClearDitherAnalysisData();
            Recommendation = null;

            // Clear ALL profiles, not only the active one (memory + files);
            // the profile names and the selection are kept
            profileDataService.ClearMemory();
            try {
                profileDataService.DeleteAllProfileDataFiles();
            } catch (Exception ex) {
                Logger.Error($"Error deleting profile data files: {ex.Message}");
            }

            UpdateSettleTimeChart();
            UpdatePixelShiftChart();
            UpdateStatistics();
            UpdateQualityMetrics();

            // Persist the cleared state so a restart does not bring the old data back
            SaveStatisticsData();

            Logger.Info("All data cleared");
        }

        private void ExportDitherEventsCsv() {
            if (ditherEvents.Count == 0) {
                Logger.Warning("No dither events to export");
                return;
            }

            try {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string filename = $"DitherEvents_{timestamp}.csv";
                string path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "N.I.N.A",
                    "DitherStatistics",
                    filename
                );

                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));

                // Build CSV content
                var csv = new System.Text.StringBuilder();

                // Header
                csv.AppendLine("DitherNumber,StartTime,EndTime,PixelShiftX,PixelShiftY,CumulativeX,CumulativeY,SettleTime,Success");

                // Data rows
                for (int i = 0; i < ditherEvents.Count; i++) {
                    var evt = ditherEvents[i];
                    csv.AppendLine($"{i + 1}," +
                        $"{evt.StartTime:yyyy-MM-dd HH:mm:ss}," +
                        $"{evt.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A"}," +
                        $"{evt.PixelShiftX?.ToString("F4") ?? "N/A"}," +
                        $"{evt.PixelShiftY?.ToString("F4") ?? "N/A"}," +
                        $"{evt.CumulativeX:F4}," +
                        $"{evt.CumulativeY:F4}," +
                        $"{evt.SettleTime?.ToString("F2") ?? "N/A"}," +
                        $"{evt.Success}");
                }

                System.IO.File.WriteAllText(path, csv.ToString());
                Logger.Info($"Dither events exported to: {path}");
            } catch (Exception ex) {
                Logger.Error($"Error exporting dither events: {ex.Message}");
            }
        }

        private void RecalculateQualityMetrics() {
            UpdateQualityMetrics();
            Logger.Info("Quality metrics manually recalculated");
        }

        private void ExportQualityMetrics() {
            if (QualityResult == null) {
                Logger.Warning("No quality metrics to export");
                return;
            }

            try {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string filename = $"DitherQuality_{timestamp}.txt";
                string path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "N.I.N.A",
                    "DitherStatistics",
                    filename
                );

                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));

                string report = DitherQualityMetrics.FormatMetricsReport(QualityResult);
                System.IO.File.WriteAllText(path, report);

                Logger.Info($"Quality metrics exported to: {path}");
            } catch (Exception ex) {
                Logger.Error($"Error exporting quality metrics: {ex.Message}");
            }
        }

        #endregion

        #region PHD2 Integration

        // Connect/retry/reconnect policy and connection-status logging live in
        // Phd2ConnectionManager (started in the constructor)

        private void OnPHD2GuidingDithered(object sender, PHD2GuidingDitheredEventArgs e) {
            try {
                Logger.Info($"🎯 DITHER START detected via GuidingDithered! dx={e.DeltaX:F2}, dy={e.DeltaY:F2}");

                // Create new dither event with pixel shift data
                currentDither = new DitherEvent {
                    StartTime = DateTime.Now,
                    PixelShiftX = e.DeltaX,
                    PixelShiftY = e.DeltaY,
                    Success = false
                };

                Logger.Info($"✅ Dither START recorded with pixel shift: ({e.DeltaX:F2}, {e.DeltaY:F2})");

            } catch (Exception ex) {
                Logger.Error($"Error handling GuidingDithered: {ex.Message}");
            }
        }

        private void OnPHD2SettleDone(object sender, PHD2SettleDoneEventArgs e) {
            try {
                var dither = currentDither;
                if (dither == null) {
                    Logger.Warning("⚠️ SettleDone received but no currentDither exists (race condition?)");
                    return;
                }

                dither.EndTime = DateTime.Now;
                dither.Success = e.Success;

                if (dither.EndTime.HasValue) {
                    dither.SettleTime = (dither.EndTime.Value - dither.StartTime).TotalSeconds;
                }

                Logger.Info($"✅ DITHER END - Success={e.Success}, SettleTime={dither.SettleTime:F2}s, " +
                    $"TotalFrames={e.TotalFrames}, DroppedFrames={e.DroppedFrames}");

                // All live-state mutation happens on the UI thread (Invoke is synchronous),
                // so it is serialized with profile switches which also run on the UI thread
                Application.Current?.Dispatcher.Invoke(() => {
                    // Update cumulative position
                    if (dither.PixelShiftX.HasValue && dither.PixelShiftY.HasValue) {
                        cumulativeX += dither.PixelShiftX.Value;
                        cumulativeY += dither.PixelShiftY.Value;
                        dither.CumulativeX = cumulativeX;
                        dither.CumulativeY = cumulativeY;
                    }

                    // Add to events collection
                    ditherEvents.Add(dither);

                    if (dither.Success && dither.SettleTime.HasValue) {
                        settleTimeValues.Add(dither.SettleTime.Value);
                        UpdateSettleTimeChart();
                    }

                    if (dither.PixelShiftX.HasValue && dither.PixelShiftY.HasValue) {
                        pixelShiftValues.Add(new PixelShiftPoint(
                            cumulativeX,
                            cumulativeY,
                            dither.PixelShiftX.Value,
                            dither.PixelShiftY.Value
                        ));
                        UpdatePixelShiftChart();
                    }

                    UpdateStatistics();
                    UpdateQualityMetrics();
                });

                // Clear current dither
                currentDither = null;

                // Persist updated statistics if multi-session persistence is enabled
                SaveStatisticsData();

            } catch (Exception ex) {
                Logger.Error($"Error handling SettleDone: {ex.Message}");
                Logger.Error($"Stack trace: {ex.StackTrace}");
            }
        }

        #endregion

        #region NINA Guider Events (Optional Monitoring)

        private void SubscribeToGuiderEvents() {
            if (guiderMediator != null) {
                guiderMediator.RegisterConsumer(this);
                Logger.Info("Subscribed to NINA guider events");
            }
        }

        // Latest guider pixel scale (arcsec/px) pushed by NINA; primary source for
        // the guider->main-camera conversion of the quality metrics
        private double ninaGuiderPixelScale = 0.0;

        public void UpdateDeviceInfo(GuiderInfo deviceInfo) {
            Logger.Debug($"Guider info updated: {deviceInfo?.Name ?? "None"}");

            double scale = deviceInfo?.Connected == true ? deviceInfo.PixelScale : 0.0;
            if (scale > 0 && Math.Abs(scale - ninaGuiderPixelScale) > 0.001) {
                ninaGuiderPixelScale = scale;
                Logger.Info($"Guider pixel scale from NINA: {scale:F2} arcsec/px");

                // Refresh the quality panel so the ratio switches away from fallback
                // without waiting for the next dither
                Application.Current?.Dispatcher.BeginInvoke(new Action(UpdateQualityMetrics));
            }
        }

        #endregion

        #region Quality Metrics

        private void InitializeQualityMetrics() {
            RecalculateMetricsCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(RecalculateQualityMetrics);
            ExportMetricsCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ExportQualityMetrics);
        }

        private void UpdateQualityMetrics() {
            if (pixelShiftValues.Count < 4) {
                QualityResult = null;
                return;
            }

            try {
                // Extract cumulative positions (X, Y) from PixelShiftValues (guide camera px)
                var positions = pixelShiftValues
                    .Select(p => (p.X, p.Y))
                    .ToList();

                double ratio = GetPixelScaleRatio();
                QualityResult = DitherQualityMetrics.CalculateQualityMetrics(positions, QualityPixfrac, ratio);
                Logger.Info($"Quality metrics updated: Score={QualityResult.CombinedScore:F4}, Rating={QualityResult.QualityRating}, ScaleRatio={ratio:F2}, Pixfrac={QualityPixfrac:F2}");

            } catch (Exception ex) {
                Logger.Error($"Error calculating quality metrics: {ex.Message}");
                QualityResult = null;
            }
        }

        private string GetCDRatingShort(double cd) {
            // Uses centralized thresholds from DitherQualityMetrics.QualityThresholds
            if (cd < DitherQualityMetrics.QualityThresholds.CD_Excellent) return "Excellent";
            if (cd < DitherQualityMetrics.QualityThresholds.CD_VeryGood) return "Very Good";
            if (cd < DitherQualityMetrics.QualityThresholds.CD_Good) return "Good";
            if (cd < DitherQualityMetrics.QualityThresholds.CD_Acceptable) return "Acceptable";
            if (cd < DitherQualityMetrics.QualityThresholds.CD_Fair) return "Fair";
            return "Poor";
        }

        private string GetDriftRatingShort(double driftRatio) {
            if (driftRatio < 0.2) return "Stable";
            if (driftRatio < 0.4) return "Low drift";
            if (driftRatio < DitherQualityMetrics.QualityThresholds.DriftRatio_Warning) return "Moderate";
            return "High drift!";
        }

        private string GetNNIRatingShort(double nni) {
            // Uses centralized thresholds from DitherQualityMetrics.QualityThresholds
            if (nni > DitherQualityMetrics.QualityThresholds.NNI_Excellent) return "Excellent";
            if (nni > DitherQualityMetrics.QualityThresholds.NNI_Good) return "Good";
            if (nni > DitherQualityMetrics.QualityThresholds.NNI_Acceptable) return "Acceptable";
            if (nni > DitherQualityMetrics.QualityThresholds.NNI_Fair) return "Fair";
            return "Poor";
        }

        #endregion

        #region ScottPlot Chart Management

        // ✅ InitializePlots() removed - no longer needed with lazy loading properties
        // Plots are created automatically when XAML accesses the properties (on UI thread)

        private void UpdatePixelShiftChart() {
            try {
                if (PixelShiftPlot == null) return;

                // Clear plot
                PixelShiftPlot.Plot.Clear();

                // Update colors dynamically (in case theme changed)
                UpdateChartColors(PixelShiftPlot);

                if (pixelShiftValues.Count == 0) {
                    PixelShiftPlot.Render();
                    return;
                }

                // Extract data
                double[] xData = pixelShiftValues.Select(p => p.X).ToArray();
                double[] yData = pixelShiftValues.Select(p => p.Y).ToArray();

                // Add connection line (thin, semi-transparent)
                if (pixelShiftValues.Count > 1) {
                    var connectionLine = PixelShiftPlot.Plot.AddScatter(xData, yData);
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

                    var scatter = PixelShiftPlot.Plot.AddScatter(
                        new double[] { xData[i] },
                        new double[] { yData[i] }
                    );
                    scatter.Color = pointColor;
                    scatter.MarkerSize = 6;
                    scatter.MarkerShape = MarkerShape.filledCircle;
                    scatter.LineWidth = 0;
                }

                // Highlight last point in lime green
                if (pixelShiftValues.Count > 0) {
                    int lastIndex = pixelShiftValues.Count - 1;
                    var lastPoint = PixelShiftPlot.Plot.AddScatter(
                        new double[] { xData[lastIndex] },
                        new double[] { yData[lastIndex] }
                    );
                    lastPoint.Color = System.Drawing.Color.Lime;
                    lastPoint.MarkerSize = 8;
                    lastPoint.MarkerShape = MarkerShape.filledCircle;
                    lastPoint.LineWidth = 0;
                    lastPoint.Label = "Latest";
                }

                // Add crosshair at origin with dynamic color
                var primaryColor = GetThemeColor("PrimaryBrush", System.Drawing.Color.White);
                PixelShiftPlot.Plot.AddVerticalLine(0, primaryColor, 2);
                PixelShiftPlot.Plot.AddHorizontalLine(0, primaryColor, 2);

                // Auto-scale and refresh
                PixelShiftPlot.Plot.AxisAuto();
                PixelShiftPlot.Render();

            } catch (Exception ex) {
                Logger.Error($"Error updating pixel shift chart: {ex.Message}");
            }
        }

        private void UpdateSettleTimeChart() {
            try {
                if (SettleTimePlot == null) return;

                // Clear plot
                SettleTimePlot.Plot.Clear();

                // Update colors dynamically (in case theme changed)
                UpdateChartColors(SettleTimePlot);

                if (settleTimeValues.Count == 0) {
                    SettleTimePlot.Render();
                    return;
                }

                // X-axis: Dither numbers (1, 2, 3, ...)
                double[] xData = Enumerable.Range(1, settleTimeValues.Count).Select(i => (double)i).ToArray();
                double[] yData = settleTimeValues.ToArray();

                // Add main settle time line
                var settleTimeLine = SettleTimePlot.Plot.AddScatter(xData, yData);
                settleTimeLine.Color = System.Drawing.Color.DodgerBlue;
                settleTimeLine.LineWidth = 2;
                settleTimeLine.MarkerSize = 8;
                settleTimeLine.MarkerShape = MarkerShape.filledCircle;
                settleTimeLine.Label = "Settle Time";

                // Add average line if we have statistics
                if (AverageSettleTime > 0 && settleTimeValues.Count > 0) {
                    double[] avgData = Enumerable.Repeat(AverageSettleTime, settleTimeValues.Count).ToArray();
                    var avgLine = SettleTimePlot.Plot.AddScatter(xData, avgData);
                    avgLine.Color = System.Drawing.Color.Red;
                    avgLine.LineWidth = 2;
                    avgLine.MarkerSize = 0;
                    avgLine.LineStyle = LineStyle.Dash;
                    avgLine.Label = "Average";

                    // Add Avg ± StdDev lines
                    if (StdDevSettleTime > 0) {
                        double[] lowerData = Enumerable.Repeat(Math.Max(0, AverageSettleTime - StdDevSettleTime), settleTimeValues.Count).ToArray();
                        double[] upperData = Enumerable.Repeat(AverageSettleTime + StdDevSettleTime, settleTimeValues.Count).ToArray();

                        var lowerLine = SettleTimePlot.Plot.AddScatter(xData, lowerData);
                        lowerLine.Color = System.Drawing.Color.FromArgb(120, 255, 0, 0);
                        lowerLine.LineWidth = 2;
                        lowerLine.MarkerSize = 0;
                        lowerLine.LineStyle = LineStyle.Dot;
                        lowerLine.Label = "Avg - StdDev";

                        var upperLine = SettleTimePlot.Plot.AddScatter(xData, upperData);
                        upperLine.Color = System.Drawing.Color.FromArgb(120, 255, 0, 0);
                        upperLine.LineWidth = 2;
                        upperLine.MarkerSize = 0;
                        upperLine.LineStyle = LineStyle.Dot;
                        upperLine.Label = "Avg + StdDev";
                    }
                }

                // Disable built-in legend (we'll show it below the chart in XAML)
                SettleTimePlot.Plot.Legend(enable: false);

                // Auto-scale (will respect 0 as minimum) and refresh
                SettleTimePlot.Plot.AxisAuto();
                SettleTimePlot.Plot.SetAxisLimits(yMin: 0);
                SettleTimePlot.Render();

            } catch (Exception ex) {
                Logger.Error($"Error updating settle time chart: {ex.Message}");
            }
        }

        /// <summary>
        /// Attach tooltip functionality to Pixel Shift Plot
        /// Tooltip shows: "ΔX: 2.71 px  ΔY: -3.29 px"
        /// </summary>
        private void AttachPixelShiftTooltip(ScottPlot.WpfPlot plot) {
            plot.MouseMove += (s, e) => {
                try {
                    if (pixelShiftValues.Count == 0) {
                        PixelShiftTooltipVisible = false;
                        return;
                    }

                    // Get mouse coordinates in plot space
                    var mouseCoords = plot.GetMouseCoordinates();

                    // Find nearest point
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

                    // Show tooltip if within reasonable distance
                    if (nearestIndex >= 0) {
                        var axisLimits = plot.Plot.GetAxisLimits();
                        double xRange = axisLimits.XMax - axisLimits.XMin;
                        double yRange = axisLimits.YMax - axisLimits.YMin;
                        double threshold = Math.Sqrt(xRange * xRange + yRange * yRange) * 0.05; // 5% of diagonal

                        if (minDistance < threshold) {
                            var point = pixelShiftValues[nearestIndex];
                            PixelShiftTooltipText = $"ΔX: {point.DeltaX:F2} px  ΔY: {point.DeltaY:F2} px";
                            PixelShiftTooltipVisible = true;
                        } else {
                            PixelShiftTooltipVisible = false;
                        }
                    } else {
                        PixelShiftTooltipVisible = false;
                    }
                } catch (Exception ex) {
                    Logger.Error($"Error in PixelShift tooltip: {ex.Message}");
                }
            };

            plot.MouseLeave += (s, e) => {
                PixelShiftTooltipVisible = false;
            };
        }

        /// <summary>
        /// Attach tooltip functionality to Settle Time Plot
        /// Tooltip shows: "Dither #5: 12.34s"
        /// </summary>
        private void AttachSettleTimeTooltip(ScottPlot.WpfPlot plot) {
            plot.MouseMove += (s, e) => {
                try {
                    if (settleTimeValues.Count == 0) {
                        SettleTimeTooltipVisible = false;
                        return;
                    }

                    // Get mouse coordinates in plot space
                    var mouseCoords = plot.GetMouseCoordinates();

                    // Find nearest point (X coordinate is dither number: 1, 2, 3, ...)
                    int ditherNumber = (int)Math.Round(mouseCoords.x);
                    int index = ditherNumber - 1; // Convert to 0-based index

                    if (index >= 0 && index < settleTimeValues.Count) {
                        double settleTime = settleTimeValues[index];

                        // Calculate distance to check if mouse is close enough
                        double yValue = settleTime;
                        double distance = Math.Abs(mouseCoords.y - yValue);

                        var axisLimits = plot.Plot.GetAxisLimits();
                        double yRange = axisLimits.YMax - axisLimits.YMin;
                        double threshold = yRange * 0.1; // 10% of Y range

                        if (distance < threshold) {
                            SettleTimeTooltipText = $"Dither #{ditherNumber}: {settleTime:F2}s";
                            SettleTimeTooltipVisible = true;
                        } else {
                            SettleTimeTooltipVisible = false;
                        }
                    } else {
                        SettleTimeTooltipVisible = false;
                    }
                } catch (Exception ex) {
                    Logger.Error($"Error in SettleTime tooltip: {ex.Message}");
                }
            };

            plot.MouseLeave += (s, e) => {
                SettleTimeTooltipVisible = false;
            };
        }

        #endregion

        #region Statistics

        private void UpdateStatistics() {
            var successfulEvents = ditherEvents.Where(d => d.Success && d.SettleTime.HasValue).ToList();

            TotalDithers = ditherEvents.Count;
            SuccessfulDithers = successfulEvents.Count;
            SuccessRate = TotalDithers > 0 ? (double)SuccessfulDithers / TotalDithers * 100 : 0;

            if (successfulEvents.Any()) {
                var settleTimes = successfulEvents.Select(d => d.SettleTime.Value).ToList();

                AverageSettleTime = DitherStatistics.CalculateAverage(settleTimes);
                MedianSettleTime = DitherStatistics.CalculateMedian(settleTimes);
                MinSettleTime = settleTimes.Min();
                MaxSettleTime = settleTimes.Max();
                StdDevSettleTime = DitherStatistics.CalculateStdDev(settleTimes);

                // Update settle time chart with new average/stddev lines
                UpdateSettleTimeChart();
            } else {
                AverageSettleTime = 0;
                MedianSettleTime = 0;
                MinSettleTime = 0;
                MaxSettleTime = 0;
                StdDevSettleTime = 0;
            }

            // Calculate Total Drift as range (Max - Min) across all points
            if (pixelShiftValues.Count > 0) {
                var xValues = pixelShiftValues.Select(p => p.X).ToList();
                var yValues = pixelShiftValues.Select(p => p.Y).ToList();

                TotalDriftX = xValues.Max() - xValues.Min();
                TotalDriftY = yValues.Max() - yValues.Min();
            } else {
                TotalDriftX = 0.0;
                TotalDriftY = 0.0;
            }
        }

        #endregion

        #region IDockableVM Implementation

        private string title = "Dither Statistics";
        public string Title {
            get => title;
            set {
                title = value;
                RaisePropertyChanged();
            }
        }

        private string contentId = "DitherStatistics_Panel";
        public string ContentId {
            get => contentId;
            set {
                contentId = value;
                RaisePropertyChanged();
            }
        }

        private GeometryGroup imageGeometry;
        public GeometryGroup ImageGeometry {
            get => imageGeometry;
            set {
                imageGeometry = value;
                RaisePropertyChanged();
            }
        }

        private bool isClosed = false;
        public bool IsClosed {
            get => isClosed;
            set {
                isClosed = value;
                RaisePropertyChanged();
            }
        }

        private bool isVisible = true;
        public bool IsVisible {
            get => isVisible;
            set {
                isVisible = value;
                RaisePropertyChanged();
            }
        }

        private bool hasSettings = false;
        public bool HasSettings {
            get => hasSettings;
            set {
                hasSettings = value;
                RaisePropertyChanged();
            }
        }

        private bool canClose = true;
        public bool CanClose {
            get => canClose;
            set {
                canClose = value;
                RaisePropertyChanged();
            }
        }

        private bool isTool = false;
        public bool IsTool {
            get => isTool;
            set {
                isTool = value;
                RaisePropertyChanged();
            }
        }

        public ICommand HideCommand => new CommunityToolkit.Mvvm.Input.RelayCommand<object>(Hide);

        public ICommand ToggleSettingsCommand => new CommunityToolkit.Mvvm.Input.RelayCommand<object>(ToggleSettings);

        public void Hide(object parameter) {
            // Toggle visibility instead of just hiding
            // This allows the icon button to show/hide the panel
            IsVisible = !IsVisible;
        }

        public void ToggleSettings(object parameter) {
            // No settings for now
        }

        #endregion

        #region Cleanup

        public void Dispose() {
            try {
                // Persist final statistics state before shutdown
                SaveStatisticsData();

                // Defensive: flush the inactive profiles too (normally already
                // written at switch time)
                if (isStatisticsPersistenceEnabled) {
                    foreach (var entry in profileDataService.GetInMemoryProfiles()) {
                        SaveProfileDataToFile(entry.Key, entry.Value);
                    }
                }

                // Stop theme color monitoring timer
                if (themeColorTimer != null) {
                    themeColorTimer.Tick -= OnThemeColorTimerTick;
                    themeColorTimer.Stop();
                    themeColorTimer = null;
                    Logger.Info("Theme color monitoring timer stopped");
                }

                // Dispose order matters: the client's Disconnect fires "Disconnected",
                // which lets the optimizer abort its running collection window (the
                // snapshot above already captured the in-progress points)
                phd2Client?.Dispose();
                optimizerService?.Dispose();

                if (guiderMediator != null) {
                    guiderMediator.RemoveConsumer(this);
                }

                PixelShiftPlot?.Plot?.Clear();
                SettleTimePlot?.Plot?.Clear();

                Logger.Info("DitherStatisticsVM disposed");
            } catch (Exception ex) {
                Logger.Error($"Error disposing: {ex.Message}");
            }
        }

        /// <summary>
        /// Start monitoring theme color changes to update charts dynamically
        /// Checks every 500ms if PrimaryBrush color has changed
        /// </summary>
        private void StartThemeColorMonitoring() {
            try {
                // Initialize last color
                lastPrimaryColor = GetThemeColor("PrimaryBrush", System.Drawing.Color.White);
                Logger.Info($"Initial PrimaryBrush color: R:{lastPrimaryColor.R} G:{lastPrimaryColor.G} B:{lastPrimaryColor.B}");

                // Use Application.Current.Dispatcher to ensure we're on UI thread
                Application.Current.Dispatcher.BeginInvoke(new Action(() => {
                    try {
                        themeColorTimer = new System.Windows.Threading.DispatcherTimer();
                        themeColorTimer.Interval = TimeSpan.FromMilliseconds(500);
                        themeColorTimer.Tick += OnThemeColorTimerTick;
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

        /// <summary>
        /// Timer tick handler for theme color monitoring
        /// </summary>
        private void OnThemeColorTimerTick(object sender, EventArgs e) {
            try {
                var currentPrimaryColor = GetThemeColor("PrimaryBrush", System.Drawing.Color.White);

                // Check if color changed
                if (currentPrimaryColor.ToArgb() != lastPrimaryColor.ToArgb()) {
                    Logger.Info($"Theme color CHANGED! Old: R:{lastPrimaryColor.R} G:{lastPrimaryColor.G} B:{lastPrimaryColor.B} -> New: R:{currentPrimaryColor.R} G:{currentPrimaryColor.G} B:{currentPrimaryColor.B}");
                    lastPrimaryColor = currentPrimaryColor;

                    // Update both charts immediately
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => {
                        try {
                            if (PixelShiftPlot != null) {
                                Logger.Debug("Updating PixelShiftPlot colors...");
                                UpdateChartColors(PixelShiftPlot);
                                PixelShiftPlot.Render();
                            }
                            if (SettleTimePlot != null) {
                                Logger.Debug("Updating SettleTimePlot colors...");
                                UpdateChartColors(SettleTimePlot);
                                SettleTimePlot.Render();
                            }
                        } catch (Exception ex) {
                            Logger.Error($"Error updating charts after color change: {ex.Message}");
                        }
                    }));
                }
            } catch (Exception ex) {
                Logger.Error($"Error in theme color monitoring tick: {ex.Message}");
            }
        }

        /// <summary>
        /// Update chart axis, grid, and tick colors to match current theme
        /// Called every time charts are updated to ensure colors stay in sync with theme
        /// </summary>
        private void UpdateChartColors(ScottPlot.WpfPlot plot) {
            try {
                var primaryColor = GetThemeColor("PrimaryBrush", System.Drawing.Color.White);

                // Set axis label colors
                plot.Plot.XAxis.LabelStyle(color: primaryColor);
                plot.Plot.YAxis.LabelStyle(color: primaryColor);

                // Set axis, grid, and tick colors
                plot.Plot.XAxis.Color(primaryColor);
                plot.Plot.YAxis.Color(primaryColor);
                plot.Plot.XAxis.TickLabelStyle(color: primaryColor);
                plot.Plot.YAxis.TickLabelStyle(color: primaryColor);
                plot.Plot.Grid(color: System.Drawing.Color.FromArgb(50, primaryColor.R, primaryColor.G, primaryColor.B));
            } catch (Exception ex) {
                Logger.Warning($"Error updating chart colors: {ex.Message}");
            }
        }

        /// <summary>
        /// Helper method to get NINA theme color from dynamic resource
        /// Converts WPF SolidColorBrush to System.Drawing.Color for ScottPlot
        /// </summary>
        private System.Drawing.Color GetThemeColor(string resourceKey, System.Drawing.Color fallback) {
            try {
                // Try to get from Application resources first
                if (Application.Current?.Resources[resourceKey] is SolidColorBrush brush) {
                    var wpfColor = brush.Color;
                    var color = System.Drawing.Color.FromArgb(wpfColor.A, wpfColor.R, wpfColor.G, wpfColor.B);
                    Logger.Debug($"GetThemeColor('{resourceKey}'): Found Brush - R:{color.R} G:{color.G} B:{color.B} A:{color.A}");
                    return color;
                }

                // Try to get Color directly (some resources might be Color instead of Brush)
                if (Application.Current?.Resources[resourceKey] is System.Windows.Media.Color wpfColor2) {
                    var color = System.Drawing.Color.FromArgb(wpfColor2.A, wpfColor2.R, wpfColor2.G, wpfColor2.B);
                    Logger.Debug($"GetThemeColor('{resourceKey}'): Found Color - R:{color.R} G:{color.G} B:{color.B} A:{color.A}");
                    return color;
                }

                // Try MainWindow resources if available
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

        /// <summary>
        /// Load DataTemplates for UI rendering
        /// Called in ViewModel constructor (not Plugin) to ensure Assembly is fully loaded
        /// </summary>
        private void LoadDataTemplates() {
            try {
                var resourceDict = new ResourceDictionary {
                    Source = new Uri("pack://application:,,,/ThierryTschanz.NINA.Ditherstatistics;component/DitherStatisticsDataTemplates.xaml", UriKind.Absolute)
                };
                Application.Current?.Resources.MergedDictionaries.Add(resourceDict);
                Logger.Info("DitherStatistics DataTemplates loaded successfully");
            } catch (Exception ex) {
                Logger.Error($"Failed to load DataTemplates: {ex.Message}");
            }
        }

        /// <summary>
        /// Creates the plugin icon for the toggle button in NINA's Image area
        /// Icon: A simple chart/statistics symbol
        /// </summary>
        private GeometryGroup CreatePluginIcon() {
            try {
                var geometryGroup = new GeometryGroup();

                // Create a simple chart icon with bars
                // Bar 1 (short)
                var bar1 = new RectangleGeometry(new Rect(2, 12, 3, 8));
                geometryGroup.Children.Add(bar1);

                // Bar 2 (medium)
                var bar2 = new RectangleGeometry(new Rect(7, 8, 3, 12));
                geometryGroup.Children.Add(bar2);

                // Bar 3 (tall)
                var bar3 = new RectangleGeometry(new Rect(12, 4, 3, 16));
                geometryGroup.Children.Add(bar3);

                // Bar 4 (medium-tall)
                var bar4 = new RectangleGeometry(new Rect(17, 6, 3, 14));
                geometryGroup.Children.Add(bar4);

                Logger.Info("Plugin icon geometry created successfully");
                return geometryGroup;
            } catch (Exception ex) {
                Logger.Error($"Failed to create plugin icon: {ex.Message}");
                // Return a simple fallback circle if creation fails
                var fallbackGroup = new GeometryGroup();
                fallbackGroup.Children.Add(new EllipseGeometry(new Point(10, 10), 8, 8));
                return fallbackGroup;
            }
        }

        ~DitherStatisticsVM() {
            Dispose();
        }

        #endregion
    }
}