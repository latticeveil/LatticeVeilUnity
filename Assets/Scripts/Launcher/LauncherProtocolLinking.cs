using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using LatticeVeil.Core;

namespace LatticeVeil.Launcher
{
    /// <summary>
    /// Protocol linking for Unity launcher.
    /// Ported from MonoGame LauncherProtocolLinking.cs implementation.
    /// Simplified for Unity compatibility - handles protocol parsing and queue management.
    /// </summary>
    internal static class LauncherProtocolLinking
    {
        private const string Scheme = "latticeveil";
        private static readonly string QueueDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LatticeVeil");
        private static readonly string QueuePath = Path.Combine(QueueDir, "veilnet_link_queue.txt");
        private static readonly string UnityQueuePath = Path.Combine(QueueDir, "unity_veilnet_link_queue.txt");
        private static readonly string PendingLoginMarkerPath = Path.Combine(QueueDir, "unity_login_pending.txt");
        private static readonly string SkinImportQueuePath = Path.Combine(QueueDir, "skin_import_clipboard_queue.txt");
        private static readonly string SkinRefreshQueuePath = Path.Combine(QueueDir, "skin_library_refresh_queue.txt");
        private static readonly string RestoreLauncherQueuePath = Path.Combine(QueueDir, "launcher_restore_queue.txt");

        public static void TryEnsureProtocolRegistration(Logger log)
        {
            try
            {
                var bridgePath = ResolveBridgeExecutablePath();
                if (!string.IsNullOrWhiteSpace(bridgePath) && File.Exists(bridgePath))
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = bridgePath,
                            Arguments = "--register",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        using var proc = Process.Start(psi);
                        proc?.WaitForExit(3000);
                        log.Info($"Protocol registration ensured via bridge: {bridgePath}");
                        return;
                    }
                    catch (Exception ex)
                    {
                        log.Warn($"Failed to run protocol bridge for registration: {ex.Message}");
                    }
                }

