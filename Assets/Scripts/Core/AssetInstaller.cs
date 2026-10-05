using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace LatticeVeil.Core
{
    /// <summary>
    /// Asset installer for LatticeVeil.
    /// Copies default assets to Documents\LatticeVeil\Assets if missing.
    /// Ported from MonoGame LatticeVeil for Unity compatibility.
    /// </summary>
    public sealed class AssetInstallResult
    {
        public static AssetInstallResult Empty { get; } = new();

        public bool UsedDevMirror { get; set; }
        public string SourceRoot { get; set; }
        public int InstalledCount { get; set; }
        public int ReplacedCount { get; set; }
        public long BytesCopied { get; set; }
        public string[] ChangedAssets { get; set; } = Array.Empty<string>();
        public string[] Errors { get; set; } = Array.Empty<string>();

        public int ChangedCount => InstalledCount + ReplacedCount;
        public bool HadChanges => ChangedCount > 0;
        public bool HasErrors => Errors.Length > 0;
    }

    public static class AssetInstaller
    {
        /// <summary>
        /// Copies Defaults/Assets into Documents\LatticeVeil\Assets if files are missing.
        /// Dev builds treat the local dev assets folder as the read-only authority and
        /// also replace stale Documents assets when the source file differs.
        /// </summary>
        public static AssetInstallResult EnsureDefaultsInstalled(string defaultsRoot, Logger log)
        {
            try
            {
                if (Paths.IsDevBuild)
                {
                    // Development build: directly use the local MonoGame Defaults/Assets directory.
                    // Do not copy, sync, or touch .asset_manifest.lvc.
                    var devAssetsPath = Paths.LocalAssetsDir;
                    log.Info($"Development asset root: {devAssetsPath}");

                    return new AssetInstallResult
                    {
                        UsedDevMirror = true,
                        SourceRoot = devAssetsPath,
                        InstalledCount = 0,
                        ReplacedCount = 0
                    };
                }

                // Release / normal build:
                log.Info($"User default assets: {Paths.AssetsDir}");
                Directory.CreateDirectory(Paths.AssetsDir);

                string defaultsAssets = null;
                var bundledCandidates = new[]
                {
                    Path.Combine(defaultsRoot, "Defaults", "Assets"),
                    Path.Combine(defaultsRoot, "Assets")
                };

                for (var i = 0; i < bundledCandidates.Length; i++)
                {
                    if (Directory.Exists(bundledCandidates[i]))
                    {
                        defaultsAssets = bundledCandidates[i];
                        break;
                    }
                }

                if (defaultsAssets == null)
                {
                    log.Info($"Using installed user assets at: {Paths.AssetsDir}");
                    return AssetInstallResult.Empty;
                }

                log.Info($"Using bundled fallback assets from: {defaultsAssets}");

                var installed = 0;
                var replaced = 0;
                long bytesCopied = 0;
                var changed = new List<string>();
                var errors = new List<string>();

                foreach (var srcPath in Directory.GetFiles(defaultsAssets, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(defaultsAssets, srcPath);
                    if (Paths.IsDisallowedAssetRelativePath(rel))
                        continue;
                    var dst = Path.Combine(Paths.AssetsDir, rel);

                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

                    try
                    {
                        if (!File.Exists(dst))
                        {
                            File.Copy(srcPath, dst);
                            installed++;
                            bytesCopied += new FileInfo(srcPath).Length;
                            changed.Add(rel);
                            log.Info($"Installed default asset: {rel}");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{rel}: {ex.Message}");
                        log.Warn($"Asset sync failed for '{rel}': {ex.Message}");
                    }
                }

                return new AssetInstallResult
                {
                    UsedDevMirror = false,
                    SourceRoot = defaultsAssets,
                    InstalledCount = installed,
                    ReplacedCount = replaced,
                    BytesCopied = bytesCopied,
                    ChangedAssets = changed.ToArray(),
                    Errors = errors.ToArray()
                };
            }
            catch (Exception ex)
            {
                log.Error($"EnsureDefaultsInstalled failed: {ex.Message}");
                return new AssetInstallResult
                {
                    Errors = new[] { ex.Message }
                };
            }
        }

        private static bool FilesMatch(string leftPath, string rightPath)
        {
            try
            {
                var left = new FileInfo(leftPath);
                var right = new FileInfo(rightPath);
                if (!right.Exists || left.Length != right.Length)
                    return false;

                using var sha = SHA256.Create();
                using var leftStream = File.OpenRead(leftPath);
                using var rightStream = File.OpenRead(rightPath);
                var leftHash = sha.ComputeHash(leftStream);
                var rightHash = sha.ComputeHash(rightStream);
                return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
            }
            catch
            {
                return false;
            }
        }
    }
}