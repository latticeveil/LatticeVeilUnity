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

        /// <summary>
        /// True while the user's saved option says borderless fullscreen.
        /// Window-style fixes (caption/thickframe) must NOT touch the window
        /// in that state, or they punch a title bar into the fullscreen view
        /// and the mode "doesn't stick" after a relaunch.
        /// </summary>
        public static bool FullscreenDesired { get; private set; }

        /// <summary>
        /// Set as early as possible (boot) so window-style fixes know the
        /// saved mode even before the full Apply runs.
        /// </summary>
        public static void SetFullscreenDesired(bool fullscreen)
        {
            FullscreenDesired = fullscreen;
        }

        /// <summary>Idempotent. Call at menu start and after options APPLY.</summary>
        public static void Apply(GameSettings settings)
        {
            if (settings == null)
                return;

            FullscreenDesired = settings.Fullscreen;

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
            // FULLSCREEN now actually applies: borderless fullscreen window
            // when enabled, normal windowed otherwise.
            RequestResolution(settings.ResolutionWidth, settings.ResolutionHeight,
                settings.Fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed);

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

            // WHITE-SCREEN FIX: Screen.SetResolution recreates the swap chain
            // while our custom window style (WS_THICKFRAME removed) is active,
            // and the player presents into a stale swap chain -> white frame
            // until the next resize. Instead we resize the OS window directly
            // with SetWindowPos. In windowed mode Unity's renderer follows the
            // window client size every frame, so there is no swap-chain
            // recreation and nothing can go white.
            var hwnd = GetGameHwnd();
            if (hwnd != System.IntPtr.Zero && _targetMode == FullScreenMode.Windowed)
            {
                // LEAVING FULLSCREEN: the SetWindowPos path only resizes the OS
                // window — the Unity renderer would stay in its fullscreen swap
                // chain and the option felt "stuck". Drop out of the mode first,
                // then restyle + resize the now-windowed window below.
                if (Screen.fullScreenMode != FullScreenMode.Windowed)
                {
                    Screen.SetResolution(_targetW, _targetH, FullScreenMode.Windowed);
                    for (int i = 0; i < 50 && Screen.fullScreen; i++)
                        yield return new WaitForSecondsRealtime(0.1f);
                    Debug.Log("SettingsBehavior: left fullscreen mode (SetResolution Windowed).");
                    yield return null;
                }

                // Wait for the window frame to actually exist: at boot the
                // player may still be borderless, and resizing a borderless
                // window gets overridden by MainMenuController.EnsureWindowFrame
                // right after (which reapplies its own frame).
                for (int i = 0; i < 60; i++)
                {
                    const long WS_CAPTION = 0x00C00000;
                    var style = GetWindowLongW(hwnd, -16 /*GWL_STYLE*/);
                    if ((style & WS_CAPTION) != 0)
                        break;
                    yield return new WaitForSecondsRealtime(0.1f);
                }

                UnlockWindowResize();
                yield return null; // let the style change settle

                // Convert the wanted CLIENT size to the outer window size the
                // locked style needs (caption bar, borders), then resize AND
                // re-center on the current monitor.
                var rect = new RECT { Left = 0, Top = 0, Right = _targetW, Bottom = _targetH };
                AdjustWindowRect(ref rect, LockedStyle, false);
                int outerW = rect.Right - rect.Left;
                int outerH = rect.Bottom - rect.Top;

                GetWindowRect(hwnd, out var cur);
                int x = cur.Left + ((cur.Right - cur.Left) - outerW) / 2;
                int y = cur.Top + ((cur.Bottom - cur.Top) - outerH) / 2;
                if (y < 0) y = 0;

                SetWindowPosW(hwnd, System.IntPtr.Zero, x, y, outerW, outerH, SWP_NOZORDER);

                // Wait until the renderer actually reports the new client size
                // (Screen.width/height lag a few frames behind SetWindowPos).
                // Treating a lagging read as failure produced the spurious
                // "resolution change did not stick" warning.
                for (int i = 0; i < 50; i++)
                {
                    if (GetClientRectSize(hwnd, out var cw, out var ch)
                        && Mathf.Abs(cw - _targetW) <= 8 && Mathf.Abs(ch - _targetH) <= 8)
                        break;
                    yield return new WaitForSecondsRealtime(0.1f);
                }
                yield return null;

                LockWindowResize();
                if (GetClientRectSize(hwnd, out var w, out var h)
                    && Mathf.Abs(w - _targetW) <= 8 && Mathf.Abs(h - _targetH) <= 8)
                    Debug.Log($"SettingsBehavior: resolution now {w}x{h} (SetWindowPos path).");
                else
                    Debug.LogWarning($"SettingsBehavior: resolution change to {_targetW}x{_targetH} did not stick.");
                yield break;
            }

            // Windowed path skipped (embedded or fullscreen): defer + retry
            // with Screen.SetResolution, which rebuilds the back buffer.
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

        private const uint SWP_NOZORDER = 0x0004;

        private const long LockedStyle = 0x00C00000 /*WS_CAPTION*/ | 0x00080000 /*WS_SYSMENU*/ | 0x00020000 /*WS_MINIMIZEBOX*/;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool AdjustWindowRect(ref RECT lpRect, long dwStyle, bool bMenu);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(System.IntPtr hWnd, out RECT lpRect);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NaRECT { public int Left; public int Top; public int Right; public int Bottom; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetClientRect(System.IntPtr hWnd, out NaRECT lpRect);

        /// <summary>Actual client size of the game window in pixels.</summary>
        private static bool GetClientRectSize(System.IntPtr hwnd, out int w, out int h)
        {
            w = 0; h = 0;
            if (hwnd == System.IntPtr.Zero || !GetClientRect(hwnd, out var r))
                return false;
            w = r.Right - r.Left;
            h = r.Bottom - r.Top;
            return w > 0 && h > 0;
        }


        // ------------------------------------------------------------------
        // Live appliers
        // ------------------------------------------------------------------

        /// <summary>
        /// GUI scale: scales the WHOLE menu UI (buttons + layout together) by
        /// adjusting each menu canvas' reference resolution. Because every
        /// element is anchored inside the canvas (corners to corners, center to
        /// center), scaling shrinks/grows everything in place — a corner button
        /// stays in its corner, just smaller. SMALL=0.9, STANDARD=1.0, LARGER=1.25 (max).
        /// </summary>
        public static void ApplyGuiScale(float guiScale)
        {
            var scale = Mathf.Clamp(guiScale, 0.9f, 1.25f);
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
            if (FullscreenDesired)
                return; // borderless fullscreen owns the window style; never lock it

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
