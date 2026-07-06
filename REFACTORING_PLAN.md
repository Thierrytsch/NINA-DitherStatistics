# Refactoring-Plan: Dither Statistics v1.6.0 → v2

Etappierter Refactoring-Plan für das NINA-Plugin "Dither Statistics". Jede Etappe ist
eigenständig buildbar, committbar und endet mit einem Verifikationsschritt.
Erstellt auf Basis einer vollständigen Analyse der Codebase (Stand v1.6.0, Branch `refactor/v2`).

---

## 1. Ausgangslage

| Datei | Zeilen | Problem |
|---|---|---|
| `DitherStatisticsVM.cs` | 2267 | God-Object: IDockableVM-Boilerplate, ScottPlot-Rendering, Theme-Polling, Tooltips, 6× copy-paste Settings-Datei-I/O, Profil- und Persistenzverwaltung, Statistik-Berechnung, PHD2-Event-Handling — alles in einer Klasse |
| `PHD2Client.cs` | 1489 | Zwei Verantwortungen vermischt: TCP/JSON-RPC-Transport **und** die komplette Optimizer-Analyse (Referenzfenster, Serien-Sammlung, Empfehlungsberechnung, Diagnose-Dateien) |
| `DitherStatisticsPlugin.cs` | 139 | Modellklassen (`DitherEvent`, `PixelShiftPoint`, `PersistedStatisticsData`) und Statistik-Helfer liegen in der Plugin-Manifest-Datei |
| `DitherQualityMetrics.cs` | 523 | In Ordnung: pure Mathematik ohne NINA-Abhängigkeiten — dient als Vorbild für die Zielarchitektur |

## 2. Zielarchitektur (entschieden)

1. **xUnit-Testprojekt** `DitherStatistics.Tests` — neue NuGet-Pakete nur im Test-csproj, nicht im Plugin
2. **Ein Projekt mit Ordnerstruktur** (`Models/`, `Services/`, `Phd2/`, `ViewModels/`, `Views/`) — eine DLL, PostBuild-Deployment unverändert
3. **PHD2Client voll trennen**: reiner Protokoll-Client (TCP/JSON-RPC, Events) + neuer `DitherOptimizerService` (Analyse, ohne Netzwerk testbar)
4. **MVVM sauber**: Logik aus dem ViewModel in Services; das VM wird Koordinator mit Bindings

## 3. Invarianten — dürfen sich in KEINER Etappe ändern

