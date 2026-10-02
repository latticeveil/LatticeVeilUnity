using System;
using System.IO;

namespace LatticeVeil.Core
{
    /// <summary>
    /// Engine-independent path definitions for LatticeVeil.
    /// Mirrors the MonoGame Paths.cs structure for compatibility.
    /// </summary>
    public static class Paths
    {
        public const string ConfigExtension = ".lvc";
        public const string ListExtension = ".lvlist";
        public const string LogExtension = ".lvlog";
        public const string WorldMetaFileName = "world.lvc";
        public const string LegacyWorldMetaFileName = "world.json";
        public const string WorldConfigFileName = "world_config.lvc";
        public const string LegacyWorldConfigFileName = "world_config.json";

        /// <summary>
        /// Determines if this is a development build.
        /// For Unity, we'll use development build configuration.
        /// </summary>
        public static bool IsDevBuild
        {
            get
            {
#if UNITY_EDITOR
                return true;
#else
                // In production builds, this could be determined by build configuration
                // For now, we'll check if we're running in a development build
                return UnityEngine.Debug.isDebugBuild;
#endif
            }
        }

        public static string DocumentsDir =>
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        public static string RootDir
        {
            get
            {
                var overrideDir = Environment.GetEnvironmentVariable("LATTICEVEIL_ROOT");
                if (!string.IsNullOrWhiteSpace(overrideDir))
                    return overrideDir.Trim();

                return Path.Combine(DocumentsDir, "LatticeVeil");
            }
        }

        public static string AssetsDir =>
            Path.Combine(RootDir, "Assets");

        public static string PacksDir =>
            Path.Combine(RootDir, "Packs");

        public static string ModsDir =>
            Path.Combine(RootDir, "Mods");

        /// <summary>
        /// Local development assets directory for dev builds.
        /// In Unity, this would be the project's Assets folder during development.
        /// </summary>
        public static string LocalAssetsDir =>
            ResolveLocalAssetsDir();

        /// <summary>
        /// Returns the active runtime assets directory.
        /// In Development builds, uses LocalAssetsDir directly (no copy/sync).
        /// In Release builds, uses AssetsDir (%USERPROFILE%\Documents\LatticeVeil\Assets).
        /// </summary>
        public static string GetAssetsDir() => IsDevBuild ? LocalAssetsDir : AssetsDir;

        private static string ResolveLocalAssetsDir()
        {
            var overrideDir = Environment.GetEnvironmentVariable("LATTICEVEIL_LOCAL_ASSETS");
            if (!string.IsNullOrWhiteSpace(overrideDir))
                return overrideDir.Trim();

            // Development source folder: %USERPROFILE%\Documents\LatticeVeil_project\LatticeVeilMonoGame\Defaults\Assets
            return Path.Combine(DocumentsDir, "LatticeVeil_project", "LatticeVeilMonoGame", "Defaults", "Assets");
        }

        public static string TexturesDir =>
            Path.Combine(GetAssetsDir(), "textures");

        public static string MenuTexturesDir =>
            Path.Combine(GetAssetsDir(), "textures", "menu");

        public static string BlocksTexturesDir =>
            Path.Combine(GetAssetsDir(), "textures", "blocks");

        public static string ItemsTexturesDir =>
            Path.Combine(GetAssetsDir(), "textures", "items");

        public static string LowQualityBlocksTexturesDir =>
            Path.Combine(GetAssetsDir(), "textures", "blocks_low");

        public static string BlocksAtlasPath =>
            Path.Combine(TexturesDir, "blocks_cubenet_atlas.png");

        public static string LowQualityBlocksAtlasPath =>
            Path.Combine(TexturesDir, "blocks_cubenet_atlas_low.png");

        public static string LogsDir =>
            Path.Combine(RootDir, "logs");

        public static string ScreenshotsDir =>
            Path.Combine(RootDir, "Screenshots");

        public static string WorldsDir =>
            Path.Combine(RootDir, "Worlds");

        public static string DeletedWorldsDir =>
            Path.Combine(RootDir, "DeletedWorlds");

        public static string MultiplayerWorldsDir =>
            Path.Combine(AppStateDir, "_OnlineCache");

        public static string BackupsDir =>
            Path.Combine(RootDir, "Backups");

        public static string ActiveLogPath =>
            Path.Combine(LogsDir, "current.lvlog");

        public static string GamePidPath =>
            Path.Combine(RuntimeStateDir, "game.pid");

        public static string LegacyGamePidPath =>
            Path.Combine(RootDir, "game.pid");

        public static string SettingsJsonPath =>
            Path.Combine(RootDir, "options.lvc");

        public static string LegacySettingsLvcPath =>
            Path.Combine(RootDir, "settings.lvc");

        public static string LegacySettingsJsonPath =>
            Path.Combine(RootDir, "settings.json");

        public static string PlayerProfileJsonPath =>
            Path.Combine(RootDir, "profile.lvc");

