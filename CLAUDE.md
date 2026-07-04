# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Deploy

This is a N.I.N.A. plugin (WPF, .NET 8.0, x64). There are no unit tests.

```powershell
# Build (PostBuild target auto-copies DLLs to NINA plugin folder)
dotnet build DitherStatistics.sln -c Debug
dotnet build DitherStatistics.sln -c Release
```

- `PostBuild` MSBuild target copies the plugin DLL, PDB, `deps.json`, and `ScottPlot.dll` / `ScottPlot.WPF.dll` to `%LocalAppData%\NINA\Plugins\3.0.0\DitherStatistics\`. Restart NINA after building to pick up changes.
- `CopyScottPlotDlls` (BeforeTargets="Build") locates the ScottPlot 4.1.59 DLLs in the NuGet cache and stages them into `$(TargetDir)`. If the build errors with "ScottPlot.dll 4.1.59 not found", restore packages.
- NINA assemblies (`NINA.Core`, `NINA.Equipment`, `NINA.Plugin`, `NINA.WPF.Base`) are referenced with `ExcludeAssets=runtime` / `PrivateAssets=all` — never copy them to the output; NINA provides them at runtime.

## Version Management

The plugin version is defined **once** in `DitherStatistics.csproj` (`<Version>`, `<AssemblyVersion>`, `<FileVersion>`). Update all three when bumping. Also update `CHANGELOG.md` and the "Version X Changes" section inside the `LongDescription` `AssemblyMetadata` in the csproj (this is what NINA's plugin manager displays).

The plugin GUID lives in `Properties/AssemblyInfo.cs` — **never change it**, NINA uses it as the persistent plugin identity.

## Architecture

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
`PixelShiftPlot` and `SettleTimePlot` are **lazy-loaded** through their property getters so the `WpfPlot` instance is created on the UI thread the first time XAML data-binding accesses it. Do not call `new ScottPlot.WpfPlot()` from a background thread or from the VM constructor. A `DispatcherTimer` (`themeColorTimer`) re-applies NINA theme colors to the plots when the theme changes.

### PHD2 integration
`PHD2Client` is a self-contained TCP/JSON-RPC client that connects to PHD2 (default `127.0.0.1:4400`) — it does **not** go through NINA's guider abstraction for receiving dither events, even though the VM also implements `IGuiderConsumer`. PHD2 events surface as C# events (`GuidingDithered`, `SettleDone`, `GuideStep`, `ConnectionStatusChanged`, `DitherRecommendationUpdated`) which the VM subscribes to in its constructor.

`PHD2Client` is also where the Dither Settings Optimizer math lives: it maintains a rolling reference window of stable-guiding distances (`referenceWindow`, 15 min / 400 points), collects post-dither `GuideStep` data per series (`allDitherData` + per-series `seriesInfos`, guarded by `ditherDataLock`; window ends at SettleDone + 10 guide steps, capped at 120 s), computes time-to-stable per profile with 3-frame debouncing, and emits `DitherSettingsRecommendation` via `DitherRecommendationUpdated`. The three profiles are empirical quantiles P90/P95/P99 (`PROFILE_QUANTILES`); property suffixes `_Quality/_Balanced/_Performance` map to Strict/Standard/Fast and are kept for persisted-JSON compatibility. The recommended "min settle time" is deliberately a small debounce value — PHD2's min settle time is the hold time within tolerance, not the time to reach it.

### Quality metrics
`DitherQualityMetrics.cs` is pure math, no NINA dependencies. Thresholds for CD/GFM/Voronoi/Combined ratings are centralized in `DitherQualityMetrics.QualityThresholds` — change them there, not in the VM or README. The grading scale is intentionally strict; see `README.md` for the calibration rationale before retuning.

### Persistent settings & diagnostic files
Two toggle flags (`IsQualityAssessmentEnabled`, `IsDitherOptimizerEnabled`) are persisted as plain text files under `%LocalAppData%\NINA\DitherStatistics\` (`settings.txt`, `optimizer_settings.txt`) — not through NINA's profile system. Quality reports exported by the user land in `%USERPROFILE%\Documents\NINA\DitherStatistics\`.

The optimizer also writes per-session diagnostic files to `%LocalAppData%\NINA\DitherStatistics\` (`<timestamp>_<profile>_dither_analysis.txt`, `<timestamp>_<profile>_settle_analysis.txt`) — useful when debugging the recommendation math in `PHD2Client`.