- **Persistenzformat ("Keep across sessions")**: JSON-Property-Namen von `PersistedStatisticsData`, `DitherAnalysisSnapshot` (inkl. `DitherDataPoint`/`DitherSeriesInfo`) und `DitherSettingsRecommendation` — insbesondere die `_Quality`/`_Balanced`/`_Performance`-Suffixe. Das Verschieben der nested classes aus `PHD2Client` ist JSON-sicher (System.Text.Json serialisiert keine Typnamen), solange die Property-Namen bleiben.
- **Settings-Dateien**: Formate und Pfade aller Text-Dateien unter `%LocalAppData%\NINA\DitherStatistics\` byte-identisch (`settings.txt`, `optimizer_settings.txt`, `persistence_settings.txt`, `multiprofile_settings.txt`, `profiles_list.txt`, `quality_settings.txt`, `profiles\<name>.json`)
- **NINA-Plugin-API-Verträge**: MEF-Exports unverändert — `DitherStatisticsPlugin : PluginBase` als `IPluginManifest`, `DitherStatisticsVM` als `IDockableVM` mit `[ImportingConstructor](IGuiderMediator, IProfileService)`, `Options : ResourceDictionary` im Namespace `ThierryTschanz.NINA.Ditherstatistics`
- **Plugin-GUID** in `Properties/AssemblyInfo.cs` niemals ändern
- **Namespace-Split bleibt**: Code in `DitherStatistics.Plugin`, `Options.xaml/.cs` in `ThierryTschanz.NINA.Ditherstatistics` (= AssemblyName, von NINA benötigt)
- **UI-Thread-Regeln**: `WpfPlot`-Instanzen entstehen lazy über Property-Getter beim XAML-Binding (nie im VM-Konstruktor oder auf Background-Threads); `LoadDataTemplates()` bleibt im VM-Konstruktor
- **Keine neuen NuGet-Abhängigkeiten im Plugin-Projekt** (Testprojekt: nur xUnit-Stack + ggf. NINA.Core für die Testlaufzeit)
- **Verhalten identisch**: Timings (Auto-Connect 2 s, Retry 10 s, Reconnect 5 s, Theme-Polling 500 ms), Diagnose-Dateischema, Empfehlungs-Mathematik, Log-Verhalten

## 4. Arbeitsregeln

- Pro Etappe ein Commit (oder mehrere logisch getrennte); Branch `refactor/v2`
- Nach jeder Etappe: `dotnet build DitherStatistics.sln -c Debug` **und** `-c Release` fehlerfrei
- Ab Etappe 2 zusätzlich: `dotnet test` grün
- Bei UI-/verhaltensrelevanten Etappen: NINA-Smoketest (Panel öffnen, Charts, Toggles, Profilwechsel, Restart mit Persistenz)

## 5. Etappen-Übersicht mit Modell-Empfehlung

| Etappe | Inhalt | Komplexität | Modell |
|---|---|---|---|
| 1 ✅ | Modelle & Ordnerstruktur | niedrig (mechanisch) | Sonnet |
| 2 | xUnit-Testprojekt + Kontrakt-Tests | niedrig | Sonnet |
| 3 | SettingsService | niedrig (mechanisch) | Sonnet |
| 4 ✅ | Statistik-Persistenz & Profilverwaltung | **hoch** (Architektur) | **Fable oder Opus** |
| 5 ✅ | Optimizer-Mathematik pur extrahieren | niedrig (mechanisch, testabgesichert) | Sonnet |
| 6 ✅ | DitherOptimizerService / PHD2-Split | **hoch** (Architektur, Kernstück) | **Fable** (bei Split: 6a Sonnet, 6b Fable) |
| 7 ✅ | Chart-Rendering & Theme | niedrig (mechanisch, UI-Thread beachten) | Sonnet |
| 8 ✅ | VM-Restentflechtung & Aufräumen | niedrig | Sonnet |

Begründung der Modellwahl:
- **Etappe 6** zieht eine Zustandsmaschine mit drei Locks (`ditherDataLock`, `referenceLock`, `sessionLock`), einem Timer mit Stale-Guard und Event-Verdrahtung über Klassengrenzen um. Fehler zeigen sich nicht beim Build, sondern als Race Conditions bei laufender Session → stärkstes Modell.
- **Etappe 4** hat mehrere subtile Invarianten im Profilwechsel-Datenfluss (Snapshot → Store → Datei → Restore; laufender Dither überlebt den Wechsel bewusst) plus Legacy-Migration.
- **Etappe 2 muss vor 4, 5 und 8 laufen**: die JSON-Kontrakt-Tests sind das Sicherheitsnetz, das die mechanischen Etappen für ein günstigeres Modell sicher macht.
- **Haiku für keine Etappe**: selbst die "niedrigen" Etappen haben Byte-Identitäts-Anforderungen (Settings-Formate, JSON-Property-Namen, Diagnose-Dateischema).

---

## Etappe 1 — Modelle & Ordnerstruktur ✅ ERLEDIGT

**Komplexität: niedrig (mechanisch) | Modell: Sonnet**

Reines Verschieben ohne Logikänderung. Alle Namespaces bleiben `DitherStatistics.Plugin`.

- Ordner anlegen: `Models/`, `Services/`, `Phd2/`, `ViewModels/`, `Views/`
- Aus `DitherStatisticsPlugin.cs` herauslösen (Datei behält nur die Plugin-Manifest-Klasse):
  - `DitherEvent`, `PixelShiftPoint`, `PersistedStatisticsData` → `Models/`
  - statische Klasse `DitherStatistics` (Average/Median/Quantile/StdDev) → `Services/Statistics.cs` — **Klassenname beibehalten** (wird von VM und PHD2Client referenziert)
- Aus `PHD2Client.cs` herauslösen:
  - `DitherSettingsRecommendation`, `DitherAnalysisSnapshot` → `Models/`
  - nested `PHD2Client.DitherDataPoint` und `PHD2Client.DitherSeriesInfo` auf Top-Level heben → `Models/` (JSON-kompatibel; `DitherAnalysisSnapshot`-Propertys referenzieren dann die Top-Level-Typen)
  - `PHD2GuidingDitheredEventArgs`, `PHD2SettleDoneEventArgs`, `PHD2GuideStepEventArgs` → `Phd2/Phd2EventArgs.cs`
- Dateien verschieben: `DitherStatisticsVM.cs` → `ViewModels/`, `DitherStatisticsView.xaml(.cs)` → `Views/`, `PHD2Client.cs` → `Phd2/`, `DitherQualityMetrics.cs` → `Services/`
- Achtung: `DitherStatisticsDataTemplates.xaml` referenziert `DitherStatisticsVM` und `DitherStatisticsView` per CLR-Namespace — da Namespaces unverändert bleiben, sind keine XAML-Änderungen nötig; Pack-URI bleibt gültig (AssemblyName unverändert). Beim Verschieben von `DitherStatisticsView.xaml` den `<Resource>`/Build-Action-Eintrag im csproj prüfen (SDK-Style-Globbing greift normalerweise automatisch).

**Verifikation:** Build Debug + Release; NINA starten, Panel öffnen; mit aktivierter Persistenz prüfen, dass gespeicherte Daten aus v1.6.0 korrekt geladen werden.

✅ Build Debug + Release fehlerfrei (0 Fehler, nur die zwei vorbestehenden NU1701-Warnungen für `ToastNotifications`/`VVVV.FreeImage`, unverändert durch dieses Refactoring). Der manuelle NINA-Smoketest (Panel öffnen, Persistenz-Restart mit v1.6.0-Daten) steht noch aus und muss vom Maintainer durchgeführt werden.

---

## Etappe 2 — xUnit-Testprojekt + Kontrakt-Tests ✅ ERLEDIGT

**Komplexität: niedrig | Modell: Sonnet**

- `DitherStatistics.Tests/DitherStatistics.Tests.csproj`: `net8.0-windows`, x64, xUnit-Stack (`xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`), ProjectReference auf das Plugin-Projekt
- **Stolperstein NINA-Laufzeit**: Die NINA-Pakete sind im Plugin mit `ExcludeAssets=runtime` referenziert — zur Testlaufzeit fehlen sie sonst. Lösung: im Test-csproj `NINA.Core` (3.1.2.9001) zusätzlich als normale PackageReference (ohne ExcludeAssets) aufnehmen, damit `NINA.Core.Utility.Logger`-Aufrufe auflösbar sind. Zusätzliche Regel für alle Folgeetappen: **neu extrahierte pure-math-Klassen bleiben Logger-frei** (Vorbild: `DitherQualityMetrics`).
- Solution um das Testprojekt erweitern; GitHub-Action um `dotnet test` ergänzen (PostBuild-Target läuft wegen `Condition="'$(CI)' != 'true'"` in CI ohnehin nicht)
- Erste Tests:
  - `DitherStatistics`-Helfer: Median/Quantile/StdDev-Randfälle (leer, 1 Element, gerade/ungerade Anzahl, Interpolation)
  - `DitherQualityMetrics.CalculateQualityMetrics`: Golden-Tests mit festen Punktmengen (Ergebniswerte einmalig aus dem Ist-Zustand erfassen und einfrieren)
  - **JSON-Kontrakt-Test** (Sicherheitsnetz für alle Folgeetappen): serialisiert `PersistedStatisticsData`, `DitherAnalysisSnapshot`, `DitherSettingsRecommendation`, `DitherEvent`, `PixelShiftPoint` und asserted die exakten Property-Namen im JSON

**Verifikation:** `dotnet test` grün; Plugin-Build unverändert; CI-Lauf grün.

✅ `DitherStatistics.Tests` (net8.0-windows, x64) mit `Microsoft.NET.Test.Sdk`/`xunit`/`xunit.runner.visualstudio`,
ProjectReference auf das Plugin, `NINA.Core` als normale PackageReference (Logger-Auflösung zur Testlaufzeit) und
`System.Drawing.Common` 8.0.0 gepinnt (sonst MSB3277-Konflikt zwischen der alten net462-Transitive von NINA.Core und
der net8.0-Version, die ScottPlot über die ProjectReference braucht). Stolperstein: der SDK-Default-Glob (`**/*.cs`)
des Plugin-Projekts las die Testdateien mit ein — behoben mit `<Compile Remove="DitherStatistics.Tests/**/*.cs" />`
in `DitherStatistics.csproj`. 29 Tests: `DitherStatistics`-Helfer (Median/Quantile/StdDev, inkl. leer/1-Element/
Interpolation), `DitherQualityMetrics`-Golden-Test (feste 8-Punkte-Menge, Werte aus dem Ist-Zustand eingefroren) und
JSON-Kontrakt-Tests für alle sieben Modelltypen (exakte Property-Namen, inkl. `_Quality/_Balanced/_Performance`-Suffixe
und dass `OptimizerData`/`Recommendation` als JSON `null` statt weggelassen serialisieren). `dotnet test` Schritt in
`.github/workflows/build-and-release.yaml` ergänzt. Solution um das Testprojekt erweitert (`dotnet sln add`).
Build Debug + Release weiterhin 0 Fehler (nur die zwei vorbestehenden NU1701-Warnungen, jetzt pro Projekt gemeldet).

