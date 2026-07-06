using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace DitherStatistics.Plugin.Tests {
    /// <summary>
    /// Tests for StatisticsProfileService: snapshot roundtrip over the data
    /// files, legacy v1.4 migration (both branches), profile name sanitization
    /// incl. file collisions, persistence off (delete files, keep memory) and
    /// profile deletion. Each test gets its own temp directory so the real
    /// %LocalAppData%\NINA\DitherStatistics\ is never touched.
    /// </summary>
    public class StatisticsProfileServiceTests : IDisposable {
        private readonly string tempDirectory;
        private readonly string profilesDirectory;
        private readonly string legacyFilePath;
        private readonly StatisticsProfileService service;

        public StatisticsProfileServiceTests() {
            tempDirectory = Path.Combine(Path.GetTempPath(), "DitherStatisticsTests_" + Guid.NewGuid());
            profilesDirectory = Path.Combine(tempDirectory, "profiles");
            legacyFilePath = Path.Combine(tempDirectory, "statistics_data.json");
            service = new StatisticsProfileService(tempDirectory);
        }

        public void Dispose() {
            if (Directory.Exists(tempDirectory)) {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        private static PersistedStatisticsData BuildSampleData() {
            return new PersistedStatisticsData {
                DitherEvents = new List<DitherEvent> {
                    new DitherEvent {
                        StartTime = new DateTime(2026, 7, 1, 22, 30, 0),
                        EndTime = new DateTime(2026, 7, 1, 22, 30, 12),
                        SettleTime = 12.0,
                        PixelShiftX = 2.5,
                        PixelShiftY = -1.5,
                        Success = true,
                        CumulativeX = 2.5,
                        CumulativeY = -1.5
                    }
                },
                SettleTimeValues = new List<double> { 12.0 },
                PixelShiftValues = new List<PixelShiftPoint> {
                    new PixelShiftPoint(2.5, -1.5, 2.5, -1.5)
                },
                CumulativeX = 2.5,
                CumulativeY = -1.5
            };
        }

        // ---- Snapshot roundtrip over the data file ----

        [Fact]
        public void SaveAndLoad_Roundtrip_PreservesData() {
            service.SaveProfileDataToFile("Scope-A", BuildSampleData());

            var loaded = service.LoadProfileDataFromFile("Scope-A");

            Assert.NotNull(loaded);
            Assert.Single(loaded.DitherEvents);
            Assert.Equal(new DateTime(2026, 7, 1, 22, 30, 0), loaded.DitherEvents[0].StartTime);
            Assert.Equal(12.0, loaded.DitherEvents[0].SettleTime);
            Assert.True(loaded.DitherEvents[0].Success);
            Assert.Equal(new List<double> { 12.0 }, loaded.SettleTimeValues);
            Assert.Single(loaded.PixelShiftValues);
            Assert.Equal(2.5, loaded.PixelShiftValues[0].X);
            Assert.Equal(-1.5, loaded.PixelShiftValues[0].Y);
            Assert.Equal(2.5, loaded.CumulativeX);
            Assert.Equal(-1.5, loaded.CumulativeY);
        }

        [Fact]
        public void Load_MissingFile_ReturnsNull() {
            Assert.Null(service.LoadProfileDataFromFile("does-not-exist"));
            Assert.False(service.ProfileDataFileExists("does-not-exist"));
        }

        [Fact]
        public void Load_CorruptFile_Throws() {
            Directory.CreateDirectory(profilesDirectory);
            File.WriteAllText(Path.Combine(profilesDirectory, "Broken.json"), "{ not json");

            Assert.ThrowsAny<Exception>(() => service.LoadProfileDataFromFile("Broken"));
        }

        [Fact]
        public void ProfileDataFileExists_AfterSave_IsTrue() {
            service.SaveProfileDataToFile("Scope-A", new PersistedStatisticsData());

            Assert.True(service.ProfileDataFileExists("Scope-A"));
            Assert.True(File.Exists(Path.Combine(profilesDirectory, "Scope-A.json")));
        }

        // ---- Legacy v1.4 migration ----

        [Fact]
        public void MigrateLegacy_NoLegacyFile_ReturnsNoLegacyFile() {
            Assert.Equal(
                StatisticsProfileService.LegacyMigrationResult.NoLegacyFile,
                service.MigrateLegacyStatisticsFile());
        }

        [Fact]
        public void MigrateLegacy_TargetMissing_MovesFileToDefaultProfile() {
            Directory.CreateDirectory(tempDirectory);
            var json = System.Text.Json.JsonSerializer.Serialize(BuildSampleData());
            File.WriteAllText(legacyFilePath, json);

            var result = service.MigrateLegacyStatisticsFile();

            Assert.Equal(StatisticsProfileService.LegacyMigrationResult.Migrated, result);
            Assert.False(File.Exists(legacyFilePath));
            var loaded = service.LoadProfileDataFromFile(StatisticsProfileService.DefaultProfileName);
            Assert.NotNull(loaded);
            Assert.Single(loaded.DitherEvents);
        }

        [Fact]
        public void MigrateLegacy_TargetExists_DeletesLegacyAndKeepsTarget() {
            var existing = new PersistedStatisticsData { CumulativeX = 99.0 };
            service.SaveProfileDataToFile(StatisticsProfileService.DefaultProfileName, existing);
            File.WriteAllText(legacyFilePath, "{}");

            var result = service.MigrateLegacyStatisticsFile();

            Assert.Equal(StatisticsProfileService.LegacyMigrationResult.LegacyDeleted, result);
            Assert.False(File.Exists(legacyFilePath));
            var loaded = service.LoadProfileDataFromFile(StatisticsProfileService.DefaultProfileName);
            Assert.Equal(99.0, loaded.CumulativeX);
        }

        // ---- Profile name sanitization ----

        [Theory]
        [InlineData("  Scope-A  ", "Scope-A")]
        [InlineData("My:Profile", "My_Profile")]
        [InlineData("a/b\\c", "a_b_c")]
        public void SanitizeProfileName_ReplacesInvalidCharsAndTrims(string input, string expected) {
            Assert.Equal(expected, StatisticsProfileService.SanitizeProfileName(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void SanitizeProfileName_EmptyInput_ReturnsNull(string input) {
            Assert.Null(StatisticsProfileService.SanitizeProfileName(input));
        }

        [Fact]
        public void SanitizeProfileName_LongName_TruncatedTo50Chars() {
            var name = new string('x', 80);

            Assert.Equal(new string('x', 50), StatisticsProfileService.SanitizeProfileName(name));
        }

        [Fact]
        public void GetProfileDataFilePath_CollidingNames_MapToSameFile() {
            // Two different display names sanitize to the same file name - the VM
            // guards against this in CreateProfile by comparing sanitized names
            Assert.Equal(
                service.GetProfileDataFilePath("My:Profile"),
                service.GetProfileDataFilePath("My?Profile"));
        }

        // ---- Persistence off: delete files, keep in-memory data ----

        [Fact]
        public void DeleteAllProfileDataFiles_RemovesFilesButKeepsMemory() {
            service.StoreInMemory("Scope-A", BuildSampleData());
            service.SaveProfileDataToFile("Scope-A", BuildSampleData());
            service.SaveProfileDataToFile("Scope-B", new PersistedStatisticsData());

            service.DeleteAllProfileDataFiles();

            Assert.Empty(Directory.GetFiles(profilesDirectory, "*.json"));
            Assert.True(service.TryGetFromMemory("Scope-A", out var kept));
            Assert.Single(kept.DitherEvents);
        }

        [Fact]
        public void DeleteAllStatisticsDataFiles_RemovesLegacyAndProfileFiles() {
            Directory.CreateDirectory(tempDirectory);
            File.WriteAllText(legacyFilePath, "{}");
            service.SaveProfileDataToFile("Scope-A", new PersistedStatisticsData());

            service.DeleteAllStatisticsDataFiles();

            Assert.False(File.Exists(legacyFilePath));
            Assert.Empty(Directory.GetFiles(profilesDirectory, "*.json"));
        }

        [Fact]
        public void DeleteAllProfileDataFiles_MissingDirectory_DoesNotThrow() {
            service.DeleteAllProfileDataFiles();
            service.DeleteAllStatisticsDataFiles();
        }

        // ---- Profile deletion ----

        [Fact]
        public void DeleteProfileDataFile_RemovesOnlyTargetFile() {
            service.SaveProfileDataToFile("Scope-A", new PersistedStatisticsData());
            service.SaveProfileDataToFile("Scope-B", new PersistedStatisticsData());

            service.DeleteProfileDataFile("Scope-A");

            Assert.False(service.ProfileDataFileExists("Scope-A"));
            Assert.True(service.ProfileDataFileExists("Scope-B"));
        }

        [Fact]
        public void DeleteProfileDataFile_MissingFile_DoesNotThrow() {
            service.DeleteProfileDataFile("does-not-exist");
        }

        // ---- In-memory store ----

        [Fact]
        public void InMemoryStore_IsCaseInsensitive() {
            service.StoreInMemory("Scope-A", BuildSampleData());

            Assert.True(service.TryGetFromMemory("scope-a", out var data));
            Assert.Single(data.DitherEvents);
        }

        [Fact]
        public void ClearMemory_RemovesAllEntries() {
            service.StoreInMemory("Scope-A", new PersistedStatisticsData());
            service.StoreInMemory("Scope-B", new PersistedStatisticsData());

            service.ClearMemory();

            Assert.False(service.TryGetFromMemory("Scope-A", out _));
            Assert.Empty(service.GetInMemoryProfiles());
        }

        [Fact]
        public void RemoveFromMemory_RemovesOnlyTargetEntry() {
            service.StoreInMemory("Scope-A", new PersistedStatisticsData());
            service.StoreInMemory("Scope-B", new PersistedStatisticsData());

            service.RemoveFromMemory("Scope-A");

            Assert.False(service.TryGetFromMemory("Scope-A", out _));
            Assert.True(service.TryGetFromMemory("Scope-B", out _));
        }

        // ---- Self-heal profile name discovery ----

        [Fact]
        public void GetProfileNamesFromDataFiles_ReturnsFileNamesWithoutExtension() {
            service.SaveProfileDataToFile("Scope-A", new PersistedStatisticsData());
            service.SaveProfileDataToFile("Scope-B", new PersistedStatisticsData());

            var names = service.GetProfileNamesFromDataFiles();

            Assert.Contains("Scope-A", names);
            Assert.Contains("Scope-B", names);
            Assert.Equal(2, names.Count);
        }

        [Fact]
        public void GetProfileNamesFromDataFiles_MissingDirectory_ReturnsEmpty() {
            Assert.Empty(service.GetProfileNamesFromDataFiles());
        }
    }
}
