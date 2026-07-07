# Improvement Plan: Post-Refactoring Review (v2 branch)

Full-code review after the 8-stage refactoring and the addition of the automated
smoke tests (branch `refactor/v2`, commit `3da17b8`). Date: 2026-07-07.

---

## 1. Review verdict

**The refactoring was implemented correctly.** Verified in this review:

- All 8 stages of `REFACTORING_PLAN.md` are complete and match the actual code layout
  (`Models/`, `Phd2/`, `Services/`, `ViewModels/`, `Views/`, `DitherStatistics.Tests/`).
- All declared invariants held: JSON property names (incl. `_Quality/_Balanced/_Performance`
  suffixes, guarded by `JsonContractTests`), byte-identical settings file formats, MEF exports,
  plugin GUID, namespace split for `Options.xaml`, lazy `WpfPlot` creation on the UI thread,
  connection timing (2 s / 10 s / 5 s), diagnostic file schema.
- The extracted services follow the Logger-free convention; the VM keeps try/catch + logging
  at call sites as planned.
- Lock discipline in `DitherOptimizerService` is consistent with its documented rule
  (only `ditherDataLock` → `referenceLock` nesting, via `FinalizeCurrentSeriesLocked`);
  the stale-timer guard and rapid-dithering finalization are correct.
- `dotnet test`: **103 passed, 0 failed** (2 PHD2 integration tests auto-skipped, PHD2 not
  guiding during the review). Build Debug clean (only the two pre-existing NU1701 warnings).
- No German text remains in any committed source file **except `REFACTORING_PLAN.md`** (task C1).
- The smoke test design (driving dithers via PHD2 RPC instead of the Sequencer) is sound and
  its persistence-flag backup/restore in the teardown is correct.

The findings below are defects and gaps that survived the refactoring (most predate it) plus
improvements the new structure now makes cheap. Nothing here blocks a merge of `refactor/v2`;
tasks A1–A3 should land before the next release, because they affect real observing sessions.

---

## 2. Task list

Priorities: **A = correctness (fix before release)**, B = robustness, C = cleanup/optional.
Each task names the model best suited to execute it (Fable / Opus / Sonnet / Haiku) and why.

### A1 ✅ — PHD2 connection lifecycle: reconnect-after-dispose and string-status fragility

**Model: Fable** — concurrency semantics across three classes, event-ordering guarantees the
optimizer depends on, and a deliberate behavior change that must be re-specified in the E2E
tests. This is the same risk class as refactoring stage 6.

Problems (all in `Phd2/Phd2ConnectionManager.cs` + `Phd2/PHD2Client.cs`):

1. **The manager can never be stopped.** `Phd2ConnectionManager` has no `Stop()`/`Dispose()`.
   When the VM is disposed, `phd2Client.Dispose()` → `Disconnect()` fires
   `ConnectionStatusChanged("Disconnected")` → the manager's handler
   (`Phd2ConnectionManager.cs:80`) matches `Contains("Disconnected")` and schedules a
   reconnect 5 s later — the client happily reconnects to PHD2 **after the plugin was
   disposed**, and the read loop keeps pushing events into the disposed optimizer/VM.
2. **String matching is wrong:** `status.Contains("Connected")` (`Phd2ConnectionManager.cs:71`)
   is `true` for `"Disconnected"`, so an explicit disconnect is logged through the success
   branch and resets the failure-dedup flag. The whole status channel is stringly-typed.
3. **Resource leak on reconnect:** `ConnectAsync` (`PHD2Client.cs:75-81`) overwrites
   `client`/`reader`/`writer` without disposing the previous instances after a connection
   loss (only explicit `Disconnect` cleans up).

Fix outline:
- Replace the `string` status event with an enum
  (`Connected / ConnectionFailed / ConnectionLost / Disconnected`), keep the log texts.
- Give the manager `Stop()` (or `IDisposable`): cancel the retry loop, unsubscribe, and make
  `Disconnected` (explicit) *not* trigger a reconnect — only `ConnectionLost` should.
