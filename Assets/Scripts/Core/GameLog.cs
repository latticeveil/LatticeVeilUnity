using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace LatticeVeil.Core
{
    /// <summary>
    /// Game-side log file. Writes to Documents/LatticeVeil/logs/game.lvlog —
    /// the same folder the launcher logs to (current.lvlog) but a DIFFERENT
    /// file, so the game never truncates or replaces the launcher's log.
    ///
    /// Captures EVERYTHING logged through Unity's Debug.* (game code and
    /// engine messages alike) via Application.logMessageReceived, so callers
    /// can just use Debug.Log as usual. The log rolls over at 5 MB
    /// (game.lvlog -> game.lvlog.1) to keep the shared logs folder tidy.
    /// </summary>
    public static class GameLog
    {
        private const long MaxLogBytes = 5 * 1024 * 1024;
        private static readonly object Lock = new object();
        private static bool _initialized;
        private static string _path;

        /// <summary>Path of the active game log file, or null before init.</summary>
        public static string LogFilePath => _path;

        /// <summary>Idempotent. Safe to call multiple times / from any scene.</summary>
        public static void Initialize()
        {
            if (_initialized)
                return;
            _initialized = true;

            try
            {
                Directory.CreateDirectory(Paths.LogsDir);
                _path = Path.Combine(Paths.LogsDir, "game.lvlog");
                RotateIfNeeded();

                Application.logMessageReceived += OnLogMessage;
                Append("INFO", $"Game log started (build V{GameVersion.Current})");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("GameLog: failed to initialize file logging: " + ex.Message);
            }
        }

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            var level =
                type == LogType.Error || type == LogType.Assert || type == LogType.Exception ? "ERROR" :
                type == LogType.Warning ? "WARN" : "INFO";
            Append(level, condition);
        }

        private static void Append(string level, string message)
        {
            if (string.IsNullOrEmpty(_path))
                return;

            try
            {
                lock (Lock)
                {
                    RotateIfNeeded();
                    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
            }
            catch
            {
                // Never let logging break the game.
            }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                if (!File.Exists(_path) || new FileInfo(_path).Length < MaxLogBytes)
                    return;

                var backup = _path + ".1";
                if (File.Exists(backup))
                    File.Delete(backup);
                File.Move(_path, backup);
            }
            catch
            {
                // Rotation is best-effort.
            }
        }
    }
}
