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
| 4 | Statistik-Persistenz & Profilverwaltung | **hoch** (Architektur) | **Fable oder Opus** |
| 5 | Optimizer-Mathematik pur extrahieren | niedrig (mechanisch, testabgesichert) | Sonnet |
| 6 | DitherOptimizerService / PHD2-Split | **hoch** (Architektur, Kernstück) | **Fable** (bei Split: 6a Sonnet, 6b Fable) |
| 7 | Chart-Rendering & Theme | niedrig (mechanisch, UI-Thread beachten) | Sonnet |
| 8 | VM-Restentflechtung & Aufräumen | niedrig | Sonnet |

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

## Etappe 3 — SettingsService

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

---

## Etappe 4 — Statistik-Persistenz & Profilverwaltung

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

---

## Etappe 5 — Optimizer-Mathematik pur extrahieren

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

---

## Etappe 6 — DitherOptimizerService, PHD2Client wird reiner Protokoll-Client

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

---

## Etappe 7 — Chart-Rendering & Theme aus dem VM

**Komplexität: niedrig (mechanisch — UI-Thread-Regeln beachten) | Modell: Sonnet**

- Neu: `Services/NinaThemeWatcher.cs` — `GetThemeColor`-Lookup (Application/MainWindow-Resources, Brush oder Color), DispatcherTimer-Polling alle 500 ms (Verhalten identisch), Event bei Farbwechsel; Dispose stoppt den Timer
- Neu: `Services/PixelShiftChartRenderer.cs` + `Services/SettleTimeChartRenderer.cs` (oder eine Datei): übernehmen `UpdatePixelShiftChart`, `UpdateSettleTimeChart`, `UpdateChartColors` — nehmen `WpfPlot` + Daten + Theme-Farbe entgegen, kein VM-Zugriff
- Tooltip-Logik (`AttachPixelShiftTooltip`, `AttachSettleTimeTooltip`, Nächster-Punkt-Suche mit 5 %-/10 %-Schwellen) in einen Helper; die Tooltip-Text/Visible-Properties bleiben im VM (Bindings)
- **Bleibt im VM**: die Lazy-Property-Getter von `PixelShiftPlot`/`SettleTimePlot` — die Erzeugung auf dem UI-Thread via XAML-Binding ist der tragende Mechanismus; die Getter delegieren Styling/Tooltip-Attach an die Services

**Verifikation:** Build; NINA-Smoketest: beide Charts rendern, Tooltips funktionieren, NINA-Theme wechseln → Chartfarben ziehen innerhalb ~1 s nach.

---

## Etappe 8 — VM-Restentflechtung & Aufräumen

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

Das VM sinkt von 2267 auf grob geschätzt 600–800 Zeilen (Properties/Bindings/IDockableVM bleiben), `PHD2Client` von 1489 auf ~600. Die gesamte Empfehlungs-, Statistik-, Persistenz- und Analyse-Logik ist ohne NINA/UI/Netzwerk testbar.
