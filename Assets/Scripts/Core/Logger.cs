using System;
using System.IO;
using System.Text;

namespace LatticeVeil.Core
{
    /// <summary>
    /// Simple logger for LatticeVeil launcher.
    /// Mirrors the MonoGame Logger structure for compatibility.
    /// </summary>
    public class Logger
    {
        private readonly string _logFilePath;
        private readonly object _lock = new object();
        private readonly StringBuilder _recentLog = new StringBuilder();

        public string LogFilePath => _logFilePath;

        public Logger(string logFilePath = null, bool truncateOnStart = true)
        {
            _logFilePath = logFilePath ?? Paths.ActiveLogPath;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath) ?? Paths.LogsDir);

                if (truncateOnStart && File.Exists(_logFilePath))
                {
                    File.Delete(_logFilePath);
                }

                Info("Logger initialized.");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"Failed to initialize logger: {ex.Message}");
            }
        }

        public void Info(string message)
        {
            Log("INFO", message);
        }

        public void Warn(string message)
        {
            Log("WARN", message);
        }

        public void Error(string message)
        {
            Log("ERROR", message);
        }

        public void Fatal(Exception ex, string context)
        {
            Log("FATAL", $"{context}: {ex}");
        }

        private void Log(string level, string message)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var logLine = $"[{timestamp}] [{level}] {message}";

            lock (_lock)
            {
                try
                {
                    File.AppendAllText(_logFilePath, logLine + Environment.NewLine);
                }
                catch
                {
                    // Silently fail if we can't write to file
                }

                _recentLog.AppendLine(logLine);

                // Also log to Unity console
                switch (level)
                {
                    case "INFO":
                        UnityEngine.Debug.Log(message);
                        break;
                    case "WARN":
                        UnityEngine.Debug.LogWarning(message);
                        break;
                    case "ERROR":
                    case "FATAL":
                        UnityEngine.Debug.LogError(message);
                        break;
                }
            }
        }

        public string GetRecentLogs(int maxLines = 100)
        {
            lock (_lock)
            {
                var lines = _recentLog.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length <= maxLines)
                    return _recentLog.ToString();

                var result = new StringBuilder();
                for (int i = lines.Length - maxLines; i < lines.Length; i++)
                {
                    result.AppendLine(lines[i]);
                }
                return result.ToString();
            }
        }
    }
}