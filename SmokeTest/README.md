# Automatisierte Funktionstests (Smoketest)

Drei Stufen ersetzen den manuellen Testablauf (PHD2 starten, Guiding aufsetzen,
NINA + Sequencer, Dithers abwarten, GUI prüfen). Kernidee: Das Plugin hört auf den
PHD2-Socket, nicht auf NINA — Dithers werden direkt über PHD2s JSON-RPC-API
(Port 4400) ausgelöst. Sequencer, NINA-Hardware-Connect und echte Hardware sind
nicht nötig.

| Stufe | Was läuft | Was sie prüft | Dauer |
|---|---|---|---|
| 1 | `dotnet test` (immer) | Socket → `PHD2Client`-Parsing → `DitherOptimizerService`, deterministisch gegen einen Fake-PHD2-Server (`FakePhd2Server.cs`, `Phd2EndToEndTests.cs`) | Sekunden |
| 2 | `dotnet test` bei laufendem PHD2 | Kompatibilität mit dem echten PHD2-Wire-Format inkl. echtem Dither-Roundtrip (`Phd2IntegrationTests.cs`, Auto-Skip ohne PHD2) | ~3 min |
| 3 | `Run-SmokeTest.ps1` | Plugin im echten NINA: MEF-Load, Panel/Charts, PHD2-Auto-Connect, Persistenz, Diagnose-Dateien | ~10–20 min |

## Einmaliges Setup

**PHD2** (für Stufe 2 + 3):

1. Ausrüstungsprofil **`Simulator`** anlegen: Kamera = *Simulator*, Montierung = *On-camera*.
2. *Tools → Enable Server* aktivieren (im Profil gespeichert).
3. Einmal manuell kalibrieren, dann in den Guiding-Einstellungen
   **„Auto restore calibration"** aktivieren — Folgeläufe überspringen die Kalibration.

**NINA** (für Stufe 3):

1. Plugin ist installiert (macht der Build automatisch via PostBuild).
2. Im **Imaging-Tab** das *Dither Statistics*-Panel einmal sichtbar anordnen —
   NINA merkt sich das Panel-Layout. Den Tab selbst wechselt das Skript
   automatisch (NINA startet immer im Equipment-Tab; der Wechsel läuft über
   UI-Automation, bei lokalisiertem NINA `-ImagingTabLabel` anpassen).

Standardpfade (per Parameter übersteuerbar): PHD2 `%ProgramFiles(x86)%\PHDGuiding2\phd2.exe`,
NINA `%ProgramFiles%\N.I.N.A. - Nighttime Imaging 'N' Astronomy\NINA.exe`.

## Verwendung

```powershell
# Stufe 1 (+ 2, falls PHD2 guidet): läuft bei jedem Test-Durchlauf mit
dotnet test DitherStatistics.sln

# Stufe 2 gezielt: erst PHD2-Simulator-Guiding starten, dann testen
.\SmokeTest\Start-Phd2Guiding.ps1
dotnet test DitherStatistics.sln --filter "Category=Integration"

# Stufe 3: Schnelllauf (5 Dithers) bzw. voller Lauf (20 Dithers)
.\SmokeTest\Run-SmokeTest.ps1 -DitherCount 5 -ReferenceWaitSec 30
.\SmokeTest\Run-SmokeTest.ps1
```

`Run-SmokeTest.ps1` macht: NINA beenden → Build + Deploy → PHD2-Sim-Guiding →
NINA starten → auf Plugin-Connect warten (NINA-Log) → Guiding-Neustart (Plugin
sieht `StartGuiding`) → Referenzfenster füllen → N × `dither`-RPC mit
SettleDone-Wait → Screenshots → NINA schließen → Assertions. Exit-Code 0 = grün.

Wichtige Schalter: `-SkipBuild`, `-KeepPhd2` (PHD2 nicht beenden), `-KeepNina`,
`-Configuration Debug`, `-ImagingTabLabel` (Label des Imaging-Tabs bei
lokalisiertem NINA).

**Assertions von Stufe 3:**

- NINA-Log enthält keine `ERROR`-Zeilen aus Plugin-Klassen.
- `%LocalAppData%\NINA\DitherStatistics\*_dither_analysis.txt` wurde in diesem
  Lauf geschrieben und enthält ≥ N Dither-Serien.
- Das aktive Profil-JSON (`profiles\<name>.json`) hat **exakt N neue**
  `DitherEvents`, alle plausibel (Success, `0 < SettleTime ≤ Timeout+30 s`,
  Pixel-Shift vorhanden).

Artefakte (Screenshots, Report, Log- und Datei-Kopien) landen in
`SmokeTest\artifacts\<timestamp>\`. Screenshots: nach 5 Dithers (Panel-Oberteil),
am Ende `nina_final_top.png` (Charts) und `nina_final_bottom.png` (per Mausrad
ans Panel-Ende gescrollt: Quality-Metriken, Optimizer-Empfehlungen, Actions —
das Rad wird bewusst über einem Text-Bereich gesendet, über den Charts würde es
zoomen statt scrollen). Das Skript aktiviert die Statistik-Persistenz des Plugins
für den Lauf und stellt die ursprüngliche Einstellung danach wieder her.

## Was bewusst manuell bleibt

- **Visuelle Chart-Korrektheit** (ScottPlot-Rendering) — nur über die Screenshots
  sichtkontrollierbar.
- **Theme-Wechsel** (`NinaThemeWatcher`), **Options-Seite**, **Clear-Data-Button**
  (GUI-Interaktion; wäre mit FlaUI automatisierbar, bewusst weggelassen).
- **Pixel-Scale über NINA-GuiderInfo** (`IGuiderConsumer`) — bräuchte einen
  NINA-Guider-Connect (z. B. via ninaAPI-Plugin); der PHD2-`get_pixel_scale`-Pfad
  ist abgedeckt.
- Echte Hardware / echter Himmel.

## Fehlersuche

- *„PHD2 profile 'Simulator' not found"* → Einmal-Setup oben; anderer Name via
  `-Phd2ProfileName`.
- *„server (port 4400) did not come up"* → *Enable Server* im PHD2-Profil fehlt.
- Stufe-2-Tests werden geskippt → PHD2 läuft nicht oder guidet nicht
  (`Start-Phd2Guiding.ps1` ausführen).
- Kalibration schlägt in der Simulator-Zeitlupe fehl → im PHD2-Simulator-Profil
  Deklination ≈ 0 lassen (Standard) und Belichtung 1 s.
