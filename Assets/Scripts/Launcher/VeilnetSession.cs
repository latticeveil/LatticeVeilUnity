using System;
using System.IO;
using System.Text;
using LatticeVeil.Core;

namespace LatticeVeil.Launcher
{
    /// <summary>
    /// Reads the persisted Veilnet session (DPAPI-protected launcher_auth.lvc, with the
    /// legacy fallback) and resolves the Veilnet Edge Function settings, so floating
    /// panels can talk to the same Supabase skin endpoints as the main window.
    /// Mirrors the private logic in LauncherUI (TryReadVeilnetAuth /
    /// GetVeilnetFunctionsBaseUrl / GetSupabaseAnonKey) without touching it.
    /// </summary>
    internal static class VeilnetSession
    {
        private const string DefaultFunctionsBaseUrl = "https://lqghurvonrvrxfwjgkuu.supabase.co/functions/v1";
        private const string DefaultAnonKey = "sb_publishable_oy1En_XHnhp5AiOWruitmQ_sniWHETA";

        private static Logger _configLog; // created lazily; never truncates the active log

        public sealed class Auth
        {
            public string Username;
            public string Token;
            public string UserId;
        }

        /// <summary>Reads the persisted Veilnet session, or null when not logged in.</summary>
        public static Auth TryRead()
        {
            try
            {
                if (!File.Exists(Paths.VeilnetLauncherAuthPath))
                    return TryReadLegacy();

                var envelopeData = LvcSerializer.Read(Paths.VeilnetLauncherAuthPath);
                var envelope = new ProtectedVeilnetTokenEnvelope();
                LvcSerializer.ApplyObject(envelope, envelopeData);
                if (string.IsNullOrWhiteSpace(envelope.PayloadBase64))
                    return TryReadLegacy();

                var protectedBytes = Convert.FromBase64String(envelope.PayloadBase64);
                var bytes = DpapiHelper.Unprotect(protectedBytes);
                var json = Encoding.UTF8.GetString(bytes);

                var record = new VeilnetTokenRecord();
                LvcSerializer.ApplyObject(record, LvcSerializer.ReadFromString(json));

                if (string.IsNullOrWhiteSpace(record.Token))
                    return TryReadLegacy();

                if (!IsTokenFresh(record.Token))
                    return null; // expired; the main window owns re-login

                return new Auth
                {
                    Username = (record.Username ?? string.Empty).Trim(),
                    Token = record.Token.Trim(),
                    UserId = (record.UserId ?? string.Empty).Trim(),
                };
            }
            catch
            {
                return null;
            }
        }

        private static Auth TryReadLegacy()
        {
            try
            {
                if (!File.Exists(Paths.LegacyVeilnetAuthPath))
                    return null;

                var protectedBytes = File.ReadAllBytes(Paths.LegacyVeilnetAuthPath);
                var bytes = DpapiHelper.Unprotect(protectedBytes);
                var json = Encoding.UTF8.GetString(bytes);

                var usernameMatch = System.Text.RegularExpressions.Regex.Match(json, "\"Username\"\\s*:\\s*\"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var tokenMatch = System.Text.RegularExpressions.Regex.Match(json, "\"Token\"\\s*:\\s*\"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var userIdMatch = System.Text.RegularExpressions.Regex.Match(json, "\"UserId\"\\s*:\\s*\"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                var token = tokenMatch.Success ? tokenMatch.Groups[1].Value.Trim() : string.Empty;
                if (string.IsNullOrWhiteSpace(token) || !IsTokenFresh(token))
                    return null;

                return new Auth
                {
                    Username = usernameMatch.Success ? usernameMatch.Groups[1].Value.Trim() : string.Empty,
                    Token = token,
                    UserId = userIdMatch.Success ? userIdMatch.Groups[1].Value.Trim() : string.Empty,
                };
            }
            catch
            {
                return null;
            }
        }

        public static SupabaseSkinClient CreateClient(Auth auth)
            => new SupabaseSkinClient(FunctionsBaseUrl(), SupabaseAnonKey(), auth?.Token);

        public static string FunctionsBaseUrl()
        {
            var envUrl = Environment.GetEnvironmentVariable("LV_VEILNET_FUNCTIONS_URL");
            if (!string.IsNullOrWhiteSpace(envUrl))
                return envUrl;

            try
            {
                var config = LauncherRuntimeConfig.Load(ConfigLog());
                if (!string.IsNullOrWhiteSpace(config.VeilnetFunctionsBaseUrl))
                    return config.VeilnetFunctionsBaseUrl;
            }
            catch { }

            return DefaultFunctionsBaseUrl;
        }

        public static string SupabaseAnonKey()
        {
            var fromEnv = (Environment.GetEnvironmentVariable("LV_SUPABASE_ANON_KEY") ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return fromEnv;

            try
            {
                var config = LauncherRuntimeConfig.Load(ConfigLog());
                if (!string.IsNullOrWhiteSpace(config.SupabaseAnonKey))
                    return config.SupabaseAnonKey;
            }
            catch { }

            return DefaultAnonKey;
        }

        private static Logger ConfigLog()
            => _configLog ?? (_configLog = new Logger(null, truncateOnStart: false));

        private static bool IsTokenFresh(string token)
        {
            if (!TryGetJwtExpiryUtc(token, out var expUtc))
                return false;
            return expUtc > DateTime.UtcNow.AddSeconds(60);
        }

        private static bool TryGetJwtExpiryUtc(string token, out DateTime expiresUtc)
        {
            expiresUtc = DateTime.MinValue;
            try
            {
                var parts = (token ?? string.Empty).Split('.');
                if (parts.Length < 2)
                    return false;

                var s = parts[1].Trim().Replace('-', '+').Replace('_', '/');
                switch (s.Length % 4)
                {
                    case 2: s += "=="; break;
                    case 3: s += "="; break;
                }
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(s));
                var expMatch = System.Text.RegularExpressions.Regex.Match(json, "\"exp\"\\s*:\\s*(\\d+)");
                if (expMatch.Success && long.TryParse(expMatch.Groups[1].Value, out var expSeconds))
                {
                    expiresUtc = DateTimeOffset.FromUnixTimeSeconds(expSeconds).UtcDateTime;
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
