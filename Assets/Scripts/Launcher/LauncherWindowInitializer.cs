using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace LatticeVeil.Launcher
{
    /// <summary>
    /// Ensures the standalone LatticeVeil launcher runs as a dedicated borderless desktop window at 1440x900.
    /// Handles native window styling, dark background brush, centering, minimizing, and dragging without white startup flash.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]
    public class LauncherWindowInitializer : MonoBehaviour
    {
        public const int DefaultLauncherWidth = 1440;
        public const int DefaultLauncherHeight = 900;

        private static bool _initialized;
        private static bool _shown;
        private static IntPtr _windowHandle = IntPtr.Zero;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        private const int GWL_STYLE = -16;
        private const long WS_BORDER = 0x00800000L;
        private const long WS_DLGFRAME = 0x00400000L;
        private const long WS_CAPTION = 0x00C00000L;
        private const long WS_THICKFRAME = 0x00040000L;
        private const long WS_MINIMIZEBOX = 0x00020000L;
        private const long WS_MAXIMIZEBOX = 0x00010000L;
        private const long WS_SYSMENU = 0x00080000L;
        private const long WS_POPUP = 0x80000000L;

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_HIDEWINDOW = 0x0080;

        public const int SW_HIDE = 0;
        public const int SW_SHOW = 5;
        public const int SW_MINIMIZE = 6;
        public const int SW_SHOWMINIMIZED = 2;
        public const int SW_RESTORE = 9;
        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HTCAPTION = 0x2;

        private const int GCLP_HBRBACKGROUND = -10;
        private const int BLACK_BRUSH = 4;

        [DllImport("gdi32.dll")]
        private static extern IntPtr GetStockObject(int fnObject);

        [DllImport("user32.dll", EntryPoint = "SetClassLongPtr", SetLastError = true)]
        private static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetClassLong", SetLastError = true)]
        private static extern int SetClassLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetActiveWindow")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool UpdateWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetWindowText(IntPtr hWnd, string lpString);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        // Extended style used to keep the hidden launcher out of the taskbar.
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x00000080L;
        private const long WS_EX_TOPMOST = 0x00000008L;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static bool _hiddenForGame;

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
                return SetClassLongPtr64(hWnd, nIndex, dwNewLong);
            return new IntPtr(SetClassLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }

        private static IntPtr GetWindowLong(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8)
                return GetWindowLongPtr64(hWnd, nIndex);
            return GetWindowLong32(hWnd, nIndex);
        }

        private static IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
                return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
            return new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void OnBeforeSplashScreen()
        {
            ApplyLauncherWindowSettings();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void OnBeforeSceneLoad()
        {
            ApplyLauncherWindowSettings();
        }

        private void Awake()
        {
            ApplyLauncherWindowSettings();
        }

        private void Start()
        {
            ShowLauncherWindow();
        }

        public static IntPtr GetWindowHandle()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (_windowHandle != IntPtr.Zero && IsWindow(_windowHandle))
            {
                return _windowHandle;
            }

            _windowHandle = GetActiveWindow();
            if (_windowHandle != IntPtr.Zero) return _windowHandle;

            _windowHandle = GetForegroundWindow();
            if (_windowHandle != IntPtr.Zero) return _windowHandle;

            uint currentPid = GetCurrentProcessId();
            IntPtr foundHwnd = IntPtr.Zero;
            EnumWindows((hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == currentPid)
                {
                    if (GetParent(hWnd) == IntPtr.Zero)
                    {
                        foundHwnd = hWnd;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);

            _windowHandle = foundHwnd;
            return _windowHandle;
#else
            return IntPtr.Zero;
#endif
        }

        public static void ApplyLauncherWindowSettings()
        {
            if (_initialized) return;

#if !UNITY_EDITOR
            Screen.fullScreen = false;
            Screen.fullScreenMode = FullScreenMode.Windowed;
            Screen.SetResolution(DefaultLauncherWidth, DefaultLauncherHeight, FullScreenMode.Windowed);

#if UNITY_STANDALONE_WIN
            IntPtr hWnd = GetWindowHandle();
            if (hWnd != IntPtr.Zero)
            {
                // Ensure native window background brush is pure black instead of default white
                try
                {
                    IntPtr blackBrush = GetStockObject(BLACK_BRUSH);
                    SetClassLongPtr(hWnd, GCLP_HBRBACKGROUND, blackBrush);
                }
                catch { }

                // Strip native caption / sizing frames and apply borderless popup
                long style = GetWindowLong(hWnd, GWL_STYLE).ToInt64();
                if ((style & (WS_CAPTION | WS_THICKFRAME)) != 0)
                {
                    style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU | WS_BORDER | WS_DLGFRAME);
                    style |= WS_POPUP;
                    SetWindowLong(hWnd, GWL_STYLE, new IntPtr(style));

                    int screenW = GetSystemMetrics(SM_CXSCREEN);
                    int screenH = GetSystemMetrics(SM_CYSCREEN);
                    int x = Mathf.Max(0, (screenW - DefaultLauncherWidth) / 2);
                    int y = Mathf.Max(0, (screenH - DefaultLauncherHeight) / 2);

                    // Position and size without prematurely showing the unpainted frame
                    SetWindowPos(hWnd, IntPtr.Zero, x, y, DefaultLauncherWidth, DefaultLauncherHeight,
                        SWP_FRAMECHANGED | SWP_NOZORDER | SWP_HIDEWINDOW);
                }
            }
#endif
#endif
            _initialized = true;
        }

        public static void ShowLauncherWindow()
        {
            if (_shown) return;
            _shown = true;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            IntPtr hWnd = GetWindowHandle();
            if (hWnd != IntPtr.Zero)
            {
                // Set window title so Task Manager shows ONE collection named
                // "LatticeVeil" — this launcher window is the group head and the
                // game joins it as a sub-process via GameProcessJob.
                SetWindowText(hWnd, "LatticeVeil");
                ShowWindow(hWnd, SW_SHOW);
                SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW);
                UpdateWindow(hWnd);
            }
#endif
        }

        public static void MinimizeLauncherWindow()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            IntPtr hWnd = GetWindowHandle();
            if (hWnd != IntPtr.Zero)
            {
                ShowWindow(hWnd, SW_MINIMIZE);
            }
#endif
        }

        /// <summary>Hides the launcher window (used while the game is running).</summary>
        public static void HideLauncherWindow()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            IntPtr hWnd = GetWindowHandle();
            if (hWnd != IntPtr.Zero)
            {
                ShowWindow(hWnd, SW_HIDE);
            }
