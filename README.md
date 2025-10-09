# NINA Dither Statistics Plugin

Real-time visualization and analysis of dithering performance in N.I.N.A. (Nighttime Imaging 'N' Astronomy).

## Features

- **Real-time Charts:**
  - Dither Settle Time tracking
  - X/Y Axis pixel shift visualization
- **Statistics:**
  - Average, Median, Min, Max settle times
  - Standard Deviation
  - Success Rate
- **Live Updates:** Automatically updates during imaging sessions
- **Clean Integration:** Seamless integration into N.I.N.A.'s Imaging Tab

## Installation

1. Download the latest release from [Releases](https://github.com/Thierrytsch/NINA-DitherStatistics/releases)
2. Extract the ZIP file
3. Copy contents to: `%LOCALAPPDATA%\NINA\Plugins\3.0.0\DitherStatistics\`
4. Restart N.I.N.A.
5. Enable plugin in: Options → Plugins → Dither Statistics ✓
6. Restart N.I.N.A. again
7. Go to Imaging Tab → Panel Selector → Activate "Dither Statistics"

## Requirements

- N.I.N.A. 3.0 or higher
- .NET 8.0 Runtime
- Guiding software (PHD2 or N.I.N.A. Direct Guider)

## Usage

1. Connect your guiding equipment
2. Start an imaging sequence with dithering enabled
3. Watch the statistics panel update in real-time

## Development

Built with:
- C# / .NET 8.0
- WPF
- LiveCharts for visualization
- N.I.N.A. Plugin SDK

## License

Mozilla Public License 2.0

## Author

Thierry Tschanz

## Support

For issues or questions, please open an issue on GitHub.