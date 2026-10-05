using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LatticeVeil.Core
{
    /// <summary>
    /// Player profile data for LatticeVeil (profile.lvc).
    /// Manages client session states, offline fallbacks, skin index, and servers.
    /// </summary>
    public sealed class PlayerProfile
    {
        // [Account]
        public string VeilnetID { get; set; } = "";
        public string VeilnetUsername { get; set; } = "OFFLINE";

        // [OfflineFallback]
        public string OfflineUsername { get; set; } = "GuestPlayer";
        public string ActiveSkin { get; set; } = "default_skin";

        // [LocalPreferences]
        public bool RememberMe { get; set; } = true;
        public string LastLoginTimestamp { get; set; } = "";

        // [Skins]
        public Dictionary<string, string> Skins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // [Servers]
        public Dictionary<string, string> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Legacy / Friends compatibility
        public string PlayerId
        {
            get => VeilnetID;
            set => VeilnetID = value;
        }

        public string Username
        {
            get => VeilnetUsername;
            set => VeilnetUsername = value;
        }

        public List<FriendEntry> Friends { get; set; } = new();
        public List<string> ReceivedRequests { get; set; } = new();
        public List<string> SentRequests { get; set; } = new();

        public sealed class FriendEntry
        {
            public string Label { get; set; } = "";
            public string UserId { get; set; } = "";
            public string LastKnownDisplayName { get; set; } = "";
            public string LastKnownPresence { get; set; } = "";
            public string PictureUrl { get; set; } = "";
            public string BannerUrl { get; set; } = "";
            public string AboutMe { get; set; } = "";
        }

        public string GetDisplayUsername()
        {
            var veilnetName = (Environment.GetEnvironmentVariable("LV_VEILNET_USERNAME") ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(veilnetName))
                return veilnetName;

            if (!string.IsNullOrWhiteSpace(VeilnetUsername) && !string.Equals(VeilnetUsername, "OFFLINE", StringComparison.OrdinalIgnoreCase))
                return VeilnetUsername.Trim();

            if (!string.IsNullOrWhiteSpace(OfflineUsername) && !string.Equals(OfflineUsername, "GuestPlayer", StringComparison.OrdinalIgnoreCase))
                return OfflineUsername.Trim();

            var suffix = string.IsNullOrWhiteSpace(VeilnetID) ? "0000" : VeilnetID.Substring(0, Math.Min(4, VeilnetID.Length)).ToUpperInvariant();
            return $"PLAYER-{suffix}";
        }

        public static string SterilizeSkinKey(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "custom_skin";
            name = Path.GetFileNameWithoutExtension(name).Trim().ToLowerInvariant();
            var sb = new StringBuilder();
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                    sb.Append(c);
                else if (c == ' ' || c == '-' || c == '.')
                    sb.Append('_');
            }
            var res = sb.ToString().Trim('_');
            return string.IsNullOrEmpty(res) ? "custom_skin" : res;
        }

        public static PlayerProfile LoadOrCreate(Logger log)
        {
            try
            {
                Directory.CreateDirectory(Paths.RootDir);

                var profilePath = Paths.PlayerProfileJsonPath;
                if (!File.Exists(profilePath))
                {
                    // Check legacy player_profile.lvc first for automatic migration
                    if (File.Exists(Paths.LegacyPlayerProfileLvcPath))
                    {
                        var migrated = new PlayerProfile();
                        migrated.LoadFromLvcFile(Paths.LegacyPlayerProfileLvcPath);
                        migrated.EnsureDefaults();
                        migrated.Save(log);
                        try { File.Delete(Paths.LegacyPlayerProfileLvcPath); } catch { }
                        return migrated;
                    }

                    var p = new PlayerProfile();
                    p.EnsureDefaults();
                    p.Save(log);
                    return p;
                }

                var profile = new PlayerProfile();
                profile.LoadFromLvcFile(profilePath);
                profile.EnsureDefaults();
                return profile;
            }
            catch (Exception ex)
            {
                log?.Warn($"Failed to load player profile: {ex.Message}");
                var fallback = new PlayerProfile();
                fallback.EnsureDefaults();
                return fallback;
            }
        }

        public void Save(Logger log)
        {
            try
            {
                Directory.CreateDirectory(Paths.RootDir);
                var profilePath = Paths.PlayerProfileJsonPath;
                EnsureDefaults();

                var sb = new StringBuilder();
                sb.AppendLine("# LatticeVeil Player Profile (profile.lvc)");
                sb.AppendLine("# This file stores your local player identity, active skin, saved servers, and preferences.");
                sb.AppendLine("# Most values here are managed automatically. You can safely edit your offline name and server list.");
                sb.AppendLine();

                // [Account]
                sb.AppendLine("[Account]");
                sb.AppendLine("# Your Veilnet account information — do not edit this section manually.");
                sb.AppendLine("# This is filled in automatically when you log in. Editing it will not grant access.");
                sb.AppendLine($"VeilnetID=\"{VeilnetID ?? ""}\"");
                sb.AppendLine($"VeilnetUsername=\"{VeilnetUsername ?? "OFFLINE"}\"");
                sb.AppendLine();

                // [OfflineFallback]
                sb.AppendLine("[OfflineFallback]");
                sb.AppendLine("# Customizable offline identity used when playing without an active Veilnet login.");
                sb.AppendLine("# You can freely edit your OfflineUsername and ActiveSkin here.");
                sb.AppendLine($"OfflineUsername=\"{OfflineUsername ?? "GuestPlayer"}\"");
                sb.AppendLine($"ActiveSkin=\"{ActiveSkin ?? "default_skin"}\"");
                sb.AppendLine();

                // [LocalPreferences]
                sb.AppendLine("[LocalPreferences]");
                sb.AppendLine("# Launcher session preferences. Set RememberMe to true to stay logged in between sessions.");
                sb.AppendLine($"RememberMe={(RememberMe ? "true" : "false")}");
                var timestamp = string.IsNullOrWhiteSpace(LastLoginTimestamp) ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") : LastLoginTimestamp;
                sb.AppendLine($"LastLoginTimestamp=\"{timestamp}\"");
                sb.AppendLine();

                // [Skins]
                sb.AppendLine("[Skins]");
                sb.AppendLine("# Registered skin index mapping skin names to their local texture hashes in the \"Skins/\" folder.");
                sb.AppendLine("# Custom skins must be standard 64x64 PNG textures.");
                foreach (var kv in Skins.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"{kv.Key}=\"{kv.Value}\"");
                }
                sb.AppendLine();

                // [Servers]
                sb.AppendLine("[Servers]");
                sb.AppendLine("# Saved multiplayer server bookmarks mapped to IP:Port addresses.");
                sb.AppendLine("# You can add, edit, or remove your favorite server addresses here.");
                if (Servers.Count == 0)
                {
                    sb.AppendLine("My_Survival_Server=\"192.168.1.50:7777\"");
                    sb.AppendLine("Public_Lobby=\"play.latticeveil.net:7777\"");
                }
                else
                {
                    foreach (var kv in Servers.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"{kv.Key}=\"{kv.Value}\"");
                    }
                }

                File.WriteAllText(profilePath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                log?.Warn($"Failed to save player profile: {ex.Message}");
            }
        }

        private void LoadFromLvcFile(string path)
        {
            if (!File.Exists(path)) return;

            var lines = File.ReadAllLines(path);
            string currentSection = "";

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith(";"))
                    continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentSection = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                var eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var key = line.Substring(0, eq).Trim();
                var val = line.Substring(eq + 1).Trim().Trim('"');

                switch (currentSection.ToLowerInvariant())
                {
                    case "account":
                        if (string.Equals(key, "VeilnetID", StringComparison.OrdinalIgnoreCase)) VeilnetID = val;
                        else if (string.Equals(key, "VeilnetUsername", StringComparison.OrdinalIgnoreCase)) VeilnetUsername = val;
                        break;

                    case "offlinefallback":
                        if (string.Equals(key, "OfflineUsername", StringComparison.OrdinalIgnoreCase)) OfflineUsername = val;
                        else if (string.Equals(key, "ActiveSkin", StringComparison.OrdinalIgnoreCase)) ActiveSkin = val;
                        break;

                    case "localpreferences":
                        if (string.Equals(key, "RememberMe", StringComparison.OrdinalIgnoreCase))
                        {
                            if (bool.TryParse(val, out var b)) RememberMe = b;
                        }
                        else if (string.Equals(key, "LastLoginTimestamp", StringComparison.OrdinalIgnoreCase))
                        {
                            LastLoginTimestamp = val;
                        }
                        break;

                    case "skins":
                        if (!string.IsNullOrEmpty(key)) Skins[key] = val;
                        break;

                    case "servers":
                        if (!string.IsNullOrEmpty(key)) Servers[key] = val;
                        break;

                    default:
                        // Top level fallback
                        if (string.Equals(key, "VeilnetID", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "PlayerId", StringComparison.OrdinalIgnoreCase))
                            VeilnetID = val;
                        else if (string.Equals(key, "VeilnetUsername", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "Username", StringComparison.OrdinalIgnoreCase))
                            VeilnetUsername = val;
                        else if (string.Equals(key, "OfflineUsername", StringComparison.OrdinalIgnoreCase))
                            OfflineUsername = val;
                        else if (string.Equals(key, "ActiveSkin", StringComparison.OrdinalIgnoreCase))
                            ActiveSkin = val;
                        break;
                }
            }
        }

        private void EnsureDefaults()
        {
            if (string.IsNullOrWhiteSpace(VeilnetID))
                VeilnetID = Guid.NewGuid().ToString("N");

            if (string.IsNullOrWhiteSpace(VeilnetUsername))
                VeilnetUsername = "OFFLINE";

            if (string.IsNullOrWhiteSpace(OfflineUsername))
                OfflineUsername = "GuestPlayer";

            if (string.IsNullOrWhiteSpace(ActiveSkin))
                ActiveSkin = "default_skin";

            Skins ??= new(StringComparer.OrdinalIgnoreCase);
            if (!Skins.ContainsKey("default_skin"))
            {
                Skins["default_skin"] = "b5c6d7e8f9a0b5c6";
            }

            Servers ??= new(StringComparer.OrdinalIgnoreCase);
            if (Servers.Count == 0)
            {
                Servers["My_Survival_Server"] = "192.168.1.50:7777";
                Servers["Public_Lobby"] = "play.latticeveil.net:7777";
            }
        }
    }
}