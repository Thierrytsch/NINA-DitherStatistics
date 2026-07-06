# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Deploy

This is a N.I.N.A. plugin (WPF, .NET 8.0, x64) with an xUnit test project (`DitherStatistics.Tests`, net8.0-windows) covering everything that doesn't need NINA/UI/network.

```powershell
# Build (PostBuild target auto-copies DLLs to NINA plugin folder)
dotnet build DitherStatistics.sln -c Debug
dotnet build DitherStatistics.sln -c Release

# Run tests
dotnet test DitherStatistics.sln
```

- `PostBuild` MSBuild target copies the plugin DLL, PDB, `deps.json`, and `ScottPlot.dll` / `ScottPlot.WPF.dll` to `%LocalAppData%\NINA\Plugins\3.0.0\DitherStatistics\`. Restart NINA after building to pick up changes.
- `CopyScottPlotDlls` (BeforeTargets="Build") locates the ScottPlot 4.1.59 DLLs in the NuGet cache and stages them into `$(TargetDir)`. If the build errors with "ScottPlot.dll 4.1.59 not found", restore packages.
- NINA assemblies (`NINA.Core`, `NINA.Equipment`, `NINA.Plugin`, `NINA.WPF.Base`) are referenced with `ExcludeAssets=runtime` / `PrivateAssets=all` — never copy them to the output; NINA provides them at runtime.

## Version Management

The plugin version is defined **once** in `DitherStatistics.csproj` (`<Version>`, `<AssemblyVersion>`, `<FileVersion>`). Update all three when bumping. Also update `CHANGELOG.md` and the "Version X Changes" section inside the `LongDescription` `AssemblyMetadata` in the csproj (this is what NINA's plugin manager displays).

The plugin GUID lives in `Properties/AssemblyInfo.cs` — **never change it**, NINA uses it as the persistent plugin identity.

## Architecture

The plugin went through an 8-stage refactor on branch `refactor/v2` (see `REFACTORING_PLAN.md` for the full history and the invariants that were preserved throughout). Current layout:

```
DitherStatistics/
├── DitherStatisticsPlugin.cs      (plugin manifest only)
├── Models/                        (DitherEvent, PixelShiftPoint, PersistedStatisticsData,
│                                    DitherDataPoint, DitherSeriesInfo, DitherAnalysisSnapshot,
│                                    DitherSettingsRecommendation)
├── Phd2/                          (PHD2Client - pure protocol client; Phd2EventArgs;
│                                    Phd2ConnectionManager - auto-connect/reconnect)
├── Services/                      (Statistics, DitherQualityMetrics, DitherAnalysis,
│                                    DitherOptimizerService, PluginSettingsStore,
│                                    StatisticsProfileService, PixelScaleService,
│                                    ExportService, NinaThemeWatcher, ChartRenderers,
│                                    ChartTooltipHelper)
├── ViewModels/DitherStatisticsVM.cs (coordinator: bindings, commands, wiring)
├── Views/DitherStatisticsView.xaml(.cs)
└── DitherStatistics.Tests/        (xUnit)
```

Convention carried through the refactor: extracted pure-math/IO services (`Statistics`, `DitherQualityMetrics`, `DitherAnalysis`, `PixelScaleService`, `PluginSettingsStore`, `StatisticsProfileService`) stay **Logger-free** so they're usable from tests without the NINA runtime; the VM keeps the surrounding try/catch and `Logger.*` calls at call sites, preserving the original log messages.

### Plugin entry point and MEF composition
NINA discovers the plugin through MEF (`System.ComponentModel.Composition`). Three `[Export]` points:
- `DitherStatisticsPlugin : PluginBase` exported as `IPluginManifest` — minimal, just the manifest. Heavy initialization happens in the VM, not here, because the assembly is not yet fully loaded during plugin construction.
- `DitherStatisticsVM : BaseINPC, IDockableVM, IGuiderConsumer` exported as `IDockableVM` — registers the dockable panel.
- `Options : ResourceDictionary` exported as `ResourceDictionary` — under namespace `ThierryTschanz.NINA.Ditherstatistics` (this matches `AssemblyName`, required for NINA to find it).

Note the namespace split: most code is in `DitherStatistics.Plugin` (matches `RootNamespace`), but `Options.xaml`/`.cs` use `ThierryTschanz.NINA.Ditherstatistics`. Don't "fix" this.

### View/ViewModel wiring
`DitherStatisticsDataTemplates.xaml` maps `DitherStatisticsVM` → `DitherStatisticsView` via a typed `DataTemplate`. It is loaded **in the VM constructor** (`LoadDataTemplates()`), not in `DitherStatisticsPlugin`, because the assembly isn't fully initialized during plugin load. The csproj embeds it via `<Resource Include="DitherStatisticsDataTemplates.xaml" />`.

The view code-behind is intentionally empty — all logic lives in `DitherStatisticsVM`.

### ScottPlot chart lifecycle
`PixelShiftPlot` and `SettleTimePlot` are **lazy-loaded** through their property getters so the `WpfPlot` instance is created on the UI thread the first time XAML data-binding accesses it. Do not call `new ScottPlot.WpfPlot()` from a background thread or from the VM constructor. The getters delegate styling to `Services/ChartRenderers.cs` (`ChartTheme.ApplyColors`, `PixelShiftChartRenderer`, `SettleTimeChartRenderer`) and tooltip wiring to `Services/ChartTooltipHelper.cs`.

`Services/NinaThemeWatcher.cs` owns the `DispatcherTimer` that polls NINA's theme brush every 500 ms and raises `PrimaryColorChanged` when it changes; the VM's `OnThemeColorChanged` handler re-applies colors to both plots on the UI thread.

### PHD2 integration
`Phd2/PHD2Client.cs` is a self-contained TCP/JSON-RPC client that connects to PHD2 (default `127.0.0.1:4400`) — it does **not** go through NINA's guider abstraction for receiving dither events, even though the VM also implements `IGuiderConsumer`. It is a pure protocol client: connection/read-loop/request-tracking and event parsing only. Events: `GuidingDithered`, `SettleDone`, `GuideStep` (raw DX/DY/exposure/timestamp), `StarLost`, `GuidingStarted`, `ConnectionStatusChanged`. `Phd2/Phd2ConnectionManager.cs` owns the auto-connect/retry/reconnect policy (2 s initial delay, 10 s retry, 5 s after connection loss) — started once from the VM constructor.

`Services/DitherOptimizerService.cs` is the state machine that consumes those events (wired up in the VM constructor) and owns the Dither Settings Optimizer: a rolling reference window of stable-guiding distances (15 min / 400 points, `referenceLock`), the per-series collection state machine (`ditherDataLock`; a series starts at `GuidingDithered` and closes at `SettleDone` + 10 guide steps, capped at 120 s, with a stale-timer guard), session RMS tracking (`sessionLock`), the diagnostic-file writer, and snapshot/restore for persistence. `Services/DitherAnalysis.cs` holds the pure math the service calls into (`AnalyzeSeries`, `CalculateRecommendation`, `CalculateThresholds`, 3-frame debounce, quantile thresholds) — no locks, no NINA, fully unit-testable. The three profiles are empirical quantiles P90/P95/P99 (`DitherAnalysis.PROFILE_QUANTILES`); property suffixes `_Quality/_Balanced/_Performance` map to Strict/Standard/Fast and are kept for persisted-JSON compatibility. The recommended "min settle time" is deliberately a small debounce value — PHD2's min settle time is the hold time within tolerance, not the time to reach it.

### Quality metrics
`Services/DitherQualityMetrics.cs` is pure math, no NINA dependencies. Thresholds for CD/GFM/Voronoi/Combined ratings are centralized in `DitherQualityMetrics.QualityThresholds` — change them there, not in the VM or README. The grading scale is intentionally strict; see `README.md` for the calibration rationale before retuning. `Services/PixelScaleService.cs` computes the guider→main-camera pixel scale ratio (manual override > NINA GuiderInfo > PHD2 pixel scale > fallback to 1.0) that feeds into it; the VM owns the one-time fallback logging since the service itself doesn't log.

### Persistent settings, profiles & diagnostic files
`Services/PluginSettingsStore.cs` encapsulates the byte-identical text-file formats for the toggle flags and quality-metric settings under `%LocalAppData%\NINA\DitherStatistics\` (`settings.txt`, `optimizer_settings.txt`, `persistence_settings.txt`, `multiprofile_settings.txt`, `quality_settings.txt`, `profiles_list.txt`) — not through NINA's profile system. `Services/StatisticsProfileService.cs` owns the per-profile statistics files (`profiles\<name>.json`), the in-memory store for inactive profiles, and the legacy v1.4 migration; the VM keeps UI state (`ProfileNames`, `SelectedProfileName`) and orchestrates `SwitchToProfile`. `Services/ExportService.cs` writes the user-triggered CSV/quality-report exports to `%USERPROFILE%\Documents\N.I.N.A\DitherStatistics\`.

`DitherOptimizerService` writes per-session diagnostic files to `%LocalAppData%\NINA\DitherStatistics\` (`<timestamp>_<profile>_dither_analysis.txt`, `<timestamp>_<profile>_settle_analysis.txt`) — useful when debugging the recommendation math.
