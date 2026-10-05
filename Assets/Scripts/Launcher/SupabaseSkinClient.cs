using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LatticeVeil.Launcher
{
    /// <summary>
    /// Handles syncing player skin data with Supabase via the official Veilnet Edge Functions
    /// (<c>player-skin-set</c> and <c>player-skin-get</c>).
    /// These functions accept Veilnet JWT tokens and manage the <c>public.player_skins</c> table.
    /// </summary>
    internal sealed class SupabaseSkinClient
    {
        private readonly HttpClient _http;
        private readonly string _functionsBaseUrl;
        private readonly string _anonKey;
        private readonly string _userToken;

        /// <summary>Maximum number of skins a user can keep online.</summary>
        public const int MaxSkinsPerUser = 5;

        public sealed class FetchResult
        {
            public bool   Success     { get; set; }
            public bool   HasSkin     { get; set; }
            public int    Slot        { get; set; }
            public string Hash        { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public byte[] PngBytes    { get; set; }
            public string Error       { get; set; }
        }

        public SupabaseSkinClient(string functionsBaseUrl, string anonKey, string userToken, HttpClient http = null)
        {
            _functionsBaseUrl = (functionsBaseUrl ?? string.Empty).Trim().TrimEnd('/');
            _anonKey          = (anonKey ?? string.Empty).Trim();
            _userToken        = (userToken ?? string.Empty).Trim();
            _http             = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        }

        /// <summary>
        /// Uploads a skin to the user's Veilnet account. Slot 0 is the primary
        /// account skin (website + game); slots 1..4 form the online library.
        /// The payload format is unchanged (hash + pngBase64).
        /// </summary>
        public async Task<(bool ok, string error)> UploadSkinAsync(string hash, byte[] pngBytes, string displayName = "Account Skin", bool hasLayers = false, CancellationToken ct = default, int slot = 0)
        {
            if (string.IsNullOrWhiteSpace(_userToken))
                return (false, "Not logged in to Veilnet.");
            if (pngBytes == null || pngBytes.Length == 0)
                return (false, "No skin PNG data to upload.");
            if (string.IsNullOrWhiteSpace(hash))
                return (false, "Missing skin hash.");
            if (slot < 0 || slot >= MaxSkinsPerUser)
                return (false, $"Skin slot must be 0..{MaxSkinsPerUser - 1}.");

            try
            {
                var b64 = Convert.ToBase64String(pngBytes);
                var escapedName = EscapeJson(string.IsNullOrWhiteSpace(displayName) ? "Account Skin" : displayName);
                var body = $"{{\"action\":\"set\",\"hash\":\"{hash.ToLowerInvariant()}\",\"pngBase64\":\"{b64}\",\"displayName\":\"{escapedName}\",\"hasLayers\":{(hasLayers ? "true" : "false")},\"slot\":{slot}}}";

                var url = $"{_functionsBaseUrl}/player-skin-set";
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                AddHeaders(req);

                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    var ok = ExtractJsonBool(respBody, "ok");
                    if (ok) return (true, null);
                }

                var err = ExtractJsonString(respBody, "error");
                var detail = ExtractJsonString(respBody, "detail");
                var msg = !string.IsNullOrEmpty(detail) ? $"{err}: {detail}" : (!string.IsNullOrEmpty(err) ? err : $"HTTP {(int)resp.StatusCode}");
                return (false, msg);
            }
            catch (Exception ex)
            {
                return (false, $"Upload error: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears the player's active skin on Veilnet (sets default/baseline skin).
        /// </summary>
        public async Task<(bool ok, string error)> ClearRemoteSkinAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_userToken))
                return (false, "Not logged in to Veilnet.");

            try
            {
                var body = "{\"action\":\"clear\"}";
                var url = $"{_functionsBaseUrl}/player-skin-set";
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                AddHeaders(req);

                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    var ok = ExtractJsonBool(respBody, "ok");
                    if (ok) return (true, null);
                }

                var err = ExtractJsonString(respBody, "error");
                var detail = ExtractJsonString(respBody, "detail");
                var msg = !string.IsNullOrEmpty(detail) ? $"{err}: {detail}" : (!string.IsNullOrEmpty(err) ? err : $"HTTP {(int)resp.StatusCode}");
                return (false, msg);
            }
            catch (Exception ex)
            {
                return (false, $"Clear skin error: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes one slot (0..4) from the user's online skin library. The
        /// remaining slots are untouched.
        /// </summary>
        public async Task<(bool ok, string error)> RemoveSkinAsync(int slot, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_userToken))
                return (false, "Not logged in to Veilnet.");
            if (slot < 0 || slot >= MaxSkinsPerUser)
                return (false, $"Skin slot must be 0..{MaxSkinsPerUser - 1}.");

            try
            {
                var body = $"{{\"action\":\"remove\",\"slot\":{slot}}}";
                var url = $"{_functionsBaseUrl}/player-skin-set";
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                AddHeaders(req);

                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    var ok = ExtractJsonBool(respBody, "ok");
                    if (ok) return (true, null);
                }

                var err = ExtractJsonString(respBody, "error");
                var detail = ExtractJsonString(respBody, "detail");
                var msg = !string.IsNullOrEmpty(detail) ? $"{err}: {detail}" : (!string.IsNullOrEmpty(err) ? err : $"HTTP {(int)resp.StatusCode}");
                return (false, msg);
            }
            catch (Exception ex)
            {
                return (false, $"Remove skin error: {ex.Message}");
            }
        }

        /// <summary>
        /// Fetches the player's skin from Veilnet (<c>player-skin-get</c>).
        /// </summary>
        public async Task<FetchResult> FetchSkinAsync(CancellationToken ct = default)
        {
            var all = await FetchSkinsAsync(ct).ConfigureAwait(false);
            if (all.Count > 0) return all[0];
            return new FetchResult { Success = true, HasSkin = false };
        }

        /// <summary>
        /// Fetches ALL skins stored on the user's account (up to 5 slots).
        /// Ordered by slot; slot 0 first. A failed list parse degrades to the
        /// legacy single-skin fields so old deployments keep working.
        /// </summary>
        public async Task<System.Collections.Generic.List<FetchResult>> FetchSkinsAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_userToken))
            {
                var singleFail = new System.Collections.Generic.List<FetchResult> { Fail("Not logged in to Veilnet.") };
                return singleFail;
            }

            try
            {
                var url = $"{_functionsBaseUrl}/player-skin-get";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                AddHeaders(req);

                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = ExtractJsonString(respBody, "error");
                    return new System.Collections.Generic.List<FetchResult> { Fail(!string.IsNullOrEmpty(err) ? err : $"HTTP {(int)resp.StatusCode}") };
                }

                var ok = ExtractJsonBool(respBody, "ok");
                if (!ok)
                {
                    var err = ExtractJsonString(respBody, "error");
                    return new System.Collections.Generic.List<FetchResult> { Fail(string.IsNullOrEmpty(err) ? "Request failed" : err) };
                }

                var hasSkin = ExtractJsonBool(respBody, "hasSkin");
                if (!hasSkin)
                {
                    // No custom skin configured online (default baseline skin)
                    return new System.Collections.Generic.List<FetchResult>();
                }

                var results = new System.Collections.Generic.List<FetchResult>();
                // Parse every flat {...} skin object: first prefer the "skins" array
                // (slot-aware deployments), falling back to the legacy "skin" object.
                var objectMatches = Regex.Matches(respBody ?? string.Empty, "\\{(?<obj>[^{}]*)\\}", RegexOptions.IgnoreCase);
                foreach (Match m in objectMatches)
                {
                    var obj = m.Groups["obj"].Value;
                    if (!Regex.IsMatch(obj, "\"pngBase64\"", RegexOptions.IgnoreCase)) continue;
                    // The legacy top-level "skin" object and array entries both parse here.
                    if (results.Count > 0 && obj.IndexOf("slot", StringComparison.OrdinalIgnoreCase) < 0 && respBody.IndexOf("\"skins\"", StringComparison.OrdinalIgnoreCase) < 0)
                        break; // legacy single-object response: only one skin

                    var slotMatch = Regex.Match(obj, "\"slot\"\\s*:\\s*(\\d+)", RegexOptions.IgnoreCase);
                    var hash = ExtractJsonString(obj, "hash");
                    var b64 = ExtractJsonString(obj, "pngBase64");
                    var displayName = ExtractJsonString(obj, "displayName");
                    if (string.IsNullOrWhiteSpace(b64)) continue;

                    byte[] pngBytes;
                    try { pngBytes = Convert.FromBase64String(b64); }
                    catch (FormatException) { continue; }

                    results.Add(new FetchResult
                    {
                        Success = true,
                        HasSkin = true,
                        Slot = slotMatch.Success ? int.Parse(slotMatch.Groups[1].Value) : results.Count,
                        Hash = (hash ?? string.Empty).Trim().ToLowerInvariant(),
                        DisplayName = displayName,
                        PngBytes = pngBytes
                    });
                }

                // De-duplicate identical slot entries (array + legacy fallback) and sort.
                var seen = new System.Collections.Generic.HashSet<int>();
                results.RemoveAll(r => !seen.Add(r.Slot));
                results.Sort((a, b) => a.Slot.CompareTo(b.Slot));
                return results;
            }
            catch (Exception ex)
            {
                return new System.Collections.Generic.List<FetchResult> { Fail($"Fetch error: {ex.Message}") };
            }
        }

        private void AddHeaders(HttpRequestMessage req)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _userToken);
            if (!string.IsNullOrWhiteSpace(_anonKey))
            {
                req.Headers.TryAddWithoutValidation("apikey", _anonKey);
            }
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        private static FetchResult Fail(string error) =>
            new FetchResult { Success = false, Error = error };

        private static string EscapeJson(string s) =>
            (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");

        private static string ExtractJsonString(string body, string key)
        {
            var pattern = $"\"{Regex.Escape(key)}\"\\s*:\\s*\"((?:\\\\\"|[^\"])*)\"";
            var m = Regex.Match(body, pattern, RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\") : string.Empty;
        }

        private static string ExtractJsonStringFromObject(string body, string objectKey, string key)
        {
            var objectPattern = $"\"{Regex.Escape(objectKey)}\"\\s*:\\s*\\{{(?<object>.*?)\\}}";
            var objectMatch = Regex.Match(body ?? string.Empty, objectPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return objectMatch.Success
                ? ExtractJsonString(objectMatch.Groups["object"].Value, key)
                : string.Empty;
        }

        private static bool ExtractJsonBool(string body, string key)
        {
            var pattern = $"\"{Regex.Escape(key)}\"\\s*:\\s*(true|false)";
            var m = Regex.Match(body, pattern, RegexOptions.IgnoreCase);
            return m.Success && string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
