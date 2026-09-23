using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PokerNoteManager.Vision
{
    /// <summary>
    /// Transparent, click through overlay that draws the hover boxes over a table and shows the
    /// player's notes on hover.
    ///
    /// It is built so it can never disturb the game or the notes editor:
    ///   * WS_EX_TRANSPARENT  -> every mouse click goes to the poker client below it
    ///   * WS_EX_NOACTIVATE   -> it can never take focus (the reason the old HUD stole the caret)
    ///   * WS_EX_TOOLWINDOW   -> it stays out of Alt+Tab
    ///   * no timers: boxes change only when a scan is run
    /// Hover and clicks are detected by the global mouse hook instead of by the window itself.
    /// </summary>
    public sealed class HoverOverlay : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private const uint SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new(-1);

        [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLongW(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

        private readonly ScreenInfo _screen;
        private readonly Canvas _canvas = new();
        private readonly Border _card;
        private readonly TextBlock _cardTitle;
        private readonly TextBlock _cardBody;
        private readonly Func<SeatBox, HoverInfo> _info;
        private readonly List<SeatBox> _boxes = new();
        private double _scale = 1.0;
        private SeatBox? _hovered;

        public HoverOverlay(ScreenInfo screen, Func<SeatBox, HoverInfo> info)
        {
            _screen = screen;
            _info = info;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Title = "Poker Notes - hover boxes";

            _cardTitle = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC))
            };
            _cardBody = new TextBlock
            {
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 430,
                Margin = new Thickness(0, 4, 0, 0)
            };
            _card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x0F, 0x17, 0x2A)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Child = new StackPanel { Children = { _cardTitle, _cardBody } },
                Visibility = Visibility.Collapsed
            };

            _canvas.Children.Add(_card);
            Content = _canvas;

            SourceInitialized += (s, e) =>
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int ex = GetWindowLongW(hwnd, GWL_EXSTYLE);
                SetWindowLongW(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

                SetWindowPos(hwnd, HWND_TOPMOST, _screen.Bounds.X, _screen.Bounds.Y,
                    _screen.Bounds.Width, _screen.Bounds.Height, SWP_NOACTIVATE);

                _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                if (_scale <= 0.1) _scale = 1.0;
            };
        }

        /// <summary>DPI scale of the display the overlay lives on (diagnostics only).</summary>
        public double DpiScale => _scale <= 0.1 ? 1.0 : _scale;

        /// <summary>Rebuilds the boxes for one scan (physical screen pixels in, DIP out).</summary>
        public void SetBoxes(IReadOnlyList<SeatBox> boxes)
        {
            _boxes.Clear();
            _boxes.AddRange(boxes);

            _canvas.Children.Clear();
            foreach (SeatBox box in _boxes)
            {
                HoverInfo info = _info(box);
                Color colour = info.Known ? info.MainColour : Color.FromRgb(0x94, 0xA3, 0xB8);

                Rectangle rect = new()
                {
                    Width = Math.Max(6, box.ScreenRect.Width / _scale),
                    Height = Math.Max(6, box.ScreenRect.Height / _scale),
                    Stroke = new SolidColorBrush(colour),
                    StrokeThickness = info.Known ? 2.5 : 1.5,
                    RadiusX = 6,
                    RadiusY = 6,
                    Fill = new SolidColorBrush(Color.FromArgb((byte)(info.Known ? 0x1C : 0x10), colour.R, colour.G, colour.B)),
                    StrokeDashArray = info.Known ? null : new DoubleCollection { 3, 2 }
                };
                Canvas.SetLeft(rect, (box.ScreenRect.X - _screen.Bounds.X) / _scale);
                Canvas.SetTop(rect, (box.ScreenRect.Y - _screen.Bounds.Y) / _scale);
                _canvas.Children.Add(rect);

                // Name plus the player's tags, each label in its own colour so the table is readable at
                // a glance. Very dark tag colours are brightened so they stay visible on the overlay.
                TextBlock label = new()
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x0B, 0x10, 0x20)),
                    Padding = new Thickness(4, 1, 4, 1),
                    MaxWidth = 340
                };
                label.Inlines.Add(new Run((info.Known ? "" : "? ") + info.Label)
                {
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Brighten(colour))
                });
                foreach ((string text, Color chipColour) in info.Chips)
                {
                    label.Inlines.Add(new Run("  " + text)
                    {
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Brighten(chipColour))
                    });
                }
                Canvas.SetLeft(label, (box.ScreenRect.X - _screen.Bounds.X) / _scale);
                Canvas.SetTop(label, Math.Max(0, (box.ScreenRect.Y - _screen.Bounds.Y) / _scale - 17));
                _canvas.Children.Add(label);
            }

            _canvas.Children.Add(_card);          // the note card is always on top
            _card.Visibility = Visibility.Collapsed;
            _hovered = null;
        }

        public void ClearBoxes()
        {
            _boxes.Clear();
            _hovered = null;
            _canvas.Children.Clear();
            _canvas.Children.Add(_card);
            _card.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Puts the overlay back on top of the table windows. SWP_NOACTIVATE keeps the focus where it
        /// was, so this can never interrupt typing or the game.
        /// </summary>
        public void BringToFront()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                SetWindowPos(hwnd, HWND_TOPMOST, _screen.Bounds.X, _screen.Bounds.Y,
                    _screen.Bounds.Width, _screen.Bounds.Height, SWP_NOACTIVATE);
            }
            catch (Exception ex) { PvLog.Error("HoverOverlay.BringToFront", ex); }
        }

        /// <summary>Seat box under a physical screen point, if any.</summary>
        public SeatBox? HitTest(int physX, int physY)
        {
            foreach (SeatBox box in _boxes)
            {
                int pad = 4;
                if (physX >= box.ScreenRect.X - pad && physX <= box.ScreenRect.Right + pad &&
                    physY >= box.ScreenRect.Y - pad && physY <= box.ScreenRect.Bottom + pad) return box;
            }
            return null;
        }

        /// <summary>Shows or moves the note card for the box under the cursor.</summary>
        public void HoverAt(int physX, int physY)
        {
            SeatBox? hit = HitTest(physX, physY);
            if (!ReferenceEquals(hit, _hovered))
            {
                _hovered = hit;
                if (hit == null)
                {
                    _card.Visibility = Visibility.Collapsed;
                    return;
                }

                HoverInfo info = _info(hit);
                _cardTitle.Text = info.Title;
                _cardBody.Text = info.Body;
                _cardBody.Visibility = string.IsNullOrEmpty(info.Body) ? Visibility.Collapsed : Visibility.Visible;
                _card.Visibility = Visibility.Visible;
            }

            if (_hovered == null) return;
            MoveCard(physX, physY);
        }

        private void MoveCard(int physX, int physY)
        {
            double localX = (physX - _screen.Bounds.X) / _scale;
            double localY = (physY - _screen.Bounds.Y) / _scale;

            _card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double cardW = _card.DesiredSize.Width;
            double cardH = _card.DesiredSize.Height;
            double screenW = _screen.Bounds.Width / _scale;
            double screenH = _screen.Bounds.Height / _scale;

            double x = Math.Min(localX + 18, Math.Max(0, screenW - cardW - 8));
            double y = Math.Min(localY + 20, Math.Max(0, screenH - cardH - 8));
            Canvas.SetLeft(_card, x);
            Canvas.SetTop(_card, y);
        }

        /// <summary>
        /// Lifts a tag colour that is too dark to read on the dark overlay (navy, dark red, ...).
        /// Bright colours are returned unchanged.
        /// </summary>
        private static Color Brighten(Color colour)
        {
            double luma = 0.299 * colour.R + 0.587 * colour.G + 0.114 * colour.B;
            if (luma >= 110) return colour;
            double lift = (110 - luma) / 110.0 * 0.75;
            byte Mix(byte channel) => (byte)Math.Clamp(channel + (255 - channel) * lift, 0, 255);
            return Color.FromRgb(Mix(colour.R), Mix(colour.G), Mix(colour.B));
        }
    }

    /// <summary>What the overlay shows for one box (built by the main window from the database).</summary>
    public sealed class HoverInfo
    {
        public bool Known { get; init; }
        public string Label { get; init; } = "";     // player name (or the raw reading)
        public string Title { get; init; } = "";     // hover card heading
        public string Body { get; init; } = "";      // hover card text (tags + notes)

        /// <summary>Colour of the box itself: the first tag's colour for tagged players.</summary>
        public Color MainColour { get; init; } = Color.FromRgb(0x22, 0xC5, 0x5E);

        /// <summary>Tag labels drawn next to the name, each in its own colour.</summary>
        public IReadOnlyList<(string Text, Color Colour)> Chips { get; init; } = Array.Empty<(string, Color)>();
    }
}
