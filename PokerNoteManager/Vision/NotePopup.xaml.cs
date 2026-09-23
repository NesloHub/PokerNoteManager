using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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

        private readonly string _initialName;
        private readonly string _initialNote;
        private readonly int _physX, _physY;      // where the box was clicked, in physical screen pixels
        private bool _activated;
        private bool _closing;               // guards against Close() being re-entered while closing

        /// <param name="known">True when the player is already in the database.</param>
        /// <param name="playerName">Database key, or the reading from the screen for a new player.</param>
        /// <param name="note">Current note text.</param>
        /// <param name="allTags">Every tag defined in the program.</param>
        /// <param name="playerTags">Tags the player already has.</param>
        /// <param name="onSave">Called with (name, selected tag keys, note) when Save is pressed.</param>
        /// <param name="openInMain">Called when "Open in program" is pressed.</param>
        /// <param name="physX">X of the clicked box in physical screen pixels (for placement).</param>
        /// <param name="physY">Y of the clicked box in physical screen pixels (for placement).</param>
        public NotePopup(bool known, string playerName, string note, IReadOnlyList<TagDef> allTags,
                         IReadOnlyList<string> playerTags,
                         Action<string, List<string>, string> onSave, Action openInMain,
                         int physX = -1, int physY = -1)
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
            NameHint.Text = "The name comes from the screen - correct it if the reading is off. "
                          + "Saving creates the player.";
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
            // Place the popup next to the clicked box and keep it inside the screen that the box is on.
            // The conversion from physical pixels to WPF units uses *this* window's DPI, which is the
            // DPI of the monitor the popup ended up on - the two can differ with several monitors.
            try
            {
                double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                if (scale <= 0.1) scale = 1.0;

                double width = ActualWidth > 0 ? ActualWidth : Width;
                double height = ActualHeight > 0 ? ActualHeight : 260;
                double vsLeft = SystemParameters.VirtualScreenLeft;
                double vsTop = SystemParameters.VirtualScreenTop;
                double vsWidth = Math.Max(320, SystemParameters.VirtualScreenWidth);
                double vsHeight = Math.Max(240, SystemParameters.VirtualScreenHeight);

                if (_physX >= 0 && _physY >= 0)
                {
                    Left = _physX / scale + 14;
                    Top = _physY / scale - 10;
                }

                double minLeft = vsLeft + 4, minTop = vsTop + 4;
                Left = Math.Clamp(double.IsNaN(Left) ? minLeft : Left, minLeft, Math.Max(minLeft, vsLeft + vsWidth - width - 8));
                Top = Math.Clamp(double.IsNaN(Top) ? minTop : Top, minTop, Math.Max(minTop, vsTop + vsHeight - height - 8));
            }
            catch (Exception ex) { PvLog.Error("NotePopup.Position", ex); }

            Activate();
            if (NameBox.IsReadOnly) { NoteBox.Focus(); NoteBox.CaretIndex = NoteBox.Text.Length; }
            else { NameBox.Focus(); NameBox.SelectAll(); }
        }

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
            if (!HasChanges()) CloseSafely();
        }

        /// <summary>True when something was typed that has not been saved yet.</summary>
        public bool HasUnsavedChanges => !_closing && HasChanges();

        /// <summary>
        /// Saves right away, exactly like pressing the Save button. Used when the editor is replaced by
        /// another one (the same player sits at several tables, so a second box may be clicked).
        /// </summary>
        public void SaveNow() => Save();

        /// <summary>
        /// Closes the popup once, from any code path. Public so the main window can close it without
        /// ever hitting WPF's "while a Window is closing" InvalidOperationException.
        /// </summary>
        public void CloseSafely()
        {
            if (_closing) return;
            _closing = true;
            try
            {
                if (IsLoaded) Close();
                else Hide();
            }
            catch (Exception ex) { PvLog.Error("NotePopup.CloseSafely", ex); }
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

