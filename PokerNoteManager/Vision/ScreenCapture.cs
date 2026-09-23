using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using OpenCvSharp;

namespace PokerNoteManager.Vision
{
    /// <summary>A top level window that was recognised as a poker table.</summary>
    public sealed class TableWindow
    {
        public IntPtr Handle { get; init; }
        public string Title { get; init; } = "";
        /// <summary>Window rectangle in physical screen pixels.</summary>
        public Rect Bounds { get; init; }
    }

    /// <summary>One recognised player position on a table (drives the hover box).</summary>
    public sealed class SeatBox
    {
        public string TableTitle { get; init; } = "";
        /// <summary>Window handle of the table the seat was read from (0 for clipboard scans).</summary>
        public IntPtr TableHandle { get; set; }
        public string OcrText { get; init; } = "";
        public string PlayerKey { get; set; } = "";      // database key when the name was matched
        public float Confidence { get; init; }
        public double Distance { get; set; }             // 0 = exact, higher = fuzzier match
        public bool Matched => PlayerKey.Length > 0;
        /// <summary>Nameplate rectangle in physical screen pixels.</summary>
        public Rect ScreenRect { get; set; }
    }

    /// <summary>Everything one capture produced.</summary>
    public sealed class ScanOutcome
    {
        public List<SeatBox> Boxes { get; } = new();
        public List<string> Tables { get; } = new();
        public int TablesSeen { get; set; }
        public string Message { get; set; } = "";
        public int MatchedCount { get; set; }
        public int UnknownCount { get; set; }
    }

    public sealed class ScreenInfo
    {
        public int Index { get; init; }
        /// <summary>Bounds in physical screen pixels.</summary>
        public Rect Bounds { get; init; }
        public string Name => $"Display {Index + 1}  ({Bounds.Width}x{Bounds.Height})";
    }

