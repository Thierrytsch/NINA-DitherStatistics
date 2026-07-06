using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace DitherStatistics.Plugin.Tests {
    /// <summary>
    /// Roundtrip / default-fallback tests for PluginSettingsStore. Each test gets its own
    /// temp directory so the real %LocalAppData%\NINA\DitherStatistics\ is never touched.
    /// </summary>
    public class PluginSettingsStoreTests : IDisposable {
        private readonly string tempDirectory;
        private readonly PluginSettingsStore store;

        public PluginSettingsStoreTests() {
            tempDirectory = Path.Combine(Path.GetTempPath(), "DitherStatisticsTests_" + Guid.NewGuid());
            store = new PluginSettingsStore(tempDirectory);
        }

        public void Dispose() {
            if (Directory.Exists(tempDirectory)) {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        // ---- ReadBool / WriteBool (settings.txt, optimizer_settings.txt, persistence_settings.txt, multiprofile_settings.txt) ----

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Bool_Roundtrip_ReturnsWrittenValue(bool value) {
            store.WriteBool(PluginSettingsStore.OptimizerFileName, value);

            Assert.Equal(value, store.ReadBool(PluginSettingsStore.OptimizerFileName));
        }

        [Fact]
        public void Bool_WriteContent_IsPlainBoolToString() {
            store.WriteBool(PluginSettingsStore.QualityAssessmentFileName, true);

            var filePath = Path.Combine(tempDirectory, PluginSettingsStore.QualityAssessmentFileName);
            Assert.Equal(bool.TrueString, File.ReadAllText(filePath));
        }

        [Fact]
        public void Bool_MissingFile_ReturnsNull() {
            Assert.Null(store.ReadBool(PluginSettingsStore.PersistenceFileName));
        }

        [Fact]
        public void Bool_CorruptContent_ReturnsNull() {
            Directory.CreateDirectory(tempDirectory);
            File.WriteAllText(Path.Combine(tempDirectory, PluginSettingsStore.MultiProfileFileName), "not-a-bool");

            Assert.Null(store.ReadBool(PluginSettingsStore.MultiProfileFileName));
        }

        // ---- ReadQualityMetricSettings / WriteQualityMetricSettings (quality_settings.txt) ----

        [Fact]
        public void QualityMetricSettings_Roundtrip_ReturnsWrittenValues() {
            store.WriteQualityMetricSettings(0.75, 2.5);

            var result = store.ReadQualityMetricSettings();

            Assert.NotNull(result);
            Assert.Equal(0.75, result.Value.Pixfrac);
            Assert.Equal(2.5, result.Value.ScaleRatioOverride);
        }

        [Fact]
        public void QualityMetricSettings_MissingFile_ReturnsNull() {
            Assert.Null(store.ReadQualityMetricSettings());
        }

        [Fact]
        public void QualityMetricSettings_CorruptContent_SkipsUnparsableLines() {
            Directory.CreateDirectory(tempDirectory);
            File.WriteAllText(
                Path.Combine(tempDirectory, PluginSettingsStore.QualityMetricsFileName),
                "pixfrac=not-a-number\ngarbage-line\nscaleRatioOverride=1.5");

            var result = store.ReadQualityMetricSettings();

            Assert.NotNull(result);
            Assert.Null(result.Value.Pixfrac);
            Assert.Equal(1.5, result.Value.ScaleRatioOverride);
        }

        [Fact]
        public void QualityMetricSettings_WriteContent_UsesInvariantCultureKeyValueLines() {
            store.WriteQualityMetricSettings(0.6, 0.0);

            var filePath = Path.Combine(tempDirectory, PluginSettingsStore.QualityMetricsFileName);
            Assert.Equal("pixfrac=0.6\nscaleRatioOverride=0", File.ReadAllText(filePath));
        }

        // ---- ReadProfileList / WriteProfileList (profiles_list.txt) ----

        [Fact]
        public void ProfileList_Roundtrip_ReturnsSelectedAndNames() {
            store.WriteProfileList("Scope-A", new List<string> { "Default", "Scope-A", "Scope-B" });

            var (selected, names) = store.ReadProfileList("Default");

            Assert.Equal("Scope-A", selected);
            Assert.Equal(new List<string> { "Default", "Scope-A", "Scope-B" }, names);
        }

        [Fact]
        public void ProfileList_MissingFile_ReturnsDefaultSelectedAndEmptyNames() {
            var (selected, names) = store.ReadProfileList("Default");

            Assert.Equal("Default", selected);
            Assert.Empty(names);
        }

        [Fact]
        public void ProfileList_EmptyFile_ReturnsDefaultSelectedAndEmptyNames() {
            Directory.CreateDirectory(tempDirectory);
            File.WriteAllText(Path.Combine(tempDirectory, PluginSettingsStore.ProfileListFileName), "");

            var (selected, names) = store.ReadProfileList("Default");

            Assert.Equal("Default", selected);
            Assert.Empty(names);
        }
    }
}
