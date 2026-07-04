# DitherStatistics

## 1.0.0.1
- Initial release

## 1.1.0.0
- Added experimental quality assessment

## 1.1.0.1
- Fixed version description

## 1.1.0.2
- Added wider description visible in N.I.N.A

## 1.2.0.0
- Added support for N.I.N.A 3.2 and look and feel improvements

## 1.2.0.1
- Bugfixes

## 1.2.0.2
- Bugfixes

## 1.2.0.3
- Compatibility with other plugins

## 1.2.0.4
- Bugfixes

## 1.3.0.0
- Added experimental dither settings optimizer

## 1.4.0.0
- Added optional multi-session statistics persistence: all statistics (charts, summary, quality metrics, dither settings optimizer data and recommendation) can be restored across NINA restarts via a new toggle in the Statistics panel (default: off)
- Clear Data now also resets the dither settings optimizer data and recommendation

## 1.5.0.0
- Added multiple statistics profiles: keep separate statistics per target or telescope via a new toggle in the Statistics panel (default: off)
- Profiles are selected, created (type a name and press +) and deleted through an editable profile box; the Default profile always exists and cannot be deleted
- Switching profiles instantly shows the selected profile's charts, summary, quality metrics and optimizer data; new dithers are recorded into the selected profile
- With "Keep across sessions" enabled, all profiles are persisted and restored across NINA restarts; existing 1.4 data is migrated into the Default profile automatically
- Clear Data clears the data of all profiles (profile names are kept)
- Optimizer diagnostic files (dither_analysis / positive_periods) are now written per profile, containing only that profile's data (filename: `<session>_<profile>_dither_analysis.txt`)
- Fixed: switching profiles during the 30-second post-dither collection window no longer leaks the remaining guide steps into the new profile as an orphaned series
- Reworked quality assessment (fixes several calculation errors):
  - Fixed sub-pixel phase wrapping for negative coordinates (previously mirrored instead of wrapped, distorting all metrics)
  - Gap-Fill metric is now a real drizzle weight simulation (min/mean drizzle weight per output pixel) instead of a dimensionally incorrect area heuristic; targets recalibrated (2×: ≥85% ≈30 dithers, 3×: ≥85% ≈80+ dithers)
  - Combined score no longer double-counts the discrepancy penalty; thresholds quantile-recalibrated by Monte-Carlo simulation of random dithering (~50 dithers = "Good", ~80 = "Very Good", ~120+ = "Excellent") including a 0.25/√N confidence margin so lucky few-dither sessions cannot reach top ratings
  - Removed the redundant "Voronoi CV" metric (duplicated NNI information)
  - New Drift Ratio metric warns about one-directional dither patterns (walking noise) and too-small pattern spread (hot-pixel rejection)
  - Dither offsets are now converted from guide-camera to main-camera pixels; the ratio is re-evaluated on every calculation from NINA's guider info (primary) or PHD2's pixel scale (secondary) plus the active profile's focal length / pixel size, with manual override in the panel and a clear fallback indicator
  - Drizzle pixfrac used by the simulation is now configurable (default 0.6)
## 1.6.0.0
- Redesigned the Dither Settings Optimizer with a statistically sound algorithm:
  - Settle Pixel Tolerance is now an empirical quantile (P90/P95/P99) of the measured stable-guiding scatter in a rolling 15-minute reference window, replacing the previous RMS + k·σ formula which mixed incompatible statistics
  - Profiles renamed Quality/Balanced/Performance → Strict/Standard/Fast: a tighter tolerance only buys confidence that guiding is back to normal at the cost of settle time — it does not improve image quality (Fast/P99 is the sensible default for most setups)
  - Fixed Minimum Settle Time semantics: PHD2's "min settle time" is the time the star must STAY within tolerance, not the time to reach it; the recommendation is now a small debounce value (max(2 × guide exposure, 5 s)) and the measured time-to-stable is shown separately as "Expected Settle" per profile
  - NEW: Settle Timeout recommendation per profile — covers 95% of observed settle delays plus safety margin, at least the longest actually measured settle
  - NEW: tolerance additionally shown in arcsec (via PHD2 pixel scale); footer warnings when few dithers are usable, settle times scatter widely, or dithers never stabilized
  - Dither series with failed settles or star-lost events are excluded from the analysis
  - Collection window now ends with the actual settle (SettleDone + 10 guide steps, hard cap 120 s) instead of a fixed 30 s, so slow-settling setups are analyzed correctly
  - Settle delays are measured from the actual GuidingDithered event and require 3 consecutive stable frames (debounce against transient dips); the old bounded-positive-period detection incl. dummy periods and fallback estimator was removed
  - The reference thresholds valid at collection time are stored per dither series, keeping multi-session persisted data self-consistent; data saved by older versions still loads (analyzed with fallbacks)
  - Diagnostic file `*_positive_periods.txt` replaced by `*_settle_analysis.txt` (per-series settle outcome and time-to-stable per profile)