---

## Etappe 3 — SettingsService ✅ ERLEDIGT

**Komplexität: niedrig (mechanisch) | Modell: Sonnet**

- Neu: `Services/PluginSettingsStore.cs` — kapselt die 6 Settings-Dateien mit **byte-identischen** Formaten:
  - `settings.txt`, `optimizer_settings.txt`, `persistence_settings.txt`, `multiprofile_settings.txt`: `bool.ToString()`-Inhalt
  - `quality_settings.txt`: `key=value`-Zeilen, InvariantCulture
  - `profiles_list.txt`: Zeile 1 = selektiertes Profil, Folgezeilen = alle Profilnamen
- Basisverzeichnis per Konstruktor injizierbar (Default `%LocalAppData%\NINA\DitherStatistics`) → in Tests Temp-Verzeichnis
- Die VM-Methoden `Load/SaveQualityAssessmentSetting`, `Load/SaveDitherOptimizerSetting`, `Load/SaveStatisticsPersistenceSetting`, `Load/SaveMultiProfileSetting`, `Load/SaveProfileListSetting`, `Load/SaveQualityMetricSettings` delegieren an den Store
- Die Lade-Semantik "Backing-Field direkt setzen, um Setter-Nebenwirkungen zu vermeiden" (Persistenz-/Multiprofil-Toggle) bleibt bewusst im VM — sie ist VM-Zustandslogik, kein Datei-I/O
- Tests: Roundtrip pro Dateityp, fehlende Datei → Default, defekter Inhalt → Default

**Verifikation:** `dotnet test`; NINA-Smoketest: alle Toggles setzen, NINA neu starten, Zustände überleben.

✅ `Services/PluginSettingsStore.cs` gekapselt die Datei-I/O für alle 6 Settings-Dateien
(`settings.txt`, `optimizer_settings.txt`, `persistence_settings.txt`, `multiprofile_settings.txt`,
`quality_settings.txt`, `profiles_list.txt`) hinter `ReadBool`/`WriteBool`,
`ReadQualityMetricSettings`/`WriteQualityMetricSettings` und `ReadProfileList`/`WriteProfileList` —
Formate byte-identisch zum bisherigen VM-Code. Basisverzeichnis ist per Konstruktor injizierbar
(Default `%LocalAppData%\NINA\DitherStatistics`), im Test auf ein Temp-Verzeichnis gesetzt. Die VM-Methoden
`Load/SaveQualityAssessmentSetting`, `Load/SaveDitherOptimizerSetting`, `Load/SaveStatisticsPersistenceSetting`,
`Load/SaveMultiProfileSetting`, `Load/SaveProfileListSetting`, `Load/SaveQualityMetricSettings` delegieren jetzt
an den Store; Logging, Backing-Field-Semantik (Persistenz-/Multiprofil-Toggle) und die Self-Heal-Logik aus dem
Profile-Verzeichnis (liest `*.json`-Dateien, wenn Persistenz an ist) bleiben bewusst im VM, da sie VM-Zustand
bzw. Profilverwaltung (Etappe 4) betreffen, nicht reines Datei-I/O. `PersistedStatisticsData`-Datei
(`statistics_data.json`) und die Profilverzeichnis-Konstanten bleiben unverändert im VM (Etappe 4).
12 neue Tests in `DitherStatistics.Tests/PluginSettingsStoreTests.cs`: Bool-Roundtrip (true/false),
Bool-Dateiinhalt (`bool.ToString()`), fehlende Datei → `null`, defekter Inhalt → `null`; Quality-Metric-Settings
Roundtrip, fehlende Datei → `null`, defekte Zeilen werden übersprungen, InvariantCulture-Format; Profile-List
Roundtrip, fehlende/leere Datei → Default-Selected + leere Liste. `dotnet test`: 41 grün (29 vorher + 12 neu).
Build Debug + Release weiterhin 0 Fehler (nur die zwei vorbestehenden NU1701-Warnungen). NINA-Smoketest
(Toggles setzen, Restart, Zustände prüfen) steht noch aus und muss vom Maintainer durchgeführt werden.

---

## Etappe 4 — Statistik-Persistenz & Profilverwaltung ✅ ERLEDIGT

**Komplexität: HOCH (Architektur) | Modell: Fable oder Opus**

- Neu: `Services/StatisticsProfileService.cs` — übernimmt aus dem VM:
  - `profileStore`-Dictionary (In-Memory-Daten inaktiver Profile), `persistenceLock`
  - `SaveProfileDataToFile`, Laden einzelner Profile, `DeleteAllProfileDataFiles`, `DeleteStatisticsData`
  - `MigrateLegacyStatisticsFile` (v1.4 `statistics_data.json` → `profiles\Default.json`)
  - `SanitizeProfileName`, `GetProfileDataFilePath`
  - Verzeichnisse injizierbar (testbar in Temp-Verzeichnis)
