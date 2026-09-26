using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;
using PokerNoteManager.Vision;
using Tesseract;
using Rect = OpenCvSharp.Rect;
using Point = OpenCvSharp.Point;
using Window = System.Windows.Window;
using Mat = OpenCvSharp.Mat;

namespace PokerNoteManager
{
    /// <summary>
    /// Poker Notes - a plain, fast notes program for poker players.
    /// No timers that touch other windows, no OCR, nothing drawn over the tables: the only
    /// background work is a debounced save of the file the user is editing.
    /// </summary>
    public partial class MainWindow : Window
    {
        private NotesFile _db = new();
        private string _dbPath = "";
        private List<TagDef> _tags = new();
        private readonly ObservableCollection<PlayerRow> _rows = new();

        private string? _currentKey;
        private bool _loading;           // true while the editor is filled in code (TextChanged is ignored)
        private bool _dirty;
        private string _search = "";
        private string? _filterTag;

        private readonly DispatcherTimer _autoSave;
        private readonly DispatcherTimer _savedFlash;

        // ===== capture / scan / hover boxes =====
        private TesseractEngine? _ocr;
        private GlobalMouseHook? _mouse;
        private readonly Dictionary<int, HoverOverlay> _overlays = new();     // screen index -> overlay window
        private readonly List<SeatBox> _lastBoxes = new();
        private readonly List<(Mat Frame, Point Offset, string Title)> _lastFrames = new();
        private List<ScreenInfo> _screens = new();
        private bool _boxesOn = true;
        private bool _onlyDatabase;                  // show boxes only for players already in the database
        private NotePopup? _notePopup;               // the note editor opened by clicking a box
        private string _notePopupPlayer = "";        // which player that editor is editing
        /// <summary>Boxes the user removed by hand; they stay hidden until Clear or a restart.</summary>
        private readonly HashSet<string> _hiddenBoxKeys = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Boxes the user placed by hand ("Box here" / Ctrl+middle click). Never replaced by a scan.</summary>
        private readonly List<SeatBox> _manualBoxes = new();
        /// <summary>Where the user dragged a box to (key -> rect); a scan puts it back there.</summary>
        private readonly Dictionary<string, Rect> _movedBoxes = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>One marking layer per screen while "Snip Player" waits for a marked area.</summary>
        private readonly List<SnipOverlay> _snipOverlays = new();
        /// <summary>What the marking layer does with the marked area (read a name, or box the chosen player).</summary>
        private Action<System.Windows.Rect?> _snipDone = _ => { };
        private SeatBox? _dragBox;
        private int _dragOffsetX, _dragOffsetY;
        private bool _dragging;
        private int _lastTableX = -1, _lastTableY = -1;     // last mouse position over another program's window
        private IntPtr _lastTableHandle = IntPtr.Zero;
        private string _lastTableTitle = "";
        private bool _scanning;
        private ScanOutcome? _lastOutcome;
        private readonly object _ocrGate = new();        // the OCR engine is created once, from any thread
        private DispatcherTimer? _autoScan;              // optional: rescan on a timer so boxes stay fresh
        private EventHandler? _displayChanged;           // re-read the screens when monitors change

        /// <summary>
        /// The shared OCR engine. Created once, under a lock: a scan runs on a worker thread while the
        /// clipboard scan and Snip Player use the engine from the UI thread, so two creations can race.
        /// </summary>
        private TesseractEngine OcrEngine()
        {
            lock (_ocrGate)
            {
                return _ocr ??= TableScanner.CreateEngine();
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            PlayerList.ItemsSource = _rows;
            RestoreWindowState();

            _autoSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _autoSave.Tick += (s, e) => { _autoSave.Stop(); SaveCurrent(); };

            _savedFlash = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _savedFlash.Tick += (s, e) => { _savedFlash.Stop(); SavedText.Text = ""; };

            PreviewKeyDown += OnPreviewKeyDown;
            Closing += OnClosing;

            LoadTagsAndDb();
            InitScan();
        }

        // ================= window chrome =================

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void RestoreWindowState()
        {
            try
            {
                if (!File.Exists(NotesStore.WindowStatePath)) return;
                string[] parts = File.ReadAllText(NotesStore.WindowStatePath).Split(',');
                if (parts.Length < 5) return;
                double l = double.Parse(parts[0], CultureInfo.InvariantCulture);
                double t = double.Parse(parts[1], CultureInfo.InvariantCulture);
                double w = double.Parse(parts[2], CultureInfo.InvariantCulture);
                double h = double.Parse(parts[3], CultureInfo.InvariantCulture);
                bool max = parts[4].Trim() == "1";

                if (double.IsNaN(l) || double.IsNaN(t) || double.IsNaN(w) || double.IsNaN(h) ||
                    w < MinWidth || h < MinHeight)
                {
                    if (max) WindowState = WindowState.Maximized;
                    return;
                }

                // The screen the window was saved on may be gone (undocked laptop, other resolution or
                // an unplugged monitor). Keep the window inside the screen that exists right now, with a
                // visible corner, instead of restoring it somewhere off screen.
                double vsLeft = SystemParameters.VirtualScreenLeft;
                double vsTop = SystemParameters.VirtualScreenTop;
                double vsWidth = Math.Max(320, SystemParameters.VirtualScreenWidth);
                double vsHeight = Math.Max(240, SystemParameters.VirtualScreenHeight);

                w = Math.Clamp(w, MinWidth, vsWidth);
                h = Math.Clamp(h, MinHeight, vsHeight);
                double gripX = Math.Min(240, w), gripY = Math.Min(160, h);
                l = Math.Clamp(l, vsLeft, vsLeft + vsWidth - gripX);
                t = Math.Clamp(t, vsTop, vsTop + vsHeight - gripY);

                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = l; Top = t; Width = w; Height = h;
                if (max) WindowState = WindowState.Maximized;

                PvLog.Write($"[UI] window restored at {l:0},{t:0} {w:0}x{h:0} (maximized={max}, " +
                            $"virtual screen {vsWidth:0}x{vsHeight:0} at {vsLeft:0},{vsTop:0})");
            }
            catch (Exception ex) { PvLog.Error("RestoreWindowState", ex); }
        }

        private void SaveWindowState()
        {
            try
            {
                System.Windows.Rect b = WindowState == WindowState.Normal
                    ? new System.Windows.Rect(Left, Top, Width, Height)
                    : RestoreBounds;
                File.WriteAllText(NotesStore.WindowStatePath, string.Format(CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4}", b.Left, b.Top, b.Width, b.Height,
                    WindowState == WindowState.Maximized ? 1 : 0));
            }
            catch (Exception ex) { PvLog.Error("SaveWindowState", ex); }
        }

        private void OnClosing(object? sender, CancelEventArgs e)
        {
            try
            {
                SaveCurrent(force: true);
                SaveWindowState();

                _autoScan?.Stop();
                if (_displayChanged != null)
                {
                    try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= _displayChanged; } catch { }
                    _displayChanged = null;
                }

                // shut the capture side down cleanly: no hook, no overlay windows, no frames in memory
                _mouse?.Dispose();
                foreach (HoverOverlay overlay in _overlays.Values)
                {
                    try { overlay.Close(); } catch (Exception ex) { PvLog.Error("OverlayClose", ex); }
                }
                _overlays.Clear();
                foreach ((Mat frame, _, _) in _lastFrames) frame.Dispose();
                _lastFrames.Clear();
                _ocr?.Dispose();
                _ocr = null;
            }
            catch (Exception ex) { PvLog.Error("MainWindow.OnClosing", ex); }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (ctrl && e.Key == Key.N) { NewPlayer_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (ctrl && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
            else if (ctrl && e.Key == Key.S) { SaveCurrent(force: true); e.Handled = true; }
            else if (ctrl && e.Key == Key.E) { Snapshot_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.F2 && _currentKey != null) { Rename_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.Escape)
            {
                if (AnyOverlayOpen()) { CloseOverlays(); e.Handled = true; }
                else if (SearchBox.Text.Length > 0) { SearchBox.Text = ""; e.Handled = true; }
            }
        }

        // ================= capture, scan and hover boxes =================

        private static string ScreenSettingPath =>
            Path.Combine(NotesStore.AppDataDir, "PokerNoteManager_Screen.txt");

        private static string OnlyDbSettingPath =>
            Path.Combine(NotesStore.AppDataDir, "PokerNoteManager_OnlyDb.txt");

        private const string OnlyDbOn = "🗂  DB only: ON";
        private const string OnlyDbOff = "🗂  DB only: OFF";

        private void InitScan()
        {
            try
            {
                _screens = ScreenCapture.GetScreens();
                FillScreenPicker();

                try
                {
                    if (File.Exists(ScreenSettingPath))
                        int.TryParse(File.ReadAllText(ScreenSettingPath).Trim(), out _savedScreenIndex);
                }
                catch (Exception ex) { PvLog.Error("LoadScreenIndex", ex); }
                ScreenPicker.SelectedIndex = Math.Clamp(_savedScreenIndex, 0, Math.Max(0, ScreenPicker.Items.Count - 1));

                _mouse = new GlobalMouseHook();
                _mouse.MouseMoved += HoverMove;
                _mouse.MouseMoved += RememberTablePoint;
                _mouse.MiddleClicked += MiddleClickScan;
                _mouse.DragStarted += BoxDragStart;
                _mouse.Dragging += BoxDragMove;
                _mouse.DragEnded += BoxDragEnd;
                _mouse.LeftClicked += LeftClickOnBox;
                _mouse.Start();

                try
                {
                    if (File.Exists(OnlyDbSettingPath))
                        _onlyDatabase = File.ReadAllText(OnlyDbSettingPath).Trim() == "1";
                }
                catch (Exception ex) { PvLog.Error("LoadOnlyDb", ex); }
                BtnOnlyDb.Content = _onlyDatabase ? OnlyDbOn : OnlyDbOff;

                InitAutoScan();

                // Monitors can be plugged in, unplugged or resized while the program runs: re-read the
                // screen list and rebuild the overlays so the boxes keep landing on the right screen.
                _displayChanged = (_, _) => Dispatcher.BeginInvoke(new Action(OnDisplaySettingsChanged));
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += _displayChanged;

                PvLog.Write($"[SCAN] {_screens.Count} screen(s), mouse hook active: {_mouse.IsRunning}");
            }
            catch (Exception ex)
            {
                PvLog.Error("InitScan", ex);
                ScanStatus.Text = "Capture is unavailable: " + ex.Message;
            }
        }

        private int _savedScreenIndex;

        /// <summary>(Re)fills the screen picker without firing a pointless change event per item.</summary>
        private void FillScreenPicker()
        {
            ScreenPicker.SelectionChanged -= ScreenPicker_SelectionChanged;
            ScreenPicker.Items.Clear();
            if (_screens.Count > 1) ScreenPicker.Items.Add(BothScreensLabel);
            foreach (ScreenInfo screen in _screens) ScreenPicker.Items.Add(screen.Name);
            ScreenPicker.SelectionChanged += ScreenPicker_SelectionChanged;
        }

        /// <summary>
        /// A monitor was added, removed or resized. Overlays belong to a screen and are therefore thrown
        /// away and rebuilt; the current boxes are drawn again on the new layout.
        /// </summary>
        private void OnDisplaySettingsChanged()
        {
            try
            {
                List<ScreenInfo> fresh = ScreenCapture.GetScreens();
                if (fresh.Count == 0) return;

                PvLog.Write($"[SCAN] display settings changed: {_screens.Count} -> {fresh.Count} screen(s)");

                foreach (HoverOverlay overlay in _overlays.Values)
                {
                    try { overlay.Close(); } catch (Exception ex) { PvLog.Error("OverlayClose (display change)", ex); }
                }
                _overlays.Clear();

                _screens = fresh;
                FillScreenPicker();
                ScreenPicker.SelectedIndex = Math.Clamp(_savedScreenIndex, 0, Math.Max(0, ScreenPicker.Items.Count - 1));

                RefreshBoxes();
                ScanStatus.Text = $"{_screens.Count} screen(s) detected - hover boxes rebuilt.";
            }
            catch (Exception ex)
            {
                PvLog.Error("OnDisplaySettingsChanged", ex);
            }
        }

        private void ScreenPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ScreenPicker.SelectedIndex < 0) return;
            _savedScreenIndex = ScreenPicker.SelectedIndex;
            try { File.WriteAllText(ScreenSettingPath, _savedScreenIndex.ToString(CultureInfo.InvariantCulture)); }
            catch (Exception ex) { PvLog.Error("SaveScreenIndex", ex); }
        }

