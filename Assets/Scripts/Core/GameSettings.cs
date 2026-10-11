using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LatticeVeil.Core
{
    /// <summary>
    /// Game settings and player options for LatticeVeil (options.lvc).
    /// Categorized into Graphics, Audio, Launcher preferences, and customizable Keybinds.
    /// </summary>
    public sealed class GameSettings
    {
        public const int RenderDistanceMin = 4;
        public const int EngineRenderDistanceMax = 16;

        // [Graphics]
        public bool Fullscreen { get; set; } = false;
        public bool VSync { get; set; } = true;
        public int ResolutionWidth { get; set; } = 1280;
        public int ResolutionHeight { get; set; } = 720;
        public float GuiScale { get; set; } = 1.5f;
        public string QualityPreset { get; set; } = "MEDIUM";
        public float Brightness { get; set; } = 1.0f;
        public int FieldOfView { get; set; } = 70;
        public int RenderDistanceChunks { get; set; } = EngineRenderDistanceMax;
        public bool PerformanceDefaultsApplied { get; set; } = false;

        // [Audio]
        public float MasterVolume { get; set; } = 1.0f;
        public float MusicVolume { get; set; } = 1.0f;
        public float SfxVolume { get; set; } = 1.0f;

        // Per-channel audio (game owns these; all clamped 0..1).
        public float GameVolume { get; set; } = 1.0f;      // blocks/world/gameplay sounds
        public float AnimalsVolume { get; set; } = 1.0f;
        public float MonstersVolume { get; set; } = 1.0f;
        public float EnvironmentVolume { get; set; } = 1.0f; // wind, water, ambience
        public float PlayersVolume { get; set; } = 1.0f;   // other players' actions

        // Microphone / voice
        public string AudioInputDeviceId { get; set; } = "";   // "" = system default
        public string AudioOutputDeviceId { get; set; } = "";  // "" = system default
        public float MicVolume { get; set; } = 1.0f;
        public bool MicEnabled { get; set; } = false;
        public bool MicMonitor { get; set; } = false;          // hear yourself (sidetone)

        // [Launcher]
        public bool KeepLauncherOpen { get; set; } = false;
        public bool InstantQuitEnabled { get; set; } = false; // close launcher process entirely when the game starts
        public bool EmbedGameInLauncher { get; set; } = true; // run the game window inside the launcher window (Unity game versions)
        public bool AlwaysMinimizeLauncherToTray { get; set; } = false;
        public string LauncherCloseButtonAction { get; set; } = ""; // "", "Tray", or "Close"
        public bool DarkMode { get; set; } = true;
        public string RendererBackend { get; set; } = "OpenGL"; // "OpenGL" or "Vulkan"
        public int LauncherRenderDistance { get; set; } = 16;
        public bool AdvancedMode { get; set; } = false;
        public string OfficialBuildHashFilePath { get; set; } = "";

        /// <summary>
        /// Development-only: game exes registered by the user through the
        /// launcher's CUSTOM BUILDS picker (Version Manager). Persisted in the
        /// [Launcher] section; each entry launches through the normal launch
        /// path so the account handoff (LV_VEILNET_ACCESS_TOKEN) applies.
        /// </summary>
        public List<string> CustomBuildExes { get; set; } = new();
        public string IgnoredGameReleaseTitle { get; set; } = "";
        public bool AutoUpdateChecksEnabled { get; set; } = true;
        public bool AutoInstallUpdatesEnabled { get; set; } = false;
        public bool AutoTextureDownloadsEnabled { get; set; } = true;
        public bool LegacyCleanupNoticeDismissed { get; set; } = false;
        public bool BackgroundDeletedWorldCleanupEnabled { get; set; } = false;
        public bool AlwaysPermanentlyDeleteWorlds { get; set; } = false;

        // [Keybinds]
        public Dictionary<string, string> Keybinds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Packs & Mods
        public List<string> EnabledPacks { get; set; } = new();
        public List<string> EnabledMods { get; set; } = new();

        public static GameSettings LoadOrCreate(Logger log)
        {
            try
            {
                Directory.CreateDirectory(Paths.RootDir);
                var settingsPath = Paths.SettingsJsonPath;

                if (!File.Exists(settingsPath))
                {
                    // Check legacy paths if present
                    if (File.Exists(Paths.LegacySettingsLvcPath))
                    {
                        var s = new GameSettings();
                        s.LoadFromLvcFile(Paths.LegacySettingsLvcPath);
                        s.EnsureDefaults(log);
                        s.Save(log);
                        try { File.Delete(Paths.LegacySettingsLvcPath); } catch { }
                        return s;
                    }

                    var newSettings = new GameSettings();
                    newSettings.ApplyAutoPerformanceDefaults(log);
                    newSettings.EnsureDefaults(log);
                    newSettings.Save(log);
                    return newSettings;
                }

                var loaded = new GameSettings();
                loaded.LoadFromLvcFile(settingsPath);
                loaded.EnsureDefaults(log);
                if (!loaded.PerformanceDefaultsApplied)
                {
                    loaded.ApplyAutoPerformanceDefaults(log);
                    loaded.Save(log);
                }
                return loaded;
            }
            catch (Exception ex)
            {
                log?.Warn($"Failed to load settings: {ex.Message}");
                var fallback = new GameSettings();
                fallback.EnsureDefaults(log);
                return fallback;
            }
        }

        public void Save(Logger log)
        {
            try
            {
                Directory.CreateDirectory(Paths.RootDir);
                var settingsPath = Paths.SettingsJsonPath;
                EnsureDefaults(log);

                var sb = new StringBuilder();
                sb.AppendLine("# LatticeVeil Options File (options.lvc)");
                sb.AppendLine("# This file stores your in-game and launcher settings.");
                sb.AppendLine("# You can edit it manually, but most settings are easier to change through the in-game menus.");
                sb.AppendLine("# Values you edit here will be loaded the next time the launcher starts.");
                sb.AppendLine();

                // [Graphics]
                sb.AppendLine("[Graphics]");
                sb.AppendLine("# Controls how the game looks on your screen.");
                sb.AppendLine("# Higher quality and render distance will use more GPU resources.");
                sb.AppendLine($"Fullscreen={(Fullscreen ? "true" : "false")}");
                sb.AppendLine($"VSync={(VSync ? "true" : "false")}");
                sb.AppendLine($"ResolutionWidth={ResolutionWidth}");
                sb.AppendLine($"ResolutionHeight={ResolutionHeight}");
                sb.AppendLine($"GuiScale={GuiScale.ToString("0.0#", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"QualityPreset=\"{QualityPreset}\"");
                sb.AppendLine($"Brightness={Brightness.ToString("0.0#", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"FieldOfView={FieldOfView}");
                sb.AppendLine($"RenderDistanceChunks={RenderDistanceChunks}");
                sb.AppendLine($"PerformanceDefaultsApplied={(PerformanceDefaultsApplied ? "true" : "false")}");
                sb.AppendLine();

                // [Audio]
                sb.AppendLine("[Audio]");
                sb.AppendLine("# Volume settings for the game. Each value is between 0.0 (muted) and 1.0 (full volume).");
                sb.AppendLine($"MasterVolume={MasterVolume.ToString("0.0#", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"MusicVolume={MusicVolume.ToString("0.0#", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"SfxVolume={SfxVolume.ToString("0.0#", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"GameVolume={GameVolume.ToString("0.0#", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"AnimalsVolume={AnimalsVolume.ToString("0.0#", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"MonstersVolume={MonstersVolume.ToString("0.0#", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"EnvironmentVolume={EnvironmentVolume.ToString("0.0#", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"PlayersVolume={PlayersVolume.ToString("0.0#", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"AudioInputDeviceId=\"{AudioInputDeviceId}\"");
                sb.AppendLine($"AudioOutputDeviceId=\"{AudioOutputDeviceId}\"");
                sb.AppendLine($"MicVolume={MicVolume.ToString("0.0#", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"MicEnabled={(MicEnabled ? "true" : "false")}");
                sb.AppendLine($"MicMonitor={(MicMonitor ? "true" : "false")}");
                sb.AppendLine();

                // [Launcher]
                sb.AppendLine("[Launcher]");
                sb.AppendLine("# Controls how the launcher behaves, including update checks and texture syncing.");
                sb.AppendLine("# AutoUpdateChecksEnabled: If true, the launcher checks for game updates every time it opens.");
                sb.AppendLine("# AutoInstallUpdatesEnabled: If true, the launcher downloads and installs the latest game version automatically (no prompt).");
                sb.AppendLine("# AutoTextureDownloadsEnabled: If true, default textures are synced on launch. Disable this to keep any manually edited textures.");
                sb.AppendLine("# InstantQuitEnabled: If true, the launcher quits entirely the moment the game starts (no return to launcher).");
                sb.AppendLine($"KeepLauncherOpen={(KeepLauncherOpen ? "true" : "false")}");
                sb.AppendLine($"InstantQuitEnabled={(InstantQuitEnabled ? "true" : "false")}");
                sb.AppendLine($"EmbedGameInLauncher={(EmbedGameInLauncher ? "true" : "false")}");
                sb.AppendLine($"AlwaysMinimizeLauncherToTray={(AlwaysMinimizeLauncherToTray ? "true" : "false")}");
                sb.AppendLine($"LauncherCloseButtonAction=\"{LauncherCloseButtonAction}\"");
                sb.AppendLine($"DarkMode={(DarkMode ? "true" : "false")}");
                sb.AppendLine($"RendererBackend=\"{RendererBackend}\"");
                sb.AppendLine($"LauncherRenderDistance={LauncherRenderDistance}");
                sb.AppendLine($"AutoUpdateChecksEnabled={(AutoUpdateChecksEnabled ? "true" : "false")}");
                sb.AppendLine($"AutoInstallUpdatesEnabled={(AutoInstallUpdatesEnabled ? "true" : "false")}");
                sb.AppendLine($"AutoTextureDownloadsEnabled={(AutoTextureDownloadsEnabled ? "true" : "false")}");
                if (CustomBuildExes != null)
                {
                    foreach (var exe in CustomBuildExes)
                        sb.AppendLine($"CustomBuildExe=\"{exe}\"");
                }
                sb.AppendLine();

                // [Keybinds]
                sb.AppendLine("[Keybinds]");
                sb.AppendLine("# Your key mappings for in-game actions.");
                sb.AppendLine("# Format: ActionName=\"KeyName\"  (e.g. MoveForward=\"W\")");
                sb.AppendLine("# These can be remapped in-game through the controls menu. Hardware key IDs are supported.");
                foreach (var kv in Keybinds.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"{kv.Key}=\"{kv.Value}\"");
                }
                sb.AppendLine();

                // [Packs]
                sb.AppendLine("[Packs]");
                sb.AppendLine("# Mods & repacks enabled through Packs & Addons (IDs from each addon's manifest).");
                foreach (var id in EnabledPacks)
                    sb.AppendLine($"EnabledPack=\"{id}\"");
                foreach (var id in EnabledMods)
                    sb.AppendLine($"EnabledMod=\"{id}\"");

                File.WriteAllText(settingsPath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                log?.Warn($"Failed to save options: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves launcher-owned settings WITHOUT clobbering game-owned ones.
        /// The launcher keeps an in-memory copy from startup; meanwhile the game
        /// can write new Graphics/Audio values (fullscreen, resolution, ...) to
        /// the same options.lvc. A plain Save() here would resurrect the stale
        /// Graphics values on launcher exit. This re-reads the file, copies only
        /// the [Launcher] section fields over it, and writes that back.
        /// </summary>
        public void SaveLauncherScoped(Logger log)
        {
            try
            {
                var fresh = new GameSettings();
                if (File.Exists(Paths.SettingsJsonPath))
                    fresh.LoadFromLvcFile(Paths.SettingsJsonPath);

                // Launcher-owned fields only ([Launcher] section).
                fresh.KeepLauncherOpen = KeepLauncherOpen;
                fresh.InstantQuitEnabled = InstantQuitEnabled;
                fresh.EmbedGameInLauncher = EmbedGameInLauncher;
                fresh.AlwaysMinimizeLauncherToTray = AlwaysMinimizeLauncherToTray;
                fresh.LauncherCloseButtonAction = LauncherCloseButtonAction;
                fresh.DarkMode = DarkMode;
                fresh.RendererBackend = RendererBackend;
                fresh.LauncherRenderDistance = LauncherRenderDistance;
                fresh.AutoUpdateChecksEnabled = AutoUpdateChecksEnabled;
                fresh.AutoInstallUpdatesEnabled = AutoInstallUpdatesEnabled;
                fresh.AutoTextureDownloadsEnabled = AutoTextureDownloadsEnabled;
                fresh.CustomBuildExes = CustomBuildExes ?? new List<string>();

                fresh.Save(log);
            }
            catch (Exception ex)
            {
                log?.Warn($"Failed to save launcher settings: {ex.Message}");
            }
        }

        public void LoadFromLvcFile(string path)
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
                    case "graphics":
                        if (string.Equals(key, "Fullscreen", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out var fs)) Fullscreen = fs;
                        else if (string.Equals(key, "VSync", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out var vs)) VSync = vs;
                        else if (string.Equals(key, "ResolutionWidth", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var rw)) ResolutionWidth = rw;
                        else if (string.Equals(key, "ResolutionHeight", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var rh)) ResolutionHeight = rh;
                        else if (string.Equals(key, "GuiScale", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var gs)) GuiScale = gs;
                        else if (string.Equals(key, "QualityPreset", StringComparison.OrdinalIgnoreCase)) QualityPreset = val;
                        else if (string.Equals(key, "Brightness", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var br)) Brightness = br;
                        else if (string.Equals(key, "FieldOfView", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var fov)) FieldOfView = fov;
                        else if (string.Equals(key, "RenderDistanceChunks", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var rd)) RenderDistanceChunks = rd;
                        else if (string.Equals(key, "PerformanceDefaultsApplied", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out var pda)) PerformanceDefaultsApplied = pda;
                        break;

                    case "audio":
                        if (string.Equals(key, "MasterVolume", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var mv)) MasterVolume = mv;
                        else if (string.Equals(key, "MusicVolume", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var muv)) MusicVolume = muv;
                        else if (string.Equals(key, "SfxVolume", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var sv)) SfxVolume = sv;
                        else if (string.Equals(key, "GameVolume", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var gv)) GameVolume = gv;
                        else if (string.Equals(key, "AnimalsVolume", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var av)) AnimalsVolume = av;
                        else if (string.Equals(key, "MonstersVolume", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var mv2)) MonstersVolume = mv2;
                        else if (string.Equals(key, "EnvironmentVolume", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var ev)) EnvironmentVolume = ev;
                        else if (string.Equals(key, "PlayersVolume", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var pv)) PlayersVolume = pv;
                        else if (string.Equals(key, "AudioInputDeviceId", StringComparison.OrdinalIgnoreCase)) AudioInputDeviceId = val;
                        else if (string.Equals(key, "AudioOutputDeviceId", StringComparison.OrdinalIgnoreCase)) AudioOutputDeviceId = val;
                        else if (string.Equals(key, "MicVolume", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var mcv)) MicVolume = mcv;
                        else if (string.Equals(key, "MicEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out var me)) MicEnabled = me;
                        else if (string.Equals(key, "MicMonitor", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out var mm)) MicMonitor = mm;
                        break;

                    case "launcher":
                        if (string.Equals(key, "KeepLauncherOpen", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out var klo)) KeepLauncherOpen = klo;
                        else if (string.Equals(key, "InstantQuitEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out var iq)) InstantQuitEnabled = iq;
                        else if (string.Equals(key, "EmbedGameInLauncher", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out var egl)) EmbedGameInLauncher = egl;
                        else if (string.Equals(key, "AlwaysMinimizeLauncherToTray", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out var amt)) AlwaysMinimizeLauncherToTray = amt;
                        else if (string.Equals(key, "LauncherCloseButtonAction", StringComparison.OrdinalIgnoreCase)) LauncherCloseButtonAction = val;
                        else if (string.Equals(key, "DarkMode", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out var dm)) DarkMode = dm;
                        else if (string.Equals(key, "RendererBackend", StringComparison.OrdinalIgnoreCase)) RendererBackend = val;
                        else if (string.Equals(key, "LauncherRenderDistance", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var lrd)) LauncherRenderDistance = lrd;
                        else if (string.Equals(key, "AutoUpdateChecksEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out var auc)) AutoUpdateChecksEnabled = auc;
                        else if (string.Equals(key, "AutoInstallUpdatesEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out var aii)) AutoInstallUpdatesEnabled = aii;
                        else if (string.Equals(key, "AutoTextureDownloadsEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out var atd)) AutoTextureDownloadsEnabled = atd;
                        else if (string.Equals(key, "CustomBuildExe", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(val))
                            CustomBuildExes.Add(val);
                        break;

                    case "keybinds":
                        if (!string.IsNullOrEmpty(key)) Keybinds[key] = val;
                        break;

                    case "packs":
                        if (string.Equals(key, "EnabledPack", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(val))
                            EnabledPacks.Add(val.ToLowerInvariant());
                        else if (string.Equals(key, "EnabledMod", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(val))
                            EnabledMods.Add(val.ToLowerInvariant());
                        break;

                    default:
                        // General fallback for top-level keys
                        if (string.Equals(key, "KeepLauncherOpen", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out var klo2)) KeepLauncherOpen = klo2;
                        else if (string.Equals(key, "InstantQuitEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out var iq2)) InstantQuitEnabled = iq2;
                        else if (string.Equals(key, "DarkMode", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out var dm2)) DarkMode = dm2;
                        else if (string.Equals(key, "AutoUpdateChecksEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out var auc2)) AutoUpdateChecksEnabled = auc2;
                        else if (string.Equals(key, "AutoInstallUpdatesEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out var aii2)) AutoInstallUpdatesEnabled = aii2;
                        else if (string.Equals(key, "AutoTextureDownloadsEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out var atd2)) AutoTextureDownloadsEnabled = atd2;
                        break;
                }
            }

            Sanitize(this);
        }

        public void EnsureDefaults(Logger log = null)
        {
            Sanitize(this);

            Keybinds ??= new(StringComparer.OrdinalIgnoreCase);
            void SetDefaultKey(string action, string key)
            {
                if (!Keybinds.ContainsKey(action)) Keybinds[action] = key;
            }

            SetDefaultKey("MoveForward", "W");
            SetDefaultKey("MoveBackward", "S");
            SetDefaultKey("MoveLeft", "A");
            SetDefaultKey("MoveRight", "D");
            SetDefaultKey("Jump", "Space");
            SetDefaultKey("Sneak", "LeftShift");
            SetDefaultKey("Sprint", "LeftControl");
            SetDefaultKey("Inventory", "E");
            SetDefaultKey("DropItem", "Q");
            SetDefaultKey("Interact", "F");
            SetDefaultKey("Chat", "Return");
            SetDefaultKey("PlayerList", "Tab");
            SetDefaultKey("TogglePerspective", "F5");
            SetDefaultKey("ToggleHUD", "F1");
            SetDefaultKey("Screenshot", "F2");
            SetDefaultKey("PauseGame", "Escape");
            SetDefaultKey("Attack", "Mouse0");
            SetDefaultKey("UseItem", "Mouse1");
            SetDefaultKey("PickBlock", "Mouse2");
        }

        private static void Sanitize(GameSettings s)
        {
            if (s.ResolutionWidth < 640) s.ResolutionWidth = 640;
            if (s.ResolutionHeight < 480) s.ResolutionHeight = 480;

            s.MasterVolume = Math.Clamp(s.MasterVolume, 0f, 1f);
            s.MusicVolume = Math.Clamp(s.MusicVolume, 0f, 1f);
            s.SfxVolume = Math.Clamp(s.SfxVolume, 0f, 1f);
            s.GameVolume = Math.Clamp(s.GameVolume, 0f, 1f);
            s.AnimalsVolume = Math.Clamp(s.AnimalsVolume, 0f, 1f);
            s.MonstersVolume = Math.Clamp(s.MonstersVolume, 0f, 1f);
            s.EnvironmentVolume = Math.Clamp(s.EnvironmentVolume, 0f, 1f);
            s.PlayersVolume = Math.Clamp(s.PlayersVolume, 0f, 1f);
            s.MicVolume = Math.Clamp(s.MicVolume, 0f, 1f);
            s.AudioInputDeviceId = (s.AudioInputDeviceId ?? string.Empty).Trim();
            s.AudioOutputDeviceId = (s.AudioOutputDeviceId ?? string.Empty).Trim();
            s.GuiScale = NormalizeGuiScale(s.GuiScale);
            s.Brightness = Math.Clamp(s.Brightness, 0.5f, 1.5f);
            s.FieldOfView = Math.Clamp(s.FieldOfView, 60, 110);
            s.RenderDistanceChunks = Math.Clamp(s.RenderDistanceChunks, RenderDistanceMin, EngineRenderDistanceMax);
            s.QualityPreset = NormalizeQuality(s.QualityPreset);
            s.OfficialBuildHashFilePath = (s.OfficialBuildHashFilePath ?? string.Empty).Trim();
            s.IgnoredGameReleaseTitle = (s.IgnoredGameReleaseTitle ?? string.Empty).Trim();

            s.EnabledPacks ??= new List<string>();
            s.EnabledMods ??= new List<string>();
            s.CustomBuildExes ??= new List<string>();
        }

        private static float NormalizeGuiScale(float value)
        {
            // GUI scale range: SMALL=0.9, STANDARD=1.0, LARGER=1.25 (max).
            // Old saved values (e.g. 1.5, 2.0) clamp down into the range.
            return Math.Clamp(value, 0.9f, 1.25f);
        }

        private static string NormalizeQuality(string value)
        {
            var quality = string.IsNullOrWhiteSpace(value) ? "MEDIUM" : value.Trim().ToUpperInvariant();
            return quality is "LOW" or "MEDIUM" or "HIGH" or "ULTRA" ? quality : "MEDIUM";
        }

        public void ApplyAutoPerformanceDefaults(Logger log)
        {
            try
            {
                var recommendation = RecommendPerformanceProfile();
                RenderDistanceChunks = recommendation.RenderDistanceChunks;
                QualityPreset = recommendation.QualityPreset;
                PerformanceDefaultsApplied = true;
                log?.Info($"Auto performance defaults applied: renderDistance={RenderDistanceChunks}, quality={QualityPreset}, tier={recommendation.TierLabel}");
            }
            catch (Exception ex)
            {
                RenderDistanceChunks = 8;
                QualityPreset = "MEDIUM";
                PerformanceDefaultsApplied = true;
                log?.Warn($"Auto performance defaults failed; using fallback defaults: {ex.Message}");
            }
        }

        private static (int RenderDistanceChunks, string QualityPreset, string TierLabel) RecommendPerformanceProfile()
        {
            var score = 0;
            var cpuThreads = Environment.ProcessorCount;
            if (cpuThreads >= 12) score += 2;
            else if (cpuThreads >= 8) score += 1;
            else if (cpuThreads <= 4) score -= 1;

            var ramGb = 16;
            if (ramGb >= 24) score += 2;
            else if (ramGb >= 16) score += 1;
            else if (ramGb <= 8) score -= 1;

            if (score >= 6) return (EngineRenderDistanceMax, "ULTRA", "ultra");
            if (score >= 4) return (10, "HIGH", "high");
            if (score >= 1) return (8, "MEDIUM", "medium");
            return (6, "LOW", "low");
        }
    }
}