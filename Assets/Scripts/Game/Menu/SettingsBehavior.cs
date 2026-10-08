using LatticeVeil.Core;
using UnityEngine;

namespace LatticeVeil.Game
{
    /// <summary>
    /// Applies GameSettings to live engine behavior. Called when settings are
    /// applied (Options APPLY) and at startup so options.lvc drives the game
    /// exactly as the MonoGame version's GameSettings consumers do.
    ///
    /// Also owns the no-resize window rule: the standalone game window is a
    /// fixed size (the serialized resolution) and the GUI scales with the
    /// resolution via CanvasScaler, so UI never cuts off.
    /// </summary>
    public static class SettingsBehavior
    {
        private static bool _appliedOnce;

        /// <summary>Idempotent. Call at menu start and after options APPLY.</summary>
        public static void Apply(GameSettings settings)
        {
            if (settings == null)
                return;

            // VSync + target frame rate.
            QualitySettings.vSyncCount = settings.VSync ? 1 : 0;

            // Brightness -> ambient light intensity (0.5x..1.5x as in the MonoGame version).
            RenderSettings.ambientIntensity = settings.Brightness;

            // Audio volumes (0..1).
            AudioListener.volume = settings.MasterVolume;

            // Window mode + resolution: forced windowed (non-borderless) for
            // now — fullscreen honors the setting but keeps Windowed mode off
            // by default per the options file (Fullscreen=false).
            var mode = FullScreenMode.Windowed;
            if (!Screen.fullScreenMode.Equals(mode) ||
                Screen.width != settings.ResolutionWidth ||
                Screen.height != settings.ResolutionHeight)
            {
                Screen.SetResolution(settings.ResolutionWidth, settings.ResolutionHeight, mode);
            }

            if (!_appliedOnce)
            {
                _appliedOnce = true;
                Debug.Log($"Settings applied: vsync={settings.VSync}, brightness={settings.Brightness:0.00}, " +
                          $"volume={settings.MasterVolume:0.00}, resolution={settings.ResolutionWidth}x{settings.ResolutionHeight}");
            }
        }

        /// <summary>
        /// Locks the standalone window against OS-level resizing: after the
        /// player creates its window, remove WS_THICKFRAME/WS_MAXIMIZEBOX so
        /// the window is a fixed size (GUI scaling handles the rest).
        /// </summary>
        public static void LockWindowResize()
        {
            var hwnd = GetGameHwnd();
            if (hwnd == System.IntPtr.Zero)
                return;

            const int GWL_STYLE = -16;
            const long WS_CAPTION = 0x00C00000;
            const long WS_THICKFRAME = 0x00040000;
            const long WS_MAXIMIZEBOX = 0x00010000;
            const long WS_MINIMIZEBOX = 0x00020000;
            const uint SWP_FRAMECHANGED = 0x0020;
            const uint SWP_NOMOVE = 0x0002;
            const uint SWP_NOSIZE = 0x0001;
            const uint SWP_NOZORDER = 0x0004;

            var style = GetWindowLongW(hwnd, GWL_STYLE);
            if ((style & WS_THICKFRAME) == 0 && (style & WS_MAXIMIZEBOX) == 0)
                return; // already locked

            var newStyle = (style | WS_CAPTION | WS_MINIMIZEBOX) & ~WS_THICKFRAME & ~WS_MAXIMIZEBOX;
            SetWindowLongW(hwnd, GWL_STYLE, newStyle);
            SetWindowPosW(hwnd, System.IntPtr.Zero, 0, 0, 0, 0,
                SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER);
            Debug.Log("SettingsBehavior: window resize locked (fixed size).");
        }

        private static System.IntPtr GetGameHwnd()
        {
            try
            {
                return System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            }
            catch
            {
                return System.IntPtr.Zero;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern long GetWindowLongW(System.IntPtr hWnd, int index);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern long SetWindowLongW(System.IntPtr hWnd, int index, long newStyle);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowPos")]
        private static extern bool SetWindowPosW(System.IntPtr hWnd, System.IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
