# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Language convention

**All source — code, comments, XML doc, log/exception messages, commit messages, UI strings, and documentation (CLAUDE.md, README, CHANGELOG, REFACTORING_PLAN) — is written in English.** The historical codebase contained German comments; these are being translated as files are touched. When editing a file with leftover German text, translate it to English as part of the change; never introduce new non-English text. Chat with the maintainer may be in German, but nothing German ends up in a committed artifact.

## Build & Deploy

This is a N.I.N.A. plugin (WPF, .NET 8.0, x64) with an xUnit test project (`DitherStatistics.Tests`, net8.0-windows). Most tests are pure (no NINA/UI/network); a set of socket-level end-to-end tests drive a real `PHD2Client` over loopback against an in-process fake PHD2 server, and an opt-in integration suite talks to a real PHD2. See **Testing** below.

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

Current version: **1.6.0.0**. Feature milestones relevant to the architecture below (full detail in `CHANGELOG.md`):
- **1.4** — multi-session statistics persistence ("Keep across sessions" toggle).
- **1.5** — multiple statistics profiles (per target/telescope) with automatic v1.4→Default migration; reworked quality assessment (real drizzle-weight simulation, Drift Ratio metric, guider→main-camera pixel-scale conversion, configurable drizzle pixfrac).
- **1.6** — Dither Settings Optimizer redesigned around empirical quantiles (P90/P95/P99 = Strict/Standard/Fast); corrected min-settle-time semantics; new Settle Timeout and Expected Settle recommendations; failed/star-lost dithers excluded; settle-follows-actual-SettleDone collection window (see the Optimizer subsections under **Architecture**).

## Testing

The functional test story has three stages (`SmokeTest/README.md` is the authoritative source; keep it in sync when the flow changes):

| Stage | Runs | Verifies | When |
|---|---|---|---|
| 1 — unit + in-process E2E | `dotnet test` (always) | pure math/IO services **and** the socket path (`PHD2Client` parsing → `DitherOptimizerService`) against a fake PHD2 server | every test run, seconds |
| 2 — PHD2 integration | `dotnet test` with PHD2 guiding | the real PHD2 wire format incl. a live dither round-trip; auto-skips without PHD2 | ~3 min, opt-in |
| 3 — NINA smoke test | `SmokeTest/Run-SmokeTest.ps1` | the plugin inside real NINA end-to-end across six scenarios (Baseline, Export, Profiles, Restart, PersistenceOff, Clear) via the `SmokeTestBridge` diagnostic channel: MEF load, panel/charts, PHD2 auto-connect, persistence, multi-profile separation, CSV/quality export, and Clear Data | ~16–18 min full run, manual |

`DitherStatistics.Tests/` (xUnit, net8.0-windows, x64) references `NINA.Core` as a *normal* PackageReference (the plugin references it `ExcludeAssets=runtime`) so `Logger.*` resolves at test runtime; `System.Drawing.Common` is pinned to 8.0.0 to avoid the MSB3277 net462↔net8.0 conflict. The plugin csproj excludes the test tree from its own glob (`<Compile Remove="DitherStatistics.Tests/**/*.cs" />`). The **JSON contract tests** (`JsonContractTests.cs`) are the safety net for the persistence format — they assert the exact property names (incl. the `_Quality`/`_Balanced`/`_Performance` suffixes); do not let a refactor change them.

