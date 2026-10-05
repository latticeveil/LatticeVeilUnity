using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LatticeVeil.Core;

namespace LatticeVeil.Launcher
{
    internal sealed class OfficialBuildVerifier
    {
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        private readonly Logger _log;
        private readonly string _endpoint;
        private readonly string _apiKey;
        private readonly object _cacheSync = new object();
        private DateTime _cacheExpiresUtc = DateTime.MinValue;
        private OfficialHashesSnapshot? _cachePayload;

        internal enum VerifyFailure
        {
            None,
            MissingFile,
            InvalidChannel,
            ServiceUnavailable,
            Unauthorized,
            BadResponse,
            HashMismatch,
            ComputeFailed
        }

        internal struct VerifyResult
        {
            public bool Ok { get; set; }
            public VerifyFailure Failure { get; set; }
            public string Message { get; set; }
            public string Channel { get; set; }
            public string ExpectedHash { get; set; }
            public string ActualHash { get; set; }
        }

        private struct OfficialHashesSnapshot
        {
            public string DevHash { get; set; }
            public string ReleaseHash { get; set; }
        }

        public OfficialBuildVerifier(Logger log, string endpoint, string apiKey = "")
        {
            _log = log;
            _endpoint = (endpoint ?? string.Empty).Trim();
            _apiKey = (apiKey ?? string.Empty).Trim();
        }

        public async Task<VerifyResult> VerifyAsync(string channel, string filePath, CancellationToken ct = default)
        {
            channel = NormalizeChannel(channel);
            if (string.IsNullOrWhiteSpace(channel))
            {
                return new VerifyResult
                {
                    Ok = false,
                    Failure = VerifyFailure.InvalidChannel,
                    Message = "Invalid hash channel. Expected 'dev' or 'release'."
                };
            }

            filePath = (filePath ?? string.Empty).Trim();
            if (!File.Exists(filePath))
            {
                return new VerifyResult
                {
                    Ok = false,
                    Failure = VerifyFailure.MissingFile,
                    Channel = channel,
                    Message = $"Build verification file not found: {filePath}"
                };
            }

            string localHash;
            try
            {
                localHash = ComputeSha256(filePath);
            }
            catch (Exception ex)
            {
                return new VerifyResult
                {
                    Ok = false,
                    Failure = VerifyFailure.ComputeFailed,
                    Channel = channel,
                    Message = $"Failed to compute local SHA256: {ex.Message}"
                };
            }

            return await VerifyHashAsync(channel, localHash, ct).ConfigureAwait(false);
        }

        public async Task<VerifyResult> VerifyHashAsync(string channel, string localHash, CancellationToken ct = default)
        {
            channel = NormalizeChannel(channel);
            if (string.IsNullOrWhiteSpace(channel))
            {
                return new VerifyResult
                {
                    Ok = false,
                    Failure = VerifyFailure.InvalidChannel,
                    Message = "Invalid hash channel. Expected 'dev' or 'release'."
                };
            }

            localHash = NormalizeHash(localHash);
            if (!IsSha256(localHash))
            {
                return new VerifyResult
                {
                    Ok = false,
                    Failure = VerifyFailure.ComputeFailed,
                    Channel = channel,
                    Message = "Computed local SHA256 is invalid."
                };
            }

            var fetch = await FetchOfficialHashesAsync(channel, ct).ConfigureAwait(false);
            if (!fetch.Ok || fetch.Payload == null)
            {
                return new VerifyResult
                {
                    Ok = false,
                    Failure = fetch.Failure,
                    Channel = channel,
                    ActualHash = localHash,
                    Message = fetch.Message
                };
            }

            var targetHash = channel == "dev" ? fetch.Payload.Value.DevHash : fetch.Payload.Value.ReleaseHash;
            targetHash = NormalizeHash(targetHash);
            if (!IsSha256(targetHash))
            {
                return new VerifyResult
                {
                    Ok = false,
                    Failure = VerifyFailure.BadResponse,
                    Channel = channel,
                    ActualHash = localHash,
                    Message = $"Official {channel} hash is missing or invalid on server."
                };
            }

            if (!string.Equals(localHash, targetHash, StringComparison.OrdinalIgnoreCase))
            {
                return new VerifyResult
                {
                    Ok = false,
                    Failure = VerifyFailure.HashMismatch,
                    Channel = channel,
                    Message = "Build hash mismatch. Online play requires an official registered build.",
                    ExpectedHash = targetHash,
                    ActualHash = localHash
                };
            }

            return new VerifyResult
            {
                Ok = true,
                Failure = VerifyFailure.None,
                Channel = channel,
                Message = "Official hash verified.",
                ExpectedHash = targetHash,
                ActualHash = localHash
            };
        }