- VM `Dispose()`: stop the manager **before** disposing the client.
- Dispose stale `TcpClient`/reader/writer at the top of `ConnectAsync`.
- Update `Phd2EndToEndTests` (they assert the exact status strings) and add a test:
  after `Stop()` + server restart, no reconnect happens.

**Done (2026-07-07):** `Phd2ConnectionStatus` enum (`Phd2/Phd2EventArgs.cs`) replaces the
string status; log texts preserved. `Phd2ConnectionManager` is `IDisposable` with `Stop()`
(cancels the retry loop via CTS, unsubscribes); only `ConnectionLost` triggers a reconnect —
explicit `Disconnected` and `ConnectionFailed` do not. A read-loop error now also maps to
`ConnectionLost` (previously the `"Error: …"` string matched no reconnect branch, leaving the
client dead until restart — deliberate behavior change). `ConnectAsync` disposes the stale
`TcpClient`/reader/writer/CTS before reconnecting. VM `Dispose()` disposes the manager before
the client. The three delays are constructor-injectable with unchanged defaults (pre-work for
C5). New `Phd2ConnectionManagerTests.cs`: reconnect on loss, no reconnect after `Stop()`, no
reconnect on explicit disconnect. Build clean; 106 tests pass (2 PHD2 integration skipped).

### A2 ✅ — Diagnostic files are named `00010101_000000_…` when PHD2 was already guiding

**Model: Sonnet** — small, well-scoped fix with a clear test.

`DitherOptimizerService.sessionStartTime` is only set in `HandleGuidingStarted()`
(`Services/DitherOptimizerService.cs:164`). If the plugin connects while PHD2 is already
guiding (the *normal* case when NINA is restarted mid-session), no `StartGuiding` event ever
arrives and every diagnostic file for that session is named
`00010101_000000_<profile>_dither_analysis.txt` — and every such session **overwrites the
same file**. The smoke test currently works around exactly this by force-restarting guiding
(`Run-SmokeTest.ps1` step 4); the workaround can stay (it also stabilizes the reference
window), but the underlying bug should be fixed.

Fix: initialize `sessionStartTime = DateTime.Now` in the constructor (and optionally re-stamp
on `HandleGuidingStarted`), plus a unit test that a dither cycle without `HandleGuidingStarted`
produces a file with a plausible timestamp.

**Done (2026-07-07):** `sessionStartTime` is now initialized to `DateTime.Now` in the
`DitherOptimizerService` constructor (`Services/DitherOptimizerService.cs:75-83`); still
re-stamped in `HandleGuidingStarted()` when that event does arrive. New test
`DiagnosticFile_HasPlausibleTimestamp_WhenGuidingStartedNeverFired`
(`DitherStatistics.Tests/DitherOptimizerServiceTests.cs`) runs a full dither cycle without ever
calling `HandleGuidingStarted` and asserts the written `*_dither_analysis.txt` file's session
timestamp is within a minute of `DateTime.Now`. Build clean; 109 tests pass (2 PHD2 integration
skipped).

### A3 ✅ — Cross-thread race: snapshot/persistence built on the PHD2 read-loop thread

**Model: Opus** — contained change, but it requires tracing every caller and arguing the
threading model; a wrong fix would deadlock (`Dispatcher.Invoke` from the UI thread itself).

`SaveStatisticsData()` → `BuildCurrentSnapshot()` (`ViewModels/DitherStatisticsVM.cs:920`)
enumerates `ditherEvents`, `settleTimeValues`, `pixelShiftValues` and reads
`selectedProfileName`. It is called:
- from `OnPHD2SettleDone` (read-loop thread, `DitherStatisticsVM.cs:1290`) *after* the
  synchronous `Dispatcher.Invoke` block, and
- from `OnDitherRecommendationUpdated` (thread-pool/timer thread, `DitherStatisticsVM.cs:656`).

Meanwhile the UI thread mutates the same collections in `SwitchToProfile`, `ClearData` and
`RestoreStatisticsData`. Consequences: `InvalidOperationException` during enumeration (caught,
snapshot lost) or — worse — a torn snapshot written to the **newly selected** profile's file
during a profile switch.

