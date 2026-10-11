using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace LatticeVeil.Game
{
    /// <summary>
    /// Online identity/profile/friends client — Unity port of the MonoGame
    /// VeilnetProfileClient + OnlineGateClient friends endpoints.
    ///
    ///   gate base : https://...supabase.co/functions/v1 (env LV_VEILNET_FUNCTIONS_URL)
    ///   profile   : GET  launcher-me
    ///   friends   : GET  friend-list
    ///   add       : POST friend-request  {username | targetId}
    ///   remove    : POST friend-remove   {productUserId}
    ///   respond   : POST friend-respond  {requesterProductUserId, accept, block}
    ///
    /// Auth: Bearer <veilnet access token> + apikey header (same as MonoGame).
    /// Profile/avatar images are fetched over UnityWebRequest with an on-disk
    /// cache under Documents/LatticeVeil/Cache/Avatars (24h freshness).
    /// </summary>
    public static class VeilnetClient
    {
        private const string DefaultGateUrl = "https://lqghurvonrvrxfwjgkuu.supabase.co/functions/v1";
        private const string DefaultAnonKey = "sb_publishable_oy1En_XHnhp5AiOWruitmQ_sniWHETA";

        public static string GateUrl =>
            (Environment.GetEnvironmentVariable("LV_VEILNET_FUNCTIONS_URL") ?? "").Trim().TrimEnd('/')
            is { Length: > 0 } env
            ? env
            : DefaultGateUrl;

        private static string AnonKey =>
            (Environment.GetEnvironmentVariable("LV_SUPABASE_ANON_KEY") ?? "").Trim() is { Length: > 0 } k
            ? k : DefaultAnonKey;

        /// <summary>Veilnet access token (set by the launcher on login).</summary>
        public static string AccessToken { get; set; } =
            (Environment.GetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN") ?? "").Trim();

        public static bool HasToken =>
            !string.IsNullOrWhiteSpace(AccessToken) &&
            !string.Equals(AccessToken, "null", StringComparison.OrdinalIgnoreCase);

        // ------------------------------------------------------------------
        // Data
        // ------------------------------------------------------------------

        [Serializable]
        public sealed class GateUser
        {
            public string ProductUserId = "";
            public string Username = "";
            public string DisplayName = "";
            public string FriendCode = "";
            public string PictureUrl = "";
            public string BannerUrl = "";
            public string AboutMe = "";
        }

        [Serializable]
        public sealed class GateFriendRequestEntry
        {
            public string ProductUserId = "";
            public string RequestedUtc = "";
            public GateUser User = new GateUser();
        }

        public sealed class FriendsResult
        {
            public bool Ok;
            public string Error;
            public List<GateUser> Friends = new List<GateUser>();
            public List<GateFriendRequestEntry> Incoming = new List<GateFriendRequestEntry>();
            public List<GateFriendRequestEntry> Outgoing = new List<GateFriendRequestEntry>();
        }

        public sealed class ProfileResult
        {
            public bool Ok;
            public string Error;
            public GateUser Profile = new GateUser();
            public string ThemeColor = "";
        }

        public sealed class MutationResult
        {
            public bool Ok;
            public string Error;
        }

        // ------------------------------------------------------------------
        // Profile
        // ------------------------------------------------------------------

        public static IEnumerator FetchProfile(ProfileResult into, Action onDone)
        {
            var url = $"{GateUrl}/launcher-me";
            var req = UnityWebRequest.Get(url);
            ApplyAuth(req);
            yield return req.SendWebRequest();
            ParseResult(into, req);
            if (!into.Ok)
            {
                onDone?.Invoke();
                yield break;
            }

            var json = req.downloadHandler.text;
            try
            {
                var parsed = ParseJson(json);
                into.Profile = MapUser(parsed, "profile", "user");
                into.ThemeColor = GetString(parsed, "themecolor", "themeColor", "theme_color", "theme");
            }
            catch (Exception ex)
            {
                into.Ok = false;
                into.Error = "Bad profile JSON: " + ex.Message;
            }
            onDone?.Invoke();
        }

        // ------------------------------------------------------------------
        // Friends
        // ------------------------------------------------------------------

        public static IEnumerator FetchFriends(FriendsResult into, Action onDone)
        {
            var req = UnityWebRequest.Get($"{GateUrl}/friend-list");
            ApplyAuth(req);
            yield return req.SendWebRequest();

            into.Ok = req.result == UnityWebRequest.Result.Success &&
                      req.responseCode >= 200 && req.responseCode < 300;
            if (!into.Ok)
            {
                into.Error = ExtractError(req, "Friends lookup failed.");
                if (req.responseCode == 401)
                    MarkHandoffInvalid();
                onDone?.Invoke();
                yield break;
            }

            try
            {
                var parsed = ParseJson(req.downloadHandler.text);
                if (GetBool(parsed, "ok") != true)
                {
                    into.Ok = false;
                    into.Error = GetString(parsed, "message", "error") ?? "Friends lookup failed.";
                    onDone?.Invoke();
                    yield break;
                }

                into.Friends = MapUserList(parsed, "friends");
                into.Incoming = MapRequestList(parsed, "incomingrequests", "incomingRequests", "incoming");
                into.Outgoing = MapRequestList(parsed, "outgoingrequests", "outgoingRequests", "outgoing");
            }
            catch (Exception ex)
            {
                into.Ok = false;
                into.Error = "Bad friends JSON: " + ex.Message;
            }
            onDone?.Invoke();
        }

        public static IEnumerator AddFriend(string usernameOrId, MutationResult into, Action onDone)
        {
            var body = Guid.TryParse(usernameOrId, out _)
                ? $"{{\"targetId\":\"{Esc(usernameOrId)}\"}}"
                : $"{{\"username\":\"{Esc(usernameOrId)}\"}}";
            yield return PostMutation($"{GateUrl}/friend-request", body, into, onDone);
        }

        public static IEnumerator RemoveFriend(string productUserId, MutationResult into, Action onDone)
        {
            var body = $"{{\"productUserId\":\"{Esc(productUserId)}\"}}";
            yield return PostMutation($"{GateUrl}/friend-remove", body, into, onDone);
        }

        public static IEnumerator RespondToFriendRequest(string requesterProductUserId, bool accept, bool block, MutationResult into, Action onDone)
        {
            var endpoint = accept ? $"{GateUrl}/friend-respond" : $"{GateUrl}/friend-respond";
            var body = accept
                ? $"{{\"requesterProductUserId\":\"{Esc(requesterProductUserId)}\",\"accept\":true}}"
                : $"{{\"requesterProductUserId\":\"{Esc(requesterProductUserId)}\",\"accept\":false,\"block\":{(block ? "true" : "false")}}}";
            yield return PostMutation(endpoint, body, into, onDone);
        }

        private static IEnumerator PostMutation(string url, string jsonBody, MutationResult into, Action onDone)
        {
            if (!HasToken)
            {
                into.Ok = false;
                into.Error = "Not signed in. Please login with Veilnet.";
                onDone?.Invoke();
                yield break;
            }

            var up = new UnityWebRequest(url, "POST");
            up.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
            up.downloadHandler = new DownloadHandlerBuffer();
            up.SetRequestHeader("Content-Type", "application/json");
            ApplyAuth(up);

            yield return up.SendWebRequest();
            into.Ok = up.result == UnityWebRequest.Result.Success &&
                      up.responseCode >= 200 && up.responseCode < 300;
            if (!into.Ok)
            {
                into.Error = ExtractError(up, "Request failed.");
                if (up.responseCode == 401)
                    MarkHandoffInvalid();
            }
            onDone?.Invoke();
        }

        // ------------------------------------------------------------------
        // Image download with disk cache (avatars / banners)
        // ------------------------------------------------------------------

        public static string CacheDir => Path.Combine(
            LatticeVeil.Core.Paths.RootDir, "Cache", "Images");

        /// <summary>Downloads (or loads from cache) an image by URL; returns a
        /// Texture2D via onDone (null on failure). Disk cache: 24h freshness.</summary>
        public static IEnumerator FetchImage(string url, Action<Texture2D> onDone)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                onDone?.Invoke(null);
                yield break;
            }

            // Cache key: sanitized URL hash.
            string key = Hash(url);
            string cachePath = Path.Combine(CacheDir, key + ".png");
            CacheDirStats.Invalidate(key);

            if (File.Exists(cachePath) &&
                (DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath)).TotalHours < 24)
            {
                var cached = LoadLocalTexture(cachePath);
                if (cached != null)
                {
                    onDone?.Invoke(cached);
                    yield break;
                }
            }

            var req = UnityWebRequestTexture.GetTexture(url, false);
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                var tex = DownloadHandlerTexture.GetContent(req);
                try
                {
                    Directory.CreateDirectory(CacheDir);
                    File.WriteAllBytes(cachePath, tex.EncodeToPNG());
                }
                catch { /* cache write best-effort */ }
                onDone?.Invoke(tex);
            }
            else
            {
                // Fall back to a stale cache copy if the network fails.
                if (File.Exists(cachePath))
                {
                    var stale = LoadLocalTexture(cachePath);
                    onDone?.Invoke(stale);
                    yield break;
                }
                onDone?.Invoke(null);
            }
        }

        private static Texture2D LoadLocalTexture(string path)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2);
                return tex.LoadImage(bytes) ? tex : null;
            }
            catch
            {
                return null;
            }
        }

        private static readonly Dictionary<string, Texture2D> _memoryCache =
            new Dictionary<string, Texture2D>();

        /// <summary>Simple in-memory dedupe of image URLs across UI rebuilds.</summary>
        public static bool TryGetCachedTexture(string url, out Texture2D tex)
        {
            if (_memoryCache.TryGetValue(url, out tex) && tex != null)
                return true;
            return false;
        }

        public static void RememberTexture(string url, Texture2D tex)
        {
            _memoryCache[url] = tex;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static void ApplyAuth(UnityWebRequest req)
        {
            if (!string.IsNullOrWhiteSpace(AnonKey))
                req.SetRequestHeader("apikey", AnonKey);
            if (HasToken)
            {
                req.SetRequestHeader("Authorization", "Bearer " + AccessToken);
                req.SetRequestHeader("x-veilnet-auth", AccessToken);
            }
        }

        private static void ParseResult(ProfileResult into, UnityWebRequest req)
        {
            into.Ok = req.result == UnityWebRequest.Result.Success &&
                      req.responseCode >= 200 && req.responseCode < 300;
            if (!into.Ok)
            {
                into.Error = ExtractError(req, "Profile fetch failed.");
                // A 401 from the gate means the handed-off token is expired
                // or revoked: treat the launcher handoff as untrusted once
                // and fall back to the signed-out placeholder.
                if (req.responseCode == 401)
                    MarkHandoffInvalid();
            }
        }

        /// <summary>Server-confirmed revocation/expiry mark for the launcher
        /// handoff. The launcher remains the only login surface; the game
        /// never re-authenticates on its own, it just stops claiming a live
        /// session after the gate has rejected the token.</summary>
        private static void MarkHandoffInvalid()
        {
            if (_handoffCheckedInvalid) return;
            _handoffCheckedInvalid = true;
            AccessToken = null;
            Debug.Log("[VeilnetClient] Gate rejected handoff token (401) — switching to signed-out.");
        }

        private static bool _handoffCheckedInvalid;

        private static string ExtractError(UnityWebRequest req, string fallback)
        {
            var body = req.downloadHandler != null ? req.downloadHandler.text : "";
            try
            {
                var parsed = ParseJson(body);
                var msg = GetString(parsed, "message", "error", "reason");
                return string.IsNullOrWhiteSpace(msg)
                    ? $"{fallback} (HTTP {req.responseCode})"
                    : msg;
            }
            catch
            {
                return $"{fallback} (HTTP {req.responseCode})";
            }
        }

        private static string Esc(string s) =>
            (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static string Hash(string s)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // Tiny JSON reader (no dependency): enough for flat profiles + friend lists.
        private static Dictionary<string, object> ParseJson(string json)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(json))
                return result;
            // Zero-dependency parse: use Unity's built-in JsonUtility for
            // objects with matching field names; arrays via MiniJSON-like scan.
            return MiniJson.Parse(json) as Dictionary<string, object>
                   ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        private static GateUser MapUser(Dictionary<string, object> root, params string[] keys)
        {
            var src = root;
            foreach (var k in keys)
            {
                if (src.TryGetValue(k, out var found) && found is Dictionary<string, object> d)
                {
                    src = d;
                    break;
                }
            }
            return new GateUser
            {
                ProductUserId = GetString(src, "productuserid", "productUserId", "id") ?? "",
                Username = GetString(src, "username", "displayName", "display_name") ?? "",
                DisplayName = GetString(src, "displayname", "displayName", "username") ?? "",
                FriendCode = GetString(src, "friendcode", "friendCode") ?? "",
                PictureUrl = GetString(src, "picture", "pictureUrl", "pictureurl") ?? "",
                BannerUrl = GetString(src, "banner", "bannerUrl", "bannerurl") ?? "",
                AboutMe = GetString(src, "aboutme", "aboutMe", "about") ?? "",
            };
        }

        private static List<GateUser> MapUserList(Dictionary<string, object> root, params string[] keys)
        {
            var list = new List<GateUser>();
            var raw = GetList(root, keys);
            if (raw == null) return list;
            foreach (var item in raw)
            {
                if (item is Dictionary<string, object> d)
                    list.Add(MapUser(d));
            }
            return list;
        }

        private static List<GateFriendRequestEntry> MapRequestList(Dictionary<string, object> root, params string[] keys)
        {
            var list = new List<GateFriendRequestEntry>();
            var raw = GetList(root, keys);
            if (raw == null) return list;
            foreach (var item in raw)
            {
                if (item is Dictionary<string, object> d)
                {
                    list.Add(new GateFriendRequestEntry
                    {
                        ProductUserId = GetString(d, "productuserid", "productUserId", "id") ?? "",
                        RequestedUtc = GetString(d, "requestedutc", "requestedUtc", "requested") ?? "",
                        User = d.TryGetValue("user", out var u) && u is Dictionary<string, object> ud
                            ? MapUser(ud)
                            : MapUser(d),
                    });
                }
            }
            return list;
        }

        private static List<object> GetList(Dictionary<string, object> root, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (root.TryGetValue(k, out var v) && v is List<object> l)
                    return l;
            }
            return null;
        }

        private static string GetString(Dictionary<string, object> src, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (src.TryGetValue(k, out var v) && v != null)
                {
                    var s = v.ToString();
                    if (!string.IsNullOrWhiteSpace(s))
                        return s;
                }
            }
            return null;
        }

        private static bool GetBool(Dictionary<string, object> src, string key)
        {
            return src.TryGetValue(key, out var v) && v is bool b ? b : false;
        }
    }

    /// <summary>Minimal dependency-free JSON parser for the gate responses.</summary>
    internal static class MiniJson
    {
        public static object Parse(string json)
        {
            int i = 0;
            return ParseValue(json, ref i);
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't') { i += 4; return true; }
            if (c == 'f') { i += 5; return false; }
            if (c == 'n') { i += 4; return null; }
            return ParseNumber(s, ref i);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var obj = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return obj; }
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                var key = ParseString(s, ref i);
                SkipWs(s, ref i);
                i++; // :
                obj[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; break; }
            }
            return obj;
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (i < s.Length)
            {
                list.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; break; }
            }
            return list;
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // "
            var sb = new StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                    char e = s[i];
                    sb.Append(e switch
                    {
                        'n' => '\n', 't' => '\t', 'r' => '\r', 'u' when i + 5 <= s.Length =>
                            (char)Convert.ToInt32(s.Substring(i + 1, 4), 16),
                        _ => e,
                    });
                    if (e == 'u') i += 4;
                    i++;
                }
                else sb.Append(s[i++]);
            }
            i++; // "
            return sb.ToString();
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || "_.-+eE".IndexOf(s[i]) >= 0)) i++;
            var txt = s.Substring(start, i - start);
            return double.TryParse(txt, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : txt;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }
    }

    /// <summary>Placeholder stats holder for cache bookkeeping (reserved).</summary>
    internal static class CacheDirStats
    {
        public static void Invalidate(string key) { }
    }
}
