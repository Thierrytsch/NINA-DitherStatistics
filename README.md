NINA Dither Statistics Plugin

A comprehensive plugin for N.I.N.A. (Nighttime Imaging 'N' Astronomy) that provides real-time monitoring, statistical analysis, and quality assessment of dithering patterns during astrophotography sessions.
Features
Core Statistics

    Real-time Dither Monitoring: Tracks all dither events with timestamps and pixel movements
    Pixel Drift Visualization: Interactive scatter plot showing cumulative X/Y pixel movements
    Settle Time History: Chart displaying settle times for each dither event
    Statistical Summary:
        Total dither count
        Average settle time
        Mean pixel drift (X/Y components)
        Standard deviation of movements

🔬 Experimental Quality Assessment

⚠️ EXPERIMENTAL FEATURE: The Quality Assessment functionality is currently in an experimental stage. The algorithms and metrics are under active development and may produce varying results depending on your dithering strategy. Use these assessments as guidance rather than absolute measurements.

The plugin includes an advanced mathematical analysis engine that evaluates dithering pattern quality using three primary metrics derived from spatial statistics and discrepancy theory:
1. Centered L₂ Discrepancy (CD)

Purpose: Measures global uniformity of dither point distribution

Algorithm: The Centered L₂ Discrepancy is a measure from quasi-Monte Carlo theory that quantifies how uniformly points are distributed across a unit hypercube. For 2D dithering patterns:

CD² = (12/n)² - 2^(d+1) * Σ(Π(1 + |x_i - 0.5| - |x_i - 0.5|²)) + 2^d * ΣΣ(Π(1 + |x_i - x_j|/2 - |x_i - x_j|²/2))

Where:

    n = number of dither positions
    d = dimension (2 for X/Y coordinates)
    Points are normalized to [0,1]² space
    Lower values indicate better uniformity

Interpretation:

    < 0.15: Excellent uniformity (quasi-random/low-discrepancy sequences)
    0.15-0.25: Good uniformity (well-distributed random patterns)
    0.25-0.40: Moderate uniformity (acceptable for most imaging)
    > 0.40: Poor uniformity (clustered or biased patterns)

2. Voronoi Cell Coefficient of Variation (CV)

Purpose: Evaluates local spatial distribution and clustering

Algorithm: This metric analyzes the Voronoi tessellation of dither points - a partition of the plane into regions where each region contains all points closest to a particular dither position.

1. Construct Voronoi diagram from dither positions
2. Calculate area of each Voronoi cell
3. Compute CV = (Standard Deviation / Mean) of cell areas

The Fortune's algorithm is used for efficient Voronoi diagram construction with O(n log n) complexity.

Interpretation:

    < 0.25: Very uniform local coverage (low clustering)
    0.25-0.40: Good local distribution
    0.40-0.60: Moderate uniformity (some clustering present)
    > 0.60: Significant clustering or gaps

3. Drizzle Gap-Fill Metrics (GFM)

Purpose: Predicts sub-pixel coverage effectiveness for drizzle stacking

⚠️ Note: This metric is particularly experimental and may not accurately reflect real-world drizzle performance. Current implementation is being refined.

Algorithm: The GFM simulates how well dither positions will fill the sub-pixel grid at different drizzle factors:

For each drizzle factor (1×, 2×, 3×):
  1. Create virtual sub-pixel grid (factor × factor cells per pixel)
  2. For each dither position (dx, dy):
     - Calculate which sub-pixel cells are covered
     - Account for pixfrac (pixel drop fraction, typically 0.6)
  3. GFM = (Number of unique covered cells) / (Total sub-pixel cells)

Interpretation:

    1× Drizzle: Should be easily achievable with most patterns (target: >85%)
    2× Drizzle: Requires well-distributed points (target: >70%)
    3× Drizzle: Very demanding, needs excellent distribution (target: >55%)

Higher drizzle factors (2× = 4 sub-pixels, 3× = 9 sub-pixels per pixel) require exponentially more dithers for good coverage.
Combined Quality Score

The plugin computes an overall quality score (0-1 scale, higher is better) by weighting the individual metrics:

Combined Score = w1*(1 - normalized_CD) + w2*(1 - Voronoi_CV) + w3*avg(GFM_1x, GFM_2x, GFM_3x)

