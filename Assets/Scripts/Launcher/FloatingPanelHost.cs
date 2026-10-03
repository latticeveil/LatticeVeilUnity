using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using UnityEngine;

namespace LatticeVeil.Launcher
{
    public enum FloatingPanelKind { Versions, Skins }

    /// <summary>
    /// In-process floating panel host. Opens the Version Manager / Skin library
    /// in a NATIVE Win32 window created inside the launcher's own process and
    /// OWNED BY the launcher's main window:
    ///   - no new process is spawned (one Task Manager entry),
    ///   - the system stacks the panel above its owner,
    ///   - closing the launcher automatically destroys the panel,
    ///   - the panel is centered over the launcher when opened.
    /// Rendering: the panel content is drawn with IMGUI into a RenderTexture
    /// from the launcher's own OnGUI loop, then the pixels are pushed into the
    /// native window via StretchDIBits. Input (mouse move / click / wheel)
    /// arrives through a real WndProc attached to the panel window.
    /// </summary>
    public class FloatingPanelHost : MonoBehaviour
    {
        private static FloatingPanelHost _active;
        private static bool _wndClassRegistered;

        private FloatingPanelKind _kind;
        private Core.Logger _log;
        private PanelContent _content;
        private IntPtr _hwnd = IntPtr.Zero;
        private RenderTexture _rt;
        private Texture2D _readTex;
        private Color32[] _pixels;
        private int _w, _h;
        private bool _destroying;

        // ------------------------- public API -------------------------

        /// <summary>
        /// Opens the panel as a native window owned by the launcher window,
        /// centered over it. Returns false when native windows are unavailable
        /// (editor / non-Windows) so the caller can fall back to the in-window
        /// modal. Opening a second panel closes the first.
        /// </summary>
        public static bool Open(FloatingPanelKind kind, Core.Logger log)
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            try
            {
                IntPtr owner = LauncherWindowInitializer.MainWindowHandle;
                if (owner == IntPtr.Zero)
                    return false;

                var prev = _active;
                _active = null;
                if (prev != null)
                {
                    prev.ClosePanel();
                    Destroy(prev.gameObject);
                }

                var go = new GameObject("FloatingPanelHost");
                DontDestroyOnLoad(go);
                _active = go.AddComponent<FloatingPanelHost>();
                if (!_active.Initialize(kind, log, owner))
                {
                    Destroy(_active.gameObject);
                    _active = null;
                    return false;
                }
                log?.Info($"Floating panel opened in-process: {kind} (owned by launcher window, no extra process).");
                return true;
            }
            catch (Exception ex)
            {
                log?.Warn($"Floating panel could not open, falling back to in-window modal: {ex.Message}");
                return false;
            }
#else
            return false; // editor: in-window modal fallback
#endif
        }

        // ------------------------- lifecycle -------------------------

        private bool Initialize(FloatingPanelKind kind, Core.Logger log, IntPtr owner)
        {
            _kind = kind;
            _log = log;
            _content = kind == FloatingPanelKind.Versions
                ? (PanelContent)new VersionsPanel(log)
                : new SkinsPanel(log);

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            _hwnd = PanelWin32.CreatePanelWindow(owner, out _w, out _h,
                kind == FloatingPanelKind.Versions ? 1080 : 880, 620);
            if (_hwnd == IntPtr.Zero)
                return false;

            PanelWin32.ActiveHost = this;

            _rt = new RenderTexture(_w, _h, 0, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1,
                useMipMap = false,
            };
            _rt.Create();
            _readTex = new Texture2D(_w, _h, TextureFormat.RGBA32, false, true);
            _pixels = new Color32[_w * _h];

            // Center the panel over the launcher window.
            PanelWin32.CenterOverOwner(_hwnd, owner);
            PanelWin32.Show(_hwnd);
            return true;
#else
            return false;
#endif
        }

        private void OnDestroy()
        {
            ClosePanel();
        }

