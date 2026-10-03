using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using UnityEngine;

namespace LatticeVeil.Launcher
{
    public enum FloatingPanelKind { Versions, Skins }

    /// <summary>
    /// Floating panel mode of the launcher executable. Launched by the main
    /// launcher as its own process (--panel=versions / --panel=skins), it shows
    /// the Version Manager or Skin library in a REAL borderless always-on-top
    /// OS window that floats over the launcher and any other application.
    /// Being a separate OS window means genuine desktop-wide floating; being
    /// the same exe means it shares all on-disk state (versions, skins,
    /// settings) with the main launcher with zero serialization work.
    /// </summary>
    public class FloatingPanelProgram : MonoBehaviour
    {
        private static FloatingPanelKind _kind;
        private Core.Logger _log;
        private GameVersionService _versionService;
        private LegacyVersionInstaller _versionInstaller;
        private System.Collections.Generic.List<GameVersionInfo> _versions = new System.Collections.Generic.List<GameVersionInfo>();
        private GameVersionInfo _selectedVersion;
        private Vector2 _listScroll = Vector2.zero;
        private Vector2 _notesScroll = Vector2.zero;
        private bool _versionsLoading = true;
        private bool _isDownloading;
        private double _downloadProgress;
        private string _statusMessage = "Checking releases...";
        private Vector2 _scroll = Vector2.zero;
        private GUIStyle _box, _label, _button, _header, _closeBtn, _panelBox, _meta, _chip, _headerTexStyle;
        private Texture2D _headerTex;
        private bool _dragging;

        public static bool TryRunFromArgs()
        {
            var args = Environment.GetCommandLineArgs();
            foreach (var raw in args)
            {
                var a = raw ?? string.Empty;
                if (a.StartsWith("--panel=versions", StringComparison.OrdinalIgnoreCase))
                {
                    _kind = FloatingPanelKind.Versions;
                    return true;
                }
                if (a.StartsWith("--panel=skins", StringComparison.OrdinalIgnoreCase))
                {
                    _kind = FloatingPanelKind.Skins;
                    return true;
                }
            }
            return false;
        }

        private void Awake()
        {
            Application.runInBackground = true;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            Application.targetFrameRate = 60;

            _log = new Core.Logger();
            _log.Info($"Floating panel starting: {_kind}");

            if (_kind == FloatingPanelKind.Versions)
            {
                Screen.SetResolution(1080, 620, FullScreenMode.Windowed);
            }
            else
            {
                Screen.SetResolution(880, 620, FullScreenMode.Windowed);
            }
        }

        private void Start()
        {
            LauncherWindowInitializer.ConfigurePanelWindow(
                _kind == FloatingPanelKind.Versions ? "LatticeVeil Versions" : "LatticeVeil Skins");

            if (_kind == FloatingPanelKind.Versions)
            {
                _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                _versionService = new GameVersionService(_log, _httpClient);
                _versionInstaller = new LegacyVersionInstaller(LegacyVersionInstaller.DownloadHttp, _log);
                _ = RefreshVersions();
            }
        }

        private HttpClient _httpClient;

        private void OnDestroy()
        {
            _log?.Info("Floating panel closing.");
        }

        private async System.Threading.Tasks.Task RefreshVersions()
        {
            _versionsLoading = true;
            _statusMessage = "Checking releases...";
            try
            {
                _versions = await _versionService.GetVersionsAsync();
                if (_versions != null && _versions.Count > 0)
                {
                    _selectedVersion = _versions[0];
                    _statusMessage = $"{_versions.Count} releases found.";
                }
                else
                {
                    _statusMessage = "No downloadable releases found.";
                }
            }
            catch (Exception ex)
            {
                _statusMessage = $"Failed to fetch releases: {ex.Message}";
                _log.Warn(_statusMessage);
            }
            finally
            {
                _versionsLoading = false;
            }
        }

