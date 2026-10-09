using LatticeVeil.Core;
using UnityEngine;
using UnityEngine.UI;

namespace LatticeVeil.Game
{
    /// <summary>
    /// Applies GameSettings to live engine behavior. Called when settings are
    /// applied (Options APPLY) and at startup so options.lvc drives the game.
    ///
    /// Owns two timing-sensitive rules:
    ///  1. Resolution changes are DEFERRED to the end of frame and retried
    ///     across several frames. Calling Screen.SetResolution from a UI click
    ///     handler while the window style is being rewritten caused the
    ///     renderer to present a blank (white) frame until the next resize.
    ///  2. The no-resize window style is REAPPLIED after every mode change,
    ///     because the player can recreate the window (WS_POPUP) when the
    ///     resolution/mode changes.
    /// </summary>
    public static class SettingsBehavior
    {
        private static bool _appliedOnce;
        private static MonoBehaviour _host;
        private static int _retriesLeft;
        private static int _targetW;
        private static int _targetH;
        private static FullScreenMode _targetMode = FullScreenMode.Windowed;

        /// <summary>Idempotent. Call at menu start and after options APPLY.</summary>
        public static void Apply(GameSettings settings)
        {
            if (settings == null)
                return;

            // VSync + brightness + audio.
            QualitySettings.vSyncCount = settings.VSync ? 1 : 0;
            RenderSettings.ambientIntensity = Mathf.Lerp(0.6f, 1.4f, Mathf.Clamp01((settings.Brightness - 0.5f) / 1.0f));
            AudioListener.volume = settings.MasterVolume;

            // Quality preset maps to Unity quality levels by name when available.
            ApplyQualityPreset(settings.QualityPreset);

            // FOV + render distance: push to any live camera(s); the world
            // renderer picks these up from GameSettings when it exists.
            ApplyCameraFov(settings.FieldOfView);
            QualitySettings.shadowDistance = settings.RenderDistanceChunks * 16f;

            // Resolution: defer + retry (never inside the click handler).
            RequestResolution(settings.ResolutionWidth, settings.ResolutionHeight, FullScreenMode.Windowed);

            // GUI scale drives the shared menu canvas immediately.
            ApplyGuiScale(settings.GuiScale);

            if (!_appliedOnce)
            {
                _appliedOnce = true;
                Debug.Log($"Settings applied: vsync={settings.VSync}, brightness={settings.Brightness:0.00}, " +
                          $"volume={settings.MasterVolume:0.00}, resolution={settings.ResolutionWidth}x{settings.ResolutionHeight}");
            }
        }

        // ------------------------------------------------------------------
        // Resolution (deferred, retried)
        // ------------------------------------------------------------------

        /// <summary>
        /// Queues a resolution change. The actual Screen.SetResolution happens
        /// at the end of the frame and is retried until the screen size sticks
        /// or retries run out — this avoids presenting frames during the
        /// window swap, which showed as a white screen.
        /// </summary>
        public static void RequestResolution(int width, int height, FullScreenMode mode)
        {
            if (width <= 0 || height <= 0)
                return;

            if (Screen.width == width && Screen.height == height &&
                Screen.fullScreenMode.Equals(mode))
                return;

            _targetW = width;
            _targetH = height;
            _targetMode = mode;
            _retriesLeft = 10;

            EnsureHost();
            if (_host != null)
            {
                _host.StopAllCoroutines();
                _host.StartCoroutine(ResolutionRoutine());
            }
        }

        private static System.Collections.IEnumerator ResolutionRoutine()
        {
            // Let the current frame (and its UI click) fully finish first.
            yield return new WaitForEndOfFrame();

            // The no-resize window style (WS_THICKFRAME removed) makes
            // Screen.SetResolution silently fail — the player presents into a
            // stale swap chain, which the user sees as a WHITE screen. Unlock
            // the window, let Unity resize, then re-lock.
            UnlockWindowResize();
            yield return null;

            while (_retriesLeft-- > 0)
            {
                Screen.SetResolution(_targetW, _targetH, _targetMode);
                yield return new WaitForSecondsRealtime(0.3f);

                if (Screen.width == _targetW && Screen.height == _targetH)
                {
                    LockWindowResize();
                    Debug.Log($"SettingsBehavior: resolution now {_targetW}x{_targetH}.");
                    yield break;
                }
            }
            LockWindowResize();
            Debug.LogWarning($"SettingsBehavior: resolution change to {_targetW}x{_targetH} did not stick.");
        }