Default weights: w1=0.35, w2=0.35, w3=0.30

Quality Ratings:

    Excellent (≥0.85): Suitable for 3× drizzle and advanced processing
    Good (≥0.75): Recommended for 2× drizzle
    Acceptable (≥0.60): Adequate for 1× drizzle
    Poor (<0.60): May benefit from more dithers or pattern adjustment

Supplementary Metrics

Nearest Neighbor Index (NNI): Compares the mean nearest-neighbor distance to the expected distance for a random distribution:

NNI = observed_mean_NN / expected_mean_NN

    NNI ≈ 1.0: Random distribution
    NNI < 1.0: Clustered pattern
    NNI > 1.0: Regular/dispersed pattern

Quality Assessment Display

The quality metrics panel appears automatically after collecting at least 4 dither positions and includes:

    Overall Quality Badge: Color-coded rating (Green/Yellow/Orange/Red)
    Primary Metrics: CD, Voronoi CV, and NNI with individual assessments
    Drizzle Coverage: Gap-fill percentages for 1×, 2×, and 3× drizzle
    Recommendation: Actionable advice based on pattern quality
    Export Function: Save detailed report to text file
    Recalculate Button: Manually refresh metrics after pattern changes

Important Limitations

⚠️ The experimental quality assessment has several limitations:

    Pattern Dependency: Metrics are optimized for quasi-random dithering patterns. Systematic patterns (spiral, grid) may score differently but still be effective.
    Sample Size: Minimum 4 dithers required; assessments become more reliable with 15+ positions.
    Drizzle GFM Accuracy: The gap-fill metric provides estimates but doesn't account for:
        Actual photon distribution in sub-pixels
        PSF effects and seeing conditions
        Processing pipeline specifics
        Image registration accuracy
    No Absolute Truth: These metrics provide relative quality indicators, not absolute performance guarantees. Real-world imaging results depend on many factors beyond dither pattern quality.
    Under Development: Algorithms and thresholds are being refined based on user feedback and testing.

Installation

    Download the latest release from the Releases page
    Close N.I.N.A. if running
    Extract the plugin files to: %localappdata%\NINA\Plugins\3.0.0\DitherStatistics\
    Restart N.I.N.A.
    Navigate to: Options → Plugins → Dither Statistics to enable
    Restart N.I.N.A. again
    Go to: Imaging Tab → Panel Selector → Activate "Dither Statistics"

Usage
Basic Operation

    Start Imaging Session: Begin your imaging session in N.I.N.A. with dithering enabled
    Monitor in Real-Time: Open the Dither Statistics panel to see live updates
    Review Statistics: Check pixel drift patterns and settle times after each dither
    Quality Assessment: Once 4+ dithers are collected, review the quality metrics

Understanding Your Results

    Pixel Drift Chart: Look for even distribution without clustering. Hovering over points shows X/Y coordinates.
    Settle Time History: Monitor for consistent settle times. Spikes may indicate guiding issues.
    Quality Metrics: Use as guidance for assessing if your dither strategy is effective for your intended processing workflow.

Exporting Data

Click the "💾 Export" button to save a comprehensive quality report including:

    All calculated metrics with detailed explanations
    Timestamp and session information
    Individual dither positions and statistics
    Recommendations for pattern improvement

Reports are saved to: %USERPROFILE%\Documents\NINA\DitherStatistics\
Technical Details
Dependencies

    N.I.N.A. 3.0 or later
    .NET 8.0 Runtime
    Guiding software (PHD2 or N.I.N.A. Direct Guider)

Built With

    C# / .NET 8.0
    WPF
    LiveCharts for visualization
    N.I.N.A. Plugin SDK

Data Collection

The plugin subscribes to N.I.N.A.'s dither events and collects:

    Dither start/end timestamps
    RMS values before and after dithering
    Pixel offset coordinates (cumulative)
    Settle time duration
    Success/failure status

Performance Considerations

    Quality metrics calculation: O(n²) complexity for n dither positions
    Voronoi diagram construction: O(n log n) using Fortune's algorithm
    Updates are computed asynchronously to avoid UI blocking
    Recommended for sessions with up to 500 dither events

Troubleshooting
Quality Metrics Not Appearing

    Ensure at least 4 dither events have been recorded
    Check that dithering is enabled in your sequence
    Verify guiding software is properly connected and sending dither events

