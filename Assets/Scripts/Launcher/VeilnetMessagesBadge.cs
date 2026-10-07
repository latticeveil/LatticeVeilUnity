using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LatticeVeil.Launcher
{
    /// <summary>
    /// Lightweight unread-messages poller for the launcher's MESSAGES button.
    /// POSTs messaging-api "bootstrap" every few seconds while the launcher is
    /// signed in and parses the conversation list with the same regex approach
    /// as VeilnetFriendsClient (no JSON dependency). Surfaces:
    ///   • total unread across every conversation
    ///   • the newest message (who + what) so arrivals can be announced live
    /// The WPF Messages app remains the realtime path; this is the
    /// launcher-side "you've got mail" surface that works even when the
    /// messages window is closed.
    /// </summary>
    internal sealed class VeilnetMessagesBadge
    {
        public sealed class Snapshot
        {
            public bool Ok;
            public string Error;
            public int TotalUnread;
            public string LastFrom = "";
            public string LastBody = "";
            public string LastAt = "";
            // Per-conversation latest activity, keyed by conversation id. The
            // launcher diffs consecutive snapshots: a conversation that
            // previously had activity but now reports last_at == null means
            // the peer (or the same account elsewhere) DELETED the chat.
            public readonly Dictionary<string, string> ConvLastAt = new Dictionary<string, string>(StringComparer.Ordinal);
            // friend display name per conversation (from the embedded other.username)
            public readonly Dictionary<string, string> ConvOtherName = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private static readonly Regex UnreadRx = new Regex("\"unread\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);
        private static readonly Regex ConvIdRx = new Regex("\"conversation_id\"\\s*:\\s*\"([a-f0-9-]{36})\"", RegexOptions.Compiled);
        private static readonly Regex LastAtRx = new Regex("\"last_at\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);
        private static readonly Regex LastBodyRx = new Regex("\"last_body\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
        private static readonly Regex UsernameRx = new Regex("\"username\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

        private readonly HttpClient _http;
        private readonly string _functionsBaseUrl;
        private readonly string _anonKey;
        private readonly string _userToken;

        public VeilnetMessagesBadge(string functionsBaseUrl, string anonKey, string userToken, HttpClient http = null)
        {
            _functionsBaseUrl = (functionsBaseUrl ?? string.Empty).Trim().TrimEnd('/');
            _anonKey = (anonKey ?? string.Empty).Trim();
            _userToken = (userToken ?? string.Empty).Trim();
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        }

        public async Task<Snapshot> PollAsync()
        {
            var snap = new Snapshot();
            if (string.IsNullOrEmpty(_userToken))
            {
                snap.Error = "not_signed_in";
                return snap;
            }

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, _functionsBaseUrl + "/messaging-api");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _userToken);
                req.Headers.Add("apikey", _anonKey);
                req.Content = new StringContent("{\"action\":\"bootstrap\"}", Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    snap.Error = "http " + (int)resp.StatusCode;
                    return snap;
                }

                // Split the conversations array into per-conversation segments
                // and pull unread / preview fields from each.
                var convStart = body.IndexOf("\"conversation_id\"", StringComparison.Ordinal);
                var lastAt = "";
                while (convStart >= 0)
                {
                    var next = body.IndexOf("\"conversation_id\"", convStart + 1, StringComparison.Ordinal);
                    var seg = next > convStart ? body.Substring(convStart, next - convStart) : body.Substring(convStart);

                    var u = UnreadRx.Match(seg);
                    if (u.Success)
                        snap.TotalUnread += int.Parse(u.Groups[1].Value);

                    // Track each conversation's last activity for delete
                    // detection: "last_at":"..." only appears when a message
                    // exists; after a delete it serializes as null.
                    var cidM = ConvIdRx.Match(seg);
                    if (cidM.Success)
                    {
                        var at2 = LastAtRx.Match(seg);
                        snap.ConvLastAt[cidM.Groups[1].Value] = at2.Success && !at2.Groups[1].Value.StartsWith("null") ? at2.Groups[1].Value : null;
                        var un = UsernameRx.Match(seg);
                        snap.ConvOtherName[cidM.Groups[1].Value] = un.Success ? UnescapeJson(un.Groups[1].Value) : "";
                    }

                    var at = LastAtRx.Match(seg);
                    var uname = UsernameRx.Match(seg);
                    var unameVal = uname.Success ? UnescapeJson(uname.Groups[1].Value) : "";
                    if (at.Success && string.CompareOrdinal(at.Groups[1].Value, lastAt) > 0)
                    {
                        lastAt = at.Groups[1].Value;
                        snap.LastAt = lastAt;
                        snap.LastFrom = unameVal;
                        var bd = LastBodyRx.Match(seg);
                        snap.LastBody = bd.Success ? UnescapeJson(bd.Groups[1].Value) : "";
                    }

                    convStart = next;
                }

                snap.Ok = true;
            }
            catch (Exception ex)
            {
                snap.Error = ex.Message;
            }
            return snap;
        }

        private static string UnescapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t")
                    .Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }
}
