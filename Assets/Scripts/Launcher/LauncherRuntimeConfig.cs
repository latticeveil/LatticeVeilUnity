using System;
using System.IO;
using LatticeVeil.Core;

namespace LatticeVeil.Launcher
{
    /// <summary>
    /// Launcher runtime configuration.
    /// Ported from MonoGame LauncherRuntimeConfig.cs implementation.
    /// Uses simple JSON parsing for Unity compatibility.
    /// </summary>
    internal sealed class LauncherRuntimeConfig
    {
        private sealed class RawModel
        {
            public string VeilnetFunctionsBaseUrl { get; set; }
            public string GameHashesGetUrl { get; set; }
            public string VeilnetLauncherPageUrl { get; set; }
            public string SupabaseAnonKey { get; set; }
        }

        public static readonly string ConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LatticeVeil");

        public static readonly string ConfigPath = Path.Combine(ConfigDirectory, "launcher_config.json");

        public string VeilnetFunctionsBaseUrl { get; set; } = string.Empty;
        public string GameHashesGetUrl { get; set; } = string.Empty;
        public string VeilnetLauncherPageUrl { get; set; } = string.Empty;
        public string SupabaseAnonKey { get; set; } = string.Empty;

        public static LauncherRuntimeConfig Empty { get; } = new();

        public static LauncherRuntimeConfig Load(Logger log)
        {
            try
            {
                if (!File.Exists(ConfigPath))
                    return Empty;

                var json = File.ReadAllText(ConfigPath);
                if (string.IsNullOrWhiteSpace(json))
                    return Empty;

                var raw = ParseJson(json);

                if (raw == null)
                    return Empty;

                var loaded = new LauncherRuntimeConfig
                {
                    VeilnetFunctionsBaseUrl = NormalizeUrl(raw.VeilnetFunctionsBaseUrl, trimTrailingSlash: true),
                    GameHashesGetUrl = NormalizeUrl(raw.GameHashesGetUrl),
                    VeilnetLauncherPageUrl = NormalizeUrl(raw.VeilnetLauncherPageUrl),
                    SupabaseAnonKey = (raw.SupabaseAnonKey ?? string.Empty).Trim()
                };

                log.Info($"Loaded launcher runtime config from {ConfigPath}");
                return loaded;
            }
            catch (Exception ex)
            {
                log.Warn($"Failed to load launcher runtime config ({ConfigPath}): {ex.Message}");
                return Empty;
            }
        }

        private static RawModel ParseJson(string json)
        {
            try
            {
                var model = new RawModel();
                
                var veilnetStart = json.IndexOf("\"VeilnetFunctionsBaseUrl\":");
                if (veilnetStart >= 0)
                {
                    var valueStart = json.IndexOf("\"", veilnetStart + 24);
                    var valueEnd = json.IndexOf("\"", valueStart + 1);
                    if (valueStart >= 0 && valueEnd >= 0)
                        model.VeilnetFunctionsBaseUrl = json.Substring(valueStart + 1, valueEnd - valueStart - 1);
                }
                
                var hashesStart = json.IndexOf("\"GameHashesGetUrl\":");
                if (hashesStart >= 0)
                {
                    var valueStart = json.IndexOf("\"", hashesStart + 18);
                    var valueEnd = json.IndexOf("\"", valueStart + 1);
                    if (valueStart >= 0 && valueEnd >= 0)
                        model.GameHashesGetUrl = json.Substring(valueStart + 1, valueEnd - valueStart - 1);
                }
                
                var launcherStart = json.IndexOf("\"VeilnetLauncherPageUrl\":");
                if (launcherStart >= 0)
                {
                    var valueStart = json.IndexOf("\"", launcherStart + 23);
                    var valueEnd = json.IndexOf("\"", valueStart + 1);
                    if (valueStart >= 0 && valueEnd >= 0)
                        model.VeilnetLauncherPageUrl = json.Substring(valueStart + 1, valueEnd - valueStart - 1);
                }
                
                var anonKeyStart = json.IndexOf("\"SupabaseAnonKey\":");
                if (anonKeyStart >= 0)
                {
                    var valueStart = json.IndexOf("\"", anonKeyStart + 19);
                    var valueEnd = json.IndexOf("\"", valueStart + 1);
                    if (valueStart >= 0 && valueEnd >= 0)
                        model.SupabaseAnonKey = json.Substring(valueStart + 1, valueEnd - valueStart - 1);
                }
                
                return model;
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeUrl(string value, bool trimTrailingSlash = false)
        {
            var url = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;

            return trimTrailingSlash ? url.TrimEnd('/') : url;
        }
    }
}