- Das VM behält: UI-Zustand (`ObservableCollection ProfileNames`, `SelectedProfileName`-Setter-Validierung, `ProfileNameInput`), `BuildCurrentSnapshot` (braucht Zugriff auf Live-Collections) und die Orchestrierung von `SwitchToProfile`
- Zu erhaltende Verhaltensdetails (dokumentieren + testen):
  - Profilwechsel: Snapshot des alten Profils → Store + ggf. Datei → neues Profil aus Store oder Datei laden → Charts/Statistik/Optimizer neu befüllen
  - Ein laufender Dither (`currentDither`) überlebt den Wechsel bewusst und landet im neuen Profil
  - `ClearData` löscht ALLE Profile (Speicher + Dateien), behält aber Profilnamen und Selektion
  - Persistenz-Toggle EIN: sofortiger Snapshot aller Profile auf Disk; AUS: alle Dateien löschen, In-Memory bleibt
  - Optimizer-Daten-Reihenfolge beim Wechsel: erst `ClearDitherAnalysisData()`, dann `RestoreDitherAnalysisData()` (Restore kehrt bei leerem Snapshot früh zurück und würde sonst Daten des alten Profils stehen lassen)
- Tests: Snapshot-Roundtrip über Datei, Legacy-Migration (beide Zweige: Ziel existiert / existiert nicht), Sanitisierungs-Kollisionen (zwei Namen → gleiche Datei), Persistenz aus/ein, Profil löschen

**Verifikation:** `dotnet test`; NINA-Smoketest: Profil anlegen/wechseln/löschen, Restart mit Persistenz an → alle Profile wiederhergestellt; v1.4-Legacy-Datei-Migration einmal manuell durchspielen.

✅ `Services/StatisticsProfileService.cs` übernimmt das `profileStore`-Dictionary, den `persistenceLock`,
`SaveProfileDataToFile`/`LoadProfileDataFromFile`, `DeleteProfileDataFile`/`DeleteAllProfileDataFiles`/
`DeleteAllStatisticsDataFiles` (inkl. Legacy-Datei), `MigrateLegacyStatisticsFile` (gibt ein
`LegacyMigrationResult`-Enum zurück, damit die Log-Meldungen im VM bleiben), `SanitizeProfileName`,
`GetProfileDataFilePath` und die Self-Heal-Namensermittlung aus den Profildateien
(`GetProfileNamesFromDataFiles`). Basisverzeichnis per Konstruktor injizierbar (Default
`%LocalAppData%\NINA\DitherStatistics`), im Test ein Temp-Verzeichnis. Wie beim `PluginSettingsStore`
ist der Service Logger-frei: I/O-Exceptions propagieren, die VM-Aufrufstellen behalten ihre bisherigen
try/catch-Blöcke mit identischen Log-Meldungen (VM-Wrapper `SaveProfileDataToFile` fängt weiterhin pro
Profil einzeln, damit der Flush-Loop beim Persistenz-Einschalten/Dispose nicht abbricht). Das VM behält
UI-Zustand (`ProfileNames`, `SelectedProfileName`-Setter-Validierung, `ProfileNameInput`),
`BuildCurrentSnapshot` und die `SwitchToProfile`-Orchestrierung; die dokumentierten Verhaltensdetails
(laufender Dither überlebt den Wechsel; `ClearData` löscht alle Profile, behält Namen+Selektion;
Persistenz-Toggle EIN = Snapshot aller Profile, AUS = Dateien löschen, Memory bleibt; erst
`ClearDitherAnalysisData()`, dann `RestoreDitherAnalysisData()`) sind unverändert — die Etappe hat nur
Datei-I/O und Store verschoben, keine Ablauflogik. Feldname im VM ist `profileDataService`
(`profileService` ist dort schon der NINA-`IProfileService`). `DefaultProfileName` bleibt als Const-Alias
im VM erhalten. 25 neue Tests in `DitherStatistics.Tests/StatisticsProfileServiceTests.cs`:
Snapshot-Roundtrip über Datei, fehlende Datei → null, defekte Datei → Exception, Legacy-Migration
(alle drei Zweige: kein Legacy-File / Ziel fehlt → Move / Ziel existiert → Delete),
Sanitisierung (Trim, Sonderzeichen, 50-Zeichen-Kappung, leer → null) inkl. Kollision zweier Namen auf
dieselbe Datei, Persistenz-AUS-Semantik (Dateien weg, Memory bleibt), Profil-Löschung (nur Zieldatei),
In-Memory-Store (case-insensitive, Remove/Clear), Self-Heal-Namen. `dotnet test`: 66 grün (41 + 25 neu).
Build Debug + Release 0 Fehler (nur die vorbestehenden NU1701-Warnungen). NINA-Smoketest
(Profil anlegen/wechseln/löschen, Persistenz-Restart, Legacy-Migration manuell) steht noch aus und
muss vom Maintainer durchgeführt werden.

---

## Etappe 5 — Optimizer-Mathematik pur extrahieren ✅ ERLEDIGT

**Komplexität: niedrig (mechanisch, durch Tests abgesichert) | Modell: Sonnet**

Vorbereitung für Etappe 6: die reine Analyse-Mathematik wird ohne Logikänderung aus `PHD2Client` in eine statische, Logger-freie Klasse verschoben.

- Neu: `Services/DitherAnalysis.cs` (pure static):
  - `AnalyzeSeries` (Zeit-bis-stabil pro Serie/Profil, 3-Frame-Debounce, Fallback auf gespeicherte Thresholds bzw. erste-Punkt-Zeitbasis für Legacy-Serien)
  - `CalculateRecommendation` (Toleranz = Quantil, MinSettle = Debounce-Formel, Expected = Median, Timeout = (P95 + MinSettle) × 1,5 usw.)
  - Quantil-Threshold-Berechnung aus einer Werteliste
  - `SeriesSettleAnalysis`-Ergebnistyp und die Konstanten (`PROFILE_QUANTILES`, `PROFILE_LABELS`, `STABLE_CONSECUTIVE_POINTS`, `REFERENCE_MIN_POINTS`, …) wandern mit
- `PHD2Client` ruft ab jetzt die statischen Funktionen — Verhalten identisch
- Tests mit synthetischen Serien: stabilisiert nach n Punkten, nie stabil (censored), ausgeschlossen (SettleFailed/StarLost), Legacy ohne `DitherSeriesInfo`, Debounce-Verhalten (Einzeldip zählt nicht), Timeout-Formel inkl. 10-s-Rundung, Fallback auf Median der gespeicherten Thresholds wenn kein Referenzfenster

**Verifikation:** `dotnet test`; Diagnose-Dateien (`*_dither_analysis.txt`, `*_settle_analysis.txt`) einer Beispielsession vor/nach der Etappe diffen — identisch.

