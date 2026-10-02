using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace LatticeVeil.Installer
{
    /// <summary>
    /// Core operations for the LatticeVeil installer. Owns the single contract the
    /// rest of the ecosystem relies on: the LATTICEVEIL_INSTALL_ROOT environment
    /// variable (user scope), with game versions under <root>\Versions\<tag>\.
    /// Uninstalling a version = deleting its subfolder.
    /// </summary>
    public sealed class InstallService
    {
        public const string EnvVarName = "LATTICEVEIL_INSTALL_ROOT";
        public static string DefaultRoot =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LatticeVeil");

        public string RootDir { get; private set; }

        /// <summary>Overrides the install root (used by the GUI when the user picks a folder).</summary>
        public void SetRoot(string rootDir)
        {
            if (!string.IsNullOrWhiteSpace(rootDir))
                RootDir = Path.GetFullPath(rootDir);
        }
        public string VersionsDir => Path.Combine(RootDir, "Versions");

        public InstallService(string rootDir = null)
        {
            RootDir = string.IsNullOrWhiteSpace(rootDir)
                ? (ReadPersistedRoot() ?? DefaultRoot)
                : Path.GetFullPath(rootDir);
        }

        /// <summary>Root dir as currently persisted in the user environment (or null).</summary>
        public static string ReadPersistedRoot()
        {
            var v = Environment.GetEnvironmentVariable(EnvVarName, EnvironmentVariableTarget.User);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        /// <summary>Creates the folder structure and persists LATTICEVEIL_INSTALL_ROOT (user scope).</summary>
        public void Apply()
        {
            Directory.CreateDirectory(VersionsDir);
            Environment.SetEnvironmentVariable(EnvVarName, RootDir, EnvironmentVariableTarget.User);
            BroadcastEnvironmentChange();
        }

        /// <summary>Removes the persisted variable (portable mode / full uninstall).</summary>
        public void ClearPersistedRoot()
        {
            Environment.SetEnvironmentVariable(EnvVarName, null, EnvironmentVariableTarget.User);
            BroadcastEnvironmentChange();
        }

        /// <summary>Copies a launcher build folder into <root>\Launcher\.</summary>
        public string InstallLauncher(string launcherBuildFolder)
        {
            if (string.IsNullOrWhiteSpace(launcherBuildFolder) || !Directory.Exists(launcherBuildFolder))
                throw new DirectoryNotFoundException("Launcher build folder not found: " + launcherBuildFolder);

            var hasExe = Directory.GetFiles(launcherBuildFolder, "*.exe").Length > 0;
            if (!hasExe)
                throw new InvalidDataException("Selected folder contains no .exe (not a launcher build).");

            var dest = Path.Combine(RootDir, "Launcher");
            if (Directory.Exists(dest))
                Directory.Delete(dest, recursive: true);

            CopyDirectory(launcherBuildFolder, dest);
            return dest;
        }

        public sealed class InstalledVersion
        {
            public string Tag { get; set; }
            public string ExePath { get; set; }
            public long SizeBytes { get; set; }
            public DateTime InstalledAt { get; set; }
        }

        /// <summary>Enumerates installed game versions under <root>\Versions\.</summary>
        public List<InstalledVersion> GetInstalledVersions()
        {
            var list = new List<InstalledVersion>();
            if (!Directory.Exists(VersionsDir))
                return list;

            foreach (var dir in Directory.GetDirectories(VersionsDir))
            {
                var exe = new[] { "LatticeVeilMonoGame.exe", "LatticeVeil.exe" }
                    .Select(n => Path.Combine(dir, n))
                    .FirstOrDefault(File.Exists);
                if (exe == null) continue;

                long size = 0;
                try
                {
                    size = new DirectoryInfo(dir)
                        .EnumerateFiles("*", SearchOption.AllDirectories)
                        .Sum(f => f.Length);
                }
                catch { }

                list.Add(new InstalledVersion
                {
                    Tag = Path.GetFileName(dir),
                    ExePath = exe,
                    SizeBytes = size,
                    InstalledAt = Directory.GetCreationTime(dir)
                });
            }
            return list.OrderBy(v => v.Tag, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Uninstalls one game version by deleting its folder. Returns false if not found.</summary>
        public bool UninstallVersion(string tag)
        {
            var safe = (tag ?? "").Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
                safe = safe.Replace(c, '_');
            var dir = Path.Combine(VersionsDir, safe);

            if (!Directory.Exists(dir)) return false;
            Directory.Delete(dir, recursive: true);
            return true;
        }

        private static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.GetFiles(src))
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
            foreach (var sub in Directory.GetDirectories(src))
                CopyDirectory(sub, Path.Combine(dst, Path.GetFileName(sub)));
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
            uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

        /// <summary>Notifies running processes that the environment changed (no reboot needed).</summary>
        private static void BroadcastEnvironmentChange()
        {
            try
            {
                SendMessageTimeout(
                    (IntPtr)0xFFFF /*HWND_BROADCAST*/, 0x001A /*WM_SETTINGCHANGE*/, UIntPtr.Zero,
                    "Environment", 2 /*SMTO_ABORTIFHUNG*/, 5000, out _);
            }
            catch { /* best effort */ }
        }
    }
}