        private void OnGUI()
        {
            EnsureStyles();
            var rect = new Rect(0, 0, Screen.width, Screen.height);
            GUI.Box(rect, "", _box);

            // Header (drag to move the floating window anywhere on the desktop)
            var headerRect = new Rect(0, 0, rect.width, 42);
            if (_headerTex != null) GUI.DrawTexture(headerRect, _headerTex);
            var dragRect = new Rect(0, 0, rect.width - 60, 42);
            if (Event.current.type == EventType.MouseDown && dragRect.Contains(Event.current.mousePosition))
            {
                LauncherWindowInitializer.DragLauncherWindow();
                Event.current.Use();
            }
            GUI.Label(new Rect(18, 10, 400, 26),
                _kind == FloatingPanelKind.Versions ? "VERSION MANAGER" : "SKIN LIBRARY", _header);

            var closeRect = new Rect(rect.width - 46, 5, 38, 32);
            if (GUI.Button(closeRect, "X", _closeBtn))
            {
                Close();
                return;
            }

            if (_kind == FloatingPanelKind.Versions) DrawVersionsBody(rect);
            else DrawSkinsBody(rect);
        }

        private void Close()
        {
            GameProcessJob.Shutdown();
            Application.Quit();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#endif
        }

        // ---------------- Versions ----------------
        private void DrawVersionsBody(Rect rect)
        {
            var listRect = new Rect(16, 54, 470, rect.height - 122);
            GUI.Box(listRect, "", _panelBox);
            float rowH = 68f;
            var content = new Rect(0, 0, listRect.width - 20, 8 + _versions.Count * rowH);
            _listScroll = GUI.BeginScrollView(listRect, _listScroll, content, false, true);

            for (int i = 0; i < _versions.Count; i++)
                DrawVersionRow(new Rect(4, 4 + i * rowH, content.width, rowH - 6), _versions[i]);

            if (_versions.Count == 0)
                GUI.Label(new Rect(8, 8, content.width - 8, 40),
                    _versionsLoading ? "Checking releases..." : "No downloadable releases found.", _label);

            GUI.EndScrollView();

            // Notes panel
            var notesRect = new Rect(502, 54, rect.width - 518, rect.height - 122);
            GUI.Box(notesRect, "", _panelBox);
            GUI.Label(new Rect(notesRect.x + 12, notesRect.y + 8, notesRect.width - 24, 24), "UPDATE NOTES", _header);
            var notes = _selectedVersion?.Body ?? "";
            var notesH = Mathf.Max(notesRect.height - 70, notes.Length * 14f);
            _notesScroll = GUI.BeginScrollView(
                new Rect(notesRect.x + 8, notesRect.y + 36, notesRect.width - 16, notesRect.height - 46),
                _notesScroll, new Rect(0, 0, notesRect.width - 36, notesH), false, true);
            GUI.Label(new Rect(0, 0, notesRect.width - 36, notesH), notes, _meta);
            GUI.EndScrollView();

            // Footer
            GUI.Label(new Rect(18, rect.height - 52, rect.width - 200, 26),
                _isDownloading ? $"Downloading {_selectedVersion?.Tag ?? ""}... {(int)(_downloadProgress * 100)}%" : _statusMessage,
                _meta);

            var refreshRect = new Rect(rect.width - 150, rect.height - 56, 132, 36);
            if (GUI.Button(refreshRect, "REFRESH", _button))
                _ = RefreshVersions();
        }

        private void DrawVersionRow(Rect row, GameVersionInfo version)
        {
            GUI.Box(row, "", _panelBox);
            var prevSel = _selectedVersion;
            if (Event.current.type == EventType.MouseDown && row.Contains(Event.current.mousePosition))
                _selectedVersion = version;

            GUI.Label(new Rect(row.x + 12, row.y + 8, row.width - 130, 22), version.ListLabel, _label);

            bool installed = Core.Paths.TryResolveInstalledVersionExe(version.Tag) != null;
            GUI.Label(new Rect(row.x + 12, row.y + 32, row.width - 130, 18),
                $"{version.Tag}{(installed ? "  •  INSTALLED" : "")}", _meta);

            var actionRect = new Rect(row.x + row.width - 112, row.y + 18, 100, 30);
            GUI.enabled = !_isDownloading;
            if (GUI.Button(actionRect, installed ? "UNINSTALL" : "INSTALL", _button))
            {
                if (installed) UninstallVersion(version);
                else _ = InstallVersion(version);
            }
            GUI.enabled = true;
            if (!ReferenceEquals(prevSel, _selectedVersion)) { /* selection change repaints next frame */ }
        }

