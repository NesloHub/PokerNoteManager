# ♠ Poker Notes (v2.0)

Et lille, hurtigt **notatprogram** til pokerspillere. Ingen live-stats, ingen HUD, ingen OCR —
kun spillere, tags, notater, historik og eksport.

Programmet er en omskrivning af det tidligere PokerVision HUD: alt tracker-/statistik-arbejde er
fjernet, og der er lagt vægt på at det skal føles let og aldrig forstyrre mens man skriver.

---

## 🚀 Kom i gang

1. Programmet åbner den database det sidst brugte (stien huskes i `%AppData%\PokerNoteManager_Settings.txt`).
2. Vælg en spiller i listen til venstre — eller tryk **＋ New player** (Ctrl+N).
3. Skriv notatet. Det gemmer sig selv ca. et sekund efter du holder op med at skrive.
   `saved HH:MM:SS` ud for navnet bekræfter.

---

## 🪟 Vinduet

| Område | Indhold |
| :--- | :--- |
| **Værktøjslinje** | Søgefelt, `＋ New player`, `🏷 Tags`, `📂 Open DB`, `⬇ Export CSV`, `?` (genveje) |
| **Venstre kolonne** | Tag-filtre (chips) + spillerlisten med tag-oversigt og notat-preview |
| **Højre kolonne** | Editor: navn (+ Rename / History / Delete), aliases, tags, notater, `Save note copy` / `Save now` |
| **Statuslinje** | Stien til databasen + antal spillere og tags |