        public static string LegacyPlayerProfileLvcPath =>
            Path.Combine(RootDir, "player_profile.lvc");

        public static string LegacyPlayerProfileJsonPath =>
            Path.Combine(RootDir, "player_profile.json");

        public static string ConfigDir =>
            RootDir;

        public static string RoamingAppDataDir =>
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        public static string LegacyLocalAppDataDir =>
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        public static string AppStateDir =>
            Path.Combine(RoamingAppDataDir, "LatticeVeil");

        /// <summary>
        /// Root directory where the launcher installs downloadable game versions.
        /// Resolution order:
        ///   1. env LATTICEVEIL_INSTALL_ROOT (set by the LatticeVeil installer;
        ///      the value the user picked during setup lives here)
        ///   2. %APPDATA%\LatticeVeil (portable default)
        /// Version folders live under <root>\Versions\<tag>\ so an uninstaller can
        /// remove individual versions by deleting their subfolder.
        /// </summary>
        public static string InstallRootDir
        {
            get
            {
                var overrideDir = Environment.GetEnvironmentVariable("LATTICEVEIL_INSTALL_ROOT");
                if (!string.IsNullOrWhiteSpace(overrideDir))
                    return overrideDir.Trim();
                return AppStateDir;
            }
        }

        /// <summary>Directory containing one subfolder per installed game version.</summary>
        public static string VersionsDir =>
            Path.Combine(InstallRootDir, "Versions");

        /// <summary>
        /// Resolves the game executable for an installed version folder.
        /// Returns null when the version is not installed. Never substitutes.
        /// </summary>
        public static string TryResolveInstalledVersionExe(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return null;

            var safeTag = tag.Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
                safeTag = safeTag.Replace(c, '_');

            var dir = Path.Combine(VersionsDir, safeTag);
            if (!Directory.Exists(dir))
                return null;

            foreach (var exeName in new[] { "LatticeVeilMonoGame.exe", "LatticeVeil.exe" })
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            return null;
        }

        public static string LegacyLocalAppStateDir =>
            Path.Combine(LegacyLocalAppDataDir, "LatticeVeil");

        public static string RuntimeStateDir =>
            Path.Combine(AppStateDir, "Runtime");

        public static string UserSkinsDir =>
            Path.Combine(RootDir, "Skins");

        public static string RuntimeSkinsDir =>
            Path.Combine(RuntimeStateDir, "skins");

        public static string ActiveSkinHashPath =>
            Path.Combine(RuntimeSkinsDir, "active.txt");

        public static string SkinBackupPath =>
            Path.Combine(RuntimeSkinsDir, "skin_backup.png");

        public static string SkinChangeSignalPath =>
            Path.Combine(RuntimeSkinsDir, "active.lvc");

        public static string RuntimeRemoteSkinsDir =>
            Path.Combine(RuntimeSkinsDir, "remote");

        public static string SocialSessionCacheDir =>
            Path.Combine(RuntimeStateDir, "SocialSession");

        public static string SystemStateDir =>
            Path.Combine(AppStateDir, "System");

        public static string FriendLabelsJsonPath =>
            Path.Combine(RootDir, "friend_labels.lvc");

        public static string LegacyFriendLabelsJsonPath =>
            Path.Combine(RootDir, "friend_labels.json");

        public static string VeilnetProfileCachePath =>
            Path.Combine(SocialSessionCacheDir, "veilnet_profile_session.lvc");

        public static string VeilnetAvatarCachePath =>
            Path.Combine(SocialSessionCacheDir, "veilnet_avatar_session.lvimg");

        public static string VeilnetBannerCachePath =>
            Path.Combine(SocialSessionCacheDir, "veilnet_banner_session.lvimg");

        public static string EosIdentityPath =>
            Path.Combine(SystemStateDir, "identity.lvc");

        public static string LegacyEosIdentityPath =>
            Path.Combine(ConfigDir, "eos.identity.json");

        public static string VeilnetLauncherAuthPath =>
            Path.Combine(SystemStateDir, "launcher_auth.lvc");

        public static string LegacyVeilnetAuthPath =>
            Path.Combine(AppStateDir, "veilnet_launcher_token.json");

        public static string GetWorldMetaPath(string worldPath) =>
            Path.Combine(worldPath, WorldMetaFileName);

        public static string GetLegacyWorldMetaPath(string worldPath) =>
            Path.Combine(worldPath, LegacyWorldMetaFileName);

        public static string ResolveWorldMetaPath(string worldPath)
        {
            var preferred = GetWorldMetaPath(worldPath);
            if (File.Exists(preferred))
                return preferred;

            var legacy = GetLegacyWorldMetaPath(worldPath);
            return File.Exists(legacy) ? legacy : preferred;
        }