        // ================= optional auto scan =================

        private static string AutoScanSettingPath =>
            Path.Combine(NotesStore.AppDataDir, "PokerNoteManager_AutoScan.txt");

        private static readonly (string Label, int Seconds)[] AutoScanOptions =
        {
            ("Auto: off", 0), ("Auto: 15 s", 15), ("Auto: 30 s", 30), ("Auto: 1 min", 60), ("Auto: 2 min", 120)
        };

        /// <summary>Fills the auto scan picker and restarts the timer with the saved interval.</summary>
        private void InitAutoScan()
        {
            int saved = 0;
            try
            {
                if (File.Exists(AutoScanSettingPath))
                    int.TryParse(File.ReadAllText(AutoScanSettingPath).Trim(), out saved);
            }
            catch (Exception ex) { PvLog.Error("LoadAutoScan", ex); }

            AutoScanPicker.SelectionChanged -= AutoScanPicker_SelectionChanged;
            AutoScanPicker.Items.Clear();
            foreach ((string label, _) in AutoScanOptions) AutoScanPicker.Items.Add(label);
            AutoScanPicker.SelectedIndex = Math.Max(0, IndexOfAutoScan(saved));
            AutoScanPicker.SelectionChanged += AutoScanPicker_SelectionChanged;

            _autoScan = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(5, saved)) };
            _autoScan.Tick += (_, _) => AutoScanTick();
            if (saved > 0) _autoScan.Start();

            PvLog.Write($"[SCAN] auto scan: {(saved > 0 ? saved + " s" : "off")}");
        }

        private static int IndexOfAutoScan(int seconds)
        {
            for (int i = 0; i < AutoScanOptions.Length; i++)
                if (AutoScanOptions[i].Seconds == seconds) return i;
            return 0;
        }

        private void AutoScanPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int index = Math.Clamp(AutoScanPicker.SelectedIndex, 0, AutoScanOptions.Length - 1);
            int seconds = AutoScanOptions[index].Seconds;

            try { File.WriteAllText(AutoScanSettingPath, seconds.ToString(CultureInfo.InvariantCulture)); }
            catch (Exception ex) { PvLog.Error("SaveAutoScan", ex); }

            if (_autoScan == null) return;
            if (seconds > 0)
            {
                _autoScan.Interval = TimeSpan.FromSeconds(seconds);
                _autoScan.Start();
                ScanStatus.Text = $"Auto scan every {seconds} s while the boxes are on. Boxes you removed " +
                                  "with Ctrl+click stay removed between scans.";
            }
            else
            {
                _autoScan.Stop();
                ScanStatus.Text = "Auto scan is off. Press Capture & Scan (or the middle mouse button) to scan.";
            }
            PvLog.Write($"[SCAN] auto scan set to {seconds} s");
        }

        /// <summary>One auto scan tick: never scans while a scan runs or the boxes are hidden.</summary>
        private void AutoScanTick()
        {
            if (!_boxesOn || _scanning) return;
            RunScan("auto", -1, -1);
        }

        private const string BothScreensLabel = "Both / all screens";

        /// <summary>The picked screen, or null when "Both" is selected.</summary>
        private ScreenInfo? SelectedScreen()
        {
            int index = ScreenPicker.SelectedIndex;
            if (index < 0) return _screens.Count > 0 ? _screens[0] : null;
            if (_screens.Count > 1)
            {
                if (index == 0) return null;         // both
                index -= 1;
            }
            return index < _screens.Count ? _screens[index] : (_screens.Count > 0 ? _screens[0] : null);
        }

        /// <summary>Screens the capture should look at: the one under the cursor, else the picked one(s).</summary>
        private List<ScreenInfo> ResolveScreens(int cursorX, int cursorY)
        {
            if (cursorX >= 0 && cursorY >= 0)
            {
                foreach (ScreenInfo screen in _screens)
                {
                    if (cursorX >= screen.Bounds.X && cursorX <= screen.Bounds.Right &&
                        cursorY >= screen.Bounds.Y && cursorY <= screen.Bounds.Bottom)
                        return new List<ScreenInfo> { screen };
                }
            }

            ScreenInfo? picked = SelectedScreen();
            return picked != null ? new List<ScreenInfo> { picked } : new List<ScreenInfo>(_screens);
        }

        private HoverOverlay? OverlayForScreen(ScreenInfo screen)
        {
            if (_overlays.TryGetValue(screen.Index, out HoverOverlay? existing))
            {
                if (!existing.IsLoaded) existing.Show();
                return existing;
            }

            try
            {
                HoverOverlay overlay = new(screen, BuildHoverInfo);
                overlay.Show();
                _overlays[screen.Index] = overlay;
                PvLog.Write($"[SCAN] hover overlay created for display {screen.Index + 1}");
                return overlay;
            }
            catch (Exception ex)
            {
                PvLog.Error("OverlayForScreen", ex);
                return null;
            }
        }

        /// <summary>Mouse move from the hook (UI thread): keeps the note card in sync.</summary>
        private void HoverMove(int x, int y)
        {
            try
            {
                if (!_boxesOn || _overlays.Count == 0) return;
                foreach (KeyValuePair<int, HoverOverlay> kv in _overlays)
                {
                    ScreenInfo? screen = _screens.FirstOrDefault(s => s.Index == kv.Key);
                    if (screen == null) continue;
                    if (x >= screen.Bounds.X && x <= screen.Bounds.Right &&
                        y >= screen.Bounds.Y && y <= screen.Bounds.Bottom)
                    {
                        kv.Value.HoverAt(x, y);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                PvLog.Throttled("HoverMove", $"!! hover: {ex.Message}", 180);
            }
        }

        private void MiddleClickScan(int x, int y, bool ctrl)
        {
            // Ctrl + middle click is the manual way to box a player: put the box of the player that is
            // selected in the main window where the mouse is. Middle click alone still scans.
            if (ctrl) AddManualBox(x, y);
            else RunScan("middle mouse", x, y);
        }

        /// <summary>
        /// Remembers where the mouse last was over another program's window, so the "Box here" button in
        /// the main window can place a box there - the cursor is on the button when it is pressed.
        /// </summary>
        private void RememberTablePoint(int x, int y)
        {
            try
            {
                TableWindow? under = ScreenCapture.WindowUnderPoint(x, y);
                if (under == null) return;

                _lastTableX = x;
                _lastTableY = y;
                _lastTableHandle = under.Handle;
                _lastTableTitle = under.Title;
            }
            catch (Exception ex) { PvLog.Throttled("RememberTablePoint", "!! " + ex.Message, 120); }
        }

        /// <summary>
        /// Middle mouse button held down and moved over a box: the box is dragged along and stays where it is
        /// put (a scan puts it back there). Handy when a name plate sits slightly off, or when the box covers
        /// the stack.
        /// </summary>
        private void BoxDragStart(int x, int y)
        {
            try
            {
                if (!_boxesOn) return;
                SeatBox? hit = FindBoxAt(x, y);
                if (hit == null) return;

                _dragBox = hit;
                _dragOffsetX = x - hit.ScreenRect.X;
                _dragOffsetY = y - hit.ScreenRect.Y;
                _dragging = true;
                ScanStatus.Text = $"Moving the box for {(hit.Matched ? hit.PlayerKey : hit.OcrText)} - " +
                                  "release where it should stay.";
            }
            catch (Exception ex) { PvLog.Error("BoxDragStart", ex); }
        }

        private void BoxDragMove(int x, int y)
        {
            if (!_dragging || _dragBox == null) return;
            try
            {
                Rect current = _dragBox.ScreenRect;
                _dragBox.ScreenRect = new Rect(x - _dragOffsetX, y - _dragOffsetY, current.Width, current.Height);
                RefreshBoxes();
            }
            catch (Exception ex) { PvLog.Error("BoxDragMove", ex); }
        }

        private void BoxDragEnd(int x, int y)
        {
            if (!_dragging || _dragBox == null) return;
            try
            {
                SeatBox box = _dragBox;
                _dragBox = null;
                _dragging = false;

                foreach (string key in KeysFor(box)) _movedBoxes[key] = box.ScreenRect;
                RefreshBoxes();

                string label = box.Matched ? box.PlayerKey : "'" + box.OcrText + "'";
                ScanStatus.Text = $"Box for {label} moved. It stays there until you press 🧹 Clear or drag it again.";
                PvLog.Write($"[SCAN] box moved: {BoxKey(box)} -> {box.ScreenRect.X},{box.ScreenRect.Y}");
            }
            catch (Exception ex) { PvLog.Error("BoxDragEnd", ex); }
        }

        /// <summary>
        /// The keys a box can be remembered under. A reading that turns into a known player (or the other way
        /// round) changes the key, so both spellings are remembered and a dragged box keeps its place.
        /// </summary>
        private static IEnumerable<string> KeysFor(SeatBox box)
        {
            yield return BoxKey(box);
            if (box.OcrText.Length > 0)
                yield return BoxFilter.TableKey(box) + "|read:" + TableScanner.NormalizeName(box.OcrText);
            if (box.Matched)
                yield return BoxFilter.TableKey(box) + "|player:" + box.PlayerKey;
        }

        /// <summary>Puts every box the user dragged back where it was put.</summary>
        private void ApplyMovedPositions()
        {
            if (_movedBoxes.Count == 0) return;
            foreach (SeatBox box in _lastBoxes)
            {
                foreach (string key in KeysFor(box))
                {
                    if (!_movedBoxes.TryGetValue(key, out Rect moved)) continue;
                    box.ScreenRect = moved;
                    break;
                }
            }
        }

        private void AddBoxHere_Click(object sender, RoutedEventArgs e) => AddSelectedPlayerToTable("Box here");

        /// <summary>
        /// The "Add to table" button: puts a hover box for the player that is selected in the list (the player
        /// that was just found in the search) where the mouse was last on a table. When the program has not
        /// seen the mouse on a table yet, the marking layer is shown instead, so the box can simply be drawn
        /// where the name is.
        /// </summary>
        private void AddToTable_Click(object sender, RoutedEventArgs e) => AddSelectedPlayerToTable("Add to table");

        private void AddSelectedPlayerToTable(string source)
        {
            string key = (_currentKey ?? "").Trim();
            if (key.Length == 0 || !_db.Players.ContainsKey(key))
            {
                ScanStatus.Text = "Pick the player in the list on the left first, then press \"Add to table\" " +
                                  "(Ctrl + middle mouse button right on the name does the same).";
                return;
            }

            if (_lastTableX >= 0 && _lastTableY >= 0)
            {
                AddManualBox(_lastTableX, _lastTableY, key);
                return;
            }

            // Nothing to place the box on yet: mark the name on the table instead of only complaining.
            _snipDone = area => PlaceMarkedBox(area, key);
            ShowSnipLayers($"Drag a box where {key}'s name is (or just click on it) - the box is put there.",
                           $"[SNIP] marking layer shown ({source} for '{key}')");
        }

        /// <summary>Puts the box for a player on the area the user marked with the "Add to table" layer.</summary>
        private void PlaceMarkedBox(System.Windows.Rect? area, string key)
        {
            CloseSnipOverlays();
            if (area == null)
            {
                ScanStatus.Text = "Cancelled - no box added.";
                return;
            }

            System.Windows.Rect mark = area.Value;
            Rect rect = new((int)mark.X, (int)mark.Y, Math.Max(1, (int)mark.Width), Math.Max(1, (int)mark.Height));
            bool dragged = rect.Width >= 12 && rect.Height >= 10;

            AddManualBox(dragged ? (int)(rect.X + rect.Width / 2) : rect.X + 1, dragged ? (int)(rect.Y + rect.Height / 2) : rect.Y + 1,
                         key, dragged ? rect : (Rect?)null);
        }

        /// <summary>
        /// Puts a hover box for the player selected in the main window at a point on the screen. This is the
        /// manual way to box a player whose name plate the OCR reads wrong or not at all: the box behaves
        /// exactly like a scanned one (hover shows tags and notes, click opens the editor, Ctrl+click
        /// removes it, Clear removes all of them) and no scan ever deletes it.
        /// </summary>
        /// <param name="x">Where the box should sit (its centre).</param>
        /// <param name="y">Where the box should sit (its centre).</param>
        /// <param name="player">The player to box; the selection in the list is used when this is null.</param>
        /// <param name="area">The name area that was marked or read - the box takes that size when it is known.</param>
        private void AddManualBox(int x, int y, string? player = null, Rect? area = null)
        {
            try
            {
                string key = (player ?? _currentKey ?? "").Trim();
                if (key.Length == 0 || !_db.Players.ContainsKey(key))
                {
                    ScanStatus.Text = "Pick the player in the list on the left first, then put the box where " +
                                      "the name is (Ctrl + middle mouse button on the table).";
                    return;
                }

                // A box the size of the name plate that was read/marked looks right; without a known area the
                // default box (140x26) has to do.
                Rect rect = area.HasValue && area.Value.Width >= 24 && area.Value.Height >= 8
                    ? new Rect(area.Value.X - 3, area.Value.Y - 3, area.Value.Width + 6, area.Value.Height + 6)
                    : new Rect(x - 70, y - 13, 140, 26);

                TableWindow? under = ScreenCapture.WindowUnderPoint(x, y);
                SeatBox box = new()
                {
                    TableTitle = under?.Title ?? "manual box",
                    TableHandle = under?.Handle ?? IntPtr.Zero,
                    OcrText = key,
                    PlayerKey = key,
                    Confidence = 100,
                    Distance = 0,
                    ScreenRect = rect
                };

                string boxKey = BoxKey(box);
                _manualBoxes.RemoveAll(b => string.Equals(BoxKey(b), boxKey, StringComparison.OrdinalIgnoreCase));
                _manualBoxes.Add(box);
                _hiddenBoxKeys.Remove(boxKey);          // adding it again must show it, not stay hidden
                _lastBoxes.RemoveAll(b => string.Equals(BoxKey(b), boxKey, StringComparison.OrdinalIgnoreCase));
                _lastBoxes.Add(box);
                RefreshBoxes();

                ScanStatus.Text = $"Box added for {key} here ({_manualBoxes.Count} placed by hand). " +
                                  "Ctrl+click removes that one, 🧹 Clear removes all boxes.";
                PvLog.Write($"[SCAN] manual box for '{key}' at {x},{y} on '{(under?.Title ?? "?")}'");
            }
            catch (Exception ex)
            {
                PvLog.Error("AddManualBox", ex);
                ScanStatus.Text = "Could not add the box: " + ex.Message;
            }
        }

        private void LeftClickOnBox(int x, int y, bool ctrl)
        {
            if (!_boxesOn) return;

            // Ctrl + left click removes that one box. Right click is *not* used for anything: on Unibet
            // a right click folds the hand, and middle click already runs Capture & Scan.
            if (ctrl) RemoveBoxAt(x, y);
            else BoxClicked(x, y);
        }

        /// <summary>
        /// Removes the single box under the cursor (a reading that does not belong to a real player, or a
        /// player who is simply not wanted in the boxes right now). It stays hidden until the boxes are
        /// cleared or the program is restarted - and only on that table, the same player may sit at others.
        /// </summary>
        private void RemoveBoxAt(int x, int y)
        {
            try
            {
                if (_lastBoxes.Count == 0) return;

                SeatBox? hit = FindBoxAt(x, y);
                if (hit == null)
                {
                    ScanStatus.Text = "No hover box under the cursor.";
                    return;
                }

                string key = BoxKey(hit);
                _hiddenBoxKeys.Add(key);
                RefreshBoxes();

                string label = hit.Matched ? hit.PlayerKey : "'" + hit.OcrText + "'";
                string where = string.IsNullOrWhiteSpace(hit.TableTitle) ? "" : $" on {hit.TableTitle}";
                ScanStatus.Text = $"Box removed: {label}{where}. It stays hidden until you press Clear " +
                                  $"(or restart the program) - {_hiddenBoxKeys.Count} removed.";
                PvLog.Write($"[SCAN] box removed by Ctrl+click: {key}");
            }
            catch (Exception ex)
            {
                PvLog.Error("RemoveBoxAt", ex);
            }
        }

        /// <summary>The hover box under a physical screen point, or null.</summary>
        private SeatBox? FindBoxAt(int x, int y)
        {
            foreach (KeyValuePair<int, HoverOverlay> kv in _overlays)
            {
                SeatBox? hit = kv.Value.HitTest(x, y);
                if (hit != null) return hit;
            }
            return null;
        }

        private void CaptureScan_Click(object sender, RoutedEventArgs e) => RunScan("button", -1, -1);

        /// <summary>Starts a scan. Capture and OCR run on a worker thread, the UI never blocks.</summary>
        private void RunScan(string trigger, int cursorX, int cursorY)
        {
            if (_scanning)
            {
                ScanStatus.Text = "A scan is already running…";
                return;
            }

            List<ScreenInfo> screens = ResolveScreens(cursorX, cursorY);
            if (screens.Count == 0)
            {
                ScanStatus.Text = "No screen found to capture.";
                return;
            }

            // Snapshot what the worker needs: the database must not be touched from another thread.
            Dictionary<string, string> keys = _db.Players.Keys.ToDictionary(k => k, TableScanner.NormalizeName);

            _scanning = true;
            ScanStatus.Text = "Scanning…";
            _ = Task.Run(() =>
            {
                ScanOutcome outcome;
                List<(Mat Frame, Point Offset, string Title)> frames = new();
                try
                {
                    outcome = ScanScreens(screens, keys, frames);
                }
                catch (Exception ex)
                {
                    PvLog.Error("RunScan (" + trigger + ")", ex);
                    outcome = new ScanOutcome { Message = "Scan failed: " + ex.Message + TableScanner.DescribeEngineProblem(ex) };
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _scanning = false;
                    ApplyScanOutcome(outcome, frames);
                }));
            });
        }

        /// <summary>Captures every table window on the given screens and reads the player names.</summary>
        private ScanOutcome ScanScreens(List<ScreenInfo> screens, Dictionary<string, string> normalizedKeys,
                                        List<(Mat Frame, Point Offset, string Title)> frames)
        {
            ScanOutcome outcome = new();
            TesseractEngine engine = OcrEngine();

            foreach (ScreenInfo screen in screens)
            {
                List<TableWindow> windows = ScreenCapture.FindCandidateWindows(screen.Bounds);
                outcome.TablesSeen += windows.Count;

                foreach (TableWindow window in windows)
                {
                    Mat? frame = ScreenCapture.CaptureWindow(window);
                    if (frame == null) continue;

                    // The green felt is the nicest frame of reference, but some client themes are grey
                    // (no felt at all, Unibet's "film set" theme for instance). In that case a poker
                    // looking window title is enough and the "light text on a dark name plate" test
                    // does the filtering. A window without a poker title only counts when the felt is
                    // a real table sized patch - that keeps desktop covering shell windows out.
                    bool pokerTitle = TableScanner.LooksLikePokerTitle(window.Title);
                    bool hasFelt = TableScanner.FindFelt(frame, out Rect felt);
                    double feltShare = hasFelt
                        ? felt.Width * (double)felt.Height / Math.Max(1, frame.Width * (double)frame.Height)
                        : 0;
                    bool usableFelt = hasFelt && (pokerTitle || feltShare >= 0.06);

                    if (!pokerTitle && !usableFelt)
                    {
                        PvLog.Write($"[SCAN] '{window.Title}' skipped (no poker title, felt={hasFelt} share={feltShare:P1})");
                        frame.Dispose();
                        continue;
                    }

                    List<SeatBox> seats = TableScanner.DetectSeats(frame, usableFelt ? felt : (Rect?)null, engine, window.Title);
                    int kept = 0;
                    foreach (SeatBox seat in seats)
                    {
                        (string key, double distance) = TableScanner.MatchPlayer(seat.OcrText, normalizedKeys);
                        seat.PlayerKey = key;
                        seat.Distance = distance;

                        // A known player always gets a box. An unknown reading is kept when the OCR was
                        // confident enough, or when it looks like exactly one player in the database - then
                        // the box is a grey reminder that this player needs a look, instead of the player
                        // silently vanishing from the screen. Everything else is noise from chips, avatars,
                        // logos or window chrome.
                        if (key.Length == 0)
                        {
                            string similar = TableScanner.FindSimilarName(seat.OcrText, normalizedKeys);
                            double needed = similar.Length > 0 ? 25 : 45;
                            if (seat.Confidence < needed)
                            {
                                PvLog.Throttled("ScanDropped:" + seat.OcrText,
                                    $"[SCAN] '{seat.OcrText}' (conf {seat.Confidence:0}) dropped as noise" +
                                    (similar.Length > 0 ? $", it looks like '{similar}'" : ""), 60);
                                continue;
                            }
                            if (similar.Length > 0)
                                PvLog.Throttled("ScanSimilar:" + seat.OcrText,
                                    $"[SCAN] '{seat.OcrText}' (conf {seat.Confidence:0}) kept as unknown - it looks like '{similar}'", 60);
                        }
                        if (kept >= 12) break;

                        seat.ScreenRect = new Rect(seat.ScreenRect.X + window.Bounds.X, seat.ScreenRect.Y + window.Bounds.Y,
                                                   seat.ScreenRect.Width, seat.ScreenRect.Height);
                        seat.TableHandle = window.Handle;      // same player at several tables stays distinguishable
                        outcome.Boxes.Add(seat);
                        kept++;
                    }

                    if (kept > 0)
                    {
                        outcome.Tables.Add(window.Title);
                        if (frames.Count < 4) frames.Add((frame.Clone(), window.Bounds.Location, window.Title));
                        PvLog.Write($"[SCAN] '{window.Title}' {window.Bounds.Width}x{window.Bounds.Height} " +
                                    $"felt={hasFelt}/{feltShare:P0} -> {kept} box(es)");
                    }

                    frame.Dispose();
                }
            }

            outcome.MatchedCount = outcome.Boxes.Count(b => b.Matched);
            outcome.UnknownCount = outcome.Boxes.Count - outcome.MatchedCount;
            return outcome;
        }

        /// <summary>Puts the result on screen (hover boxes) and in the status bar.</summary>
        private void ApplyScanOutcome(ScanOutcome outcome, List<(Mat Frame, Point Offset, string Title)> frames)
        {
            foreach ((Mat frame, _, _) in _lastFrames) frame.Dispose();
            _lastFrames.Clear();
            _lastFrames.AddRange(frames);

            _lastOutcome = outcome;

            // A scan that finds nothing at all is usually a covered or just resized table, not every player
            // leaving at once. The boxes from the last good scan are kept, so a note never disappears from
            // the screen because of one bad capture; "Clear" removes them for good.
            bool keepPrevious = outcome.Boxes.Count == 0 && _lastBoxes.Count > 0;
            if (!keepPrevious)
            {
                _lastBoxes.Clear();
                _lastBoxes.AddRange(outcome.Boxes);
            }
            MergeManualBoxes();
            ApplyMovedPositions();

            RefreshBoxes();

            int shown = ShownBoxCount();
            int multiTable = _lastBoxes.Where(b => b.Matched)
                .Select(b => b.PlayerKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(key => BoxFilter.TableCountFor(_lastBoxes, key) > 1);

            ScanStatus.Text = outcome.Message.Length > 0
                ? outcome.Message
                : keepPrevious
                  ? $"No names found in this scan - the {shown} box(es) from the last scan are kept " +
                    "(🧹 Clear removes them)."
                  : $"{shown} box(es) on {outcome.TablesSeen} window(s) · " +
                    $"{outcome.MatchedCount} known, {outcome.UnknownCount} left out of the box list · " +
                    (multiTable > 0 ? $"{multiTable} player(s) sitting at more than one table · " : "") +
                    (_onlyDatabase && outcome.UnknownCount > 0
                        ? $"{outcome.UnknownCount} hidden by the database filter · "
                        : "") +
                    (shown > 0
                        ? "click a box to write a note, Ctrl+click to remove that box, Ctrl+middle click boxes a player by hand"
                        : "nothing found - pick the right screen and check that the table is not covered");
        }

        /// <summary>
        /// Boxes the user placed by hand are part of every result: a scan never removes them. That is the
        /// way to box a player whose name plate the OCR cannot read at all.
        /// </summary>
        private void MergeManualBoxes()
        {
            foreach (SeatBox box in _manualBoxes)
            {
                string key = BoxKey(box);
                if (!_lastBoxes.Any(b => string.Equals(BoxKey(b), key, StringComparison.OrdinalIgnoreCase)))
                    _lastBoxes.Add(box);
            }
        }

        /// <summary>Boxes that pass the "only the database" filter and are not removed by hand.</summary>
        private int ShownBoxCount() => _lastBoxes.Count(b => IsBoxVisible(b));

        /// <summary>A stable key for one box: the matched player, else the OCR reading.</summary>
        private static string BoxKey(SeatBox box) => BoxFilter.KeyFor(box);

        /// <summary>True when a box should be drawn: database filter and the right click removals.</summary>
        private bool IsBoxVisible(SeatBox box) => BoxFilter.IsVisible(box, _onlyDatabase, _hiddenBoxKeys);

        /// <summary>(Re)draws the current boxes on every screen. Used after a scan and by the on/off button.</summary>
        private void RefreshBoxes()
        {
            List<SeatBox> visible = _lastBoxes.Where(IsBoxVisible).ToList();
            foreach (ScreenInfo screen in _screens)
            {
                List<SeatBox> onScreen = visible.Where(b =>
                    CenterX(b) >= screen.Bounds.X && CenterX(b) <= screen.Bounds.Right &&
                    CenterY(b) >= screen.Bounds.Y && CenterY(b) <= screen.Bounds.Bottom).ToList();

                HoverOverlay? overlay = onScreen.Count > 0
                    ? OverlayForScreen(screen)
                    : (_overlays.TryGetValue(screen.Index, out HoverOverlay? existing) ? existing : null);
                if (overlay == null) continue;

                if (_boxesOn && onScreen.Count > 0)
                {
                    overlay.SetBoxes(onScreen);
                    overlay.BringToFront();
                }
                else
                {
                    overlay.ClearBoxes();
                }
            }
        }

        private static double CenterX(SeatBox box) => box.ScreenRect.X + box.ScreenRect.Width / 2.0;
        private static double CenterY(SeatBox box) => box.ScreenRect.Y + box.ScreenRect.Height / 2.0;

        /// <summary>
        /// What the hover overlay shows for one box. A tagged player colours the whole box with the
        /// colour of the first tag, and every tag is written next to the name in its own colour, so
        /// the table shows at a glance which tags each player has.
        /// </summary>
        private HoverInfo BuildHoverInfo(SeatBox box)
        {
            if (box.PlayerKey.Length > 0 && _db.Players.TryGetValue(box.PlayerKey, out PlayerEntry? entry))
            {
                List<string> tags = (entry.Tags ?? new List<string>())
                    .Where(t => (t ?? "").Trim().Length > 0).ToList();
                string tagLine = string.Join(" · ", tags.Select(TagDisplay));
                string notes = (entry.Notes ?? "").Trim();

                // The same player can sit at several tables: say so in the card so it is obvious that
                // the boxes on the other tables belong to the same person (and share these notes).
                int tables = BoxFilter.TableCountFor(_lastBoxes, box.PlayerKey);

                string body = (tagLine.Length > 0 ? tagLine + "\n" : "") +
                              (notes.Length > 0 ? notes : "(no notes yet)") +
                              (tables > 1 ? $"\n\nAt {tables} tables right now - they share this note." : "") +
                              "\n\nClick the box to write a note (tags included)" +
                              "\nCtrl+click removes this box";

                return new HoverInfo
                {
                    Known = true,
                    Label = box.PlayerKey,
                    Title = tables > 1 ? $"{box.PlayerKey}  ·  at {tables} tables" : box.PlayerKey,
                    Body = body,
                    MainColour = tags.Count > 0
                        ? TagColour(tags[0], Color.FromRgb(0x22, 0xC5, 0x5E))
                        : Color.FromRgb(0x22, 0xC5, 0x5E),
                    Chips = tags.Take(3).Select(t => (Text: TagDisplay(t), Colour: TagColour(t, Color.FromRgb(0x64, 0x74, 0x8B)))).ToList()
                };
            }

            return new HoverInfo
            {
                Known = false,
                Label = box.OcrText,
                Title = "? " + box.OcrText,
                Body = "Not in the database yet.\r\n\r\n" +
                       "Click the box to create the player and write the first note.\r\n" +
                       "Ctrl+click removes this box.",
                MainColour = Color.FromRgb(0x94, 0xA3, 0xB8),
                Chips = Array.Empty<(string, Color)>()
            };
        }

        /// <summary>The colour of a tag; the colour from the tag list when the tag is known, else a fallback.</summary>
        private Color TagColour(string tag, Color fallback)
        {
            string t = (tag ?? "").Trim();
            TagDef? def = _tags.FirstOrDefault(d =>
                string.Equals((d.TagKey ?? "").Trim(), t, StringComparison.OrdinalIgnoreCase) ||
                string.Equals((d.DisplayName ?? "").Trim(), t, StringComparison.OrdinalIgnoreCase));
            return ColorFromHex(def?.ColorHex)?.Color ?? fallback;
        }

        /// <summary>
        /// Plain left click on a box: the little note editor opens right at the box, so a note can be
        /// written (and tags set) without leaving the tables. From there "Open" shows the player in the
        /// main window. Removing a box is Ctrl + left click, see RemoveBoxAt.
        /// </summary>
        private void BoxClicked(int x, int y)
        {
            try
            {
                SeatBox? hit = FindBoxAt(x, y);
                if (hit == null)
                {
                    // The boxes are redrawn by every scan, so say why the click did nothing instead of
                    // doing nothing at all (that is what made the editor look like it would not open).
                    ScanStatus.Text = _boxesOn
                        ? "No box under the cursor - the boxes are redrawn by every scan (middle mouse rescans, " +
                          "Ctrl+middle click boxes the selected player right where the mouse is)."
                        : "The hover boxes are switched off (👁 Boxes).";
                    return;
                }
                OpenNotePopup(hit, x, y);
            }
            catch (Exception ex)
            {
                PvLog.Error("BoxClicked", ex);
            }
        }

        /// <summary>Opens the inline note editor for one box (also used to create brand new players).</summary>
        private void OpenNotePopup(SeatBox box, int physX, int physY)
        {
            try
            {
                string reading = (box.OcrText ?? "").Trim();
                bool known = box.PlayerKey.Length > 0 && _db.Players.TryGetValue(box.PlayerKey, out PlayerEntry? entry);
                if (!known && reading.Length == 0) return;

                PlayerEntry? player = known ? _db.Players[box.PlayerKey] : null;
                string name = known ? box.PlayerKey : reading;

                // The same player is often seated at several tables: clicking the second box must not
                // throw away what is already typed. Bring the open editor forward instead, and save
                // before replacing it when another player is clicked.
                if (_notePopup is { } open && open.IsUsable)
                {
                    if (string.Equals(_notePopupPlayer, name, StringComparison.OrdinalIgnoreCase))
                    {
                        open.BringToFront();
                        ScanStatus.Text = $"The note editor for {name} is already open.";
                        return;
                    }

                    // Another player: save what is typed and *always* close this editor. Leaving it open
                    // made a stack of stale editors that sat on top of the new one (and its closed flag
                    // then swallowed the next click), which is what made the editor "not come up".
                    if (open.HasUnsavedChanges) open.SaveNow();
                    open.CloseSafely("replaced by another player");
                }
                else if (_notePopup != null)
                {
                    // An editor that never reached the screen (or is on its way out) must never block the
                    // next click - that is what made a click look like it did nothing at all.
                    _notePopup = null;
                    _notePopupPlayer = "";
                }

                long started = Environment.TickCount64;
                NotePopup popup = new(
                    known: known,
                    playerName: name,
                    note: player?.Notes ?? "",
                    allTags: _tags,
                    playerTags: player?.Tags ?? new List<string>(),
                    onSave: (savedName, savedTags, savedNote) => SaveFromPopup(box, savedName, savedTags, savedNote),
                    openInMain: () => OpenPlayer(known ? box.PlayerKey : name),
                    physX: physX,
                    physY: physY,
                    // A reading that was *not* matched may still be one character off an existing player:
                    // say so in the editor instead of silently creating a duplicate.
                    similarName: known
                        ? ""
                        : TableScanner.FindSimilarName(reading, _db.Players.Keys.ToDictionary(k => k, TableScanner.NormalizeName)));

                _notePopup = popup;
                _notePopupPlayer = name;
                popup.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_notePopup, popup)) { _notePopup = null; _notePopupPlayer = ""; }
                };

                popup.Show();
                PvLog.Throttled("PopupOpen", $"[POPUP] '{name}' editor opened in {Environment.TickCount64 - started} ms", 20);
            }
            catch (Exception ex)
            {
                PvLog.Error("OpenNotePopup", ex);
                ScanStatus.Text = "Could not open the note editor: " + ex.Message;
            }
        }

        /// <summary>Stores what the note popup collected (creating the player when it is new).</summary>
        private void SaveFromPopup(SeatBox box, string name, List<string> tags, string note)
        {
            try
            {
                string key = (name ?? "").Trim();
                if (key.Length == 0)
                {
                    ScanStatus.Text = "Nothing was saved: the player name is empty.";
                    return;
                }

                // A note written in the main window must not be lost when the popup saves first.
                if (string.Equals(_currentKey, key, StringComparison.OrdinalIgnoreCase)) SaveCurrent(force: true);

                bool isNew = !_db.Players.TryGetValue(key, out PlayerEntry? entry);
                if (entry == null)
                {
                    entry = new PlayerEntry { Tags = new List<string>(), Notes = "" };
                    _db.Players[key] = entry;
                }

                string oldNote = (entry.Notes ?? "").Trim();
                string newNote = (note ?? "").Trim();
                if (newNote != oldNote && oldNote.Length > 0)
                {
                    entry.NoteHistory ??= new List<NoteSnapshot>();
                    entry.NoteHistory.Insert(0, new NoteSnapshot
                    {
                        When = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        Text = oldNote
                    });
                }

                entry.Notes = newNote;
                entry.Tags = (tags ?? new List<string>()).Where(t => (t ?? "").Trim().Length > 0).ToList();

                NotesStore.Save(_dbPath, _db);
                _dirty = false;

                if (isNew) RefreshList();
                else RefreshRow(key);
                UpdateCounts();
                if (string.Equals(_currentKey, key, StringComparison.OrdinalIgnoreCase)) SelectExisting(key);

                SavedText.Text = "saved " + DateTime.Now.ToString("HH:mm:ss");
                _savedFlash.Stop();
                _savedFlash.Start();

                // Point the box at the player, so it gets its name and tag colour without a new scan.
                if (!box.Matched && string.Equals((box.OcrText ?? "").Trim(), key, StringComparison.OrdinalIgnoreCase))
                    box.PlayerKey = key;
                RefreshBoxes();

                ScanStatus.Text = (isNew ? $"'{key}' created. " : "") +
                                  (newNote.Length > 0 ? "Note saved." : "Note cleared.") +
                                  (entry.Tags.Count > 0 ? " Tags: " + string.Join(", ", entry.Tags.Select(TagDisplay)) : "");
                PvLog.Write($"[NOTE] popup saved '{key}' (new={isNew}, tags={entry.Tags.Count}, note={newNote.Length} chars)");
            }
            catch (Exception ex)
            {
                PvLog.Error("SaveFromPopup", ex);
                ScanStatus.Text = "Save failed: " + ex.Message;
            }
        }

        /// <summary>Opens a player in the window (and gives the window focus - a user action asked for it).</summary>
        private void OpenPlayer(string key)
        {
            if (!_db.Players.ContainsKey(key)) return;
            SelectExisting(key);
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        // ================= paste, snip, boxes and snapshot =================

        private void PasteScan_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BitmapSource? source = Clipboard.GetImage();
                if (source == null)
                {
                    ScanStatus.Text = "The clipboard does not contain an image.";
                    return;
                }

                Mat? frame = BitmapSourceToMat(source);
                if (frame == null)
                {
                    ScanStatus.Text = "The clipboard image could not be read.";
                    return;
                }

                ScanStatus.Text = "Scanning the clipboard image…";
                Dictionary<string, string> keys = _db.Players.Keys.ToDictionary(k => k, TableScanner.NormalizeName);

                _ = Task.Run(() =>
                {
                    ScanOutcome outcome = new() { TablesSeen = 1 };
                    try
                    {
                        TesseractEngine engine = OcrEngine();
                        bool hasFelt = TableScanner.FindFelt(frame, out Rect felt);
                        List<SeatBox> found = TableScanner.DetectSeats(frame, hasFelt ? felt : (Rect?)null, engine, "clipboard");
                        foreach (SeatBox seat in found)
                        {
                            (string key, double distance) = TableScanner.MatchPlayer(seat.OcrText, keys);
                            seat.PlayerKey = key;
                            seat.Distance = distance;
                            if (key.Length == 0 && seat.Confidence < 45) continue;
                            outcome.Boxes.Add(seat);
                        }
                        if (!hasFelt && outcome.Boxes.Count == 0)
                            outcome.Message = "No poker table found in that image (no felt and no name plates).";
                    }
                    catch (Exception ex)
                    {
                        PvLog.Error("PasteScan", ex);
                        outcome.Message = "Scan failed: " + ex.Message;
                    }
                    finally { frame.Dispose(); }

                    outcome.MatchedCount = outcome.Boxes.Count(b => b.Matched);
                    outcome.UnknownCount = outcome.Boxes.Count - outcome.MatchedCount;
                    Dispatcher.BeginInvoke(new Action(() => ShowScanResults(outcome)));
                });
            }
            catch (Exception ex)
            {
                PvLog.Error("PasteScan_Click", ex);
                ScanStatus.Text = "Clipboard scan failed: " + ex.Message;
            }
        }

        private static Mat? BitmapSourceToMat(BitmapSource source)
        {
            try
            {
                FormatConvertedBitmap converted = new(source, PixelFormats.Bgra32, null, 0);
                int w = converted.PixelWidth, h = converted.PixelHeight;
                int stride = w * 4;
                byte[] buffer = new byte[stride * h];
                converted.CopyPixels(buffer, stride, 0);

                Mat bgra = new(h, w, MatType.CV_8UC4);
                Marshal.Copy(buffer, 0, bgra.Data, buffer.Length);
                Mat bgr = new();
                Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
                bgra.Dispose();
                return bgr;
            }
            catch (Exception ex)
            {
                PvLog.Error("BitmapSourceToMat", ex);
                return null;
            }
        }

        /// <summary>
        /// The 300x110 patch around the cursor, cut out of the window under the cursor when there is one
        /// (so a window that covers the table, or our own overlay, cannot pollute the reading).
        /// Falls back to a plain screen capture when the cursor is not over a normal window.
        /// </summary>
        private static Mat? CaptureAroundCursor(int cx, int cy, ScreenInfo? screen)
        {
            const int halfWidth = 150, halfHeight = 55;
            Mat? shot = null;
            try
            {
                TableWindow? under = ScreenCapture.WindowUnderPoint(cx, cy);
                if (under != null)
                {
                    using Mat? windowFrame = ScreenCapture.CaptureWindow(under);
                    if (windowFrame != null)
                    {
                        int rx = cx - under.Bounds.X - halfWidth;
                        int ry = cy - under.Bounds.Y - halfHeight;
                        int w = Math.Min(halfWidth * 2, windowFrame.Width - rx);
                        int h = Math.Min(halfHeight * 2, windowFrame.Height - ry);
                        if (rx >= 0 && ry >= 0 && w >= 24 && h >= 14)
                        {
                            using Mat crop = new(windowFrame, new OpenCvSharp.Rect(rx, ry, w, h));
                            shot = crop.Clone();
                        }
                    }
                }
            }
            catch (Exception ex) { PvLog.Error("CaptureAroundCursor (window)", ex); }

            if (shot != null) return shot;

            Rect area = new(cx - halfWidth, cy - halfHeight, halfWidth * 2, halfHeight * 2);
            if (screen != null)
            {
                int left = (int)Math.Max(area.X, screen.Bounds.X);
                int top = (int)Math.Max(area.Y, screen.Bounds.Y);
                int right = (int)Math.Min(area.Right, screen.Bounds.Right);
                int bottom = (int)Math.Min(area.Bottom, screen.Bounds.Bottom);
                area = new Rect(left, top, Math.Max(8, right - left), Math.Max(8, bottom - top));
            }
            return ScreenCapture.CaptureArea(area);
        }

        /// <summary>
        /// Snip Player: mark the area with the player's name with the mouse and the name is read from that
        /// area. A name that is in the database opens that player (and gets a hover box right where the name
        /// was); an unknown name opens the "create player" dialog with the reading filled in.
        /// </summary>
        private void Snip_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _snipDone = OnSnipMarked;
                ShowSnipLayers("Mark the player's name: drag a box over it with the left mouse button " +
                               "(Esc or a right click cancels, a plain left click reads the name under the cursor).",
                               "[SNIP] marking layer shown");
            }
            catch (Exception ex)
            {
                PvLog.Error("Snip_Click", ex);
                CloseSnipOverlays();
                ScanStatus.Text = "Snip failed: " + ex.Message;
            }
        }

        /// <summary>
        /// Shows the drag-a-box layer on every screen. Pressing the button again while the layer is up cancels
        /// the marking (it was started by mistake).
        /// </summary>
        private void ShowSnipLayers(string status, string log)
        {
            if (_snipOverlays.Count > 0)
            {
                CloseSnipOverlays();
                ScanStatus.Text = "Snip cancelled.";
                return;
            }

            _screens = ScreenCapture.GetScreens();
            foreach (ScreenInfo screen in _screens)
            {
                SnipOverlay overlay = new(screen, area => _snipDone(area));
                overlay.Show();
                _snipOverlays.Add(overlay);
            }

            ScanStatus.Text = status;
            PvLog.Write(log);
        }

        private void CloseSnipOverlays()
        {
            foreach (SnipOverlay overlay in _snipOverlays.ToList()) overlay.CloseSafely();
            _snipOverlays.Clear();
        }

        /// <summary>The marked area came back (physical pixels): read the name in it and open/create the player.</summary>
        private void OnSnipMarked(System.Windows.Rect? area)
        {
            CloseSnipOverlays();
            try
            {
                if (area == null)
                {
                    ScanStatus.Text = "Snip cancelled.";
                    return;
                }

                System.Windows.Rect mark = area.Value;
                if (mark.Width < 12 || mark.Height < 10)
                {
                    // A click without dragging: read the name plate under the cursor instead.
                    ReadNameAtCursor((int)mark.X, (int)mark.Y);
                    return;
                }

                Rect marked = new((int)mark.X, (int)mark.Y, (int)mark.Width, (int)mark.Height);
                using Mat? patch = CaptureMarkedArea(marked);
                if (patch == null)
                {
                    ScanStatus.Text = "Could not capture the marked area.";
                    return;
                }

                TesseractEngine engine = OcrEngine();
                string text = TableScanner.ReadNameIn(patch, engine, out float confidence, out Rect namePlate);
                if (text.Length == 0)
                {
                    // A mark that is off (too far to the side, a name with a logo above it) still gets the
                    // reading around its middle as a second chance, the same reading a plain click uses.
                    text = TableScanner.ReadNameNear(patch, patch.Width / 2, patch.Height / 2, engine,
                                                     out confidence, out namePlate);
                    if (text.Length > 0)
                        PvLog.Write($"[SNIP] marked {mark.Width}x{mark.Height} -> '{text}' " +
                                    $"(conf {confidence:0}, from the middle of the mark)");
                }

                if (text.Length == 0)
                {
                    ScanStatus.Text = "No name could be read in the marked area - mark the name a little tighter " +
                                      "and try again.";
                    return;
                }

                // The box goes on the name that was read, not over the whole mark: a mark is usually much
                // bigger than the name (the stack below it, the felt around it), and a box that big looks
                // like it belongs to somebody else.
                Rect boxAt = namePlate.Width >= 24 && namePlate.Height >= 8
                    ? new Rect(marked.X + namePlate.X, marked.Y + namePlate.Y, namePlate.Width, namePlate.Height)
                    : marked;

                PvLog.Write($"[SNIP] marked {mark.Width}x{mark.Height} -> '{text}' (conf {confidence:0})" +
                            $", box {boxAt.Width}x{boxAt.Height}");
                HandleReadName(text, boxAt);
            }
            catch (Exception ex)
            {
                PvLog.Error("OnSnipMarked", ex);
                ScanStatus.Text = "Snip failed: " + ex.Message;
            }
        }

        /// <summary>
        /// The marked part of the window under it. PrintWindow is used when possible, so our own hover boxes
        /// and the marking layer can never be read as a name; the plain screen is the fallback.
        /// </summary>
        private static Mat? CaptureMarkedArea(Rect area)
        {
            try
            {
                TableWindow? under = ScreenCapture.WindowUnderPoint(area.X + area.Width / 2, area.Y + area.Height / 2);
                if (under != null)
                {
                    using Mat? frame = ScreenCapture.CaptureWindow(under);
                    if (frame != null)
                    {
                        int x = area.X - under.Bounds.X;
                        int y = area.Y - under.Bounds.Y;
                        int w = Math.Min(area.Width, frame.Width - x);
                        int h = Math.Min(area.Height, frame.Height - y);
                        if (x >= 0 && y >= 0 && w >= 8 && h >= 6)
                        {
                            using Mat crop = new(frame, new Rect(x, y, w, h));
                            return crop.Clone();
                        }
                    }
                }
            }
            catch (Exception ex) { PvLog.Error("CaptureMarkedArea (window)", ex); }

            return ScreenCapture.CaptureArea(area);
        }

        /// <summary>Reads the name plate at a point (a plain click on the marking layer).</summary>
        private void ReadNameAtCursor(int cx, int cy)
        {
            try
            {
                ScreenInfo? screen = ResolveScreens(cx, cy).FirstOrDefault();

                // Pointing at our own windows must never be read as a name: say so instead of offering to
                // create a player out of the notes program's own text.
                if (ScreenCapture.IsOwnWindowAt(cx, cy))
                {
                    ScanStatus.Text = "That is this program's own window - point at the player's name on the " +
                                      "table and press Snip again.";
                    return;
                }

                // Read the window *under* the cursor. That way the reading is the table's real text and
                // our own hover boxes (which are drawn on top of the table) can never be OCR'd by mistake.
                using Mat? frame = CaptureAroundCursor(cx, cy, screen);
                if (frame == null)
                {
                    ScanStatus.Text = "Could not capture the area under the cursor.";
                    return;
                }

                // The cursor is in the middle of the patch. OCR runs on a few bands around it and only a
                // reading that looks like a player name is accepted: a click on the felt, a chip stack or
                // an avatar used to end in the prompt "create the player '‘'".
                TesseractEngine engine = OcrEngine();
                string text = TableScanner.ReadNameNear(frame, frame.Width / 2, frame.Height / 2, engine, out float confidence);
                if (text.Length == 0)
                {
                    ScanStatus.Text = "No readable name under the cursor - point at the player's name and try " +
                                      "again, or select the player in the list and press Ctrl + middle mouse " +
                                      "button on the name to box him by hand.";
                    PvLog.Throttled("SnipNoName", "[SNIP] no player name in the patch under the cursor", 15);
                    return;
                }
                PvLog.Write($"[SNIP] cursor read '{text}' (conf {confidence:0})");
                HandleReadName(text, null);
            }
            catch (Exception ex)
            {
                PvLog.Error("ReadNameAtCursor", ex);
                ScanStatus.Text = "Snip failed: " + ex.Message;
            }
        }

        /// <summary>
        /// A name that was read becomes a player: the matching player is opened, a name that only *looks* like
        /// one is opened with a warning (so a duplicate is not created by accident), and otherwise the create
        /// dialog opens with the reading filled in. When an area was marked, that player also gets a hover box
        /// right where the name is.
        /// </summary>
        private void HandleReadName(string text, Rect? boxAt)
        {
            Dictionary<string, string> keys = _db.Players.Keys.ToDictionary(k => k, TableScanner.NormalizeName);
            (string key, _) = TableScanner.MatchPlayer(text, keys);

            if (key.Length == 0)
            {
                string similar = TableScanner.FindSimilarName(text, keys);
                if (similar.Length > 0)
                {
                    OpenPlayer(similar);
                    if (boxAt.HasValue) AddBoxAt(boxAt.Value);
                    ScanStatus.Text = $"Read '{text}' -> looks like {similar}, which is opened and boxed. " +
                                      "Correct the name above if it is the wrong player.";
                    return;
                }

                ScanStatus.Text = $"Read '{text}' -> not in the database: fill in the name and press OK.";
                CreatePlayerFromReading(text, boxAt);
                return;
            }

            OpenPlayer(key);
            if (boxAt.HasValue) AddBoxAt(boxAt.Value);
            ScanStatus.Text = boxAt.HasValue
                ? $"Read '{text}' -> opened {key} and put a box on the name."
                : $"Read '{text}' -> opened {key}";
        }

        /// <summary>Puts a box for the player that was read right on the name area that was read.</summary>
        private void AddBoxAt(Rect area)
        {
            if (area.Width <= 1 || area.Height <= 1) return;
            AddManualBox(area.X + area.Width / 2, area.Y + area.Height / 2, null, area);
        }

        private void ToggleBoxes_Click(object sender, RoutedEventArgs e)
        {
            _boxesOn = !_boxesOn;
            BtnBoxes.Content = _boxesOn ? "👁  Boxes: ON" : "👁  Boxes: OFF";
            RefreshBoxes();
            ScanStatus.Text = _boxesOn ? "Hover boxes are shown." : "Hover boxes are hidden (scanning still works).";
        }

        /// <summary>Switches between "everyone on the table" and "only players I already have notes on".</summary>
        private void ToggleOnlyDb_Click(object sender, RoutedEventArgs e)
        {
            _onlyDatabase = !_onlyDatabase;
            BtnOnlyDb.Content = _onlyDatabase ? OnlyDbOn : OnlyDbOff;
            try { File.WriteAllText(OnlyDbSettingPath, _onlyDatabase ? "1" : "0"); }
            catch (Exception ex) { PvLog.Error("SaveOnlyDb", ex); }

            RefreshBoxes();
            ScanStatus.Text = _onlyDatabase
                ? "Only players that are already in the database are boxed."
                : "Every reading is boxed (new names are marked with ?).";
            PvLog.Write($"[SCAN] database only filter: {_onlyDatabase}");
        }

        private void ClearBoxes_Click(object sender, RoutedEventArgs e)
        {
            _lastBoxes.Clear();
            _lastOutcome = null;
            int manual = _manualBoxes.Count;
            _manualBoxes.Clear();                   // boxes placed by hand are cleared here too
            _movedBoxes.Clear();                    // and the places boxes were dragged to are forgotten
            _hiddenBoxKeys.Clear();                 // a fresh start: removed boxes may come back on the next scan
            foreach (HoverOverlay overlay in _overlays.Values) overlay.ClearBoxes();
            ScanStatus.Text = manual > 0
                ? $"Hover boxes cleared ({manual} placed by hand included)."
                : "Hover boxes cleared (boxes removed with Ctrl+click are forgotten too).";
        }

        /// <summary>Result list used by Paste &amp; Scan (and handy for reviewing a scan).</summary>
        private void ShowScanResults(ScanOutcome outcome)
        {
            ScanNamePanel.Children.Clear();
            ScanResultTitle.Text = outcome.Message.Length > 0 ? "SCAN RESULT" : $"SCAN RESULT · {outcome.Boxes.Count} name(s)";
            ScanResultHint.Text = outcome.Message.Length > 0
                ? outcome.Message
                : $"{outcome.MatchedCount} known · {outcome.UnknownCount} not in the database. " +
                  "Click a name to open it; unknown names are offered for creation.";

            foreach (SeatBox box in outcome.Boxes
                         .OrderByDescending(b => b.Matched)
                         .ThenBy(b => b.OcrText, StringComparer.OrdinalIgnoreCase))
            {
                SeatBox captured = box;
                bool known = box.Matched;
                string label = known ? captured.PlayerKey : "? " + captured.OcrText;
                SolidColorBrush colour = known
                    ? new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E))
                    : new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

                ScanNamePanel.Children.Add(MakeChip(label, colour, filled: known, active: false, strike: false,
                    onClick: (s, e) =>
                    {
                        if (known) { OpenPlayer(captured.PlayerKey); CloseOverlays(); }
                        else CreatePlayerFromReading(captured.OcrText);
                    },
                    tooltip: known ? "Open " + captured.PlayerKey : "Not in the database - click to create"));
            }

            if (outcome.Boxes.Count == 0)
            {
                ScanNamePanel.Children.Add(new TextBlock
                {
                    Text = "No names found.",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
                    FontSize = 12
                });
            }

            ScanResultOverlay.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Asks for a name (prefilled with the reading from the screen) and creates the player. When
        /// <paramref name="boxAt"/> is given the new player also gets a hover box right there.
        /// </summary>
        private void CreatePlayerFromReading(string reading, Rect? boxAt = null)
        {
            ShowPrompt("Create player", reading.Trim(),
                "The name comes from the screen - correct it if the reading is off.",
                name =>
                {
                    string key = name.Trim();
                    if (key.Length == 0) return;

                    if (!_db.Players.ContainsKey(key))
                    {
                        _db.Players[key] = new PlayerEntry { Tags = new List<string>(), Notes = "" };
                        try { NotesStore.Save(_dbPath, _db); }
                        catch (Exception ex) { PvLog.Error("CreatePlayerFromReading", ex); }
                        RefreshList();
                    }

                    OpenPlayer(key);
                    if (boxAt.HasValue) AddBoxAt(boxAt.Value);
                    ScanStatus.Text = boxAt.HasValue
                        ? $"'{key}' is ready and has a box on the name - write the note."
                        : $"'{key}' is ready - write the note.";
                });
        }

        /// <summary>Saves the captured tables with the boxes and the readings drawn on them.</summary>
        private void DebugSnapshot_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = Path.Combine(PvLog.LogDirectory, "debug_snapshots");
                Directory.CreateDirectory(dir);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                System.Text.StringBuilder report = new();
                report.AppendLine($"Poker Notes scan snapshot {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                report.AppendLine($"boxes {_lastBoxes.Count}   known {_lastBoxes.Count(b => b.Matched)}   unknown {_lastBoxes.Count(b => !b.Matched)}");
                report.AppendLine("boxes screen rect + reading:");
                foreach (SeatBox box in _lastBoxes)
                    report.AppendLine($"   '{box.OcrText}' conf={box.Confidence:0} at {box.ScreenRect} -> {(box.Matched ? box.PlayerKey : "UNKNOWN")}");
                report.AppendLine();

                int saved = 0;
                foreach ((Mat frame, Point offset, string title) in _lastFrames)
                {
                    using Mat vis = frame.Clone();
                    foreach (SeatBox box in _lastBoxes)
                    {
                        Rect local = new(box.ScreenRect.X - offset.X, box.ScreenRect.Y - offset.Y,
                                         box.ScreenRect.Width, box.ScreenRect.Height);
                        Scalar colour = box.Matched ? new Scalar(0x5E, 0xC5, 0x22) : new Scalar(0xB8, 0xA3, 0x94);
                        Cv2.Rectangle(vis, local, colour, 2);
                        Cv2.PutText(vis, (box.Matched ? box.PlayerKey : "? " + box.OcrText) + $"  conf {box.Confidence:0}",
                            new Point(local.X, Math.Max(14, local.Y - 6)), HersheyFonts.HersheySimplex, 0.5, colour, 1);
                    }

                    string file = Path.Combine(dir, $"{stamp}_{saved}_table.png");
                    Cv2.ImWrite(file, vis);
                    saved++;
                }

                File.WriteAllText(Path.Combine(dir, $"{stamp}_readings.txt"), report.ToString());
                ScanStatus.Text = saved > 0
                    ? $"{saved} table snapshot(s) saved in {dir}"
                    : $"Readings saved in {dir} (run a capture first to get images)";
                PvLog.Write($"[SCAN] debug snapshot: {saved} image(s) in {dir}");
                if (saved > 0) Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                PvLog.Error("DebugSnapshot_Click", ex);
                ScanStatus.Text = "Snapshot failed: " + ex.Message;
            }
        }

        private void LoadTagsAndDb()
        {
            _tags = NotesStore.LoadTags();
            _dbPath = NotesStore.DefaultDbPath();
            try
            {
                _db = NotesStore.Load(_dbPath);
                PvLog.Write($"[DB] loaded {_db.Players.Count} player(s) from '{_dbPath}'");
            }
            catch (Exception ex)
            {
                PvLog.Error("LoadTagsAndDb", ex);
                ModernMessageBox.Show($"The notes file could not be read:\r\n\r\n{_dbPath}\r\n\r\n{ex.Message}\r\n\r\n" +
                                      "A new empty file will be used. The old file is left untouched.",
                                      "Poker Notes", MessageBoxButton.OK, MessageBoxImage.Warning);
                _db = new NotesFile();
            }

            BuildTagFilterChips();
            RefreshList();
            UpdateCounts();

            if (_rows.Count > 0) PlayerList.SelectedIndex = 0;
            else SelectPlayer(null);
        }

        private void RefreshList()
        {
            string keep = _currentKey ?? "";
            _rows.Clear();

            foreach (KeyValuePair<string, PlayerEntry> kv in _db.Players
                         .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!PlayerMatches(kv.Key, kv.Value)) continue;
                _rows.Add(new PlayerRow
                {
                    Key = kv.Key,
                    Name = kv.Key,
                    Preview = PreviewText(kv.Value),
                    TagSummary = TagSummaryText(kv.Value)
                });
            }

            ListCount.Text = _rows.Count + (_rows.Count == _db.Players.Count ? "" : $" / {_db.Players.Count}");
            UpdateCounts();

            if (keep.Length > 0)
            {
                PlayerRow? row = _rows.FirstOrDefault(r => r.Key == keep);
                if (row != null) PlayerList.SelectedItem = row;
            }
        }

        private void UpdateCounts()
        {
            StatusDb.Text = _dbPath;
            StatusCount.Text = $"{_db.Players.Count} player(s)   ·   {_tags.Count} tag(s)";
        }

        private bool PlayerMatches(string key, PlayerEntry entry)
        {
            if (_filterTag != null)
            {
                bool has = (entry.Tags ?? new List<string>())
                    .Any(t => string.Equals(CanonicalTag(t), _filterTag, StringComparison.OrdinalIgnoreCase));
                if (!has) return false;
            }

            if (_search.Length == 0) return true;
            if (key.Contains(_search, StringComparison.OrdinalIgnoreCase)) return true;
            if ((entry.Aliases ?? new List<string>()).Any(a => a.Contains(_search, StringComparison.OrdinalIgnoreCase))) return true;
            if ((entry.Notes ?? "").Contains(_search, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string PreviewText(PlayerEntry entry)
        {
            string notes = (entry.Notes ?? "").Trim();
            if (notes.Length == 0)
            {
                List<string> aliases = entry.Aliases ?? new List<string>();
                return aliases.Count > 0 ? "aka " + string.Join(", ", aliases) : "(no notes yet)";
            }

            string firstLine = notes.Split('\n')[0].Trim();
            return firstLine.Length > 96 ? firstLine.Substring(0, 96) + "…" : firstLine;
        }

        private string TagSummaryText(PlayerEntry entry)
        {
            List<string> tags = (entry.Tags ?? new List<string>()).Where(t => t.Trim().Length > 0).ToList();
            if (tags.Count == 0) return "";
            IEnumerable<string> shown = tags.Take(3).Select(TagDisplay);
            string text = string.Join(" · ", shown);
            return tags.Count > 3 ? text + $" +{tags.Count - 3}" : text;
        }

        // ================= tags =================

        /// <summary>Maps a tag as written on a player to the canonical tag key from the tag list.</summary>
        private string CanonicalTag(string raw)
        {
            string t = raw.Trim();
            TagDef? def = _tags.FirstOrDefault(d =>
                string.Equals(d.TagKey, t, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.DisplayName, t, StringComparison.OrdinalIgnoreCase));
            return def?.TagKey ?? t;
        }

        private string TagDisplay(string tag)
        {
            string t = tag.Trim();
            TagDef? def = _tags.FirstOrDefault(d =>
                string.Equals(d.TagKey, t, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.DisplayName, t, StringComparison.OrdinalIgnoreCase));
            return def?.DisplayName ?? t;
        }

        private SolidColorBrush TagBrush(string tag)
        {
            string t = tag.Trim();
            TagDef? def = _tags.FirstOrDefault(d =>
                string.Equals(d.TagKey, t, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.DisplayName, t, StringComparison.OrdinalIgnoreCase));
            return ColorFromHex(def?.ColorHex) ?? new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        }

        private static SolidColorBrush? ColorFromHex(string? hex)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hex)) return null;
                string h = hex.Trim().TrimStart('#');
                if (h.Length == 3) h = new string(new[] { h[0], h[0], h[1], h[1], h[2], h[2] });
                if (h.Length == 6) h = "FF" + h;
                if (h.Length != 8) return null;
                byte a = Convert.ToByte(h.Substring(0, 2), 16);
                byte r = Convert.ToByte(h.Substring(2, 2), 16);
                byte g = Convert.ToByte(h.Substring(4, 2), 16);
                byte b = Convert.ToByte(h.Substring(6, 2), 16);
                return new SolidColorBrush(Color.FromArgb(a, r, g, b));
            }
            catch { return null; }
        }

        private Button MakeChip(string label, SolidColorBrush color, bool filled, bool active, bool strike, RoutedEventHandler onClick, string tooltip)
        {
            Button b = new()
            {
                Content = label,
                Style = (Style)FindResource("Btn"),
                Padding = new Thickness(9, 3, 9, 3),
                Margin = new Thickness(0, 0, 6, 6),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                BorderThickness = new Thickness(active ? 2 : 1),
                ToolTip = tooltip
            };
            if (filled)
            {
                b.Background = color;
                b.Foreground = Brushes.White;
                b.BorderBrush = color;
                b.Opacity = strike ? 0.35 : 1.0;
            }
            else
            {
                b.Background = new SolidColorBrush(Color.FromArgb(34, color.Color.R, color.Color.G, color.Color.B));
                b.Foreground = color;
                b.BorderBrush = color;
            }
            if (strike) b.Content = label + "  ✕";
            b.Click += onClick;
            return b;
        }

        private void BuildTagFilterChips()
        {
            TagFilterPanel.Children.Clear();
            if (_tags.Count == 0) return;

            TagFilterPanel.Children.Add(MakeChip("All", new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)),
                filled: _filterTag == null, active: _filterTag == null, strike: false,
                onClick: (s, e) => { _filterTag = null; BuildTagFilterChips(); RefreshList(); },
                tooltip: "Show every player"));

            foreach (TagDef def in _tags.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                if (def.TagKey.StartsWith("tag_", StringComparison.OrdinalIgnoreCase)) continue;   // leftovers from earlier versions
                SolidColorBrush color = ColorFromHex(def.ColorHex) ?? new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
                bool active = string.Equals(_filterTag, def.TagKey, StringComparison.OrdinalIgnoreCase);
                string tagKey = def.TagKey;
                TagFilterPanel.Children.Add(MakeChip(def.DisplayName, color, filled: active, active: active, strike: false,
                    onClick: (s, e) =>
                    {
                        _filterTag = active ? null : tagKey;
                        BuildTagFilterChips();
                        RefreshList();
                    },
                    tooltip: $"Show players tagged '{def.DisplayName}'"));
            }
        }

        // ================= selection & editor =================

        private void PlayerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlayerList.SelectedItem is PlayerRow row && row.Key != _currentKey)
            {
                SaveCurrent();
                SelectPlayer(row.Key);
            }
        }

        private void SelectPlayer(string? key)
        {
            _currentKey = key;

            if (key == null || !_db.Players.TryGetValue(key, out PlayerEntry? entry))
            {
                EmptyHint.Visibility = Visibility.Visible;
                EditorBody.Visibility = Visibility.Collapsed;
                return;
            }

            EmptyHint.Visibility = Visibility.Collapsed;
            EditorBody.Visibility = Visibility.Visible;

            _loading = true;
            PlayerNameText.Text = key;
            AliasBox.Text = string.Join(", ", entry.Aliases ?? new List<string>());
            NotesBox.Text = entry.Notes ?? "";
            _loading = false;

            BuildPlayerTagPanel(entry);
            BuildTagPickPanel(entry);
            NotesInfoUpdate();
            SavedText.Text = "";
        }

        private void NotesInfoUpdate()
        {
            string t = NotesBox.Text ?? "";
            int lines = t.Length == 0 ? 0 : t.Split('\n').Length;
            int words = t.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
            NotesInfo.Text = $"{t.Length} chars · {words} word(s) · {lines} line(s)";
        }

        private void BuildPlayerTagPanel(PlayerEntry entry)
        {
            PlayerTagPanel.Children.Clear();
            List<string> tags = (entry.Tags ?? new List<string>()).Where(t => t.Trim().Length > 0).ToList();

            if (tags.Count == 0)
            {
                PlayerTagPanel.Children.Add(new TextBlock
                {
                    Text = "no tags - click one below to add it",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
                    FontSize = 11,
                    Margin = new Thickness(0, 2, 0, 4)
                });
                return;
            }

            foreach (string tag in tags.OrderBy(TagDisplay, StringComparer.OrdinalIgnoreCase))
            {
                string key = CanonicalTag(tag);
                PlayerTagPanel.Children.Add(MakeChip(TagDisplay(tag), TagBrush(tag), filled: true, active: false, strike: true,
                    onClick: (s, e) => ToggleTag(key),
                    tooltip: "Click to remove this tag"));
            }
        }

        private void BuildTagPickPanel(PlayerEntry entry)
        {
            TagPickPanel.Children.Clear();
            List<string> playerTags = (entry.Tags ?? new List<string>()).Select(CanonicalTag).ToList();

            List<TagDef> pool = _tags
                .Where(d => !d.TagKey.StartsWith("tag_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (pool.Count == 0)
            {
                TagPickPanel.Children.Add(new TextBlock
                {
                    Text = "no tags defined - add some in the 🏷 Tags dialog",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
                    FontSize = 11,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                return;
            }

            foreach (TagDef def in pool)
            {
                bool has = playerTags.Contains(def.TagKey, StringComparer.OrdinalIgnoreCase);
                string key = def.TagKey;
                TagPickPanel.Children.Add(MakeChip(
                    (has ? "✓ " : "＋ ") + def.DisplayName,
                    ColorFromHex(def.ColorHex) ?? new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
                    filled: has, active: false, strike: false,
                    onClick: (s, e) => ToggleTag(key),
                    tooltip: has ? "Click to remove this tag" : "Click to add this tag"));
            }
        }

        private void ToggleTag(string tagKey)
        {
            if (_currentKey == null || !_db.Players.TryGetValue(_currentKey, out PlayerEntry? entry)) return;

            entry.Tags ??= new List<string>();
            bool has = entry.Tags.Any(t => string.Equals(CanonicalTag(t), tagKey, StringComparison.OrdinalIgnoreCase));
            if (has) entry.Tags.RemoveAll(t => string.Equals(CanonicalTag(t), tagKey, StringComparison.OrdinalIgnoreCase));
            else entry.Tags.Add(tagKey);

            entry.Tags = entry.Tags.Where(t => t.Trim().Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            MarkDirty();
            BuildPlayerTagPanel(entry);
            BuildTagPickPanel(entry);
            RefreshRow(_currentKey);
            SaveCurrent();
        }

        // ================= saving =================

        private void MarkDirty()
        {
            _dirty = true;
            _autoSave.Stop();
            _autoSave.Start();
        }

        private void NotesBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            NotesInfoUpdate();
            if (_currentKey == null) return;
            MarkDirty();
        }

        private void AliasBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || _currentKey == null) return;
            MarkDirty();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _search = (SearchBox.Text ?? "").Trim();
            RefreshList();
        }

        private void SaveNow_Click(object sender, RoutedEventArgs e) => SaveCurrent(force: true);

        /// <summary>
        /// Stores the editor in the model and writes the file. Deliberately silent: no dialogs and
        /// no window activation, so it never interrupts typing.
        /// </summary>
        private void SaveCurrent(bool force = false)
        {
            _autoSave.Stop();
            if (_currentKey == null || !_db.Players.TryGetValue(_currentKey, out PlayerEntry? entry)) return;

            bool aliasChanged = false;
            if (!_loading)
            {
                string currentAlias = string.Join(", ", entry.Aliases ?? new List<string>());
                aliasChanged = currentAlias != (AliasBox.Text ?? "").Trim();
            }
            if (!_dirty && !force && !aliasChanged) return;

            entry.Notes = NotesBox.Text ?? "";
            List<string> aliases = (AliasBox.Text ?? "")
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Trim())
                .Where(a => a.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            entry.Aliases = aliases.Count > 0 ? aliases : null;

            try
            {
                NotesStore.Save(_dbPath, _db);
                _dirty = false;
                RefreshRow(_currentKey);
                SavedText.Text = "saved " + DateTime.Now.ToString("HH:mm:ss");
                _savedFlash.Stop();
                _savedFlash.Start();
                UpdateCounts();
            }
            catch (Exception ex)
            {
                PvLog.Error("SaveCurrent", ex);
                SavedText.Text = "save failed";
            }
        }

        private void RefreshRow(string key)
        {
            if (!_db.Players.TryGetValue(key, out PlayerEntry? entry)) return;
            PlayerRow? row = _rows.FirstOrDefault(r => r.Key == key);
            if (row == null) return;
            row.Preview = PreviewText(entry);
            row.TagSummary = TagSummaryText(entry);
        }

        // ================= players =================

        private void NewPlayer_Click(object sender, RoutedEventArgs e)
        {
            ShowPrompt("New player", "", "Write the name exactly as it appears at the table.", name =>
            {
                string key = name.Trim();
                if (key.Length == 0) return;

                SaveCurrent();

                if (_db.Players.ContainsKey(key))
                {
                    SelectExisting(key);
                    ModernMessageBox.Show($"'{key}' is already in the database.", "Poker Notes",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                _db.Players[key] = new PlayerEntry { Tags = new List<string>(), Notes = "" };
                try { NotesStore.Save(_dbPath, _db); }
                catch (Exception ex) { PvLog.Error("NewPlayer save", ex); }

                _search = "";
                SearchBox.Text = "";
                BuildTagFilterChips();
                RefreshList();
                SelectExisting(key);
                NotesBox.Focus();
            });
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            if (_currentKey == null) return;
            string oldKey = _currentKey;

            ShowPrompt("Rename player", oldKey, "Notes, tags, aliases and history follow the new name.", name =>
            {
                string newKey = name.Trim();
                if (newKey.Length == 0 || newKey == oldKey) return;
                if (_db.Players.ContainsKey(newKey))
                {
                    ModernMessageBox.Show($"'{newKey}' already exists.", "Poker Notes", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                SaveCurrent();
                PlayerEntry entry = _db.Players[oldKey];
                _db.Players.Remove(oldKey);
                _db.Players[newKey] = entry;
                _currentKey = newKey;

                try { NotesStore.Save(_dbPath, _db); }
                catch (Exception ex) { PvLog.Error("Rename save", ex); }

                RefreshList();
                SelectExisting(newKey);
            });
        }

        private void DeletePlayer_Click(object sender, RoutedEventArgs e)
        {
            if (_currentKey == null) return;
            string key = _currentKey;
            _autoSave.Stop();

            MessageBoxResult answer = ModernMessageBox.Show(
                $"Delete '{key}'?\r\n\r\nAll notes, tags and history for this player are removed. " +
                "A .bak copy of the file from before this save is kept next to the database.",
                "Poker Notes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            _db.Players.Remove(key);
            _dirty = false;
            _currentKey = null;

            try { NotesStore.Save(_dbPath, _db); }
            catch (Exception ex) { PvLog.Error("Delete save", ex); }

            RefreshList();
            if (_rows.Count > 0) PlayerList.SelectedIndex = 0;
            else SelectPlayer(null);
        }

        private void SelectExisting(string key)
        {
            PlayerRow? row = _rows.FirstOrDefault(r => r.Key == key);
            if (row != null)
            {
                PlayerList.SelectedItem = row;
                PlayerList.ScrollIntoView(row);
                if (_currentKey != key) SelectPlayer(key);   // filtered out of the list
            }
            else
            {
                SelectPlayer(key);
            }
        }

        // ================= small prompt dialog =================

        private Action<string>? _promptAction;

        private void ShowPrompt(string title, string initial, string hint, Action<string> onOk)
        {
            _promptAction = onOk;
            PromptTitle.Text = title;
            PromptHint.Text = hint;
            PromptInput.Text = initial;
            PromptOverlay.Visibility = Visibility.Visible;
            PromptInput.Focus();
            PromptInput.SelectAll();
        }

        private void PromptOk_Click(object sender, RoutedEventArgs e) => ConfirmPrompt();

        private void PromptInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { ConfirmPrompt(); e.Handled = true; }
        }

        private void ConfirmPrompt()
        {
            string value = PromptInput.Text ?? "";
            Action<string>? action = _promptAction;
            _promptAction = null;
            CloseOverlays();
            action?.Invoke(value);
        }

        // ================= note history =================

        private void Snapshot_Click(object sender, RoutedEventArgs e)
        {
            if (_currentKey == null || !_db.Players.TryGetValue(_currentKey, out PlayerEntry? entry)) return;
            string text = NotesBox.Text ?? "";
            if (text.Trim().Length == 0) return;

            entry.NoteHistory ??= new List<NoteSnapshot>();
            entry.NoteHistory.Insert(0, new NoteSnapshot
            {
                When = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Text = text
            });
            if (entry.NoteHistory.Count > 40)
                entry.NoteHistory.RemoveRange(40, entry.NoteHistory.Count - 40);

            MarkDirty();
            SaveCurrent(force: true);
            SavedText.Text = "copy stored " + DateTime.Now.ToString("HH:mm:ss");
            _savedFlash.Stop();
            _savedFlash.Start();
        }

        private void History_Click(object sender, RoutedEventArgs e)
        {
            if (_currentKey == null || !_db.Players.TryGetValue(_currentKey, out PlayerEntry? entry)) return;

            HistoryTitle.Text = "🕘  NOTE HISTORY · " + _currentKey;
            HistoryList.ItemsSource = (entry.NoteHistory ?? new List<NoteSnapshot>())
                .Select(h => new HistoryRow(h.When, h.Text))
                .ToList();
            HistoryOverlay.Visibility = Visibility.Visible;
        }

        private void HistoryClear_Click(object sender, RoutedEventArgs e)
        {
            if (_currentKey == null || !_db.Players.TryGetValue(_currentKey, out PlayerEntry? entry)) return;
            if ((entry.NoteHistory?.Count ?? 0) == 0) return;

            if (ModernMessageBox.Show("Delete every stored copy of this note?", "Poker Notes",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            entry.NoteHistory = null;
            _dirty = true;
            SaveCurrent(force: true);
            HistoryList.ItemsSource = null;
            CloseOverlays();
        }

        private void HistoryLoad_Click(object sender, RoutedEventArgs e)
        {
            if (HistoryList.SelectedItem is not HistoryRow row) return;
            NotesBox.Text = row.Text;
            MarkDirty();
            CloseOverlays();
            NotesBox.Focus();
        }

        // ================= tag manager =================

        private void Tags_Click(object sender, RoutedEventArgs e)
        {
            RefreshTagManageList();
            TagsOverlay.Visibility = Visibility.Visible;
            NewTagName.Focus();
        }

        private void RefreshTagManageList()
        {
            TagManageList.ItemsSource = _tags
                .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(t => new TagRowItem
                {
                    TagKey = t.TagKey,
                    DisplayName = t.DisplayName,
                    ColorHex = t.ColorHex,
                    Brush = ColorFromHex(t.ColorHex) ?? new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B))
                })
                .ToList();
        }

        private void NewTagName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { TagAdd_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        }

        private void TagAdd_Click(object sender, RoutedEventArgs e)
        {
            string name = (NewTagName.Text ?? "").Trim();
            if (name.Length == 0) return;

            string color = (NewTagColor.Text ?? "").Trim();
            if (ColorFromHex(color) == null) color = "#808080";

            string key = name.ToLowerInvariant();
            TagDef? existing = _tags.FirstOrDefault(t => string.Equals(t.TagKey, key, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.DisplayName = name;
                existing.ColorHex = color;
            }
            else
            {
                _tags.Add(new TagDef { TagKey = key, DisplayName = name, ColorHex = color });
            }

            NotesStore.SaveTags(_tags);
            NewTagName.Text = "";
            RefreshTagManageList();
            BuildTagFilterChips();
            if (_currentKey != null && _db.Players.TryGetValue(_currentKey, out PlayerEntry? entry))
            {
                BuildPlayerTagPanel(entry);
                BuildTagPickPanel(entry);
            }
            UpdateCounts();
        }

        private void TagDelete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not TagRowItem row) return;

            int used = _db.Players.Values.Count(p => (p.Tags ?? new List<string>())
                .Any(t => string.Equals(CanonicalTag(t), row.TagKey, StringComparison.OrdinalIgnoreCase)));

            if (ModernMessageBox.Show($"Delete tag '{row.DisplayName}'?\r\n\r\nIt is removed from {used} player(s).",
                    "Poker Notes", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            _tags.RemoveAll(t => string.Equals(t.TagKey, row.TagKey, StringComparison.OrdinalIgnoreCase));
            NotesStore.SaveTags(_tags);

            foreach (PlayerEntry p in _db.Players.Values)
                p.Tags?.RemoveAll(t => string.Equals(CanonicalTag(t), row.TagKey, StringComparison.OrdinalIgnoreCase));

            try { NotesStore.Save(_dbPath, _db); }
            catch (Exception ex) { PvLog.Error("TagDelete save", ex); }

            if (string.Equals(_filterTag, row.TagKey, StringComparison.OrdinalIgnoreCase)) _filterTag = null;

            RefreshTagManageList();
            BuildTagFilterChips();
            RefreshList();
            UpdateCounts();
            if (_currentKey != null && _db.Players.TryGetValue(_currentKey, out PlayerEntry? cur))
            {
                BuildPlayerTagPanel(cur);
                BuildTagPickPanel(cur);
            }
        }

        // ================= files =================

        private void OpenDb_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrent(force: true);

            Microsoft.Win32.OpenFileDialog dlg = new()
            {
                Title = "Open notes file",
                Filter = "Notes file (*.json)|*.json|All files (*.*)|*.*",
                InitialDirectory = Path.GetDirectoryName(_dbPath) ?? ""
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                _dbPath = dlg.FileName;
                _db = NotesStore.Load(_dbPath);
                NotesStore.SaveSettings(_dbPath);
                PvLog.Write($"[DB] opened '{_dbPath}' with {_db.Players.Count} player(s)");
            }
            catch (Exception ex)
            {
                PvLog.Error("OpenDb", ex);
                ModernMessageBox.Show("The file could not be opened:\r\n\r\n" + ex.Message, "Poker Notes",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _currentKey = null;
            _filterTag = null;
            _search = "";
            SearchBox.Text = "";
            BuildTagFilterChips();
            RefreshList();
            UpdateCounts();
            if (_rows.Count > 0) PlayerList.SelectedIndex = 0;
            else SelectPlayer(null);
        }

        private void ExportCsv_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrent(force: true);

            Microsoft.Win32.SaveFileDialog dlg = new()
            {
                Title = "Export players",
                Filter = "CSV (*.csv)|*.csv",
                FileName = "poker_notes_" + DateTime.Now.ToString("yyyy-MM-dd") + ".csv",
                InitialDirectory = Path.GetDirectoryName(_dbPath) ?? ""
            };
            if (dlg.ShowDialog(this) != true) return;

            System.Text.StringBuilder sb = new();
            sb.AppendLine("player;aliases;tags;notes;history_copies");
            foreach (KeyValuePair<string, PlayerEntry> kv in _db.Players.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(string.Join(";", new[]
                {
                    Csv(kv.Key),
                    Csv(string.Join(", ", kv.Value.Aliases ?? new List<string>())),
                    Csv(string.Join(", ", (kv.Value.Tags ?? new List<string>()).Select(TagDisplay))),
                    Csv(kv.Value.Notes ?? ""),
                    (kv.Value.NoteHistory?.Count ?? 0).ToString(CultureInfo.InvariantCulture)
                }));
            }

            try
            {
                File.WriteAllText(dlg.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));
                ModernMessageBox.Show($"{_db.Players.Count} player(s) exported to:\r\n\r\n{dlg.FileName}",
                    "Poker Notes", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                PvLog.Error("ExportCsv", ex);
                ModernMessageBox.Show("Export failed:\r\n\r\n" + ex.Message, "Poker Notes", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string Csv(string value)
        {
            if (value.IndexOfAny(new[] { ';', '"', '\n', '\r' }) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        // ================= overlays, help and log =================

        private bool AnyOverlayOpen() =>
            TagsOverlay.Visibility == Visibility.Visible ||
            HistoryOverlay.Visibility == Visibility.Visible ||
            PromptOverlay.Visibility == Visibility.Visible ||
            ScanResultOverlay.Visibility == Visibility.Visible ||
            HelpOverlay.Visibility == Visibility.Visible;

        private void CloseOverlays()
        {
            TagsOverlay.Visibility = Visibility.Collapsed;
            HistoryOverlay.Visibility = Visibility.Collapsed;
            PromptOverlay.Visibility = Visibility.Collapsed;
            ScanResultOverlay.Visibility = Visibility.Collapsed;
            HelpOverlay.Visibility = Visibility.Collapsed;
            _promptAction = null;
            CloseSnipOverlays();
            _notePopup?.CloseSafely();
        }

        private void CloseOverlays_Click(object sender, RoutedEventArgs e) => CloseOverlays();

        private void Help_Click(object sender, RoutedEventArgs e) => HelpOverlay.Visibility = Visibility.Visible;

        private void OpenLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string target = File.Exists(PvLog.LogFilePath) ? PvLog.LogFilePath : PvLog.LogDirectory;
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex) { PvLog.Error("OpenLog", ex); }
        }
    }

    /// <summary>Row in the player list: only the three texts the list shows.</summary>
    public sealed class PlayerRow : INotifyPropertyChanged
    {
        private string _preview = "";
        private string _tagSummary = "";

        public string Key { get; init; } = "";
        public string Name { get; init; } = "";

        public string Preview
        {
            get => _preview;
            set { _preview = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview))); }
        }

        public string TagSummary
        {
            get => _tagSummary;
            set { _tagSummary = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagSummary))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Row in the tag manager list.</summary>
    public sealed class TagRowItem
    {
        public string TagKey { get; init; } = "";
        public string DisplayName { get; set; } = "";
        public string ColorHex { get; set; } = "";
        public SolidColorBrush Brush { get; set; } = new(Color.FromRgb(0x64, 0x74, 0x8B));
    }

    /// <summary>Row in the note history list.</summary>
    public sealed class HistoryRow
    {
        public HistoryRow(string when, string text)
        {
            When = when;
            Text = text;
        }

        public string When { get; }
        public string Text { get; }
    }
}