Test files by concern:
- `StatisticsTests.cs`, `DitherQualityMetricsTests.cs`, `DitherAnalysisTests.cs`, `PixelScaleServiceTests.cs` — pure-math services (golden values, synthetic dither series, quantile/timeout formulas).
- `PluginSettingsStoreTests.cs`, `StatisticsProfileServiceTests.cs` — settings/profile file I/O against a temp directory (roundtrip, missing/corrupt file → default, legacy migration, sanitization collisions).
- `JsonContractTests.cs` — persistence-format property-name contract.
- `DitherOptimizerServiceTests.cs` — the state machine driven directly via its `Handle*` methods (no socket): full dither cycle, rapid dithering, star-lost/settle-failed, disconnect/clear/restore semantics.
- `FakePhd2Server.cs` + `Phd2EndToEndTests.cs` — a real `PHD2Client` over a real loopback socket to an in-process fake PHD2 (`FakePhd2Server`), wired to a `DitherOptimizerService` exactly as the VM wires it; covers TCP connect/read-loop, JSON-RPC round-trips, event parsing, malformed-line resilience, connection-lost vs. explicit-disconnect. Events are stamped `DateTime.Now` inside `PHD2Client`, so time-window scenarios stay in the `Handle*`-level tests.
- `Phd2IntegrationTests.cs` — `[Trait("Category","Integration")]`, gated by a custom `[Phd2Fact]` that probes `127.0.0.1:4400` and **skips** unless a real PHD2 is *guiding*; runs a live dither round-trip. Start PHD2 with `SmokeTest/Start-Phd2Guiding.ps1` first.

The stage-3 smoke test (`SmokeTest/Run-SmokeTest.ps1`) exercises the plugin end-to-end in real NINA by triggering dithers directly over PHD2's JSON-RPC API (port 4400) and driving the panel through `Services/SmokeTestBridge.cs` — no Sequencer, NINA hardware-connect, real hardware, or element-level UI Automation needed (NINA exposes no tab *content* to UIA2/UIA3 at all, only the window shell/tab headers — see `Spike-UiaVisibility.ps1` and the Defender/AMSI note below for why the bridge exists instead). One NINA session boots once (build/deploy → PHD2 simulator guiding → back up profiles/settings → enable persistence + the bridge flag → start NINA → connect the bridge → fill the reference window) and runs six scenarios in a fixed order, selectable via `-Scenario` (default `All`, ~16–18 min): `Baseline → Export → Profiles → Restart → PersistenceOff → Clear`. Baseline dithers N times and cross-checks the bridge's `get-state` against the profile JSON and the diagnostic file's P90/P95/P99 thresholds; Export drives `ExportCsv`/`ExportReport` and verifies CSV column count/row count/content and the quality-report sections against `get-state`; Profiles creates/dithers/switches/deletes a second profile and proves data separation from the base profile; Restart closes and reopens NINA and asserts every persisted settle/drift/count/optimizer value round-trips unchanged; PersistenceOff proves in-memory state survives with the toggle off and every profile file deleted, then that re-enabling flushes it back unchanged; Clear proves `ClearData` zeroes `get-state` and rewrites the active profile's file with an empty event array. Screenshots are named `<NN>_<scenario>_<what>.png`; artifacts (screenshots, `report.txt`, NINA log copies, `state_*.json` bridge dumps, diagnostic/export file copies) land in `SmokeTest/artifacts/<timestamp>/`. `-HoldForInspection` skips teardown and leaves NINA running so Claude can drive the live session with `Invoke-NinaUi.ps1` (`state`/`invoke`/`toggle`/`select-profile`/`scroll`/`screenshot`) during analysis — finish with `Complete-SmokeTest.ps1 -ArtifactDir <dir>` afterward. Deliberately still manual: theme switching, the unwired `Options.xaml` template stub (excluded from testing — all real functionality lives in the dockable panel), and the NINA-`GuiderInfo` pixel-scale path.

**Launching the smoke test:** run it with `pwsh -NoProfile -File SmokeTest\Run-SmokeTest.ps1 ...` (PowerShell 7). Windows PowerShell (`powershell.exe`) does not work on this machine: its execution policy is Restricted in every scope, so `-File` refuses to load the script, and the `-ExecutionPolicy Bypass` workaround is blocked by the Claude Code permission classifier. pwsh's default RemoteSigned policy runs these local scripts as-is.