        public static void EnsureAssetDirectoriesExist()
        {
            try
            {
                Directory.CreateDirectory(AssetsDir);
                Directory.CreateDirectory(PacksDir);
                Directory.CreateDirectory(ModsDir);

                Directory.CreateDirectory(TexturesDir);
                Directory.CreateDirectory(MenuTexturesDir);
                Directory.CreateDirectory(BlocksTexturesDir);
                Directory.CreateDirectory(ItemsTexturesDir);
                Directory.CreateDirectory(Path.Combine(GetAssetsDir(), "Models", "Blocks"));
                EnsurePacksReadme();

                WarnIfLegacyAssetFoldersExist(GetAssetsDir());
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"Failed to create asset directories: {ex.Message}");
            }
        }

        private static void EnsurePacksReadme()
        {
            try
            {
                var readmePath = Path.Combine(PacksDir, "README.txt");
                if (File.Exists(readmePath))
                    return;

                File.WriteAllText(
                    readmePath,
                    "Drop content packs into this folder.\r\n" +
                    "Folder: Documents/LatticeVeil/Packs\r\n" +
                    "Each pack should live in its own subfolder.\r\n");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"Failed to create packs README: {ex.Message}");
            }
        }

        private static bool HasExpectedAssetLayout(string assetsRoot)
        {
            var texturesDir = Path.Combine(assetsRoot, "textures");
            return Directory.Exists(texturesDir)
                && Directory.Exists(Path.Combine(texturesDir, "menu"))
                && Directory.Exists(Path.Combine(texturesDir, "blocks"));
        }

        private static void WarnIfLegacyAssetFoldersExist(string assetsRoot)
        {
            var found = FindLegacyAssetFolders(assetsRoot);

            if (found.Count > 0)
            {
                UnityEngine.Debug.LogWarning(
                    "Legacy asset folders detected in active asset root. " +
                    "The game reads textures\\menu and textures\\blocks. " +
                    $"Found: {string.Join(", ", found)}");
            }
        }

        private static System.Collections.Generic.List<string> FindLegacyAssetFolders(string assetsRoot)
        {
            var found = new System.Collections.Generic.List<string>();
            if (!Directory.Exists(assetsRoot))
                return found;

            foreach (var directory in Directory.EnumerateDirectories(assetsRoot, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(directory);
                if (name.Equals("menu", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("blocks", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(Path.GetRelativePath(assetsRoot, directory));
                }
            }

            var texturesRoot = Path.Combine(assetsRoot, "textures");
            if (!Directory.Exists(texturesRoot))
                return found;

            foreach (var directory in Directory.EnumerateDirectories(texturesRoot, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(directory);
                if ((name.Equals("menu", StringComparison.OrdinalIgnoreCase) && !name.Equals("menu", StringComparison.Ordinal)) ||
                    (name.Equals("blocks", StringComparison.OrdinalIgnoreCase) && !name.Equals("blocks", StringComparison.Ordinal)))
                {
                    found.Add(Path.GetRelativePath(assetsRoot, directory));
                }
            }

            return found;
        }

        /// <summary>
        /// Returns a UI-friendly version of a path. This does NOT change the actual filesystem path used.
        /// Pixel-font rendering may not support backslashes in some builds, so we display '/'.
        /// </summary>
        public static string ToUiPath(string path) => path.Replace('\\', '/');

        /// <summary>
        /// Some users accidentally extract to Documents\LatticeVeil\Assets\Assets\... .
        /// This stays within Documents\LatticeVeil but allows locating files in that nested structure.
        /// </summary>
        public static string ResolveAssetPath(string relativePath)
        {
            return Path.Combine(GetAssetsDir(), relativePath);
        }

        public static bool IsDisallowedAssetRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return false;

            var normalized = relativePath.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var first = parts.Length > 0 ? parts[0] : null;
            if (string.IsNullOrWhiteSpace(first))
                return false;

            var hasNestedPath = normalized.Contains('/');
            if (!hasNestedPath && normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                return true;

            return first.Equals("packs", StringComparison.OrdinalIgnoreCase)
                || first.Equals("data", StringComparison.OrdinalIgnoreCase)
                || first.Equals(".github", StringComparison.OrdinalIgnoreCase)
                || first.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || first.Equals(".gitignore", StringComparison.OrdinalIgnoreCase)
                || first.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase);
        }

        public static void RemoveDisallowedAssetEntries()
        {
            try
            {
                if (!Directory.Exists(AssetsDir))
                    return;

                foreach (var entry in Directory.GetFileSystemEntries(AssetsDir, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(entry);
                    if (!IsDisallowedAssetRelativePath(name))
                        continue;

                    try
                    {
                        if (Directory.Exists(entry))
                            Directory.Delete(entry, recursive: true);
                        else if (File.Exists(entry))
                            File.Delete(entry);

                        UnityEngine.Debug.Log($"Removed disallowed asset entry: {name}");
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"Failed removing disallowed asset entry '{name}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"Failed cleaning disallowed asset entries: {ex.Message}");
            }
        }
    }
}