✅ `Services/DitherAnalysis.cs` (pure static, Logger-frei) übernimmt `AnalyzeSeries`, `CalculateRecommendation`,
die neue `CalculateThresholds`-Funktion (Quantil-Berechnung aus einer Werteliste, vorher inline in
`GetReferenceThresholds`), den `SeriesSettleAnalysis`-Ergebnistyp sowie die Konstanten `PROFILE_COUNT`,
`PROFILE_QUANTILES`, `PROFILE_LABELS`, `REFERENCE_MIN_POINTS`, `STABLE_CONSECUTIVE_POINTS` — 1:1 aus
`PHD2Client` übernommen, keine Logikänderung. `PHD2Client` behält `REFERENCE_MAX_POINTS`/`REFERENCE_MAX_AGE`
(Fenster-Trimming) und `POST_SETTLE_STEPS`/`COLLECTION_CAP_MS` (Sammel-Zustandsmaschine, Etappe 6), da das
reine Zustandsverwaltung ist, keine Mathematik. `GetReferenceThresholds` delegiert an
`DitherAnalysis.CalculateThresholds`; `RunAnalysisAndRecommendation` ruft `DitherAnalysis.AnalyzeSeries`/
`CalculateRecommendation` auf und behält dabei das identische Log-Verhalten bei übersprungener Empfehlung
("No dither series in data" / "No reference distribution available yet") durch einen `else`-Zweig am
Call-Standort, da die ausgelagerte Funktion selbst nicht mehr loggt. `WriteDitherAnalysisFile`/
`WriteSettleAnalysisFile` referenzieren die Konstanten jetzt über `DitherAnalysis.*`, Dateiformat unverändert.
14 neue Tests in `DitherStatistics.Tests/DitherAnalysisTests.cs`: Stabilisierung nach n Punkten, censored
(nie stabil), Ausschluss via SettleFailed/StarLost (Theory), Legacy-Serie ohne `DitherSeriesInfo`
(Zeitbasis = erster Punkt), Debounce (Einzeldip zählt nicht), gespeicherter Threshold hat Vorrang vor
aktuellem, `CalculateThresholds` unter/über Mindestpunktzahl, `CalculateRecommendation` ohne Analysen/ohne
Toleranz (null), Median-Fallback der gespeicherten Thresholds ohne Referenzfenster, Timeout-Formel inkl.
10-s-Rundung und IQR-Spread, sowie dass ausgeschlossene Serien in den Totals mitgezählt aber aus dem Timing
ausgeschlossen werden. `dotnet test`: 80 grün (66 vorher + 14 neu). Build Debug + Release 0 Fehler (nur die
vorbestehenden NU1701-Warnungen). Diagnose-Datei-Diff einer echten PHD2-Session vor/nach der Etappe steht
noch aus und muss vom Maintainer durchgeführt werden (reine Verschiebung ohne Formeländerung macht Abweichung
unwahrscheinlich).

---

## Etappe 6 — DitherOptimizerService, PHD2Client wird reiner Protokoll-Client ✅ ERLEDIGT

**Komplexität: HOCH (Architektur, Kernstück) | Modell: Fable — bei Split: 6a Sonnet, 6b Fable**

Größte Etappe. Bei Bedarf splitten: **6a** Service-Gerüst + neue Client-Events (Sonnet), **6b** Zustandsmaschine umziehen (Fable).

- `Phd2/PHD2Client.cs` behält nur Protokoll:
  - TCP/JSON-RPC (Verbindung, Read-Loop, Request/Response-Tracking), Event-Parsing
  - Events: `GuidingDithered`, `SettleDone`, `GuideStep` (roh), `ConnectionStatusChanged` — **neu: `StarLost` und `GuidingStarted`** als Events (heute inline im Client behandelt)
  - `QueryExposureTime`/`QueryPixelScale` inkl. Timing-Fallback für die Exposure (transport-nah), `GuiderPixelScaleArcsec`
- Neu: `Services/DitherOptimizerService.cs` — übernimmt:
  - Session-Tracking (`sessionDX/DY/RMS`, `ComputeSessionStatsLocked`) inkl. Reset bei `GuidingStarted`
  - Rolling-Referenzfenster (400 Punkte / 15 min) + Trimming
  - Sammel-Zustandsmaschine: Serie starten bei `GuidingDithered`, `postSettleStepsRemaining`-Countdown nach `SettleDone`, Collection-Cap-Timer (120 s) **inkl. Stale-Timer-Guard** (`ReferenceEquals(sender, timer)`), Finalisierung mit Threshold-Capture, Rapid-Dithering-Fall (neuer Dither schließt altes Fenster)
  - `isDithering`-Zustand (heute im Client; steuert NaN-RMS und Referenzfenster-Ausschluss während des Settlings)
  - Diagnose-Datei-Writer — Dateinamen-Schema identisch: `<sessionTimestamp>_<profil>_dither_analysis.txt` / `..._settle_analysis.txt`
  - `GetDitherAnalysisSnapshot` / `RestoreDitherAnalysisData` / `ClearDitherAnalysisData`, `CurrentProfileName`
  - Event `DitherRecommendationUpdated`
- `RunningRMS`/`RMSStdDev` in `PHD2GuideStepEventArgs`: das VM abonniert `GuideStep` nicht — die Berechnung wandert in den Service; die Args-Klasse bleibt unverändert (Kompatibilität), der Client füllt nur noch DX/DY/Exposure/Timestamp
- PHD2-Reconnect-Schleife (heute rekursiv im VM: `ConnectToPHD2`) → `Phd2/Phd2ConnectionManager.cs` oder in den Client; **gleiches Timing**: 2 s Initial-Delay, 10 s Retry, 5 s nach Verbindungsverlust
- VM-Verdrahtung: Client-Events → OptimizerService; `OptimizerService.DitherRecommendationUpdated` → `Recommendation`-Property; Snapshot/Restore/Clear-Aufrufe des VM gehen an den Service statt an `phd2Client`
- Lock-Disziplin 1:1 übernehmen und im Code dokumentieren: `ditherDataLock` (Serien + Sammelzustand), `referenceLock` (Fenster), `sessionLock` (Session-Statistik); `Disconnect` räumt das laufende Sammelfenster auf, behält aber akkumulierte Serien