Unexpected Quality Scores

    Remember this is an experimental feature
    Scores may vary significantly with different dither strategies
    Grid or spiral patterns may score lower despite being effective
    Very small dither amplitudes (<1 pixel) may affect scoring

No Dither Events Detected

    Verify guiding software connection in N.I.N.A.
    Check N.I.N.A. logs: %LOCALAPPDATA%\NINA\Logs\
    Ensure dither instruction is in your sequence
    Confirm guiding is active when dithering occurs

Contributing

This is an experimental plugin under active development. Feedback and suggestions are welcome:

    Report issues via GitHub Issues
    Share your dither patterns and quality scores for algorithm improvement
    Suggest additional metrics or improvements to existing calculations

Changelog
Version 1.1.0 (Current)

    ✨ Added experimental Quality Assessment functionality
    Added Centered L₂ Discrepancy metric
    Added Voronoi Cell CV analysis
    Added Drizzle Gap-Fill predictions (1×, 2×, 3×)
    Added Combined Quality Score with recommendations
    Added quality report export functionality
    Improved tooltip visibility and formatting
    Enhanced chart interactivity

Version 1.0.0

    Initial release
    Real-time dither monitoring
    Pixel drift visualization
    Settle time history
    Basic statistical summary

License

This plugin is provided under the Mozilla Public License 2.0.
Acknowledgments

    N.I.N.A. development team for the excellent plugin API
    PHD2 team for robust guiding integration
    Discrepancy theory research by Harald Niederreiter
    Voronoi diagram algorithms by Steven Fortune

Disclaimer

THE EXPERIMENTAL QUALITY ASSESSMENT FEATURES ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND. The metrics and recommendations are for guidance only and should not be considered absolute measurements of dithering effectiveness. Real-world imaging results depend on numerous factors beyond dither pattern quality including seeing conditions, tracking accuracy, optical quality, and processing techniques.

Plugin Version: 1.1.0 (Experimental Quality Assessment)
N.I.N.A. Compatibility: 3.0+
Author: Thierry Tschanz
Repository: NINA-DitherStatistics
NINA Dither Statistics Plugin

A comprehensive plugin for N.I.N.A. (Nighttime Imaging 'N' Astronomy) that provides real-time monitoring, statistical analysis, and quality assessment of dithering patterns during astrophotography sessions.
Features
Core Statistics

    Real-time Dither Monitoring: Tracks all dither events with timestamps and pixel movements
    Pixel Drift Visualization: Interactive scatter plot showing cumulative X/Y pixel movements
    Settle Time History: Chart displaying settle times for each dither event
    Statistical Summary:
        Total dither count
        Average settle time
        Mean pixel drift (X/Y components)
        Standard deviation of movements

🔬 Experimental Quality Assessment

⚠️ EXPERIMENTAL FEATURE: The Quality Assessment functionality is currently in an experimental stage. The algorithms and metrics are under active development and may produce varying results depending on your dithering strategy. Use these assessments as guidance rather than absolute measurements.

The plugin includes an advanced mathematical analysis engine that evaluates dithering pattern quality using three primary metrics derived from spatial statistics and discrepancy theory:
1. Centered L₂ Discrepancy (CD)

Purpose: Measures global uniformity of dither point distribution

Algorithm: The Centered L₂ Discrepancy is a measure from quasi-Monte Carlo theory that quantifies how uniformly points are distributed across a unit hypercube. For 2D dithering patterns:

CD² = (12/n)² - 2^(d+1) * Σ(Π(1 + |x_i - 0.5| - |x_i - 0.5|²)) + 2^d * ΣΣ(Π(1 + |x_i - x_j|/2 - |x_i - x_j|²/2))

Where:

    n = number of dither positions
    d = dimension (2 for X/Y coordinates)
    Points are normalized to [0,1]² space
    Lower values indicate better uniformity

Interpretation:

    < 0.15: Excellent uniformity (quasi-random/low-discrepancy sequences)
    0.15-0.25: Good uniformity (well-distributed random patterns)
    0.25-0.40: Moderate uniformity (acceptable for most imaging)
    > 0.40: Poor uniformity (clustered or biased patterns)

2. Voronoi Cell Coefficient of Variation (CV)

