using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LatticeVeil.Launcher
{
    /// <summary>
    /// Downloads a game-version zip from its GitHub release and installs it under
    /// Paths.VersionsDir\<tag>\. The install is atomic (temp dir + move) so a failed
    /// download never leaves a half-installed version behind, and uninstall is a
    /// plain folder delete for the future installer.
    /// </summary>
    internal sealed class LegacyVersionInstaller
    {
        private readonly HttpClient _http;
        private readonly Core.Logger _log;

        public LegacyVersionInstaller(HttpClient http, Core.Logger log)
        {
            _http = http;
            _log = log;
        }

        /// <summary>
        /// Downloads and installs the version. progress reports 0..1.
        /// Returns the installed exe path.
        /// </summary>
        public async Task<string> DownloadAndInstallAsync(
            GameVersionInfo version,
            IProgress<double> progress,
            CancellationToken ct = default)
        {
            if (version == null || string.IsNullOrWhiteSpace(version.AssetUrl) || string.IsNullOrWhiteSpace(version.Tag))
                throw new ArgumentException("Invalid version to download.");

            var existing = Core.Paths.TryResolveInstalledVersionExe(version.Tag);
            if (existing != null)
            {
                _log?.Info($"Version {version.Tag} already installed at {existing}");
                return existing;
            }

            Directory.CreateDirectory(Core.Paths.VersionsDir);
            var stagingDir = Path.Combine(Path.GetTempPath(), "LatticeVeil-install-" + Guid.NewGuid().ToString("N"));
            var zipPath = Path.Combine(stagingDir, version.AssetName);
            var extractDir = Path.Combine(stagingDir, "extracted");
            var finalDir = Path.Combine(Core.Paths.VersionsDir, MakeSafeFolderName(version.Tag));

            try
            {
                Directory.CreateDirectory(stagingDir);

                // ---- Download with progress ----
                _log?.Info($"Downloading {version.AssetName} ({version.SizeDisplay})...");
                using (var response = await _http.GetAsync(version.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? version.AssetSizeBytes;

                    using var httpStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                    var buffer = new byte[1024 * 128];
                    long readTotal = 0;
                    int read;
                    var lastReport = 0.0;
                    while ((read = await httpStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                        readTotal += read;
                        if (total > 0 && progress != null)
                        {
                            var frac = (double)readTotal / total;
                            if (frac - lastReport >= 0.01) // report every ~1%
                            {
                                lastReport = frac;
                                progress.Report(frac * 0.9); // download = 0-90%
                            }
                        }
                    }
                }

                // ---- Size sanity check ----
                var actualSize = new FileInfo(zipPath).Length;
                if (version.AssetSizeBytes > 0 && Math.Abs(actualSize - version.AssetSizeBytes) > 1024 * 1024)
                    throw new InvalidDataException($"Downloaded size mismatch: expected {version.AssetSizeBytes}, got {actualSize}.");

                // ---- Extract ----
                progress?.Report(0.92);
                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(zipPath, extractDir);

                // The zip may carry a top-level folder (e.g. Assets/) or files at root.
                // Normalize: find the folder that contains the game exe, else the root.
                var gameRoot = FindGameRoot(extractDir) ?? extractDir;

                progress?.Report(0.97);
                Directory.CreateDirectory(Path.GetDirectoryName(finalDir));
                if (Directory.Exists(finalDir))
                    Directory.Delete(finalDir, recursive: true);
                Directory.Move(gameRoot, finalDir);

                var exe = Core.Paths.TryResolveInstalledVersionExe(version.Tag)
                    ?? throw new FileNotFoundException("Archive did not contain a game executable.");
                progress?.Report(1.0);
                _log?.Info($"Installed {version.Tag} -> {exe}");
                return exe;
            }
            finally
            {
                try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); }
                catch { /* best effort */ }
            }
        }

        /// <summary>Walks down single-child folders to find the folder holding the game exe.</summary>
        private static string FindGameRoot(string dir)
        {
            var current = dir;
            for (var i = 0; i < 4; i++) // defend against deep nesting
            {
                if (File.Exists(Path.Combine(current, "LatticeVeilMonoGame.exe")) ||
                    File.Exists(Path.Combine(current, "LatticeVeil.exe")))
                    return current;

                var children = Directory.GetDirectories(current);
                if (children.Length == 0) return null;
                if (children.Length == 1 && Directory.GetFiles(current).Length == 0)
                    current = children[0];
                else
                    return null;
            }
            return null;
        }

        private static string MakeSafeFolderName(string tag)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                tag = tag.Replace(c, '_');
            return tag.Trim();
        }
    }
}
