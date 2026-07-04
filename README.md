# NINA Dither Statistics Plugin

A comprehensive plugin for N.I.N.A. (Nighttime Imaging 'N' Astronomy) that provides real-time monitoring, statistical analysis, and quality assessment of dithering patterns during astrophotography sessions.

## Features

### Core Statistics

- **Real-time Dither Monitoring**: Tracks all dither events with timestamps and pixel movements
- **Pixel Drift Visualization**: Interactive scatter plot showing cumulative X/Y pixel movements
- **Settle Time History**: Chart displaying settle times for each dither event
- **Statistical Summary**:
  - Total dither count
  - Average settle time
  - Mean pixel drift (X/Y components)
  - Standard deviation of movements
- **Multi-Session Persistence** (optional): "Keep across sessions" toggle in the Statistics panel restores all statistics — including quality metrics and optimizer data — across NINA restarts, accumulating new dithers on top

## 🔬 Experimental Quality Assessment

⚠️ **EXPERIMENTAL FEATURE**: The Quality Assessment functionality is currently in an experimental stage. The algorithms and metrics are under active development and may produce varying results depending on your dithering strategy. Use these assessments as guidance rather than absolute measurements.

The plugin evaluates dithering pattern quality on the **sub-pixel phases** of the cumulative dither positions — the fractional part of each position on the unit torus, expressed in **main-camera pixels**. Dither offsets arrive from PHD2 in guide-camera pixels and are converted using the pixel scale ratio (see "Pixel Scale Conversion" below). All thresholds were calibrated by Monte-Carlo simulation of random dithering, which is what PHD2 actually produces.

### 1. Centered L₂ Discrepancy (CD)

**Purpose**: Measures global uniformity of the sub-pixel phase distribution

**Algorithm**: The Centered L₂ Discrepancy is a measure from quasi-Monte Carlo theory that quantifies how uniformly points are distributed across a unit hypercube. For 2D dithering patterns:

```
CD² = (13/12)² - (2/N)Σ[prodX × prodY] + (1/N²)ΣΣ[prodX × prodY]
```

Lower values indicate better uniformity.

**Interpretation** (Monte-Carlo calibrated for random dithering):

- **< 0.08**: Excellent uniformity (typical for ~80+ random dithers)
- **0.08-0.10**: Very Good uniformity (~50 dithers)
- **0.10-0.13**: Good uniformity (~30 dithers)
- **0.13-0.17**: Acceptable uniformity (~20 dithers)
- **0.17-0.22**: Fair uniformity (~10 dithers)
- **> 0.22**: Poor uniformity (clustered or biased pattern)

**Note**: The dither counts are typical values for healthy random dithering; individual sessions scatter around them. Values below ~0.05 would require low-discrepancy (quasi-random) dither sequences, which PHD2 does not produce.

### 2. Drizzle Weight Uniformity (GFM)

**Purpose**: Predicts how evenly the drizzle weights are distributed across output pixels

**Algorithm**: A direct simulation of the drizzle geometry. For each drizzle factor (1×, 2×, 3×) the unit input pixel is divided into scale × scale output cells. Each exposure deposits a square "drop" of side `pixfrac` (input pixels) at its measured sub-pixel phase (with toroidal wrap-around); the accumulated overlap area per cell is that cell's drizzle weight. The metric is:

```
GFM = min(weight) / mean(weight)
```

- **1.0** = perfectly even coverage; noise is uniform across all output pixels
- **0.0** = at least one output pixel receives no flux at all (a real gap)

Noise per output pixel scales with 1/√weight, so a GFM of e.g. 0.8 means the worst output pixel has ~12% more noise than average.

**Targets** (pixfrac 0.6, random dithering):

- **1× Drizzle**: Target ≥95% — always met (no upsampling, no gaps possible)
- **2× Drizzle**: Target ≥85% — typically reached with ~30 dithers
- **3× Drizzle**: Target ≥85% — typically reached with ~80+ dithers

Note: at pixfrac 0.6, coverage is not the limiting factor for 3× drizzle — the real requirement is total integration time for SNR (the output pixel area is 9× smaller). The recommendation text points this out when the 3× target is met.

The `pixfrac` used in the simulation is configurable in the quality panel (default 0.6). Smaller pixfrac values make gaps more likely and require more dithers.

### 3. Drift Ratio (Walking-Noise Indicator)

**Purpose**: Detects one-directional drift of the dither pattern, the cause of "walking noise" in stacked images

**Algorithm**:

```
Drift Ratio = |net displacement| / total path length
```

