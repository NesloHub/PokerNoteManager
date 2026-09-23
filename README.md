# ♠ Poker Notes (v2.0)

A small, fast **note-taking app** for poker players. No live stats, no HUD, no OCR —
just players, tags, notes, history and export.

It is a rewrite of the earlier PokerVision HUD: all tracker and stats work has been removed,
and the priority is that it feels light and never disturbs you while you type.

---

## 🚀 Getting started

1. The app opens the database you used last (the path is remembered in `%AppData%\PokerNoteManager_Settings.txt`).
2. Pick a player in the list on the left — or press **＋ New player** (Ctrl+N).
3. Type the note. It saves itself about a second after you stop typing.
   `saved HH:MM:SS` next to the name confirms it.

---

## 🪟 The window

| Area | Content |
| :--- | :--- |
| **Toolbar** | Search box, `＋ New player`, `🏷 Tags`, `📂 Open DB`, `⬇ Export CSV`, `?` (shortcuts) |
| **Left column** | Tag filters (chips) + the player list with tag summary and note preview |
| **Right column** | Editor: name (+ Rename / History / Delete), aliases, tags, notes, `Save note copy` / `Save now` |
| **Status bar** | Path to the database + number of players and tags |

* **Search** (Ctrl+F) matches the name, the aliases **and** the note text itself.
* **Tag chips** in the editor set/remove tags with one click (filled = the player has the tag).
* **Tag filters** under `PLAYERS` show only players with a given tag.

---

## 📸 Screenshot, capture and hover boxes

The app can read the player names off the table and show your notes on top — without stats and
**without background scanning** (images are only grabbed when you press something yourself).

| Button | How it works |
| :--- | :--- |
| **Screen picker** | Choose which screen to look at (the choice is remembered). On a mouse click/scan the screen under the mouse is used automatically |
| **AUTO** | `Auto: off` / 15 s / 30 s / 1 min / 2 min — rescans the tables on a timer so the boxes follow players who come and go. The setting is remembered. Boxes you removed with Ctrl+click stay removed |
| **📸 Capture & Scan** | Middle mouse button or the button: finds every window on the screen, checks for green felt (browsers/lobbies are skipped - if the table has no felt, the window title decides), reads the names with OCR and places a hover box over each seat |
| **Hover box** | Green = the player exists → the note (tags + text) is shown in a card next to the mouse. Grey and dashed with `?` = the name was read but is not in the database |
| **Click a box** | Opens the note editor **out on the table**: type the note, set/remove tags with one click and save with the button or Ctrl+Enter. New names are created automatically, and the previous note goes into the history. Esc or a click outside closes it |
| **Ctrl+click a box** | Removes **that single** box (e.g. a wrong OCR read) — and only on **that table**. It stays gone until you press 🧹 Clear or restart. (Right-click is **not** used, because it folds on Unibet) |
| **Same player at several tables** | Every box belongs to its table: the note is shared, but if you remove a box on table A, the box on table B stays. The hover card shows `ruhhy · at 2 tables`, and the status bar counts them |
| **The note editor** | If you click a box for a player whose editor is already open, the same editor comes back — text you typed is not thrown away. Clicking another player saves what you have written first. `Open` opens the player in the main window |
| **🗂 DB only: ON/OFF** | Shows a box only for players that already exist in the database — new names are ignored completely. The setting is remembered |
| **🏷 Tag colours** | The box border takes the colour of the player's first tag, and every tag is written next to the name in its own colour (dark colours are lightened so they stay readable) |
| **👁 Boxes: ON/OFF / 🧹 Clear** | Hide/show or remove the boxes |
| **🔎 Snapshot** | Saves the captured tables with the boxes + the OCR reads drawn on top, in `%LocalAppData%\PokerVisionHUD\debug_snapshots\` |
| **📋 Paste & Scan** | Scans a screenshot from the clipboard and shows the names as clickable chips |
| **✂️ Snip Player** | Reads the name under the mouse and opens/creates the player |

**How the boxes avoid disturbing the game or your typing** (this was exactly where the old
tracker stole focus):

* The overlay is **click-through** (`WS_EX_TRANSPARENT`) — every click goes through to the poker client.
* It **cannot take focus** (`WS_EX_NOACTIVATE` + `ShowActivated = false`) and is only brought to the
  front with `SWP_NOACTIVATE`.
* **No timers:** the boxes are only drawn when you press Capture.
* Hover and clicks are caught by a global mouse hook that only *listens* (middle mouse button and
  hover), so normal clicks and keystrokes are untouched.

Name matching is tolerant (OCR homoglyphs: `1/l/i`, `0/o`, `5/s`, `8/b`, `3/e` plus Levenshtein
distance), so `ug7z` matches `U87` and `ninja tin` matches `ninjatin`.

---
## 🏷 Tags and colours

* Managed under **🏷 Tags**: create tags, pick a colour as hex (`#FF8C00`), delete tags.
* Deleting a tag removes it from every player too (after a confirmation).
* The colours are used on the chips in the editor, in the filter row and in the player list.
* Stored in `%AppData%\PokerNoteManager_Tags.json` (same file and format as before).

