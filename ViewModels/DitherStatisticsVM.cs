using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.ViewModel;
using ScottPlot;
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
        private readonly SmokeTestBridge smokeTestBridge;
        private DitherEvent currentDither = null;

        // Theme color monitoring
        private readonly NinaThemeWatcher themeWatcher = new NinaThemeWatcher();

        // Cumulative position tracking for absolute chart display
        private double cumulativeX = 0.0;
        private double cumulativeY = 0.0;

        [ImportingConstructor]
        public DitherStatisticsVM(IGuiderMediator guiderMediator, IProfileService profileService) {
            this.guiderMediator = guiderMediator;
            this.profileService = profileService;

            Title = "Dither Statistics";
            ContentId = "DitherStatistics_Panel";

            // Info-box status wrappers for the Quality / Optimizer "disabled" cards.
            // Created before settings load so the enable-toggle setters seed them.
            QualityStatus = new SectionStatusDisplay("Quality Assessment", v => IsQualityAssessmentEnabled = v);
            OptimizerStatus = new SectionStatusDisplay("Dither Settings Optimizer", v => IsDitherOptimizerEnabled = v);

            // Set Icon Geometry for the toggle button in NINA's Image area
            // Must be created on UI thread
            if (Application.Current != null) {
                Application.Current.Dispatcher.Invoke(() => {
                    ImageGeometry = CreatePluginIcon();
                });
            }

            // ✅ Load DataTemplates HERE in the ViewModel (not in the Plugin!)
            LoadDataTemplates();

            // ✅ NO MORE InitializePlots() call here!
            // Plots are now created lazily via Properties when XAML accesses them

            // Initialize commands
            InitializeCommands();

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
                if (status == Phd2ConnectionStatus.Disconnected) optimizerService.HandleDisconnected();
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
            themeWatcher.PrimaryColorChanged += OnThemeColorChanged;
            themeWatcher.Start();

            // Optional localhost diagnostic channel for the stage-3 smoke test;
            // disabled unless smoketest_settings.txt opts in (see SmokeTestBridge).
            var (bridgeEnabled, bridgePort) = settingsStore.ReadSmokeTestSetting();
            smokeTestBridge = new SmokeTestBridge(new SmokeTestBridgeAdapter(this), bridgeEnabled, bridgePort);
            smokeTestBridge.Start();

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

                    // Get NINA theme colors and style axes/grid to match
                    var primaryColor = NinaThemeWatcher.GetThemeColor("PrimaryBrush", System.Drawing.Color.White);
                    ChartTheme.ApplyColors(pixelShiftPlot, primaryColor);

                    // ✅ Attach tooltip event handlers
                    ChartTooltipHelper.AttachPixelShiftTooltip(
                        pixelShiftPlot,
                        () => pixelShiftValues,
                        text => PixelShiftTooltipText = text,
                        visible => PixelShiftTooltipVisible = visible);

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

                    // Get NINA theme colors and style axes/grid to match
                    var primaryColor = NinaThemeWatcher.GetThemeColor("PrimaryBrush", System.Drawing.Color.White);
                    ChartTheme.ApplyColors(settleTimePlot, primaryColor);

                    // ✅ Attach tooltip event handlers
                    ChartTooltipHelper.AttachSettleTimeTooltip(
                        settleTimePlot,
                        () => settleTimeValues,
                        text => SettleTimeTooltipText = text,
                        visible => SettleTimeTooltipVisible = visible);

                    Logger.Info("SettleTimePlot created (lazy loading)");
                }
                return settleTimePlot;
            }
            set {
                settleTimePlot = value;
                RaisePropertyChanged();
            }
        }

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
                SyncQualityStatus();

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
            try {
                double phd2Scale = phd2Client?.GuiderPixelScaleArcsec ?? 0;
                double pixelSize = profileService?.ActiveProfile?.CameraSettings?.PixelSize ?? 0;
                double focalLength = profileService?.ActiveProfile?.TelescopeSettings?.FocalLength ?? 0;

                var result = PixelScaleService.Calculate(pixelScaleRatioOverride, ninaGuiderPixelScale, phd2Scale, pixelSize, focalLength);
                pixelScaleRatioSource = result.Source;

                if (result.ImplausibleWarning != null) {
                    Logger.Warning(result.ImplausibleWarning);
                } else if (result.FallbackReason != null) {
                    if (!hasLoggedRatioFallback) {
                        Logger.Info($"Pixel scale ratio fallback (1.0). Missing: {result.FallbackReason}");
                        hasLoggedRatioFallback = true;
                    }
                } else {
                    hasLoggedRatioFallback = false;
                }
                return result.Ratio;
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
                SyncOptimizerStatus();
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
                RaisePropertyChanged(nameof(OptimizerProfiles));
            }
        }

        // The three optimizer columns as a single collection so the view can render
        // them from one DataTemplate. Regroups the already-formatted strings above;
        // a fresh list is built each read and refreshed via the Recommendation setter.
        public IReadOnlyList<OptimizerProfileDisplay> OptimizerProfiles => new[] {
            new OptimizerProfileDisplay {
                Name = "Strict", Quantile = "P90", Tagline = "slow",
                SettlePixel = SettlePixelQuality, Arcsec = SettleArcsecQuality,
                ExpectedSettle = ExpectedSettleQuality, Timeout = SettleTimeoutQuality,
                IsRecommended = false,
                ToolTip = "Strict: waits until guiding is well inside its normal range. Highest confidence, longest settling — does NOT improve image quality."
            },
            new OptimizerProfileDisplay {
                Name = "Standard", Quantile = "P95", Tagline = "balanced",
                SettlePixel = SettlePixelBalanced, Arcsec = SettleArcsecBalanced,
                ExpectedSettle = ExpectedSettleBalanced, Timeout = SettleTimeoutBalanced,
                IsRecommended = false,
                ToolTip = "Standard: good confidence that guiding is back to normal with moderate settle times."
            },
            new OptimizerProfileDisplay {
                Name = "Fast", Quantile = "P99", Tagline = "recommended",
                SettlePixel = SettlePixelPerformance, Arcsec = SettleArcsecPerformance,
                ExpectedSettle = ExpectedSettlePerformance, Timeout = SettleTimeoutPerformance,
                IsRecommended = true,
                ToolTip = "Fast: tolerance safely above the normal guiding scatter — settles quickly and rarely restarts. Recommended for most setups."
            }
        };

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

        #endregion

        #region Section status info boxes (shared "disabled / insufficient data" cards)

        // Normalize the Quality and Optimizer "disabled / not enough data" info boxes
        // onto one shape so a single DataTemplate renders both. Created before the
        // settings are loaded so the enable-toggle setters can push their initial state.
        public SectionStatusDisplay QualityStatus { get; }
        public SectionStatusDisplay OptimizerStatus { get; }

        private void SyncQualityStatus() {
            QualityStatus?.Sync(
                IsQualityAssessmentEnabled,
                ShowQualityDisabledMessage ? "Quality Assessment disabled" : "Requires at least 4 dither events");
        }

        private void SyncOptimizerStatus() {
            OptimizerStatus?.Sync(
                IsDitherOptimizerEnabled,
                ShowDitherOptimizerDisabledMessage ? "Dither Settings Optimizer disabled" : "Requires at least 3 dither events");
        }

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
                    // Refresh the settle chart so the P90/P95/P99 threshold lines
                    // reflect the new recommendation immediately, not only on the next dither
                    UpdateSettleTimeChart();
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
        /// <remarks>
        /// This is invoked both from UI-thread callers (Clear Data, persistence toggle,
        /// Dispose) and from background threads (the PHD2 read loop via OnPHD2SettleDone,
        /// the optimizer analysis thread via OnDitherRecommendationUpdated). Because
        /// BuildCurrentSnapshot reads the UI-thread-only live collections and
        /// selectedProfileName, the snapshot is built on the UI thread to avoid a torn
        /// read (InvalidOperationException during enumeration) or a snapshot landing in
        /// the wrong profile's file during a concurrent SwitchToProfile. The file write
        /// itself stays on the calling thread.
        /// </remarks>
        private void SaveStatisticsData() {
            if (!isStatisticsPersistenceEnabled) return;

            string profileName = null;
            PersistedStatisticsData snapshot = null;
            int eventCount = 0;
            InvokeOnUiThread(() => {
                profileName = selectedProfileName;
                snapshot = BuildCurrentSnapshot();
                eventCount = ditherEvents.Count;
            });
            if (snapshot == null) return;

            SaveProfileDataToFile(profileName, snapshot);
            Logger.Debug($"Statistics data persisted ({eventCount} dither events, profile '{profileName}')");
        }

        /// <summary>
        /// Run <paramref name="action"/> synchronously on the WPF UI thread. Runs inline
        /// when already on the UI thread (Dispatcher.Invoke detects same-thread access, so
        /// there is no deadlock and no re-entrancy) and also when there is no WPF
        /// Application (unit tests). This is how background threads safely touch the
        /// UI-thread-only live statistics collections (see BuildCurrentSnapshot).
        /// </summary>
        private static void InvokeOnUiThread(Action action) {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) {
                action();
            } else {
                dispatcher.Invoke(action);
            }
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

        /// <summary>
        /// Build a persistence snapshot of the current statistics state.
        /// UI-thread only: it enumerates the live collections (ditherEvents,
        /// settleTimeValues, pixelShiftValues) and reads selectedProfileName /
        /// Recommendation, all of which are mutated on the UI thread (SwitchToProfile,
        /// ClearData, RestoreStatisticsData, OnPHD2SettleDone's dispatcher block).
        /// Background callers must marshal through InvokeOnUiThread (see SaveStatisticsData).
        /// </summary>
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

        // The rating→colour palette lives in XAML now (RatingBadgeStyle DataTriggers
        // keyed on QualityResult.QualityRating), so the semantic colours sit next to
        // the rest of the view's design-system resources instead of in the VM.

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
        public ICommand HideCommand { get; private set; }
        public ICommand ToggleSettingsCommand { get; private set; }

        private void InitializeCommands() {
            ClearDataCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ClearData);
            ExportDitherEventsCsvCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ExportDitherEventsCsv);
            RecalculateMetricsCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(RecalculateQualityMetrics);
            ExportMetricsCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ExportQualityMetrics);
            CreateProfileCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(CreateProfile);
            DeleteProfileCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(DeleteProfile);
            HideCommand = new CommunityToolkit.Mvvm.Input.RelayCommand<object>(Hide);
            ToggleSettingsCommand = new CommunityToolkit.Mvvm.Input.RelayCommand<object>(ToggleSettings);
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
                string path = ExportService.ExportDitherEventsCsv(ditherEvents);
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
                string path = ExportService.ExportQualityReport(QualityResult);
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
                var primaryColor = NinaThemeWatcher.GetThemeColor("PrimaryBrush", System.Drawing.Color.White);
                PixelShiftChartRenderer.Render(PixelShiftPlot, pixelShiftValues, primaryColor);
            } catch (Exception ex) {
                Logger.Error($"Error updating pixel shift chart: {ex.Message}");
            }
        }

        private void UpdateSettleTimeChart() {
            try {
                if (SettleTimePlot == null) return;
                var primaryColor = NinaThemeWatcher.GetThemeColor("PrimaryBrush", System.Drawing.Color.White);
                SettleTimeChartRenderer.Render(
                    SettleTimePlot,
                    settleTimeValues,
                    AverageSettleTime,
                    StdDevSettleTime,
                    Recommendation?.ExpectedSettleDuration_Quality ?? 0,
                    Recommendation?.ExpectedSettleDuration_Balanced ?? 0,
                    Recommendation?.ExpectedSettleDuration_Performance ?? 0,
                    primaryColor);
            } catch (Exception ex) {
                Logger.Error($"Error updating settle time chart: {ex.Message}");
            }
        }

        #endregion

        #region Statistics

        private void UpdateStatistics() {
            var summary = DitherStatistics.Aggregate(ditherEvents, pixelShiftValues);

            TotalDithers = summary.TotalDithers;
            SuccessfulDithers = summary.SuccessfulDithers;
            SuccessRate = summary.SuccessRate;
            AverageSettleTime = summary.AverageSettleTime;
            MedianSettleTime = summary.MedianSettleTime;
            MinSettleTime = summary.MinSettleTime;
            MaxSettleTime = summary.MaxSettleTime;
            StdDevSettleTime = summary.StdDevSettleTime;
            TotalDriftX = summary.TotalDriftX;
            TotalDriftY = summary.TotalDriftY;

            if (summary.SuccessfulDithers > 0) {
                // Update settle time chart with new average/stddev lines
                UpdateSettleTimeChart();
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
                // Stop the diagnostic channel first so no in-flight command marshals
                // onto the UI thread during teardown
                smokeTestBridge?.Dispose();

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
                themeWatcher.PrimaryColorChanged -= OnThemeColorChanged;
                themeWatcher.Dispose();

                // Dispose order matters: the connection manager must stop first so
                // the client's final Disconnected status cannot schedule a reconnect
                // after disposal; the client's Disconnect then fires Disconnected,
                // which lets the optimizer abort its running collection window (the
                // snapshot above already captured the in-progress points)
                phd2ConnectionManager?.Dispose();
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
        /// Raised by the theme watcher when PrimaryBrush changes; refresh both
        /// charts' colors immediately (same dispatcher hop as before the split).
        /// </summary>
        private void OnThemeColorChanged(object sender, System.Drawing.Color newColor) {
            Application.Current.Dispatcher.BeginInvoke(new Action(() => {
                try {
                    if (PixelShiftPlot != null) {
                        Logger.Debug("Updating PixelShiftPlot colors...");
                        ChartTheme.ApplyColors(PixelShiftPlot, newColor);
                        PixelShiftPlot.Render();
                    }
                    if (SettleTimePlot != null) {
                        Logger.Debug("Updating SettleTimePlot colors...");
                        ChartTheme.ApplyColors(SettleTimePlot, newColor);
                        SettleTimePlot.Render();
                    }
                } catch (Exception ex) {
                    Logger.Error($"Error updating charts after color change: {ex.Message}");
                }
            }));
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

        #endregion

        #region SmokeTest bridge adapter

        /// <summary>
        /// Bridges the localhost diagnostic channel (SmokeTestBridge) to this VM. The
        /// bridge calls every method on the WPF UI thread, so these run exactly the
        /// same code paths as the panel buttons/toggles. Nested so it can read the
        /// VM's private live collections; it exposes only panel-equivalent operations.
        /// </summary>
        private sealed class SmokeTestBridgeAdapter : ISmokeTestBridgeAdapter {
            private readonly DitherStatisticsVM vm;

            public SmokeTestBridgeAdapter(DitherStatisticsVM vm) {
                this.vm = vm;
            }

            public IDictionary<string, object> GetState() {
                return new Dictionary<string, object> {
                    ["TotalDithers"] = vm.TotalDithers,
                    ["SuccessfulDithers"] = vm.SuccessfulDithers,
                    ["SuccessRate"] = vm.SuccessRate,
                    ["MedianSettleTime"] = vm.MedianSettleTime,
                    ["MinSettleTime"] = vm.MinSettleTime,
                    ["MaxSettleTime"] = vm.MaxSettleTime,
                    ["AverageSettleTime"] = vm.AverageSettleTime,
                    ["StdDevSettleTime"] = vm.StdDevSettleTime,
                    ["TotalDriftX"] = vm.TotalDriftX,
                    ["TotalDriftY"] = vm.TotalDriftY,
                    ["PixelShiftPointCount"] = vm.pixelShiftValues.Count,
                    ["SettleTimePointCount"] = vm.settleTimeValues.Count,
                    ["HasQualityData"] = vm.HasQualityData,
                    ["HasRecommendationData"] = vm.HasRecommendationData,
                    ["Quality"] = BuildQuality(),
                    ["Optimizer"] = BuildOptimizer(),
                    ["Toggles"] = new Dictionary<string, object> {
                        ["Persistence"] = vm.IsStatisticsPersistenceEnabled,
                        ["MultiProfile"] = vm.IsMultiProfileEnabled,
                        ["Quality"] = vm.IsQualityAssessmentEnabled,
                        ["Optimizer"] = vm.IsDitherOptimizerEnabled
                    },
                    ["ProfileNames"] = vm.ProfileNames.ToList(),
                    ["SelectedProfileName"] = vm.SelectedProfileName
                };
            }

            private object BuildQuality() {
                var q = vm.QualityResult;
                if (q == null) return null;
                return new Dictionary<string, object> {
                    ["CombinedScore"] = q.CombinedScore,
                    ["QualityRating"] = q.QualityRating,
                    ["CenteredL2Discrepancy"] = q.CenteredL2Discrepancy,
                    ["DriftRatio"] = q.DriftRatio,
                    ["NearestNeighborIndex"] = q.NearestNeighborIndex,
                    ["GapFillMetric_1x"] = q.GapFillMetric_1x,
                    ["GapFillMetric_2x"] = q.GapFillMetric_2x,
                    ["GapFillMetric_3x"] = q.GapFillMetric_3x,
                    ["PixelScaleRatio"] = q.PixelScaleRatio,
                    ["Pixfrac"] = q.Pixfrac,
                    ["EffectiveScaleRatioText"] = vm.EffectiveScaleRatioText
                };
            }

            private object BuildOptimizer() {
                var r = vm.Recommendation;
                if (r == null) return null;
                double scale = r.GuiderPixelScaleArcsec;
                object Arcsec(double px) => scale > 0 ? (object)(px * scale) : null;
                return new Dictionary<string, object> {
                    ["SettlePixel_Strict"] = r.SettlePixelTolerance_Quality,
                    ["SettlePixel_Standard"] = r.SettlePixelTolerance_Balanced,
                    ["SettlePixel_Fast"] = r.SettlePixelTolerance_Performance,
                    ["SettleArcsec_Strict"] = Arcsec(r.SettlePixelTolerance_Quality),
                    ["SettleArcsec_Standard"] = Arcsec(r.SettlePixelTolerance_Balanced),
                    ["SettleArcsec_Fast"] = Arcsec(r.SettlePixelTolerance_Performance),
                    ["ExpectedSettle_Strict"] = r.ExpectedSettleDuration_Quality,
                    ["ExpectedSettle_Standard"] = r.ExpectedSettleDuration_Balanced,
                    ["ExpectedSettle_Fast"] = r.ExpectedSettleDuration_Performance,
                    ["Timeout_Strict"] = r.SettleTimeout_Quality,
                    ["Timeout_Standard"] = r.SettleTimeout_Balanced,
                    ["Timeout_Fast"] = r.SettleTimeout_Performance,
                    ["DitherEventsAnalyzed"] = r.DitherEventsAnalyzed,
                    ["GuiderPixelScaleArcsec"] = scale,
                    ["GuideExposure"] = r.GuideExposure,
                    ["RecommendationInfo"] = vm.RecommendationInfo,
                    ["RecommendationWarning"] = vm.RecommendationWarning
                };
            }

            public void Invoke(string name) {
                ICommand command = name switch {
                    "ClearData" => vm.ClearDataCommand,
                    "ExportCsv" => vm.ExportDitherEventsCsvCommand,
                    "ExportReport" => vm.ExportMetricsCommand,
                    "Recalc" => vm.RecalculateMetricsCommand,
                    _ => throw new ArgumentException($"unknown invoke target '{name}'")
                };
                command.Execute(null);
            }

            public bool SetToggle(string name, bool value) {
                switch (name) {
                    case "Persistence": {
                        bool prior = vm.IsStatisticsPersistenceEnabled;
                        vm.IsStatisticsPersistenceEnabled = value;
                        return prior;
                    }
                    case "MultiProfile": {
                        bool prior = vm.IsMultiProfileEnabled;
                        vm.IsMultiProfileEnabled = value;
                        return prior;
                    }
                    case "Quality": {
                        bool prior = vm.IsQualityAssessmentEnabled;
                        vm.IsQualityAssessmentEnabled = value;
                        return prior;
                    }
                    case "Optimizer": {
                        bool prior = vm.IsDitherOptimizerEnabled;
                        vm.IsDitherOptimizerEnabled = value;
                        return prior;
                    }
                    default:
                        throw new ArgumentException($"unknown toggle '{name}'");
                }
            }

            public void CreateProfile(string name) {
                vm.ProfileNameInput = name;
                vm.CreateProfileCommand.Execute(null);
            }

            public void SelectProfile(string name) {
                vm.SelectedProfileName = name;
            }

            public void DeleteProfile(string name) {
                // DeleteProfile() deletes the currently selected profile, so select it first
                if (!string.Equals(vm.SelectedProfileName, name, StringComparison.OrdinalIgnoreCase)) {
                    vm.SelectedProfileName = name;
                }
                vm.DeleteProfileCommand.Execute(null);
            }
        }

        #endregion
    }
}