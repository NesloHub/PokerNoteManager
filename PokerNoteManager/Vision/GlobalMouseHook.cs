using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PokerNoteManager.Vision
{
    /// <summary>
    /// Low level mouse hook. Used for three things and nothing else:
    ///   * middle mouse button  -> capture and scan (same shortcut as the old tracker)
    ///   * mouse move           -> hover: show the note card for the box under the cursor
    ///   * left click           -> notes (plain) and removing that single box (Ctrl + left click)
    /// Right clicks are only observed, never used: on Unibet a right click folds the hand.
    /// It never swallows an event, so the poker client keeps working exactly as before.
    /// </summary>
    public sealed class GlobalMouseHook : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MBUTTONUP = 0x0208;
        private const int WM_RBUTTONDOWN = 0x0204;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string? name);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private IntPtr _hook = IntPtr.Zero;
        private readonly HookProc _proc;          // keep a reference so it is not collected
        private long _lastMoveTicks;
        private long _lastDragTicks;

        // Middle button press-and-drag is how a hover box is moved on the table.
        private bool _middleDown;
        private bool _middleMoved;
        private bool _middleCtrl;
        private int _middleStartX, _middleStartY;

        /// <summary>Physical screen pixels.</summary>
        public event Action<int, int>? MouseMoved;
        /// <summary>Middle mouse button pressed and released without moving: scan, or (with ctrl) box a
        /// player by hand. The ctrl flag is read when the button goes down.</summary>
        public event Action<int, int, bool>? MiddleClicked;
        /// <summary>Middle button held down and moved: start/move/end of a box drag.</summary>
        public event Action<int, int>? DragStarted;
        public event Action<int, int>? Dragging;
        public event Action<int, int>? DragEnded;
        public event Action<int, int, bool>? LeftClicked;   // bool = ctrl held
        public event Action<int, int>? RightClicked;        // removes the hover box under the cursor

        public bool IsRunning => _hook != IntPtr.Zero;

        public GlobalMouseHook() => _proc = Callback;

        public void Start()
        {
            if (_hook != IntPtr.Zero) return;
            try
            {
                _hook = SetWindowsHookExW(WH_MOUSE_LL, _proc, GetModuleHandleW(null), 0);
                if (_hook == IntPtr.Zero) PvLog.Write("!! mouse hook could not be installed");
            }
            catch (Exception ex) { PvLog.Error("GlobalMouseHook.Start", ex); }
        }

        public void Stop()
        {
            if (_hook == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(_hook); } catch (Exception ex) { PvLog.Error("GlobalMouseHook.Stop", ex); }
            _hook = IntPtr.Zero;
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int message = (int)wParam;
                    MSLLHOOKSTRUCT data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

                    if (message == WM_MOUSEMOVE)
                    {
                        // throttle: max ~40 checks per second is plenty for a hover box
                        long now = Environment.TickCount64;
                        if (now - _lastMoveTicks >= 25)
                        {
                            _lastMoveTicks = now;
                            MouseMoved?.Invoke(data.pt.X, data.pt.Y);
                        }

                        if (_middleDown)
                        {
                            int dx = data.pt.X - _middleStartX, dy = data.pt.Y - _middleStartY;
                            if (!_middleMoved && dx * dx + dy * dy > 36)      // 6 px: a real drag, not a click
                            {
                                _middleMoved = true;
                                DragStarted?.Invoke(data.pt.X, data.pt.Y);
                                _lastDragTicks = now;
                            }
                            else if (_middleMoved && now - _lastDragTicks >= 20)
                            {
                                _lastDragTicks = now;
                                Dragging?.Invoke(data.pt.X, data.pt.Y);
                            }
                        }
                    }
                    else if (message == WM_MBUTTONDOWN)
                    {
                        _middleDown = true;
                        _middleMoved = false;
                        _middleCtrl = CtrlDown();
                        _middleStartX = data.pt.X;
                        _middleStartY = data.pt.Y;
                    }
                    else if (message == WM_MBUTTONUP)
                    {
                        if (!_middleDown) return CallNextHookEx(_hook, nCode, wParam, lParam);
                        _middleDown = false;
                        if (_middleMoved) DragEnded?.Invoke(data.pt.X, data.pt.Y);
                        else MiddleClicked?.Invoke(data.pt.X, data.pt.Y, _middleCtrl);
                    }
                    else if (message == WM_LBUTTONDOWN)
                    {
                        LeftClicked?.Invoke(data.pt.X, data.pt.Y, CtrlDown());
                    }
                    else if (message == WM_RBUTTONDOWN)
                    {
                        // Reported for completeness, but the UI deliberately ignores it: on Unibet a
                        // right click folds the hand. Boxes are removed with Ctrl + left click.
                        RightClicked?.Invoke(data.pt.X, data.pt.Y);
                    }
                }
            }
            catch (Exception ex) { PvLog.Throttled("MouseHook", $"!! mouse hook: {ex.Message}", 120); }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

        /// <summary>True while ctrl is held (the hook runs before the click reaches any window).</summary>
        private static bool CtrlDown() => (GetAsyncKeyState(0x11) & 0x8000) != 0;

        public void Dispose() => Stop();
    }
}
