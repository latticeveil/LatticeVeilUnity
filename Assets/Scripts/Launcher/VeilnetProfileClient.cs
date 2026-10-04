using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LatticeVeil.Launcher
{
    /// <summary>
    /// Fetches the Veilnet profile (username + picture URL) from the launcher-me
    /// edge function. Unity port of the MonoGame VeilnetProfileClient, kept
    /// minimal: the launcher only needs the username and the profile picture URL.
    /// Uses the same retry schedule and key fallbacks as the MonoGame version.
    /// </summary>
    internal sealed class VeilnetProfileClient
    {
        private static readonly TimeSpan[] RetryDelays =
        {
            TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(900)
        };

        private readonly HttpClient _http;
        private readonly string _functionsBaseUrl;
        private readonly string _anonKey;

        public VeilnetProfileClient(string functionsBaseUrl, string anonKey, HttpClient http = null)
        {
            _functionsBaseUrl = (functionsBaseUrl ?? string.Empty).Trim().TrimEnd('/');
            _anonKey = (anonKey ?? string.Empty).Trim();
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        }

        public sealed class ProfileResult
        {
            public bool Ok;
            public string Username = "";
            public string PictureUrl = "";
            public string Error;
        }

        public async Task<ProfileResult> GetProfileAsync(string accessToken, CancellationToken ct = default)
        {
            var token = (accessToken ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(token) || IsPlaceholderToken(token))
                return new ProfileResult { Error = "missing_access_token" };

            for (int attempt = 0; ; attempt++)
            {
                var result = await GetOnceAsync(token, ct).ConfigureAwait(false);
                if (result.Ok) return result;

                var transient = result.Error != null
                    && (result.Error.StartsWith("HTTP 5", StringComparison.Ordinal)
                        || result.Error.StartsWith("request_", StringComparison.Ordinal));
                if (!transient || attempt >= RetryDelays.Length)
                    return result;

                await Task.Delay(RetryDelays[attempt], ct).ConfigureAwait(false);
            }
        }

        private async Task<ProfileResult> GetOnceAsync(string token, CancellationToken ct)
        {
            try
            {
                var url = $"{_functionsBaseUrl}/launcher-me";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (!string.IsNullOrWhiteSpace(_anonKey))
                    req.Headers.TryAddWithoutValidation("apikey", _anonKey);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return new ProfileResult { Error = $"HTTP {(int)resp.StatusCode}" };

                var username = ExtractFirstString(body, "username", "displayName", "display_name");
                var picture = ExtractFirstString(body, "picture", "avatar", "avatar_url", "picture_url");
                if (string.IsNullOrWhiteSpace(username))
                    return new ProfileResult { Error = "profile_lookup_failed" };

                return new ProfileResult { Ok = true, Username = username.Trim(), PictureUrl = (picture ?? string.Empty).Trim() };
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex)
            {
                return new ProfileResult { Error = "request_" + ex.GetType().Name };
            }
            catch (Exception ex)
            {
                return new ProfileResult { Error = ex.Message };
            }
        }

        private static bool IsPlaceholderToken(string token) =>
            Regex.IsMatch(token, "^(null|undefined|none|placeholder)$", RegexOptions.IgnoreCase);

        /// <summary>
        /// Pulls the first present string property (root or nested "profile"/"user"
        /// objects both work, since the search scans the whole body) without a JSON
        /// dependency - same key fallbacks as the MonoGame parser.
        /// </summary>
        private static string ExtractFirstString(string json, params string[] keys)
        {
            if (string.IsNullOrEmpty(json)) return null;
            foreach (var key in keys)
            {
                var m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
                    return m.Groups[1].Value;
            }
            return null;
        }
    }
}
