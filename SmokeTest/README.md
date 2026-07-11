# Automated Functional Tests (Smoketest)

Three stages replace the manual test workflow (start PHD2, set up guiding,
NINA + Sequencer, wait for dithers, check GUI). Core idea: the plugin listens
on the PHD2 socket, not on NINA — dithers are triggered directly via PHD2's
JSON-RPC API (port 4400), and the panel itself is driven through the plugin's
own `SmokeTestBridge` diagnostic channel rather than UI Automation (NINA
exposes no tab *content* to UIA — see **UI Automation spike outcome** below).
Sequencer, NINA hardware-connect, and real hardware are not needed.

| Stage | What runs | What it verifies | Duration |
|---|---|---|---|
| 1 | `dotnet test` (always) | Socket → `PHD2Client` parsing → `DitherOptimizerService`, deterministic against a fake PHD2 server (`FakePhd2Server.cs`, `Phd2EndToEndTests.cs`) | seconds |
| 2 | `dotnet test` while PHD2 is running | Compatibility with the real PHD2 wire format, including a real dither round-trip (`Phd2IntegrationTests.cs`, auto-skips without PHD2) | ~3 min |
| 3 | `Run-SmokeTest.ps1` | The plugin in real NINA across six scenarios: MEF load, panel/charts, PHD2 auto-connect, persistence, multi-profile separation, CSV/quality export, Clear Data | ~16–18 min (full run) |

## One-time setup

**PHD2** (for stages 2 + 3):

1. Create equipment profile **`Simulator`**: camera = *Simulator*, mount = *On-camera*.
2. Enable *Tools → Enable Server* (saved in the profile).
3. Calibrate manually once, then enable **"Auto restore calibration"** in the
   guiding settings — subsequent runs skip calibration.

**NINA** (for stage 3):

1. Plugin is installed (the build does this automatically via PostBuild).
2. In the **Imaging tab**, arrange the *Dither Statistics* panel to be visible
   once — NINA remembers the panel layout. The script switches the tab itself
   automatically (NINA always starts on the Equipment tab; the switch runs via
   UI automation — adjust `-ImagingTabLabel` for a localized NINA).

Default paths (overridable via parameter): PHD2 `%ProgramFiles(x86)%\PHDGuiding2\phd2.exe`,
NINA `%ProgramFiles%\N.I.N.A. - Nighttime Imaging 'N' Astronomy\NINA.exe`.

## Usage

```powershell
# Stage 1 (+ 2, if PHD2 is guiding): runs with every test run
dotnet test DitherStatistics.sln

# Stage 2 specifically: start PHD2 simulator guiding first, then test
.\SmokeTest\Start-Phd2Guiding.ps1
dotnet test DitherStatistics.sln --filter "Category=Integration"

# Stage 3: full run, all six scenarios (~16-18 min)
.\SmokeTest\Run-SmokeTest.ps1

# Stage 3: a single scenario or a subset, against an already-deployed plugin
.\SmokeTest\Run-SmokeTest.ps1 -Scenario Baseline -DitherCount 4 -ReferenceWaitSec 30
.\SmokeTest\Run-SmokeTest.ps1 -Scenario Export,Profiles -SkipBuild

# Stage 3: leave NINA running for live, interactive analysis
.\SmokeTest\Run-SmokeTest.ps1 -HoldForInspection
.\SmokeTest\Invoke-NinaUi.ps1 state
.\SmokeTest\Invoke-NinaUi.ps1 screenshot -Path .\look.png -ScrollDirection Down
.\SmokeTest\Complete-SmokeTest.ps1 -ArtifactDir .\SmokeTest\artifacts\<timestamp>
```

`Run-SmokeTest.ps1` boots **one** NINA session and reuses it across every
requested scenario: stop NINA (locks the plugin DLL) → build/deploy → PHD2
simulator guiding → back up the user's statistics profiles/settings → enable
persistence and the `SmokeTestBridge` diagnostic flag → start NINA → wait for
the plugin's PHD2-connection log line → connect the bridge → Imaging tab →
restart guiding → fill the optimizer's reference window → run each requested
scenario in the fixed order below → close NINA (unless `-HoldForInspection`
or `-KeepNina`) → assert no plugin `ERROR` lines across the whole run's NINA
log(s) → restore all backed-up settings/profiles → sweep this run's export
files into the artifact dir. Exit code 0 = all assertions passed.

