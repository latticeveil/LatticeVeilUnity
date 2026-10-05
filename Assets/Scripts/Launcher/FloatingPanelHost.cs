using System;
using System.Collections.Generic;
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
    /// Rendering: the panel is drawn with pure GDI into the window's device
    /// context (double-buffered through a memory DC) from the launcher's own
    /// frame loop — no dependency on Unity's render pipeline. Input (mouse
    /// move / click / wheel) arrives through a real WndProc; the header strip
    /// doubles as a native drag handle (HTCAPTION).
    /// </summary>
    public class FloatingPanelHost : MonoBehaviour
    {
        private static FloatingPanelHost _active;

        private FloatingPanelKind _kind;
        private Core.Logger _log;
        private PanelContent _content;
        private IntPtr _hwnd = IntPtr.Zero;
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

        /// <summary>Tears down the native window and GDI resources.</summary>
        public void ClosePanel()
        {
            if (_destroying) return;
            _destroying = true;

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

        // ------------------------- frame loop -------------------------

        private void Update()
        {
            if (_hwnd == IntPtr.Zero) return;

            // Per-frame content tick (kick off async work etc.).
            _content.Tick();

            PanelWin32.DrawFrame(this);

            if (_content.ConsumeClose())
            {
                ClosePanel();
                Destroy(gameObject);
            }
        }

        // ======================= tiny UI toolkit =======================

        /// <summary>Input + GDI draw helpers for panel content. Hit-testing
        /// uses real Win32 input harvested by the WndProc.</summary>
        public class PanelUI
        {
            public readonly Vector2 Mouse;
            public readonly bool Clicked;
            public float WheelDelta; // consumed via ConsumeWheel() so scroll and zoom stay exclusive
            internal IntPtr Hdc;

            public PanelUI(Vector2 mouse, bool clicked, float wheelDelta, IntPtr hdc)
            {
                Mouse = mouse;
                Clicked = clicked;
                WheelDelta = wheelDelta;
                Hdc = hdc;
            }

            public void Fill(Rect r, Color c) => PanelWin32.FillRect(Hdc, r, c);

            /// <summary>Wheel motion not yet consumed by another widget (scroll vs zoom are exclusive).</summary>
            public float ConsumeWheel()
            {
                var v = WheelDelta;
                WheelDelta = 0f;
                return v;
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
                PanelWin32.DrawTextString(Hdc, r, text ?? "", size, bold, color, anchor, wrap);
            }

            /// <summary>Measured height (px) a wrapped label needs for the given width.</summary>
            public static float MeasureWrappedTextHeight(string text, float width, int size)
                => PanelWin32.MeasureTextHeight(text, width, size);

            /// <summary>Draws a button; returns true on click this frame.</summary>
            public bool Button(Rect r, string text, int size, Color bg, Color hoverBg, Color fg)
            {
                bool hover = r.Contains(Mouse);
                Fill(r, hover ? hoverBg : bg);
                Frame(r, new Color(0.28f, 0.30f, 0.34f));
                Label(r, text, size, fg, bold: true, anchor: TextAnchor.MiddleCenter);
                return Clicked && hover;
            }

            /// <summary>Draws a PNG image scaled into the rect. Returns false when unavailable.</summary>
            public bool Image(Rect r, string imagePath) => PanelWin32.DrawImage(Hdc, r, imagePath);

            /// <summary>
            /// Clipped list viewport with wheel scrolling. Returns the offset
            /// rows should be drawn at (-scroll). Pair with EndList.
            /// </summary>
            public Rect BeginList(Rect viewport, float contentHeight, ref Vector2 scroll)
            {
                if (viewport.Contains(Mouse))
                    scroll.y = Mathf.Clamp(scroll.y - ConsumeWheel(), 0, Mathf.Max(0, contentHeight - viewport.height));
                scroll.y = Mathf.Clamp(scroll.y, 0, Mathf.Max(0, contentHeight - viewport.height));

                Fill(viewport, new Color(0.09f, 0.09f, 0.10f, 1f));
                Frame(viewport, new Color(0.16f, 0.16f, 0.18f));

                PanelWin32.PushClip(Hdc, viewport);

                // Scrollbar indicator
                if (contentHeight > viewport.height)
                {
                    float barH = Mathf.Max(24, viewport.height * viewport.height / contentHeight);
                    float barY = (viewport.height - barH) * (scroll.y / (contentHeight - viewport.height));
                    Fill(new Rect(viewport.width - 5, barY, 3, barH), new Color(0.35f, 0.36f, 0.4f));
                }
                return new Rect(viewport.x, viewport.y - scroll.y, viewport.width, viewport.y - scroll.y + contentHeight);
            }

            public void EndList() => PanelWin32.PopClip(Hdc);
        }

        // ======================= panel content =======================

        public abstract class PanelContent
        {
            protected Core.Logger Log;
            private bool _closeRequested;

            protected PanelContent(Core.Logger log) { Log = log; }

            public abstract void OnGUI(PanelUI ui, Rect panelRect);

            /// <summary>Called each frame before content drawing (e.g. kick off async work).</summary>
            public virtual void Tick() { }

            public virtual void Shutdown() { }

            public void RequestClose() => _closeRequested = true;
            public bool ConsumeClose() { var v = _closeRequested; _closeRequested = false; return v; }

            // Shared colors
            protected static readonly Color Bg = new Color(0.07f, 0.07f, 0.08f);
            protected static readonly Color HeaderBg = new Color(0.10f, 0.10f, 0.11f);
            protected static readonly Color Accent = new Color(1f, 0.82f, 0.45f);
            protected static readonly Color Text = new Color(0.95f, 0.95f, 0.95f);
            protected static readonly Color Dim = new Color(0.70f, 0.70f, 0.70f);
            protected static readonly Color RowBg = new Color(0.11f, 0.11f, 0.12f);
            protected static readonly Color RowHover = new Color(0.15f, 0.15f, 0.17f);
            protected static readonly Color BtnBg = new Color(0.18f, 0.19f, 0.22f);
            protected static readonly Color BtnHover = new Color(0.26f, 0.28f, 0.32f);
            protected static readonly Color OnlineGreen = new Color(0.36f, 0.80f, 0.44f);
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
                    RequestClose();
            }
        }

        // ------------------------- Versions -------------------------

        public class VersionsPanel : PanelContent
        {
            private GameVersionService _versionService;
            private HttpClient _httpClient;
            private readonly LegacyVersionInstaller _installer;
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

                var notesViewport = new Rect(notesRect.x + 8, notesRect.y + 36, notesRect.width - 16, notesRect.height - 44);
                var notes = _selectedVersion?.Body ?? "";
                float measured = PanelUI.MeasureWrappedTextHeight(notes, notesViewport.width - 8, 12);
                float notesH = Mathf.Max(notesViewport.height, measured);
                _notesScroll.y = Mathf.Clamp(_notesScroll.y - (notesViewport.Contains(ui.Mouse) ? ui.ConsumeWheel() : 0),
                    0, Mathf.Max(0, notesH - notesViewport.height));
                _notesScroll.y = Mathf.Clamp(_notesScroll.y, 0, Mathf.Max(0, notesH - notesViewport.height));
                ui.Fill(notesViewport, RowBg);
                PanelWin32.PushClip(ui.Hdc, notesViewport);
                ui.Label(new Rect(notesViewport.x + 4, notesViewport.y - _notesScroll.y, notesViewport.width - 8, notesH),
                    notes, 12, Dim, wrap: true);
                PanelWin32.PopClip(ui.Hdc);

                // Footer
                ui.Label(new Rect(18, panelRect.height - 40, panelRect.width - 200, 26),
                    _isDownloading
                        ? $"Downloading {_selectedVersion?.Tag ?? ""}... {(int)(_downloadProgress * 100)}%"
                        : _statusMessage, 12, Dim);

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
                    $"{version.Tag}{(installed ? "  -  INSTALLED" : "")}", 11, Dim);

                var actionRect = new Rect(row.x + row.width - 112, row.y + 18, 100, 30);
                if (ui.Button(actionRect, installed ? "UNINSTALL" : "INSTALL", 11, BtnBg, BtnHover, Text) && !_isDownloading)
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
            // --- interactive 3D preview state (MonoGame parity) ---
            private float _yaw = 0.42f;       // matches MonoGame default
            private float _pitch = 0f;
            private float _zoom = 1f;         // generator-relative; clamped 0.72..1.45 in DrawPreview
            private bool _layers = true;
            private string[] _skinPaths = Array.Empty<string>();
            private Vector2 _skinScroll = Vector2.zero;
            private Vector2 _onlineScroll = Vector2.zero;
            private int _skinTab;             // 0 = LOCAL, 1 = ONLINE
            private string _selectedSkinPath;
            private string _statusMessage = "";
            private bool _uploadQueued;

            // Last baked pose; the renderer only runs when this differs.
            private string _bakedKey;
            private bool _lastBakedLayers = true;
            private float _lastBakedZoom = -1f, _lastBakedYaw = float.NaN, _lastBakedPitch = float.NaN;
            private float _lastBakeTime;

            // --- baked bitmap cache (CPU-rendered 3D preview -> GDI) ---
            private class PreviewBitmap
            {
                public byte[] Pixels;      // BGRA, top-down, generator-sized
                public int Width, Height;
                public string SkinPath;    // identity of the baked source
                public bool HasContent;    // false until the first async bake lands
            }

            // --- async bake: render on a worker, publish in Tick, blit in paint ---
            private volatile bool _bakeInFlight;
            private volatile PreviewBitmap _pendingPublish;   // set by the worker
            private byte[] _spareBgra;      // finished buffer handed back for reuse
            private Color[] _workerPreview; // worker-owned scratch, reused per bake
            private bool _bakeFailureLogged;

            // --- Veilnet online-skin sync ---
            private SupabaseSkinClient _syncClient;
            private bool _onlineFetchQueued = true;          // check once when the panel opens
            private volatile OnlineResult _pendingOnline;    // worker -> Tick
            private volatile UploadResult _pendingUpload;    // worker -> Tick
            private volatile bool _syncBusy;
            private bool _syncCheckRequested;                // SYNC button was pressed
            private bool _syncDialogOpen;                    // local-vs-online mismatch prompt
            private byte[] _onlinePngBytes;                  // bytes of the fetched online skin
            private string _onlineHash = "";                 // hash of the uploaded skin ("" = default/none)
            private bool _onlineIsDefault;                   // online profile has the baseline skin
            private string _onlinePngPath;                   // cached PNG for the ONLINE row thumbnail
            private string _onlineName = "ONLINE SKIN";
            private readonly Dictionary<string, string> _hashCache = new Dictionary<string, string>();
            private bool _activeSelectionDone;

            private class OnlineResult
            {
                public bool Success, HasSkin;
                public string Hash, DisplayName, Error;
                public byte[] PngBytes;
            }

            private class UploadResult
            {
                public bool Ok;
                public string Hash, Error;
            }

            private static readonly Dictionary<string, PreviewBitmap> _previewCache =
                new Dictionary<string, PreviewBitmap>();

            public SkinsPanel(Core.Logger log) : base(log) { }

            public override void Tick()
            {
                // Swap in the newest finished bake (written by the worker thread)
                // before this frame's DrawFrame blits it. Runs on the main thread,
                // so the handoff needs no locking beyond the volatile publish.
                var published = _pendingPublish;
                if (published != null)
                {
                    _pendingPublish = null;
                    var bmp = GetOrCreatePreviewBitmap(published.SkinPath);
                    if (bmp != null)
                    {
                        _spareBgra = bmp.Pixels; // old frame buffer becomes the next worker target
                        bmp.Pixels = published.Pixels;
                        bmp.Width = published.Width;
                        bmp.Height = published.Height;
                        bmp.SkinPath = published.SkinPath;
                        bmp.HasContent = true;
                    }
                    else
                    {
                        _spareBgra = published.Pixels;
                    }
                }

                if (_uploadQueued)
                {
                    _uploadQueued = false;
                    var filePath = SkinManager.PromptSelectSkinFile();
                    if (!string.IsNullOrEmpty(filePath))
                    {
                        if (SkinManager.ImportAndSetActiveSkin(filePath, out _, out var importedHash, Path.GetFileNameWithoutExtension(filePath)))
                        {
                            _statusMessage = "Skin imported. Uploading to Veilnet…";
                            QueueUploadActiveSkin(importedHash, filePath);
                        }
                        else
                            _statusMessage = $"Invalid skin: could not import.";
                    }
                }

                // Consume worker results (online fetch / upload) and kick the
                // one-time open fetch. All main-thread.
                var online = _pendingOnline;
                if (online != null)
                {
                    _pendingOnline = null;
                    ConsumeOnlineResult(online);
                }
                var uploaded = _pendingUpload;
                if (uploaded != null)
                {
                    _pendingUpload = null;
                    ConsumeUploadResult(uploaded);
                }
                if (_onlineFetchQueued)
                    QueueOnlineFetch();
            }

            // ---------------- Veilnet online-skin sync ----------------

            private void QueueOnlineFetch()
            {
                _onlineFetchQueued = false;
                var auth = VeilnetSession.TryRead();
                if (auth == null)
                {
                    if (_syncCheckRequested)
                    {
                        _syncCheckRequested = false;
                        _statusMessage = "Log in to Veilnet to sync your online skin.";
                    }
                    return;
                }
                if (_syncBusy) { _onlineFetchQueued = true; return; }

                _syncBusy = true;
                var client = _syncClient ?? (_syncClient = VeilnetSession.CreateClient(auth));
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    SupabaseSkinClient.FetchResult r;
                    try { r = client.FetchSkinAsync().GetAwaiter().GetResult(); }
                    catch (Exception ex) { r = new SupabaseSkinClient.FetchResult { Success = false, Error = ex.Message }; }
                    _pendingOnline = new OnlineResult
                    {
                        Success = r.Success,
                        HasSkin = r.HasSkin,
                        Hash = r.Hash,
                        DisplayName = r.DisplayName,
                        Error = r.Error,
                        PngBytes = r.PngBytes,
                    };
                    _syncBusy = false;
                });
            }

            private void ConsumeOnlineResult(OnlineResult r)
            {
                if (!r.Success)
                {
                    if (_syncCheckRequested) _statusMessage = $"Sync check failed: {r.Error}";
                    _syncCheckRequested = false;
                    return;
                }

                if (!r.HasSkin || r.PngBytes == null || r.PngBytes.Length == 0)
                {
                    _onlineIsDefault = true;
                    _onlineHash = "";
                    _onlinePngPath = null;
                    _onlinePngBytes = null;
                }
                else
                {
                    _onlineIsDefault = false;
                    _onlineHash = (r.Hash ?? string.Empty).Trim().ToLowerInvariant();
                    _onlinePngBytes = r.PngBytes;
                    try
                    {
                        Directory.CreateDirectory(Core.Paths.RuntimeSkinsDir);
                        _onlinePngPath = Path.Combine(Core.Paths.RuntimeSkinsDir, "Veilnet_online.png");
                        File.WriteAllBytes(_onlinePngPath, r.PngBytes);
                    }
                    catch { _onlinePngPath = null; }
                    _onlineName = string.IsNullOrWhiteSpace(r.DisplayName) ? "ONLINE SKIN" : r.DisplayName.Trim().ToUpperInvariant();
                }

                if (_syncCheckRequested)
                {
                    _syncCheckRequested = false;
                    EvaluateSync();
                }
            }

            /// <summary>Compares the local ACTIVE skin against the fetched online skin.</summary>
            private void EvaluateSync()
            {
                var activeHash = ReadActiveSkinHashSafe();
                bool localDefault = string.IsNullOrWhiteSpace(activeHash)
                    || string.Equals(activeHash, "default_skin", StringComparison.OrdinalIgnoreCase);

                if (_onlineIsDefault)
                {
                    if (localDefault)
                        _statusMessage = "In sync: the default skin is online and active.";
                    else
                        _syncDialogOpen = true; // online default vs local custom
                }
                else if (localDefault)
                {
                    _syncDialogOpen = true; // local default vs online custom
                }
                else if (string.Equals(activeHash, _onlineHash, StringComparison.OrdinalIgnoreCase))
                {
                    _statusMessage = "In sync: local skin matches the uploaded skin.";
                }
                else
                {
                    _syncDialogOpen = true;
                }
            }

            /// <summary>"USE LOCAL": uploads the local active skin to Veilnet.</summary>
            private void ApplyLocalAsLatest(bool localIsUsable)
            {
                if (!localIsUsable)
                {
                    _statusMessage = "The default skin is active locally; nothing to upload.";
                    return;
                }

                var activeHash = ReadActiveSkinHashSafe();
                string localPath = null;
                var candidates = new[]
                {
                    Path.Combine(Core.Paths.UserSkinsDir, $"{activeHash}.png"),
                    Path.Combine(Core.Paths.RuntimeSkinsDir, $"{activeHash}.png"),
                };
                foreach (var c in candidates) if (File.Exists(c)) { localPath = c; break; }
                if (localPath == null)
                {
                    _statusMessage = $"Local skin file for the active hash was not found.";
                    return;
                }

                var auth = VeilnetSession.TryRead();
                if (auth == null) { _statusMessage = "Log in to Veilnet to upload."; return; }

                byte[] png;
                try { png = File.ReadAllBytes(localPath); }
                catch (Exception ex) { _statusMessage = $"Could not read local skin: {ex.Message}"; return; }

                _statusMessage = "Uploading local skin to Veilnet…";
                QueueUpload(auth, activeHash, png);
            }            /// <summary>"USE ONLINE": downloads/applies the online skin locally.</summary>
            private void ApplyOnlineAsLatest()
            {
                if (_onlineIsDefault)
                {
                    SkinManager.ClearActiveSkin();
                    SelectDefaultPreview();
                    _statusMessage = "Switched to the default (online) skin.";
                    return;
                }
                if (_onlinePngBytes == null || _onlinePngBytes.Length == 0)
                {
                    _statusMessage = "Online skin data unavailable; press SYNC to re-fetch.";
                    return;
                }

                // Already in the local library? Apply that copy instead of
                // adding a duplicate entry.
                var existing = FindLibrarySkinByHash(_onlineHash);
                if (existing != null)
                {
                    if (SkinManager.ImportAndSetActiveSkin(existing, out var reuseError, out _, Path.GetFileNameWithoutExtension(existing)))
                    {
                        _selectedSkinPath = existing;
                        _statusMessage = "Online skin already downloaded — applied the local copy.";
                    }
                    else
                        _statusMessage = $"Could not apply skin: {reuseError}";
                    return;
                }

                try
                {
                    Directory.CreateDirectory(Core.Paths.UserSkinsDir);
                    var tempPath = Path.Combine(Core.Paths.UserSkinsDir, ".temp_veilnet_skin.png");
                    try
                    {
                        File.WriteAllBytes(tempPath, _onlinePngBytes);
                        if (SkinManager.ImportAndSetActiveSkin(tempPath, out var importError, out _, "Veilnet_skin"))
                        {
                            _selectedSkinPath = Path.Combine(Core.Paths.UserSkinsDir, "Veilnet_skin.png");
                            _statusMessage = "Downloaded the online skin locally and applied it.";
                        }
                        else
                            _statusMessage = $"Failed to import online skin: {importError}";
                    }
                    finally
                    {
                        try { File.Delete(tempPath); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    _statusMessage = $"Failed to apply online skin: {ex.Message}";
                }
            }

            /// <summary>Library (Skins folder) path whose hash matches, or null.</summary>
            private string FindLibrarySkinByHash(string hash)
            {
                if (string.IsNullOrEmpty(hash)) return null;
                foreach (var p in _skinPaths)
                    if (string.Equals(GetSkinHashCached(p), hash, StringComparison.OrdinalIgnoreCase))
                        return p;
                return null;
            }

            /// <summary>
            /// REMOVE on the ONLINE row: saves the account skin into the local
            /// library (never overwriting a different skin under the same name)
            /// so it becomes a normal local copy. Active skin and the runtime
            /// cache are left untouched; the row then shows as a local entry.
            /// </summary>
            private void KeepOnlineSkinAsLocalCopy()
            {
                try
                {
                    if (_onlinePngBytes == null || _onlinePngBytes.Length == 0)
                    {
                        if (_onlinePngPath != null && File.Exists(_onlinePngPath))
                            _onlinePngBytes = File.ReadAllBytes(_onlinePngPath);
                    }
                    if (_onlinePngBytes == null || _onlinePngBytes.Length == 0)
                    {
                        _statusMessage = "Online skin data unavailable; press SYNC to re-fetch.";
                        return;
                    }

                    Directory.CreateDirectory(Core.Paths.UserSkinsDir);
                    var dest = Path.Combine(Core.Paths.UserSkinsDir, "Veilnet_skin.png");
                    for (int i = 2; File.Exists(dest)
                         && !string.Equals(GetSkinHashCached(dest), _onlineHash, StringComparison.OrdinalIgnoreCase); i++)
                        dest = Path.Combine(Core.Paths.UserSkinsDir, $"Veilnet_skin_{i}.png");

                    File.WriteAllBytes(dest, _onlinePngBytes);
                    _selectedSkinPath = dest;
                    _statusMessage = "Kept the online skin as a local copy.";
                }
                catch (Exception ex)
                {
                    _statusMessage = $"Could not save the online skin locally: {ex.Message}";
                }
            }

            /// <summary>Uploads the just-imported skin to Veilnet in the background (UPLOAD button).</summary>
            private void QueueUploadActiveSkin(string hash, string importedPath)
            {
                var auth = VeilnetSession.TryRead();
                if (auth == null)
                {
                    _statusMessage = "Imported locally. Log in to Veilnet to upload it.";
                    return;
                }
                if (string.IsNullOrEmpty(hash))
                {
                    if (!SkinManager.ValidateSkinFile(importedPath, out var tex, out hash, out _))
                    {
                        _statusMessage = "Imported locally, but the skin could not be hashed for upload.";
                        if (tex != null) Destroy(tex);
                        return;
                    }
                    if (tex != null) Destroy(tex);
                }

                byte[] png;
                try { png = File.ReadAllBytes(importedPath); }
                catch (Exception ex) { _statusMessage = $"Imported locally, but upload failed: {ex.Message}"; return; }

                QueueUpload(auth, hash, png);
            }

            private void QueueUpload(VeilnetSession.Auth auth, string hash, byte[] png)
            {
                if (_syncBusy) { _statusMessage = "Another sync operation is running; try again in a moment."; return; }
                _syncBusy = true;
                var client = _syncClient ?? (_syncClient = VeilnetSession.CreateClient(auth));
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    var (ok, error) = client.UploadSkinAsync(hash, png).GetAwaiter().GetResult();
                    _pendingUpload = new UploadResult { Ok = ok, Error = error, Hash = hash };
                    _syncBusy = false;
                });
            }

            private void ConsumeUploadResult(UploadResult r)
            {
                if (r.Ok)
                {
                    SkinManager.MarkSkinUploaded(r.Hash);
                    _onlineHash = (r.Hash ?? string.Empty).ToLowerInvariant();
                    _onlineIsDefault = false;
                    _statusMessage = "Uploaded — local and online skins now match.";
                }
                else
                {
                    _statusMessage = $"Upload failed: {r.Error}";
                }
            }

            /// <summary>Resolves the local file (or ONLINE row) for the active skin hash.</summary>
            private string FindActiveSkinPath(string activeHash)
            {
                if (string.IsNullOrWhiteSpace(activeHash) || string.Equals(activeHash, "default_skin", StringComparison.OrdinalIgnoreCase))
                    return null; // default skin: panel preview shows its default
                try
                {
                    if (_onlinePngPath != null && File.Exists(_onlinePngPath)
                        && string.Equals(activeHash, _onlineHash, StringComparison.OrdinalIgnoreCase))
                        return _onlinePngPath;

                    var candidates = new[]
                    {
                        Path.Combine(Core.Paths.RuntimeSkinsDir, activeHash + ".png"),
                        Path.Combine(Core.Paths.UserSkinsDir, activeHash + ".png"),
                    };
                    foreach (var c in candidates) if (File.Exists(c)) return c;

                    foreach (var p in _skinPaths)
                        if (string.Equals(GetSkinHashCached(p), activeHash, StringComparison.OrdinalIgnoreCase))
                            return p;
                }
                catch { }
                return null;
            }

            /// <summary>SHA-256 of a skin file, computed once per path (the list redraws every frame).</summary>
            private string GetSkinHashCached(string path)
            {
                if (string.IsNullOrEmpty(path)) return null;
                if (_hashCache.TryGetValue(path, out var h)) return h;

                h = "";
                if (SkinManager.ValidateSkinFile(path, out var tex, out var hash, out _))
                {
                    h = hash;
                    if (tex != null) Destroy(tex); // ValidateSkinFile decodes a Texture2D we don't need
                }
                _hashCache[path] = h;
                return h;
            }

            private static string ReadUploadedHashSafe()
            {
                try
                {
                    return File.Exists(SkinManager.UploadedSkinHashPath)
                        ? File.ReadAllText(SkinManager.UploadedSkinHashPath).Trim()
                        : string.Empty;
                }
                catch { return string.Empty; }
            }

            public override void OnGUI(PanelUI ui, Rect panelRect)
            {
                DrawHeader(ui, panelRect, "SKIN LIBRARY");

                _skinPaths = GetLocalSkinPaths();
                var activeHash = ReadActiveSkinHashSafe();

                // Reopening the panel starts with the ACTIVE skin in the preview
                // (the ONLINE row's copy when the active skin came from Veilnet).
                if (!_activeSelectionDone)
                {
                    _activeSelectionDone = true;
                    _selectedSkinPath = FindActiveSkinPath(activeHash);
                }

                // LOCAL / ONLINE tabs keep the two skin sources separate.
                var tabRect = new Rect(16, HeaderH + 12, 400, 30);
                bool onlineTab = DrawSkinTabs(ui, tabRect);
                var listRect = new Rect(16, HeaderH + 46, 400, panelRect.height - HeaderH - 24 - 34);
                float rowH = 74f;

                string uploadedMarker = ReadUploadedHashSafe();
                if (onlineTab)
                {
                    DrawOnlineTab(ui, listRect, activeHash, rowH);
                }
                else
                {
                    float contentH = 8 + (_skinPaths.Length + 1) * rowH;
                    var list = ui.BeginList(listRect, contentH, ref _skinScroll);

                    bool defaultUploaded = _onlineIsDefault
                        || string.Equals(_onlineHash, "default_skin", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(uploadedMarker, "default_skin", StringComparison.OrdinalIgnoreCase);

                    float rowY = list.y + 4;
                    DrawSkinRow(ui, new Rect(4, rowY, list.width - 10, rowH - 6), "DEFAULT SKIN", null,
                        string.IsNullOrWhiteSpace(activeHash), "USE DEFAULT",
                        defaultUploaded ? "UPLOADED" : null);
                    rowY += rowH;

                    bool anyRowActive = false;
                    foreach (var path in _skinPaths)
                    {
                        var name = Path.GetFileNameWithoutExtension(path);
                        // Imported skins keep their friendly filename (hashes live in
                        // active.txt), so a name match is preferred but a direct hash
                        // file still matches too.
                        var rowHash = GetSkinHashCached(path);
                        var isActive = string.Equals(name, activeHash, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(rowHash, activeHash, StringComparison.OrdinalIgnoreCase);
                        if (isActive) anyRowActive = true;
                        // Badge: the account skin shows UPLOADED if that library
                        // copy is the one uploaded, DOWNLOADED if it was fetched.
                        string rowBadge = null;
                        if (!string.IsNullOrEmpty(_onlineHash) && string.Equals(rowHash, _onlineHash, StringComparison.OrdinalIgnoreCase))
                            rowBadge = string.Equals(rowHash, uploadedMarker, StringComparison.OrdinalIgnoreCase) ? "UPLOADED" : "DOWNLOADED";
                        else if (string.IsNullOrEmpty(_onlineHash) && !string.IsNullOrEmpty(uploadedMarker) && string.Equals(rowHash, uploadedMarker, StringComparison.OrdinalIgnoreCase))
                            rowBadge = "UPLOADED";
                        DrawSkinRow(ui, new Rect(4, rowY, list.width - 10, rowH - 6), name, path, isActive, "USE",
                            rowBadge);
                        rowY += rowH;
                    }
                    if (!anyRowActive && !string.IsNullOrWhiteSpace(activeHash) && !string.Equals("DEFAULT SKIN", activeHash, StringComparison.OrdinalIgnoreCase))
                        ui.Label(new Rect(list.x + 4, rowY + 4, list.width - 10, 22),
                            $"Active skin hash: {activeHash}", 11, Accent);
                    ui.EndList();
                }

                // Right column: preview + actions
                var previewRect = new Rect(432, HeaderH + 12, panelRect.width - 448, panelRect.height - HeaderH - 24);
                ui.Fill(previewRect, RowBg);
                ui.Frame(previewRect, new Color(0.16f, 0.16f, 0.18f));

                var previewViewport = new Rect(previewRect.x + 16, previewRect.y + 16, previewRect.width - 32, previewRect.height - 84);
                DrawPreview(ui, previewViewport, panelRect);

                if (!string.IsNullOrEmpty(_statusMessage))
                    ui.Label(new Rect(16, panelRect.height - 40, panelRect.width - 32, 26), _statusMessage, 12, Dim);

                var buttonRowY = previewRect.y + previewRect.height - 52;

                // Distribute the buttons across the preview column so they can
                // never be cut off by the window edge at small panel widths.
                const int ButtonCount = 5;
                const float Gap = 10f, Margin = 16f;
                float btnW = (previewRect.width - Margin * 2 - Gap * (ButtonCount - 1)) / ButtonCount;
                if (btnW < 56f) btnW = 56f;
                float totalW = ButtonCount * btnW + (ButtonCount - 1) * Gap;
                float bx = previewRect.x + Mathf.Max(Margin, (previewRect.width - totalW) * 0.5f);
                int btnFont = btnW < 84 ? 9 : 11;
                Rect B(int i) => new Rect(bx + i * (btnW + Gap), buttonRowY, btnW, 38);

                if (ui.Button(B(0), "RECENTER", btnFont, BtnBg, BtnHover, Text))
                {
                    // yaw = 90 => rootYaw = 0 => the model faces the camera.
                    _yaw = 90f; _pitch = 0f; _zoom = 1f;
                }
                if (ui.Button(B(1), _layers ? "LAYERS: ON" : "LAYERS: OFF", btnFont, BtnBg, BtnHover, Text))
                    _layers = !_layers;
                if (ui.Button(B(2), "UPLOAD", btnFont, BtnBg, BtnHover, Text))
                    _uploadQueued = true;
                if (ui.Button(B(3), _syncBusy ? "…" : "SYNC", btnFont, BtnBg, BtnHover, Text))
                {
                    _syncCheckRequested = true;
                    QueueOnlineFetch();
                }
                if (ui.Button(B(4), "FOLDER", btnFont, BtnBg, BtnHover, Text))
                {
                    try { Process.Start("explorer.exe", Core.Paths.UserSkinsDir); } catch { }
                }

                if (_syncDialogOpen)
                    DrawSyncDialog(ui, panelRect);
            }

            /// <summary>"Local and online differ" prompt: the user picks which one is the latest.</summary>
            private void DrawSyncDialog(PanelUI ui, Rect panelRect)
            {
                const float w = 380f, h = 148f;
                var box = new Rect((panelRect.width - w) * 0.5f, (panelRect.height - h) * 0.5f, w, h);
                ui.Fill(box, new Color(0.12f, 0.12f, 0.14f));
                ui.Frame(box, Accent);
                ui.Label(new Rect(box.x + 16, box.y + 14, w - 32, 24), "LOCAL AND ONLINE SKINS DIFFER", 13, Text, bold: true);
                ui.Label(new Rect(box.x + 16, box.y + 42, w - 32, 40),
                    "Which one is the latest? Pick to make local and online match.", 11, Dim);

                bool localIsUsable = !string.IsNullOrWhiteSpace(ReadActiveSkinHashSafe())
                    && !string.Equals(ReadActiveSkinHashSafe(), "default_skin", StringComparison.OrdinalIgnoreCase);

                if (ui.Button(new Rect(box.x + 16, box.y + h - 48, (w - 48) / 2, 34), "USE LOCAL (UPLOAD)", 11, BtnBg, BtnHover, Text))
                {
                    _syncDialogOpen = false;
                    ApplyLocalAsLatest(localIsUsable);
                }
                if (ui.Button(new Rect(box.x + 32 + (w - 48) / 2, box.y + h - 48, (w - 48) / 2, 34), "USE ONLINE (DOWNLOAD)", 11, BtnBg, BtnHover, Text))
                {
                    _syncDialogOpen = false;
                    ApplyOnlineAsLatest();
                }
                if (ui.Button(new Rect(box.x + w - 34, box.y + 8, 24, 22), "X", 11, BtnBg, BtnHover, Text))
                    _syncDialogOpen = false;
            }

            /// <summary>
            /// Interactive 3D player preview (MonoGame-parity software renderer).
            /// Drag (started inside the viewport) rotates; wheel zooms; RECENTER/LAYERS controls.
            /// Bakes run on a worker thread (single in-flight, latest pose wins); the paint
            /// path only ever blits the latest finished bitmap, so dragging never blocks.
            /// </summary>
            private void DrawPreview(PanelUI ui, Rect viewport, Rect panelRect)
            {
                if (panelRect.width - 448 < 60 || panelRect.height - HeaderH - 24 < 60)
                    return;

                // Wheel zoom (exclusive against list scrolling via ConsumeWheel).
                if (viewport.Contains(ui.Mouse))
                {
                    var wheel = ui.ConsumeWheel();
                    if (Mathf.Abs(wheel) > 0.01f)
                        _zoom = Mathf.Clamp(_zoom + Mathf.Sign(wheel) * 0.08f, 0.72f, 1.45f);
                }

                // Rotation only when THIS viewport is what the user grabbed:
                // a drag must start inside the preview, otherwise list/header
                // drags would spin the model.
                bool rotateDrag = PanelWin32.IsDragging && PanelWin32.DragStartedIn(viewport);
                if (rotateDrag)
                {
                    _yaw += PanelWin32.DragDeltaX * 0.8f;             // ~0.8 deg per pixel (MonoGame: 0.014 rad)
                    // Screen-drag down should tilt the camera toward the model's
                    // top; the renderer's pitch sign is opposite, so negate.
                    _pitch = Mathf.Clamp(_pitch - PanelWin32.DragDeltaY * 0.8f, -55f, 55f);
                }

                // Bake only when the output would actually differ.
                var skinTex = LoadSkinTexture(_selectedSkinPath);
                if (skinTex != null)
                {
                    var key = _selectedSkinPath ?? "default";
                    bool poseChanged = _layers != _lastBakedLayers || _zoom != _lastBakedZoom ||
                                       !Mathf.Approximately(_yaw, _lastBakedYaw) ||
                                       !Mathf.Approximately(_pitch, _lastBakedPitch) ||
                                       _bakedKey != key;
                    if (poseChanged && !_bakeInFlight)
                    {
                        StartAsyncBake(key, skinTex);
                        _bakedKey = key;
                        _lastBakedLayers = _layers; _lastBakedZoom = _zoom;
                        _lastBakedYaw = _yaw; _lastBakedPitch = _pitch;
                        _lastBakeTime = Time.realtimeSinceStartup;
                    }

                    if (_previewCache.TryGetValue(key, out var cached) && cached?.Pixels != null && cached.HasContent)
                    {
                        // Fit-preserve-aspect blit into the viewport.
                        var drawRect = FitInto(cached.Width, cached.Height, viewport);
                        PanelWin32.DrawPixelsScaled(ui.Hdc, drawRect, cached.Pixels, cached.Width, cached.Height);
                    }
                }
            }

            /// <summary>
            /// Renders the preview on a worker thread so drags stay smooth: the
            /// paint path never blocks on the rasterizer. Skin pixels are decoded
            /// here on the main thread; the render core (RenderPreviewBGRA) only
            /// touches plain arrays and Unity math structs, which are thread-safe.
            /// Single in-flight bake, latest pose wins - if the user keeps
            /// dragging, the next bake picks up wherever the model is now.
            /// </summary>
            private void StartAsyncBake(string key, Texture2D skinTex)
            {
                var bmp = GetOrCreatePreviewBitmap(key);
                if (bmp == null) return;

                var target = _spareBgra;
                _spareBgra = null;
                if (target == null || target.Length < bmp.Width * bmp.Height * 4)
                    target = new byte[bmp.Width * bmp.Height * 4];

                var scratch = _workerPreview ??= new Color[
                    PlayerSkinPreviewGenerator.PreviewWidth * PlayerSkinPreviewGenerator.PreviewHeight];

                // Main-thread Unity API ends here; below is pure C#.
                var skinPixels = skinTex.GetPixels();
                int skinW = skinTex.width, skinH = skinTex.height;
                float yaw = _yaw, pitch = _pitch, zoom = _zoom;
                bool layers = _layers;

                _bakeInFlight = true;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        PlayerSkinPreviewGenerator.RenderPreviewBGRA(
                            skinPixels, skinW, skinH, yaw, pitch, layers, zoom, scratch, target);
                        _pendingPublish = new PreviewBitmap
                        {
                            Pixels = target,
                            Width = PlayerSkinPreviewGenerator.PreviewWidth,
                            Height = PlayerSkinPreviewGenerator.PreviewHeight,
                            SkinPath = key,
                            HasContent = true,
                        };
                    }
                    catch (Exception ex)
                    {
                        if (!_bakeFailureLogged)
                        {
                            _bakeFailureLogged = true;
                            Log?.Warn($"Skin preview async bake failed: {ex.Message}");
                        }
                    }
                    finally
                    {
                        _bakeInFlight = false;
                    }
                });
            }

            /// <summary>Aspect-preserving fit of a w×h image into r.</summary>
            private static Rect FitInto(int w, int h, Rect r)
            {
                float scale = Mathf.Min(r.width / w, r.height / h);
                float dw = w * scale, dh = h * scale;
                return new Rect(r.x + (r.width - dw) * 0.5f, r.y + (r.height - dh) * 0.5f, dw, dh);
            }

            /// <summary>RGBA bytes (Unity GetPixels32 order) → BGRA for GDI, mutated in place.</summary>
            // Skin decode is expensive (disk + SHA + PNG) — cache the Texture2D per path.
            private Texture2D _cachedSkinTex;
            private string _cachedSkinPath;

            private Texture2D LoadSkinTexture(string path)
            {
                if (string.IsNullOrEmpty(path))
                {
                    if (_cachedSkinTex != null && _cachedSkinPath == null) return _cachedSkinTex;
                    if (_cachedSkinTex != null) Destroy(_cachedSkinTex);
                    _cachedSkinTex = DefaultPlayerSkinFactory.CreateTexture();
                    _cachedSkinPath = null;
                    return _cachedSkinTex;
                }
                if (_cachedSkinTex != null && _cachedSkinPath == path) return _cachedSkinTex;
                if (_cachedSkinTex != null) Destroy(_cachedSkinTex);
                _cachedSkinTex = null;
                if (SkinManager.ValidateSkinFile(path, out var tex, out _, out _))
                    _cachedSkinTex = tex;
                _cachedSkinPath = path;
                return _cachedSkinTex;
            }

            /// <summary>Creates/reuses the cache entry for a skin; the worker fills its pixels.</summary>
            private PreviewBitmap GetOrCreatePreviewBitmap(string key)
            {
                if (!_previewCache.TryGetValue(key, out var bmp) || bmp == null)
                {
                    bmp = new PreviewBitmap
                    {
                        Width = PlayerSkinPreviewGenerator.PreviewWidth,
                        Height = PlayerSkinPreviewGenerator.PreviewHeight,
                        SkinPath = key,
                        Pixels = new byte[PlayerSkinPreviewGenerator.PreviewWidth * PlayerSkinPreviewGenerator.PreviewHeight * 4],
                    };
                    _previewCache[key] = bmp;
                }

                return bmp;
            }

            /// <summary>
            /// Points the preview back at the default skin and forces a rebake:
            /// clearing the selection alone is not enough because the bake
            /// trigger only fires when the baked key changes.
            /// </summary>
            private void SelectDefaultPreview()
            {
                _selectedSkinPath = null;
                _bakedKey = null;
                _lastBakedYaw = float.NaN;
                _lastBakedPitch = float.NaN;
            }

            /// <summary>Draws the LOCAL / ONLINE tab strip. Returns true when the ONLINE tab is active.</summary>
            private bool DrawSkinTabs(PanelUI ui, Rect rect)
            {
                float w = (rect.width - 8) * 0.5f;
                var localRect = new Rect(rect.x, rect.y, w, rect.height);
                var onlineRect = new Rect(rect.x + w + 8, rect.y, w, rect.height);
                if (ui.Button(localRect, "LOCAL SKINS", 11, _skinTab == 0 ? Accent : BtnBg, BtnHover, Text))
                    _skinTab = 0;
                if (ui.Button(onlineRect, "ONLINE SKINS", 11, _skinTab == 1 ? Accent : BtnBg, BtnHover, Text))
                    _skinTab = 1;
                return _skinTab == 1;
            }

            /// <summary>
            /// ONLINE tab: the skin currently on the Veilnet account, shown on
            /// its own so it can never be confused with local library copies.
            /// </summary>
            private void DrawOnlineTab(PanelUI ui, Rect listRect, string activeHash, float rowH)
            {
                float contentH = 8 + 2 * rowH;
                var list = ui.BeginList(listRect, contentH, ref _onlineScroll);
                float rowY = list.y + 4;
                var rowRect = new Rect(4, rowY, list.width - 10, rowH - 6);

                if (_onlineIsDefault)
                {
                    DrawSkinRow(ui, rowRect, "DEFAULT SKIN (ONLINE)", null,
                        string.IsNullOrWhiteSpace(activeHash)
                            || string.Equals(activeHash, "default_skin", StringComparison.OrdinalIgnoreCase),
                        "USE DEFAULT", "UPLOADED");
                }
                else if (_onlinePngPath != null)
                {
                    var onlineIsActive = string.Equals(activeHash, _onlineHash, StringComparison.OrdinalIgnoreCase);
                    DrawSkinRow(ui, rowRect, _onlineName, _onlinePngPath, onlineIsActive, "USE", "UPLOADED");
                }
                else
                {
                    ui.Label(new Rect(list.x + 8, list.y + 12, list.width - 16, 44),
                        "No online skin fetched yet.\nPress SYNC to load the skin on your Veilnet account.", 11, Dim);
                }
                ui.EndList();
            }

            private void DrawSkinRow(PanelUI ui, Rect row, string displayName, string path, bool isActive, string useText, string badge = null)
            {
                bool hover = row.Contains(ui.Mouse);
                if (hover && ui.Clicked && path != null) _selectedSkinPath = path;
                bool isOnlineRow = path != null && path == _onlinePngPath;
                ui.Fill(row, hover ? RowHover : RowBg);
                ui.Frame(row, new Color(0.18f, 0.18f, 0.20f));

                if (path != null)
                    ui.Image(new Rect(row.x + 8, row.y + 8, 58, 58), path);

                ui.Label(new Rect(row.x + 74, row.y + 8, row.width - 190, 22), displayName, 14, Text, bold: true);
                var status = isActive ? "ACTIVE" : "";
                if (badge != null)
                    status = string.IsNullOrEmpty(status) ? badge : status + "  ·  " + badge;
                ui.Label(new Rect(row.x + 74, row.y + 32, row.width - 190, 18), status, 11,
                    isActive ? Accent : OnlineGreen);

                var useRect = new Rect(row.x + row.width - 106, row.y + 8, 96, 28);
                if (ui.Button(useRect, useText, 11, BtnBg, BtnHover, Text) && !isActive)
                {
                    if (path == null)
                    {
                        SkinManager.ClearActiveSkin();
                        SelectDefaultPreview();
                        _statusMessage = "Reverted to the default skin.";
                    }
                    else if (isOnlineRow)
                    {
                        // The ONLINE row's USE downloads/applies through the
                        // dedupe-aware path (no duplicate library entries).
                        ApplyOnlineAsLatest();
                    }
                    else
                    {
                        var applied = SkinManager.ImportAndSetActiveSkin(path, out var importError, out _, Path.GetFileNameWithoutExtension(path));
                        _statusMessage = applied ? $"Applied {displayName}." : $"Could not apply skin: {importError}";
                    }
                }

                if (path != null)
                {
                    var removeRect = new Rect(row.x + row.width - 106, row.y + 40, 96, 24);
                    if (ui.Button(removeRect, "REMOVE", 11, BtnBg, BtnHover, Text))
                    {
                        if (isOnlineRow)
                        {
                            // Removing the ONLINE row keeps the skin: it swaps
                            // from the online representation to a normal local
                            // library copy of that online skin.
                            KeepOnlineSkinAsLocalCopy();
                        }
                        else
                        {
                            try
                            {
                                File.Delete(path);
                                if (string.Equals(_selectedSkinPath, path)) _selectedSkinPath = null;
                                _statusMessage = $"Removed {displayName}.";
                            }
                            catch
                            {
                                _statusMessage = $"Could not remove {displayName} (file may be in use).";
                            }
                        }
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
        }

        // ======================= Win32 plumbing =======================

        /// <summary>All native window creation, input, GDI drawing, GDI+ images.</summary>
        private static class PanelWin32
        {
            public static FloatingPanelHost ActiveHost;

            private const string ClassName = "LatticeVeilPanelWindow";

            private const uint WS_POPUP = 0x80000000u;
            private const uint WS_VISIBLE = 0x10000000u;
            private const int WS_EX_TOOLWINDOW = 0x00000080;

            private const int WM_DESTROY = 0x0002;
            private const int WM_PAINT = 0x000F;
            private const int WM_SETCURSOR = 0x0020;
            private const int WM_ERASEBKGND = 0x0014;
            private const int WM_LBUTTONDOWN = 0x0201;
            private const int WM_LBUTTONUP = 0x0202;
            private const int WM_MOUSEMOVE = 0x0200;
            private const int WM_MOUSEWHEEL = 0x020A;
            private const int WM_NCHITTEST = 0x0084;
            private const int WM_CAPTURECHANGED = 0x0215;
            private const int WM_RBUTTONDOWN = 0x0204;
            private const int WM_RBUTTONUP = 0x0205;

            private const int RDW_INVALIDATE = 0x0001;
            private const int RDW_UPDATENOW = 0x0080;
            private const int RDW_NOFRAME = 0x0800;

            [DllImport("user32.dll")]
            private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

            private const int HTCLIENT = 1;
            private const int HTCAPTION = 2;

            private const uint SRCCOPY = 0x00CC0020;

            private const int BI_RGB = 0;
            private const int DIB_RGB_COLORS = 0;
            private const int HORZRES = 8;
            private const int VERTRES = 10;

            [StructLayout(LayoutKind.Sequential)]
            private struct BITMAPINFOHEADER
            {
                public uint biSize;
                public int biWidth;
                public int biHeight;
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

            // ---- drag input state (settled by GetInput) ----
            private static bool _dragging;
            private static int _lastMouseX, _lastDragY;
            private static Vector2 _deltaAccum; // pending mouse-move deltas since the last frame
            /// <summary>Client coords where the current drag began (valid while IsDragging).</summary>
            public static int DragStartX, DragStartY;

            [DllImport("user32.dll", SetLastError = true)]
            private static extern IntPtr SetCapture(IntPtr hWnd);

            [DllImport("user32.dll")]
            private static extern bool ReleaseCapture();

            [DllImport("user32.dll")]
            private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

            [DllImport("user32.dll")]
            private static extern IntPtr SetCursor(IntPtr hCursor);

            // DrawText flags
            private const uint DT_CENTER = 0x1;
            private const uint DT_VCENTER = 0x4;
            private const uint DT_SINGLELINE = 0x20;
            private const uint DT_WORDBREAK = 0x10;
            private const uint DT_EDITCONTROL = 0x2000;
            private const uint DT_NOPREFIX = 0x800;
            private const uint DT_END_ELLIPSIS = 0x8000;
            private const uint DT_CALCRECT = 0x400;

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

            [DllImport("user32.dll")]
            private static extern IntPtr GetDC(IntPtr hWnd);

            [DllImport("user32.dll")]
            private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

            // FillRect is exported by user32 (NOT gdi32) — wrong DLL here used to
            // throw EntryPointNotFoundException from inside WndProc, crashing the player.
            [DllImport("user32.dll")]
            private static extern bool FillRect(IntPtr hdc, ref RECT lprc, IntPtr hbr);

            // ---- GDI ----
            [DllImport("gdi32.dll")]
            private static extern IntPtr CreateSolidBrush(uint crColor);

            [DllImport("gdi32.dll")]
            private static extern bool DeleteObject(IntPtr hObject);

            [DllImport("gdi32.dll")]
            private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

            [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr CreateFontW(int cHeight, int cWidth, int cEscapement, int cOrientation,
                int cWeight, uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet, uint iOutPrecision,
                uint iClipPrecision, uint iQuality, uint iPitchAndFamily, string pszFaceName);

            [DllImport("gdi32.dll")]
            private static extern uint SetTextColor(IntPtr hdc, uint crColor);

            [DllImport("gdi32.dll")]
            private static extern int SetBkMode(IntPtr hdc, int iBkMode);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);

            [DllImport("gdi32.dll")]
            private static extern int SaveDC(IntPtr hdc);

            [DllImport("gdi32.dll")]
            private static extern bool RestoreDC(IntPtr hdc, int nSavedDC);

            [DllImport("gdi32.dll")]
            private static extern bool IntersectClipRect(IntPtr hdc, int left, int top, int right, int bottom);

            [DllImport("gdi32.dll")]
            private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

            [DllImport("gdi32.dll")]
            private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

            [DllImport("gdi32.dll")]
            private static extern bool DeleteDC(IntPtr hdc);

            [DllImport("gdi32.dll")]
            private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
                IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

            [DllImport("gdi32.dll")]
            private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

            [DllImport("gdi32.dll")]
            private static extern int SetDIBitsToDevice(IntPtr hdc, int XDest, int YDest,
                uint dwWidth, uint dhHeight, int XSrc, int YSrc,
                uint uStartScan, uint cScanLines,
                byte[] lpvBits, [In] ref BITMAPINFO lpbmi, uint fuColorUse);

            [DllImport("gdi32.dll")]
            private static extern bool StretchDIBits(IntPtr hdc, int XDest, int YDest, int DestWidth, int DestHeight,
                int XSrc, int YSrc, int SrcWidth, int SrcHeight, byte[] lpBits,
                [In] ref BITMAPINFO lpbmi, uint iUsage, uint dwRop);

            // ---- GDI+ (PNG thumbnails) ----
            [StructLayout(LayoutKind.Sequential)]
            private struct GdiplusStartupInput
            {
                public int GdiplusVersion;
                public IntPtr DebugEventCallback;
                public int SuppressBackgroundThread;
                public int SuppressExternalCodecs;
            }

            [DllImport("gdiplus.dll")]
            private static extern int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, IntPtr output);

            [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
            private static extern int GdipCreateBitmapFromFile(string filename, out IntPtr bitmap);

            [DllImport("gdiplus.dll")]
            private static extern int GdipCreateFromHDC(IntPtr hdc, out IntPtr graphics);

            [DllImport("gdiplus.dll")]
            private static extern int GdipDrawImageRectI(IntPtr graphics, IntPtr image, int x, int y, int width, int height);

            [DllImport("gdiplus.dll")]
            private static extern int GdipSetInterpolationMode(IntPtr graphics, int mode);

            [DllImport("gdiplus.dll")]
            private static extern int GdipDeleteGraphics(IntPtr graphics);

            [DllImport("gdiplus.dll")]
            private static extern int GdipDisposeImage(IntPtr image);

            // ---- state ----
            private static int _clickQueued; // 1 when a click is waiting to be consumed
            private static float _wheelAccum;
            private static Vector2 _lastMouse;

            private static IntPtr _memDc = IntPtr.Zero;
            private static IntPtr _memBmp = IntPtr.Zero;
            private static IntPtr _oldBmp = IntPtr.Zero;
            private static int _memW, _memH;

            private static readonly Dictionary<long, IntPtr> BrushCache = new Dictionary<long, IntPtr>();
            private static readonly Dictionary<long, IntPtr> FontCache = new Dictionary<long, IntPtr>();
            private static readonly Dictionary<string, Tuple<byte[], int, int, DateTime>> ImageCache =
                new Dictionary<string, Tuple<byte[], int, int, DateTime>>(StringComparer.OrdinalIgnoreCase);
            private static IntPtr _gdiplusToken = IntPtr.Zero;
            private static bool _gdiplusReady;

            private static IntPtr _psBuffer = Marshal.AllocHGlobal(96);

            // ---------------- window ----------------

            public static IntPtr CreatePanelWindow(IntPtr owner, out int w, out int h, int prefW, int prefH)
            {
                w = prefW; h = prefH;

                if (_wndProc == null)
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
                ReleaseBuffers();
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

            // ---------------- input ----------------

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
                _wheelAccum = 0f;

                DragDeltaX = _deltaAccum.x;
                DragDeltaY = _deltaAccum.y;
                _deltaAccum = Vector2.zero;
            }

            /// <summary>Pending mouse-motion deltas while a button is held (drag rotation), consumed by GetInput.</summary>
            public static float DragDeltaX;
            /// <summary>Pending vertical drag delta.</summary>
            public static float DragDeltaY;
            /// <summary>True while a mouse button is held down on the panel (drag in progress).</summary>
            public static bool IsDragging => _dragging;

            /// <summary>True when the current drag began inside the given client rect. A drag
            /// started elsewhere (list, header) must not rotate the 3D preview.</summary>
            public static bool DragStartedIn(Rect viewport)
                => viewport.Contains(new Vector2(DragStartX, DragStartY));

            // ---------------- drawing ----------------

            /// <summary>Draws the panel of the active host (double-buffered GDI).</summary>
            public static void DrawFrame(FloatingPanelHost host)
            {
                if (host == null || host._hwnd == IntPtr.Zero) return;

                if (!GetClientRect(host._hwnd, out var cr)) return;
                int w = cr.Right - cr.Left, h = cr.Bottom - cr.Top;
                if (w <= 0 || h <= 0) return;

                EnsureBuffers(host._hwnd, w, h);
                if (_memDc == IntPtr.Zero) return; // buffer setup failed; skip this frame

                GetInput(out var mouse, out var clicked, out var wheelDelta);

                // Clear to panel background (memory DC may hold stale bits).
                FillRect(_memDc, ref cr, BrushFor(new Color(0.07f, 0.07f, 0.08f)));
                SetBkMode(_memDc, 1 /*TRANSPARENT*/);

                var ui = new PanelUI(mouse, clicked, wheelDelta, _memDc);
                host._content.OnGUI(ui, new Rect(0, 0, w, h));

                var hdc = GetDC(host._hwnd);
                BitBlt(hdc, 0, 0, w, h, _memDc, 0, 0, SRCCOPY);
                ReleaseDC(host._hwnd, hdc);
            }

            private static void EnsureBuffers(IntPtr hwnd, int w, int h)
            {
                if (_memDc != IntPtr.Zero && _memW == w && _memH == h) return;

                ReleaseBuffers();

                var windowDc = GetDC(hwnd);
                _memDc = CreateCompatibleDC(windowDc);
                _memBmp = CreateCompatibleBitmap(windowDc, w, h);
                ReleaseDC(hwnd, windowDc);
                _oldBmp = SelectObject(_memDc, _memBmp);
                _memW = w; _memH = h;
            }

            private static void ReleaseBuffers()
            {
                if (_memBmp != IntPtr.Zero && _oldBmp != IntPtr.Zero)
                    SelectObject(_memDc, _oldBmp);
                if (_memBmp != IntPtr.Zero) { DeleteObject(_memBmp); _memBmp = IntPtr.Zero; }
                if (_memDc != IntPtr.Zero) { DeleteDC(_memDc); _memDc = IntPtr.Zero; }
                _memW = _memH = 0;
            }

            private static uint ColorRef(Color c)
                => ((uint)Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255))
                 | ((uint)Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255) << 8)
                 | ((uint)Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255) << 16);

            private static IntPtr BrushFor(Color c)
            {
                long key = ColorRef(c);
                if (!BrushCache.TryGetValue(key, out var brush))
                {
                    brush = CreateSolidBrush((uint)key);
                    BrushCache[key] = brush;
                }
                return brush;
            }

            public static void FillRect(IntPtr hdc, Rect r, Color c)
            {
                var rc = new RECT
                {
                    Left = (int)r.x,
                    Top = (int)r.y,
                    Right = (int)(r.x + r.width),
                    Bottom = (int)(r.y + r.height),
                };
                FillRect(hdc, ref rc, BrushFor(c));
            }

            public static void DrawTextString(IntPtr hdc, Rect r, string text, int size, bool bold,
                Color color, TextAnchor anchor, bool wrap)
            {
                if (string.IsNullOrEmpty(text)) return;

                SelectObject(hdc, FontFor(size, bold));
                SetTextColor(hdc, ColorRef(color));

                uint flags = DT_NOPREFIX;
                if (wrap) flags |= DT_WORDBREAK | DT_EDITCONTROL;
                else flags |= DT_SINGLELINE | DT_END_ELLIPSIS;

                if (anchor == TextAnchor.MiddleCenter || anchor == TextAnchor.UpperCenter || anchor == TextAnchor.LowerCenter)
                    flags |= DT_CENTER;
                if (anchor == TextAnchor.MiddleCenter || anchor == TextAnchor.MiddleLeft || anchor == TextAnchor.MiddleRight)
                    flags |= DT_VCENTER;

                var rc = new RECT
                {
                    Left = (int)r.x,
                    Top = (int)r.y,
                    Right = (int)(r.x + r.width),
                    Bottom = (int)(r.y + r.height),
                };
                DrawTextW(hdc, text, text.Length, ref rc, flags);
            }

            /// <summary>DT_CALCRECT measurement pass mirroring DrawTextString's wrap style.</summary>
            public static int MeasureTextHeight(string text, float width, int size)
            {
                if (string.IsNullOrEmpty(text)) return 0;

                SelectObject(_memDc, FontFor(size, false));
                var rc = new RECT { Left = 0, Top = 0, Right = Mathf.Max(16, (int)width), Bottom = 0 };
                DrawTextW(_memDc, text, text.Length, ref rc,
                    DT_NOPREFIX | DT_WORDBREAK | DT_EDITCONTROL | DT_CALCRECT);
                return rc.Bottom;
            }

            private static IntPtr FontFor(int size, bool bold)
            {
                long key = ((long)size << 8) | (long)(bold ? 1 : 0);
                if (!FontCache.TryGetValue(key, out var font))
                {
                    font = CreateFontW(-size, 0, 0, 0, bold ? 700 : 400, 0, 0, 0,
                        1 /*DEFAULT_CHARSET*/, 0, 0, 5 /*CLEARTYPE_QUALITY*/, 0, "Segoe UI");
                    FontCache[key] = font;
                }
                return font;
            }

            public static void PushClip(IntPtr hdc, Rect r)
            {
                SaveDC(hdc);
                IntersectClipRect(hdc, (int)r.x, (int)r.y, (int)(r.x + r.width), (int)(r.y + r.height));
            }

            public static void PopClip(IntPtr hdc) => RestoreDC(hdc, -1);

            /// <summary>Blits a row-order-agnostic 32bpp BGRA buffer at 1:1. Buffer must be w*h*4 bytes.</summary>
            public static void DrawPixels(IntPtr hdc, Rect r, byte[] bgra, int w, int h, bool topDown)
            {
                if (bgra == null || bgra.Length < w * h * 4 || w <= 0 || h <= 0) return;
                var bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                bmi.bmiHeader.biWidth = w;
                bmi.bmiHeader.biHeight = topDown ? -h : h; // negative = top-down
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = BI_RGB;
                SetDIBitsToDevice(hdc, (int)r.x, (int)r.y, (uint)w, (uint)h,
                    0, 0, 0, (uint)h, bgra, ref bmi, DIB_RGB_COLORS);
            }

            /// <summary>Scales a top-down 32bpp BGRA buffer into the destination rect (bilinear-ish via GDI Stretch).</summary>
            public static void DrawPixelsScaled(IntPtr hdc, Rect r, byte[] bgra, int srcW, int srcH)
            {
                if (bgra == null || bgra.Length < srcW * srcH * 4 || srcW <= 0 || srcH <= 0) return;
                var bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                bmi.bmiHeader.biWidth = srcW;
                bmi.bmiHeader.biHeight = -srcH; // top-down
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = BI_RGB;
                StretchDIBits(hdc, (int)r.x, (int)r.y, (int)r.width, (int)r.height,
                    0, 0, srcW, srcH, bgra, ref bmi, DIB_RGB_COLORS, SRCCOPY);
            }

            public static bool DrawImage(IntPtr hdc, Rect r, string path)
            {
                if (string.IsNullOrEmpty(path) || r.width < 2 || r.height < 2) return false;
                if (!File.Exists(path)) { DropImage(path); return false; }

                DateTime mtime;
                try { mtime = File.GetLastWriteTimeUtc(path); } catch { return false; }

                if (!ImageCache.TryGetValue(path, out var entry) || entry == null || entry.Item4 != mtime)
                {
                    // Decode via Unity instead of GdipCreateBitmapFromFile: GDI+ keeps
                    // the source FILE locked for the lifetime of the bitmap, which made
                    // re-importing/uploading a displayed skin fail with a sharing
                    // violation. Caching decoded BGRA bytes locks nothing.
                    try
                    {
                        byte[] png;
                        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var ms = new MemoryStream())
                        {
                            fs.CopyTo(ms);
                            png = ms.ToArray();
                        }

                        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (!tex.LoadImage(png))
                        {
                            UnityEngine.Object.Destroy(tex);
                            ImageCache.Remove(path);
                            return false;
                        }
                        var pixels = tex.GetPixels32();
                        int w = tex.width, h = tex.height;
                        UnityEngine.Object.Destroy(tex);

                        var bgra = new byte[w * h * 4];
                        for (int y = 0; y < h; y++) // flip: Unity rows are bottom-up, GDI wants top-down
                        {
                            var src = (h - 1 - y) * w;
                            var dst = y * w * 4;
                            for (int x = 0; x < w; x++)
                            {
                                var c = pixels[src + x];
                                int i = dst + x * 4;
                                bgra[i + 0] = c.b; bgra[i + 1] = c.g;
                                bgra[i + 2] = c.r; bgra[i + 3] = c.a;
                            }
                        }

                        entry = Tuple.Create(bgra, w, h, mtime);
                        ImageCache[path] = entry;
                    }
                    catch
                    {
                        ImageCache.Remove(path);
                        return false;
                    }
                }

                DrawPixelsScaled(hdc, r, entry.Item1, entry.Item2, entry.Item3);
                return true;
            }

            private static void DropImage(string path)
            {
                ImageCache.Remove(path);
            }

            public static void DisposeImages()
            {
                ImageCache.Clear();
            }

            private static bool EnsureGdiplus()
            {
                if (_gdiplusReady) return true;
                var input = new GdiplusStartupInput { GdiplusVersion = 1 };
                _gdiplusReady = GdiplusStartup(out _gdiplusToken, ref input, IntPtr.Zero) == 0;
                return _gdiplusReady;
            }

            // ---------------- wndproc ----------------

            private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
            {
                // A managed exception escaping this native-to-managed callback
                // terminates the whole player, so nothing here may throw.
                try
                {
                    return WndProcCore(hWnd, msg, wParam, lParam);
                }
                catch (System.Exception e)
                {
                    UnityEngine.Debug.LogException(e);
                    return DefWindowProcW(hWnd, msg, wParam, lParam);
                }
            }

            private static IntPtr WndProcCore(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
            {
                switch (msg)
                {
                    case WM_LBUTTONDOWN:
                        System.Threading.Interlocked.Exchange(ref _clickQueued, 1);
                        _dragging = true;
                        _lastMouseX = DragStartX = (short)(lParam.ToInt64() & 0xFFFF);
                        _lastDragY = DragStartY = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                        SetCapture(hWnd);
                        return IntPtr.Zero;

                    case WM_RBUTTONDOWN:
                        _dragging = true;
                        _lastMouseX = DragStartX = (short)(lParam.ToInt64() & 0xFFFF);
                        _lastDragY = DragStartY = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                        SetCapture(hWnd);
                        return IntPtr.Zero;

                    case WM_MOUSEMOVE:
                        if (_dragging)
                        {
                            int x = (short)(lParam.ToInt64() & 0xFFFF);
                            int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                            _deltaAccum.x += x - _lastMouseX;
                            _deltaAccum.y += y - _lastDragY;
                            _lastMouseX = x; _lastDragY = y;
                        }
                        // Queue an OS paint request so dragging repaints without
                        // drawing inside the native callback (re-entrancy hazard).
                        if (_deltaAccum.sqrMagnitude > 0f)
                            RedrawWindow(hWnd, IntPtr.Zero, IntPtr.Zero,
                                (uint)(RDW_INVALIDATE | RDW_UPDATENOW | RDW_NOFRAME));
                        return IntPtr.Zero;

                    case WM_LBUTTONUP:
                    case WM_RBUTTONUP:
                        _dragging = false;
                        ReleaseCapture();
                        return IntPtr.Zero;

                    case WM_CAPTURECHANGED:
                        // Another window took capture; stop tracking the drag.
                        _dragging = false;
                        return IntPtr.Zero;

                    case WM_SETCURSOR:
                        int hitTest = (int)(lParam.ToInt64() & 0xFFFF);
                        if (hitTest == HTCLIENT)
                        {
                            SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)32512 /* IDC_ARROW */));
                            return (IntPtr)1;
                        }
                        break;

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
                            if (lp.Y >= 0 && lp.Y < 42)
                            {
                                var closeRect = new Rect(ActiveHost._w - 46, 5, 38, 32);
                                if (!closeRect.Contains(new Vector2(lp.X, lp.Y)))
                                    return (IntPtr)HTCAPTION;
                            }
                        }
                        return def;

                    case WM_ERASEBKGND:
                        return (IntPtr)1; // we paint everything ourselves

                    case WM_PAINT:
                        BeginPaint(hWnd, _psBuffer);
                        EndPaint(hWnd, _psBuffer);
                        DrawFrame(ActiveHost); // immediate repaint on OS request
                        return IntPtr.Zero;

                    case WM_DESTROY:
                        if (ActiveHost != null && ActiveHost._hwnd == hWnd)
                            ActiveHost._hwnd = IntPtr.Zero; // panel closed via the OS
                        return IntPtr.Zero;
                }
                return DefWindowProcW(hWnd, msg, wParam, lParam);
            }
        }
    }
}