        /// <summary>Tears down the native window and GPU resources.</summary>
        public void ClosePanel()
        {
            if (_destroying) return;
            _destroying = true;

            if (_rt != null) { _rt.Release(); Destroy(_rt); }
            if (_readTex != null) Destroy(_readTex);
            _content?.Shutdown();

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (_hwnd != IntPtr.Zero)
            {
                PanelWin32.ActiveHost = null;
                PanelWin32.DestroyPanelWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
#endif

            if (_active == this) _active = null;
        }

        // ------------------------- rendering -------------------------

        /// <summary>
        /// Called from the launcher's own OnGUI loop (Repaint only). Draws the
        /// panel into the render texture and pushes the pixels to the window.
        /// </summary>
        private void OnGUI()
        {
            if (_hwnd == IntPtr.Zero || _rt == null) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;

            PanelWin32.PollWindowSize(_hwnd, ref _w, ref _h, _rt, _readTex, ref _pixels);
            if (_pixels == null || _pixels.Length != _w * _h)
                _pixels = new Color32[_w * _h];

            // Per-frame content tick (kick off async work etc.).
            _content.Tick();

            // Real input harvested by the WndProc since the last frame.
            PanelWin32.GetInput(out var mouse, out var clicked, out var wheelDelta);

            var prevActive = RenderTexture.active;
            RenderTexture.active = _rt;
            GL.Clear(false, true, new Color(0.05f, 0.05f, 0.06f, 1f));

            // Map IMGUI's pixel space onto the render texture (top-left origin,
            // y down) so all panel drawing lands 1:1 in the native window.
            var prevMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Ortho(0, _w, _h, 0, -1, 1);

            var ui = new PanelUI(mouse, clicked, wheelDelta);
            GUI.BeginGroup(new Rect(0, 0, _w, _h));
            _content.OnGUI(ui, new Rect(0, 0, _w, _h));
            GUI.EndGroup();

            GUI.matrix = prevMatrix;

            // Blit the render texture into the native window.
            _readTex.ReadPixels(new Rect(0, 0, _w, _h), 0, 0);
            RenderTexture.active = prevActive;
            _readTex.Apply(false);
            _pixels = _readTex.GetPixels32();

            // RGBA (Unity) -> BGRA (DIB), force opaque.
            for (int i = 0; i < _pixels.Length; i++)
            {
                var c = _pixels[i];
                _pixels[i] = new Color32(c.b, c.g, c.r, 255);
            }

            var pin = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
            try
            {
                PanelWin32.PresentPixels(_hwnd, pin.AddrOfPinnedObject(), _w, _h);
            }
            finally
            {
                pin.Free();
            }
            PanelWin32.ConsumeWheel();
        }

        // ======================= tiny UI toolkit =======================

        /// <summary>Input + draw helpers for panel content. IMGUI calls inside
        /// the panel's render texture; hit-testing uses real Win32 input.</summary>
        public class PanelUI
        {
            public readonly Vector2 Mouse;
            public readonly bool Clicked;
            public readonly float WheelDelta;

            private Texture2D _flat;

            public PanelUI(Vector2 mouse, bool clicked, float wheelDelta)
            {
                Mouse = mouse;
                Clicked = clicked;
                WheelDelta = wheelDelta;
            }

            private Texture2D Flat
            {
                get
                {
                    if (_flat == null)
                    {
                        _flat = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                        _flat.SetPixel(0, 0, Color.white);
                        _flat.Apply();
                    }
                    return _flat;
                }
            }

            public void Fill(Rect r, Color c)
            {
                var prev = GUI.color;
                GUI.color = c;
                GUI.DrawTexture(r, Flat, ScaleMode.StretchToFill);
                GUI.color = prev;
            }

            public void Frame(Rect r, Color c)
            {
                Fill(new Rect(r.x, r.y, r.width, 1), c);
                Fill(new Rect(r.x, r.yMax - 1, r.width, 1), c);
                Fill(new Rect(r.x, r.y, 1, r.height), c);
                Fill(new Rect(r.xMax - 1, r.y, 1, r.height), c);
            }

            public void Label(Rect r, string text, int size, Color color, bool bold = false,
                TextAnchor anchor = TextAnchor.UpperLeft, bool wrap = false)
            {
                EnsureStyle(ref _labelStyle, size, color, bold, anchor, wrap);
                GUI.Label(r, text ?? "", _labelStyle);
            }
            private GUIStyle _labelStyle;

            /// <summary>Draws a button; returns true on click this frame.</summary>
            public bool Button(Rect r, string text, int size, Color bg, Color hoverBg, Color fg)
            {
                bool hover = r.Contains(Mouse);
                Fill(r, hover ? hoverBg : bg);
                Frame(r, new Color(0.28f, 0.30f, 0.34f));
                Label(r, text, size, fg, bold: true, anchor: TextAnchor.MiddleCenter);
                return Clicked && hover;
            }

            /// <summary>
            /// Clipped list viewport with wheel scrolling. Call inside it after
            /// Begin; returns the offset rows should be drawn at (-scroll).
            /// </summary>
            public Rect BeginList(Rect viewport, float contentHeight, ref Vector2 scroll)
            {
                if (viewport.Contains(Mouse))
                    scroll.y = Mathf.Clamp(scroll.y - WheelDelta, 0, Mathf.Max(0, contentHeight - viewport.height));
                scroll.y = Mathf.Clamp(scroll.y, 0, Mathf.Max(0, contentHeight - viewport.height));

                Fill(viewport, new Color(0.09f, 0.09f, 0.10f, 1f));
                Frame(viewport, new Color(0.16f, 0.16f, 0.18f));
                GUI.BeginGroup(viewport);

                // Scrollbar indicator
                if (contentHeight > viewport.height)
                {
                    float barH = Mathf.Max(24, viewport.height * viewport.height / contentHeight);
                    float barY = (viewport.height - barH) * (scroll.y / (contentHeight - viewport.height));
                    Fill(new Rect(viewport.width - 5, barY, 3, barH), new Color(0.35f, 0.36f, 0.4f));
                }
                return new Rect(0, -scroll.y, viewport.width, contentHeight);
            }

            public void EndList() => GUI.EndGroup();

            private static void EnsureStyle(ref GUIStyle style, int size, Color color, bool bold,
                TextAnchor anchor, bool wrap)
            {
                if (style != null)
                {
                    style.fontSize = size;
                    style.normal.textColor = color;
                    style.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
                    style.alignment = anchor;
                    style.wordWrap = wrap;
                    return;
                }
                style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = size,
                    fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
                    alignment = anchor,
                    wordWrap = wrap,
                };
                style.normal.textColor = color;
            }
        }