        private async System.Threading.Tasks.Task InstallVersion(GameVersionInfo version)
        {
            _isDownloading = true;
            _downloadProgress = 0;
            _statusMessage = $"Installing {version.Tag}...";
            try
            {
                var progress = new Progress<double>(p => _downloadProgress = p);
                // DownloadAndInstallAsync dispatches to the manifest-verified exe
                // flow when the release ships one, else the zip flow.
                await _versionInstaller.DownloadAndInstallAsync(version, progress);
                _statusMessage = $"Installed {version.Tag}.";
                _log.Info(_statusMessage);
            }
            catch (Exception ex)
            {
                _statusMessage = $"Install failed: {ex.Message}";
                _log.Error(_statusMessage);
            }
            finally
            {
                _isDownloading = false;
            }
        }

        private static string MakeSafeFolderName(string tag)
        {
            var safe = (tag ?? "").Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
                safe = safe.Replace(c, '_');
            return safe;
        }

        private void UninstallVersion(GameVersionInfo version)
        {
            try
            {
                var safeTag = MakeSafeFolderName(version.Tag);
                var dir = Path.Combine(Core.Paths.VersionsDir, safeTag);
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                    _statusMessage = $"Uninstalled {version.Tag}.";
                    _log.Info(_statusMessage);
                }
                else
                {
                    _statusMessage = $"{version.Tag} was not installed.";
                }
            }
            catch (Exception ex)
            {
                _statusMessage = $"Uninstall failed: {ex.Message}";
                _log.Error(_statusMessage);
            }
        }

        // ---------------- Skins ----------------
        private string[] _skinPaths;
        private Vector2 _skinScroll = Vector2.zero;

        private void DrawSkinsBody(Rect rect)
        {
            _skinPaths = GetLocalSkinPaths();
            var activeHash = ReadActiveSkinHashSafe();

            var listRect = new Rect(16, 54, 400, rect.height - 70);
            GUI.Box(listRect, "", _panelBox);
            float rowH = 74f;
            var content = new Rect(0, 0, listRect.width - 20, 8 + (_skinPaths.Length + 1) * rowH);
            _skinScroll = GUI.BeginScrollView(listRect, _skinScroll, content, false, true);

            float rowY = 4;
            DrawSkinRow(new Rect(4, rowY, content.width, rowH - 6), "DEFAULT SKIN", null,
                string.IsNullOrWhiteSpace(activeHash), "USE DEFAULT");
            rowY += rowH;
            foreach (var path in _skinPaths)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var isActive = string.Equals(name, activeHash, StringComparison.OrdinalIgnoreCase);
                DrawSkinRow(new Rect(4, rowY, content.width, rowH - 6), name, path, isActive, "USE");
                rowY += rowH;
            }
            GUI.EndScrollView();

            // Right column: preview + actions
            var previewRect = new Rect(432, 54, rect.width - 448, rect.height - 70);
            GUI.Box(previewRect, "", _panelBox);
            var selectedPath = _selectedSkinPath;
            var thumb = LoadSkinThumb(selectedPath);
            if (thumb != null)
                GUI.DrawTexture(new Rect(previewRect.x + 16, previewRect.y + 16, previewRect.width - 32, previewRect.height - 120), thumb, ScaleMode.ScaleToFit);