Fix outline: marshal the snapshot creation (not necessarily the file write) onto the
dispatcher — e.g. build the snapshot inside the existing `Dispatcher.Invoke` block in
`OnPHD2SettleDone`, and use `Dispatcher.Invoke` in `OnDitherRecommendationUpdated`'s
persistence path. Keep the file I/O off the UI thread if easy, but correctness first: the
lists are small. Document the rule ("live collections are UI-thread-only") next to
`BuildCurrentSnapshot`.

**Done (2026-07-07):** The fix is centralized in `SaveStatisticsData()`
(`ViewModels/DitherStatisticsVM.cs`): it now builds the snapshot **and** captures
`selectedProfileName` inside a new `InvokeOnUiThread(...)` helper, so the profile name and
the enumerated live collections are read as one atomic UI-thread unit — a concurrent
`SwitchToProfile` can no longer tear the read or divert a snapshot into the wrong profile's
file. The subsequent `SaveProfileDataToFile` write stays on the calling (background) thread.
Centralizing in `SaveStatisticsData` covers **all** off-thread callers at once — the PHD2
read-loop path (`OnPHD2SettleDone`) and the optimizer-analysis path
(`OnDitherRecommendationUpdated`) — as well as the UI-thread callers, without per-call-site
changes. `InvokeOnUiThread` runs the action inline when already on the UI thread
(`Dispatcher.CheckAccess()`, so no deadlock/re-entrancy) and also when there is no WPF
`Application` (unit tests). The UI-thread-only rule is now documented on both
`BuildCurrentSnapshot` and `SaveStatisticsData`. Build clean; 109 tests pass (PHD2 was
guiding, so the 2 integration tests ran too).

### A4 ✅ — PHD2Client JSON-RPC hardening

**Model: Opus** — classic async/concurrency pitfalls; needs judgment but is localized to
one file, and the E2E tests provide a safety net.

Three independent weaknesses in `Phd2/PHD2Client.cs`:

1. **Concurrent writes are unserialized.** `SendJsonRpcRequest` writes with
   `writer.WriteLineAsync` (`PHD2Client.cs:430`); `QueryExposureTime` and `QueryPixelScale`
   are fired from several places (`ConnectAsync` delayed task, `StartGuiding`,
   `ConfigurationChange`, the `SettleDone` pixel-scale retry) and can overlap → interleaved
   bytes produce a corrupt request line. Fix: a `SemaphoreSlim(1,1)` around the write.
2. **TCS continuations run under `requestLock`.** `HandleJsonRpcResponse` calls
   `SetResult`/`SetException` while holding the lock (`PHD2Client.cs:467-480`); without
   `TaskCreationOptions.RunContinuationsAsynchronously` the awaiting continuation executes
   inline on the read-loop thread inside the lock. Fix: create the TCS with
   `RunContinuationsAsynchronously`, and fail all pending requests on disconnect instead of
   letting each ride out its 5 s timeout.
3. **An empty line is treated as EOF.** `ReadEventsAsync` uses `string.IsNullOrEmpty(line)`
   (`PHD2Client.cs:145`) — only `null` means the stream ended; a blank keep-alive line would
   tear the connection down. Fix: `line == null` for EOF, skip empty lines.

**Done (2026-07-07):** All three fixed in `Phd2/PHD2Client.cs`.
(1) A `SemaphoreSlim(1,1) writeLock` serializes the `writer.WriteLineAsync` in
`SendJsonRpcRequest`, so overlapping `QueryExposureTime`/`QueryPixelScale` calls can no longer
interleave bytes; disposed in `Dispose()`.
(2) The request TCS is created with `TaskCreationOptions.RunContinuationsAsynchronously`, so the
awaiting continuation no longer runs inline on the read-loop thread under `requestLock`. Because
that continuation now runs *after* `ProcessEvent`'s `using JsonDocument` block disposes the
document, the result element is `Clone()`d before `TrySetResult` (otherwise it would throw
`ObjectDisposedException` on access). A new `FailAllPendingRequests(...)` fails every in-flight
request immediately on connection loss / read-loop error / explicit disconnect, instead of each
riding out its 5 s timeout; the response setters use the `Try*` variants to stay safe against
that concurrent completion.
(3) EOF is now `line == null` only; an empty keep-alive line is skipped (`continue`) rather than
tearing the connection down. New E2E test
`EmptyKeepAliveLine_DoesNotTearDownTheConnection` (`Phd2EndToEndTests.cs`) pushes blank lines
then a guide step and asserts the client stays connected with no `ConnectionLost`. Build clean;
110 tests pass (PHD2 was guiding, so the 2 integration tests ran too).