        // ======================= panel content =======================

        public abstract class PanelContent
        {
            protected Core.Logger Log;

            protected PanelContent(Core.Logger log) { Log = log; }

            public abstract void OnGUI(PanelUI ui, Rect panelRect);

            /// <summary>Called each frame before content drawing (e.g. kick off async work).</summary>
            public virtual void Tick() { }

            public virtual void Shutdown() { }

            // Shared styles/colors
            protected static readonly Color Bg = new Color(0.07f, 0.07f, 0.08f);
            protected static readonly Color HeaderBg = new Color(0.10f, 0.10f, 0.11f);
            protected static readonly Color Accent = new Color(1f, 0.82f, 0.45f);
            protected static readonly Color Text = new Color(0.95f, 0.95f, 0.95f);
            protected static readonly Color Dim = new Color(0.70f, 0.70f, 0.70f);
            protected static readonly Color RowBg = new Color(0.11f, 0.11f, 0.12f);
            protected static readonly Color RowHover = new Color(0.15f, 0.15f, 0.17f);
            protected static readonly Color BtnBg = new Color(0.18f, 0.19f, 0.22f);
            protected static readonly Color BtnHover = new Color(0.26f, 0.28f, 0.32f);
            protected const int HeaderH = 42;

            protected void DrawHeader(PanelUI ui, Rect panelRect, string title)
            {
                var header = new Rect(0, 0, panelRect.width, HeaderH);
                ui.Fill(header, HeaderBg);
                ui.Label(new Rect(18, 10, panelRect.width - 120, 26), title, 15, Accent, bold: true);

                // Close button (top-right). The WndProc leaves this strip as
                // HTCLIENT so the click reaches us instead of the title drag.
                var closeRect = new Rect(panelRect.width - 46, 5, 38, 32);
                if (ui.Button(closeRect, "X", 13, BtnBg, BtnHover, Text))
                    CloseRequested = true;
            }

            protected bool CloseRequested;
        }

        // ------------------------- Versions -------------------------

        public class VersionsPanel : PanelContent
        {
            private GameVersionService _versionService;
            private HttpClient _httpClient;
            private System.Collections.Generic.List<GameVersionInfo> _versions =
                new System.Collections.Generic.List<GameVersionInfo>();
            private GameVersionInfo _selectedVersion;
            private Vector2 _listScroll = Vector2.zero;
            private Vector2 _notesScroll = Vector2.zero;
            private bool _versionsLoading = true;
            private bool _isDownloading;
            private double _downloadProgress;
            private string _statusMessage = "Checking releases...";
            private bool _refreshQueued;
            private int _pendingInstall = -1; // index into _versions