    /// <summary>
    /// Screen and window capture. Everything is in physical pixels because the app is per monitor
    /// DPI aware (see app.manifest).
    /// </summary>
    public static class ScreenCapture
    {
        // ---------- win32 ----------
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc lpfnEnum, IntPtr data);
        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        private const uint GA_ROOT = 2;

        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFO bmi, uint usage);

        private const int SRCCOPY = 0x00CC0020;
        private const uint PW_RENDERFULLCONTENT = 0x00000002;

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }
        [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes;
            public ushort biBitCount; public uint biCompression; public uint biSizeImage;
            public int biXPelsPerMeter; public int biYPelsPerMeter; public uint biClrUsed; public uint biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; }

        // ---------- screens ----------

        public static List<ScreenInfo> GetScreens()
        {
            List<ScreenInfo> screens = new();
            int index = 0;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                MONITORINFO info = new() { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMonitor, ref info))
                {
                    RECT m = info.rcMonitor;
                    screens.Add(new ScreenInfo
                    {
                        Index = index++,
                        Bounds = new Rect(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top)
                    });
                }
                return true;
            }, IntPtr.Zero);
            return screens;
        }

        public static (int X, int Y) CursorPosition()
        {
            GetCursorPos(out POINT p);
            return (p.X, p.Y);
        }

        // ---------- capture ----------

        /// <summary>
        /// Captures a window. PrintWindow is tried first because it renders the window itself even
        /// when another window (e.g. this program) covers it; plain BitBlt of the desktop area would
        /// otherwise return whatever is on top - which is exactly what makes a scan read the wrong
        /// picture.
        /// </summary>
        public static Mat? CaptureWindow(TableWindow window)
        {
            Mat? viaPrint = CaptureAreaWith(window.Bounds, window.Handle, usePrintWindow: true);
            if (viaPrint != null && !IsMostlyBlank(viaPrint)) return viaPrint;
            viaPrint?.Dispose();

            Mat? viaScreen = CaptureAreaWith(window.Bounds, IntPtr.Zero, usePrintWindow: false);
            if (viaScreen != null && IsMostlyBlank(viaScreen))
            {
                viaScreen.Dispose();
                return null;      // nothing useful to read
            }
            return viaScreen;
        }

        /// <summary>True when the image is (almost) one flat colour - a failed capture.</summary>
        private static bool IsMostlyBlank(Mat bgr)
        {
            try
            {
                using Mat small = new();
                Cv2.Resize(bgr, small, new Size(Math.Min(160, bgr.Width), Math.Min(120, bgr.Height)));
                using Mat gray = new();
                Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.MeanStdDev(gray, out _, out Scalar std);
                return std.Val0 < 3.0;
            }
            catch (Exception ex)
            {
                PvLog.Error("IsMostlyBlank", ex);
                return false;
            }
        }

        private static Mat? CaptureAreaWith(Rect area, IntPtr hwnd, bool usePrintWindow)
        {
            if (area.Width < 8 || area.Height < 8) return null;

            IntPtr hdcScreen = GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero) return null;
            IntPtr hdcMem = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr hOld = IntPtr.Zero;
            try
            {
                hdcMem = CreateCompatibleDC(hdcScreen);
                hBitmap = CreateCompatibleBitmap(hdcScreen, area.Width, area.Height);
                hOld = SelectObject(hdcMem, hBitmap);

                bool ok = usePrintWindow && hwnd != IntPtr.Zero
                    ? PrintWindow(hwnd, hdcMem, PW_RENDERFULLCONTENT)
                    : BitBlt(hdcMem, 0, 0, area.Width, area.Height, hdcScreen, area.X, area.Y, SRCCOPY);
                if (!ok) return null;

                BITMAPINFO bmi = new();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                bmi.bmiHeader.biWidth = area.Width;
                bmi.bmiHeader.biHeight = -area.Height;   // top down
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = 0;

                Mat bgra = new(area.Height, area.Width, MatType.CV_8UC4);
                byte[] buffer = new byte[area.Width * area.Height * 4];
                if (GetDIBits(hdcMem, hBitmap, 0, (uint)area.Height, buffer, ref bmi, 0) == 0)
                {
                    bgra.Dispose();
                    return null;
                }
                Marshal.Copy(buffer, 0, bgra.Data, buffer.Length);

                Mat bgr = new();
                Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
                bgra.Dispose();
                return bgr;
            }
            catch (Exception ex)
            {
                PvLog.Error("ScreenCapture.CaptureAreaWith", ex);
                return null;
            }
            finally
            {
                if (hOld != IntPtr.Zero) SelectObject(hdcMem, hOld);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                if (hdcMem != IntPtr.Zero) DeleteDC(hdcMem);
                ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }

        /// <summary>Copies a pixel area of the desktop into a BGR Mat (3 channels).</summary>
        public static Mat? CaptureArea(Rect area) => CaptureAreaWith(area, IntPtr.Zero, usePrintWindow: false);

        /// <summary>
        /// All visible top level windows that could be a poker table: normal size, not ours, not the
        /// shell. The caller confirms with the felt detection, so this stays site independent.
        /// </summary>
        /// <summary>
        /// Shell and utility windows (IME/emoji panel, taskbar flyouts, the desktop) are never poker
        /// tables. Without this list a full screen shell window can be mistaken for a table and paint
        /// boxes all over the desktop.
        /// </summary>
        private static readonly string[] NotATableExact =
        {
            "Program Manager", "Search", "Start", "Settings", "Task Manager", "Poker Notes",
            "Cortana", "Action center", "Notification Center"
        };

        private static readonly string[] NotATableContains =
        {
            "Windows Input Experience", "Microsoft Text Input Application",
            "Windows Shell Experience Host", "MSCTFIME UI", "Default IME", "CiceroUIWndFrame",
            "PopupHost", "NVIDIA Share", "Snipping Tool", "PokerVisionHUD"
        };

        /// <summary>True for shell/utility windows that must never be scanned as a table.</summary>
        public static bool IsNotATableWindow(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return true;
            string t = title.Trim();
            foreach (string exact in NotATableExact)
                if (string.Equals(t, exact, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string part in NotATableContains)
                if (t.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// The top level window under a physical screen point, or null when there is none, when it is
        /// one of our own windows, or when it is a shell window. Used by "Snip Player" so the reading
        /// comes from the table itself and never from our own hover boxes.
        /// </summary>
        public static TableWindow? WindowUnderPoint(int x, int y)
        {
            try
            {
                IntPtr hWnd = WindowFromPoint(new POINT { X = x, Y = y });
                if (hWnd == IntPtr.Zero) return null;

                IntPtr root = GetAncestor(hWnd, GA_ROOT);
                if (root != IntPtr.Zero) hWnd = root;

                if (hWnd == GetDesktopWindow() || hWnd == GetShellWindow()) return null;
                if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return null;

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == (uint)Environment.ProcessId) return null;

                StringBuilder title = new(300);
                GetWindowTextW(hWnd, title, title.Capacity);
                if (IsNotATableWindow(title.ToString())) return null;

                if (!GetWindowRect(hWnd, out RECT r)) return null;
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w < 160 || h < 100) return null;

                return new TableWindow { Handle = hWnd, Title = title.ToString(), Bounds = new Rect(r.Left, r.Top, w, h) };
            }
            catch (Exception ex)
            {
                PvLog.Error("WindowUnderPoint", ex);
                return null;
            }
        }

        public static List<TableWindow> FindCandidateWindows(Rect? limitToScreen = null)
        {
            List<TableWindow> list = new();
            IntPtr shell = GetShellWindow();
            IntPtr desktop = GetDesktopWindow();
            uint myPid = (uint)Environment.ProcessId;

            EnumWindows((hWnd, _) =>
            {
                try
                {
                    if (hWnd == shell || hWnd == desktop) return true;
                    if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;

                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == myPid) return true;

                    StringBuilder title = new(300);
                    if (GetWindowTextW(hWnd, title, title.Capacity) <= 0) return true;
                    if (IsNotATableWindow(title.ToString())) return true;

                    if (!GetWindowRect(hWnd, out RECT r)) return true;
                    int w = r.Right - r.Left, h = r.Bottom - r.Top;
                    if (w < 320 || h < 240) return true;
                    if (w > 5000 || h > 3000) return true;
                    if (r.Top <= -3000 || r.Left <= -3000) return true;

                    Rect bounds = new(r.Left, r.Top, w, h);
                    if (limitToScreen.HasValue)
                    {
                        Rect s = limitToScreen.Value;
                        int overlapW = Math.Min(bounds.Right, s.Right) - Math.Max(bounds.X, s.X);
                        int overlapH = Math.Min(bounds.Bottom, s.Bottom) - Math.Max(bounds.Y, s.Y);
                        if (overlapW < bounds.Width * 0.6 || overlapH < bounds.Height * 0.6) return true;
                    }

                    list.Add(new TableWindow { Handle = hWnd, Title = title.ToString(), Bounds = bounds });
                }
                catch (Exception ex) { PvLog.Throttled("FindCandidateWindows", $"!! window scan: {ex.Message}", 120); }
                return true;
            }, IntPtr.Zero);

            return list;
        }
    }
}