### B1 ✅ — Atomic writes for profile data files

**Model: Sonnet** — mechanical, format must stay identical, existing tests cover roundtrips.

`StatisticsProfileService.SaveProfileDataToFile` (`Services/StatisticsProfileService.cs:110`)
uses `File.WriteAllText` directly. A crash/power loss mid-write leaves a truncated JSON;
`LoadProfileDataFromFile` then throws and the VM starts empty — the user's accumulated
multi-session statistics are gone. Fix: write to `<name>.json.tmp`, then `File.Replace`/
`File.Move` over the target; on load, treat a corrupt file by renaming it to `<name>.json.bak`
(and logging at the VM call site) instead of failing hard. Add tests for both. The plain-text
settings files can stay as-is (single-value, self-healing defaults).

**Done (2026-07-07):** `SaveProfileDataToFile` (`Services/StatisticsProfileService.cs`) now
serializes to `<name>.json.tmp` under the existing `persistenceLock`, then commits it with
`File.Replace` when the target already exists or `File.Move` for a first write — a crash
mid-write leaves only the stale `.tmp` behind, never a truncated target file.
`LoadProfileDataFromFile` still throws on unparsable content (the VM's existing `try/catch` at
every call site already logs that and starts empty, so the "failing hard" the task described
was really "loses the chance to recover the bad file"); it now additionally renames the corrupt
file to `<name>.json.bak` (overwriting any previous `.bak`) before rethrowing, so the broken
content survives for inspection instead of being silently discarded or overwritten by the next
save. New tests in `DitherStatistics.Tests/StatisticsProfileServiceTests.cs`:
`Load_CorruptFile_RenamesFileToBak`, `Load_CorruptFile_OverwritesExistingBakFile`,
`Save_DoesNotLeaveTempFileBehind`, `Save_OverExistingFile_ReplacesContentAtomically`. Build
clean; 112 tests pass (2 PHD2 integration tests skipped, PHD2 not guiding during this run).

### B2 ✅ — Smoke test permanently pollutes the user's real statistics profile

**Model: Sonnet** — PowerShell-only change, but the teardown ordering needs care.

`Run-SmokeTest.ps1` backs up and restores `persistence_settings.txt`, but the N simulator
dithers it triggers are persisted into the **user's active profile JSON**
(`profiles\<active>.json` — on this machine that is a real target profile) and stay there
forever. Anyone who later enables persistence sees simulator data mixed into real statistics.

