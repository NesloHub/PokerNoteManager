using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PokerNoteManager.Vision
{
    /// <summary>
    /// Small note editor that opens right on top of a hover box, so a note can be written without
    /// leaving the tables. Name (only for players that are new), tags and the note text are all
    /// edited in place.
    ///
    /// The window is a normal tool window: it is opened by an explicit click, so taking the keyboard
    /// while it is open is exactly what the user asked for. It closes itself when the user clicks
    /// away *unless* something was typed, so no work is lost.
    /// </summary>
    public partial class NotePopup : Window
    {
        private readonly Action<string, List<string>, string> _onSave;
        private readonly Action _openInMain;
        private readonly List<(TagDef Def, Button Button)> _chips = new();
        private readonly Dictionary<string, bool> _selected = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _initialTags = new(StringComparer.OrdinalIgnoreCase);

        private string _initialName;
        private string _initialNote;
        private readonly int _physX, _physY;      // where the box was clicked, in physical screen pixels
        private bool _activated;
        private bool _closing;               // guards against Close() being re-entered while closing

        /// <summary>When the editor was built - see <see cref="OpenGraceMs"/>.</summary>
        private readonly long _openedTicks = Environment.TickCount64;
        private DispatcherTimer? _focusTimer;
        private int _focusAttempts;

        /// <summary>
        /// A click on a hover box reaches the poker client *and* opens this editor, and the activation
        /// that the client gets in the same click would deactivate (and close) the brand new editor
        /// again. Deactivations inside this grace period are therefore ignored, which is what makes the
        /// editor appear on the *first* click - on a nine table set that is the whole difference.
        /// </summary>
        private const int OpenGraceMs = 600;

        /// <summary>The editor claims the keyboard again a few times after opening, because Windows does
        /// not always let the process that owns the click take the foreground. About 600 ms, the same
        /// window as the grace period above.</summary>
        private const int FocusAttempts = 5;

        /// <param name="known">True when the player is already in the database.</param>
        /// <param name="playerName">Database key, or the reading from the screen for a new player.</param>
        /// <param name="note">Current note text.</param>
        /// <param name="allTags">Every tag defined in the program.</param>
        /// <param name="playerTags">Tags the player already has.</param>
        /// <param name="onSave">Called with (name, selected tag keys, note) when Save is pressed.</param>
        /// <param name="openInMain">Called when "Open in program" is pressed.</param>
        /// <param name="physX">X of the clicked box in physical screen pixels (for placement).</param>
        /// <param name="physY">Y of the clicked box in physical screen pixels (for placement).</param>
        /// <param name="similarName">Database player the reading looks like, when the reading was not
        /// matched (so a near miss does not silently create a duplicate). "" when there is none.</param>
        public NotePopup(bool known, string playerName, string note, IReadOnlyList<TagDef> allTags,
                         IReadOnlyList<string> playerTags,
                         Action<string, List<string>, string> onSave, Action openInMain,
                         int physX = -1, int physY = -1, string similarName = "")
        {
            InitializeComponent();

            _onSave = onSave;
            _openInMain = openInMain;
            _physX = physX;
            _physY = physY;
            _initialName = (playerName ?? "").Trim();
            _initialNote = note ?? "";

            CaptionText.Text = known ? "✎  " + _initialName : "＋  New player";
            NameBox.Text = playerName ?? "";
            NameBox.IsReadOnly = known;
            NameHint.Visibility = known ? Visibility.Collapsed : Visibility.Visible;
            NameHint.Text = string.IsNullOrWhiteSpace(similarName)
                ? "The name comes from the screen - correct it if the reading is off. Saving creates the player."
                : "'" + similarName.Trim() + "' in the database looks almost the same. Correct the name "
                  + "above to write on that player, or save as it is to create a new one.";
            NoteBox.Text = _initialNote;

            foreach (string tag in playerTags ?? Array.Empty<string>())
            {
                string t = (tag ?? "").Trim();
                if (t.Length == 0) continue;
                _selected[t] = true;
                _initialTags.Add(t);
            }

            BuildTagChips(allTags ?? Array.Empty<TagDef>());
            NoTagsHint.Visibility = _chips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            PreviewKeyDown += OnPreviewKeyDown;
            Loaded += OnLoaded;
        }

        private void BuildTagChips(IReadOnlyList<TagDef> allTags)
        {
            foreach (TagDef def in allTags.Where(d => (d.TagKey ?? "").Trim().Length > 0)
                                          .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                Button button = new()
                {
                    Margin = new Thickness(0, 0, 6, 6),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Cursor = Cursors.Hand,
                    ToolTip = "Toggle the tag '" + def.DisplayName + "'"
                };
                button.Click += Tag_Click;
                _chips.Add((def, button));
                PaintChip(def, button, IsSelected(def));
                TagPanel.Children.Add(button);
            }
        }

        /// <summary>A tag counts as selected when it is ticked by key or by display name.</summary>
        private bool IsSelected(TagDef def) =>
            _selected.ContainsKey((def.TagKey ?? "").Trim()) ||
            _selected.ContainsKey((def.DisplayName ?? "").Trim());

        private void Tag_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;
            int index = _chips.FindIndex(c => ReferenceEquals(c.Button, button));
            if (index < 0) return;

            TagDef def = _chips[index].Def;
            bool on = !IsSelected(def);
            _selected.Remove((def.TagKey ?? "").Trim());
            _selected.Remove((def.DisplayName ?? "").Trim());
            if (on)
            {
                string mark = (def.TagKey ?? def.DisplayName ?? "").Trim();
                if (mark.Length > 0) _selected[mark] = true;
            }

            PaintChip(def, button, on);
        }

        private static void PaintChip(TagDef def, Button button, bool on)
        {
            Color colour = ParseHex(def.ColorHex, Color.FromRgb(0x64, 0x74, 0x8B));
            button.Content = on ? "✓ " + def.DisplayName : def.DisplayName;
            button.Foreground = new SolidColorBrush(on ? Colors.White : Brighten(colour));
            button.Background = new SolidColorBrush(on
                ? colour
                : Color.FromArgb(0x28, colour.R, colour.G, colour.B));
            button.BorderBrush = new SolidColorBrush(colour);
            button.BorderThickness = new Thickness(1);
            button.Padding = new Thickness(9, 4, 9, 4);
        }

        /// <summary>Tag keys of the ticked tags, in the order of the tag list.</summary>
        private List<string> SelectedTags()
        {
            List<string> result = new();
            foreach ((TagDef def, _) in _chips)
                if (IsSelected(def)) result.Add(def.TagKey);
            return result;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Place(_physX, _physY);

            BringToFront();
            FocusEditor();
            StartFocusRetry();
        }

        /// <summary>
        /// Puts the editor next to the clicked box, in physical pixels, clamped to the work area of the
        /// monitor the click was on. WPF's own Left/Top are in device independent units of whichever
        /// monitor the window starts on, which on a multi monitor setup with different DPI easily puts the
        /// editor off screen - one of the reasons it looked like it "did not open".
        /// </summary>
        private void Place(int physX, int physY)
        {
            if (physX < 0 || physY < 0) return;          // no click position: leave WPF's placement alone
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                var work = ScreenCapture.WorkAreaForPoint(physX, physY);
                if (work.Width <= 0 || work.Height <= 0) return;

                double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                if (scale <= 0.1) scale = 1.0;
                int width = (int)Math.Ceiling((ActualWidth > 0 ? ActualWidth : Width) * scale);
                int height = (int)Math.Ceiling((ActualHeight > 0 ? ActualHeight : 220) * scale);

                const int margin = 10;
                int x = physX + 18;
                int y = physY - 10;
                if (x + width > work.Right - margin) x = physX - width - 18;        // no room on the right
                if (y + height > work.Bottom - margin) y = physY - height + 10;     // no room below
                x = Math.Clamp(x, work.X + margin, Math.Max(work.X + margin, work.Right - width - margin));
                y = Math.Clamp(y, work.Y + margin, Math.Max(work.Y + margin, work.Bottom - height - margin));

                SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                PvLog.Throttled("PopupPlace", $"[POPUP] placed at {x},{y} ({width}x{height}) - click {physX},{physY} " +
                                             $"on work area {work.X},{work.Y} {work.Width}x{work.Height}", 15);
            }
            catch (Exception ex) { PvLog.Error("NotePopup.Place", ex); }
        }

        /// <summary>True while the editor is on screen and not on its way out.</summary>
        public bool IsUsable => IsLoaded && !_closing;

        /// <summary>
        /// Brings the editor to the front *and* gives it the keyboard. Windows only lets the process
        /// that owns the last input take the foreground, and the click that opened this editor belongs
        /// to the poker client, so the foreground window's input queue is attached for the call. That is
        /// what makes the editor ready to type in on the very first click.
        /// </summary>
        public void BringToFront()
        {
            try
            {
                if (IsLoaded)
                {
                    IntPtr hwnd = new WindowInteropHelper(this).Handle;
                    IntPtr foreground = GetForegroundWindow();
                    uint other = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
                    uint mine = GetCurrentThreadId();
                    bool attached = other != 0 && other != mine && AttachThreadInput(other, mine, true);
                    try { SetForegroundWindow(hwnd); }
                    finally { if (attached) AttachThreadInput(other, mine, false); }
                    Topmost = true;      // the hover boxes are topmost too: stay above the table
                }

                Activate();
                if (IsActive) FocusEditor();
            }
            catch (Exception ex) { PvLog.Error("NotePopup.BringToFront", ex); }
        }

        /// <summary>Puts the caret in the box the user wants first.</summary>
        private void FocusEditor()
        {
            if (NameBox.IsReadOnly) { NoteBox.Focus(); NoteBox.CaretIndex = NoteBox.Text.Length; }
            else { NameBox.Focus(); NameBox.SelectAll(); }
        }

        /// <summary>
        /// Repeats the attempt to take the keyboard for a short while: the poker client takes the
        /// foreground from the click that opened the editor, so one attempt is not enough.
        /// </summary>
        private void StartFocusRetry()
        {
            if (_focusTimer == null)
            {
                _focusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                _focusTimer.Tick += FocusRetryTick;
            }

            _focusAttempts = 0;
            _focusTimer.Start();
        }

        private void FocusRetryTick(object? sender, EventArgs e)
        {
            if (_closing || !IsLoaded || IsActive || ++_focusAttempts > FocusAttempts)
            {
                _focusTimer?.Stop();
                if (IsActive) FocusEditor();
                return;
            }

            // A move to a monitor with another DPI makes WPF re-place the window (WM_DPICHANGED), so the
            // position is set again while the editor is still fighting for the keyboard.
            Place(_physX, _physY);
            BringToFront();
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new(-2);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            _activated = true;
        }

        /// <summary>
        /// Clicking away closes the popup - but keeps it open while there is unsaved typing.
        ///
        /// WPF sends an activation change *while* a window is closing, so Close() must never be
        /// re-entered here: that is what produced "Cannot set Visibility ... while a Window is
        /// closing" before. CloseSafely() sets the flag that makes this return immediately.
        /// </summary>
        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
            if (_closing || !_activated || !IsLoaded) return;

            // The click that opened the editor makes the poker client the foreground window a moment
            // later, and that deactivation must not close the brand new editor - it would need a second
            // click, which is exactly what made it feel broken on a nine table set.
            if (Environment.TickCount64 - _openedTicks < OpenGraceMs) return;

            if (!HasChanges()) CloseSafely();
        }

        /// <summary>True when something was typed that has not been saved yet.</summary>
        public bool HasUnsavedChanges => !_closing && HasChanges();

        /// <summary>
        /// Saves right away, exactly like pressing the Save button, and then treats the current text as
        /// the saved state. Used when the editor is replaced by another one (the same player sits at
        /// several tables, so a second box may be clicked) - without that the replaced editor stayed
        /// "dirty" forever and would not close itself, so stale editors piled up on top of the table.
        /// </summary>
        public void SaveNow()
        {
            Save();
            MarkSaved();
        }

        /// <summary>Remembers what is in the editor now as the saved state (so it is not dirty any more).</summary>
        private void MarkSaved()
        {
            _initialName = (NameBox.Text ?? "").Trim();
            _initialNote = NoteBox.Text ?? "";
            _initialTags.Clear();
            foreach (string tag in SelectedTags()) _initialTags.Add(tag);
        }

        /// <summary>
        /// Closes the popup once, from any code path. Public so the main window can close it without
        /// ever hitting WPF's "while a Window is closing" InvalidOperationException.
        /// </summary>
        public void CloseSafely() => CloseSafely("closed");

        /// <summary>Same, but the reason ends up in the log so the next report can be traced.</summary>
        public void CloseSafely(string reason)
        {
            if (_closing) return;
            _closing = true;
            _focusTimer?.Stop();
            try
            {
                if (IsLoaded) Close();
                else Hide();
            }
            catch (Exception ex)
            {
                // "Cannot set Visibility ... while a Window is closing": WPF was already closing this
                // window (program shutdown, Alt+F4). Closing again is impossible, so the window is at
                // least pushed behind the table instead of floating on top of it forever.
                PvLog.Error("NotePopup.CloseSafely (" + reason + ")", ex);
                try
                {
                    IntPtr hwnd = new WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
                catch { }
            }

            PvLog.Throttled("PopupClose", $"[POPUP] editor closed ({reason})", 10);
        }

        private bool HasChanges()
        {
            if ((NameBox.Text ?? "").Trim() != _initialName) return true;
            if ((NoteBox.Text ?? "") != _initialNote) return true;
            return !SelectedTags().ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(InitialSelected());
        }

        /// <summary>Tag keys that were ticked when the popup opened (only tags in the list can be ticked).</summary>
        private IEnumerable<string> InitialSelected() =>
            _chips.Where(c => _initialTags.Any(t =>
                        string.Equals(t, c.Def.TagKey?.Trim(), StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t, c.Def.DisplayName?.Trim(), StringComparison.OrdinalIgnoreCase)))
                  .Select(c => c.Def.TagKey);

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (e.Key == Key.Escape) { CloseSafely(); e.Handled = true; }
            else if (ctrl && e.Key == Key.Enter) { Save(); e.Handled = true; }
        }

        private void Save_Click(object sender, RoutedEventArgs e) => Save();

        private void Save()
        {
            string name = (NameBox.Text ?? "").Trim();
            if (name.Length == 0)
            {
                HintText.Text = "Write a name first.";
                HintText.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                NameBox.Focus();
                return;
            }

            _onSave?.Invoke(name, SelectedTags(), NoteBox.Text ?? "");
            CloseSafely();
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            _openInMain?.Invoke();
            CloseSafely();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => CloseSafely();

        private static Color ParseHex(string? hex, Color fallback)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hex)) return fallback;
                string h = hex.Trim().TrimStart('#');
                if (h.Length == 3) h = new string(new[] { h[0], h[0], h[1], h[1], h[2], h[2] });
                if (h.Length == 6) h = "FF" + h;
                if (h.Length != 8) return fallback;
                return Color.FromArgb(Convert.ToByte(h.Substring(0, 2), 16), Convert.ToByte(h.Substring(2, 2), 16),
                                      Convert.ToByte(h.Substring(4, 2), 16), Convert.ToByte(h.Substring(6, 2), 16));
            }
            catch { return fallback; }
        }

        /// <summary>Keeps dark tag colours readable on the dark popup.</summary>
        private static Color Brighten(Color colour)
        {
            double luma = 0.299 * colour.R + 0.587 * colour.G + 0.114 * colour.B;
            if (luma >= 120) return colour;
            double lift = (120 - luma) / 120.0 * 0.7;
            byte Mix(byte channel) => (byte)Math.Clamp(channel + (255 - channel) * lift, 0, 255);
            return Color.FromRgb(Mix(colour.R), Mix(colour.G), Mix(colour.B));
        }
    }
}

