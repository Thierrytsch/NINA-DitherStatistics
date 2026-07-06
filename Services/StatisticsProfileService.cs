using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// Owns the per-profile statistics data: the in-memory store for inactive
    /// profiles and the JSON data files under
    /// %LocalAppData%\NINA\DitherStatistics\profiles\ (one &lt;name&gt;.json per profile),
    /// plus the migration of the legacy v1.4 single data file. Like
    /// PluginSettingsStore this class is Logger-free and lets I/O exceptions
    /// propagate; the VM call sites keep the original try/catch + log messages.
    /// </summary>
    public class StatisticsProfileService {
        public const string DefaultProfileName = "Default";

        public enum LegacyMigrationResult {
            NoLegacyFile,
            Migrated,
            LegacyDeleted
        }

        // Legacy pre-1.5 single data file (statistics_data.json)
        private readonly string legacyDataFilePath;

        // Per-profile data files live here, one <name>.json per profile
        private readonly string profilesDirectory;

        // Guards every file operation on the data files (same role as the
        // former persistenceLock in the VM)
        private readonly object persistenceLock = new object();

        // Inactive profiles keep their data here for the duration of the session,
        // so switching away and back never loses data even with persistence off
        private readonly Dictionary<string, PersistedStatisticsData> profileStore =
            new Dictionary<string, PersistedStatisticsData>(StringComparer.OrdinalIgnoreCase);

        public StatisticsProfileService(string baseDirectory = null) {
            baseDirectory ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA", "DitherStatistics");
            legacyDataFilePath = Path.Combine(baseDirectory, "statistics_data.json");
            profilesDirectory = Path.Combine(baseDirectory, "profiles");
        }

        // ---- In-memory store for inactive profiles ----

        public void StoreInMemory(string profileName, PersistedStatisticsData data) {
            profileStore[profileName] = data;
        }

        public bool TryGetFromMemory(string profileName, out PersistedStatisticsData data) {
            return profileStore.TryGetValue(profileName, out data);
        }

        public void RemoveFromMemory(string profileName) {
            profileStore.Remove(profileName);
        }

        public void ClearMemory() {
            profileStore.Clear();
        }

        /// <summary>
        /// Snapshot of the in-memory entries so callers can flush them file by
        /// file (each save individually guarded/logged at the call site).
        /// </summary>
        public IReadOnlyCollection<KeyValuePair<string, PersistedStatisticsData>> GetInMemoryProfiles() {
            return new List<KeyValuePair<string, PersistedStatisticsData>>(profileStore);
        }

        // ---- Profile name handling ----

        public static string SanitizeProfileName(string name) {
            if (name == null) return null;
            var result = name.Trim();
            foreach (var c in Path.GetInvalidFileNameChars()) {
                result = result.Replace(c, '_');
            }
            if (result.Length > 50) {
                result = result.Substring(0, 50);
            }
            return result.Length == 0 ? null : result;
        }

        public string GetProfileDataFilePath(string profileName) {
            return Path.Combine(profilesDirectory, SanitizeProfileName(profileName) + ".json");
        }

        public bool ProfileDataFileExists(string profileName) {
            return File.Exists(GetProfileDataFilePath(profileName));
        }

        /// <summary>
        /// Profile names derived from the existing data files (self-heal for
        /// the profile list when persistence is enabled).
        /// </summary>
        public List<string> GetProfileNamesFromDataFiles() {
            var names = new List<string>();
            if (!Directory.Exists(profilesDirectory)) return names;
            foreach (var file in Directory.GetFiles(profilesDirectory, "*.json")) {
                names.Add(Path.GetFileNameWithoutExtension(file));
            }
            return names;
        }

        // ---- Data file I/O ----

        public void SaveProfileDataToFile(string profileName, PersistedStatisticsData data) {
            lock (persistenceLock) {
                if (!Directory.Exists(profilesDirectory)) {
                    Directory.CreateDirectory(profilesDirectory);
                }
                var json = JsonSerializer.Serialize(data);
                File.WriteAllText(GetProfileDataFilePath(profileName), json);
            }
        }

        /// <summary>
        /// Returns null when no data file exists; throws on unreadable or corrupt content.
        /// </summary>
        public PersistedStatisticsData LoadProfileDataFromFile(string profileName) {
            var path = GetProfileDataFilePath(profileName);
            if (!File.Exists(path)) return null;
            lock (persistenceLock) {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<PersistedStatisticsData>(json);
            }
        }

        public void DeleteProfileDataFile(string profileName) {
            lock (persistenceLock) {
                var path = GetProfileDataFilePath(profileName);
                if (File.Exists(path)) {
                    File.Delete(path);
                }
            }
        }

        public void DeleteAllProfileDataFiles() {
            lock (persistenceLock) {
                DeleteAllProfileDataFilesLocked();
            }
        }

        /// <summary>
        /// Deletes the legacy pre-1.5 single data file plus every per-profile
        /// data file. In-memory data is not touched.
        /// </summary>
        public void DeleteAllStatisticsDataFiles() {
            lock (persistenceLock) {
                if (File.Exists(legacyDataFilePath)) {
                    File.Delete(legacyDataFilePath);
                }
                DeleteAllProfileDataFilesLocked();
            }
        }

        private void DeleteAllProfileDataFilesLocked() {
            if (!Directory.Exists(profilesDirectory)) return;
            foreach (var file in Directory.GetFiles(profilesDirectory, "*.json")) {
                File.Delete(file);
            }
        }

        // ---- Legacy migration ----

        /// <summary>
        /// One-time migration of the v1.4 single data file into the per-profile
        /// layout: moved to profiles\Default.json, or deleted when a Default
        /// profile file already exists.
        /// </summary>
        public LegacyMigrationResult MigrateLegacyStatisticsFile() {
            lock (persistenceLock) {
                if (!File.Exists(legacyDataFilePath)) {
                    return LegacyMigrationResult.NoLegacyFile;
                }
                if (!Directory.Exists(profilesDirectory)) {
                    Directory.CreateDirectory(profilesDirectory);
                }
                var target = GetProfileDataFilePath(DefaultProfileName);
                if (!File.Exists(target)) {
                    File.Move(legacyDataFilePath, target);
                    return LegacyMigrationResult.Migrated;
                }
                File.Delete(legacyDataFilePath);
                return LegacyMigrationResult.LegacyDeleted;
            }
        }
    }
}