**Verifikation:** `dotnet test` (Analyse-Tests aus Etappe 5 laufen jetzt gegen den Service-Pfad); echte oder simulierte PHD2-Session: Empfehlungswerte und Diagnose-Dateien identisch zu vorher; Persistenz-Roundtrip mit Optimizer-Daten (Snapshot alt → Restore neu); Profilwechsel während laufendem Sammelfenster.

✅ Drei-Wege-Split umgesetzt. **`Phd2/PHD2Client.cs`** (~530 Zeilen) ist reiner Protokoll-Client:
TCP/JSON-RPC, Read-Loop, Request-Tracking, Event-Parsing, `QueryExposureTime`/`QueryPixelScale` inkl.
Timing-Fallback (samt "Skip first GuideStep"-Verhalten), `GuiderPixelScaleArcsec`, neu öffentlich
`CurrentGuideExposure`. Neue Events `StarLost` und `GuidingStarted` (vorher inline behandelt);
`GuideStep`-Args werden nur noch mit DX/DY/Exposure/Timestamp gefüllt (`RunningRMS`/`RMSStdDev` bleiben
als Felder für Kompatibilität, per Doku-Kommentar als bewusst ungefüllt markiert — kein Konsument mehr).
**`Services/DitherOptimizerService.cs`** übernimmt die komplette Zustandsmaschine 1:1: Session-Tracking
(`sessionLock`), Rolling-Referenzfenster (`referenceLock`, 400 P / 15 min), Sammel-Zustandsmaschine
(`ditherDataLock`, Cap-Timer 120 s inkl. Stale-Guard `ReferenceEquals(sender, timer)`, Rapid-Dithering-
Finalisierung, Threshold-Capture), `isDithering`, Diagnose-Datei-Writer (Schema identisch, Verzeichnis
per Konstruktor injizierbar), Snapshot/Restore/Clear, `CurrentProfileName`, Event
`DitherRecommendationUpdated`. Lock-Disziplin im Klassenkommentar dokumentiert (einzige erlaubte
Verschachtelung: `ditherDataLock` → `referenceLock`, wie vorher). Exposure/Pixel-Scale liest der Service
lazy über injizierte `Func<>`-Delegates vom Client (in Tests stubbar). Die per-GuideStep-Berechnung der
Session-RMS entfällt (wurde nur für die Event-Args gebraucht); alle beobachtbaren Ausgaben (Empfehlung,
Diagnose-Dateien) berechnen sie wie bisher zum Analysezeitpunkt via `ComputeSessionStatsLocked`.
**`Phd2/Phd2ConnectionManager.cs`** übernimmt Auto-Connect/Reconnect aus dem VM (Rekursion → Schleife,
Timing identisch: 2 s initial, 10 s Retry, 5 s nach Verlust) samt dedupliziertem Status-Logging
(geteiltes `hasLoggedConnectionFailure`-Flag wie vorher im VM). VM-Verdrahtung: Client-Events →
`optimizerService.Handle*` (Optimizer vor den VM-Handlern abonniert = alte Reihenfolge); Cleanup des
Sammelfensters nur bei explizitem `Disconnect` (Status exakt `"Disconnected"`), Verbindungsverlust räumt
bewusst nicht auf (wie vorher); Dispose-Reihenfolge Snapshot → Client-Dispose → Service-Dispose erhalten,
d. h. In-Progress-Punkte landen weiterhin in der Persistenz. Bewusste sichtbare Abweichung: Log-Präfix
der umgezogenen Meldungen ist jetzt `DitherOptimizer:` statt `PHD2Client:` (Texte unverändert).
10 neue Tests in `DitherStatistics.Tests/DitherOptimizerServiceTests.cs` treiben den Service über die
`Handle*`-Methoden ohne Netzwerk: kompletter Dither-Zyklus (Finalisierung nach SettleDone + 10 Steps,
Threshold-Capture, Empfehlungs-Event, Diagnose-Dateien im Temp-Verzeichnis), Ausschluss der Settling-
Steps aus dem Referenzfenster, Rapid-Dithering, StarLost/SettleFailed-Markierung, Disconnect-Semantik
(laufendes Fenster verworfen, Serien+Zähler bleiben), Clear (inkl. keine Orphan-Sammlung danach),
Restore-Zähler-Fortsetzung über höchster ID, In-Progress-Snapshot, leerer/null-Snapshot räumt nicht.
`dotnet test`: 90 grün (80 + 10 neu). Build Debug + Release 0 Fehler (nur vorbestehende NU1701-Warnungen).
Verifikation mit echter PHD2-Session (Empfehlungswerte/Diagnose-Dateien-Diff, Profilwechsel während
laufendem Sammelfenster) steht noch aus und muss vom Maintainer durchgeführt werden.

---

## Etappe 7 — Chart-Rendering & Theme aus dem VM ✅ ERLEDIGT

**Komplexität: niedrig (mechanisch — UI-Thread-Regeln beachten) | Modell: Sonnet**

- Neu: `Services/NinaThemeWatcher.cs` — `GetThemeColor`-Lookup (Application/MainWindow-Resources, Brush oder Color), DispatcherTimer-Polling alle 500 ms (Verhalten identisch), Event bei Farbwechsel; Dispose stoppt den Timer
- Neu: `Services/PixelShiftChartRenderer.cs` + `Services/SettleTimeChartRenderer.cs` (oder eine Datei): übernehmen `UpdatePixelShiftChart`, `UpdateSettleTimeChart`, `UpdateChartColors` — nehmen `WpfPlot` + Daten + Theme-Farbe entgegen, kein VM-Zugriff
- Tooltip-Logik (`AttachPixelShiftTooltip`, `AttachSettleTimeTooltip`, Nächster-Punkt-Suche mit 5 %-/10 %-Schwellen) in einen Helper; die Tooltip-Text/Visible-Properties bleiben im VM (Bindings)
- **Bleibt im VM**: die Lazy-Property-Getter von `PixelShiftPlot`/`SettleTimePlot` — die Erzeugung auf dem UI-Thread via XAML-Binding ist der tragende Mechanismus; die Getter delegieren Styling/Tooltip-Attach an die Services

**Verifikation:** Build; NINA-Smoketest: beide Charts rendern, Tooltips funktionieren, NINA-Theme wechseln → Chartfarben ziehen innerhalb ~1 s nach.