            private readonly LegacyVersionInstaller _installer;

            public VersionsPanel(Core.Logger log) : base(log)
            {
                _installer = new LegacyVersionInstaller(LegacyVersionInstaller.DownloadHttp, log);
            }

            public override void Tick()
            {
                if (_versionService == null)
                {
                    _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                    _versionService = new GameVersionService(Log, _httpClient);
                    _refreshQueued = true;
                }
                if (_refreshQueued)
                {
                    _refreshQueued = false;
                    _ = RefreshVersions();
                }
                if (_pendingInstall >= 0 && _pendingInstall < _versions.Count)
                {
                    var v = _versions[_pendingInstall];
                    _pendingInstall = -1;
                    _ = InstallVersion(v);
                }
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
                    Log?.Warn(_statusMessage);
                }
                finally
                {
                    _versionsLoading = false;
                }
            }

            public override void OnGUI(PanelUI ui, Rect panelRect)
            {
                DrawHeader(ui, panelRect, "VERSION MANAGER");
                if (CloseRequested) return;

                float bodyTop = HeaderH + 12;
                float bodyH = panelRect.height - bodyTop - 56;

                // Version list
                var listRect = new Rect(16, bodyTop, 470, bodyH);
                float rowH = 68f;
                float contentH = 8 + _versions.Count * rowH;
                var list = ui.BeginList(listRect, contentH, ref _listScroll);
                for (int i = 0; i < _versions.Count; i++)
                    DrawVersionRow(ui, new Rect(4, list.y + 4 + i * rowH, list.width - 10, rowH - 6), _versions[i], i);
                if (_versions.Count == 0)
                    ui.Label(new Rect(8, list.y + 8, listRect.width - 16, 40),
                        _versionsLoading ? "Checking releases..." : "No downloadable releases found.", 12, Dim, wrap: true);
                ui.EndList();

                // Notes
                var notesRect = new Rect(502, bodyTop, panelRect.width - 518, bodyH);
                ui.Fill(notesRect, RowBg);
                ui.Frame(notesRect, new Color(0.16f, 0.16f, 0.18f));
                ui.Label(new Rect(notesRect.x + 12, notesRect.y + 8, notesRect.width - 24, 24), "UPDATE NOTES", 13, Accent, bold: true);
                var notes = _selectedVersion?.Body ?? "";
                GUI.BeginGroup(new Rect(notesRect.x + 8, notesRect.y + 36, notesRect.width - 16, notesRect.height - 46));
                float notesH = Mathf.Max(notesRect.height - 70, notes.Length * 14f);
                _notesScroll.y = Mathf.Clamp(_notesScroll.y - (notesRect.Contains(ui.Mouse) ? ui.WheelDelta : 0),
                    0, Mathf.Max(0, notesH - (notesRect.height - 46)));
                ui.Label(new Rect(0, -_notesScroll.y, notesRect.width - 36, notesH), notes, 12, Dim, wrap: true);
                GUI.EndGroup();

                // Footer
                ui.Label(new Rect(18, panelRect.height - 40, panelRect.width - 200, 26),
                    _isDownloading
                        ? $"Downloading {_selectedVersion?.Tag ?? ""}... {(int)(_downloadProgress * 100)}%"
                        : _statusMessage, 12, Dim, wrap: false);

                if (ui.Button(new Rect(panelRect.width - 150, panelRect.height - 50, 132, 34), "REFRESH", 12,
                    BtnBg, BtnHover, Text))
                    _refreshQueued = true;
            }