- **0** = the pattern keeps returning near its origin (good)
- **1** = every dither step moves in the same direction (walking noise risk)

A warning is issued above 0.6 (with ≥8 dithers). The panel also reports the **pattern spread** (bounding-box diagonal in pixels); a very small spread means hot pixels land on nearly the same sensor position in every frame, defeating outlier rejection during stacking.

### Combined Quality Score

The overall quality score (0-1, higher is better) combines three normalized components:

```
Combined Score = 0.30×GFM-score + 0.45×CD-score + 0.25×NNI-score − 0.25/√N
```

with the quantile-calibrated transforms `CD-score = (0.30 - CD)/0.26`, `GFM-score = (mean(GFM2×, GFM3×) - 0.70)/0.25`, `NNI-score = (NNI - 0.4)/0.7` (each clamped to [0,1]). The CD enters the score only once — it is not baked into the GFM.

The `0.25/√N` term is a **small-sample confidence margin**: individual sessions scatter considerably at low dither counts, and without the margin a lucky 50-dither session would reach "Excellent" about a third of the time. With it, top ratings require accumulated evidence, not luck (P(Excellent) at 50 random dithers ≈ 10%).

**Quality Ratings** (typical median dither counts for healthy random dithering):

- **Excellent (≥0.85)**: ~120+ dithers
- **Very Good (≥0.75)**: ~80 dithers
- **Good (≥0.65)**: ~50 dithers
- **Acceptable (≥0.55)**: ~30 dithers
- **Fair (≥0.45)**: ~20 dithers
- **Poor (<0.45)**: insufficient or clustered pattern

The rating describes **pattern quality**; whether the coverage is sufficient for a given drizzle factor is reported separately in the recommendation text.

### Supplementary Metrics

**Nearest Neighbor Index (NNI)**: Compares the mean nearest-neighbor distance of the sub-pixel phases to the expected distance for a random distribution:

```
NNI = observed_mean_NN / expected_mean_NN
```

**Interpretation**:
- **NNI > 1.5**: Excellent (almost regular grid)
- **NNI > 1.2**: Good (quasi-random distribution)
- **NNI ≈ 1.0**: Acceptable (random distribution, fine for drizzle!)
- **NNI < 0.9**: Fair (some clustering)
- **NNI < 0.7**: Poor (significant clustering)

### Pixel Scale Conversion

Drizzle happens on the **main imaging sensor**, but PHD2 reports dither offsets in **guide-camera pixels**. With a typical setup (e.g. guider at 3.5″/px, main camera at 0.75″/px) one guider pixel corresponds to several main-camera pixels, so the sub-pixel phases must be converted before they mean anything for drizzle.

The plugin derives the conversion factor automatically and re-evaluates it on **every calculation** (each dither, Recalc, and NINA profile change), so per-profile equipment differences are picked up:
- Guider scale from NINA's guider info (pushed while the guider is connected in NINA; primary source) or from PHD2's `get_pixel_scale` API (secondary)
- Main-camera scale from the active NINA profile: `206.265 × pixel size [µm] / focal length [mm]`

The effective ratio and its source (`auto/NINA`, `auto/PHD2`, `manual`, `fallback`) are shown in the quality panel. If the automatic derivation fails (guider not connected in NINA, focal length or camera pixel size missing in the profile options), the ratio falls back to 1.0 and the panel says what to fix; you can also enter a manual override (`px ratio`, 0 = automatic).

## 🔧 Experimental Dither Settings Optimizer

⚠️ **EXPERIMENTAL FEATURE**: The Dither Settings Optimizer analyzes post-dither guiding data to recommend optimal settle parameters. Results depend on your guiding setup and conditions. Use these recommendations as a starting point for tuning.

After each dither event, the plugin collects guiding data and analyzes "positive periods" — time intervals where guiding is stable (PairRMS below threshold). From this data, it calculates recommended values for two key PHD2 dither settings:

### Settle Pixel Tolerance

**Algorithm**: Based on running RMS statistics across all analyzed dither events:

```
Settle Pixel Tolerance = Running RMS + multiplier × RMS Standard Deviation
```

Three profiles with different multipliers:
- **Quality (1.5σ)**: Tight tolerance — waits for excellent guiding stability before resuming. Best image quality, longer settle times.
- **Balanced (2.0σ)**: Moderate tolerance — good balance between quality and speed. Recommended for most setups.
- **Performance (3.0σ)**: Relaxed tolerance — resumes imaging quickly. Maximizes imaging time, slight quality trade-off.

### Minimum Settle Time

**Algorithm**: Derived from the time it takes guiding to first become stable after each dither:

1. For each dither event, identify the first "positive period" (stable guiding interval)
2. Calculate the elapsed time from dither start to first stable guiding
3. Take the median across all dither events
4. Round up to the next full guide exposure interval

The three profiles use the same σ multipliers for the stability threshold, resulting in:
- **Quality (strict)**: Requires tighter stability → longer minimum settle time
- **Balanced (normal)**: Moderate stability requirement
- **Performance (fast)**: Accepts earlier stability → shorter minimum settle time

### Requirements

- Minimum **3 dither events** before recommendations appear
- PHD2 must be connected and guiding
- Recommendations improve in accuracy with more dither events
- Panel can be toggled on/off independently of Quality Assessment

### Display

The optimizer panel shows three profile columns (Quality / Balanced / Performance), each displaying:
- **Settle Pixel Tolerance** (in pixels)
- **Minimum Settle Time** (in seconds, rounded to guide exposure)
- Footer with number of analyzed events, current RMS, and guide exposure

## Quality Assessment Display

The quality metrics panel appears automatically after collecting at least 4 dither positions and includes:

- **Overall Quality Badge**: Color-coded rating (Green/Yellow/Orange/Red)
- **Primary Metrics**: CD, Drift Ratio, and NNI with individual assessments
- **Drizzle Coverage**: Weight-uniformity percentages for 1×, 2×, and 3× drizzle with targets
- **Recommendation**: Actionable advice, coverage sufficiency per drizzle factor, and warnings (clustering, drift, small pattern spread)
- **Metric Settings**: Drizzle `pixfrac` and guider→main-camera pixel scale ratio (automatic with manual override)
- **Export Function**: Save detailed report to text file
- **Recalculate Button**: Manually refresh metrics after pattern changes

## Important Limitations

⚠️ **The experimental quality assessment has several limitations**:

1. **Pattern Dependency**: Thresholds are calibrated for random dithering (what PHD2 produces). Systematic patterns (spiral, grid) may score differently but still be effective.

2. **Sample Size**: Minimum 4 dithers required; assessments become reliable with 20+ positions. At low counts, individual sessions scatter noticeably around the typical values.

3. **Commanded vs. Actual Position**: Metrics are computed from the dither offsets PHD2 commanded, not from the actual pointing of each exposure. Settle residuals and drift between dithers slightly randomize the true sub-pixel phases (usually in your favor).

4. **Drizzle GFM Accuracy**: The weight-uniformity simulation models the drizzle drop geometry but doesn't account for:
   - PSF effects and seeing conditions
   - Processing pipeline specifics (PixInsight vs. Siril)
   - Image registration accuracy

5. **Pixel Scale Ratio**: The conversion from guider to main-camera pixels depends on PHD2's calibration and correct profile data (pixel size, focal length). Check the ratio shown in the panel; set it manually if the automatic value is wrong.

6. **No Absolute Truth**: These metrics provide relative quality indicators, not absolute performance guarantees. Real-world imaging results depend on numerous factors beyond dither pattern quality including seeing conditions, tracking accuracy, optical quality, and processing techniques.

7. **Under Development**: Algorithms and thresholds are being refined based on user feedback and testing.

## Installation

### From NINA Plugin Manager (Recommended)

1. Open N.I.N.A.
2. Go to: **Options** → **Plugins** → **Available**
3. Find **"Dither Statistics"**
4. Click **"Install"**
5. Restart N.I.N.A.
6. Go to: **Imaging Tab** → **Panel Selector** → Activate "Dither Statistics"

### Manual Installation