✅ Drei neue Dateien unter `Services/`, alle in der gewohnten `DitherStatistics.Plugin`-Namespace
(keine Unter-Namespaces, konsistent zu den bisherigen Services). **`Services/NinaThemeWatcher.cs`**:
`GetThemeColor` als statische Methode (Brush/Color/MainWindow-Fallback, identische Log-Meldungen),
`Start()` erzeugt den `DispatcherTimer` weiterhin über `Application.Current.Dispatcher.BeginInvoke`
(500 ms, gleiche Lognachrichten), Event `PrimaryColorChanged` statt direktem Chart-Zugriff, `Dispose()`
mit dem gleichen Null-Guard wie vorher (verhindert doppeltes Stop-Log). **`Services/ChartRenderers.cs`**:
`ChartTheme.ApplyColors` (die 7 Zeilen Achsen-/Grid-Styling, vorher `UpdateChartColors`, jetzt von beiden
Renderern und den Lazy-Property-Gettern geteilt), `PixelShiftChartRenderer.Render` und
`SettleTimeChartRenderer.Render` — beide nehmen `WpfPlot` + Rohdaten (Liste/Statistikwerte) + Theme-Farbe
entgegen, identische Zeichenlogik (Gradient-Scatter, Crosshair, Avg/StdDev-Bänder), kein VM-Typ importiert.
**`Services/ChartTooltipHelper.cs`**: `AttachPixelShiftTooltip`/`AttachSettleTimeTooltip` nehmen statt eines
VM-Zeigers `Func<IReadOnlyList<T>>` (liest die aktuelle, veränderliche VM-Liste erst beim Mausereignis)
sowie `Action<string>`/`Action<bool>` für Tooltip-Text/-Sichtbarkeit — 5 %-Diagonale- bzw. 10 %-Y-Range-
Schwellenwerte unverändert. Das VM behält die Lazy-Getter von `PixelShiftPlot`/`SettleTimePlot`
(Plot-Erzeugung weiterhin nur beim ersten XAML-Zugriff auf dem UI-Thread) und delegiert darin nur noch
Styling (`ChartTheme.ApplyColors`) und Tooltip-Attach an die Services; `UpdatePixelShiftChart`/
`UpdateSettleTimeChart` sind auf Try/Catch + Delegation an die Renderer geschrumpft (identische
Error-Log-Meldungen). Neuer VM-Handler `OnThemeColorChanged` ersetzt `OnThemeColorTimerTick` und
reproduziert den gleichen Dispatcher-Hop (`BeginInvoke`) beim Aktualisieren beider Chartfarben.
Entfernt aus dem VM: Felder `themeColorTimer`/`lastPrimaryColor`, Methoden `StartThemeColorMonitoring`,
`OnThemeColorTimerTick`, `UpdateChartColors`, `GetThemeColor`, `AttachPixelShiftTooltip`,
`AttachSettleTimeTooltip` sowie das jetzt ungenutzte `using ScottPlot.Plottable;` (MarkerShape/LineStyle
werden nur noch in `ChartRenderers.cs` gebraucht). Build Debug + Release 0 Fehler (nur die zwei
vorbestehenden NU1701-Warnungen); `dotnet test`: 90 grün (unverändert zu Etappe 6, keine Tests betreffen
Chart-Rendering). NINA-Smoketest (beide Charts rendern, Tooltips, Theme-Wechsel) steht noch aus und muss
vom Maintainer durchgeführt werden.

---

## Etappe 8 — VM-Restentflechtung & Aufräumen ✅ ERLEDIGT

**Komplexität: niedrig | Modell: Sonnet**

- Neu: `Services/ExportService.cs` — CSV-Export (`DitherEvents_<timestamp>.csv`) und Quality-Report (`DitherQuality_<timestamp>.txt`); Pfade/Formate identisch (`Documents\N.I.N.A\DitherStatistics`)
- `UpdateStatistics`-Kern (Avg/Median/Min/Max/StdDev/SuccessRate/Drift-Range) als pure Funktion → `Services/Statistics.cs`, testbar; VM mappt Ergebnis auf Properties
- `GetPixelScaleRatio` in kleinen `Services/PixelScaleService.cs` (NINA-GuiderInfo-Scale, PHD2-Scale, Profil-Brennweite/Pixelgröße, manueller Override, Quellen-Reporting "manual/auto-NINA/auto-PHD2/fallback")
- Tote Symbole entfernen (vorher je einzeln verifizieren):
  - ungenutztes Feld `Random random` im VM
  - `XFormatter`/`YFormatter`/`SettleTimeFormatter` — nur entfernen, wenn kein XAML-Binding existiert (prüfen!)
  - doppelte Command-Registrierung: `InitializeCommands()` und `InitializeQualityMetrics()` registrieren `RecalculateMetricsCommand`/`ExportMetricsCommand` beide
  - `Properties/Settings.Designer.cs` + `app.config`, falls ungenutzt
- ⚠️ **Bewusste Entscheidung, kein stilles Refactoring** (Verhaltensänderung): der Finalizer `~DitherStatisticsVM()` ruft `Dispose()` auf dem Finalizer-Thread (Dispatcher-/Timer-Zugriffe dort sind riskant). Empfehlung: entfernen — aber nur nach explizitem OK des Maintainers.
- Doku: CLAUDE.md-Architekturabschnitt an die neue Struktur anpassen; CHANGELOG-Eintrag optional (reines Refactoring, Versions-Bump nicht erforderlich — Entscheidung beim Maintainer)

**Verifikation:** `dotnet test`; voller NINA-Smoketest aller Features (Charts, Statistik, Quality-Panel, Optimizer, Export, Profile, Persistenz-Restart).

