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
    /// Dateiname: DitherStatisticsVM.cs
    /// </summary>
    [Export(typeof(IDockableVM))]
    public partial class DitherStatisticsVM : BaseINPC, IDockableVM, IGuiderConsumer {
        private readonly ObservableCollection<DitherEvent> ditherEvents = new ObservableCollection<DitherEvent>();
        private readonly IGuiderMediator guiderMediator;
        private readonly IProfileService profileService;
        private readonly PHD2Client phd2Client;
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

            // Subscribe to NINA guider events (optional, for connection monitoring)
            SubscribeToGuiderEvents();

            // Initialize PHD2 client
            phd2Client = new PHD2Client("127.0.0.1", 4400);
            phd2Client.GuidingDithered += OnPHD2GuidingDithered;
            phd2Client.SettleDone += OnPHD2SettleDone;
            phd2Client.ConnectionStatusChanged += OnPHD2ConnectionStatusChanged;

            // Auto-connect to PHD2 after a short delay
            System.Threading.Tasks.Task.Run(async () => {
                await System.Threading.Tasks.Task.Delay(2000);
                await ConnectToPHD2();
            });

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

        // Total Drift Properties - Spannweite der Verteilung
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

        // Settings file path
        private static readonly string SettingsFilePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA", "DitherStatistics", "settings.txt"
        );

        private void LoadQualityAssessmentSetting() {
            try {
                // Load from simple text file
                if (System.IO.File.Exists(SettingsFilePath)) {
                    var content = System.IO.File.ReadAllText(SettingsFilePath).Trim();
                    if (bool.TryParse(content, out bool value)) {
                        IsQualityAssessmentEnabled = value;
                        Logger.Info($"Quality Assessment setting loaded from file: {IsQualityAssessmentEnabled}");
                        return;
                    }
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
                // Save to simple text file
                var directory = System.IO.Path.GetDirectoryName(SettingsFilePath);
                if (!System.IO.Directory.Exists(directory)) {
                    System.IO.Directory.CreateDirectory(directory);
                }

                System.IO.File.WriteAllText(SettingsFilePath, IsQualityAssessmentEnabled.ToString());
                Logger.Info($"Quality Assessment setting saved to file: {IsQualityAssessmentEnabled}");
            } catch (Exception ex) {
                Logger.Error($"Error saving Quality Assessment setting: {ex.Message}");
            }
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
                RaisePropertyChanged(nameof(VoronoiValue));
                RaisePropertyChanged(nameof(VoronoiRating));
                RaisePropertyChanged(nameof(CombinedScoreValue));
                RaisePropertyChanged(nameof(NNIValue));
                RaisePropertyChanged(nameof(NNIRating));
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

        // Target Coverage für Gap-Fill Metrics (using centralized thresholds)
        public string GFM1xTarget => $"Target: {DitherQualityMetrics.QualityThresholds.GFM_Target_1x:P0}";
        public string GFM2xTarget => $"Target: {DitherQualityMetrics.QualityThresholds.GFM_Target_2x:P0}";
        public string GFM3xTarget => $"Target: {DitherQualityMetrics.QualityThresholds.GFM_Target_3x:P0}";

        public string VoronoiValue => QualityResult != null
            ? $"{QualityResult.VoronoiCV:F3}"
            : "N/A";

        public string VoronoiRating => QualityResult != null
            ? GetVoronoiRatingShort(QualityResult.VoronoiCV)
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

        private void InitializeCommands() {
            ClearDataCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ClearData);
            ExportDitherEventsCsvCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ExportDitherEventsCsv);
            RecalculateMetricsCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(RecalculateQualityMetrics);
            ExportMetricsCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ExportQualityMetrics);
        }

        private void ClearData() {
            settleTimeValues.Clear();
            pixelShiftValues.Clear();
            ditherEvents.Clear();

            cumulativeX = 0.0;
            cumulativeY = 0.0;
            currentDither = null;

            UpdateSettleTimeChart();
            UpdatePixelShiftChart();
            UpdateStatistics();
            UpdateQualityMetrics();

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

        private async System.Threading.Tasks.Task ConnectToPHD2() {
            try {
                Logger.Info("Attempting to connect to PHD2...");
                bool connected = await phd2Client.ConnectAsync();

                if (connected) {
                    Logger.Info("Successfully connected to PHD2!");
                } else {
                    Logger.Warning("Failed to connect to PHD2 - will retry later");

                    // Retry after 10 seconds
                    await System.Threading.Tasks.Task.Delay(10000);
                    await ConnectToPHD2();
                }
            } catch (Exception ex) {
                Logger.Error($"Error connecting to PHD2: {ex.Message}");
            }
        }

        private void OnPHD2ConnectionStatusChanged(object sender, string status) {
            Logger.Info($"PHD2 Connection Status: {status}");

            // If disconnected, try to reconnect after delay
            if (status.Contains("Connection lost") || status.Contains("Disconnected")) {
                System.Threading.Tasks.Task.Run(async () => {
                    await System.Threading.Tasks.Task.Delay(5000);
                    await ConnectToPHD2();
                });
            }
        }

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
                if (currentDither == null) {
                    Logger.Warning("⚠️ SettleDone received but no currentDither exists (race condition?)");
                    return;
                }

                currentDither.EndTime = DateTime.Now;
                currentDither.Success = e.Success;

                if (currentDither.EndTime.HasValue) {
                    currentDither.SettleTime = (currentDither.EndTime.Value - currentDither.StartTime).TotalSeconds;
                }

                // Update cumulative position
                if (currentDither.PixelShiftX.HasValue && currentDither.PixelShiftY.HasValue) {
                    cumulativeX += currentDither.PixelShiftX.Value;
                    cumulativeY += currentDither.PixelShiftY.Value;
                    currentDither.CumulativeX = cumulativeX;
                    currentDither.CumulativeY = cumulativeY;
                }

                // Add to events collection
                ditherEvents.Add(currentDither);

                Logger.Info($"✅ DITHER END - Success={e.Success}, SettleTime={currentDither.SettleTime:F2}s, " +
                    $"TotalFrames={e.TotalFrames}, DroppedFrames={e.DroppedFrames}");

                // Update charts and statistics (must be on UI thread)
                Application.Current?.Dispatcher.Invoke(() => {
                    if (currentDither.Success && currentDither.SettleTime.HasValue) {
                        settleTimeValues.Add(currentDither.SettleTime.Value);
                        UpdateSettleTimeChart();
                    }

                    if (currentDither.PixelShiftX.HasValue && currentDither.PixelShiftY.HasValue) {
                        pixelShiftValues.Add(new PixelShiftPoint(
                            cumulativeX,
                            cumulativeY,
                            currentDither.PixelShiftX.Value,
                            currentDither.PixelShiftY.Value
                        ));
                        UpdatePixelShiftChart();
                    }

                    UpdateStatistics();
                    UpdateQualityMetrics();
                });

                // Clear current dither
                currentDither = null;

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

        public void UpdateDeviceInfo(GuiderInfo deviceInfo) {
            // Optional: Monitor NINA guider connection status
            Logger.Debug($"Guider info updated: {deviceInfo?.Name ?? "None"}");
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
                // Extract cumulative positions (X, Y) from PixelShiftValues
                var positions = pixelShiftValues
                    .Select(p => (p.X, p.Y))
                    .ToList();

                QualityResult = DitherQualityMetrics.CalculateQualityMetrics(positions);
                Logger.Info($"Quality metrics updated: Score={QualityResult.CombinedScore:F4}, Rating={QualityResult.QualityRating}");

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

        private string GetVoronoiRatingShort(double cv) {
            // Uses centralized thresholds from DitherQualityMetrics.QualityThresholds
            if (cv < DitherQualityMetrics.QualityThresholds.VoronoiCV_Excellent) return "Excellent";
            if (cv < DitherQualityMetrics.QualityThresholds.VoronoiCV_Good) return "Good";
            if (cv < DitherQualityMetrics.QualityThresholds.VoronoiCV_Acceptable) return "Acceptable";
            if (cv < DitherQualityMetrics.QualityThresholds.VoronoiCV_Fair) return "Fair";
            return "Poor";
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

            // Calculate Total Drift as Spannweite (Max - Min) über alle Punkte
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
            IsVisible = false;
        }

        public void ToggleSettings(object parameter) {
            // No settings for now
        }

        #endregion

        #region Cleanup

        public void Dispose() {
            try {
                // Stop theme color monitoring timer
                if (themeColorTimer != null) {
                    themeColorTimer.Tick -= OnThemeColorTimerTick;
                    themeColorTimer.Stop();
                    themeColorTimer = null;
                    Logger.Info("Theme color monitoring timer stopped");
                }

                phd2Client?.Dispose();

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

        ~DitherStatisticsVM() {
            Dispose();
        }

        #endregion
    }
}