        // ------------------------------------------------------------------
        // Live appliers
        // ------------------------------------------------------------------

        /// <summary>GUI scale: multiplies the reference DPI of every menu canvas.</summary>
        public static void ApplyGuiScale(float guiScale)
        {
            var scale = Mathf.Clamp(guiScale, 0.5f, 3f);
            foreach (var scaler in Object.FindObjectsOfType<CanvasScaler>())
            {
                if (scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize)
                    continue;
                scaler.referenceResolution = new Vector2(1920f / scale, 1080f / scale);
            }
        }

        /// <summary>Maps the preset name onto Unity quality levels when present.</summary>
        public static void ApplyQualityPreset(string preset)
        {
            var name = string.IsNullOrWhiteSpace(preset) ? "MEDIUM" : preset.Trim();
            var levels = QualitySettings.names;
            for (int i = 0; i < levels.Length; i++)
            {
                if (string.Equals(levels[i], name, System.StringComparison.OrdinalIgnoreCase))
                {
                    QualitySettings.SetQualityLevel(i, true);
                    return;
                }
            }
            // Fallback: bucket by preset position (LOW=0 ... ULTRA=top).
            int idx = name.ToUpperInvariant() switch
            {
                "LOW" => 0,
                "MEDIUM" => (levels.Length - 1) / 3,
                "HIGH" => (levels.Length * 2 - 1) / 3,
                "ULTRA" => levels.Length - 1,
                _ => (levels.Length - 1) / 3
            };
            QualitySettings.SetQualityLevel(Mathf.Clamp(idx, 0, levels.Length - 1), true);
        }

        /// <summary>FOV applies to every camera tagged MainCamera (menu + world).</summary>
        public static void ApplyCameraFov(int fov)
        {
            var value = Mathf.Clamp(fov, 60, 110);
            foreach (var cam in Object.FindObjectsOfType<Camera>())
                cam.fieldOfView = value;
        }

        // ------------------------------------------------------------------
        // Window locking (unchanged behavior, reused after resolution change)
        // ------------------------------------------------------------------

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

        /// <summary>Re-enables the OS resize frame so SetResolution can work.</summary>
        public static void UnlockWindowResize()
        {
            var hwnd = GetGameHwnd();
            if (hwnd == System.IntPtr.Zero)
                return;

            const int GWL_STYLE = -16;
            const long WS_CAPTION = 0x00C00000;
            const long WS_THICKFRAME = 0x00040000;
            const long WS_MAXIMIZEBOX = 0x00010000;
            const long WS_MINIMIZEBOX = 0x00020000;
            const long WS_SYSMENU = 0x00080000;
            const uint SWP_FRAMECHANGED = 0x0020;
            const uint SWP_NOMOVE = 0x0002;
            const uint SWP_NOSIZE = 0x0001;
            const uint SWP_NOZORDER = 0x0004;

            var style = GetWindowLongW(hwnd, GWL_STYLE);
            if ((style & WS_THICKFRAME) != 0)
                return; // already resizable

            var newStyle = style | WS_THICKFRAME | WS_MAXIMIZEBOX | WS_MINIMIZEBOX | WS_CAPTION | WS_SYSMENU;
            SetWindowLongW(hwnd, GWL_STYLE, newStyle);
            SetWindowPosW(hwnd, System.IntPtr.Zero, 0, 0, 0, 0,
                SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER);
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

        /// <summary>
        /// Coroutines need a MonoBehaviour host. Reuses the menu controller
        /// when present, otherwise creates a quiet persistent object.
        /// </summary>
        private static void EnsureHost()
        {
            if (_host != null)
                return;
            foreach (var mb in Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (mb.GetType().Name == "MainMenuController")
                {
                    _host = mb;
                    return;
                }
            }
            var go = new GameObject("SettingsBehaviorHost");
            Object.DontDestroyOnLoad(go);
            _host = go.AddComponent<SettingsBehaviorHost>();
        }

        private sealed class SettingsBehaviorHost : MonoBehaviour { }
    }
}
