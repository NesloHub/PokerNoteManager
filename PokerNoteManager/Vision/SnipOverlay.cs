using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PokerNoteManager.Vision
{
    /// <summary>
    /// Full screen "draw a box" layer for Snip Player: the user drags a rectangle over the player's name and
    /// the marked rectangle (in physical screen pixels) is handed over. Esc, a right click or a click without
    /// dragging cancels (a plain click reports a 1x1 rectangle, which the caller reads as "the spot under the
    /// cursor").
    ///
    /// Unlike the hover overlay this window *does* take the mouse and the keyboard: the user started it on
    /// purpose, and while a name is marked the clicks must not reach the poker client.
    /// </summary>
    public sealed class SnipOverlay : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const uint SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new(-1);

        [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLongW(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

        private readonly ScreenInfo _screen;
        private readonly Action<Rect?> _done;
        private readonly Canvas _canvas = new();
        private readonly Rectangle _band;
        private readonly TextBlock _hint;
        private Point _start;
        private bool _dragging;
        private bool _finished;
        private double _scale = 1.0;

        /// <param name="screen">The screen this layer covers.</param>
        /// <param name="done">Receives the marked rectangle in physical screen pixels, or null when cancelled.</param>
        public SnipOverlay(ScreenInfo screen, Action<Rect?> done)
        {
            _screen = screen;
            _done = done;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(0x4A, 0x02, 0x06, 0x17));
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Title = "Poker Notes - mark the player name";
            Cursor = Cursors.Cross;

            _band = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(0x33, 0x22, 0xC5, 0x5E)),
                Visibility = Visibility.Collapsed
            };
            _hint = new TextBlock
            {
                Text = "Drag a box over the player's name   ·   Esc cancels",
                Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)),
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0F, 0x17, 0x2A)),
                Padding = new Thickness(12, 7, 12, 7),
                FontSize = 13
            };

            _canvas.Children.Add(_band);
            _canvas.Children.Add(_hint);
            Content = _canvas;

            Loaded += OnLoaded;
            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            MouseRightButtonDown += (_, _) => Finish(null);
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                SetWindowLongW(hwnd, GWL_EXSTYLE, GetWindowLongW(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
                SetWindowPos(hwnd, HWND_TOPMOST, _screen.Bounds.X, _screen.Bounds.Y,
                             _screen.Bounds.Width, _screen.Bounds.Height, SWP_NOACTIVATE);

                _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                if (_scale <= 0.1) _scale = 1.0;

                _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(_hint, Math.Max(10, (_screen.Bounds.Width / _scale - _hint.DesiredSize.Width) / 2));
                Canvas.SetTop(_hint, 40);

                Activate();          // Esc has to reach this window
            }
            catch (Exception ex) { PvLog.Error("SnipOverlay.OnLoaded", ex); }
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _start = e.GetPosition(this);
            _dragging = true;
            _band.Visibility = Visibility.Visible;
            UpdateBand(_start);
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            UpdateBand(e.GetPosition(this));
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) { Finish(null); return; }
            _dragging = false;
            UpdateBand(e.GetPosition(this));

            Rect band = BandRect();
            if (band.Width < 4 || band.Height < 4)
            {
                // A click without dragging: hand over the point, the caller reads the name plate there.
                Finish(new Rect(ToPhysical(band.X), ToPhysical(band.Y), 1, 1));
                return;
            }

            Finish(new Rect(ToPhysical(band.X), ToPhysical(band.Y),
                            Math.Max(1, band.Width * _scale), Math.Max(1, band.Height * _scale)));
        }

        private void UpdateBand(Point current)
        {
            Canvas.SetLeft(_band, Math.Min(_start.X, current.X));
            Canvas.SetTop(_band, Math.Min(_start.Y, current.Y));
            _band.Width = Math.Abs(current.X - _start.X);
            _band.Height = Math.Abs(current.Y - _start.Y);
        }

        private Rect BandRect() => new(Canvas.GetLeft(_band), Canvas.GetTop(_band), _band.Width, _band.Height);

        /// <summary>Window units (device independent) to physical screen pixels.</summary>
        private double ToPhysical(double value) => value * _scale + _screen.Bounds.X;

        private void Finish(Rect? area)
        {
            if (_finished) return;
            _finished = true;
            try { _done?.Invoke(area); } catch (Exception ex) { PvLog.Error("SnipOverlay.Finish", ex); }
            CloseSafely();
        }

        /// <summary>
        /// Closes the layer once; used by the main window when another screen is finished or when the user
        /// cancels. A layer closed this way reports nothing.
        /// </summary>
        public void CloseSafely()
        {
            _finished = true;
            _dragging = false;
            try
            {
                if (IsLoaded) Close();
                else Hide();
            }
            catch (Exception ex) { PvLog.Error("SnipOverlay.CloseSafely", ex); }
        }
    }
}