        private async Task<(bool Ok, VerifyFailure Failure, string Message, OfficialHashesSnapshot? Payload)> FetchOfficialHashesAsync(string channel, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_endpoint))
                return (false, VerifyFailure.BadResponse, "Official hash endpoint is not configured.", null);

            var now = DateTime.UtcNow;
            lock (_cacheSync)
            {
                if (_cachePayload.HasValue && now < _cacheExpiresUtc)
                    return (true, VerifyFailure.None, "cache", _cachePayload);
            }

            try
            {
                var target = NormalizeChannel(channel);
                if (string.IsNullOrWhiteSpace(target))
                    target = Paths.IsDevBuild ? "dev" : "release";

                var isRest = _endpoint.Contains("/rest/v1/", StringComparison.OrdinalIgnoreCase);
                var query = new List<string>();

                if (isRest)
                {
                    query.Add("is_active=eq.true");
                    query.Add("platform=eq.windows");
                    query.Add("select=hash,target,platform,channel,is_active,updated_at");
                    query.Add("order=updated_at.desc");
                }
                else
                {
                    query.Add("platform=windows");
                    query.Add($"target={target}");
                    query.Add($"channel={target}");
                }

                var separator = _endpoint.Contains("?") ? "&" : "?";
                var endpointWithParams = $"{_endpoint}{separator}{string.Join("&", query)}";

                using var request = new HttpRequestMessage(HttpMethod.Get, endpointWithParams);
                if (!string.IsNullOrWhiteSpace(_apiKey))
                {
                    request.Headers.Add("apikey", _apiKey);
                }

                using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var failure = response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized => VerifyFailure.Unauthorized,
                        HttpStatusCode.Forbidden => VerifyFailure.Unauthorized,
                        HttpStatusCode.ServiceUnavailable => VerifyFailure.ServiceUnavailable,
                        HttpStatusCode.BadGateway => VerifyFailure.ServiceUnavailable,
                        HttpStatusCode.GatewayTimeout => VerifyFailure.ServiceUnavailable,
                        _ => VerifyFailure.BadResponse
                    };
                    return (false, failure, $"Server returned HTTP {(int)response.StatusCode}", null);
                }

                var snapshot = ParseHashesSnapshot(body);
                lock (_cacheSync)
                {
                    _cachePayload = snapshot;
                    _cacheExpiresUtc = DateTime.UtcNow.AddMinutes(5);
                }

                return (true, VerifyFailure.None, "ok", snapshot);
            }
            catch (Exception ex)
            {
                return (false, VerifyFailure.ServiceUnavailable, $"Network error: {ex.Message}", null);
            }
        }

        private static OfficialHashesSnapshot ParseHashesSnapshot(string body)
        {
            var snapshot = new OfficialHashesSnapshot();
            if (string.IsNullOrWhiteSpace(body)) return snapshot;

            // Extract hash rows from JSON array or object
            // Matches objects containing "hash": "..." and "channel": "..." or "target": "..."
            var objectMatches = Regex.Matches(body, @"\{[^{}]*\}");
            foreach (Match match in objectMatches)
            {
                var row = match.Value;
                var hashMatch = Regex.Match(row, @"""hash""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                if (!hashMatch.Success) continue;
                var hash = hashMatch.Groups[1].Value.Trim();

                var channelMatch = Regex.Match(row, @"""(?:channel|target)""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                var ch = channelMatch.Success ? channelMatch.Groups[1].Value.Trim().ToLowerInvariant() : "";

                if (ch == "dev" && string.IsNullOrEmpty(snapshot.DevHash))
                    snapshot.DevHash = hash;
                else if ((ch == "release" || ch == "prod" || ch == "stable") && string.IsNullOrEmpty(snapshot.ReleaseHash))
                    snapshot.ReleaseHash = hash;
                else if (string.IsNullOrEmpty(snapshot.ReleaseHash) && string.IsNullOrEmpty(snapshot.DevHash))
                    snapshot.ReleaseHash = hash;
            }

            return snapshot;
        }

        public static string ComputeSha256(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = sha256.ComputeHash(stream);
            var sb = new StringBuilder(hashBytes.Length * 2);
            foreach (var b in hashBytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static string NormalizeChannel(string channel)
        {
            channel = (channel ?? string.Empty).Trim().ToLowerInvariant();
            if (channel == "dev" || channel == "development") return "dev";
            if (channel == "release" || channel == "prod" || channel == "stable") return "release";
            return channel;
        }

        private static string NormalizeHash(string hash)
        {
            return (hash ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static bool IsSha256(string hash)
        {
            return !string.IsNullOrWhiteSpace(hash) && hash.Length == 64 && hash.All(c => "0123456789abcdefABCDEF".Contains(c));
        }
    }
}
