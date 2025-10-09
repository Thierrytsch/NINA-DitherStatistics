using LiveCharts;
using LiveCharts.Wpf;
using LiveCharts.Configurations;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Equipment.MyGuider;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.ViewModel;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// ViewModel for Dither Statistics Panel with PHD2 direct integration
    /// Dateiname: DitherStatisticsVM.cs
    /// </summary>
    [Export(typeof(IDockableVM))]
    public class DitherStatisticsVM : BaseINPC, IDockableVM, IGuiderConsumer {
        private readonly ObservableCollection<DitherEvent> ditherEvents = new ObservableCollection<DitherEvent>();
        private readonly IGuiderMediator guiderMediator;
        private readonly PHD2Client phd2Client;
        private DitherEvent currentDither = null;
        private readonly Random random = new Random();

        [ImportingConstructor]
        public DitherStatisticsVM(IGuiderMediator guiderMediator) {
            this.guiderMediator = guiderMediator;

            Title = "Dither Statistics";
            ContentId = "DitherStatistics_Panel";

            // Configure LiveCharts for PixelShiftPoint
            ConfigureLiveCharts();

            // Initialize commands
            InitializeCommands();

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

            Logger.Info("DitherStatisticsVM initialized successfully!");
        }

        #region Properties - Chart Data

        private ChartValues<double> settleTimeValues = new ChartValues<double>();
        public ChartValues<double> SettleTimeValues {
            get => settleTimeValues;
            set {
                settleTimeValues = value;
                RaisePropertyChanged();
            }
        }

        private ChartValues<PixelShiftPoint> pixelShiftValues = new ChartValues<PixelShiftPoint>();
        public ChartValues<PixelShiftPoint> PixelShiftValues {
            get => pixelShiftValues;
            set {
                pixelShiftValues = value;
                RaisePropertyChanged();
            }
        }

        public SeriesCollection SettleTimeSeriesCollection {
            get {
                if (_settleTimeSeriesCollection == null) {
                    InitializeCharts();
                }
                return _settleTimeSeriesCollection;
            }
            set => _settleTimeSeriesCollection = value;
        }
        private SeriesCollection _settleTimeSeriesCollection;

        public SeriesCollection PixelShiftSeriesCollection {
            get {
                if (_pixelShiftSeriesCollection == null) {
                    InitializeCharts();
                }
                return _pixelShiftSeriesCollection;
            }
            set => _pixelShiftSeriesCollection = value;
        }
        private SeriesCollection _pixelShiftSeriesCollection;

        public Func<double, string> XFormatter { get; set; } = value => value.ToString("F1");
        public Func<double, string> YFormatter { get; set; } = value => value.ToString("F1");

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

        #endregion

        #region Commands

        public ICommand ClearDataCommand { get; private set; }
        public ICommand ExportDataCommand { get; private set; }

        private void InitializeCommands() {
            ClearDataCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ClearData);
            ExportDataCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ExportData);
        }

        private void ClearData() {
            ditherEvents.Clear();
            SettleTimeValues.Clear();
            PixelShiftValues.Clear();
            currentDither = null;
            UpdatePixelShiftColors();
            UpdateStatistics();
            Logger.Info("Dither statistics cleared");
        }

        private void ExportData() {
            Logger.Info("Export functionality - to be implemented");
            // TODO: Implement CSV export in future version
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
                Logger.Info($"✅ DITHER END detected! Success={e.Success}, TotalFrames={e.TotalFrames}, DroppedFrames={e.DroppedFrames}");

                if (currentDither != null) {
                    // Complete the dither event
                    currentDither.EndTime = DateTime.Now;
                    currentDither.Success = e.Success;
                    currentDither.SettleTime = (currentDither.EndTime.Value - currentDither.StartTime).TotalSeconds;

                    // Add to collection
                    ditherEvents.Add(currentDither);

                    // Update UI on dispatcher thread
                    System.Windows.Application.Current?.Dispatcher.Invoke(() => {
                        SettleTimeValues.Add(currentDither.SettleTime.Value);
                        PixelShiftValues.Add(new PixelShiftPoint(
                            currentDither.PixelShiftX.Value,
                            currentDither.PixelShiftY.Value
                        ));
                        UpdatePixelShiftColors();
                        UpdateStatistics();
                    });

                    Logger.Info($"📊 Dither recorded: {currentDither.SettleTime:F2}s settle, shift: ({currentDither.PixelShiftX:F2}, {currentDither.PixelShiftY:F2}) px");

                    currentDither = null;
                } else {
                    Logger.Warning("SettleDone received but no currentDither exists - plugin may have been started after dithering began");
                }

            } catch (Exception ex) {
                Logger.Error($"Error handling SettleDone: {ex.Message}");
            }
        }

        #endregion

        #region NINA Guider Events (Optional)

        private void SubscribeToGuiderEvents() {
            try {
                if (guiderMediator != null) {
                    guiderMediator.RegisterConsumer(this);
                    Logger.Info("Registered as NINA guider consumer");
                }
            } catch (Exception ex) {
                Logger.Error($"Failed to subscribe to NINA guider events: {ex.Message}");
            }
        }

        public void UpdateDeviceInfo(GuiderInfo guiderInfo) {
            // Optional: Monitor guider connection status
            // We're primarily using PHD2 direct connection now
        }

        #endregion

        #region Chart Methods

        private void ConfigureLiveCharts() {
            var mapper = Mappers.Xy<PixelShiftPoint>()
                .X(point => point.X)
                .Y(point => point.Y);

            Charting.For<PixelShiftPoint>(mapper);
        }

        private void InitializeCharts() {
            if (_settleTimeSeriesCollection != null && _pixelShiftSeriesCollection != null)
                return;

            // Settle Time Chart - OPTIMIZED TOOLTIP (nur Sekundenwert)
            _settleTimeSeriesCollection = new SeriesCollection
            {
                new LineSeries
                {
                    Title = "Settle Time",
                    Values = SettleTimeValues,
                    PointGeometry = DefaultGeometries.Circle,
                    PointGeometrySize = 8,
                    Fill = System.Windows.Media.Brushes.Transparent,
                    Stroke = System.Windows.Media.Brushes.DodgerBlue,
                    StrokeThickness = 2,
                    LineSmoothness = 0,
                    LabelPoint = point => $"{point.Y:F2}s"  // NUR Sekundenwert
                },
                new LineSeries
                {
                    Title = "Average",
                    Values = new ChartValues<double>(),
                    PointGeometry = null,
                    Fill = System.Windows.Media.Brushes.Transparent,
                    Stroke = System.Windows.Media.Brushes.Red,
                    StrokeThickness = 2,
                    StrokeDashArray = new System.Windows.Media.DoubleCollection { 5, 5 },
                    LineSmoothness = 0
                },
                new LineSeries
                {
                    Title = "Avg - StdDev",
                    Values = new ChartValues<double>(),
                    PointGeometry = null,
                    Fill = System.Windows.Media.Brushes.Transparent,
                    Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 255, 0, 0)),
                    StrokeThickness = 2,
                    StrokeDashArray = new System.Windows.Media.DoubleCollection { 2, 2 },
                    LineSmoothness = 0
                },
                new LineSeries
                {
                    Title = "Avg + StdDev",
                    Values = new ChartValues<double>(),
                    PointGeometry = null,
                    Fill = System.Windows.Media.Brushes.Transparent,
                    Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 255, 0, 0)),
                    StrokeThickness = 2,
                    StrokeDashArray = new System.Windows.Media.DoubleCollection { 2, 2 },
                    LineSmoothness = 0
                }
            };

            // Pixel Shift Chart
            _pixelShiftSeriesCollection = new SeriesCollection
            {
                new LineSeries
                {
                    Title = "Connections",
                    Values = PixelShiftValues,
                    PointGeometry = null,
                    Fill = System.Windows.Media.Brushes.Transparent,
                    Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 100, 149, 237)),
                    StrokeThickness = 1,
                    LineSmoothness = 0
                }
            };
        }

        private void UpdatePixelShiftColors() {
            if (_pixelShiftSeriesCollection == null)
                return;

            int count = PixelShiftValues.Count;
            if (count == 0)
                return;

            // Remove old point series (keep connection line at index 0)
            while (_pixelShiftSeriesCollection.Count > 1) {
                _pixelShiftSeriesCollection.RemoveAt(1);
            }

            // Add scatter series for each point with gradient color
            // OPTIMIZED TOOLTIP (nur X/Y Koordinaten)
            for (int i = 0; i < count; i++) {
                double ratio = count > 1 ? (double)i / (count - 1) : 1.0;
                byte red = (byte)(60 + (200 - 60) * ratio);

                var pointColor = System.Windows.Media.Color.FromRgb(red, 0, 0);

                var scatterSeries = new ScatterSeries {
                    Values = new ChartValues<PixelShiftPoint> { PixelShiftValues[i] },
                    PointGeometry = DefaultGeometries.Circle,
                    MinPointShapeDiameter = 6,
                    MaxPointShapeDiameter = 6,
                    Fill = new System.Windows.Media.SolidColorBrush(pointColor),
                    Stroke = System.Windows.Media.Brushes.Transparent,
                    LabelPoint = point => $"X: {point.X:F2}  Y: {point.Y:F2}"  // NUR X/Y Koordinaten
                };

                _pixelShiftSeriesCollection.Add(scatterSeries);
            }

            RaisePropertyChanged(nameof(PixelShiftSeriesCollection));
        }

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

                // Update chart lines
                if (_settleTimeSeriesCollection != null && _settleTimeSeriesCollection.Count > 3) {
                    var avgLine = _settleTimeSeriesCollection[1].Values as ChartValues<double>;
                    avgLine?.Clear();
                    for (int i = 0; i < SettleTimeValues.Count; i++) {
                        avgLine?.Add(AverageSettleTime);
                    }

                    var lowerLine = _settleTimeSeriesCollection[2].Values as ChartValues<double>;
                    lowerLine?.Clear();
                    for (int i = 0; i < SettleTimeValues.Count; i++) {
                        lowerLine?.Add(Math.Max(0, AverageSettleTime - StdDevSettleTime));
                    }

                    var upperLine = _settleTimeSeriesCollection[3].Values as ChartValues<double>;
                    upperLine?.Clear();
                    for (int i = 0; i < SettleTimeValues.Count; i++) {
                        upperLine?.Add(AverageSettleTime + StdDevSettleTime);
                    }
                }
            } else {
                AverageSettleTime = 0;
                MedianSettleTime = 0;
                MinSettleTime = 0;
                MaxSettleTime = 0;
                StdDevSettleTime = 0;
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
                phd2Client?.Dispose();

                if (guiderMediator != null) {
                    guiderMediator.RemoveConsumer(this);
                }

                Logger.Info("DitherStatisticsVM disposed");
            } catch (Exception ex) {
                Logger.Error($"Error disposing: {ex.Message}");
            }
        }

        ~DitherStatisticsVM() {
            Dispose();
        }

        #endregion
    }
}
