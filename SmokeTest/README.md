# Automated Functional Tests (Smoketest)

Three stages replace the manual test workflow (start PHD2, set up guiding,
NINA + Sequencer, wait for dithers, check GUI). Core idea: the plugin listens
on the PHD2 socket, not on NINA — dithers are triggered directly via PHD2's
JSON-RPC API (port 4400). Sequencer, NINA hardware-connect, and real hardware
are not needed.

| Stage | What runs | What it verifies | Duration |
|---|---|---|---|
| 1 | `dotnet test` (always) | Socket → `PHD2Client` parsing → `DitherOptimizerService`, deterministic against a fake PHD2 server (`FakePhd2Server.cs`, `Phd2EndToEndTests.cs`) | seconds |
| 2 | `dotnet test` while PHD2 is running | Compatibility with the real PHD2 wire format, including a real dither round-trip (`Phd2IntegrationTests.cs`, auto-skips without PHD2) | ~3 min |
| 3 | `Run-SmokeTest.ps1` | Plugin in real NINA: MEF load, panel/charts, PHD2 auto-connect, persistence, diagnostic files | ~10-20 min |

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

# Stage 3: quick run (5 dithers) or full run (20 dithers)
.\SmokeTest\Run-SmokeTest.ps1 -DitherCount 5 -ReferenceWaitSec 30
.\SmokeTest\Run-SmokeTest.ps1
```

`Run-SmokeTest.ps1` does: stop NINA → build + deploy → PHD2 sim guiding →
start NINA → wait for plugin connect (NINA log) → restart guiding (plugin
sees `StartGuiding`) → fill reference window → N × `dither` RPC with
SettleDone wait → screenshots → close NINA → assertions. Exit code 0 = green.

Important switches: `-SkipBuild`, `-KeepPhd2` (don't stop PHD2), `-KeepNina`,
`-Configuration Debug`, `-ImagingTabLabel` (label of the Imaging tab for a
localized NINA).

**Stage 3 assertions:**

- NINA log contains no `ERROR` lines from plugin classes.
- `%LocalAppData%\NINA\DitherStatistics\*_dither_analysis.txt` was written
  during this run and contains ≥ N dither series.
- The active profile JSON (`profiles\<name>.json`) has **exactly N new**
  `DitherEvents`, all plausible (Success, `0 < SettleTime ≤ Timeout+30 s`,
  pixel shift present).

Artifacts (screenshots, report, log and file copies) end up in
`SmokeTest\artifacts\<timestamp>\`. Screenshots: after 5 dithers (top of the
panel), at the end `nina_final_top.png` (charts) and `nina_final_bottom.png`
(scrolled to the end of the panel via mouse wheel: quality metrics, optimizer
recommendations, actions — the wheel is deliberately sent over a text area,
since over the charts it would zoom instead of scroll). The script enables
the plugin's statistics persistence for the run and restores the original
setting afterward.

## What deliberately stays manual

- **Visual chart correctness** (ScottPlot rendering) — only verifiable by
  visually checking the screenshots.
- **Theme switching** (`NinaThemeWatcher`), **Options page**, **Clear Data
  button** (GUI interaction; could be automated with FlaUI, deliberately
  omitted).
- **Pixel scale via NINA GuiderInfo** (`IGuiderConsumer`) — would need a
  NINA guider connect (e.g. via the ninaAPI plugin); the PHD2 `get_pixel_scale`
  path is covered.
- Real hardware / real sky.

## Troubleshooting

- *"PHD2 profile 'Simulator' not found"* → see one-time setup above; a
  different name can be passed via `-Phd2ProfileName`.
- *"server (port 4400) did not come up"* → *Enable Server* is missing in the
  PHD2 profile.
- Stage-2 tests get skipped → PHD2 isn't running or isn't guiding
  (run `Start-Phd2Guiding.ps1`).
- Calibration fails in simulator slow-motion mode → in the PHD2 simulator
  profile, leave declination ≈ 0 (default) and exposure at 1 s.
