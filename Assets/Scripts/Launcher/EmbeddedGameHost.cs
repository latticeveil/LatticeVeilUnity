using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LatticeVeil.Launcher
{
    /// <summary>
    /// Hosts the game window INSIDE the launcher window (embedded play).
    /// Unity standalone players support native embedding: launching with
    /// "-parentHWND &lt;hwnd&gt;" makes the player create its window as a child
    /// of the given window instead of a separate top-level window. The
    /// embedded player does not follow parent resizes on its own, so this
    /// host polls every frame and keeps the game window sized to the
    /// launcher's client area — including its first placement (the Unity
    /// window can appear a second or two after the process starts).
    /// Only Unity player builds accept -parentHWND; the legacy MonoGame exe
    /// is detected via its missing "&lt;exe&gt;_Data" folder and skipped.
    /// </summary>
    internal static class EmbeddedGameHost
    {
        private static IntPtr _launcherHwnd;
        private static IntPtr _lastGameHwnd;
        private static int _lastClientW = -1;
        private static int _lastClientH = -1;
        private static bool _active;

        /// <summary>True while an embedded game session is being hosted.</summary>
        public static bool Active => _active;

        /// <summary>
        /// A Unity player build always carries an "&lt;ExeName&gt;_Data" folder
        /// next to its exe; MonoGame/legacy builds do not.
        /// </summary>
        public static bool IsUnityPlayerExe(string exePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                    return false;
                var dataDir = Path.Combine(
                    Path.GetDirectoryName(exePath) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(exePath) + "_Data");
                return Directory.Exists(dataDir);
            }
            catch { return false; }
        }

        /// <summary>Arms the host before/after the game process starts. Idempotent.</summary>
        public static void Begin(IntPtr launcherHwnd, Core.Logger log = null)
        {
            if (launcherHwnd == IntPtr.Zero)
            {
                log?.Warn("EmbeddedGameHost.Begin: no launcher window handle; embed skipped.");
                return;
            }
            _launcherHwnd = launcherHwnd;
            _lastGameHwnd = IntPtr.Zero;
            _lastClientW = -1;
            _lastClientH = -1;
            _active = true;
            log?.Info("Embedded game host armed — the game will run inside the launcher window.");
        }

        /// <summary>
        /// Call every frame while the game runs. Finds the game's window
        /// (embedded Unity windows are children of the launcher), sizes it to
        /// fill the launcher client area, and claims it into the launcher if
        /// the player ignored -parentHWND (defensive SetParent).
        /// </summary>
        public static void Poll(Process game, Core.Logger log = null)
        {
            if (!_active || _launcherHwnd == IntPtr.Zero || game == null || game.HasExited)
                return;

            var gameHwnd = FindGameWindow(game);
            if (gameHwnd == IntPtr.Zero)
                return; // window not up yet — retry next frame

            if (!GetClientRect(_launcherHwnd, out var rc))
                return;
            int w = rc.Right - rc.Left;
            int h = rc.Bottom - rc.Top;
            if (w <= 0 || h <= 0)
                return;

            bool isNewWindow = gameHwnd != _lastGameHwnd;
            if (!isNewWindow && w == _lastClientW && h == _lastClientH)
                return; // nothing changed this frame

            // Defensive: if the player was launched without -parentHWND (or an
            // older build ignores it), pull its window into the launcher now.
            if (GetAncestor(gameHwnd, GA_ROOT) != _launcherHwnd)
                SetParent(gameHwnd, _launcherHwnd);

            if (!SetWindowPos(gameHwnd, IntPtr.Zero, 0, 0, w, h, SwpNoZOrder | SwpNoActivate))
                return;

            _lastGameHwnd = gameHwnd;
            _lastClientW = w;
            _lastClientH = h;
            if (isNewWindow)
                log?.Info($"Embedded game window attached and sized {w}x{h} inside the launcher.");
        }

        /// <summary>Releases the host. Safe to call from any teardown path.</summary>
        public static void End(Core.Logger log = null)
        {
            if (!_active)
                return;
            _active = false;
            _lastGameHwnd = IntPtr.Zero;
            _launcherHwnd = IntPtr.Zero;
            log?.Info("Embedded game host released.");
        }

        /// <summary>
        /// Locates the game window: the launcher's child window of Unity's
        /// well-known "UnityWndClass" when embedded, else the process main
        /// window (top-level, before the player creates its window or when
        /// it never became a child).
        /// </summary>
        private static IntPtr FindGameWindow(Process game)
        {
            _foundHwnd = IntPtr.Zero;
            EnumChildWindows(_launcherHwnd, EnumChildCallback, IntPtr.Zero);
            if (_foundHwnd != IntPtr.Zero)
                return _foundHwnd;

            try { return game.MainWindowHandle; }
            catch { return IntPtr.Zero; }
        }

        private static IntPtr _foundHwnd;

        private static bool EnumChildCallbackImpl(IntPtr hwnd, IntPtr lparam)
        {
            var sb = new StringBuilder(64);
            if (GetClassName(hwnd, sb, sb.Capacity) > 0 &&
                string.Equals(sb.ToString(), "UnityWndClass", StringComparison.OrdinalIgnoreCase))
            {
                _foundHwnd = hwnd;
                return false; // stop enumeration
            }
            return true;
        }

        // Cache the delegate so the GC can never collect it while user32 holds it.
        private static readonly EnumChildWindowsProc EnumChildCallback = EnumChildCallbackImpl;
        private delegate bool EnumChildWindowsProc(IntPtr hwnd, IntPtr lparam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumChildWindowsProc proc, IntPtr lparam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);
        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint GA_ROOT = 2;
    }
}