            private void DrawVersionRow(PanelUI ui, Rect row, GameVersionInfo version, int index)
            {
                bool hover = row.Contains(ui.Mouse);
                if (hover && ui.Clicked) _selectedVersion = version;
                ui.Fill(row, hover ? RowHover : RowBg);
                ui.Frame(row, new Color(0.18f, 0.18f, 0.20f));

                ui.Label(new Rect(row.x + 12, row.y + 8, row.width - 130, 22), version.ListLabel, 14, Text, bold: true);
                bool installed = Core.Paths.TryResolveInstalledVersionExe(version.Tag) != null;
                ui.Label(new Rect(row.x + 12, row.y + 32, row.width - 130, 18),
                    $"{version.Tag}{(installed ? "  •  INSTALLED" : "")}", 11, Dim);

                var actionRect = new Rect(row.x + row.width - 112, row.y + 18, 100, 30);
                var prevBg = BtnBg; var prevHover = BtnHover;
                if (ui.Button(actionRect, installed ? "UNINSTALL" : "INSTALL", 11, prevBg, prevHover, Text) && !_isDownloading)
                {
                    if (installed) UninstallVersion(version);
                    else _pendingInstall = index;
                }
            }

            private async System.Threading.Tasks.Task InstallVersion(GameVersionInfo version)
            {
                _isDownloading = true;
                _downloadProgress = 0;
                _statusMessage = $"Installing {version.Tag}...";
                try
                {
                    var progress = new Progress<double>(p => _downloadProgress = p);
                    await _installer.DownloadAndInstallAsync(version, progress);
                    _statusMessage = $"Installed {version.Tag}.";
                    Log?.Info(_statusMessage);
                }
                catch (Exception ex)
                {
                    _statusMessage = $"Install failed: {ex.Message}";
                    Log?.Error(_statusMessage);
                }
                finally
                {
                    _isDownloading = false;
                }
            }

