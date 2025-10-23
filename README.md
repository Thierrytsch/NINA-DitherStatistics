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

## 🔬 Experimental Quality Assessment

⚠️ **EXPERIMENTAL FEATURE**: The Quality Assessment functionality is currently in an experimental stage. The algorithms and metrics are under active development and may produce varying results depending on your dithering strategy. Use these assessments as guidance rather than absolute measurements.

The plugin includes an advanced mathematical analysis engine that evaluates dithering pattern quality using three primary metrics derived from spatial statistics and discrepancy theory:

### 1. Centered L₂ Discrepancy (CD)

**Purpose**: Measures global uniformity of dither point distribution

**Algorithm**: The Centered L₂ Discrepancy is a measure from quasi-Monte Carlo theory that quantifies how uniformly points are distributed across a unit hypercube. For 2D dithering patterns:

```
CD² = (13/12)² - (2/N)Σ[prodX × prodY] + (1/N²)ΣΣ[prodX × prodY]
```

Where:
- `N` = number of dither positions
- `d` = dimension (2 for X/Y coordinates)
- Points are normalized to [0,1]² space (sub-pixel analysis)
- Lower values indicate better uniformity

**Interpretation (STRICT grading scale)**:

- **< 0.05**: Excellent uniformity (50-100+ well-distributed dithers)
- **0.05-0.10**: Very Good uniformity (40-50 dithers)
- **0.10-0.20**: Good uniformity (25-35 dithers)
- **0.20-0.35**: Acceptable uniformity (15-25 dithers)
- **0.35-0.50**: Fair uniformity (some clustering present)
- **> 0.50**: Poor uniformity (heavily clustered or biased patterns)

**Note**: This strict grading scale encourages collecting 30+ dithers for 'Good' and 50+ dithers for 'Excellent' ratings, differentiating between adequate and truly excellent dither patterns.

### 2. Voronoi Cell Coefficient of Variation (CV)

**Purpose**: Evaluates local spatial distribution and clustering

**Algorithm**: This metric analyzes the spatial uniformity using nearest-neighbor distances as an approximation of Voronoi cell area variation:

1. Calculate nearest-neighbor distances for all dither positions (sub-pixel space)
2. Apply toroidal distance metric (periodic boundaries)
3. Compute CV = (Standard Deviation / Mean) of distances

**Interpretation**:

- **< 0.25**: Excellent local uniformity (near-regular distribution)
- **0.25-0.40**: Good local distribution (low clustering)
- **0.40-0.60**: Acceptable uniformity (random-like, OK for drizzle!)
- **0.60-0.80**: Fair uniformity (moderate clustering)
- **> 0.80**: Poor uniformity - significant clustering or gaps

**Note**: Even random distribution (CV ≈ 0.5) is acceptable for drizzle processing.

### 3. Drizzle Gap-Fill Metrics (GFM)

**Purpose**: Predicts sub-pixel coverage effectiveness for drizzle stacking

**Algorithm**: The GFM simulates how well dither positions will fill the sub-pixel grid at different drizzle factors:

For each drizzle factor (1×, 2×, 3×):
1. Calculate effective drop area per dither (pixfrac/scale)²
2. Compute raw coverage: (N × drop_area) / (scale² output pixels)
3. Apply uniformity penalty based on CD value
4. Normalize against scale-specific requirement

**Interpretation (CORRECTED targets)**:

- **1× Drizzle**: Target ≥98% (easy: achievable with 10-15 dithers)
- **2× Drizzle**: Target ≥95% (moderate: requires 25-30 dithers)
- **3× Drizzle**: Target ≥90% (very demanding: needs 80+ dithers)

**Critical Understanding**: Higher drizzle scales are MORE challenging because:
- Scale 2× creates 4× more output pixels than 1×
- Scale 3× creates 9× more output pixels than 1×
- More pixels require exponentially more dithers for equivalent coverage

Therefore, the TARGET percentages are LOWER for higher scales (not higher) because achieving 90% at 3× is already excellent and requires 80+ well-distributed dithers.

### Combined Quality Score

The plugin computes an overall quality score (0-1 scale, higher is better) by weighting the individual metrics:

```
Combined Score = w1×(1 - normalized_CD) + w2×GFM_2x + w3×(1 - Voronoi_CV)
```

**Weights** (calibrated for strict grading):
- w1 = 0.35 (CD - global uniformity)
- w2 = 0.45 (GFM - drizzle coverage, most directly useful)
- w3 = 0.20 (Voronoi CV - local uniformity, supplementary)

**Quality Ratings**:

- **Excellent (≥0.85)**: Professional-grade pattern, suitable for 3× drizzle
- **Good (≥0.75)**: High-quality, recommended for 2× drizzle
- **Acceptable (≥0.60)**: Standard quality, adequate for 1× drizzle
- **Fair (0.40-0.60)**: Suboptimal, consider more dithers
- **Poor (<0.40)**: Insufficient quality, expect artifacts

### Supplementary Metrics

**Nearest Neighbor Index (NNI)**: Compares the mean nearest-neighbor distance to the expected distance for a random distribution:

```
NNI = observed_mean_NN / expected_mean_NN
```

**Interpretation**:
- **NNI > 1.5**: Excellent (almost regular grid)
- **NNI > 1.2**: Good (quasi-random distribution)
- **NNI ≈ 1.0**: Acceptable (random distribution, fine for drizzle!)
- **NNI < 0.8**: Fair (some clustering)
- **NNI < 0.6**: Poor (significant clustering)

## Quality Assessment Display

The quality metrics panel appears automatically after collecting at least 4 dither positions and includes:

- **Overall Quality Badge**: Color-coded rating (Green/Yellow/Orange/Red)
- **Primary Metrics**: CD, Voronoi CV, and NNI with individual assessments
- **Drizzle Coverage**: Gap-fill percentages for 1×, 2×, and 3× drizzle with targets
- **Recommendation**: Actionable advice based on pattern quality
- **Export Function**: Save detailed report to text file
- **Recalculate Button**: Manually refresh metrics after pattern changes

## Important Limitations

⚠️ **The experimental quality assessment has several limitations**:

1. **Pattern Dependency**: Metrics are optimized for quasi-random dithering patterns. Systematic patterns (spiral, grid) may score differently but still be effective.

2. **Sample Size**: Minimum 4 dithers required; assessments become more reliable with 20+ positions. The strict grading scale is calibrated for 30+ dithers.

3. **Drizzle GFM Accuracy**: The gap-fill metric provides estimates but doesn't account for:
   - Actual photon distribution in sub-pixels
   - PSF effects and seeing conditions
   - Processing pipeline specifics (PixInsight vs. Siril)
   - Image registration accuracy

4. **No Absolute Truth**: These metrics provide relative quality indicators, not absolute performance guarantees. Real-world imaging results depend on numerous factors beyond dither pattern quality including seeing conditions, tracking accuracy, optical quality, and processing techniques.

5. **Strict Grading**: The quality scale is intentionally demanding to encourage collecting sufficient dithers for excellent drizzle results. A "Good" rating (30+ dithers) is already high-quality for most amateur work.

6. **Under Development**: Algorithms and thresholds are being refined based on user feedback and testing.

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

**Typical Results by Dither Count** (with good distribution):

| Dithers | Expected CD | Expected GFM 2× | Quality Rating |
|---------|-------------|-----------------|----------------|
| 10-15   | 0.25-0.35   | 88-92%          | Acceptable     |
| 20-25   | 0.18-0.28   | 92-95%          | Good           |
| 30-40   | 0.12-0.22   | 95-97%          | Good to Very Good |
| 50-80   | 0.08-0.15   | 97-99%          | Very Good to Excellent |
| 100+    | 0.04-0.10   | 98-100%         | Excellent      |

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
- LiveCharts for visualization
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

- Remember this is an experimental feature with a STRICT grading scale
- Scores may vary significantly with different dither strategies
- Grid or spiral patterns may score lower despite being effective
- Very small dither amplitudes (<1 pixel) may affect scoring
- "Good" rating already requires 25-35 dithers for optimal results

### Why are my Gap-Fill percentages counterintuitive?

This is normal! The targets are LOWER for higher drizzle scales because:
- **1× Drizzle** has 1 output pixel → easy to fill → Target 98%
- **2× Drizzle** has 4 output pixels → moderate → Target 95%
- **3× Drizzle** has 9 output pixels → very challenging → Target 90%

A 90% coverage at 3× is excellent and requires 80+ well-distributed dithers!

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

### Version 1.2.0 (Current - STRICT Grading, N.I.N.A 3.2 support)

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

The STRICT grading scale is intentionally demanding to encourage excellent dither patterns. A "Good" rating already represents high-quality amateur astrophotography results.

---

**Plugin Version**: 1.2.0 (Strict Grading Scale)  
**N.I.N.A. Compatibility**: 3.0+  
**Author**: Thierry Tschanz  
**Repository**: [NINA-DitherStatistics](https://github.com/Thierrytsch/NINA-DitherStatistics)