Key switches: `-Scenario` (`All` default, or any subset of `Baseline`,
`Export`, `Profiles`, `Restart`, `PersistenceOff`, `Clear` — always run in
that fixed order), `-DitherCount` (default 6), `-ProfileDitherCount` (default
3, for the Profiles scenario), `-SkipBuild`, `-HoldForInspection`, `-KeepPhd2`,
`-KeepNina`, `-Configuration Debug`, `-ImagingTabLabel` (localized NINA).
Running a scenario standalone that depends on prior data (e.g. `-Scenario
Export` alone) seeds a short baseline (`Min(5, DitherCount)` dithers) first
so the scenario has something to assert against.

## Scenarios

Scenarios always run in this order — later ones depend on data earlier ones
create, and destructive ones run last:

`Baseline → Export → Profiles → Restart → PersistenceOff → Clear`

All "read the UI" assertions go through the bridge's `get-state` (the exact
values the panel binds to); clicks/toggles go through bridge commands;
scrolling and screenshots stay at the UI-Automation/mouse level (see
**SmokeTestBridge** below for why).

- **Baseline** — dithers `-DitherCount` times into the active profile.
  Asserts: no plugin `ERROR` lines; the diagnostic file
  (`*_dither_analysis.txt`) gained ≥ N dither series; the profile JSON gained
  exactly N new, plausible `DitherEvents` (`Success`, `0 < SettleTime ≤
  Timeout+30s`, pixel shift present); `get-state`'s Total/Successful/Success
  Rate/Median/Min/Max settle and drift match values recomputed from the
  profile JSON; the optimizer's `SettlePixel_Strict/Standard/Fast` match the
  diagnostic file's `Threshold_P90/P95/P99` header. Screenshots:
  `10_baseline_top.png` (charts), `11_baseline_bottom.png` (scrolled to
  stats/quality/optimizer).
- **Export** — triggers `ExportCsv` then `Recalc` + `ExportReport` over the
  bridge. Asserts: the CSV has the exact 9-column header, one row per profile
  JSON event, every row has exactly 9 comma-separated fields (proves numeric
  fields stay culture-invariant even under a decimal-comma locale), and
  first/last-row values match the JSON; the quality report contains all six
  section headers and its Combined Score/Discrepancy/Nearest-Neighbor-Index
  match `get-state`'s Quality block. Both files are copied into the artifact
  dir.
