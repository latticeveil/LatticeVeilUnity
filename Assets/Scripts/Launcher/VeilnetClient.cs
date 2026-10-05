using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LatticeVeil.Launcher
{
    /// <summary>
    /// Veilnet client for Unity launcher.
    /// Ported from MonoGame implementation with robust JSON extraction for cross-platform compatibility.
    /// </summary>
    internal sealed class VeilnetClient
    {
        private readonly HttpClient _http;
        private readonly string _functionsBaseUrl;

        internal sealed class ExchangeResponse
        {
            public string Token { get; set; } = string.Empty;
            public string Username { get; set; } = string.Empty;
            public string UserId { get; set; } = string.Empty;
            public string UserIdSnakeCase { get; set; } = string.Empty;

            public void NormalizeUserId()
            {
                if (string.IsNullOrWhiteSpace(UserId))
                    UserId = (UserIdSnakeCase ?? string.Empty).Trim();
                else
                    UserId = UserId.Trim();
            }
        }

        internal sealed class MeResponse
        {
            public string Username { get; set; } = string.Empty;
            public string UserId { get; set; } = string.Empty;
            public string UserIdSnakeCase { get; set; } = string.Empty;

            public void NormalizeUserId()
            {
                if (string.IsNullOrWhiteSpace(UserId))
                    UserId = (UserIdSnakeCase ?? string.Empty).Trim();
                else
                    UserId = UserId.Trim();
            }
        }

        internal sealed class DevAccessResponse
        {
            public bool Ok { get; set; }
            public bool Allowed { get; set; }
            public string Role { get; set; } = string.Empty;
            public string Username { get; set; } = string.Empty;
            public bool IsAdmin { get; set; }
            public bool IsTester { get; set; }
        }

        public VeilnetClient(string functionsBaseUrl, HttpClient http = null)
        {
            _functionsBaseUrl = (functionsBaseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(_functionsBaseUrl))
                throw new ArgumentException("functionsBaseUrl is required", nameof(functionsBaseUrl));

            _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        }

        public async Task<ExchangeResponse> ExchangeCodeAsync(string code, CancellationToken ct = default)
        {
            var normalizedCode = NormalizeCode(code);
            if (string.IsNullOrWhiteSpace(normalizedCode))
                throw new Exception("Please paste a valid Veilnet link code.");

            var url = $"{_functionsBaseUrl}/launcher-exchange";
            var payload = $"{{\"code\":\"{normalizedCode}\"}}";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new Exception(MapExchangeError(response.StatusCode, ParseErrorKey(body)));

            var parsed = ParseExchangeResponse(body);
            parsed?.NormalizeUserId();

            if (parsed == null
                || string.IsNullOrWhiteSpace(parsed.Token)
                || string.IsNullOrWhiteSpace(parsed.Username)
                || string.IsNullOrWhiteSpace(parsed.UserId))
            {
                throw new Exception("Veilnet returned an invalid exchange response.");
            }

            return parsed;
        }

        public async Task<MeResponse> GetMeAsync(string token, CancellationToken ct = default)
        {
            token = (token ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(token))
                throw new Exception("Missing launcher token.");

            var url = $"{_functionsBaseUrl}/launcher-me";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new Exception(MapMeError(response.StatusCode, ParseErrorKey(body)));

            var parsed = ParseMeResponse(body);
            parsed?.NormalizeUserId();

            if (parsed == null || string.IsNullOrWhiteSpace(parsed.Username))
                throw new Exception("Veilnet returned an invalid profile response.");

            return parsed;
        }

        public async Task<DevAccessResponse> CheckDevAccessAsync(string token, CancellationToken ct = default)
        {
            token = (token ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(token))
                throw new Exception("Missing launcher token.");

            var url = $"{_functionsBaseUrl}/dev-access";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent("{\"action\":\"check\"}", Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new Exception(MapDevAccessError(response.StatusCode, ParseErrorKey(body)));

            var parsed = ParseDevAccessResponse(body);
            if (parsed == null)
                throw new Exception("Veilnet returned an invalid DEV access response.");

            return parsed;
        }

        private static string NormalizeCode(string code)
        {
            var value = (code ?? string.Empty).Trim().ToUpperInvariant();
            if (value.Length == 0) return string.Empty;
            return string.Concat(value.Where(ch => !char.IsWhiteSpace(ch)));
        }

        private static string ParseErrorKey(string body)
        {
            return ExtractJsonString(body, "error", "message", "err");
        }

        private static ExchangeResponse ParseExchangeResponse(string body)
        {
            try
            {
                var response = new ExchangeResponse
                {
                    Token = ExtractJsonString(body, "token", "access_token", "accessToken"),
                    Username = ExtractJsonString(body, "username", "userName", "name"),
                    UserId = ExtractJsonString(body, "user_id", "userId", "id", "sub"),
                    UserIdSnakeCase = ExtractJsonString(body, "user_id")
                };
                response.NormalizeUserId();
                return response;
            }
            catch
            {
                return null;
            }
        }

        private static MeResponse ParseMeResponse(string body)
        {
            try
            {
                var response = new MeResponse
                {
                    Username = ExtractJsonString(body, "username", "userName", "name"),
                    UserId = ExtractJsonString(body, "user_id", "userId", "id", "sub"),
                    UserIdSnakeCase = ExtractJsonString(body, "user_id")
                };
                response.NormalizeUserId();
                return response;
            }
            catch
            {
                return null;
            }
        }

        private static DevAccessResponse ParseDevAccessResponse(string body)
        {
            try
            {
                var response = new DevAccessResponse
                {
                    Ok = ExtractJsonBool(body, "ok", "success"),
                    Allowed = ExtractJsonBool(body, "allowed", "is_allowed", "isAllowed"),
                    Role = ExtractJsonString(body, "role"),
                    Username = ExtractJsonString(body, "username", "userName"),
                    IsAdmin = ExtractJsonBool(body, "is_admin", "isAdmin", "admin"),
                    IsTester = ExtractJsonBool(body, "is_tester", "isTester", "tester")
                };
                return response;
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractJsonString(string body, params string[] propertyNames)
        {
            if (string.IsNullOrWhiteSpace(body) || propertyNames == null || propertyNames.Length == 0)
                return string.Empty;

            foreach (var prop in propertyNames)
            {
                if (string.IsNullOrWhiteSpace(prop)) continue;

                var pattern = $"\"{Regex.Escape(prop)}\"\\s*:\\s*\"((?:\\\\\"|[^\"])*)\"";
                var match = Regex.Match(body, pattern, RegexOptions.IgnoreCase);
                if (match.Success && match.Groups.Count > 1)
                {
                    var val = match.Groups[1].Value;
                    val = val.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\/", "/");
                    return val.Trim();
                }
            }

            return string.Empty;
        }

        private static bool ExtractJsonBool(string body, params string[] propertyNames)
        {
            if (string.IsNullOrWhiteSpace(body) || propertyNames == null || propertyNames.Length == 0)
                return false;

            foreach (var prop in propertyNames)
            {
                if (string.IsNullOrWhiteSpace(prop)) continue;

                var pattern = $"\"{Regex.Escape(prop)}\"\\s*:\\s*(true|false)";
                var match = Regex.Match(body, pattern, RegexOptions.IgnoreCase);
                if (match.Success && match.Groups.Count > 1)
                {
                    return bool.TryParse(match.Groups[1].Value, out var result) && result;
                }
            }

            return false;
        }

        private static string MapExchangeError(HttpStatusCode status, string errorKey)
        {
            if (status == HttpStatusCode.BadRequest && errorKey == "invalid_or_expired")
                return "Code is invalid or expired. Generate a new code on Veilnet.";

            if (status == HttpStatusCode.Conflict && errorKey == "username_required")
                return "Set your Veilnet username on the website first, then try again.";

            if (status == HttpStatusCode.NotFound)
                return "Launcher exchange endpoint is not available yet.";

            if ((int)status >= 500)
                return "Veilnet service error. Please try again.";

            if (!string.IsNullOrWhiteSpace(errorKey))
                return errorKey;

            return $"Launcher exchange failed (HTTP {(int)status}).";
        }

        private static string MapMeError(HttpStatusCode status, string errorKey)
        {
            if (status == HttpStatusCode.Unauthorized && (errorKey == "invalid_launcher_token" || errorKey == "missing_launcher_token"))
                return "Saved Veilnet login has expired.";

            if (status == HttpStatusCode.Conflict && errorKey == "username_required")
                return "Your Veilnet account needs a username before linking.";

            if (!string.IsNullOrWhiteSpace(errorKey))
                return errorKey;

            return $"Launcher profile lookup failed (HTTP {(int)status}).";
        }

        private static string MapDevAccessError(HttpStatusCode status, string errorKey)
        {
            if (status == HttpStatusCode.Unauthorized)
                return "Saved Veilnet login has expired. Sign into Veilnet again.";

            if (status == HttpStatusCode.Forbidden)
                return "This Veilnet account is not allowed to receive DEV assets.";

            if (!string.IsNullOrWhiteSpace(errorKey))
                return errorKey;

            return $"DEV access check failed (HTTP {(int)status}).";
        }
    }
}