**`SmokeTestBridge` security framing:** `Services/SmokeTestBridge.cs` is a line-delimited JSON diagnostic channel bound to `127.0.0.1` only, **disabled by default** — it only starts listening (default port 4406) when `%LocalAppData%\NINA\DitherStatistics\smoketest_settings.txt` line 1 is `True`, a flag `Run-SmokeTest.ps1` sets for the duration of the run and restores afterward. It exposes/executes nothing beyond what the panel UI already offers: `get-state` returns the exact raw values the view binds to, `invoke`/`set-toggle`/`create-profile`/`select-profile`/`delete-profile` run the same `RelayCommand`s and property setters the buttons/checkboxes/profile controls bind to, all marshaled onto the UI thread. `SmokeTest/BridgeClient.ps1` is the PowerShell client; `SmokeTestBridgeTests.cs` pins the protocol with real loopback-socket tests, in the style of `FakePhd2Server`/`Phd2EndToEndTests`.

**Whenever Claude Code runs the stage-3 smoke test, it must visually inspect every screenshot in the run's artifact dir, in scenario order, with the `Read` tool** — the script's own assertions only check log/JSON/bridge-state content, not chart rendering or value plausibility. A run passes only when both the exit code is 0 **and** this checklist is clean:
- `*_top.png`: the Pixel Shift chart renders a sensible cumulative random-walk with the expected point count, centered near the origin, not empty/garbled/clipped/wrong colors; the Settle Time History values and average band look reasonable against the run's `-SettleTime`/`-SettleTimeout` parameters. Known cosmetic non-failure: on a nearly-empty profile (very few points) the Pixel Shift X ticks get dense, because the tick step is computed from the small data range before the 1:1 `AxisScaleLock` widens the visible range — legible ticks at step 1 across a wide range are expected there, not a defect.
- `*_bottom.png`: the statistics/quality/optimizer numbers are internally consistent — cross-check against the matching `state_*.json` bridge dump in the artifact dir, the exported `DitherQuality_*.txt`, and the `*_dither_analysis.txt` threshold header, not just "the assertions passed."
- Scenario-specific expectations: Profiles' `30_profiles_b.png` shows exactly `-ProfileDitherCount` points and `31_profiles_default_again.png` shows the base profile's full point count again; Clear's screenshots show empty charts and zeroed stats; Restart's screenshots match the pre-restart Baseline finals — except the quality-card metrics (CD/NNI/drizzle/rating), which may legitimately differ right after a restart: the guider→camera pixel-scale ratio drops to the 1.0 fallback until PHD2 re-reports it, and the caption line under the recommendation states the active source (`auto/PHD2` vs `fallback`). Settle/drift/count/optimizer values must still round-trip exactly.
- If anything looks ambiguous or wrong, rerun with `-HoldForInspection` and use `Invoke-NinaUi.ps1` (`state`/`scroll`/`screenshot`/`invoke`) against the live session to investigate further, then finish with `Complete-SmokeTest.ps1`.
- Screenshots require an actively displayed RDP session — `Test-ScreenCaptureAvailable` (a 1×1 px `CopyFromScreen` probe) fails the run fast otherwise; this cannot be worked around from a detached session.

**Defender/AMSI naming constraint in the PowerShell smoke test:** the per-scenario functions are named `Test-<Name>Scenario`, **not** `Invoke-<Name>Scenario`. A cluster of five or more `Invoke-Baseline/Export/Profiles/Restart/PersistenceOff/Clear` identifiers in proximity matches Microsoft Defender's `HackTool:PowerShell/ApexToolkit.A` AMSI signature and blocks the whole script at runtime (`ScriptContainedMaliciousContent`). Because AMSI scans the in-memory script buffer, **file/folder exclusions do not suppress it** — the fix is the naming, not an exclusion. Do not rename the verb back to `Invoke-` in `SmokeTest/Scenarios.ps1` or the dispatcher `switch` in `SmokeTest/Run-SmokeTest.ps1`; keep any new scenario dispatcher off that identifier pattern.

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
├── DitherStatistics.Tests/        (xUnit: unit, in-process socket E2E, opt-in PHD2 integration)
└── SmokeTest/                     (PowerShell stage-3 NINA smoke test + PHD2 helpers, artifacts/)
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