✅ **`Services/ExportService.cs`** übernimmt `ExportDitherEventsCsv`/`ExportQualityReport` (identische
Pfade `Documents\N.I.N.A\DitherStatistics\DitherEvents_<timestamp>.csv` bzw. `DitherQuality_<timestamp>.txt`
und identisches CSV-/Report-Format); die VM behält Leer-Guard, Try/Catch und Log-Meldungen an den
Aufrufstellen. **`DitherStatistics.Aggregate`** (neu in `Services/Statistics.cs`) ist die reine Funktion
hinter `UpdateStatistics` (Avg/Median/Min/Max/StdDev der erfolgreichen Settle-Times, Success-Rate,
Drift-Range aus den Pixel-Shift-Punkten) und gibt einen `StatisticsSummary`-Wert zurück, den die VM 1:1
auf die Properties mappt; das bedingte `UpdateSettleTimeChart()` (nur wenn mind. eine erfolgreiche
Serie vorliegt) bleibt als Verhalten in der VM erhalten. **`Services/PixelScaleService.cs`** ist die
reine (Logger-freie) Berechnung von `GetPixelScaleRatio` — manueller Override > NINA-GuiderInfo >
PHD2-Pixelskala > Fallback 1.0 — als `Result`-Wert (`Ratio`, `Source`, optional `ImplausibleWarning`/
`FallbackReason`); die VM entscheidet anhand dieser Felder weiterhin selbst, wann sie warnt bzw. das
einmalige Fallback-Log (`hasLoggedRatioFallback`) schreibt, identische Lognachrichten.
Tote Symbole entfernt: `Random random` (ungenutzt), `XFormatter`/`YFormatter`/`SettleTimeFormatter`
(kein XAML-Binding gefunden, geprüft), die doppelte Command-Registrierung (`InitializeQualityMetrics()`
registrierte `RecalculateMetricsCommand`/`ExportMetricsCommand` ein zweites Mal nach `InitializeCommands()`
— Methode und Aufruf entfernt, Registrierung bleibt einmalig in `InitializeCommands()`), sowie
`Properties/Settings.Designer.cs`, `Properties/Settings.Settings` und `app.config` (verifiziert: keine
Referenz im Code außer der Designer-Datei selbst; `app.config` enthielt nur Boilerplate-Bindungsumleitungen
und eine ungenutzte EntityFramework/SQLite-Sektion aus einer Projektvorlage).
⚠️ Finalizer `~DitherStatisticsVM()` nach expliziter Rückfrage beim Maintainer entfernt (Dispose läuft
jetzt nur noch über den regulären NINA-Dispose-Pfad, kein Dispatcher-/Timer-Zugriff mehr vom
Finalizer-Thread).
CLAUDE.md-Architekturabschnitt komplett auf die neue Ordnerstruktur (`Models/`, `Phd2/`, `Services/`,
`ViewModels/`, `Views/`, `DitherStatistics.Tests/`) umgeschrieben, inkl. der Logger-frei-Konvention für
extrahierte pure-math/IO-Services; "There are no unit tests" korrigiert. CHANGELOG-Eintrag bewusst
ausgelassen (reines internes Refactoring ohne Verhaltensänderung außer dem Finalizer, kein Versions-Bump).
8 neue Tests (`DitherStatistics.Tests/StatisticsTests.cs` + neue `PixelScaleServiceTests.cs`):
`Aggregate` ohne Events, gemischte Erfolg/Misserfolg-Serie (inkl. `SettleTime == null` ausgeschlossen),
Drift-Range aus Pixel-Shift-Punkten; `PixelScaleService.Calculate` für manuellen Override, NINA- vs.
PHD2-Quelle, fehlende Eingaben (Fallback-Grund) und unplausible Ratio (Fallback-Warnung).
`dotnet test`: 98 grün (90 + 8 neu). Build Debug + Release 0 Fehler (nur die vorbestehenden
NU1701-Warnungen). Voller NINA-Smoketest steht noch aus und muss vom Maintainer durchgeführt werden.

---

## 6. Verifikations-Checkliste (pro Etappe abhaken)

1. `dotnet build DitherStatistics.sln -c Debug` fehlerfrei
2. `dotnet build DitherStatistics.sln -c Release` fehlerfrei
3. `dotnet test` grün (ab Etappe 2)
4. JSON-Kontrakt-Tests grün → Persistenzformat unverändert
5. Bei UI-/Verhaltensrelevanz: NINA-Smoketest (Panel, Charts, Toggles, Profilwechsel, Restart mit Persistenz, Dither-Verarbeitung mit PHD2)
6. Commit mit aussagekräftiger Message

## 7. Ergebnis nach Etappe 8

```
DitherStatistics/
├── DitherStatisticsPlugin.cs          (nur noch Plugin-Manifest)
├── Models/
│   ├── DitherEvent.cs, PixelShiftPoint.cs, PersistedStatisticsData.cs
│   ├── DitherDataPoint.cs, DitherSeriesInfo.cs, DitherAnalysisSnapshot.cs
│   └── DitherSettingsRecommendation.cs
├── Phd2/
│   ├── PHD2Client.cs                  (reiner Protokoll-Client)
│   ├── Phd2EventArgs.cs
│   └── Phd2ConnectionManager.cs
├── Services/
│   ├── Statistics.cs                  (pure Statistik-Helfer + Aggregation)
│   ├── DitherQualityMetrics.cs        (unverändert pure)
│   ├── DitherAnalysis.cs              (pure Optimizer-Mathematik)
│   ├── DitherOptimizerService.cs      (Zustandsmaschine, Diagnose-Dateien)
│   ├── PluginSettingsStore.cs
│   ├── StatisticsProfileService.cs
│   ├── PixelScaleService.cs
│   ├── ExportService.cs
│   ├── NinaThemeWatcher.cs
│   └── *ChartRenderer.cs
├── ViewModels/
│   └── DitherStatisticsVM.cs          (Koordinator: Bindings, Commands, Verdrahtung)
├── Views/
│   └── DitherStatisticsView.xaml(.cs)
└── DitherStatistics.Tests/            (xUnit)
```

Tatsächliches Ergebnis nach Etappe 8: Das VM sinkt von 2267 auf 1636 Zeilen — mehr als die ursprünglich
geschätzten 600–800, weil die Properties/Bindings/Commands/IDockableVM-Boilerplate allein schon einen
Grossteil ausmacht (Recommendation-Property-Kaskaden, IDockableVM-Properties, ~15 reine Anzeige-Properties
für den Optimizer) und bewusst nicht weiter aufgespalten wurde, um die Etappe mechanisch/niedrig-riskant
zu halten. `PHD2Client` liegt bei 548 Zeilen (Ziel ~600 erreicht). Die gesamte Empfehlungs-, Statistik-,
Persistenz- und Analyse-Logik ist ohne NINA/UI/Netzwerk testbar (98 Tests in `DitherStatistics.Tests`).