1. Download the latest release from [Releases](https://github.com/Thierrytsch/NINA-DitherStatistics/releases)
2. Close N.I.N.A. if running
3. Extract the plugin files to: `%localappdata%\NINA\Plugins\3.0.0\DitherStatistics\`
4. Restart N.I.N.A.
5. Navigate to: **Options** → **Plugins** → **Dither Statistics** to enable
6. Restart N.I.N.A. again
7. Go to: **Imaging Tab** → **Panel Selector** → Activate "Dither Statistics"

## Usage

### Basic Operation

1. **Start Imaging Session**: Begin your imaging session in N.I.N.A. with dithering enabled
2. **Monitor in Real-Time**: Open the Dither Statistics panel to see live updates
3. **Review Statistics**: Check pixel drift patterns and settle times after each dither
4. **Quality Assessment**: Once 4+ dithers are collected, review the quality metrics

### Understanding Your Results

**Pixel Drift Chart**: Look for even distribution without clustering. Hovering over points shows X/Y coordinates.

**Settle Time History**: Monitor for consistent settle times. Spikes may indicate guiding issues.

**Quality Metrics**: Use as guidance for assessing if your dither strategy is effective for your intended processing workflow.

**Typical Results by Dither Count** (healthy random dithering, Monte-Carlo means; individual sessions scatter around these values):

| Dithers | Expected CD | Expected GFM 2× | Quality Rating (median) |
|---------|-------------|-----------------|-------------------------|
| ~10     | ≈0.20       | ≈75%            | Poor                    |
| ~20     | ≈0.13       | ≈85%            | Fair                    |
| ~30     | ≈0.11       | ≈86%            | Acceptable              |
| ~50     | ≈0.09       | ≈89%            | Good                    |
| ~80     | ≈0.07       | ≈92%            | Very Good               |
| ~120+   | ≈0.06       | ≈93%            | Excellent               |

### Exporting Data

Click the **"💾 Export"** button to save a comprehensive quality report including:

- All calculated metrics with detailed explanations
- Timestamp and session information
- Individual dither positions and statistics
- Recommendations for pattern improvement

Reports are saved to: `%USERPROFILE%\Documents\NINA\DitherStatistics\`

## Technical Details

### Dependencies

- N.I.N.A. 3.0 or later
- .NET 8.0 Runtime
- Guiding software (PHD2)

### Built With

- C# / .NET 8.0
- WPF
- ScottPlot for visualization
- N.I.N.A. Plugin SDK

### Data Collection

The plugin subscribes to N.I.N.A.'s dither events and collects:

- Dither start/end timestamps
- RMS values before and after dithering
- Pixel offset coordinates (cumulative)
- Settle time duration
- Success/failure status

### Performance Considerations

- Quality metrics calculation: O(n²) complexity for n dither positions
- Nearest-neighbor analysis: O(n²) using toroidal distance
- Updates are computed asynchronously to avoid UI blocking
- Recommended for sessions with up to 500 dither events
- Memory footprint: < 2 MB for typical sessions

## Troubleshooting

### Quality Metrics Not Appearing

- Ensure at least 4 dither events have been recorded
- Check that dithering is enabled in your sequence
- Verify guiding software is properly connected and sending dither events

### Unexpected Quality Scores

- Remember this is an experimental feature; scores of individual sessions scatter noticeably at low dither counts
- Very small dither amplitudes cluster the sub-pixel phases and score Poor — that is intentional (see Drift Ratio and pattern spread warnings)
- Check the effective pixel scale ratio shown in the panel: if it is 1.00 despite guider and main camera having very different scales, the automatic derivation failed — set the ratio manually
- "Good" is typically reached around 50 random dithers, "Excellent" around 120; the score includes a 0.25/√N confidence margin so few-dither sessions cannot reach top ratings by luck

### Why is the 1× coverage always 100%?

At 1× drizzle there is one output cell per input pixel and no upsampling — every exposure contributes flux to it, so gaps cannot occur. The coverage question only becomes interesting at 2× and 3×, where the output grid is finer than the drop footprint:
- **2× Drizzle**: 4 output cells per input pixel → target ≥85%, typically ~30 dithers
- **3× Drizzle**: 9 output cells per input pixel → target ≥85%, typically ~80+ dithers

### Dither Settings Optimizer Not Showing Recommendations

- Ensure at least 3 dither events have been recorded
- Verify the Dither Settings Optimizer toggle is enabled
- Check that PHD2 is connected and guiding during dither events
- With very short exposures (DitherAfterExposures=1), recommendations update after each dither rather than waiting for the 30-second collection period

### No Dither Events Detected

- Verify guiding software connection in N.I.N.A.
- Check N.I.N.A. logs: `%LOCALAPPDATA%\NINA\Logs\`
- Ensure dither instruction is in your sequence
- Confirm guiding is active when dithering occurs

## Contributing

This is an experimental plugin under active development. Feedback and suggestions are welcome:

- Report issues via [GitHub Issues](https://github.com/Thierrytsch/NINA-DitherStatistics/issues)
- Share your dither patterns and quality scores for algorithm improvement
- Suggest additional metrics or improvements to existing calculations

## Changelog

### Version 1.5.0 (Current - Multiple Statistics Profiles, Reworked Quality Assessment)

- ✨ NEW: Multiple statistics profiles - keep separate statistics per target or telescope (toggle in the Statistics panel)
- 🔬 Reworked quality assessment (fixes several calculation errors):
  - Fixed sub-pixel phase wrapping for negative coordinates (previously mirrored instead of wrapped, distorting all metrics)
  - Gap-Fill metric replaced by a real drizzle weight simulation (min/mean weight per output pixel); targets recalibrated
  - Combined score no longer double-counts the discrepancy penalty; thresholds quantile-recalibrated by Monte-Carlo simulation (~50 random dithers rate "Good", ~80 "Very Good", ~120+ "Excellent") with a 0.25/√N confidence margin so lucky few-dither sessions cannot reach top ratings
  - Removed the redundant Voronoi CV metric (duplicated NNI information)
  - NEW Drift Ratio metric warns about one-directional patterns (walking noise) and too-small pattern spread
  - NEW automatic guider→main-camera pixel scale conversion (PHD2 `get_pixel_scale` + NINA profile) with manual override
  - Drizzle pixfrac used by the simulation is configurable (default 0.6)

### Version 1.4.0 (Multi-Session Statistics Persistence)

- ✨ NEW: Optional multi-session statistics persistence
- "Keep across sessions" toggle in the Statistics panel (default: off), toggle state survives restarts
- With the toggle ON, all statistics are restored on startup exactly as they were when NINA was closed: charts, statistical summary, quality metrics, optimizer data and recommendation
- Subsequent dithers accumulate on top of the restored state, as if the session had never been interrupted
- Data is stored in `%LOCALAPPDATA%\NINA\DitherStatistics\statistics_data.json` and updated after every dither, on Clear Data and on shutdown
- Clear Data now also resets the Dither Settings Optimizer data and recommendation

### Version 1.3.0 (Dither Settings Optimizer)

- ✨ NEW: Experimental Dither Settings Optimizer
- **Three profiles**: Quality (1.5σ), Balanced (2.0σ), Performance (3.0σ)
- Recommends optimal **Settle Pixel Tolerance** based on running RMS and standard deviation
- Recommends optimal **Minimum Settle Time** based on time-to-first-stable guiding analysis
- Post-dither guiding data collection with positive period detection
- Auto-updates after each dither event (minimum 3 events required)
- Toggle-able panel with persistent settings
- Thread-safe data collection supporting rapid dithering scenarios

### Version 1.2.0 (STRICT Grading, N.I.N.A 3.2 support)

- ✨ Updated quality assessment with STRICT grading scale
- **CD Scale**: Recalibrated to encourage 30+ dithers for "Good", 50+ for "Excellent"
  - < 0.05: Excellent (was < 0.15)
  - < 0.20: Good (was < 0.25)
- **GFM Targets**: CORRECTED to reflect physical reality
  - 1× Drizzle: Target ≥98% (was incorrectly >85%)
  - 2× Drizzle: Target ≥95% (was incorrectly >70%)
  - 3× Drizzle: Target ≥90% (was incorrectly >55%)
- **Combined Score**: Adjusted weights (CD: 35%, GFM: 45%, Voronoi: 20%)
- Improved documentation with expected values by dither count
- Added comprehensive grading scale explanations
- Support for N.I.N.A 3.2

### Version 1.1.0

- ✨ Added experimental Quality Assessment functionality
- Added Centered L₂ Discrepancy metric
- Added Voronoi Cell CV analysis
- Added Drizzle Gap-Fill predictions (1×, 2×, 3×)
- Added Combined Quality Score with recommendations
- Added quality report export functionality
- Improved tooltip visibility and formatting
- Enhanced chart interactivity

### Version 1.0.0

- Initial release
- Real-time dither monitoring
- Pixel drift visualization
- Settle time history
- Basic statistical summary

## License

This plugin is provided under the Mozilla Public License 2.0.

## Acknowledgments

- N.I.N.A. development team for the excellent plugin API
- PHD2 team for robust guiding integration
- Discrepancy theory research by Harald Niederreiter and Fred J. Hickernell
- Spatial statistics algorithms by Peter J. Clark and Francis C. Evans

## Disclaimer

**THE EXPERIMENTAL QUALITY ASSESSMENT FEATURES ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND.** The metrics and recommendations are for guidance only and should not be considered absolute measurements of dithering effectiveness. Real-world imaging results depend on numerous factors beyond dither pattern quality including seeing conditions, tracking accuracy, optical quality, and processing techniques.

The grading scale is quantile-calibrated for random dithering: "Good" (typically ~50 dithers) already represents high-quality amateur astrophotography results; "Excellent" typically requires ~120+ dithers.

---

**Plugin Version**: 1.5.0 (Multiple Statistics Profiles, Reworked Quality Assessment)
**N.I.N.A. Compatibility**: 3.0+  
**Author**: Thierry Tschanz  
**Repository**: [NINA-DitherStatistics](https://github.com/Thierrytsch/NINA-DitherStatistics)