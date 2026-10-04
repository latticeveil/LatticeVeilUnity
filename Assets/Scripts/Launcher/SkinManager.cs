using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using LatticeVeil.Core;

namespace LatticeVeil.Launcher
{
    /// <summary>
    /// Manages player skin importing, backups, active selection,
    /// and online skin syncing for the launcher.
    /// </summary>
    public static class SkinManager
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct OpenFileName
        {
            public int structSize;
            public IntPtr hwnd;
            public IntPtr hinst;
            public string filter;
            public string customFilter;
            public int maxCustFilter;
            public int filterIndex;
            public string file;
            public int maxFile;
            public string fileTitle;
            public int maxFileTitle;
            public string initialDir;
            public string title;
            public int flags;
            public short fileOffset;
            public short fileExtension;
            public string defExt;
            public IntPtr custData;
            public IntPtr hook;
            public string templateName;
            public IntPtr reservedPtr;
            public int reservedInt;
            public int flagsEx;
        }

        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool GetOpenFileName([In, Out] ref OpenFileName ofn);

        public static string PromptSelectSkinFile()
        {
            try
            {
                var ofn = new OpenFileName();
                ofn.structSize = Marshal.SizeOf(ofn);
                ofn.filter = "Skin Files (*.png)\0*.png\0All Files (*.*)\0*.*\0";
                ofn.file = new string(new char[512]);
                ofn.maxFile = ofn.file.Length;
                ofn.fileTitle = new string(new char[512]);
                ofn.maxFileTitle = ofn.fileTitle.Length;
                ofn.initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                ofn.title = "Select LatticeVeil Player Skin (64x64 PNG)";
                ofn.flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008; // OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR

                if (GetOpenFileName(ref ofn))
                {
                    return ofn.file.Trim('\0', ' ');
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[SkinManager] OpenFileDialog failed: {ex.Message}");
            }
            return null;
        }

        public static Texture2D LoadActiveSkinTexture()
        {
            try
            {
                var activeTxt = Paths.ActiveSkinHashPath;
                if (File.Exists(activeTxt))
                {
                    var hash = File.ReadAllText(activeTxt).Trim();
                    if (!string.IsNullOrWhiteSpace(hash) && !string.Equals(hash, "default_skin", StringComparison.OrdinalIgnoreCase))
                    {
                        var candidates = new[]
                        {
                            Path.Combine(Paths.UserSkinsDir, $"{hash}.png"),
                            Path.Combine(Paths.RuntimeSkinsDir, $"{hash}.png"),
                            Path.Combine(Paths.UserSkinsDir, "Veilnet_skin.png"),
                            Path.Combine(Paths.RuntimeSkinsDir, "Veilnet_skin.png")
                        };

                        foreach (var candidate in candidates)
                        {
                            if (File.Exists(candidate))
                            {
                                var bytes = File.ReadAllBytes(candidate);
                                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                                if (tex.LoadImage(bytes))
                                {
                                    tex.filterMode = FilterMode.Point;
                                    return tex;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[SkinManager] Failed loading active skin: {ex.Message}");
            }

            return DefaultPlayerSkinFactory.CreateTexture();
        }

        public static bool ValidateSkinFile(string sourceFilePath, out Texture2D texture, out string hash, out string error)
        {
            texture = null;
            hash = null;
            error = null;

            try
            {
                if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
                {
                    error = "Skin file not found.";
                    return false;
                }

                var bytes = File.ReadAllBytes(sourceFilePath);
                if (bytes.Length == 0 || bytes.Length > 64 * 1024)
                {
                    error = "Invalid skin file size (must be <= 64KB).";
                    return false;
                }

                var tempTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tempTex.LoadImage(bytes))
                {
                    error = "Failed to decode PNG image.";
                    return false;
                }

                if (tempTex.width != 64 || (tempTex.height != 64 && tempTex.height != 32))
                {
                    error = $"Skin must be 64x64 PNG (found {tempTex.width}x{tempTex.height}).";
                    return false;
                }

                tempTex.filterMode = FilterMode.Point;
                texture = tempTex;

                using (var sha = SHA256.Create())
                {
                    var hashBytes = sha.ComputeHash(bytes);
                    var sb = new StringBuilder();
                    foreach (var b in hashBytes) sb.Append(b.ToString("x2"));
                    hash = sb.ToString();
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool ImportAndSetActiveSkin(string sourceFilePath, out string error, out string computedHash, string userFileName = null)
        {
            error = null;
            computedHash = null;
            try
            {
                if (!ValidateSkinFile(sourceFilePath, out var tex, out var hash, out error))
                {
                    return false;
                }

                computedHash = hash;
                var bytes = File.ReadAllBytes(sourceFilePath);

                // 1. Save to User Skins Dir (Documents/LatticeVeil/Skins)
                var userSkinsDir = Paths.UserSkinsDir;
                Directory.CreateDirectory(userSkinsDir);

                // If userFileName is specified (e.g. "Veilnet_skin"), save/replace it directly
                // Do NOT generate extra duplicate hash files in the user skins folder
                if (!string.IsNullOrWhiteSpace(userFileName))
                {
                    var cleanName = Path.GetFileNameWithoutExtension(userFileName).Trim();
                    if (!string.IsNullOrWhiteSpace(cleanName))
                    {
                        var namedUserPng = Path.Combine(userSkinsDir, $"{cleanName}.png");
                        File.WriteAllBytes(namedUserPng, bytes);
                    }
                }
                else
                {
                    var destUserPng = Path.Combine(userSkinsDir, $"{hash}.png");
                    File.WriteAllBytes(destUserPng, bytes);
                }

                // 2. Save to Runtime Skins Dir (AppData/Roaming/LatticeVeil/Runtime/skins)
                var runtimeSkinsDir = Paths.RuntimeSkinsDir;
                Directory.CreateDirectory(runtimeSkinsDir);

                // Create backup of currently active skin before overriding
                BackupCurrentSkin();

                var destPng = Path.Combine(runtimeSkinsDir, $"{hash}.png");
                File.WriteAllBytes(destPng, bytes);

                // Set active.txt
                File.WriteAllText(Paths.ActiveSkinHashPath, hash);

                // Touch signal
                try { File.WriteAllText(Paths.SkinChangeSignalPath, hash); } catch { }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool ImportAndSetActiveSkin(string sourceFilePath, out string error, out string computedHash)
        {
            return ImportAndSetActiveSkin(sourceFilePath, out error, out computedHash, Path.GetFileName(sourceFilePath));
        }

        public static bool ImportAndSetActiveSkin(string sourceFilePath, out string error)
        {
            return ImportAndSetActiveSkin(sourceFilePath, out error, out _, Path.GetFileName(sourceFilePath));
        }

        public static bool BackupCurrentSkin()
        {
            try
            {
                var activeTxt = Paths.ActiveSkinHashPath;
                if (!File.Exists(activeTxt)) return false;

                var hash = File.ReadAllText(activeTxt).Trim();
                if (string.IsNullOrWhiteSpace(hash)) return false;

                var candidates = new[]
                {
                    Path.Combine(Paths.UserSkinsDir, $"{hash}.png"),
                    Path.Combine(Paths.RuntimeSkinsDir, $"{hash}.png")
                };

                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate))
                    {
                        Directory.CreateDirectory(Paths.RuntimeSkinsDir);
                        File.Copy(candidate, Paths.SkinBackupPath, true);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static bool HasBackup()
        {
            try
            {
                return File.Exists(Paths.SkinBackupPath) && new FileInfo(Paths.SkinBackupPath).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        public static bool RevertToBackup(out string error)
        {
            error = null;
            try
            {
                if (!HasBackup())
                {
                    error = "No backup skin found.";
                    return false;
                }

                return ImportAndSetActiveSkin(Paths.SkinBackupPath, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool ClearActiveSkin()
        {
            try
            {
                // Save current skin to backup first so user can undo
                BackupCurrentSkin();

                if (File.Exists(Paths.ActiveSkinHashPath))
                {
                    File.Delete(Paths.ActiveSkinHashPath);
                }
                try { File.WriteAllText(Paths.SkinChangeSignalPath, ""); } catch { }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static async Task<(bool success, string message)> ReloadOnlineSkinAsync(string userId, string token)
        {
            await Task.Yield();
            if (string.IsNullOrWhiteSpace(token))
            {
                return (false, "You must be logged into Veilnet to fetch your online skin.");
            }

            try
            {
                // In Veilnet/Supabase, avatars/skins are synced into SocialSession
                var socialDir = Path.Combine(Paths.RuntimeStateDir, "SocialSession");
                var avatarPath = Path.Combine(socialDir, "veilnet_avatar_session.png");

                if (File.Exists(avatarPath))
                {
                    if (ImportAndSetActiveSkin(avatarPath, out var err))
                    {
                        return (true, "Online skin synced successfully.");
                    }
                    return (false, $"Online skin import error: {err}");
                }

                return (true, "No distinct online skin found; using local active skin.");
            }
            catch (Exception ex)
            {
                return (false, $"Sync failed: {ex.Message}");
            }
        }

        // ---------------- uploaded-skin marker ----------------
        // Tracks which skin was last uploaded to Veilnet so the skins panel can
        // badge the currently uploaded entry even before the online fetch lands.

        public static string UploadedSkinHashPath =>
            Path.Combine(Paths.RuntimeSkinsDir, "uploaded.txt");

        public static void MarkSkinUploaded(string hash)
        {
            try
            {
                Directory.CreateDirectory(Paths.RuntimeSkinsDir);
                File.WriteAllText(UploadedSkinHashPath, (hash ?? string.Empty).Trim());
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[SkinManager] Failed to record uploaded skin hash: {ex.Message}");
            }
        }

        public static string TryReadUploadedSkinHash()
        {
            try
            {
                return File.Exists(UploadedSkinHashPath)
                    ? File.ReadAllText(UploadedSkinHashPath).Trim()
                    : string.Empty;
            }
            catch { return string.Empty; }
        }
    }
}