            private void UninstallVersion(GameVersionInfo version)
            {
                try
                {
                    var safe = (version.Tag ?? "").Trim();
                    foreach (var c in Path.GetInvalidFileNameChars())
                        safe = safe.Replace(c, '_');
                    var dir = Path.Combine(Core.Paths.VersionsDir, safe);
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, true);
                        _statusMessage = $"Uninstalled {version.Tag}.";
                        Log?.Info(_statusMessage);
                    }
                    else
                    {
                        _statusMessage = $"{version.Tag} was not installed.";
                    }
                }
                catch (Exception ex)
                {
                    _statusMessage = $"Uninstall failed: {ex.Message}";
                    Log?.Error(_statusMessage);
                }
            }
        }

        // ------------------------- Skins -------------------------

        public class SkinsPanel : PanelContent
        {
            private string[] _skinPaths = Array.Empty<string>();
            private Vector2 _skinScroll = Vector2.zero;
            private string _selectedSkinPath;
            private string _statusMessage = "";
            private bool _uploadQueued;

            public SkinsPanel(Core.Logger log) : base(log) { }

            public override void Tick()
            {
                if (_uploadQueued)
                {
                    _uploadQueued = false;
                    var filePath = SkinManager.PromptSelectSkinFile();
                    if (!string.IsNullOrEmpty(filePath))
                    {
                        if (SkinManager.ValidateSkinFile(filePath, out _, out _, out var err))
                            _statusMessage = "Skin imported and set as active.";
                        else
                            _statusMessage = $"Invalid skin: {err}";
                    }
                }
            }

            public override void OnGUI(PanelUI ui, Rect panelRect)
            {
                DrawHeader(ui, panelRect, "SKIN LIBRARY");
                if (CloseRequested) return;

                _skinPaths = GetLocalSkinPaths();
                var activeHash = ReadActiveSkinHashSafe();

                var listRect = new Rect(16, HeaderH + 12, 400, panelRect.height - HeaderH - 24);
                float rowH = 74f;
                float contentH = 8 + (_skinPaths.Length + 1) * rowH;
                var list = ui.BeginList(listRect, contentH, ref _skinScroll);

                float rowY = list.y + 4;
                DrawSkinRow(ui, new Rect(4, rowY, list.width - 10, rowH - 6), "DEFAULT SKIN", null,
                    string.IsNullOrWhiteSpace(activeHash), "USE DEFAULT");
                rowY += rowH;
                foreach (var path in _skinPaths)
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    var isActive = string.Equals(name, activeHash, StringComparison.OrdinalIgnoreCase);
                    DrawSkinRow(ui, new Rect(4, rowY, list.width - 10, rowH - 6), name, path, isActive, "USE");
                    rowY += rowH;
                }
                ui.EndList();

                // Right column: preview + actions
                var previewRect = new Rect(432, HeaderH + 12, panelRect.width - 448, panelRect.height - HeaderH - 24);
                ui.Fill(previewRect, RowBg);
                ui.Frame(previewRect, new Color(0.16f, 0.16f, 0.18f));

                var thumb = LoadSkinThumb(_selectedSkinPath);
                if (thumb != null)
                    GUI.DrawTexture(new Rect(previewRect.x + 16, previewRect.y + 16, previewRect.width - 32, previewRect.height - 120), thumb, ScaleMode.ScaleToFit);
                else
                    ui.Label(new Rect(previewRect.x + 16, previewRect.y + 16, previewRect.width - 32, 60),
                        "Select a skin from the list.", 12, Dim, wrap: true);

                if (!string.IsNullOrEmpty(_statusMessage))
                    ui.Label(new Rect(16, panelRect.height - 40, panelRect.width - 32, 26), _statusMessage, 12, Dim);

                if (ui.Button(new Rect(previewRect.x + 16, previewRect.y + previewRect.height - 52, 120, 38), "UPLOAD", 12, BtnBg, BtnHover, Text))
                    _uploadQueued = true;
                if (ui.Button(new Rect(previewRect.x + 148, previewRect.y + previewRect.height - 52, 120, 38), "FOLDER", 12, BtnBg, BtnHover, Text))
                {
                    try { Process.Start("explorer.exe", Core.Paths.UserSkinsDir); } catch { }
                }
            }

            private void DrawSkinRow(PanelUI ui, Rect row, string displayName, string path, bool isActive, string useText)
            {
                bool hover = row.Contains(ui.Mouse);
                if (hover && ui.Clicked && path != null) _selectedSkinPath = path;
                ui.Fill(row, hover ? RowHover : RowBg);
                ui.Frame(row, new Color(0.18f, 0.18f, 0.20f));

                var thumb = LoadSkinThumb(path);
                if (thumb != null)
                    GUI.DrawTexture(new Rect(row.x + 8, row.y + 8, 58, 58), thumb, ScaleMode.ScaleToFit);

                ui.Label(new Rect(row.x + 74, row.y + 8, row.width - 190, 22), displayName, 14, Text, bold: true);
                ui.Label(new Rect(row.x + 74, row.y + 32, row.width - 190, 18), isActive ? "ACTIVE" : "", 11, Accent);

                var useRect = new Rect(row.x + row.width - 106, row.y + 8, 96, 28);
                if (ui.Button(useRect, useText, 11, BtnBg, BtnHover, Text) && !isActive)
                {
                    if (path == null) SkinManager.ClearActiveSkin();
                    else SkinManager.ImportAndSetActiveSkin(path, out _, out _, Path.GetFileNameWithoutExtension(path));
                    _statusMessage = isActive ? "Already active." : $"Applied {displayName}.";
                }

                if (path != null)
                {
                    var removeRect = new Rect(row.x + row.width - 106, row.y + 40, 96, 24);
                    if (ui.Button(removeRect, "REMOVE", 11, BtnBg, BtnHover, Text))
                    {
                        try
                        {
                            File.Delete(path);
                            if (string.Equals(_selectedSkinPath, path)) _selectedSkinPath = null;
                            _statusMessage = $"Removed {displayName}.";
                        }
                        catch { }
                    }
                }
            }

            private static string[] GetLocalSkinPaths()
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

            private static string ReadActiveSkinHashSafe()
            {
                try
                {
                    return File.Exists(Core.Paths.ActiveSkinHashPath)
                        ? File.ReadAllText(Core.Paths.ActiveSkinHashPath).Trim()
                        : string.Empty;
                }
                catch { return string.Empty; }
            }

            private static Texture2D LoadSkinThumb(string path)
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
        }

        // ======================= Win32 plumbing =======================

        /// <summary>All native window creation / input / presentation.</summary>
        private static class PanelWin32
        {
            public static FloatingPanelHost ActiveHost;

            private const string ClassName = "LatticeVeilPanelWindow";

            private const uint WS_POPUP = 0x80000000u;
            private const uint WS_VISIBLE = 0x10000000u;
            private const int WS_EX_TOOLWINDOW = 0x00000080;

            private const int WM_DESTROY = 0x0002;
            private const int WM_PAINT = 0x000F;
            private const int WM_MOUSEMOVE = 0x0200;
            private const int WM_LBUTTONDOWN = 0x0201;
            private const int WM_LBUTTONUP = 0x0202;
            private const int WM_MOUSEWHEEL = 0x020A;
            private const int WM_NCHITTEST = 0x0084;

            private const int HTCLIENT = 1;
            private const int HTCAPTION = 2;

            private const int SRCCOPY = 0x00CC0020;
            private const int DIB_RGB_COLORS = 0;

            private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            private static WndProcDelegate _wndProc; // rooted so the delegate is not collected

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct WNDCLASSW
            {
                public uint style;
                public IntPtr lpfnWndProc;
                public int cbClsExtra;
                public int cbWndExtra;
                public IntPtr hInstance;
                public IntPtr hIcon;
                public IntPtr hCursor;
                public IntPtr hbrBackground;
                [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
                [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct BITMAPINFOHEADER
            {
                public uint biSize;
                public int biWidth;
                public int biHeight; // positive = bottom-up (matches Unity's pixel order)
                public ushort biPlanes;
                public ushort biBitCount;
                public uint biCompression;
                public uint biSizeImage;
                public int biXPelsPerMeter;
                public int biYPelsPerMeter;
                public uint biClrUsed;
                public uint biClrImportant;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct BITMAPINFO
            {
                public BITMAPINFOHEADER bmiHeader;
                public uint bmiColors; // unused for 32bpp BI_RGB
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct RECT { public int Left, Top, Right, Bottom; }

            [StructLayout(LayoutKind.Sequential)]
            private struct POINT { public int X, Y; }

            [DllImport("kernel32.dll")]
            private static extern IntPtr GetModuleHandleW(string lpModuleName);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName,
                uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
                IntPtr hInstance, IntPtr lpParam);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool DestroyWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

            [DllImport("user32.dll")]
            private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

            [DllImport("user32.dll")]
            private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

            [DllImport("user32.dll")]
            private static extern bool GetCursorPos(out POINT lpPoint);

            [DllImport("user32.dll")]
            private static extern IntPtr BeginPaint(IntPtr hWnd, IntPtr lpPaint);

            [DllImport("user32.dll")]
            private static extern bool EndPaint(IntPtr hWnd, IntPtr lpPaint);

            [DllImport("gdi32.dll", SetLastError = true)]
            private static extern int StretchDIBits(IntPtr hdc, int XDest, int YDest, int nDestWidth, int nDestHeight,
                int XSrc, int YSrc, int nSrcWidth, int nSrcHeight, IntPtr lpBits,
                [In] ref BITMAPINFO lpBitsInfo, int iUsage, int dwRop);

            [DllImport("user32.dll")]
            private static extern IntPtr GetDC(IntPtr hWnd);

            [DllImport("user32.dll")]
            private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

            private static int _clickQueued; // 1 when a click is waiting to be consumed
            private static float _wheelAccum;
            private static Vector2 _lastMouse;

            public static IntPtr CreatePanelWindow(IntPtr owner, out int w, out int h, int prefW, int prefH)
            {
                w = prefW; h = prefH;

                if (!_wndClassRegistered)
                {
                    _wndProc = WndProc;
                    var wc = new WNDCLASSW
                    {
                        lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                        hInstance = GetModuleHandleW(null),
                        lpszClassName = ClassName,
                        hbrBackground = IntPtr.Zero,
                    };
                    if (RegisterClassW(ref wc) == 0)
                        return IntPtr.Zero;
                    _wndClassRegistered = true;
                }

                var hwnd = CreateWindowExW(
                    WS_EX_TOOLWINDOW, ClassName, "LatticeVeil",
                    WS_POPUP | WS_VISIBLE,
                    0, 0, prefW, prefH,
                    owner, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);

                if (hwnd != IntPtr.Zero)
                {
                    if (GetClientRect(hwnd, out var cr))
                    {
                        w = cr.Right - cr.Left;
                        h = cr.Bottom - cr.Top;
                    }
                }
                return hwnd;
            }

            public static void DestroyPanelWindow(IntPtr hwnd)
            {
                DestroyWindow(hwnd);
            }

            public static void CenterOverOwner(IntPtr hwnd, IntPtr owner)
            {
                if (!GetWindowRect(owner, out var or)) return;
                if (!GetWindowRect(hwnd, out var pr)) return;

                int pw = pr.Right - pr.Left, ph = pr.Bottom - pr.Top;
                int lw = or.Right - or.Left, lh = or.Bottom - or.Top;
                int x = or.Left + Mathf.Max(0, (lw - pw) / 2);
                int y = or.Top + Mathf.Max(0, (lh - ph) / 2);

                // SWP_NOACTIVATE: don't steal focus from the launcher on open.
                SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 /*NOSIZE*/ | 0x0010 /*NOACTIVATE*/);
            }

            public static void Show(IntPtr hwnd) => ShowWindow(hwnd, 5 /*SW_SHOW*/);

            public static void PollWindowSize(IntPtr hwnd, ref int w, ref int h, RenderTexture rt, Texture2D tex, ref Color32[] pixels)
            {
                if (!GetClientRect(hwnd, out var cr)) return;
                int cw = cr.Right - cr.Left, ch = cr.Bottom - cr.Top;
                if (cw == w && ch == h) return;
                if (cw <= 0 || ch <= 0) return;
                w = cw; h = ch;
                rt.Release();
                rt.width = w; rt.height = h;
                rt.Create();
                tex.Reinitialize(w, h);
                pixels = new Color32[w * h];
            }

            public static void PresentPixels(IntPtr hwnd, IntPtr pixelPtr, int w, int h)
            {
                var bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                bmi.bmiHeader.biWidth = w;
                bmi.bmiHeader.biHeight = h; // bottom-up
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = 0; // BI_RGB

                var hdc = GetDC(hwnd);
                StretchDIBits(hdc, 0, 0, w, h, 0, 0, w, h, pixelPtr, ref bmi, DIB_RGB_COLORS, SRCCOPY);
                ReleaseDC(hwnd, hdc);
            }

            public static void GetInput(out Vector2 mouse, out bool clicked, out float wheelDelta)
            {
                if (GetCursorPos(out var p) && ActiveHost != null && ActiveHost._hwnd != IntPtr.Zero)
                {
                    var pt = new POINT { X = p.X, Y = p.Y };
                    ScreenToClient(ActiveHost._hwnd, ref pt);
                    _lastMouse = new Vector2(pt.X, pt.Y);
                }
                mouse = _lastMouse;
                clicked = System.Threading.Interlocked.Exchange(ref _clickQueued, 0) == 1;
                wheelDelta = _wheelAccum;
            }

            public static void ConsumeWheel() => _wheelAccum = 0f;

            private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
            {
                switch (msg)
                {
                    case WM_MOUSEMOVE:
                        break;

                    case WM_LBUTTONDOWN:
                        System.Threading.Interlocked.Exchange(ref _clickQueued, 1);
                        return IntPtr.Zero;

                    case WM_LBUTTONUP:
                        return IntPtr.Zero;

                    case WM_MOUSEWHEEL:
                        short delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                        _wheelAccum += delta / 120f * 40f; // one notch = 40px
                        return IntPtr.Zero;

                    case WM_NCHITTEST:
                        // Header strip (minus the close button) drags the window;
                        // everything else is client area for our own controls.
                        var def = DefWindowProcW(hWnd, msg, wParam, lParam);
                        if (def == (IntPtr)HTCLIENT && ActiveHost != null)
                        {
                            var lp = new POINT { X = (short)(lParam.ToInt64() & 0xFFFF), Y = (short)((lParam.ToInt64() >> 16) & 0xFFFF) };
                            ScreenToClient(hWnd, ref lp);
                            if (lp.Y >= 0 && lp.Y < 42 && lp.X < 10000)
                            {
                                var closeRect = new Rect(ActiveHost._w - 46, 5, 38, 32);
                                if (!closeRect.Contains(new Vector2(lp.X, lp.Y)))
                                    return (IntPtr)HTCAPTION;
                            }
                        }
                        return def;

                    case WM_PAINT:
                        BeginPaint(hWnd, _psBuffer);
                        EndPaint(hWnd, _psBuffer);
                        return IntPtr.Zero;

                    case WM_DESTROY:
                        if (ActiveHost != null && ActiveHost._hwnd == hWnd)
                            ActiveHost._hwnd = IntPtr.Zero; // panel closed via the OS
                        return IntPtr.Zero;
                }
                return DefWindowProcW(hWnd, msg, wParam, lParam);
            }

            private static IntPtr _psBuffer = Marshal.AllocHGlobal(96);
        }
    }
}
