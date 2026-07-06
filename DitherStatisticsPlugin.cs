using System.ComponentModel.Composition;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// Main plugin class - Entry point for the NINA plugin system
    /// Migrated from LiveCharts to ScottPlot
    /// Filename: DitherStatisticsPlugin.cs
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class DitherStatisticsPlugin : PluginBase {
        [ImportingConstructor]
        public DitherStatisticsPlugin(IProfileService profileService) {
            // DataTemplates are now loaded in the ViewModel constructor
            // This is necessary because the assembly is not fully initialized during plugin load
        }
    }
}
