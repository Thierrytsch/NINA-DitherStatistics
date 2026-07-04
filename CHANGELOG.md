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