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
        /// <summary>
        /// Dedicated download client without a hard timeout — the launcher's shared
        /// 20s client would abort large game downloads mid-stream.
        /// </summary>
        private static readonly HttpClient DownloadHttp = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        static LegacyVersionInstaller()
        {
            DownloadHttp.DefaultRequestHeaders.Add("User-Agent", "LatticeVeil-Launcher/1.0");
        }

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

            // Manifest flow: download the exe, verify SHA-256 against the release
            // manifest, then install. Only hash-verified exes are ever installed.
            if (version.HasManifest)
                return await DownloadExeWithManifestAsync(version, progress, ct).ConfigureAwait(false);

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
                await DownloadToFileAsync(version.AssetUrl, zipPath, version.AssetSizeBytes, progress, ct).ConfigureAwait(false);

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

        /// <summary>
        /// Manifest install: download the game exe, compute its SHA-256 and reject
        /// the download on any mismatch with the manifest hash, then place the exe
        /// into Versions\<tag>\ together with a copy of the manifest so the
        /// launcher can re-verify the install later without the network.
        /// </summary>
        private async Task<string> DownloadExeWithManifestAsync(
            GameVersionInfo version,
            IProgress<double> progress,
            CancellationToken ct)
        {
            Directory.CreateDirectory(Core.Paths.VersionsDir);
            var stagingDir = Path.Combine(Path.GetTempPath(), "LatticeVeil-install-" + Guid.NewGuid().ToString("N"));
            var finalDir = Path.Combine(Core.Paths.VersionsDir, MakeSafeFolderName(version.Tag));
            var exeName = string.IsNullOrWhiteSpace(version.ExeAssetName)
                ? "LatticeVeilMonoGame.exe"
                : version.ExeAssetName;

            try
            {
                Directory.CreateDirectory(stagingDir);
                var stagedExePath = Path.Combine(stagingDir, exeName);

                _log?.Info($"Downloading {version.ExeAssetName} ({version.SizeDisplay})...");
                await DownloadToFileAsync(version.ExeAssetUrl, stagedExePath, version.ExeAssetSizeBytes, progress, ct).ConfigureAwait(false);

                // ---- Hash verification (the whole point of the manifest) ----
                progress?.Report(0.93);
                var actualHash = ComputeSha256(stagedExePath);
                if (!string.Equals(actualHash, version.ExeHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Hash mismatch for {version.Tag}: expected {version.ExeHash}, got {actualHash}. Download rejected.");
                }
                _log?.Info($"Hash verified for {version.Tag}: {actualHash}");

                // ---- Install into Versions\<tag>\ ----
                progress?.Report(0.97);
                Directory.CreateDirectory(finalDir);
                var finalExePath = Path.Combine(finalDir, exeName);
                if (File.Exists(finalExePath))
                    File.Delete(finalExePath);
                File.Move(stagedExePath, finalExePath);

                if (!string.IsNullOrWhiteSpace(version.ManifestJson))
                {
                    var manifestPath = Path.Combine(finalDir, "version.json");
                    File.WriteAllText(manifestPath, version.ManifestJson, System.Text.Encoding.UTF8);
                }

                progress?.Report(1.0);
                _log?.Info($"Verified and installed {version.Tag} -> {finalExePath}");
                return finalExePath;
            }
            finally
            {
                try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); }
                catch { /* best effort */ }
            }
        }

        /// <summary>Streams a download to disk, reporting 0..0.9 via progress.</summary>
        private async Task DownloadToFileAsync(
            string url,
            string destinationPath,
            long totalBytes,
            IProgress<double> progress,
            CancellationToken ct)
        {
            using var response = await DownloadHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? totalBytes;

            using var httpStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

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

        /// <summary>SHA-256 of a file as lowercase hex.</summary>
        private static string ComputeSha256(string filePath)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hash = sha.ComputeHash(stream);
            var sb = new System.Text.StringBuilder(hash.Length * 2);
            foreach (var b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
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
