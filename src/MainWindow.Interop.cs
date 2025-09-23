using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using WF = System.Windows.Forms;
using SD = System.Drawing;

namespace Traycer
{
    public partial class MainWindow
    {
        // ===== Interop =====
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public int lParam;
        }

        [DllImport("shell32.dll")]
        private static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOPMOST = new(-1);

        private const uint ABM_GETTASKBARPOS = 0x00000005;
        private const int ABE_LEFT = 0;
        private const int ABE_TOP = 1;
        private const int ABE_RIGHT = 2;
        private const int ABE_BOTTOM = 3;

        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;

        private const int HOTKEY_ID = 0xBEEF;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_WIN = 0x0008;
        private const uint VK_H = 0x48;

        private (int edge, SD.Rectangle rect) GetTaskbarRect()
        {
            var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
            SHAppBarMessage(ABM_GETTASKBARPOS, ref abd);
            var r = abd.rc;
            var rect = SD.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            int edge = (int)abd.uEdge;

            if (rect.Width == 0 || rect.Height == 0)
            {
                var full = WF.Screen.PrimaryScreen!.Bounds;
                int thickness = Math.Max(40, full.Height - WF.Screen.PrimaryScreen!.WorkingArea.Height);
                rect = new SD.Rectangle(full.Left, full.Bottom - thickness, full.Width, thickness);
                edge = ABE_BOTTOM;
            }

            return (edge, rect);
        }

        private void ReassertTopmost()
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        }

        private void ToggleClickThrough()
        {
            _clickThrough = !_clickThrough;
            int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            ex |= WS_EX_TOOLWINDOW | WS_EX_LAYERED;
            if (_clickThrough)
            {
                ex |= WS_EX_TRANSPARENT;
            }
            else
            {
                ex &= ~WS_EX_TRANSPARENT;
            }
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex);

            _tray.BalloonTipTitle = "Traycer HUD";
            _tray.BalloonTipText = _clickThrough ? "Click-through: ON" : "Click-through: OFF (click wells to run actions)";
            _tray.ShowBalloonTip(1200);

            ReassertTopmost();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                ToggleClickThrough();
                handled = true;
            }

            return IntPtr.Zero;
        }
    }
}
