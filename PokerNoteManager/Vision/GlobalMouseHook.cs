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

        /// <summary>Physical screen pixels.</summary>
        public event Action<int, int>? MouseMoved;
        public event Action<int, int>? MiddleClicked;
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
                    }
                    else if (message == WM_MBUTTONDOWN)
                    {
                        MiddleClicked?.Invoke(data.pt.X, data.pt.Y);
                    }
                    else if (message == WM_LBUTTONDOWN)
                    {
                        bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
                        LeftClicked?.Invoke(data.pt.X, data.pt.Y, ctrl);
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

        public void Dispose() => Stop();
    }
}