Purpose: Evaluates local spatial distribution and clustering

Algorithm: This metric analyzes the Voronoi tessellation of dither points - a partition of the plane into regions where each region contains all points closest to a particular dither position.

1. Construct Voronoi diagram from dither positions
2. Calculate area of each Voronoi cell
3. Compute CV = (Standard Deviation / Mean) of cell areas

The Fortune's algorithm is used for efficient Voronoi diagram construction with O(n log n) complexity.

Interpretation:

    < 0.25: Very uniform local coverage (low clustering)
    0.25-0.40: Good local distribution
    0.40-0.60: Moderate uniformity (some clustering present)
    > 0.60: Significant clustering or gaps

3. Drizzle Gap-Fill Metrics (GFM)

Purpose: Predicts sub-pixel coverage effectiveness for drizzle stacking

⚠️ Note: This metric is particularly experimental and may not accurately reflect real-world drizzle performance. Current implementation is being refined.

Algorithm: The GFM simulates how well dither positions will fill the sub-pixel grid at different drizzle factors:

For each drizzle factor (1×, 2×, 3×):
  1. Create virtual sub-pixel grid (factor × factor cells per pixel)
  2. For each dither position (dx, dy):
     - Calculate which sub-pixel cells are covered
     - Account for pixfrac (pixel drop fraction, typically 0.6)
  3. GFM = (Number of unique covered cells) / (Total sub-pixel cells)

Interpretation:

    1× Drizzle: Should be easily achievable with most patterns (target: >85%)
    2× Drizzle: Requires well-distributed points (target: >70%)
    3× Drizzle: Very demanding, needs excellent distribution (target: >55%)

Higher drizzle factors (2× = 4 sub-pixels, 3× = 9 sub-pixels per pixel) require exponentially more dithers for good coverage.
Combined Quality Score

The plugin computes an overall quality score (0-1 scale, higher is better) by weighting the individual metrics:

Combined Score = w1*(1 - normalized_CD) + w2*(1 - Voronoi_CV) + w3*avg(GFM_1x, GFM_2x, GFM_3x)

Default weights: w1=0.35, w2=0.35, w3=0.30

Quality Ratings:

    Excellent (≥0.85): Suitable for 3× drizzle and advanced processing
    Good (≥0.75): Recommended for 2× drizzle
    Acceptable (≥0.60): Adequate for 1× drizzle
    Poor (<0.60): May benefit from more dithers or pattern adjustment

Supplementary Metrics

Nearest Neighbor Index (NNI): Compares the mean nearest-neighbor distance to the expected distance for a random distribution:

NNI = observed_mean_NN / expected_mean_NN

    NNI ≈ 1.0: Random distribution
    NNI < 1.0: Clustered pattern
    NNI > 1.0: Regular/dispersed pattern

Quality Assessment Display

The quality metrics panel appears automatically after collecting at least 4 dither positions and includes:

    Overall Quality Badge: Color-coded rating (Green/Yellow/Orange/Red)
    Primary Metrics: CD, Voronoi CV, and NNI with individual assessments
    Drizzle Coverage: Gap-fill percentages for 1×, 2×, and 3× drizzle
    Recommendation: Actionable advice based on pattern quality
    Export Function: Save detailed report to text file
    Recalculate Button: Manually refresh metrics after pattern changes

Important Limitations

⚠️ The experimental quality assessment has several limitations:

    Pattern Dependency: Metrics are optimized for quasi-random dithering patterns. Systematic patterns (spiral, grid) may score differently but still be effective.
    Sample Size: Minimum 4 dithers required; assessments become more reliable with 15+ positions.
    Drizzle GFM Accuracy: The gap-fill metric provides estimates but doesn't account for:
        Actual photon distribution in sub-pixels
        PSF effects and seeing conditions
        Processing pipeline specifics
        Image registration accuracy
    No Absolute Truth: These metrics provide relative quality indicators, not absolute performance guarantees. Real-world imaging results depend on many factors beyond dither pattern quality.
    Under Development: Algorithms and thresholds are being refined based on user feedback and testing.