* **Søgning** (Ctrl+F) rammer navn, aliases **og** selve notatteksten.
* **Tag-chips** i editoren sætter/fjerner tags med ét klik (fyldt = spilleren har tag'en).
* **Tag-filtre** under `PLAYERS` viser kun spillere med en bestemt tag.

---

## 📸 Skærmbillede, capture og hover-bokse

Programmet kan læse spillernavnene fra bordet og vise notaterne ovenpå — uden statistik og
**uden baggrundsscanning** (der hentes kun billeder når du selv trykker).

| Knap | Virker sådan |
| :--- | :--- |
| **Skærmvælger** | Vælg hvilken skærm der skal kigges på (valget huskes). Ved museklik/scan bruges skærmen under musen automatisk |
| **AUTO** | `Auto: off` / 15 s / 30 s / 1 min / 2 min — scanner bordene igen på en timer, så boksene følger spillere der kommer og går. Huskestillingen gemmes. Boksene du fjernede med Ctrl+klik forbliver fjernet |
| **📸 Capture & Scan** | Middel musetast eller knappen: finder alle vinduer på skærmen, tjekker for grøn filt (browser/lobby springes over - har bordet ikke filt, afgør vinduets titel), læser navnene med OCR og sætter en hover-boks over hvert sæde |
| **Hover-boks** | Grøn = spilleren findes → notatet (tags + tekst) vises i et kort ved musen. Grå stiplet med `?` = navnet er læst men findes ikke i databasen |
| **Klik på en boks** | Åbner note-editoren **ude på bordet**: skriv notatet, sæt/fjern tags med et klik og gem med knappen eller Ctrl+Enter. Nye navne oprettes automatisk, og det gamle notat ryger i historikken. Esc eller klik udenfor lukker |
| **Ctrl+klik på en boks** | Fjerner **den enkelte** boks (fx en forkert OCR-læsning) — og kun på **det bord** den ligger på. Den bliver væk indtil du trykker 🧹 Clear eller genstarter. (Højreklik bruges **ikke**, for det folder på Unibet) |
| **Samme spiller på flere borde** | Hver boks hører til sit bord: noten er fælles, men fjerner du en boks på bord A, bliver boksen på bord B stående. Hover-kortet viser `ruhhy · at 2 tables`, og statuslinjen tæller dem med |
| **Note-editoren** | Klikker du en boks for en spiller hvis editor allerede er åben, kommer den samme editor frem igen — der smides ikke skrevet tekst væk. Klikker du en anden spiller, gemmes det du har skrevet først. `Open` åbner spilleren i hovedvinduet |
| **🗂 DB only: ON/OFF** | Viser kun boks om spillere der allerede findes i databasen — nye navne ignoreres helt. Huskestillingen gemmes |
| **🏷 Farve på tags** | Boksens ramme får farven fra spillerens første tag, og alle tags skrives ved navnet i deres egen farve (mørke farver lysnes, så de kan læses) |
| **👁 Boxes: ON/OFF / 🧹 Clear** | Skjul/vis eller fjern boksene |
| **🔎 Snapshot** | Gemmer de fangede borde med boksene + OCR-læsningerne tegnet på, i `%LocalAppData%\PokerVisionHUD\debug_snapshots\` |
| **📋 Paste & Scan** | Scanner et skærmbillede fra udklipsholderen og viser navnene som klikbare chips |
| **✂️ Snip Player** | Læser navnet under musen og åbner/opretter spilleren |

**Sådan undgår boksene at forstyrre spillet eller skrivningen** (det var netop her den gamle
tracker stjal fokus):

* Overlayet er **klik-transparent** (`WS_EX_TRANSPARENT`) — alle klik går videre til pokerklienten.
* Det kan **ikke tage fokus** (`WS_EX_NOACTIVATE` + `ShowActivated = false`) og sættes kun i
  forgrunden med `SWP_NOACTIVATE`.
* **Ingen timere:** boksene tegnes kun når du trykker Capture.
* Hover og klik fanges af en global musekrog der kun *lytter* (til middel musetast og hover),
  så almindelige klik og tastetryk er urørte.

Navnematching er tolerant (OCR-homoglyffer: `1/l/i`, `0/o`, `5/s`, `8/b`, `3/e` samt
Levenshtein-afstand), så `ug7z` matcher `U87` og `ninja tin` matcher `ninjatin`.

---

## 🏷 Tags og farver

* Administration under **🏷 Tags**: opret tags, vælg farve som hex (`#FF8C00`), slet tags.
* Sletning af en tag fjerner den fra alle spillere (efter bekræftelse).
* Farverne bruges på chips i editoren, i filterlinjen og i spillerlisten.
* Gemmes i `%AppData%\PokerNoteManager_Tags.json` (samme fil og format som før).

---

## 🗂 Aliases, historik og eksport

* **Aliases** — andre nick til samme spiller, adskilt med komma. Søgningen matcher dem også.
* **Historik** — `💾 Save note copy` (Ctrl+E) gemmer en tidsstemplet kopi af notatet.
  `🕘 History` viser kopierne; vælg én og tryk `↩ Load into editor` for at hente den tilbage.
  Maks 40 kopier pr. spiller.
* **CSV-eksport** — alle spillere med aliases, tags, notater og antal historik-kopier
  (semikolon-adskilt, UTF-8 med BOM, klar til Excel).

---

## ⌨️ Genveje

| Genvej | Handling |
| :--- | :--- |
| `Ctrl+N` | Ny spiller |
| `Ctrl+F` | Hen til søgefeltet |
| `Ctrl+S` | Gem nu |
| `Ctrl+E` | Gem en kopi af notatet i historikken |
| `F2` | Omdøb valgt spiller |
| `Esc` | Luk dialog / ryd søgningen |

---

## 📄 Data og filer

Database (`.json`, version 1 — samme format som det tidligere program skrev):

```json
{
  "version": 1,
  "players": {
    "ninjatin": {
      "tags": ["fish"],
      "notes": "mintclick in 3bet pots ...",
      "note_history": [ { "when": "2026-09-23 12:58", "text": "..." } ],
      "aliases": ["ninjatin2"]
    }
  }
}
```

* Ukendte felter (fx `stats` fra den gamle version) ignoreres ved læsning og skrives ikke mere.
* **Atomisk gemning**: der skrives først til en `.tmp`-fil, som derefter flyttes på plads.
* **Én `.bak`** pr. gemning bevares ved siden af databasen.

| Fil | Sted |
| :--- | :--- |
| Database | Vælges med `📂 Open DB` — stien huskes |
| Tags og farver | `%AppData%\PokerNoteManager_Tags.json` |
| Seneste database-sti | `%AppData%\PokerNoteManager_Settings.txt` |
| Vinduets størrelse/placering | `%AppData%\PokerNotes_Window.txt` |
| Log (kun opstart/aabninger/fejl) | `%LocalAppData%\PokerVisionHUD\pokervision_debug.log` |

---

## 🔇 Fokus forstyrres ikke mere

Det tidligere program kørte en baggrundsscanning på en timer, som løbende oprettede og opdaterede
HUD-vinduer oven på pokerbordene. Hver opdatering satte vinduet `Topmost` og aktiverede det
(`ShowActivated = true`) — og netop dét stjal fokus fra notatfeltet, mens man skrev.

I denne version findes der:

* ingen timere der opretter eller aktiverer vinduer
* ingen baggrundsscanning (kun når du selv trykker Capture / middel musetast)
* ingen statistik, ingen tracker, intet der skriver til databasen af sig selv
* intet der kalder `Activate()` på et vindue (kun når du selv Ctrl+klikker en boks)

Den eneste baggrundsaktivitet er den udskudte fil-gemning (1,2 sek. efter sidste tastetryk), og den
rører hverken fokus eller andre vinduer. Alle dialoger (tags, historik, navn, hjælp, scan-resultat)
er paneler *inde i* hovedvinduet — der åbnes aldrig et ekstra vindue under arbejdet, bortset fra
hover-overlayet, som er klik-transparent og ikke kan tage fokus.

---

## 🛠️ Udvikling: build og udgivelse

### Build-output ligger på E:

`Directory.Build.props` i repo-roden omdirigerer alt build-output, så C: ikke fyldes op:

| | Sti |
| :--- | :--- |
| Intermediate (`obj`, NuGet-assets) | `E:\Build\PokerNoteManager\obj\` |
| Output (`bin`, alle konfigurationer) | `E:\Build\PokerNoteManager\bin\` |

### Byg

```powershell
dotnet build .\PokerNoteManager\PokerNoteManager.csproj -c Debug
```

Debug-exe'en ligger herefter i `E:\Build\PokerNoteManager\bin\Debug\net10.0-windows\win-x64\PokerVisionHUD.exe`.

### Udgiv release (portabel single-file)

```powershell
powershell -ExecutionPolicy Bypass -File tools\release_notes.ps1   # publish + kopiér + zip
powershell -ExecutionPolicy Bypass -File tools\zip_release.ps1     # kun zip
```

Scriptet publicerer `-r win-x64 --self-contained true -p:PublishSingleFile=true`, kopierer exe'en
til `E:\Poker Tools\PokerVisionHUD_v1.0\`, fjerner OCR-data (`tessdata`/`x64`) fra release-mappen,
laver en backup af den forrige zip i `E:\Poker Tools\Backups\` og genopbygger
`PokerVisionHUD_v1.0.zip` (via `ZipFile.CreateFromDirectory` — `Compress-Archive` udelod
under-mapper). Genvejene på skrivebordet peger stadig på den samme exe.

### Projekt-indhold

| Fil | Rolle |
| :--- | :--- |
| `MainWindow.xaml` / `.xaml.cs` | Al UI: liste, editor, chips, overlays, scan-knapper, genveje, gemning |
| `NotesData.cs` | Model + fillag (`NotesFile`, `PlayerEntry`, `TagDef`, `NotesStore`) |
| `Vision\ScreenCapture.cs` | Skærme, vinduesliste, BitBlt-capture + modeltyper (`SeatBox`, `ScanOutcome`) |
| `Vision\TableScanner.cs` | Filt-detektion, OCR af navneskilte, navne-matching (homoglyffer + Levenshtein) |
| `Vision\HoverOverlay.cs` | Klik-transparent hover-overlay med boks og notatkort |
| `Vision\NotePopup.xaml` / `.xaml.cs` | Note-editoren der åbner ude på bordet når man klikker på en boks |
| `Vision\GlobalMouseHook.cs` | Musekrog: middel musetast = capture, hover/klik til boksene |
| `PvLog.cs` | Logning til ét sted (opstart og fejl) |
| `ModernMessageBox.*` | Bekræftelses-/beskedsdialoger i samme stil |
| `App.xaml.cs` | Enkelt-instans, globale fejlhandlers, opstart |
| `tools\release_notes.ps1`, `zip_release.ps1` | Release- og zip-scripts |
| `tools\verify_*.ps1`, `analyze_*.ps1` | Målescripts mod rigtige skærmskud (bruges til at tune zoner/OCR) |

Pakker: `OpenCvSharp4` (+ `runtime.win`, `WpfExtensions`) og `Tesseract` 5.2.0. OCR-data
(`tessdata\eng.traineddata`, 23 MB) og Tesseract-DLL'erne i `x64\` skal ligge ved siden af exe'en —
det sørger `tools\release_notes.ps1` for.

**Vigtigt:** `Tesseract.dll` må **ikke** pakkes ind i single-file exe'en. Tesseracts native loader
(InteropDotNet) finder `x64\tesseract50.dll` via `Assembly.Location`, som er tom for assemblies
inde i en bundle → `Value cannot be null (Parameter 'path1')`. Derfor holder csproj-target
`KeepTesseractOutsideSingleFile` den udenfor, og `PrepareNativePaths()` i `App.xaml.cs` sætter
arbejdsmappen til exe-mappen, tilføjer `x64` til DLL-søgestien og forindlæser de to native DLL'er.

Fejlfinding af netop dette: `PokerVisionHUD.exe --selftest-ocr` skriver resultatet (og hvilke
mapper der blev søgt i) til `%LocalAppData%\PokerVisionHUD\selftest.txt`.

Programmet har fire selvtest-switches, som alle skriver til den samme `selftest.txt` og afslutter
sig selv bagefter. De tre sidste lægger desuden et PNG i
`%LocalAppData%\PokerVisionHUD\debug_snapshots\`, så man kan se resultatet uden at klikke:

| Switch | Kontrollerer |
| :--- | :--- |
| `--selftest-ocr` | At OCR- og OpenCV-motoren starter i den udgivne build |
| `--selftest-note` | At note-editoren kan bygges, vise sig, rende og gemme (trykker "Save" programmatisk) |
| `--selftest-ui` | At hovedvinduet kan bygges og tegnes op (`ui_main_window.png`) |
| `--selftest-overlay` | At hover-boksene tegnes med tag-farver og navne (`selftest_hover_overlay.png`) |
| `--selftest-mouse` | At musekrogen fanger venstre-, Ctrl+venstre- og midterklik (sender rigtige klik til et tomt testvindue) |
| `--selftest-screens` | At boksene rammer rigtigt på **hver** skærm, også en skærm med negativt x (til venstre for hovedskærmen). Renderer et PNG pr. skærm |

### Round-trip-test af datalaget

`E:\Build\NotesRoundTripTest\` (bevidst uden for repo'et, fordi `Directory.Build.props` deler én
`obj`-mappe for alt under repo-roden): et lille konsol-harness der linker `NotesData.cs` + `PvLog.cs`,
ændrer én spiller i en **kopi** af den rigtige database, gemmer og genindlæser.

```powershell
dotnet run --project E:\Build\NotesRoundTripTest\NotesRoundTrip.csproj
```

Den kontrollerer 49 ting: at notater/tags/aliases/historik gemmes, at `stats` og `watch` fra den
gamle version **bevares**, at der laves `.bak`, at der ikke efterlades `.tmp`, at programmets
indstillinger ikke røres, hvilke vinduestitler der må læses uden grøn filt, at shell-vinduer
(IME, input-panel, proceslinje) afvises, at "Snip Player"-klippet kan læses fra et bordvindue, og
hvilke hover-bokse der tegnes (kun-database-filteret, højreklik-fjernelse **pr. bord**, og at samme
spiller på flere borde tælles og behandles hver for sig).

---

## 🖥️ Skærme, opløsninger og DPI

Programmet er **per-monitor DPI aware** (`app.manifest`: `PerMonitorV2`) og regner alt i fysiske
pixels. Det betyder:

* **Flere skærme i alle opsætninger**, også en skærm med negativt x/y (til venstre for eller over
  hovedskærmen). Bokse tegnes på den skærm sædet ligger på, og hover-kortet holder sig inden for
  skærmen.
* **Forskellig DPI pr. skærm** (fx 100 % + 150 %): overlayet regner om med den skærmens skala, og
  note-editoren placerer sig efter DPI'en på den skærm boksen ligger på.
* **Skærme der ændres mens programmet kører** (til-/frakobling, opløsningsskift): programmet lytter
  på `DisplaySettingsChanged`, læser skærmlisten igen og bygger hover-lagene forfra.
* **Små skærme/vinduer**: knap-rækken i scan-baren **ombryder**, så ingen knap kan blive klemt
  usynlig, og editoren er **scrollbar**, så notatfeltet og gem-knapperne altid kan nås (testet ned
  til vinduets minimum 880x520).
* **Vinduesplacering**: gemt vinduesstørrelse/-position klemmes ind i den skærm der findes lige nu,
  så programmet ikke kan starte "uden for skærmen" efter et skærmskift.
* **Logfilen roterer** ved 2 MB (`pokervision_debug.log.1`), så den ikke vokser ubegrænset.

Der er en selvtest til netop dette: `--selftest-screens` tegner tre bokse på hver skærm og gemmer et
PNG pr. skærm i `%LocalAppData%\PokerVisionHUD\debug_snapshots\`.

---

* **v2.1** — optimering og fejlretning før udgivelse: multi-skærm/DPI-robusthed (skærmskift mens
  programmet kører, klemmende vinduesplacering, DPI-korrekt note-editor), knap-række der ombryder og
  scrollbar editor på små skærme, valgfri **AUTO-scan** på timer, rotation af logfilen, OCR-motoren
  oprettes kun én gang (trådsikker), og en hjælpetekst hvis OCR-filerne mangler. Ny `--selftest-screens`.
* **v2.0.1** — capture-laget tilbage (skærmvælger, hover-bokse, note-editor ude på bordet) plus
  rettelser efter test på Unibets grå bordtema: vindues-capture i stedet for skrivebords-capture,
  filten gjort valgfri, kun database-filter, tag-farver på boksene, og et crash i note-editorens
  lukning fjernet ("Cannot set Visibility ... while a Window is closing").
* **v2.0** — notatprogram: HUD, tracker, OCR og alle stats fjernet. Ny editor med tags, aliases,
  historik, CSV-eksport, autosave, enkelt-instans og fokus-venligt UI.
* **v1.0** — PokerVision HUD (tracker-versionen) findes som backup:
  `E:\Poker Tools\Backups\PokerVisionHUD_HUD_build_20260922_2013.exe`