Unused `tag_<number>` leftovers from the old version are not shown in the list.

---

## 🗂 Aliases, history and export

* **Aliases** — other nicks for the same player, separated by commas. Search matches them too.
* **History** — `💾 Save note copy` (Ctrl+E) stores a timestamped copy of the note.
  `🕘 History` lists the copies; pick one and press `↩ Load into editor` to bring it back, or
  `Clear history` to delete them. Max 40 copies per player.
* **CSV export** — every player with aliases, tags, notes and the number of history copies
  (semicolon separated, UTF-8 with BOM, ready for Excel).

---

## ⌨️ Shortcuts

| Shortcut | Action |
| :--- | :--- |
| `Ctrl+N` | New player |
| `Ctrl+F` | Jump to the search box |
| `Ctrl+S` | Save now |
| `Ctrl+E` | Save a copy of the note in the history |
| `F2` | Rename the selected player |
| `Esc` | Close dialog / clear the search |

---

## 📄 Data and files

Database (`.json`, version 1 — the same format the earlier program wrote):

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

* Unknown fields (e.g. `stats` from the old version) are ignored when reading and are no longer written.
* **Atomic saves**: the file is first written to a `.tmp` file, which is then moved into place.
* **One `.bak`** per save is kept next to the database.

| File | Location |
| :--- | :--- |
| Database | Chosen with `📂 Open DB` — the path is remembered |
| Tags and colours | `%AppData%\PokerNoteManager_Tags.json` |
| Latest database path | `%AppData%\PokerNoteManager_Settings.txt` |
| Window size/position | `%AppData%\PokerNotes_Window.txt` |
| Log (startup, database openings and errors only) | `%LocalAppData%\PokerVisionHUD\pokervision_debug.log` |

---

## 🔇 Focus is no longer disturbed

The earlier program ran a background scan on a timer, which continuously created and updated HUD
windows on top of the poker tables. Every refresh set the window `Topmost` and activated it
(`ShowActivated = true`) — and that is exactly what stole focus from the note field while you typed.

This version has:

* no timers that create or activate windows
* no background scanning (only when you press Capture / the middle mouse button)
* no stats, no tracker, nothing that writes to the database by itself
* nothing that calls `Activate()` on a window (only when you Ctrl+click a box yourself)

The only background activity is the deferred file save (1.2 s after the last keystroke), and it
touches neither focus nor other windows. All dialogs (tags, history, name, help, scan result) are
panels *inside* the main window — no extra window is ever opened while you work, apart from the
hover overlay, which is click-through and cannot take focus.

---
## 🛠️ Development: build and release

### Build output lives on E:

`Directory.Build.props` in the repo root redirects all build output, so C: does not fill up:

| | Path |
| :--- | :--- |
| Intermediate (`obj`, NuGet assets) | `E:\Build\PokerNoteManager\obj\` |
| Output (`bin`, all configurations) | `E:\Build\PokerNoteManager\bin\` |

### Build

```powershell
dotnet build .\PokerNoteManager\PokerNoteManager.csproj -c Debug
```

The Debug exe then lives in `E:\Build\PokerNoteManager\bin\Debug\net10.0-windows\win-x64\PokerVisionHUD.exe`.

### Publish a release (portable single file)

```powershell
powershell -ExecutionPolicy Bypass -File tools\release_notes.ps1   # publish + copy + zip
powershell -ExecutionPolicy Bypass -File tools\zip_release.ps1     # zip only
```

The script publishes `-r win-x64 --self-contained true -p:PublishSingleFile=true`, copies the exe to
`E:\Poker Tools\PokerVisionHUD_v1.0\`, removes the OCR data (`tessdata`/`x64`) from the release folder,
makes a backup of the previous zip in `E:\Poker Tools\Backups\` and rebuilds `PokerVisionHUD_v1.0.zip`
(via `ZipFile.CreateFromDirectory` — `Compress-Archive` left the sub folders out). The desktop
shortcuts still point at the same exe.

### Project contents

| File | Role |
| :--- | :--- |
| `MainWindow.xaml` / `.xaml.cs` | All UI: list, editor, chips, overlays, scan buttons, shortcuts, saving |
| `NotesData.cs` | Model + file layer (`NotesFile`, `PlayerEntry`, `TagDef`, `NotesStore`) |
| `Vision\ScreenCapture.cs` | Screens, window list, BitBlt capture + model types (`SeatBox`, `ScanOutcome`) |
| `Vision\TableScanner.cs` | Felt detection, OCR of name plates, name matching (homoglyphs + Levenshtein) |
| `Vision\HoverOverlay.cs` | Click-through hover overlay with boxes and note cards |
| `Vision\NotePopup.xaml` / `.xaml.cs` | The note editor that opens out on the table when you click a box |
| `Vision\GlobalMouseHook.cs` | Mouse hook: middle mouse button = capture, hover/click for the boxes |
| `PvLog.cs` | Logging in one place (startup and errors) |
| `ModernMessageBox.*` | Confirmation/message dialogs in the same style |
| `App.xaml.cs` | Single instance, global error handlers, startup |
| `tools\release_notes.ps1`, `zip_release.ps1` | Release and zip scripts |
| `tools\verify_*.ps1`, `analyze_*.ps1` | Measurement scripts against real screenshots (used to tune zones/OCR) |

Packages: `OpenCvSharp4` (+ `runtime.win`, `WpfExtensions`) and `Tesseract` 5.2.0. The OCR data
(`tessdata\eng.traineddata`, 23 MB) and the Tesseract DLLs in `x64\` must sit next to the exe — that is
what `tools\release_notes.ps1` takes care of.

**Important:** `Tesseract.dll` must **not** be bundled into the single-file exe. Tesseract's native
loader (InteropDotNet) finds `x64\tesseract50.dll` through `Assembly.Location`, which is empty for
assemblies inside a bundle → `Value cannot be null (Parameter 'path1')`. That is why the csproj target
`KeepTesseractOutsideSingleFile` keeps it outside, and `PrepareNativePaths()` in `App.xaml.cs` sets the
working directory to the exe folder, adds `x64` to the DLL search path and preloads the two native DLLs.

Troubleshooting exactly this: `PokerVisionHUD.exe --selftest-ocr` writes the result (and which folders
were searched) to `%LocalAppData%\PokerVisionHUD\selftest.txt`.

The app has a set of self-test switches, all writing to the same `selftest.txt` and closing themselves
afterwards. Most of them also drop a PNG in `%LocalAppData%\PokerVisionHUD\debug_snapshots\`, so you can
see the result without clicking:

| Switch | Checks |
| :--- | :--- |
| `--selftest-ocr` | That the OCR and OpenCV engines start in the released build |
| `--selftest-note` | That the note editor can be built, shown, rendered and saved (presses "Save" programmatically) |
| `--selftest-ui` | That the main window can be built and rendered (`ui_main_window.png`) |
| `--selftest-overlay` | That the hover boxes are drawn with tag colours and names (`selftest_hover_overlay.png`) |
| `--selftest-mouse` | That the mouse hook catches left, Ctrl+left and middle clicks (sends real clicks to an empty test window) |
### Data-layer round-trip test

`E:\Build\NotesRoundTripTest\` (deliberately outside the repo, because `Directory.Build.props` shares one
`obj` folder for everything under the repo root): a small console harness that links `NotesData.cs` +
`PvLog.cs`, changes one player in a **copy** of the real database, saves and reloads.

```powershell
dotnet run --project E:\Build\NotesRoundTripTest\NotesRoundTrip.csproj
```

It checks 49 things: that notes/tags/aliases/history are saved, that `stats` and `watch` from the old
version are **preserved**, that `.bak` is created, that no `.tmp` is left behind, that the app's settings
are not touched, which window titles may be read without green felt, that shell windows (IME, input panel,
taskbar) are rejected, that the "Snip Player" clip can be read from a table window, and which hover boxes
are drawn (the DB-only filter, box removal **per table**, and that the same player at several tables is
counted and handled individually).

---

## 🖥️ Screens, resolutions and DPI

The app is **per-monitor DPI aware** (`app.manifest`: `PerMonitorV2`) and does all maths in physical
pixels. That means:

* **Multiple screens in any layout**, including a screen with negative x/y (to the left of or above the
  main screen). Boxes are drawn on the screen the seat is on, and the hover card stays inside the screen.
* **Different DPI per screen** (e.g. 100 % + 150 %): the overlay recalculates with that screen's scale,
  and the note editor positions itself according to the DPI of the screen the box is on.
* **Screens that change while the app runs** (plug/unplug, resolution change): the app listens to
  `DisplaySettingsChanged`, re-reads the screen list and rebuilds the hover layers from scratch.
* **Small screens/windows**: the button row in the scan bar **wraps**, so no button can be squeezed
  invisible, and the editor is **scrollable**, so the note field and the save buttons can always be reached
  (tested down to the window minimum 880x520).
* **Window placement**: the saved window size/position is clamped into the screen that exists right now,
  so the app cannot start "off screen" after a screen change.
* **The log file rotates** at 2 MB (`pokervision_debug.log.1`), so it does not grow without limit.

There is a self-test for exactly this: `--selftest-screens` draws three boxes on every screen and saves
one PNG per screen in `%LocalAppData%\PokerVisionHUD\debug_snapshots\`.

---

* **v2.1** — optimisation and bug fixing before release: multi-screen/DPI robustness (screen changes
  while the app runs, clamped window placement, DPI-correct note editor), a button row that wraps and a
  scrollable editor on small screens, optional **AUTO scan** on a timer, rotation of the log file, the
  OCR engine created only once (thread-safe), and a hint if the OCR files are missing. New
  `--selftest-screens`.
* **v2.0.1** — the capture layer is back (screen picker, hover boxes, note editor out on the table) plus
  fixes after testing on Unibet's grey table theme: window capture instead of desktop capture, felt made
  optional, DB-only filter, tag colours on the boxes, and a crash when closing the note editor removed
  ("Cannot set Visibility ... while a Window is closing").
* **v2.0** — notes app: HUD, tracker, OCR and all stats removed. New editor with tags, aliases, history,
  CSV export, autosave, single instance and a focus-friendly UI.
* **v1.0** — PokerVision HUD (the tracker build) is kept as a backup:
  `E:\Poker Tools\Backups\PokerVisionHUD_HUD_build_20260922_2013.exe`