- **Profiles** — enables multi-profile mode, creates `SmokeTestB`, dithers
  `-ProfileDitherCount` times into it, and proves data separation: the new
  profile's file/`get-state` show exactly the new count while the base
  profile's file is untouched; switching back restores the base profile's
  count; deleting `SmokeTestB` falls back to `Default`, removes its file, and
  updates `profiles_list.txt`. The multi-profile toggle is restored to its
  prior state afterward. Screenshots: `30_profiles_b.png` (exactly N points),
  `31_profiles_default_again.png` (base profile's full point count again).
- **Restart** — closes and reopens NINA mid-run. Asserts: the profile JSON is
  either byte-identical across shutdown or re-serialized with unchanged data;
  after restart, every persisted value (Total/Successful/SuccessRate,
  Median/Min/Max/Average/StdDev settle, drift X/Y, chart point counts, and
  the optimizer's `SettlePixel_*`/`DitherEventsAnalyzed`) round-trips exactly;
  no phantom events appear. Screenshots: `40_restart_restored_top.png`,
  `41_restart_restored_bottom.png` — should visually match the pre-restart
  Baseline finals.
- **PersistenceOff** — disables the persistence toggle. Asserts:
  `persistence_settings.txt` flips to `False`; **every** `profiles\*.json`
  file is deleted; `get-state` is completely unchanged (values live on in
  memory). Re-enabling flips the setting back to `True` and rewrites the
  active profile's file with the same event count. Screenshot:
  `45_persistence_off.png`.
- **Clear** — triggers `ClearData`. Asserts: all `get-state` numerics are
  zeroed, `Quality`/`Optimizer` are both `null`, and the active profile's
  file reappears with an empty `DitherEvents` array within 5 s. Screenshots:
  `50_clear_top.png`, `51_clear_bottom.png` — charts should be visibly empty.

**Stage 3 whole-run assertion:** no NINA log touched during the run contains
an `ERROR` line from a plugin class.

## SmokeTestBridge

`Services/SmokeTestBridge.cs` is a minimal, line-delimited JSON diagnostic
channel the plugin exposes purely for this smoke test — it is what lets the
scenarios above read exact panel-bound values and drive buttons/toggles/
profile switches without needing element-level UI Automation (which does not
work against NINA's window — see **UI Automation spike outcome** below).

- **Security:** bound to `127.0.0.1` only, **disabled by default**. It only
  starts listening once `%LocalAppData%\NINA\DitherStatistics\
  smoketest_settings.txt` line 1 is `True` (optional line 2 = port, default
  **4406**); `Run-SmokeTest.ps1` sets this flag for the run's duration and
  restores the prior file (or removes it) in teardown. It executes nothing
  beyond what the panel UI already offers — no arbitrary file/process access.
- **Protocol:** one JSON object per line, request `{"cmd":"..."}` →
  `{"ok":true,...}` / `{"ok":false,"error":"..."}`. Commands:
  - `get-state` — the exact raw values the view binds to (numerics, not
    formatted strings): dither/settle/drift totals, the Quality block, the
    Optimizer block (`_Strict`/`_Standard`/`_Fast`), toggle states,
    `ProfileNames`/`SelectedProfileName`, chart point counts.
  - `invoke` with `name` ∈ `ClearData`, `ExportCsv`, `ExportReport`, `Recalc`.
  - `set-toggle` with `name` ∈ `Persistence`, `MultiProfile`, `Quality`,
    `Optimizer` + `value` — returns the prior value.
  - `create-profile` / `select-profile` / `delete-profile` with `name`.
- **PowerShell client:** `SmokeTest/BridgeClient.ps1` (`Connect-Bridge`,
  `Get-BridgeState`, `Invoke-BridgeCommand`, `Set-BridgeToggle`,
  `New-BridgeProfile`, `Select-BridgeProfile`, `Remove-BridgeProfile`,
  `Disconnect-Bridge`). `SmokeTestBridgeTests.cs` pins the wire protocol with
  real loopback-socket tests, in the style of `FakePhd2Server`/
  `Phd2EndToEndTests`.

### Interactive analysis: `Invoke-NinaUi.ps1` + `Complete-SmokeTest.ps1`

`Run-SmokeTest.ps1 -HoldForInspection` skips its normal teardown, leaves NINA
running with the bridge enabled, and writes `hold_state.json` into the
artifact dir instead. `SmokeTest/Invoke-NinaUi.ps1` is a self-contained CLI
(re-connects to the bridge on every call — no state carried between
invocations) for Claude (or a human) to poke at that live session:

```powershell
.\SmokeTest\Invoke-NinaUi.ps1 state                                 # pretty-printed get-state JSON
.\SmokeTest\Invoke-NinaUi.ps1 invoke -Name Recalc                   # ClearData|ExportCsv|ExportReport|Recalc
.\SmokeTest\Invoke-NinaUi.ps1 toggle -Name Quality -State On
.\SmokeTest\Invoke-NinaUi.ps1 select-profile -Name Default
.\SmokeTest\Invoke-NinaUi.ps1 scroll -Direction Down -Notches 30
.\SmokeTest\Invoke-NinaUi.ps1 screenshot -Path out.png -ScrollDirection Down
```

Exits non-zero with a stderr message if NINA or the bridge isn't reachable.
Once analysis is done, `SmokeTest/Complete-SmokeTest.ps1 -ArtifactDir
<artifacts\timestamp>` finishes the deferred teardown: restores
`persistence_settings.txt` / `multiprofile_settings.txt` /
`smoketest_settings.txt`, restores the original statistics profiles, closes
NINA (unless `-KeepNina`), sweeps any exports produced during the inspection
session into the artifact dir, shuts down PHD2 (unless `-KeepPhd2`), and
removes `hold_state.json`.

## Artifacts

Each run creates `SmokeTest\artifacts\<timestamp>\` containing: screenshots
named `<NN>_<scenario>_<what>.png` (numeric prefix reflects run order, e.g.
`10_baseline_top.png`), `report.txt` (the full Pass/Fail/Log transcript),
copies of every NINA log file touched during the run, `state_*.json` bridge
snapshots (Baseline, Restart before/after), copies of the diagnostic file(s)
and profile JSON(s) at key points, an `exports\` subfolder with copies of any
CSV/quality-report exports (removed from Documents afterward so repeated runs
don't pile up), and — for `-HoldForInspection` runs pending
`Complete-SmokeTest.ps1` — `hold_state.json`.

## What deliberately stays manual

- **Visual chart correctness** (ScottPlot rendering) — only verifiable by
  visually checking the screenshots (see the mandatory screenshot checklist
  in `CLAUDE.md`).
- **Theme switching** (`NinaThemeWatcher`).
- **The `Options.xaml` page** — an unwired template stub with no data
  bindings; all real plugin functionality lives in the dockable panel, so
  there is nothing to test there.
- **Pixel scale via NINA GuiderInfo** (`IGuiderConsumer`) — would need a
  NINA guider connect (e.g. via the ninaAPI plugin); the PHD2 `get_pixel_scale`
  path is covered.
- Real hardware / real sky.

## UI Automation spike outcome

`Spike-UiaVisibility.ps1` (2026-07-10) established, against live NINA 3.1
HF2 with both the managed UIA2 stack and FlaUI/UIA3, that **NINA exposes no
tab content to UI Automation at all** — only the window shell, the main
`TabControl`'s tab *headers*, and the status bar are visible (54 raw elements,
9 buttons app-wide). The plugin panel is invisible to UIA even while
displayed on screen; this is not a deliberate suppression, most likely
NINA's restyled `TabControl`/AvalonDock peers not exposing content children.
Element-level clicking/reading of the panel from outside the process is
therefore not possible — hence `SmokeTestBridge` for interactions/value
reads, and blind mouse-wheel scrolling (`Invoke-PanelScroll`, aimed at a
proven text-only region so it never zooms a chart instead of scrolling) plus
whole-window screenshots for the visual layer. Evidence:
`artifacts/20260710_141905_uia_spike/`.

## Troubleshooting

- *"PHD2 profile 'Simulator' not found"* → see one-time setup above; a
  different name can be passed via `-Phd2ProfileName`.
- *"server (port 4400) did not come up"* → *Enable Server* is missing in the
  PHD2 profile.
- *"Could not connect to SmokeTestBridge at 127.0.0.1:4406"* → the bridge
  only starts listening once the VM constructor runs, a few seconds after
  NINA is up; `Connect-Bridge` already retries for 30 s. If it still fails,
  check `smoketest_settings.txt` is `True` and that port 4406 isn't held by
  another process (pass a different port on line 2 of the flag file, and to
  `Connect-Bridge -Port`/`Invoke-NinaUi.ps1 -Port` if changed).
- *"Screen capture unavailable"* → screenshots (`CopyFromScreen`/`PrintWindow`)
  fail while the RDP session has no actively displayed console; the session
  must be actively connected and visible, not just logged in.
- Stage-2 tests get skipped → PHD2 isn't running or isn't guiding
  (run `Start-Phd2Guiding.ps1`).
- Calibration fails in simulator slow-motion mode → in the PHD2 simulator
  profile, leave declination ≈ 0 (default) and exposure at 1 s.
- CSV/report exports use invariant-culture numeric formatting regardless of
  the host locale (Step 1b of the smoke-test extension) — a decimal-comma
  Windows locale will not break the CSV's column count or the report's
  numbers; the Export scenario's "9 fields per row" assertion is the
  regression guard for this.