            var uploadRect = new Rect(previewRect.x + 16, previewRect.y + previewRect.height - 92, 120, 38);
            if (GUI.Button(uploadRect, "UPLOAD", _button))
                ImportSkin();
            var folderRect = new Rect(previewRect.x + 148, previewRect.y + previewRect.height - 92, 120, 38);
            if (GUI.Button(folderRect, "FOLDER", _button))
            {
                try { Process.Start("explorer.exe", Core.Paths.UserSkinsDir); } catch { }
            }
        }

        private string _selectedSkinPath;

        private void DrawSkinRow(Rect row, string displayName, string path, bool isActive, string useText)
        {
            GUI.Box(row, "", _panelBox);
            var thumb = LoadSkinThumb(path);
            if (thumb != null)
                GUI.DrawTexture(new Rect(row.x + 8, row.y + 8, 58, 58), thumb, ScaleMode.ScaleToFit);

            GUI.Label(new Rect(row.x + 74, row.y + 8, row.width - 190, 22), displayName, _label);
            GUI.Label(new Rect(row.x + 74, row.y + 32, row.width - 190, 18), isActive ? "ACTIVE" : "", _meta);
            if (Event.current.type == EventType.MouseDown && row.Contains(Event.current.mousePosition) && path != null)
                _selectedSkinPath = path;

            var useRect = new Rect(row.x + row.width - 106, row.y + 8, 96, 28);
            GUI.enabled = !isActive;
            if (GUI.Button(useRect, useText, _button))
            {
                if (path == null) SkinManager.ClearActiveSkin();
                else SkinManager.ImportAndSetActiveSkin(path, out _, out _, Path.GetFileNameWithoutExtension(path));
            }
            GUI.enabled = true;

            if (path != null)
            {
                var removeRect = new Rect(row.x + row.width - 106, row.y + 40, 96, 24);
                if (GUI.Button(removeRect, "REMOVE", _button))
                {
                    try { File.Delete(path); if (string.Equals(_selectedSkinPath, path)) _selectedSkinPath = null; } catch { }
                }
            }
        }

        private string[] GetLocalSkinPaths()
        {
            try
            {
                Directory.CreateDirectory(Core.Paths.UserSkinsDir);
                return Directory.GetFiles(Core.Paths.UserSkinsDir, "*.png", SearchOption.TopDirectoryOnly)
                    .Where(path => !Path.GetFileName(path).StartsWith(".temp_", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch { return Array.Empty<string>(); }
        }

        private string ReadActiveSkinHashSafe()
        {
            try
            {
                return File.Exists(Core.Paths.ActiveSkinHashPath) ? File.ReadAllText(Core.Paths.ActiveSkinHashPath).Trim() : string.Empty;
            }
            catch { return string.Empty; }
        }

        private Texture2D LoadSkinThumb(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.LoadImage(File.ReadAllBytes(path));
                return tex;
            }
            catch { return null; }
        }

        private void ImportSkin()
        {
            var filePath = SkinManager.PromptSelectSkinFile();
            if (string.IsNullOrEmpty(filePath)) return;
            if (SkinManager.ValidateSkinFile(filePath, out _, out _, out var err))
                _statusMessage = "Skin imported.";
            else
                _statusMessage = $"Invalid skin: {err}";
        }

        // ---------------- styles ----------------
        private void EnsureStyles()
        {
            if (_box != null) return;

            _box = new GUIStyle(GUI.skin.box);
            _headerTex = MakeHeaderTex();
            _headerTexStyle = new GUIStyle();

            _label = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
            _label.normal.textColor = new Color(0.95f, 0.95f, 0.95f);

            _meta = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
            _meta.normal.textColor = new Color(0.7f, 0.7f, 0.7f);

            _button = new GUIStyle(GUI.skin.button) { fontSize = 12, fontStyle = FontStyle.Bold };
            _header = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };
            _header.normal.textColor = new Color(1f, 0.82f, 0.45f);

            _closeBtn = new GUIStyle(GUI.skin.button) { fontSize = 13, fontStyle = FontStyle.Bold };

            _panelBox = new GUIStyle(GUI.skin.box);
            _panelBox.normal.background = MakeFlatTex(new Color(0.09f, 0.09f, 0.10f, 1f));
        }

        private static Texture2D MakeHeaderTex()
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, new Color(0.07f, 0.07f, 0.08f, 1f));
            t.Apply();
            return t;
        }

        private static Texture2D MakeFlatTex(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }
    }
}
