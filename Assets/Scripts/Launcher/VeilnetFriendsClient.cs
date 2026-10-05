using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LatticeVeil.Launcher
{
    /// <summary>
    /// Launcher client for the Veilnet social endpoints: friends
    /// (friend-list / friend-request / friend-respond / friend-remove),
    /// presence (game-presence-query) and world invites
    /// (world-invites-me / world-invite-send / world-invite-respond /
    /// world-invite-revoke-host). All calls require a Veilnet launcher
    /// token; responses are parsed with the same lightweight regex
    /// approach as SupabaseSkinClient (no JSON dependency).
    /// </summary>
    internal sealed class VeilnetFriendsClient
    {
        private readonly HttpClient _http;
        private readonly string _functionsBaseUrl;
        private readonly string _anonKey;
        private readonly string _userToken;

        public VeilnetFriendsClient(string functionsBaseUrl, string anonKey, string userToken, HttpClient http = null)
        {
            _functionsBaseUrl = (functionsBaseUrl ?? string.Empty).Trim().TrimEnd('/');
            _anonKey = (anonKey ?? string.Empty).Trim();
            _userToken = (userToken ?? string.Empty).Trim();
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        }

        // ------------------------- data types -------------------------

        public sealed class FriendUser
        {
            public string Id;
            public string Username;
            public string FriendCode;
            public string PictureUrl;
        }

        public sealed class FriendListResult
        {
            public bool Ok;
            public string Error;
            public List<FriendUser> Friends = new List<FriendUser>();
            public List<FriendUser> IncomingRequests = new List<FriendUser>();
            public List<FriendUser> OutgoingRequests = new List<FriendUser>();
        }

        public sealed class PresenceEntry
        {
            public string UserId;
            public string Status;      // IN_WORLD | MENU | LAUNCHER
            public string WorldName;
            public string GameMode;
            public bool IsHosting;
            public bool IsInWorld;
            public bool IsMultiplayer;
        }

        public sealed class PresenceResult
        {
            public bool Ok;
            public string Error;
            public List<PresenceEntry> Entries = new List<PresenceEntry>();
        }

        public sealed class WorldInvite
        {
            public string SenderId;
            public string SenderName;
            public string SenderPictureUrl;
            public string WorldName;
            public string GameMode;
            public string Status;
        }

        public sealed class WorldInvitesResult
        {
            public bool Ok;
            public string Error;
            public List<WorldInvite> Incoming = new List<WorldInvite>();
            public List<WorldInvite> Outgoing = new List<WorldInvite>();
        }

        public sealed class ActionResult
        {
            public bool Ok;
            public string Status;
            public string Message;
            public string Error;
        }

        // ------------------------- API methods -------------------------

        private void AddHeaders(HttpRequestMessage req)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _userToken);
            if (!string.IsNullOrWhiteSpace(_anonKey))
                req.Headers.TryAddWithoutValidation("apikey", _anonKey);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        private async Task<string> SendAsync(string function, string method, string jsonBody, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), $"{_functionsBaseUrl}/{function}");
            AddHeaders(req);
            if (!string.IsNullOrEmpty(jsonBody))
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var err = ExtractFirst(body, "error");
                return string.IsNullOrEmpty(err) ? $"HTTP {(int)resp.StatusCode}" : err;
            }
            return body;
        }

        public async Task<FriendListResult> GetFriendListAsync(CancellationToken ct = default)
        {
            var result = new FriendListResult();
            try
            {
                var body = await SendAsync("friend-list", "GET", null, ct).ConfigureAwait(false);
                if (body == null) { result.Error = "Request failed"; return result; }
                if (!ExtractBool(body, "ok")) { result.Error = ExtractFirst(body, "error") ?? "Request failed"; return result; }

                foreach (var obj in ParseSection(body, "friends"))
                    result.Friends.Add(ParseUser(obj));
                foreach (var obj in ParseSection(body, "incomingRequests"))
                    result.IncomingRequests.Add(ParseUser(obj));
                foreach (var obj in ParseSection(body, "outgoingRequests"))
                    result.OutgoingRequests.Add(ParseUser(obj));
                result.Ok = true;
            }
            catch (Exception ex) { result.Error = ex.Message; }
            return result;
        }

        /// <summary>Adds a friend by username (or direct product-user id).</summary>
        public async Task<ActionResult> RequestFriendAsync(string usernameOrId, CancellationToken ct = default)
        {
            var input = (usernameOrId ?? string.Empty).Trim();
            var payload = input.Contains("-") && input.Length == 36
                ? $"{{\"targetId\":\"{EscapeJson(input)}\"}}"
                : $"{{\"username\":\"{EscapeJson(input)}\"}}";
            return await PostActionAsync("friend-request", payload, ct).ConfigureAwait(false);
        }

        /// <summary>Accepts (accept=true) or declines/blocks an incoming request.</summary>
        public async Task<ActionResult> RespondFriendAsync(string requesterId, bool accept, bool block = false, CancellationToken ct = default)
        {
            var payload = $"{{\"requesterProductUserId\":\"{EscapeJson(requesterId ?? string.Empty)}\",\"accept\":{(accept ? "true" : "false")},\"block\":{(block ? "true" : "false")}}}";
            return await PostActionAsync("friend-respond", payload, ct).ConfigureAwait(false);
        }

        public async Task<ActionResult> CancelFriendRequestAsync(string targetId, CancellationToken ct = default)
        {
            var payload = $"{{\"cancel\":true,\"targetProductUserId\":\"{EscapeJson(targetId ?? string.Empty)}\"}}";
            return await PostActionAsync("friend-respond", payload, ct).ConfigureAwait(false);
        }

        public async Task<ActionResult> RemoveFriendAsync(string targetId, CancellationToken ct = default)
        {
            var payload = $"{{\"targetId\":\"{EscapeJson(targetId ?? string.Empty)}\"}}";
            return await PostActionAsync("friend-remove", payload, ct).ConfigureAwait(false);
        }

        /// <summary>Live presence for friend ids. Absent ids are offline.</summary>
        public async Task<PresenceResult> QueryPresenceAsync(IList<string> friendIds, CancellationToken ct = default)
        {
            var result = new PresenceResult();
            try
            {
                var ids = new StringBuilder("[");
                for (int i = 0; i < friendIds.Count; i++)
                {
                    if (i > 0) ids.Append(',');
                    ids.Append('"').Append(EscapeJson(friendIds[i])).Append('"');
                }
                ids.Append(']');

                var body = await SendAsync("game-presence-query", "POST", $"{{\"friendIds\":{ids}}}", ct).ConfigureAwait(false);
                if (body == null) { result.Error = "Request failed"; return result; }
                if (!ExtractBool(body, "ok")) { result.Error = ExtractFirst(body, "error") ?? "Request failed"; return result; }

                foreach (var obj in ParseSection(body, "entries"))
                {
                    var entry = new PresenceEntry
                    {
                        UserId = ExtractFirst(obj, "productUserId"),
                        Status = (ExtractFirst(obj, "status") ?? string.Empty).ToUpperInvariant(),
                        WorldName = ExtractFirst(obj, "worldName"),
                        GameMode = ExtractFirst(obj, "gameMode"),
                        IsHosting = ExtractBool(obj, "isHosting"),
                        IsInWorld = ExtractBool(obj, "isInWorld"),
                        IsMultiplayer = ExtractBool(obj, "isMultiplayer"),
                    };
                    result.Entries.Add(entry);
                }
                result.Ok = true;
            }
            catch (Exception ex) { result.Error = ex.Message; }
            return result;
        }

        public async Task<WorldInvitesResult> GetWorldInvitesAsync(CancellationToken ct = default)
        {
            var result = new WorldInvitesResult();
            try
            {
                var body = await SendAsync("world-invites-me", "GET", null, ct).ConfigureAwait(false);
                if (body == null) { result.Error = "Request failed"; return result; }
                if (!ExtractBool(body, "ok")) { result.Error = ExtractFirst(body, "error") ?? "Request failed"; return result; }

                foreach (var obj in ParseSection(body, "incoming"))
                    result.Incoming.Add(ParseInvite(obj));
                foreach (var obj in ParseSection(body, "outgoing"))
                    result.Outgoing.Add(ParseInvite(obj));
                result.Ok = true;
            }
            catch (Exception ex) { result.Error = ex.Message; }
            return result;
        }

        public async Task<ActionResult> SendWorldInviteAsync(string targetId, CancellationToken ct = default)
        {
            var payload = $"{{\"targetProductUserId\":\"{EscapeJson(targetId ?? string.Empty)}\"}}";
            return await PostActionAsync("world-invite-send", payload, ct).ConfigureAwait(false);
        }

        public async Task<ActionResult> RespondWorldInviteAsync(string senderId, bool accepted, CancellationToken ct = default)
        {
            var payload = $"{{\"senderProductUserId\":\"{EscapeJson(senderId ?? string.Empty)}\",\"response\":\"{(accepted ? "accepted" : "declined")}\"}}";
            return await PostActionAsync("world-invite-respond", payload, ct).ConfigureAwait(false);
        }

        /// <summary>Revokes ALL world invites the local player has sent (host semantics).</summary>
        public async Task<ActionResult> RevokeWorldInvitesAsync(CancellationToken ct = default)
        {
            return await PostActionAsync("world-invite-revoke-host", "{}", ct).ConfigureAwait(false);
        }

        private async Task<ActionResult> PostActionAsync(string function, string payload, CancellationToken ct)
        {
            var result = new ActionResult();
            try
            {
                var body = await SendAsync(function, "POST", payload, ct).ConfigureAwait(false);
                if (body == null) { result.Error = "Request failed"; return result; }
                result.Ok = ExtractBool(body, "ok");
                result.Status = ExtractFirst(body, "status");
                result.Message = ExtractFirst(body, "message");
                if (!result.Ok)
                    result.Error = ExtractFirst(body, "error") ?? ExtractFirst(body, "message") ?? "Request failed";
            }
            catch (Exception ex) { result.Error = ex.Message; }
            return result;
        }

        // ------------------------- JSON helpers -------------------------

        private static string EscapeJson(string value)
            => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

        /// <summary>Extracts the raw text of the JSON array bound to "key" (first occurrence).</summary>
        private static string ExtractArrayText(string body, string key)
        {
            if (string.IsNullOrEmpty(body) || string.IsNullOrEmpty(key)) return null;
            var match = Regex.Match(body, $"\\\"{Regex.Escape(key)}\\\"\\s*:\\s*\\[");
            if (!match.Success) return null;
            var start = match.Index + match.Length; // one past '['
            var depth = 1;
            var inString = false;
            for (var i = start; i < body.Length; i++)
            {
                var c = body[i];
                if (inString)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                        return body.Substring(start, i - start);
                }
            }
            return null;
        }

        /// <summary>Parses every innermost {...} object of the array bound to "key".</summary>
        private static List<string> ParseSection(string body, string key)
        {
            var results = new List<string>();
            var arrayText = ExtractArrayText(body, key);
            if (string.IsNullOrEmpty(arrayText)) return results;
            foreach (Match m in Regex.Matches(arrayText, "\\{[^{}]*\\}"))
                results.Add(m.Value);
            return results;
        }

        /// <summary>First string value among the candidate keys, JSON-unescaped.</summary>
        private static string ExtractFirst(string obj, params string[] keys)
        {
            if (string.IsNullOrEmpty(obj)) return null;
            foreach (var key in keys)
            {
                var m = Regex.Match(obj, $"\\\"{Regex.Escape(key)}\\\"\\s*:\\s*\\\"(?<v>(?:[^\"\\\\]|\\\\.)*)\\\"");
                if (m.Success)
                {
                    var v = m.Groups["v"].Value;
                    return v.Replace("\\\\\"", "\"").Replace("\\\\\\\\", "\\\\");
                }
            }
            return null;
        }

        private static bool ExtractBool(string obj, string key)
        {
            if (string.IsNullOrEmpty(obj)) return false;
            return Regex.IsMatch(obj, $"\\\"{Regex.Escape(key)}\\\"\\s*:\\s*true", RegexOptions.IgnoreCase);
        }

        private static FriendUser ParseUser(string obj)
        {
            return new FriendUser
            {
                Id = ExtractFirst(obj, "productUserId") ?? string.Empty,
                Username = ExtractFirst(obj, "username", "displayName") ?? string.Empty,
                FriendCode = ExtractFirst(obj, "friendCode") ?? string.Empty,
                PictureUrl = ExtractFirst(obj, "pictureUrl") ?? string.Empty,
            };
        }

        private static WorldInvite ParseInvite(string obj)
        {
            return new WorldInvite
            {
                SenderId = ExtractFirst(obj, "senderProductUserId") ?? string.Empty,
                SenderName = ExtractFirst(obj, "senderDisplayName") ?? "PLAYER",
                SenderPictureUrl = ExtractFirst(obj, "senderPictureUrl") ?? string.Empty,
                WorldName = ExtractFirst(obj, "worldName") ?? "WORLD",
                GameMode = ExtractFirst(obj, "gameMode") ?? string.Empty,
                Status = ExtractFirst(obj, "status") ?? "pending",
            };
        }
    }
}