Fix (choose one, document in `SmokeTest/README.md`):
- Preferred: back up `profiles\*.json` + `profiles_list.txt` alongside the persistence flag
  and restore both in the teardown (the artifacts folder already keeps the run's copy), or
- switch to a dedicated `SmokeTest` statistics profile for the run and delete it afterwards
  (more moving parts: needs profiles_list manipulation while NINA is closed).

**Done (2026-07-07):** Implemented the preferred option in `SmokeTest/Run-SmokeTest.ps1`. Right
before persistence is enabled, the script now backs up `profiles\*.json` into
`artifacts\<timestamp>\profiles_backup\` and captures `profiles_list.txt`'s content (tracking
whether each existed, so a first-ever run with no `profiles\` folder yet is restored back to
"doesn't exist" rather than leaving an empty folder behind). In the `finally` teardown, right
after the existing persistence-flag restore, the script deletes the (possibly run-modified)
`profiles\` directory and recreates it from the backup, and restores or removes
`profiles_list.txt` the same way `persistence_settings.txt` already was — so the run's own
copy of the active profile JSON collected for assertions (step 7c) still lands in the artifacts
folder, but the user's real profile data is untouched afterward. `SmokeTest/README.md` documents
the new backup/restore step. Verified with a live run
(`Run-SmokeTest.ps1 -DitherCount 5 -ReferenceWaitSec 30 -SkipBuild`, `Default` was the active
profile): all 4 assertions passed (0 plugin `ERROR` lines, diagnostic file with 10 series, exactly
5 new `DitherEvents`, all plausible), the log showed "Backed up statistics profiles..." /
"Restored original statistics profiles.", and a manual check post-run confirmed `Default.json` is
byte-identical to its pre-run state (same size 17090 bytes, same `LastWriteTime`, back to 5
`DitherEvents` after having grown to 10 mid-run) and `profiles_list.txt` unchanged. Build clean
(Debug, 0 errors, only the 2 pre-existing NU1701 warnings); no C# was touched by this task.

### B3 ✅ — Diagnostic file housekeeping

**Model: Haiku** — trivially specified: bounded deletion of a known file pattern + one test.

`DitherOptimizerService` writes two diagnostic files per guiding session and profile into
`%LocalAppData%\NINA\DitherStatistics\` and never deletes anything — after a season that is
hundreds of files. Add a prune step (e.g. on service construction: keep the newest ~30
`*_dither_analysis.txt`/`*_settle_analysis.txt`, delete the rest), directory injectable as
today, one unit test.

**Done (2026-07-07):** `PruneDiagnosticFiles()` (`Services/DitherOptimizerService.cs`) is called
in the constructor. It independently keeps the newest ~30 `*_dither_analysis.txt` and the newest
~30 `*_settle_analysis.txt` files (sorted by `LastWriteTimeUtc`), deleting older ones. Directory
stays injectable. Deletion errors are caught and logged. New test
`Constructor_PrunesDiagnosticFiles_KeepsNewest30` (`DitherStatistics.Tests/DitherOptimizerServiceTests.cs`)
creates 50 old files of each type and verifies only the 30 newest remain after construction.
Build clean; 113 tests pass (2 PHD2 integration tests skipped, PHD2 not guiding).

### C1 — Translate `REFACTORING_PLAN.md` to English

**Model: Haiku** — pure translation, no code. Content is historical and complete.

`CLAUDE.md` declares all documentation **including REFACTORING_PLAN** English-only; the file
is entirely German. Either translate it (preserving the per-stage ✅ result notes), or — if
the maintainer prefers — declare it a frozen historical document and note that exemption in
`CLAUDE.md`. Decision is the maintainer's; the plan assumes translation.

### C2 ✅ — Unbounded session-statistics growth in the optimizer

**Model: Sonnet** — small change but requires a semantic decision (what "session RMS" means).

`sessionDX/sessionDY/sessionRMS` (`Services/DitherOptimizerService.cs:49-51`) grow for the
whole guiding session (≈ 43 000 entries per 24 h at 2 s exposures) and
`ComputeSessionStatsLocked` re-runs full-list LINQ passes on every analysis. Not a crash risk,
but pointless O(n) work and memory. Options: cap the lists to a rolling window (consistent
with the 15-min reference window), or keep incremental sums (Welford). Whichever is chosen,
state the definition of "Running RMS" in the diagnostic-file header comment.

**Done (2026-07-07):** Chose the incremental-sums option: "session" here means the whole
guiding session since the last reset (`GuidingStarted`/`Disconnected`), a different and
intentionally unbounded-count scope from the 15-minute/400-point reference window, so
capping it to a rolling window would have silently changed its meaning. `sessionDX`,
`sessionDY` and `sessionRMS` (`Services/DitherOptimizerService.cs`) are now a private
`WelfordAccumulator` each (Welford's online mean/sample-variance algorithm) instead of
`List<double>`: O(1) memory and O(1) per-`HandleGuideStep` update instead of retaining every
point and re-running full-list LINQ passes on every analysis. `ComputeSessionStatsLocked`
now reads `SampleStdDev` off the accumulators; `Reset()` replaces `Clear()` at the two
existing reset sites (`HandleGuidingStarted`, `HandleDisconnected`). The diagnostic-file
header in `WriteDitherAnalysisFile` now states the definition explicitly: `Running_RMS`/
`RMS_StdDev` cover every guide step since the last session reset (not the reference window),
with the formulas spelled out (`sqrt(RA_stddev² + DEC_stddev²)` / stddev of per-step pair
distances). New test `SessionRms_MatchesDirectCalculation_ForWelfordAccumulator`
(`DitherStatistics.Tests/DitherOptimizerServiceTests.cs`) pins the Welford-accumulated
`CurrentRunningRMS`/`CurrentRMSStdDev` to the direct Σ(x-mean)² formula it replaced, computed
over the exact known guide steps a full dither cycle feeds into the session statistics. Build
clean; 114 tests pass (2 PHD2 integration tests skipped, PHD2 not guiding during this run).

### C3 ✅ — Pixel-shift chart creates one plottable per point

**Model: Sonnet** — ScottPlot 4 API knowledge needed; visual result must stay identical
(gradient + lime last point), verified via the smoke-test screenshots.

`PixelShiftChartRenderer.Render` (`Services/ChartRenderers.cs:51-64`) adds one single-point
scatter per dither to get the red gradient; with persistence and hundreds of dithers every
render rebuilds hundreds of plottables. Replace with batched rendering (e.g. one
`AddScatterPoints` per gradient bucket, or a `MarkerPlot` set) while keeping the visual output.

**Done (2026-07-07):** The per-point gradient loop in `PixelShiftChartRenderer.Render`
(`Services/ChartRenderers.cs`) now adds every point to a single `ScottPlot.Plottable.BubblePlot`
(`plot.Plot.AddBubblePlot()`, one `.Add(x, y, radius, fillColor, edgeWidth, edgeColor)` call per
point) instead of one `AddScatter` plottable per point. `BubblePlot` stores its points in an
internal `List<Bubble>` and renders them as filled circles in pixel-radius units (confirmed from
the ScottPlot 4.1.59 source, commit `689c100f`), so `radius: 3` reproduces the previous
`MarkerSize = 6` filled-circle markers exactly (MarkerSize is a diameter), and `edgeColor` is set
equal to `fillColor` so no outline appears (matching the previous `LineWidth = 0`). The
connection-line and lime-green "Latest" overlay (already O(1) single-array `AddScatter` calls)
and their add order are unchanged, so draw order (line → gradient points → lime highlight →
crosshair) is preserved. Verified with a standalone harness (WPF `WpfPlot` + the real
`PixelShiftChartRenderer.Render`, 300 synthetic random-walk points, `SaveFig` to PNG): plottable
count dropped from ~302 to 5, render time 37 ms, and the saved image shows the same visual
result as before — a dark→bright red gradient scatter connected by thin lines with a single lime
dot at the last point. Build clean; `dotnet test` 114 passed, 2 skipped (PHD2 integration tests,
not guiding during this run).

### C4 ✅ — Minor VM/manager cleanups

**Model: Haiku** — each item is a one-liner with an obvious correct form; no design freedom.

- `HideCommand`/`ToggleSettingsCommand` allocate a new `RelayCommand` on every getter access
  (`ViewModels/DitherStatisticsVM.cs:1506-1508`) — cache them like the other commands.
- `hasLoggedConnectionFailure` exists in both `PHD2Client` and `Phd2ConnectionManager` with
  overlapping dedup logic — after A1, consolidate it in the manager.
- (Optional, discuss first): the static class `DitherStatistics` in namespace
  `DitherStatistics.Plugin` shadows the root namespace; a rename (e.g. `StatisticsMath`)
  would remove a recurring source of confusion but touches many call sites — only do it if
  the maintainer wants the churn.

**Done (2026-07-07):** First two items fixed; the optional rename was left untouched (it needs
the maintainer's explicit go-ahead per its own note, and wasn't given here).
`HideCommand`/`ToggleSettingsCommand` (`ViewModels/DitherStatisticsVM.cs`) are now
`{ get; private set; }` properties assigned once in `InitializeCommands()`, alongside the other
cached commands, instead of allocating a new `RelayCommand` on every binding access.
`hasLoggedConnectionFailure` is now owned solely by `Phd2ConnectionManager`, which already had
its own copy driving the deduped user-facing "Connecting/Connection failed/lost" Info/Warning
logs from `ConnectionStatusChanged`. The field and its three dedup branches were removed from
`Phd2/PHD2Client.cs`; the client's own per-attempt detail (`Connecting to PHD2 at {host}:{port}`
and `Connection failed: {ex.Message}`) is now logged unconditionally at `Logger.Debug` instead of
gated behind the flag — it no longer needs to dedup because the manager already suppresses the
repeated higher-level status messages on every retry, and Debug-level detail is expected to be
verbose. No test asserted the removed Info/Error-level client messages. Build clean; 114 tests
pass (2 PHD2 integration tests skipped, PHD2 not guiding during this run).

### C5 ✅ — Unit tests for `Phd2ConnectionManager`

**Model: Sonnet** — straightforward testability refactor (inject delays), depends on A1.

The retry/reconnect policy (2 s / 10 s / 5 s) is the only remaining logic without any test.
After A1 introduces the enum + `Stop()`, make the three delays injectable (constructor
parameters with the current defaults) and add tests: retries until success, reconnects on
`ConnectionLost`, does **not** reconnect on explicit `Disconnected`, `Stop()` halts the loop.

**Done (2026-07-07):** The three delays were already made constructor-injectable in A1
(`Phd2ConnectionManager(client, initialDelayMs, retryDelayMs, reconnectDelayMs)`, defaults
unchanged), and A1 already added `Phd2ConnectionManagerTests.cs` covering reconnect-on-loss,
no-reconnect-after-`Stop()`, and no-reconnect-on-explicit-`Disconnected` — but always starting
from an already-connected state, so the retry loop itself (as opposed to the reconnect-after-loss
path) was never exercised. This task closes that gap with two more tests, plus a small
`FakePhd2Server` change to support them: `FakePhd2Server`'s constructor now takes an optional
`port` parameter (default `0` for the existing ephemeral-port behavior) so a server can be bound
to a specific port *after* a client has already failed to reach it there.
`RetriesUntilSuccess_WhenServerBecomesAvailableLater` reserves a free loopback port (bind-then-
immediately-`Stop()` a throwaway `TcpListener`), points a `PHD2Client`/`Phd2ConnectionManager` at
it while nothing is listening (`Start()` fails, then the retry loop fails again after
`retryDelayMs`), then binds a `FakePhd2Server` to that same port and asserts the manager connects
without any code change on the production side. `Stop_DuringRetryLoop_PreventsLaterConnect` runs
the same setup but calls `manager.Stop()` after two observed failures, *then* starts the server on
that port, and asserts no connection ever happens — proving `Stop()` halts an in-flight retry loop
(the existing A1 test only proved `Stop()` prevents a reconnect-after-loss from an already-connected
state). Build clean; 116 tests pass (2 PHD2 integration tests skipped, PHD2 not guiding during this
run); the two new tests were run three consecutive times with no flakiness.

---

## 3. Suggested order & dependencies

| Order | Task | Depends on | Risk |
|---|---|---|---|
| 1 | A2 (session timestamp) | — | low |
| 2 | A4 (RPC hardening) | — | low/medium |
| 3 | A1 (connection lifecycle) | A4 helpful, not required | medium |
| 4 | C5 (manager tests) | A1 | low |
| 5 | A3 (snapshot threading) | — | medium |
| 6 | B1 (atomic writes) | — | low |
| 7 | B2 (smoke-test isolation) | — | low |
| 8 | B3, C1, C2, C3, C4 | — | low |

Verification after A1–A4: full `dotnet test`, one `Run-SmokeTest.ps1` pass, and one manual
NINA session with a mid-session PHD2 restart (exercises reconnect + collection-window abort).
