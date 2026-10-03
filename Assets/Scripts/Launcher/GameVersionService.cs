using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LatticeVeil.Launcher
{
    /// <summary>
    /// One downloadable game version advertised by a GitHub release on
    /// latticeveil/LatticeVeilUnity. Any release that ships a .zip asset is
    /// listed, so future uploads appear in the launcher dropdown automatically.
    /// </summary>
    public sealed class GameVersionInfo
    {
        public string Tag { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string AssetName { get; set; } = "";
        public string AssetUrl { get; set; } = "";
        public long AssetSizeBytes { get; set; }
        public string PublishedAt { get; set; } = "";
        public bool IsPrerelease { get; set; }

        /// <summary>Release notes body from GitHub (markdown, may be empty).</summary>
        public string Body { get; set; } = "";

        // ---- Manifest-driven fields (releases shipping exe + version.json) ----
        /// <summary>Version number from the manifest (e.g. "17.0.0").</summary>
        public string VersionNumber { get; set; } = "";
        /// <summary>Engine declared by the manifest ("monogame" or "unity").</summary>
        public string Engine { get; set; } = "";
        /// <summary>Expected SHA-256 of the game exe, from the manifest.</summary>
        public string ExeHash { get; set; } = "";
        public string ExeAssetName { get; set; } = "";
        public string ExeAssetUrl { get; set; } = "";
        public long ExeAssetSizeBytes { get; set; }
        /// <summary>Raw manifest json, persisted beside the installed exe.</summary>
        public string ManifestJson { get; set; } = "";

        /// <summary>True when the release ships the exe + version.json manifest pair.</summary>
        public bool HasManifest =>
            !string.IsNullOrWhiteSpace(ExeHash) && !string.IsNullOrWhiteSpace(ExeAssetUrl);

        public bool IsInstalled { get; set; }
        public string InstallDirectory { get; set; } = "";

        /// <summary>
        /// Engine badge shown next to the version in UI lists. The legacy MonoGame
        /// builds (the "first release" standalone client) are tagged [MONOGAME];
        /// everything else is a native Unity build.
        /// </summary>
        public string EngineBadge =>
            IsLegacyEngine ? " [MONOGAME]" : "";

        public bool IsLegacyEngine
        {
            get
            {
                if (string.Equals(Engine, "monogame", StringComparison.OrdinalIgnoreCase))
                    return true;

                var haystack = $"{Tag} {DisplayName}";
                return haystack.IndexOf("legacy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       haystack.IndexOf("monogame", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        /// <summary>Short single-line label for dropdowns/lists (manifest name preferred).</summary>
        public string ListLabel =>
            $"{(string.IsNullOrWhiteSpace(DisplayName) ? Tag : DisplayName)}{EngineBadge}";

        public string SizeDisplay =>
            AssetSizeBytes <= 0 ? "" :
            AssetSizeBytes >= 1_000_000_000 ? string.Format("{0:0.0} GB", AssetSizeBytes / 1_000_000_000.0) :
            string.Format("{0:0} MB", AssetSizeBytes / 1_000_000.0);
    }

    /// <summary>
    /// Fetches the release list from the LatticeVeilUnity GitHub repo and maps each
    /// release with a .zip asset to a GameVersionInfo. The dropdown always reflects
    /// the latest uploads when refreshed.
    /// </summary>
    internal sealed class GameVersionService
    {
        private const string ReleasesApiUrl =
            "https://api.github.com/repos/latticeveil/LatticeVeilUnity/releases?per_page=100";
        private const string UserAgent = "LatticeVeil-Launcher/1.0";

        private readonly HttpClient _http;
        private readonly Core.Logger _log;

        public GameVersionService(Core.Logger log, HttpClient http)
        {
            _log = log;
            _http = http;
        }

        /// <summary>Fetches all releases and returns downloadable game versions (newest first).</summary>
        public async Task<List<GameVersionInfo>> GetVersionsAsync(CancellationToken ct = default)
        {
            var versions = new List<GameVersionInfo>();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
                request.Headers.Add("User-Agent", UserAgent);
                request.Headers.Add("Accept", "application/vnd.github+json");

                // Send the prepared request — GetAsync(request.RequestUri) would
                // discard these headers and GitHub 403s User-Agent-less calls.
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return versions;
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                foreach (var release in JsonLite.SplitTopLevelObjects(json))
                {
                    var tag = JsonLite.ExtractString(release, "tag_name");
                    if (string.IsNullOrWhiteSpace(tag))
                        continue;

                    var publishedAt = JsonLite.ExtractString(release, "published_at");
                    var isPrerelease = JsonLite.ExtractBool(release, "prerelease");
                    var body = JsonLite.ExtractString(release, "body") ?? "";
                    var installedExe = Core.Paths.TryResolveInstalledVersionExe(tag);

                    GameVersionInfo version = null;

                    // Preferred flow: release ships the game exe + a version.json
                    // manifest (name, number, engine, sha256, size). No zip needed —
                    // the launcher reads the manifest directly from the release.
                    var exeAsset = JsonLite.FindFirstAssetWithExtension(release, ".exe");
                    var jsonAsset = JsonLite.FindFirstAssetWithExtension(release, ".json");
                    if (!string.IsNullOrWhiteSpace(exeAsset.Item2) && !string.IsNullOrWhiteSpace(jsonAsset.Item2))
                    {
                        try
                        {
                            using var manifestRequest = new HttpRequestMessage(HttpMethod.Get, jsonAsset.Item2);
                            manifestRequest.Headers.Add("User-Agent", UserAgent);
                            using var manifestResponse = await _http.SendAsync(manifestRequest, ct).ConfigureAwait(false);
                            manifestResponse.EnsureSuccessStatusCode();
                            var manifestJson = await manifestResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                            version = new GameVersionInfo
                            {
                                Tag = tag,
                                DisplayName = FirstNonEmpty(
                                    JsonLite.ExtractString(manifestJson, "versionName"),
                                    JsonLite.ExtractString(release, "name"),
                                    tag),
                                VersionNumber = JsonLite.ExtractString(manifestJson, "version"),
                                Engine = JsonLite.ExtractString(manifestJson, "engine"),
                                ExeHash = (JsonLite.ExtractString(manifestJson, "sha256") ?? "").Trim(),
                                ExeAssetName = exeAsset.Item1,
                                ExeAssetUrl = exeAsset.Item2,
                                ExeAssetSizeBytes = exeAsset.Item3,
                                ManifestJson = manifestJson,
                                PublishedAt = publishedAt,
                                IsPrerelease = isPrerelease,
                                Body = body
                            };

                            // Fall back to the manifest's size when the asset metadata is unavailable.
                            var manifestSize = JsonLite.ExtractNumber(manifestJson, "size");
                            version.AssetSizeBytes = version.ExeAssetSizeBytes > 0 ? version.ExeAssetSizeBytes : manifestSize;
                            version.AssetName = version.ExeAssetName;
                            version.AssetUrl = version.ExeAssetUrl;
                        }
                        catch (Exception manifestEx)
                        {
                            _log?.Warn($"Manifest fetch failed for {tag}: {manifestEx.Message}");
                        }
                    }

                    // Fallback flow: legacy zip-only releases (no hash verification possible).
                    if (version == null)
                    {
                        var asset = JsonLite.FindFirstZipAsset(release);
                        if (string.IsNullOrWhiteSpace(asset.Item1))
                            continue;

                        version = new GameVersionInfo
                        {
                            Tag = tag,
                            DisplayName = FirstNonEmpty(JsonLite.ExtractString(release, "name"), tag),
                            AssetName = asset.Item1,
                            AssetUrl = asset.Item2,
                            AssetSizeBytes = asset.Item3,
                            PublishedAt = publishedAt,
                            IsPrerelease = isPrerelease,
                            Body = body
                        };
                    }

                    version.IsInstalled = installedExe != null;
                    version.InstallDirectory = installedExe != null
                        ? System.IO.Path.GetDirectoryName(installedExe)
                        : "";

                    versions.Add(version);
                }

                versions.Sort((a, b) => string.Compare(b.PublishedAt, a.PublishedAt, StringComparison.Ordinal));
                _log?.Info($"GameVersionService: {versions.Count} downloadable version(s) found.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log?.Warn($"GameVersionService fetch failed: {ex.Message}");
            }
            return versions;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return string.Empty;
        }
    }

    /// <summary>
    /// Minimal JSON scanning for the GitHub release payload (machine-generated and
    /// regular). Avoids pulling a full JSON dependency into the launcher.
    /// </summary>
    internal static class JsonLite
    {
        /// <summary>Splits a JSON array into raw top-level object substrings.</summary>
        public static List<string> SplitTopLevelObjects(string json)
        {
            var results = new List<string>();
            if (string.IsNullOrEmpty(json)) return results;

            var arrayStart = json.IndexOf('[');
            if (arrayStart < 0) return results;

            var i = arrayStart + 1;
            while (i < json.Length)
            {
                while (i < json.Length && (char.IsWhiteSpace(json[i]) || json[i] == ',')) i++;
                if (i >= json.Length || json[i] == ']') break;
                if (json[i] != '{') break;

                var close = FindMatchingBrace(json, i);
                if (close < 0) break;
                results.Add(json.Substring(i, close - i + 1));
                i = close + 1;
            }
            return results;
        }

        private static int FindMatchingBrace(string json, int openIndex)
        {
            var inString = false;
            var escaped = false;
            var depth = 0;
            for (var i = openIndex; i < json.Length; i++)
            {
                var c = json[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        public static string ExtractString(string json, string key)
        {
            var search = "\"" + key + "\"";
            var idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return string.Empty;
            var colon = json.IndexOf(':', idx + search.Length);
            if (colon < 0) return string.Empty;
            var quote1 = json.IndexOf('"', colon + 1);
            if (quote1 < 0) return string.Empty;

            var sb = new System.Text.StringBuilder();
            for (var i = quote1 + 1; i < json.Length; i++)
            {
                var c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    var n = json[i + 1];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 't') sb.Append('\t');
                    else if (n == 'r') { i++; continue; }
                    else sb.Append(n);
                    i++;
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        public static bool ExtractBool(string json, string key)
        {
            var search = "\"" + key + "\"";
            var idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return false;
            var colon = json.IndexOf(':', idx + search.Length);
            if (colon < 0) return false;
            var rest = json.Substring(colon + 1, Math.Min(12, json.Length - colon - 1));
            return rest.TrimStart().StartsWith("true", StringComparison.Ordinal);
        }

        public static long ExtractNumber(string json, string key)
        {
            var search = "\"" + key + "\"";
            var idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return 0;
            var colon = json.IndexOf(':', idx + search.Length);
            if (colon < 0) return 0;
            var sb = new System.Text.StringBuilder();
            for (var i = colon + 1; i < json.Length; i++)
            {
                if (char.IsDigit(json[i])) sb.Append(json[i]);
                else if (sb.Length > 0) break;
            }
            return long.TryParse(sb.ToString(), out var v) ? v : 0;
        }

        /// <summary>Finds the first .zip asset (name, browser_download_url, size) in a release object.</summary>
        public static (string, string, long) FindFirstZipAsset(string releaseJson) =>
            FindFirstAssetWithExtension(releaseJson, ".zip");

        /// <summary>Finds the first asset with the given extension (name, browser_download_url, size).</summary>
        public static (string, string, long) FindFirstAssetWithExtension(string releaseJson, string extension)
        {
            var assetsIdx = releaseJson.IndexOf("\"assets\"", StringComparison.Ordinal);
            if (assetsIdx < 0) return (null, null, 0);
            var open = releaseJson.IndexOf('[', assetsIdx);
            if (open < 0) return (null, null, 0);
            var close = releaseJson.IndexOf(']', open);
            if (close < 0) return (null, null, 0);
            var assetsArray = releaseJson.Substring(open, close - open + 1);

            foreach (var obj in SplitTopLevelObjects(assetsArray))
            {
                var name = ExtractString(obj, "name");
                if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
                var url = ExtractString(obj, "browser_download_url");
                if (string.IsNullOrWhiteSpace(url)) continue;
                return (name, url, ExtractNumber(obj, "size"));
            }
            return (null, null, 0);
        }
    }
}