Installation

    From NINA Plugin Manager (Recommended)
        1. Open N.I.N.A.
        2. Go to: **Options** → **Plugins** → **Available**
        3. Find **"Dither Statistics"**
        4. Click **"Install"**
        5. Restart N.I.N.A.
        6. Go to: **Imaging Tab** → **Panel Selector** → Activate "Dither Statistics"

    Manual Installation
        Download the latest release from [Releases](https://github.com/Thierrytsch/NINA-DitherStatistics/releases)...

Usage
Basic Operation

    Start Imaging Session: Begin your imaging session in N.I.N.A. with dithering enabled
    Monitor in Real-Time: Open the Dither Statistics panel to see live updates
    Review Statistics: Check pixel drift patterns and settle times after each dither
    Quality Assessment: Once 4+ dithers are collected, review the quality metrics

Understanding Your Results

    Pixel Drift Chart: Look for even distribution without clustering. Hovering over points shows X/Y coordinates.
    Settle Time History: Monitor for consistent settle times. Spikes may indicate guiding issues.
    Quality Metrics: Use as guidance for assessing if your dither strategy is effective for your intended processing workflow.

Exporting Data

Click the "💾 Export" button to save a comprehensive quality report including:

    All calculated metrics with detailed explanations
    Timestamp and session information
    Individual dither positions and statistics
    Recommendations for pattern improvement

Reports are saved to: %USERPROFILE%\Documents\N.I.N.A\DitherStatistics\
Technical Details
Dependencies

    N.I.N.A. 3.0 or later
    .NET 8.0 Runtime
    Guiding software (PHD2)

Built With

    C# / .NET 8.0
    WPF
    LiveCharts for visualization
    N.I.N.A. Plugin SDK

Data Collection

The plugin subscribes to N.I.N.A.'s dither events and collects:

    Dither start/end timestamps
    RMS values before and after dithering
    Pixel offset coordinates (cumulative)
    Settle time duration
    Success/failure status

Performance Considerations

    Quality metrics calculation: O(n²) complexity for n dither positions
    Voronoi diagram construction: O(n log n) using Fortune's algorithm
    Updates are computed asynchronously to avoid UI blocking
    Recommended for sessions with up to 500 dither events

Troubleshooting
Quality Metrics Not Appearing

    Ensure at least 4 dither events have been recorded
    Check that dithering is enabled in your sequence
    Verify guiding software is properly connected and sending dither events

Unexpected Quality Scores

    Remember this is an experimental feature
    Scores may vary significantly with different dither strategies
    Grid or spiral patterns may score lower despite being effective
    Very small dither amplitudes (<1 pixel) may affect scoring

No Dither Events Detected

    Verify guiding software connection in N.I.N.A.
    Check N.I.N.A. logs: %LOCALAPPDATA%\NINA\Logs\
    Ensure dither instruction is in your sequence
    Confirm guiding is active when dithering occurs

Contributing

This is an experimental plugin under active development. Feedback and suggestions are welcome:

    Report issues via GitHub Issues
    Share your dither patterns and quality scores for algorithm improvement
    Suggest additional metrics or improvements to existing calculations

Changelog
Version 1.1.0 (Current)

    ✨ Added experimental Quality Assessment functionality
    Added Centered L₂ Discrepancy metric
    Added Voronoi Cell CV analysis
    Added Drizzle Gap-Fill predictions (1×, 2×, 3×)
    Added Combined Quality Score with recommendations
    Added quality report export functionality
    Improved tooltip visibility and formatting
    Enhanced chart interactivity

Version 1.0.0

    Initial release
    Real-time dither monitoring
    Pixel drift visualization
    Settle time history
    Basic statistical summary

License

This plugin is provided under the Mozilla Public License 2.0.
Acknowledgments

    N.I.N.A. development team for the excellent plugin API
    PHD2 team for robust guiding integration
    Discrepancy theory research by Harald Niederreiter
    Voronoi diagram algorithms by Steven Fortune

Disclaimer

THE EXPERIMENTAL QUALITY ASSESSMENT FEATURES ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND. The metrics and recommendations are for guidance only and should not be considered absolute measurements of dithering effectiveness. Real-world imaging results depend on numerous factors beyond dither pattern quality including seeing conditions, tracking accuracy, optical quality, and processing techniques.

Plugin Version: 1.1.0 (Experimental Quality Assessment)
N.I.N.A. Compatibility: 3.0+
Author: Thierry Tschanz
Repository: NINA-DitherStatistics