#endif
        }

        /// <summary>
        /// Renames any top-level window (used to retitle the game's main window
        /// to "LatticeVeilMonogame" / "LatticeVeil" so its Task Manager entry
        /// reads correctly inside the single LatticeVeil collection).
        /// </summary>
        public static bool SetExternalWindowTitle(IntPtr hWnd, string title)
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (hWnd == IntPtr.Zero || string.IsNullOrEmpty(title) || !IsWindow(hWnd))
                return false;
            return SetWindowText(hWnd, title);
#else
            return false;
#endif
        }

        /// <summary>
        /// Configures this player instance as a floating panel window: real
        /// borderless always-on-top OS window (Version Manager / Skin library)
        /// that floats over the launcher and every other application.
        /// </summary>
        public static void ConfigurePanelWindow(string title)
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            IntPtr hWnd = GetWindowHandle();
            if (hWnd == IntPtr.Zero)
                return;

            // Borderless popup (strip caption/frames), like the launcher itself.
            long style = GetWindowLong(hWnd, GWL_STYLE).ToInt64();
            style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU | WS_BORDER | WS_DLGFRAME);
            style |= WS_POPUP;
            SetWindowLong(hWnd, GWL_STYLE, new IntPtr(style));

            // Always on top, and out of the taskbar/alt-tab (panels belong to
            // the launcher, they are not standalone apps).
            long exStyle = GetWindowLong(hWnd, GWL_EXSTYLE).ToInt64();
            SetWindowLong(hWnd, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_TOPMOST | WS_EX_TOOLWINDOW));

            SetWindowText(hWnd, title);

            // Place top-right of the work area as the default spot; draggable
            // anywhere by the user via the header strip.
            SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
            UpdateWindow(hWnd);
#else
            // Editor: nothing to configure.
#endif
        }

        /// <summary>
        /// Fully hides the launcher while the game runs: the window disappears
        /// from the screen, the taskbar AND alt-tab (WS_EX_TOOLWINDOW keeps it
        /// out of the taskbar even if something forces it visible again).
        /// </summary>
        public static void HideLauncherWindowCompletely()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            IntPtr hWnd = GetWindowHandle();
            if (hWnd == IntPtr.Zero)
                return;

            // Add WS_EX_TOOLWINDOW (0x80) so the window can never reappear in
            // the taskbar; hide after setting the style.
            long exStyle = GetWindowLong(hWnd, GWL_EXSTYLE).ToInt64();
            SetWindowLong(hWnd, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_TOOLWINDOW));

            ShowWindow(hWnd, SW_HIDE);
            _hiddenForGame = true;
#endif
        }

        /// <summary>Brings the launcher back after the game closes (removes the taskbar-hiding style).</summary>
        public static void RestoreLauncherAfterGame()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (!_hiddenForGame)
                return;
            _hiddenForGame = false;

            IntPtr hWnd = GetWindowHandle();
            if (hWnd == IntPtr.Zero)
                return;

            long exStyle = GetWindowLong(hWnd, GWL_EXSTYLE).ToInt64();
            SetWindowLong(hWnd, GWL_EXSTYLE, new IntPtr(exStyle & ~WS_EX_TOOLWINDOW));

            ShowWindow(hWnd, SW_SHOW);
            SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW);
            UpdateWindow(hWnd);
            SetForegroundWindow(hWnd);
#endif
        }

        /// <summary>Brings the launcher window back after the game closes.</summary>

        /// <summary>Brings the launcher window back after the game closes.</summary>
        public static void RestoreLauncherWindow()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            IntPtr hWnd = GetWindowHandle();
            if (hWnd != IntPtr.Zero)
            {
                // SW_RESTORE un-minimizes (and shows) the window — the launcher
                // now stays minimized rather than hidden while the game runs so
                // it remains the Task Manager group head.
                ShowWindow(hWnd, SW_RESTORE);
                SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW);
                UpdateWindow(hWnd);
                SetForegroundWindow(hWnd);
            }
#endif
        }

        public static void DragLauncherWindow()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            IntPtr hWnd = GetWindowHandle();
            if (hWnd != IntPtr.Zero)
            {
                ReleaseCapture();
                SendMessage(hWnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
#endif
        }
    }
}