                var exePath = ResolveLauncherExecutablePath();
                log.Info($"Protocol bridge not found, launcher exe: {exePath}");
            }
            catch (Exception ex)
            {
                log.Warn($"Protocol registration failed: {ex.Message}");
            }
        }

        public static string TryExtractLinkCodeFromArgs(string[] args)
        {
            if (args == null || args.Length == 0)
                return null;

            for (var i = 0; i < args.Length; i++)
            {
                var code = TryExtractLinkCodeFromUri(args[i]);
                if (!string.IsNullOrWhiteSpace(code))
                    return code;
            }

            return null;
        }

        public static bool HasSkinImportClipboardRequest(string[] args)
        {
            if (args == null || args.Length == 0)
                return false;

            for (var i = 0; i < args.Length; i++)
            {
                if (IsSkinImportClipboardUri(args[i]))
                    return true;
            }

            return false;
        }

        public static bool HasSkinLibraryRefreshRequest(string[] args)
        {
            if (args == null || args.Length == 0)
                return false;

            for (var i = 0; i < args.Length; i++)
            {
                if (IsSkinLibraryRefreshUri(args[i]))
                    return true;
            }

            return false;
        }

        public static void SetUnityLoginPending(Logger log)
        {
            try
            {
                Directory.CreateDirectory(QueueDir);
                File.WriteAllText(PendingLoginMarkerPath, DateTime.UtcNow.ToString("O"), Encoding.UTF8);
                log?.Info("Marked Unity login flow as active.");
            }
            catch (Exception ex)
            {
                log?.Warn($"Failed to mark Unity login pending: {ex.Message}");
            }
        }

        public static void ClearUnityLoginPending(Logger log)
        {
            try
            {
                if (File.Exists(PendingLoginMarkerPath))
                {
                    File.Delete(PendingLoginMarkerPath);
                    log?.Info("Cleared Unity login pending marker.");
                }
            }
            catch (Exception ex)
            {
                log?.Warn($"Failed to clear Unity login pending marker: {ex.Message}");
            }
        }

        public static bool IsUnityLoginPending()
        {
            try
            {
                if (!File.Exists(PendingLoginMarkerPath))
                    return false;

                var text = File.ReadAllText(PendingLoginMarkerPath).Trim();
                if (DateTime.TryParse(text, out var dt))
                {
                    return (DateTime.UtcNow - dt.ToUniversalTime()) < TimeSpan.FromMinutes(10);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsClassicRunning()
        {
            try
            {
                return Process.GetProcessesByName("LatticeVeilMonoGame").Length > 0;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryQueueLinkCodeForRunningLauncher(string code, Logger log)
        {
            try
            {
                var normalized = NormalizeCode(code);
                if (string.IsNullOrWhiteSpace(normalized))
                    return false;

                Directory.CreateDirectory(QueueDir);
                File.AppendAllText(UnityQueuePath, normalized + Environment.NewLine, Encoding.UTF8);
                log.Info($"Queued protocol link code for running Unity launcher at {UnityQueuePath}");
                return true;
            }
            catch (Exception ex)
            {
                log.Warn($"Failed to queue protocol link code: {ex.Message}");
                return false;
            }
        }

        public static string[] DequeuePendingLinkCodes(Logger log)
        {
            try
            {
                var codes = new System.Collections.Generic.List<string>();

                // 1. Dedicated Unity queue
                if (File.Exists(UnityQueuePath))
                {
                    var lines = File.ReadAllLines(UnityQueuePath);
                    File.Delete(UnityQueuePath);
                    codes.AddRange(lines.Select(NormalizeCode).Where(v => !string.IsNullOrWhiteSpace(v)));
                }

                // 2. Generic queue fallback if Classic is not running or if Unity initiated the login
                if (codes.Count == 0 && File.Exists(QueuePath))
                {
                    if (IsUnityLoginPending() || !IsClassicRunning())
                    {
                        var lines = File.ReadAllLines(QueuePath);
                        File.Delete(QueuePath);
                        codes.AddRange(lines.Select(NormalizeCode).Where(v => !string.IsNullOrWhiteSpace(v)));
                    }
                }

                var distinct = codes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (distinct.Length > 0)
                    log.Info($"Dequeued {distinct.Length} pending protocol link code(s) for Unity");

                return distinct;
            }
            catch (Exception ex)
            {
                log.Warn($"Failed to dequeue protocol link codes: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        public static bool TryQueueSkinImportClipboardForRunningLauncher(Logger log)
        {
            try
            {
                Directory.CreateDirectory(QueueDir);
                File.AppendAllText(SkinImportQueuePath, DateTime.UtcNow.ToString("O") + Environment.NewLine, Encoding.UTF8);
                log.Info($"Queued protocol skin import request for running launcher at {SkinImportQueuePath}");
                return true;
            }
            catch (Exception ex)
            {
                log.Warn($"Failed to queue protocol skin import request: {ex.Message}");
                return false;
            }
        }

        public static bool TryQueueSkinLibraryRefreshForRunningLauncher(Logger log)
        {
            try
            {
                Directory.CreateDirectory(QueueDir);
                File.AppendAllText(SkinRefreshQueuePath, DateTime.UtcNow.ToString("O") + Environment.NewLine, Encoding.UTF8);
                log.Info($"Queued protocol skin library refresh request for running launcher at {SkinRefreshQueuePath}");
                return true;
            }
            catch (Exception ex)
            {
                log.Warn($"Failed to queue protocol skin library refresh request: {ex.Message}");
                return false;
            }
        }

        public static bool TryQueueRestoreLauncherForRunningLauncher(Logger log)
        {
            try
            {
                Directory.CreateDirectory(QueueDir);
                File.AppendAllText(RestoreLauncherQueuePath, DateTime.UtcNow.ToString("O") + Environment.NewLine, Encoding.UTF8);
                log.Info($"Queued launcher restore request for running launcher at {RestoreLauncherQueuePath}");
                return true;
            }
            catch (Exception ex)
            {
                log.Warn($"Failed to queue launcher restore request: {ex.Message}");
                return false;
            }
        }

        public static int DequeuePendingSkinImportClipboardRequests(Logger log)
        {
            try
            {
                if (!File.Exists(SkinImportQueuePath))
                    return 0;

                var count = File.ReadAllLines(SkinImportQueuePath)
                    .Count(line => !string.IsNullOrWhiteSpace(line));
                File.Delete(SkinImportQueuePath);

                if (count > 0)
                    log.Info($"Dequeued {count} pending skin import request(s) from {SkinImportQueuePath}");

                return count;
            }
            catch (Exception ex)
            {
                log.Warn($"Failed to dequeue protocol skin import requests: {ex.Message}");
                return 0;
            }
        }

        public static int DequeuePendingSkinLibraryRefreshRequests(Logger log)
        {
            try
            {
                if (!File.Exists(SkinRefreshQueuePath))
                    return 0;

                var count = File.ReadAllLines(SkinRefreshQueuePath)
                    .Count(line => !string.IsNullOrWhiteSpace(line));
                File.Delete(SkinRefreshQueuePath);

                if (count > 0)
                    log.Info($"Dequeued {count} pending skin library refresh request(s) from {SkinRefreshQueuePath}");

                return count;
            }
            catch (Exception ex)
            {
                log.Warn($"Failed to dequeue protocol skin library refresh requests: {ex.Message}");
                return 0;
            }
        }

        public static int DequeuePendingRestoreLauncherRequests(Logger log)
        {
            try
            {
                if (!File.Exists(RestoreLauncherQueuePath))
                    return 0;

                var count = File.ReadAllLines(RestoreLauncherQueuePath)
                    .Count(line => !string.IsNullOrWhiteSpace(line));
                File.Delete(RestoreLauncherQueuePath);

                if (count > 0)
                    log.Info($"Dequeued {count} launcher restore request(s) from {RestoreLauncherQueuePath}");

                return count;
            }
            catch (Exception ex)
            {
                log.Warn($"Failed to dequeue launcher restore requests: {ex.Message}");
                return 0;
            }
        }

        public static void ClearPendingLinkCodes(Logger log)
        {
            try
            {
                if (File.Exists(QueuePath))
                {
                    File.Delete(QueuePath);
                    log.Info($"Cleared pending protocol link code queue at {QueuePath}");
                }
            }
            catch (Exception ex)
            {
                log.Warn($"Failed to clear protocol link code queue: {ex.Message}");
            }
        }

        private static string TryExtractLinkCodeFromUri(string rawValue)
        {
            var value = (rawValue ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(value))
                return null;

            if (!value.StartsWith($"{Scheme}://", StringComparison.OrdinalIgnoreCase))
                return null;

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
                return null;

            if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
                return null;

            var action = (uri.Host ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(action))
                action = (uri.AbsolutePath ?? string.Empty).Trim('/').ToLowerInvariant();

            if (!string.Equals(action, "link", StringComparison.OrdinalIgnoreCase))
                return null;

            var query = (uri.Query ?? string.Empty).TrimStart('?');
            if (string.IsNullOrWhiteSpace(query))
                return null;

            var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < pairs.Length; i++)
            {
                var pair = pairs[i];
                var split = pair.Split('=', 2);
                var key = Uri.UnescapeDataString(split[0] ?? string.Empty).Trim();
                if (!string.Equals(key, "code", StringComparison.OrdinalIgnoreCase))
                    continue;

                var valuePart = split.Length > 1 ? split[1] : string.Empty;
                var code = Uri.UnescapeDataString(valuePart ?? string.Empty);
                code = string.Concat(code.Where(ch => !char.IsWhiteSpace(ch))).Trim().ToUpperInvariant();
                if (code.Length < 4 || code.Length > 24)
                    return null;
                return code;
            }

            return null;
        }

        private static bool IsSkinImportClipboardUri(string rawValue)
        {
            var value = (rawValue ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (!value.StartsWith($"{Scheme}://", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
                return false;

            if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
                return false;

            var action = (uri.Host ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(action))
                action = (uri.AbsolutePath ?? string.Empty).Trim('/').ToLowerInvariant();

            return string.Equals(action, "skin-import-clipboard", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSkinLibraryRefreshUri(string rawValue)
        {
            var value = (rawValue ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (!value.StartsWith($"{Scheme}://", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
                return false;

            if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
                return false;

            var action = (uri.Host ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(action))
                action = (uri.AbsolutePath ?? string.Empty).Trim('/').ToLowerInvariant();

            return string.Equals(action, "skin-library-refresh", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeCode(string raw)
        {
            var value = string.Concat((raw ?? string.Empty).Where(ch => !char.IsWhiteSpace(ch)))
                .Trim()
                .ToUpperInvariant();
            if (value.Length < 4 || value.Length > 24)
                return string.Empty;
            return value;
        }

        public static string ResolveBridgeExecutablePath()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LatticeVeil", "bin", "LatticeVeilProtocolBridge.exe"),
                Path.Combine(AppContext.BaseDirectory, "LatticeVeilProtocolBridge.exe"),
                Path.Combine(AppContext.BaseDirectory, "..", "LatticeVeilProtocolBridge.exe"),
                @"C:\Users\Redacted\Documents\LatticeVeilUnity\builds\LatticeVeilProtocolBridge.exe",
                @"C:\Users\Redacted\Documents\LatticeVeil_project\DEV\LatticeVeilProtocolBridge.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LatticeVeil", "LatticeVeilProtocolBridge.exe")
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }
                catch { }
            }

            return null;
        }

        private static string ResolveLauncherExecutablePath()
        {
            try
            {
                var modulePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                modulePath = modulePath.Trim();
                if (!string.IsNullOrWhiteSpace(modulePath) && File.Exists(modulePath))
                    return modulePath;
            }
            catch
            {
                // Best effort.
            }

            return Path.Combine(AppContext.BaseDirectory, "LatticeVeilUnity.exe");
        }
    }
}
