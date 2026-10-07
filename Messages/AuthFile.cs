using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LatticeVeil.Messages
{
    /// <summary>
    /// Reads the launcher's DPAPI-protected Veilnet session
    /// (%APPDATA%\LatticeVeil\System\launcher_auth.lvc) using the same
    ///CurrentUser scope the launcher writes it with. Nothing is displayed
    /// or persisted elsewhere; the token stays in process memory.
    /// </summary>
    public static class AuthFile
    {
        public class Record
        {
            public string Username;
            public string Token;
            public string UserId;
        }

        public static Record Read()
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LatticeVeil", "System", "launcher_auth.lvc");
            if (!File.Exists(path)) return null;

            var raw = File.ReadAllText(path);
            var m = Regex.Match(raw, @"PayloadBase64\s*[=:]\s*([A-Za-z0-9+/=]+)");
            if (!m.Success) return null;

            byte[] protectedBytes;
            try { protectedBytes = Convert.FromBase64String(m.Groups[1].Value); }
            catch { return null; }

            byte[] plain;
            try
            {
                plain = System.Security.Cryptography.ProtectedData.Unprotect(
                    protectedBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            }
            catch { return null; }

            var text = Encoding.UTF8.GetString(plain);

            string Grab(string key)
            {
                var mm = Regex.Match(text, key + @"\s*[=:]\s*([^\r\n"",&]+)");
                return mm.Success ? mm.Groups[1].Value.Trim() : "";
            }

            var rec = new Record
            {
                Token = Grab("Token"),
                UserId = Grab("UserId"),
                Username = Grab("Username"),
            };

            if (string.IsNullOrEmpty(rec.Token))
            {
                // Fallback: some serializer versions write JSON.
                try
                {
                    var i = text.IndexOf('{');
                    if (i >= 0)
                    {
                        var j = ParseLooseJson(text.Substring(i));
                        rec.Token = j.TryGetValue("Token", out var t) ? t : rec.Token;
                        rec.UserId = j.TryGetValue("UserId", out var u) ? u : rec.UserId;
                        rec.Username = j.TryGetValue("Username", out var n) ? n : rec.Username;
                    }
                }
                catch { }
            }

            return string.IsNullOrEmpty(rec.Token) ? null : rec;
        }

        private static System.Collections.Generic.Dictionary<string, string> ParseLooseJson(string json)
        {
            var dict = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var rx = new Regex("\"(\\w+)\"\\s*:\\s*\"([^\"]*)\"");
            foreach (Match mm in rx.Matches(json))
                dict[mm.Groups[1].Value] = mm.Groups[2].Value;
            return dict;
        }
    }
}
