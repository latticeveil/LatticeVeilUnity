using UnityEngine;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using LatticeVeil.Core;

namespace LatticeVeil.Launcher
{
    /// <summary>
    /// Main Unity launcher for LatticeVeil.
    /// Implements Veilnet authentication and protocol linking.
    /// Ported from MonoGame LauncherForm.cs implementation.
    /// </summary>
    public class LauncherUI : MonoBehaviour
    {
        [Header("Launcher Configuration")]
        public bool autoLaunchOnStart = false;
        public string launchMode = "Offline";
        public int windowWidth = 1440;
        public int windowHeight = 900;

        // Core systems
        private Core.Logger _log;
        private Core.PlayerProfile _profile;
        private Core.GameSettings _settings;
        private LauncherRuntimeConfig _runtimeConfig;
        private VeilnetClient _veilnetClient;
        private HttpClient _httpClient;

        // Process management
        private Process _gameProcess;
        private bool _isLaunching;

        // Task Manager grouping: Win11 never nests a process that owns a window
        // under another app (jobs, parentage and even window ownership all fail —
        // ownership demotes the game to a background process). The one surviving
        // mechanism is TITLE MERGE: windows sharing the same title collapse into
        // a single group. So the game window is retitled to exactly the
        // launcher's window title ("LatticeVeil") and left UNOWNED.
        private const string GameWindowTitle = "LatticeVeil";
        private bool _gameWindowRenamed = false;
        private float _gameWindowRenameElapsed = 0f;
        private float _gameWindowRenameNextAttempt = 0f;

        // Set when Instant Quit initiated the launcher shutdown, so OnDestroy
        // leaves the running game alone instead of killing it.
        private bool _instantQuitInProgress = false;

        // Veilnet authentication
        private string _veilnetUsername = "";
        private string _veilnetToken = "";
        private string _veilnetUserId = "";
        private bool _veilnetLoggedIn;
        private bool _veilnetAutoLoginAttempted;
        private bool _queuedLinkCodeConsumeInProgress;

        // Protocol linking & Backend endpoints
        private string _startupLinkCode;
        private const string DefaultVeilnetLauncherPageUrl = "https://latticeveil.github.io/veilnet/launcher/";
        private const string DefaultVeilnetFunctionsBaseUrl = "https://lqghurvonrvrxfwjgkuu.supabase.co/functions/v1";
        private const string DefaultGameHashesGetUrl = "https://lqghurvonrvrxfwjgkuu.supabase.co/rest/v1/game_hashes";
        private const string DefaultSupabaseAnonKey = "sb_publishable_oy1En_XHnhp5AiOWruitmQ_sniWHETA";

        // Build verification
        private OfficialBuildVerifier _officialBuildVerifier;
        private bool _hashVerificationComplete;
        private bool _hashVerified;
        private string _hashStatusMessage = "";
        private float _linkCheckTimer;

        // Version checking
        private VersionChecker _versionChecker;
        private string _versionSubtitleText = "";

        // Downloadable game versions (populated from GitHub releases)
        private GameVersionService _gameVersionService;
        private LegacyVersionInstaller _versionInstaller;
        private System.Collections.Generic.List<GameVersionInfo> _gameVersions = new System.Collections.Generic.List<GameVersionInfo>();
        private GameVersionInfo _selectedVersion;
        private bool _versionDropdownOpen = false;
        private bool _versionsLoading = false;
        private bool _isDownloadingVersion = false;
        private double _downloadProgress = 0.0;
        private string _versionStatusMessage = "";

        // Option-2 version system: dropdown lists installed versions + LATEST only;
        // everything else (full library, notes, install/uninstall) lives in the Version Manager.
        private bool _selectedIsLatest = false;
        private bool _showVersionManagerModal = false;
        private Rect _versionManagerRect = new Rect(170, 90, 1080, 620);
        private bool _draggingVersionManager = false;
        private Vector2 _versionManagerListScroll = Vector2.zero;
        private Vector2 _versionManagerNotesScroll = Vector2.zero;
        private GameVersionInfo _versionManagerSelected = null;
        private bool _showInstallPromptModal = false;
        private bool _latestInstallPromptDecided = false;
        private Vector2 _installPromptNotesScroll = Vector2.zero;
        private bool _draggingInstallPromptModal = false;

        // Drag offsets so every popup window can be moved independently.
        private Vector2 _settingsModalOffset = Vector2.zero;
        private bool _draggingSettingsModal = false;
        private Vector2 _skinModalOffset = Vector2.zero;
        private bool _draggingSkinModal = false;

        // UI state
        private string _offlineUsernameEdit = "";
        private Vector2 _scrollPosition;
        private string _logContent = "";
        private bool _launcherVisible = true;
        private bool _parkedLauncherForGame = false;
        private bool _launchModeDropdownOpen = false;
        private bool _showSettingsModal = false;
        private bool _showSkinModal = false;
        private Texture2D _skin3DPreviewTexture;
        private string _skinStatusMessage = "";
        private float _skinPreviewYaw = 0f;
        private float _skinPreviewPitch = 0f;
        private float _skinPreviewTargetYaw = 0f;
        private float _skinPreviewTargetPitch = 0f;
        private float _skinPreviewYawVelocity = 0f;
        private float _skinPreviewPitchVelocity = 0f;
        private string _selectedSkinLibraryPath;
        private string _skinLibraryPreviewPathLoaded;
        private string _skinPreviewAnimation = "IDLE";
        private bool _skinPreviewLayers = true;
        private Vector2 _skinLibraryScroll;
        private readonly System.Collections.Generic.Dictionary<string, Texture2D> _skinLibraryThumbCache = new();
        private bool _isDraggingSkinModel = false;
        private float _lastSkinDragMouseX = 0f;
        private float _lastSkinDragMouseY = 0f;
        private Texture2D _currentLoadedSkinTex;
        private string _stagedSkinPath;
        private Texture2D _stagedSkinTex;
        private string _stagedSkinHash;

        // Supabase skin sync
        private SupabaseSkinClient _supabaseSkinClient;
        private bool _skinSyncInProgress;

        // Revert-choice dialog (LOCAL vs VEILNET)
        private bool _showRevertChoiceDialog;
        private bool _revertVeilnetInProgress;


        private readonly string[] _settingsTabs = new[] { "General", "Updates", "Cleanup", "Status" };
        private int _settingsTabIndex = 0;

        // Textures
        private Texture2D _logoTexture;
        private Texture2D _avatarTexture;
        private Texture2D _skinHeadTexture;
        private Texture2D _greenBarTex;
        private Texture2D _amberBarTex;
        private Texture2D _redBarTex;
        private Texture2D _logoutBtnTex;
        private Texture2D _logoutBtnHoverTex;
        private Texture2D _skinsBtnTex;
        private Texture2D _settingsHeaderTex;
        private Texture2D _activeTabTex;
        private Texture2D _inactiveTabTex;
        private Texture2D _modalBackgroundTex;
        private Texture2D _dimmerTex;
        private Texture2D _panelBorderTex;
        private Texture2D _settingsIconTex;
        private Texture2D _folderIconTex;
        private Texture2D _switchTrackOnTex;
        private Texture2D _switchTrackOffTex;
        private Texture2D _switchTrackOffHoverTex;
        private Texture2D _switchKnobTex;

        // Skin screen button icons (procedural white glyphs)
        private Texture2D _iconUpload;
        private Texture2D _iconOnline;
        private Texture2D _iconRevert;
        private Texture2D _iconReset;
        private Texture2D _iconApply;

        // UI styles
        private GUIStyle _titleStyle;
        private GUIStyle _versionSubtitleStyle;
        private GUIStyle _sectionHeaderStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _statusTextStyle;
        private GUIStyle _buttonStyle;
        private GUIStyle _launchButtonStyle;
        private GUIStyle _logoutButtonStyle;
        private GUIStyle _skinsButtonStyle;
        private GUIStyle _windowControlStyle;
        private GUIStyle _wrenchControlStyle;
        private GUIStyle _toggleStyle;
        private GUIStyle _switchLabelStyle;
        private GUIStyle _switchSubLabelStyle;
        private GUIStyle _boxStyle;
        private GUIStyle _panelBoxStyle;
        private GUIStyle _logStyle;
        private GUIStyle _topBarStyle;
        private GUIStyle _textFieldStyle;
        private GUIStyle _dropdownStyle;
        private GUIStyle _dropdownArrowStyle;
        private GUIStyle _tooltipStyle;
        private GUIStyle _dropdownItemStyle;
        private GUIStyle _dropdownActiveItemStyle;
        private GUIStyle _dropdownContainerStyle;
        private GUIStyle _modalHeaderStyle;
        private GUIStyle _modalHeaderTitleStyle;
        private GUIStyle _modalTabActiveStyle;
        private GUIStyle _modalTabInactiveStyle;
        private GUIStyle _modalCloseBtnStyle;
        private GUIStyle _dimmerStyle;

        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _mainThreadQueue = new();

        private void EnqueueMainThread(Action action)
        {
            if (action != null)
            {
                _mainThreadQueue.Enqueue(action);
            }
        }

        private void Awake()
        {
            LauncherWindowInitializer.ApplyLauncherWindowSettings();
        }

        private void Update()
        {
            while (_mainThreadQueue.TryDequeue(out var action))
            {
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    _log?.Warn($"MainThread dispatch error: {ex.Message}");
                }
            }

            if (_showSkinModal)
            {
                var nextYaw = Mathf.SmoothDampAngle(_skinPreviewYaw, _skinPreviewTargetYaw, ref _skinPreviewYawVelocity, 0.08f);
                var nextPitch = Mathf.SmoothDamp(_skinPreviewPitch, _skinPreviewTargetPitch, ref _skinPreviewPitchVelocity, 0.08f);
                if (Mathf.Abs(Mathf.DeltaAngle(nextYaw, _skinPreviewYaw)) > 0.05f || Mathf.Abs(nextPitch - _skinPreviewPitch) > 0.05f)
                {
                    _skinPreviewYaw = nextYaw;
                    _skinPreviewPitch = nextPitch;
                    RenderSkinPreview();
                }
                else
                {
                    _skinPreviewYaw = _skinPreviewTargetYaw;
                    _skinPreviewPitch = _skinPreviewTargetPitch;
                    _skinPreviewYawVelocity = 0f;
                    _skinPreviewPitchVelocity = 0f;
                }
            }

            // While the game runs, retitle its main window to exactly the
            // launcher's window title ("LatticeVeil") so Task Manager's
            // title-based grouping merges both windows into ONE entry instead of
            // two separate app collections. The engine may set its own title
            // during startup, so retry every 0.5 s until it sticks (max 60 s).
            if (_gameProcess != null && !_gameProcess.HasExited && !_gameWindowRenamed)
            {
                _gameWindowRenameElapsed += Time.deltaTime;
                if (_gameWindowRenameElapsed >= _gameWindowRenameNextAttempt)
                {
                    if (_gameWindowRenameElapsed > 60f)
                    {
                        _gameWindowRenamed = true; // give up after a minute
                    }
                    else
                    {
                        _gameWindowRenameNextAttempt += 0.5f;
                        try
                        {
                            _gameProcess.Refresh();
                            var hwnd = _gameProcess.MainWindowHandle;
                            if (hwnd != IntPtr.Zero && LauncherWindowInitializer.SetExternalWindowTitle(hwnd, GameWindowTitle))
                            {
                                _gameWindowRenamed = true;
                                _log.Info($"Game window retitled to '{GameWindowTitle}' (Task Manager title-merge grouping).");
                            }
                        }
                        catch
                        {
                            // Window not up yet (or exited between checks); retry next attempt.
                        }
                    }
                }
            }

            // Check if game process is still running
            if (_gameProcess != null && _gameProcess.HasExited)
            {
                _log.Info("Game process exited.");
                _gameProcess = null;
                _isLaunching = false;

                // Return the user to the launcher once the game closes again.
                if (_parkedLauncherForGame)
                {
                    _parkedLauncherForGame = false;
                    LauncherWindowInitializer.RestoreLauncherAfterGame();
                    _log.Info("Returned to launcher after game exit.");
                }
            }

            // Update log display periodically
            if (_log != null)
            {
                var recentLogs = _log.GetRecentLogs(50);
                _logContent = recentLogs;
            }

            // Try to consume pending link codes
            _ = TryConsumePendingLinkCodesAsync();

            // Re-apply theme if dark mode changed
            if (_settings != null && _titleStyle != null)
            {
                var isCurrentlyDark = _titleStyle.normal.textColor.r > 0.5f;
                if (_settings.DarkMode != isCurrentlyDark)
                {
                    ApplyTheme(_settings.DarkMode);
                }
            }
        }

        private void Start()
        {
            try
            {
                LauncherWindowInitializer.ApplyLauncherWindowSettings();

                // Initialize core systems
                _log = new Core.Logger();
                _log.Info("Unity Launcher starting...");

                // Initialize HTTP client
                _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

                // Load runtime config
                _runtimeConfig = LauncherRuntimeConfig.Load(_log);

                // Load profile and settings
                _profile = Core.PlayerProfile.LoadOrCreate(_log);
                _settings = Core.GameSettings.LoadOrCreate(_log);

                // Ensure required directories exist
                Directory.CreateDirectory(Paths.RootDir);
                Directory.CreateDirectory(Paths.LogsDir);
                Directory.CreateDirectory(Paths.WorldsDir);
                Directory.CreateDirectory(Paths.ScreenshotsDir);
                if (!Paths.IsDevBuild)
                {
                    Paths.EnsureAssetDirectoriesExist();
                    Paths.RemoveDisallowedAssetEntries();
                }

                // Install default assets (in DEV, resolves and logs the authoritative development root directly)
                if (Paths.IsDevBuild || _settings.AutoTextureDownloadsEnabled)
                {
                    var appDirectory = Application.dataPath;
                    var assetSync = Core.AssetInstaller.EnsureDefaultsInstalled(appDirectory, _log);
                    if (!Paths.IsDevBuild && assetSync.HadChanges)
                    {
                        _log.Info($"Asset sync complete: installed={assetSync.InstalledCount}, replaced={assetSync.ReplacedCount}");
                    }
                }
                else
                {
                    _log.Info("Automatic texture downloads disabled in settings; keeping current local assets.");
                }

                // Initialize offline username edit field
                _offlineUsernameEdit = _profile.OfflineUsername;

                // Initialize Veilnet client
                var functionsBaseUrl = GetVeilnetFunctionsBaseUrl();
                if (!string.IsNullOrWhiteSpace(functionsBaseUrl))
                {
                    _veilnetClient = new VeilnetClient(functionsBaseUrl, _httpClient);
                }

                // Initialize build verifier
                _officialBuildVerifier = new OfficialBuildVerifier(_log, GetGameHashesGetUrl(), GetSupabaseAnonKey());

                // Initialize downloadable-version services and fetch the version list
                _gameVersionService = new GameVersionService(_log, _httpClient);
                _versionInstaller = new LegacyVersionInstaller(_httpClient, _log);
                _ = RefreshGameVersionsAsync();

                // Try to load saved Veilnet auth
                TryLoadVeilnetAuth();

                // Start async build hash verification
                _ = VerifyBuildHashAsync();

                // Start async version check (dev builds skip remote fetch)
                _versionChecker = new VersionChecker();
                _ = RunVersionCheckAsync();

                // Load visual assets (Logo, Avatar, Skin)
                LoadVisualAssets();

                // Register protocol (only in built game, not editor)
#if !UNITY_EDITOR
                LauncherProtocolLinking.TryEnsureProtocolRegistration(_log);
#endif

                // Check for startup link code from command line
                _startupLinkCode = LauncherProtocolLinking.TryExtractLinkCodeFromArgs(Environment.GetCommandLineArgs());

                // Initialize launcher
                InitializeLauncher();

                // Show native window now that initial frame setup is complete
                LauncherWindowInitializer.ShowLauncherWindow();

                _log.Info("Unity Launcher initialized successfully.");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"Failed to initialize launcher: {ex.Message}");
                _log.Error($"Launcher initialization failed: {ex.Message}");
            }
        }

        private void LoadVisualAssets()
        {
            try
            {
                // 1. Logo
                if (_logoTexture == null)
                {
                    var projectDir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                    var logoCandidates = new[]
                    {
                        // StreamingAssets: correct location for standalone builds
                        Path.Combine(Application.streamingAssetsPath, "LatticeVeil Launcher logo.png"),
                        // Editor / legacy paths
                        Path.Combine(Application.dataPath, "LatticeVeil Launcher logo.png"),
                        Path.Combine(projectDir, "Assets", "LatticeVeil Launcher logo.png"),
                        Path.Combine(Application.dataPath, "Textures", "LatticeVeil Launcher logo.png"),
                        Path.Combine(Paths.RootDir, "LatticeVeil Launcher logo.png")
                    };

                    foreach (var candidate in logoCandidates)
                    {
                        if (File.Exists(candidate))
                        {
                            var bytes = File.ReadAllBytes(candidate);
                            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                            if (tex.LoadImage(bytes))
                            {
                                tex.filterMode = FilterMode.Bilinear;
                                _logoTexture = tex;
                                _log?.Info($"Loaded launcher logo from: {candidate}");
                                break;
                            }
                        }
                    }

                    if (_logoTexture == null)
                    {
                        _logoTexture = CreateProceduralLogo();
                    }
                }

                // 2. Avatar
                if (_avatarTexture == null) LoadAvatarTexture();

                // 3. Skin
                if (_skinHeadTexture == null) LoadSkinHeadTexture();

                // 4. Settings gear icon
                if (_settingsIconTex == null)
                    _settingsIconTex = CreateGearIconTexture();

                // 4b. White folder icon (Game Folder button)
                if (_folderIconTex == null)
                    _folderIconTex = CreateFolderIconTexture();

                // 5. Skin button icons (procedural white glyphs)
                _iconUpload  = CreateSkinButtonIcon(SkinIconType.Upload);
                _iconOnline  = CreateSkinButtonIcon(SkinIconType.Online);
                _iconRevert  = CreateSkinButtonIcon(SkinIconType.Revert);
                _iconReset   = CreateSkinButtonIcon(SkinIconType.Reset);
                _iconApply   = CreateSkinButtonIcon(SkinIconType.Apply);
            }
            catch (Exception ex)
            {
                _log?.Warn($"Visual asset loading error: {ex.Message}");
            }
        }

        /// <summary>Runs version check and updates _versionSubtitleText.</summary>
        private async System.Threading.Tasks.Task RunVersionCheckAsync()
        {
            try
            {
                var localVer = Application.version;
                var defaultTitle = GetDefaultReleaseTitle(localVer);
                _versionSubtitleText = Paths.IsDevBuild
                    ? $"[DEV] {defaultTitle}"
                    : defaultTitle;

                if (_settings != null && !_settings.AutoUpdateChecksEnabled)
                {
                    _log?.Info("Automatic update checks disabled in settings.");
                    return;
                }

                if (_versionChecker == null) return;

                await _versionChecker.CheckAsync();

                switch (_versionChecker.State)
                {
                    case VersionChecker.CheckState.UpToDate:
                        // The subtitle is the always-current "latest update" spot.
                        _versionSubtitleText = !string.IsNullOrWhiteSpace(_versionChecker.RemoteDisplayName)
                            ? $"LATEST UPDATE: {_versionChecker.RemoteDisplayName}"
                            : defaultTitle;
                        break;

                    case VersionChecker.CheckState.OutOfDate:
                        _versionSubtitleText = !string.IsNullOrWhiteSpace(_versionChecker.RemoteDisplayName)
                            ? $"LATEST UPDATE: {_versionChecker.RemoteDisplayName}"
                            : $"{defaultTitle}  (Update available: {_versionChecker.RemoteTag})";
                        _log?.Warn($"Version out of date: local={localVer}, latest={_versionChecker.RemoteTag}");
                        break;

                    case VersionChecker.CheckState.NoRelease:
                        // No release published yet on GitHub — use the local default release title.
                        _versionSubtitleText = Paths.IsDevBuild
                            ? $"[DEV] {defaultTitle}"
                            : defaultTitle;
                        break;

                    default:
                        // Network unavailable or parse error — keep local version title.
                        break;
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"Version check failed: {ex.Message}");
            }
        }

        private static string GetDefaultReleaseTitle(string version)
        {
            var cleanVer = (version ?? "1.0.0").TrimStart('v', 'V').Trim();
            if (cleanVer == "1.0.0" || cleanVer == "1.0")
            {
                return "V1.0.0 - Veilwalkers: Unified";
            }
            return $"V{cleanVer}";
        }

        /// <summary>
        /// Generates a clean 24×24 pixel gear icon texture for the settings button.
        /// Drawn procedurally so no external asset file is needed.
        /// </summary>
        private static Texture2D CreateGearIconTexture()
        {
            const int S = 24;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            var pix = new Color[S * S];

            // Transparent by default.
            for (int i = 0; i < pix.Length; i++) pix[i] = Color.clear;

            var white = new Color(0.95f, 0.95f, 0.95f, 1f);
            float cx = (S - 1) * 0.5f;
            float cy = (S - 1) * 0.5f;

            // Draw each pixel: gear body + 6 teeth.
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float dx = x - cx, dy = y - cy;
                float r  = Mathf.Sqrt(dx * dx + dy * dy);
                float a  = Mathf.Atan2(dy, dx);

                // Inner hole
                if (r < 3.2f) continue;

                // Main disc  (r 3.2 – 7.5 = ring body)
                bool inBody = r >= 3.2f && r <= 7.5f;

                // 6 rectangular teeth protruding from the ring out to r=10
                float toothAngle = Mathf.PI / 3f; // 60° per tooth
                float aNorm = ((a % toothAngle) + toothAngle) % toothAngle; // 0..60°
                bool inTooth = r > 7.0f && r <= 10.0f && aNorm >= 0.18f && aNorm <= toothAngle - 0.18f;

                if (inBody || inTooth)
                    pix[y * S + x] = white;
            }

            tex.SetPixels(pix);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// Generates a Windows 11 style folder icon (64×64, drawn at high res and
        /// scaled down by the button for crisp edges): rounded dark-amber back panel
        /// with a tab, and a bright yellow gradient front panel. No asset file needed.
        /// </summary>
        private static Texture2D CreateFolderIconTexture()
        {
            const int S = 64;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            var pix = new Color[S * S];

            for (int i = 0; i < pix.Length; i++) pix[i] = Color.clear;

            // Signed-distance test for a rounded rectangle.
            bool InRounded(float x, float y, float x0, float y0, float x1, float y1, float r)
            {
                var qx = Mathf.Max(x0 + r - x, x - (x1 - r), 0f);
                var qy = Mathf.Max(y0 + r - y, y - (y1 - r), 0f);
                return qx * qx + qy * qy <= r * r;
            }

            var back = new Color(0.85f, 0.60f, 0.16f, 1f);      // dark amber (back panel + tab)
            var frontTop = new Color(1.00f, 0.87f, 0.47f, 1f);  // bright yellow (front gradient top)
            var frontBottom = new Color(0.95f, 0.66f, 0.20f, 1f); // amber (front gradient bottom)

            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    var fx = x + 0.5f;
                    var fy = y + 0.5f;

                    // Back panel + tab (peeking above/left of the front panel)
                    if (InRounded(fx, fy, 5, 16, 59, 52, 6) || InRounded(fx, fy, 5, 10, 31, 24, 5))
                    {
                        pix[y * S + x] = back;
                    }

                    // Front panel with a vertical gradient (Win11 look)
                    if (InRounded(fx, fy, 5, 20, 59, 54, 6))
                    {
                        var t = Mathf.Clamp01((fy - 20f) / 34f);
                        var c = Color.Lerp(frontTop, frontBottom, t);

                        // Subtle lighter top edge highlight
                        if (fy < 23f) c = Color.Lerp(c, new Color(1f, 0.94f, 0.62f, 1f), 0.55f);

                        pix[y * S + x] = c;
                    }
                }
            }

            tex.SetPixels(pix);
            tex.Apply();
            return tex;
        }

        private enum SkinIconType { Upload, Online, Revert, Reset, Apply }

        /// <summary>Creates a 20x20 white procedural icon for skin screen action buttons.</summary>
        private static Texture2D CreateSkinButtonIcon(SkinIconType type)
        {
            const int S = 20;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            var pix = new Color[S * S];
            var w = new Color(1f, 1f, 1f, 1f);

            void Set(int x, int y) { if (x >= 0 && x < S && y >= 0 && y < S) pix[y * S + x] = w; }
            void HLine(int y, int x0, int x1) { for (int x = x0; x <= x1; x++) Set(x, y); }
            void VLine(int x, int y0, int y1) { for (int y = y0; y <= y1; y++) Set(x, y); }

            switch (type)
            {
                case SkinIconType.Upload:   // Folder with up-arrow
                    HLine(4, 2, 8); HLine(5, 2, 8); VLine(2, 4, 14); VLine(14, 4, 14); HLine(14, 2, 14);
                    HLine(9, 9, 9); VLine(9, 6, 12); Set(7,8); Set(11,8); Set(7,7); Set(11,7); Set(8,6); Set(10,6); Set(9,5);
                    break;
                case SkinIconType.Online:   // Globe outline + meridian lines
                    for (int a = 0; a < 360; a += 12) { int px = (int)(10 + 7*Mathf.Cos(a*Mathf.Deg2Rad)); int py = (int)(10 + 7*Mathf.Sin(a*Mathf.Deg2Rad)); Set(px,py); }
                    VLine(10, 3, 17); HLine(10, 3, 17); HLine(6,4,16); HLine(14,4,16);
                    break;
                case SkinIconType.Revert:   // Counter-clockwise arrow
                    HLine(5, 4, 10); VLine(4, 5, 11); HLine(11, 4, 10); VLine(10, 5, 11);
                    Set(6,3); Set(7,3); Set(8,2); Set(5,4); Set(4,5); Set(4,6);
                    break;
                case SkinIconType.Reset:    // Trash bin outline
                    HLine(4, 4, 14); VLine(4, 5, 16); VLine(14, 5, 16); HLine(16, 4, 14);
                    VLine(7, 6, 15); VLine(10, 6, 15); VLine(13, 6, 15);
                    HLine(3, 6, 12); Set(5,2); Set(6,2); Set(7,1); Set(11,1); Set(12,2); Set(13,2);
                    break;
                case SkinIconType.Apply:    // Checkmark
                    Set(3,8); Set(4,7); Set(5,6); Set(6,7); Set(7,8); Set(8,9); Set(9,10);
                    Set(10,9); Set(11,8); Set(12,7); Set(13,6); Set(14,5); Set(15,4);
                    break;
            }

            tex.SetPixels(pix);
            tex.Apply();
            return tex;
        }


        private Texture2D CreateProceduralLogo()
        {
            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pix = new Color[size * size];
            var cyan = new Color(0f, 0.85f, 0.95f, 1f);
            var green = new Color(0f, 0.9f, 0.46f, 1f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Abs(x - (size / 2f - 0.5f));
                    float dy = Mathf.Abs(y - (size / 2f - 0.5f));
                    float d = dx + dy;
                    if (d < size * 0.45f && d > size * 0.25f)
                    {
                        pix[y * size + x] = Color.Lerp(cyan, green, (float)y / size);
                    }
                    else if (d <= size * 0.25f)
                    {
                        pix[y * size + x] = new Color(0.05f, 0.2f, 0.15f, 1f);
                    }
                    else
                    {
                        pix[y * size + x] = Color.clear;
                    }
                }
            }
            tex.SetPixels(pix);
            tex.Apply();
            return tex;
        }

        private void LoadAvatarTexture()
        {
            try
            {
                var socialDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LatticeVeil", "Runtime", "SocialSession");
                var avatarCandidates = new[]
                {
                    Path.Combine(socialDir, "veilnet_avatar_session.lvimg"),
                    Path.Combine(socialDir, "veilnet_avatar_session.png"),
                    Path.Combine(socialDir, "avatar.png")
                };

                foreach (var candidate in avatarCandidates)
                {
                    if (File.Exists(candidate))
                    {
                        var bytes = File.ReadAllBytes(candidate);
                        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (tex.LoadImage(bytes))
                        {
                            tex.filterMode = FilterMode.Bilinear;
                            _avatarTexture = tex;
                            return;
                        }
                    }
                }
            }
            catch { }
        }

        private void LoadSkinHeadTexture()
        {
            try
            {
                var fullSkin = SkinManager.LoadActiveSkinTexture();
                if (fullSkin != null)
                {
                    _skinHeadTexture = ExtractHeadTexture(fullSkin);
                }
            }
            catch { }
        }

        private Texture2D ExtractHeadTexture(Texture2D skin)
        {
            if (skin == null) return null;
            int width = skin.width;
            int height = skin.height;

            int scale = width / 64;
            if (scale < 1) scale = 1;

            int headX = 8 * scale;
            int headY = height - (16 * scale);
            int headSize = 8 * scale;
            int hatX = 40 * scale;
            int hatY = height - (16 * scale);

            Color[] headPixels = skin.GetPixels(headX, headY, headSize, headSize);
            Color[] hatPixels = (width >= 64 * scale) ? skin.GetPixels(hatX, hatY, headSize, headSize) : null;

            Color[] composite = new Color[headSize * headSize];
            for (int i = 0; i < composite.Length; i++)
            {
                var baseCol = headPixels[i];
                if (hatPixels != null && hatPixels[i].a > 0.05f)
                {
                    var hatCol = hatPixels[i];
                    float a = hatCol.a;
                    composite[i] = new Color(
                        hatCol.r * a + baseCol.r * (1f - a),
                        hatCol.g * a + baseCol.g * (1f - a),
                        hatCol.b * a + baseCol.b * (1f - a),
                        1f
                    );
                }
                else
                {
                    composite[i] = new Color(baseCol.r, baseCol.g, baseCol.b, 1f);
                }
            }

            var headTex = new Texture2D(headSize, headSize, TextureFormat.RGBA32, false);
            headTex.SetPixels(composite);
            headTex.Apply();
            headTex.filterMode = FilterMode.Point;
            return headTex;
        }

        private void InitializeLauncher()
        {
            _log.Info($"LatticeVeil Launcher initialized");
            _log.Info($"Build: {(Paths.IsDevBuild ? "DEV" : "RELEASE")}");
            _log.Info($"Username: {_profile.GetDisplayUsername()}");
            _log.Info($"Offline Username: {_profile.OfflineUsername}");
            _log.Info($"Keep Launcher Open: {_settings.KeepLauncherOpen}");
            _log.Info($"Dark Mode: {_settings.DarkMode}");
            _log.Info($"Launch Mode: {launchMode}");
            _log.Info($"Veilnet Logged In: {_veilnetLoggedIn}");
            if (_veilnetLoggedIn)
            {
                _log.Info($"Veilnet Username: {_veilnetUsername}");
            }

            // Consume startup link code if present
            if (!string.IsNullOrWhiteSpace(_startupLinkCode))
            {
                _ = ConsumeVeilnetLinkCodeAsync(_startupLinkCode, "startup protocol callback");
            }

            if (autoLaunchOnStart)
            {
                _log.Info("Auto-launch enabled, launching game...");
                LaunchGame();
            }
        }

        private void OnGUI()
        {
            if (!_launcherVisible) return;

            // Stream latest logs into UI
            if (_log != null)
            {
                _logContent = _log.GetRecentLogs(100);
            }

            // Ensure styles are initialized
            if (_titleStyle == null)
            {
                InitializeStyles();
            }

            if (_logoTexture == null)
            {
                LoadVisualAssets();
            }

            // Reveal window on first OnGUI paint
            LauncherWindowInitializer.ShowLauncherWindow();

            bool modalOpen = _showSettingsModal || _showSkinModal || _showVersionManagerModal || _showInstallPromptModal;

            // Hover tooltip state — anchored to the hovered control, not the cursor,
            // so the tooltip can never stick to the mouse or linger after leaving.
            string hoverTooltip = null;
            Rect hoverTooltipAnchor = Rect.zero;

            // Calculate layout filling the full window client area
            var launcherRect = new Rect(0, 0, Screen.width, Screen.height);

            // Paint entire background pure OLED black
            if (_dimmerTex != null)
            {
                GUI.DrawTexture(launcherRect, _dimmerTex);
            }
            GUI.Box(launcherRect, "", _boxStyle);

            // Top bar (52px height) — drawn before GUI.enabled so it always renders on top
            const float topBarHeight = 52f;
            var topBarRect = new Rect(launcherRect.x, launcherRect.y, launcherRect.width, topBarHeight);

            // Window drag — checked BEFORE GUI.enabled so it works regardless of any open modal or submenu.
            // Excludes the right-most 160px reserved for window control buttons.
            var dragAreaRect = new Rect(topBarRect.x, topBarRect.y, topBarRect.width - 160f, topBarHeight);
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0)
            {
                if (dragAreaRect.Contains(Event.current.mousePosition))
                {
                    LauncherWindowInitializer.DragLauncherWindow();
                }
            }

            // Disable interaction on underlying controls when any modal is open
            GUI.enabled = !modalOpen;

            GUI.Box(topBarRect, "", _topBarStyle);

            // Top-left Logo (32x32)
            var logoRect = new Rect(topBarRect.x + 16, topBarRect.y + 10, 32, 32);
            if (_logoTexture != null)
            {
                GUI.DrawTexture(logoRect, _logoTexture, ScaleMode.ScaleToFit);
            }
            else
            {
                GUI.Box(logoRect, "LV", _boxStyle);
            }

            // App Name ("LatticeLauncher" or "[DEV] LatticeLauncher")
            var titleX = logoRect.x + logoRect.width + 12;
            var titleText = Paths.IsDevBuild ? "[DEV] LatticeLauncher" : "LatticeLauncher";
            var titleRect = new Rect(titleX, topBarRect.y + 11, 230, 30);
            GUI.Label(titleRect, titleText, _titleStyle);

            // Version Subtitle — fed from VersionChecker (dynamic)
            var subX = titleRect.x + titleRect.width + 8;
            var subtitleRect = new Rect(subX, topBarRect.y + 13, 440, 28);
            GUI.Label(subtitleRect, _versionSubtitleText, _versionSubtitleStyle);

            // Top-right window controls: Settings (gear icon), Minimize (-), Close (X)
            // Settings and Minimize are disabled when modal is open, but MAIN X is ALWAYS ENABLED!
            GUI.enabled = !modalOpen;

            var settingsBtnRect = new Rect(topBarRect.x + topBarRect.width - 146, topBarRect.y + 8, 44, 36);
            var settingsBtnContent = _settingsIconTex != null
                ? new GUIContent(_settingsIconTex)
                : new GUIContent("*");
            if (GUI.Button(settingsBtnRect, settingsBtnContent, _wrenchControlStyle))
            {
                _showSettingsModal = !_showSettingsModal;
                if (_showSettingsModal)
                {
                    _showSkinModal = false;
                    _showVersionManagerModal = false;
                    _showInstallPromptModal = false;
                }
            }

            var minBtnRect = new Rect(topBarRect.x + topBarRect.width - 98, topBarRect.y + 8, 44, 36);
            if (GUI.Button(minBtnRect, "-", _windowControlStyle))
            {
                HandleLauncherMinimizeButtonRequest();
            }

            // Main Launcher Close (X) button is ALWAYS enabled regardless of any modal / popup!
            GUI.enabled = true;
            var closeBtnRect = new Rect(topBarRect.x + topBarRect.width - 50, topBarRect.y + 8, 44, 36);
            if (GUI.Button(closeBtnRect, "X", _windowControlStyle))
            {
                HandleLauncherCloseButtonRequest();
            }

            // Disable background interaction if any modal is open
            GUI.enabled = !modalOpen;

            // Calculate main content area
            const float bottomSectionHeight = 110f;
            var bodyY = topBarRect.y + topBarRect.height + 12;
            var bodyHeight = Mathf.Max(100, launcherRect.height - bodyY - bottomSectionHeight);

            // Left panel: 62% width
            var leftPanelWidth = Mathf.Max(300, (launcherRect.width - 42) * 0.62f);
            var leftPanelRect = new Rect(launcherRect.x + 16, bodyY, leftPanelWidth, bodyHeight);

            // Big Log Box
            var logBoxHeight = leftPanelHeightWithoutStatus(leftPanelRect.height);
            var logBoxRect = new Rect(leftPanelRect.x, leftPanelRect.y, leftPanelRect.width, logBoxHeight);
            GUI.Box(logBoxRect, "", _boxStyle);

            // Scrollable log text — TextArea so log lines can be selected and
            // copied (Ctrl+C) like any Windows text box. Edits are discarded:
            // the buffer is re-fed every frame from the logger.
            var scrollAreaRect = new Rect(logBoxRect.x + 6, logBoxRect.y + 6, logBoxRect.width - 12, logBoxRect.height - 12);
            var textHeight = Mathf.Max(scrollAreaRect.height, _logContent.Length * 22);
            _scrollPosition = GUI.BeginScrollView(scrollAreaRect, _scrollPosition, new Rect(0, 0, scrollAreaRect.width - 20, textHeight));
            GUI.TextArea(new Rect(4, 4, scrollAreaRect.width - 24, textHeight), _logContent, _logStyle);
            GUI.EndScrollView();

            // Status label & Colored horizontal status bar
            var statusY = logBoxRect.y + logBoxRect.height + 10;
            string statusText;
            Texture2D statusBarTex;

            if (_hashVerified && _veilnetLoggedIn)
            {
                statusText = "Official online ready";
                statusBarTex = _greenBarTex;
            }
            else if (_hashVerified)
            {
                statusText = "Official build verified (offline ready)";
                statusBarTex = _greenBarTex;
            }
            else if (!_hashVerificationComplete)
            {
                statusText = "Checking build hash...";
                statusBarTex = _amberBarTex;
            }
            else
            {
                statusText = $"Unofficial / unverified build: {_hashStatusMessage}";
                statusBarTex = _redBarTex;
            }

            var statusLabelRect = new Rect(leftPanelRect.x + 2, statusY, leftPanelRect.width - 4, 24);
            GUI.Label(statusLabelRect, statusText, _statusTextStyle);

            // Colored horizontal bar (height: 6px)
            var barY = statusLabelRect.y + statusLabelRect.height + 4;
            var statusBarRect = new Rect(leftPanelRect.x, barY, leftPanelRect.width, 6);
            if (statusBarTex != null)
            {
                GUI.DrawTexture(statusBarRect, statusBarTex);
            }

            // Right panel
            var rightPanelX = leftPanelRect.x + leftPanelRect.width + 16;
            var rightPanelWidth = Mathf.Max(250, launcherRect.width - rightPanelX - 16);
            var rightPanelRect = new Rect(rightPanelX, bodyY, rightPanelWidth, bodyHeight + 44);
            GUI.Box(rightPanelRect, "", _panelBoxStyle);

            // Right panel inner content
            var contentX = rightPanelRect.x + 18;
            var contentY = rightPanelRect.y + 18;
            var contentWidth = rightPanelRect.width - 36;

            // Profile Section: Avatar square (72x72) + Username + LOGOUT/LOGIN button
            var avatarRect = new Rect(contentX, contentY, 72, 72);
            GUI.Box(avatarRect, "", _boxStyle);
            if (_avatarTexture != null)
            {
                GUI.DrawTexture(new Rect(avatarRect.x + 2, avatarRect.y + 2, 68, 68), _avatarTexture, ScaleMode.ScaleAndCrop);
            }
            else
            {
                GUI.Label(avatarRect, "", _sectionHeaderStyle);
            }

            var profileInfoX = avatarRect.x + avatarRect.width + 14;
            var profileInfoWidth = Mathf.Max(100, contentWidth - (avatarRect.width + 14));

            var displayUser = _veilnetLoggedIn ? _veilnetUsername.ToUpperInvariant() : (!string.IsNullOrWhiteSpace(_profile.OfflineUsername) ? _profile.OfflineUsername.ToUpperInvariant() : "OFFLINE");
            var userLabelRect = new Rect(profileInfoX, contentY + 2, profileInfoWidth, 24);
            GUI.Label(userLabelRect, displayUser, _sectionHeaderStyle);

            var authBtnRect = new Rect(profileInfoX, contentY + 30, profileInfoWidth, 42);
            if (_veilnetLoggedIn)
            {
                if (GUI.Button(authBtnRect, "LOGOUT", _logoutButtonStyle))
                {
                    OnVeilnetResetClicked();
                }
            }
            else
            {
                if (GUI.Button(authBtnRect, "LOGIN", _logoutButtonStyle))
                {
                    OnVeilnetPrimaryActionClicked();
                }
            }

            // Skin Section
            contentY += 94;
            var skinHeaderRect = new Rect(contentX, contentY, contentWidth, 24);
            GUI.Label(skinHeaderRect, "SKIN", _sectionHeaderStyle);

            contentY += 26;
            var skinHeadRect = new Rect(contentX, contentY, 72, 72);
            GUI.Box(skinHeadRect, "", _boxStyle);
            if (_skinHeadTexture != null)
            {
                GUI.DrawTexture(new Rect(skinHeadRect.x + 2, skinHeadRect.y + 2, 68, 68), _skinHeadTexture, ScaleMode.ScaleToFit);
            }
            else
            {
                GUI.Label(skinHeadRect, "", _sectionHeaderStyle);
            }

            var skinsBtnRect = new Rect(profileInfoX, contentY, profileInfoWidth, 72);
            if (GUI.Button(skinsBtnRect, "SKINS", _skinsButtonStyle))
            {
                // Floating panel: real always-on-top OS window over everything.
                if (TryOpenFloatingPanel(FloatingPanelKind.Skins))
                {
                    _showSkinModal = false;
                    _showSettingsModal = false;
                    _showVersionManagerModal = false;
                    _showInstallPromptModal = false;
                    _log.Info("Skin library opened in floating panel.");
                }
                else
                {
                    _showSkinModal = true;
                    _showSettingsModal = false;
                    _showVersionManagerModal = false;
                    _showInstallPromptModal = false;
                    RefreshSkinModalPreview();
                    _log.Info("Skin library opened.");
                }
            }

            // Offline Username Section
            contentY += 92;
            var offlineHeaderRect = new Rect(contentX, contentY, contentWidth, 24);
            GUI.Label(offlineHeaderRect, "OFFLINE USERNAME", _sectionHeaderStyle);

            contentY += 26;
            var offlineFieldRect = new Rect(contentX, contentY, contentWidth, 36);
            _offlineUsernameEdit = GUI.TextField(offlineFieldRect, _offlineUsernameEdit, _textFieldStyle);

            contentY += 44;
            var saveOfflineRect = new Rect(contentX, contentY, 170, 36);
            if (GUI.Button(saveOfflineRect, "Save Offline Name", _buttonStyle))
            {
                if (!string.IsNullOrWhiteSpace(_offlineUsernameEdit))
                {
                    _profile.OfflineUsername = _offlineUsernameEdit.Trim();
                    _profile.Save(_log);
                    _log.Info($"Offline username updated to: {_profile.OfflineUsername}");
                }
            }

            // Bottom Controls Area
            var bottomY = launcherRect.y + launcherRect.height - 96;

            // Bottom-left version cluster:
            //   [ VERSION SELECTOR ]      <- opens the Version Manager directly
            //   [ version dropdown    v ] [folder]
            // The dropdown and the selector are separate controls stacked on top
            // of each other, so the manager never requires opening the dropdown.
            var versionClusterX = launcherRect.x + 16;
            var versionClusterWidth = 250;
            var selectorRect = new Rect(versionClusterX, bottomY + 10, versionClusterWidth, 30);
            var versionRect = new Rect(versionClusterX, bottomY + 44, versionClusterWidth, 30);
            var folderIconRect = new Rect(versionClusterX + versionClusterWidth + 10, bottomY + 22, 44, 44);

            var installedVersions = new System.Collections.Generic.List<GameVersionInfo>();
            foreach (var v in _gameVersions)
            {
                if (v.IsInstalled) installedVersions.Add(v);
            }
            var latestVersion = GetLatestGameVersion();

            // VERSION SELECTOR — opens the Version Manager in one click.
            if (GUI.Button(selectorRect, "VERSION SELECTOR", _buttonStyle))
            {
                OpenVersionManager();
            }

            // Game Folder (Windows 11 style folder icon + hover text)
            if (!modalOpen && folderIconRect.Contains(Event.current.mousePosition))
            {
                hoverTooltip = "Open Game Folder";
                hoverTooltipAnchor = folderIconRect;
            }

            var folderBtnContent = _folderIconTex != null ? new GUIContent(_folderIconTex) : new GUIContent("...");
            if (GUI.Button(folderIconRect, folderBtnContent, _wrenchControlStyle))
            {
                OpenGameFolder();
            }

            // Version dropdown — ONLY installed versions plus a LATEST entry.
            // The full library (all releases, notes, install/uninstall) lives in
            // the Version Manager, reachable via the VERSION SELECTOR button above.
            var vItemHeight = 30;
            var vPopupHeight = (1 + installedVersions.Count + 1) * vItemHeight + 6;
            var versionPopupRect = new Rect(versionRect.x, versionRect.y - vPopupHeight, versionRect.width, vPopupHeight);

            if (Event.current.type == EventType.MouseDown && _versionDropdownOpen)
            {
                if (!versionRect.Contains(Event.current.mousePosition) && !versionPopupRect.Contains(Event.current.mousePosition))
                {
                    _versionDropdownOpen = false;
                }
            }

            string versionButtonText;
            if (_versionsLoading) versionButtonText = "Versions...";
            else if (_selectedIsLatest || _selectedVersion == null)
                versionButtonText = latestVersion != null ? $"LATEST ({latestVersion.Tag})" : "LATEST";
            else versionButtonText = _selectedVersion.ListLabel;
            if (DrawDropdownButton(versionRect, versionButtonText, _versionDropdownOpen))
            {
                _versionDropdownOpen = !_versionDropdownOpen;
                if (_versionDropdownOpen)
                {
                    // Refresh on every open so new uploads appear automatically.
                    _ = RefreshGameVersionsAsync();
                }
            }

            if (_versionDropdownOpen)
            {
                GUI.Box(versionPopupRect, "", _dropdownContainerStyle);
                var vItemY = versionPopupRect.y + 3;

                // LATEST entry
                var latestStatus = latestVersion == null ? "checking..." : (latestVersion.IsInstalled ? "installed" : latestVersion.SizeDisplay);
                var latestSelected = _selectedIsLatest;
                var latestText = latestSelected ? $"> LATEST  ({latestStatus})" : $"   LATEST  ({latestStatus})";
                if (GUI.Button(new Rect(versionPopupRect.x + 2, vItemY, versionPopupRect.width - 4, vItemHeight), latestText, latestSelected ? _dropdownActiveItemStyle : _dropdownItemStyle))
                {
                    _selectedIsLatest = true;
                    _selectedVersion = latestVersion;
                    _versionDropdownOpen = false;
                    _log?.Info($"Selected game version: LATEST ({latestVersion?.Tag ?? "unknown"})");
                }
                vItemY += vItemHeight;

                // Installed versions (short tag + engine badge fits the narrow dropdown;
                // the full manifest name + classification chip lives in the Version Manager)
                foreach (var v in installedVersions)
                {
                    var isSelected = !_selectedIsLatest && _selectedVersion != null && string.Equals(v.Tag, _selectedVersion.Tag, StringComparison.OrdinalIgnoreCase);
                    var vText = isSelected ? $"> {v.Tag}{v.EngineBadge}" : $"   {v.Tag}{v.EngineBadge}";
                    if (v.IsPrerelease) vText += " [pre]";

                    var vStyle = isSelected ? _dropdownActiveItemStyle : _dropdownItemStyle;
                    if (GUI.Button(new Rect(versionPopupRect.x + 2, vItemY, versionPopupRect.width - 4, vItemHeight), vText, vStyle))
                    {
                        _selectedIsLatest = false;
                        _selectedVersion = v;
                        _versionDropdownOpen = false;
                        _log?.Info($"Selected game version: {v.Tag} (installed)");
                    }
                    vItemY += vItemHeight;
                }

                // Version Manager
                if (GUI.Button(new Rect(versionPopupRect.x + 2, vItemY, versionPopupRect.width - 4, vItemHeight), "   MANAGE VERSIONS...", _dropdownItemStyle))
                {
                    _versionDropdownOpen = false;
                    OpenVersionManager();
                }
            }

            // Right-side bottom controls
            var launchBtnWidth = 160;
            var logsBtnWidth = 140;
            var launchBtnHeight = 68;

            var launchBtnX = launcherRect.width - 16 - launchBtnWidth;
            var logsBtnX = launchBtnX - logsBtnWidth - 14;

            // Logs Button
            var logsBtnRect = new Rect(logsBtnX, bottomY + 12, logsBtnWidth, launchBtnHeight);
            if (GUI.Button(logsBtnRect, "Logs", _buttonStyle))
            {
                OpenLogsFolder();
            }

            // Online / Offline Dropdown (positioned directly above LAUNCH button)
            var launchModeWidth = launchBtnWidth;
            var launchModeHeight = 30;
            var launchModeRect = new Rect(launchBtnX, bottomY - 26, launchModeWidth, launchModeHeight);

            var canUseOnline = _hashVerified && _veilnetLoggedIn;
            if (!canUseOnline && string.Equals(launchMode, "Online", StringComparison.OrdinalIgnoreCase))
            {
                launchMode = "Offline";
            }

            var launchModes = new string[] { "Online", "Offline" };
            var selectedIndex = Array.IndexOf(launchModes, launchMode);
            if (selectedIndex < 0) selectedIndex = 1;

            var itemHeight = 30;
            var popupHeight = launchModes.Length * itemHeight + 6;
            var dropdownPopupRect = new Rect(launchBtnX, launchModeRect.y - popupHeight, launchModeWidth, popupHeight);

            if (Event.current.type == EventType.MouseDown && _launchModeDropdownOpen)
            {
                if (!launchModeRect.Contains(Event.current.mousePosition) && !dropdownPopupRect.Contains(Event.current.mousePosition))
                {
                    _launchModeDropdownOpen = false;
                }
            }

            if (DrawDropdownButton(launchModeRect, launchModes[selectedIndex], _launchModeDropdownOpen))
            {
                _launchModeDropdownOpen = !_launchModeDropdownOpen;
            }

            // LAUNCH Button (or Download / progress for non-installed versions)
            var launchBtnRect = new Rect(launchBtnX, bottomY + 12, launchBtnWidth, launchBtnHeight);
            var selectedNeedsDownload = _selectedVersion != null && !_selectedVersion.IsInstalled;
            if (_isLaunching && _gameProcess != null && !_gameProcess.HasExited)
            {
                if (GUI.Button(launchBtnRect, "KILL GAME", _launchButtonStyle))
                {
                    LaunchGame();
                }
            }
            else if (_isDownloadingVersion)
            {
                GUI.Box(launchBtnRect, $"...{(int)(_downloadProgress * 100)}%", _launchButtonStyle);
                if (_downloadProgress > 0.01)
                {
                    var barRect = new Rect(launchBtnRect.x + 2, launchBtnRect.y + launchBtnRect.height - 6, (float)(_downloadProgress * (launchBtnRect.width - 4)), 4);
                    GUI.DrawTexture(barRect, Texture2D.whiteTexture, ScaleMode.StretchToFill);
                }
            }
            else if (selectedNeedsDownload)
            {
                if (GUI.Button(launchBtnRect, "DOWNLOAD", _launchButtonStyle))
                {
                    StartSelectedVersionDownload();
                }
            }
            else
            {
                if (GUI.Button(launchBtnRect, "LAUNCH", _launchButtonStyle))
                {
                    LaunchGame();
                }
            }

            // Render open dropdown menu on top
            if (_launchModeDropdownOpen)
            {
                GUI.Box(dropdownPopupRect, "", _dropdownContainerStyle);
                for (int i = 0; i < launchModes.Length; i++)
                {
                    var mode = launchModes[i];
                    var isSelected = string.Equals(mode, launchMode, StringComparison.OrdinalIgnoreCase);
                    var itemRect = new Rect(dropdownPopupRect.x + 2, dropdownPopupRect.y + 3 + (i * itemHeight), dropdownPopupRect.width - 4, itemHeight);

                    var isOnlineOption = string.Equals(mode, "Online", StringComparison.OrdinalIgnoreCase);
                    var isAllowed = !isOnlineOption || canUseOnline;

                    string itemText;
                    if (isOnlineOption && !_hashVerificationComplete)
                    {
                        itemText = "  Online (Checking...)";
                    }
                    else if (isOnlineOption && !_hashVerified)
                    {
                        itemText = "  Online (Unverified)";
                    }
                    else if (isOnlineOption && !_veilnetLoggedIn)
                    {
                        itemText = "  Online (Login Req)";
                    }
                    else
                    {
                        itemText = isSelected ? $"> {mode}" : $"   {mode}";
                    }

                    var style = isSelected ? _dropdownActiveItemStyle : _dropdownItemStyle;
                    if (GUI.Button(itemRect, itemText, style))
                    {
                        if (isAllowed)
                        {
                            launchMode = mode;
                            _launchModeDropdownOpen = false;
                            _log.Info($"Launch mode set to {launchMode}");
                        }
                        else
                        {
                            if (!_hashVerified)
                            {
                                _log.Warn($"Cannot switch to Online mode: Build hash is not verified against Supabase ({_hashStatusMessage}).");
                            }
                            else if (!_veilnetLoggedIn)
                            {
                                _log.Warn("Cannot switch to Online mode: Veilnet login is required.");
                            }
                            _launchModeDropdownOpen = false;
                        }
                    }
                }
            }

            // Restore GUI enabled before drawing modals
            GUI.enabled = true;

            // Render Modals if open (install prompt sits on top of everything)
            if (_showInstallPromptModal)
            {
                DrawInstallPromptModal(launcherRect);
            }
            else if (_showVersionManagerModal)
            {
                DrawVersionManagerModal(launcherRect);
            }
            else if (_showSettingsModal)
            {
                DrawSettingsModal(launcherRect);
            }
            else if (_showSkinModal)
            {
                DrawSkinModal(launcherRect);
            }

            // Hover tooltip — drawn beside the hovered control so it never sticks
            // to the cursor, and sized from the text so nothing is clipped.
            if (!modalOpen && !string.IsNullOrEmpty(hoverTooltip))
            {
                if (_tooltipStyle == null)
                {
                    _tooltipStyle = new GUIStyle(_labelStyle)
                    {
                        alignment = TextAnchor.MiddleLeft,
                        fontSize = 14,
                        fontStyle = FontStyle.Bold,
                        padding = new RectOffset(14, 14, 8, 8),
                        wordWrap = false
                    };
                    _tooltipStyle.normal.textColor = new Color(0.95f, 0.95f, 0.95f);
                }

                var tipContent = new GUIContent(hoverTooltip);
                var tipSize = _tooltipStyle.CalcSize(tipContent);
                var tipRect = new Rect(
                    hoverTooltipAnchor.x + hoverTooltipAnchor.width + 12,
                    hoverTooltipAnchor.y + (hoverTooltipAnchor.height - tipSize.y) * 0.5f,
                    tipSize.x + 2,
                    tipSize.y + 2);

                // Flip above the button when it would run off the right edge.
                if (tipRect.x + tipRect.width > Screen.width - 4)
                {
                    tipRect.x = hoverTooltipAnchor.x + hoverTooltipAnchor.width * 0.5f - tipRect.width * 0.5f;
                    tipRect.y = hoverTooltipAnchor.y - tipRect.height - 8;
                }

                tipRect.x = Mathf.Clamp(tipRect.x, 4, Mathf.Max(4, Screen.width - tipRect.width - 4));
                tipRect.y = Mathf.Clamp(tipRect.y, 4, Mathf.Max(4, Screen.height - tipRect.height - 4));

                GUI.enabled = true;
                GUI.Box(tipRect, "", _boxStyle);
                GUI.Label(tipRect, tipContent, _tooltipStyle);
            }
        }

        /// <summary>
        /// Dropdown button with a context-menu style separator line between the
        /// label and the arrow zone. Returns true when clicked.
        /// </summary>
        private bool DrawDropdownButton(Rect rect, string text, bool isOpen)
        {
            var clicked = GUI.Button(rect, text, _dropdownStyle);

            // Light-dark vertical separator line before the arrow zone
            const float arrowZoneWidth = 26f;
            var prevColor = GUI.color;
            GUI.color = new Color(0.62f, 0.62f, 0.62f, 0.5f);
            GUI.DrawTexture(new Rect(rect.x + rect.width - arrowZoneWidth, rect.y + 5, 1, rect.height - 10), Texture2D.whiteTexture, ScaleMode.StretchToFill);
            GUI.color = prevColor;

            if (_dropdownArrowStyle == null)
            {
                _dropdownArrowStyle = new GUIStyle(_dropdownStyle)
                {
                    alignment = TextAnchor.MiddleCenter
                };
            }

            GUI.Label(new Rect(rect.x + rect.width - arrowZoneWidth, rect.y, arrowZoneWidth, rect.height), isOpen ? "^" : "v", _dropdownArrowStyle);

            return clicked;
        }

        /// <summary>Shared drag handling for movable modal windows (header region only).
        /// Returns the pointer delta while the window is being dragged.</summary>
        private Vector2 GetModalDragDelta(Rect dragRect, ref bool dragging)
        {
            if (Event.current.type == EventType.MouseDown && dragRect.Contains(Event.current.mousePosition))
            {
                dragging = true;
                Event.current.Use();
            }
            else if (Event.current.type == EventType.MouseDrag && dragging)
            {
                Event.current.Use();
                return Event.current.delta;
            }
            else if (Event.current.type == EventType.MouseUp && dragging)
            {
                dragging = false;
                Event.current.Use();
            }
            return Vector2.zero;
        }

        /// <summary>
        /// Spawns this exe as a floating panel process (real topmost OS window).
        /// Returns false when the panel could not be started (caller falls back
        /// to the in-window modal). The panel shares all on-disk state because
        /// it is the same binary reading the same folders.
        /// </summary>
        private bool TryOpenFloatingPanel(FloatingPanelKind kind)
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath) || IsEditorOrHostProcess(exePath))
                    return false;

                var arg = kind == FloatingPanelKind.Versions ? "--panel=versions" : "--panel=skins";
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arg,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.EnvironmentVariables["LATTICEVEIL_INSTALL_ROOT"] = Core.Paths.InstallRootDir;

                var proc = Process.Start(startInfo);
                return proc != null;
            }
            catch (Exception ex)
            {
                _log?.Warn($"Floating panel could not start, falling back to in-window modal: {ex.Message}");
                return false;
            }
        }

        private void OpenVersionManager()
        {
            // Floating panel: open in its own real always-on-top OS window that
            // floats over the launcher (and other apps). Falls back to the
            // in-window modal if the panel process cannot be started.
            if (TryOpenFloatingPanel(FloatingPanelKind.Versions))
            {
                _showVersionManagerModal = false;
                _showSettingsModal = false;
                _showSkinModal = false;
                _log.Info("Version Manager opened in floating panel.");
                return;
            }

            _showVersionManagerModal = true;
            _showSettingsModal = false;
            _showSkinModal = false;
            _showInstallPromptModal = false;
            _versionManagerSelected = GetLatestGameVersion();
            _ = RefreshGameVersionsAsync();
            _log?.Info("Version manager opened.");
        }

        private GameVersionInfo GetLatestGameVersion() =>
            _gameVersions.Count > 0 ? _gameVersions[0] : null;

        private void StartVersionDownload(GameVersionInfo version)
        {
            if (version == null || _isDownloadingVersion) return;
            _selectedVersion = version;
            _selectedIsLatest = false;
            StartSelectedVersionDownload();
        }

        private void UninstallGameVersion(GameVersionInfo version)
        {
            if (version == null || string.IsNullOrWhiteSpace(version.Tag)) return;
            try
            {
                var safeTag = version.Tag.Trim();
                foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                    safeTag = safeTag.Replace(c, '_');
                var dir = System.IO.Path.Combine(Core.Paths.VersionsDir, safeTag);
                if (System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.Delete(dir, true);
                    _log?.Info($"Uninstalled version {version.Tag} ({dir}).");
                }
                else
                {
                    _log?.Warn($"Uninstall requested but folder is missing: {dir}");
                }

                if (_selectedVersion != null && string.Equals(_selectedVersion.Tag, version.Tag, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedVersion = null;
                    _selectedIsLatest = true;
                }
                if (_versionManagerSelected != null && string.Equals(_versionManagerSelected.Tag, version.Tag, StringComparison.OrdinalIgnoreCase))
                    _versionManagerSelected = null;

                _ = RefreshGameVersionsAsync();
            }
            catch (Exception ex)
            {
                _log?.Error($"Failed to uninstall {version.Tag}: {ex.Message}");
                _versionStatusMessage = $"Uninstall failed: {ex.Message}";
            }
        }

        /// <summary>
        /// Decides what to do about the latest release after a version refresh:
        /// auto-install when the setting is on, otherwise show the one-time
        /// "WOULD YOU LIKE TO INSTALL THE LATEST LATTICEVEIL VERSION?" prompt.
        /// </summary>
        private void EvaluateLatestInstallPrompt()
        {
            if (_latestInstallPromptDecided) return;
            _latestInstallPromptDecided = true;

            var latest = GetLatestGameVersion();
            if (latest == null || latest.IsInstalled) return;
            if (_settings == null || !_settings.AutoUpdateChecksEnabled) return;

            if (_settings.AutoInstallUpdatesEnabled)
            {
                _log?.Info($"Auto-install enabled: downloading {latest.Tag} automatically.");
                StartVersionDownload(latest);
            }
            else
            {
                _showInstallPromptModal = true;
            }
        }

        private string GetVersionNotes(GameVersionInfo version)
        {
            if (version == null) return "Select a version to read its update notes.";
            if (!string.IsNullOrWhiteSpace(version.Body)) return version.Body;
            return $"No update notes were published for {version.DisplayName}.";
        }

        /// <summary>
        /// Startup prompt: "WOULD YOU LIKE TO INSTALL THE LATEST LATTICEVEIL VERSION?"
        /// Shows the latest release with its update notes; INSTALL or SKIP.
        /// </summary>
        private void DrawInstallPromptModal(Rect screenRect)
        {
            GUI.Box(screenRect, "", _dimmerStyle);

            const float modalWidth = 640f;
            const float modalHeight = 480f;
            var modalRect = new Rect((screenRect.width - modalWidth) * 0.5f, (screenRect.height - modalHeight) * 0.5f, modalWidth, modalHeight);

            if (Event.current.type == EventType.MouseDown && screenRect.Contains(Event.current.mousePosition) && !modalRect.Contains(Event.current.mousePosition))
            {
                Event.current.Use();
            }

            GUI.Box(modalRect, "", _boxStyle);

            var headerRect = new Rect(modalRect.x, modalRect.y, modalRect.width, 42);
            if (_settingsHeaderTex != null) GUI.DrawTexture(headerRect, _settingsHeaderTex);
            else GUI.Box(headerRect, "", _modalHeaderStyle);

            var headerDragRect = new Rect(modalRect.x + 8, modalRect.y + 8, modalRect.width - 70, 30);
            var dragDelta = GetModalDragDelta(headerDragRect, ref _draggingInstallPromptModal);
            modalRect.x += dragDelta.x;
            modalRect.y += dragDelta.y;

            var latest = GetLatestGameVersion();

            GUI.Label(new Rect(modalRect.x + 18, modalRect.y + 8, modalRect.width - 80, 28),
                "WOULD YOU LIKE TO INSTALL THE LATEST LATTICEVEIL VERSION?", _modalHeaderTitleStyle);

            var versionLine = latest != null
                ? $"{latest.DisplayName}   ({latest.ListLabel}  •  {latest.SizeDisplay})"
                : "Latest release: checking...";
            GUI.Label(new Rect(modalRect.x + 18, modalRect.y + 52, modalRect.width - 36, 24), versionLine, _sectionHeaderStyle);

            var notesRect = new Rect(modalRect.x + 18, modalRect.y + 82, modalRect.width - 36, modalRect.height - 172);
            GUI.Box(notesRect, "", _panelBoxStyle);

            var notesBody = GetVersionNotes(latest);
            var notesContentHeight = Mathf.Max(notesRect.height - 16, notesBody.Length * 14f);
            _installPromptNotesScroll = GUI.BeginScrollView(
                new Rect(notesRect.x + 8, notesRect.y + 8, notesRect.width - 16, notesRect.height - 16),
                _installPromptNotesScroll,
                new Rect(0, 0, notesRect.width - 36, notesContentHeight), false, true);
            GUI.Label(new Rect(0, 0, notesRect.width - 36, notesContentHeight), notesBody, _logStyle);
            GUI.EndScrollView();

            var btnY = modalRect.y + modalRect.height - 74;
            var wasEnabled = GUI.enabled;
            if (_isDownloadingVersion) GUI.enabled = false;

            var installRect = new Rect(modalRect.x + 18, btnY, 200, 44);
            if (GUI.Button(installRect, "INSTALL LATEST", _launchButtonStyle))
            {
                _showInstallPromptModal = false;
                if (latest != null) StartVersionDownload(latest);
            }

            GUI.enabled = wasEnabled;

            var skipRect = new Rect(modalRect.x + modalRect.width - 158, btnY, 140, 44);
            if (GUI.Button(skipRect, "SKIP", _buttonStyle))
            {
                _showInstallPromptModal = false;
                _log?.Info("Latest-version install prompt dismissed (skipped).");
            }

            var hintRect = new Rect(modalRect.x + 236, btnY + 12, modalRect.width - 236 - 158, 24);
            GUI.Label(hintRect, "You can install it any time from MANAGE VERSIONS.", _labelStyle);
        }

        /// <summary>
        /// Movable Version Manager overlay: every GitHub release with a zip asset,
        /// its update notes, and install/reinstall/uninstall actions.
        /// </summary>
        private void DrawVersionManagerModal(Rect screenRect)
        {
            GUI.Box(screenRect, "", _dimmerStyle);

            // Clamp the movable window inside the launcher window.
            _versionManagerRect.x = Mathf.Clamp(_versionManagerRect.x, 0, Mathf.Max(0, screenRect.width - _versionManagerRect.width));
            _versionManagerRect.y = Mathf.Clamp(_versionManagerRect.y, 0, Mathf.Max(0, screenRect.height - _versionManagerRect.height));

            if (Event.current.type == EventType.MouseDown && screenRect.Contains(Event.current.mousePosition) && !_versionManagerRect.Contains(Event.current.mousePosition))
            {
                Event.current.Use();
            }

            var modalRect = _versionManagerRect;
            GUI.Box(modalRect, "", _boxStyle);

            // Movable header (excludes the close button hitbox)
            var headerRect = new Rect(modalRect.x, modalRect.y, modalRect.width, 42);
            if (_settingsHeaderTex != null) GUI.DrawTexture(headerRect, _settingsHeaderTex);
            else GUI.Box(headerRect, "", _modalHeaderStyle);

            var headerDragRect = new Rect(modalRect.x + 8, modalRect.y + 8, modalRect.width - 70, 30);
            var dragDelta = GetModalDragDelta(headerDragRect, ref _draggingVersionManager);
            _versionManagerRect.x += dragDelta.x;
            _versionManagerRect.y += dragDelta.y;

            GUI.Label(new Rect(modalRect.x + 18, modalRect.y + 10, 300, 24), "VERSION MANAGER", _modalHeaderTitleStyle);

            var closeBtnRect = new Rect(modalRect.x + modalRect.width - 46, modalRect.y + 5, 38, 32);
            if (GUI.Button(closeBtnRect, "X", _modalCloseBtnStyle))
            {
                _showVersionManagerModal = false;
                Event.current.Use();
                return;
            }

            var wasEnabled = GUI.enabled;

            // Left: version list (all releases with a zip asset)
            var listRect = new Rect(modalRect.x + 16, modalRect.y + 54, 470, modalRect.height - 122);
            GUI.Box(listRect, "", _panelBoxStyle);
            var rowHeight = 68f;
            var listContent = new Rect(0, 0, listRect.width - 20, 8 + _gameVersions.Count * rowHeight);
            _versionManagerListScroll = GUI.BeginScrollView(listRect, _versionManagerListScroll, listContent, false, true);

            for (int i = 0; i < _gameVersions.Count; i++)
            {
                DrawVersionManagerRow(new Rect(4, 4 + i * rowHeight, listContent.width, rowHeight - 6), _gameVersions[i]);
            }

            if (_gameVersions.Count == 0)
            {
                GUI.Label(new Rect(8, 8, listContent.width - 8, 40), _versionsLoading ? "Checking releases..." : "No downloadable releases found.", _labelStyle);
            }

            GUI.EndScrollView();

            // Right: update notes for the selected release
            var notesRect = new Rect(modalRect.x + 502, modalRect.y + 54, modalRect.width - 518, modalRect.height - 122);
            GUI.Box(notesRect, "", _panelBoxStyle);
            GUI.Label(new Rect(notesRect.x + 12, notesRect.y + 8, notesRect.width - 24, 24), "UPDATE NOTES", _sectionHeaderStyle);

            var notesBody = GetVersionNotes(_versionManagerSelected);
            var notesContentHeight = Mathf.Max(notesRect.height - 70, notesBody.Length * 14f);
            _versionManagerNotesScroll = GUI.BeginScrollView(
                new Rect(notesRect.x + 8, notesRect.y + 36, notesRect.width - 16, notesRect.height - 46),
                _versionManagerNotesScroll,
                new Rect(0, 0, notesRect.width - 36, notesContentHeight), false, true);
            GUI.Label(new Rect(0, 0, notesRect.width - 36, notesContentHeight), notesBody, _logStyle);
            GUI.EndScrollView();

            // Footer: refresh + status
            GUI.enabled = wasEnabled;
            var footerY = modalRect.y + modalRect.height - 58;
            GUI.Label(new Rect(modalRect.x + 18, footerY + 10, modalRect.width - 200, 26),
                _isDownloadingVersion ? $"Downloading {_selectedVersion?.Tag ?? ""}... {(int)(_downloadProgress * 100)}%" : _versionStatusMessage,
                _statusTextStyle);

            var refreshRect = new Rect(modalRect.x + modalRect.width - 150, footerY + 6, 132, 36);
            if (GUI.Button(refreshRect, "REFRESH", _buttonStyle))
            {
                _ = RefreshGameVersionsAsync();
            }
        }

        private GUIStyle _rowTitleStyle;
        private GUIStyle _rowMetaStyle;
        private GUIStyle _engineChipStyle;

        private void EnsureVersionRowStyles()
        {
            if (_rowTitleStyle == null)
            {
                _rowTitleStyle = new GUIStyle(_labelStyle)
                {
                    fontSize = 13,
                    fontStyle = FontStyle.Bold,
                    wordWrap = false,
                    clipping = TextClipping.Clip
                };
                _rowTitleStyle.normal.textColor = new Color(0.95f, 0.95f, 0.95f);
            }

            if (_rowMetaStyle == null)
            {
                _rowMetaStyle = new GUIStyle(_labelStyle)
                {
                    fontSize = 11,
                    wordWrap = false,
                    clipping = TextClipping.Overflow
                };
                _rowMetaStyle.normal.textColor = new Color(0.65f, 0.65f, 0.65f);
            }

            if (_engineChipStyle == null)
            {
                _engineChipStyle = new GUIStyle(_labelStyle)
                {
                    fontSize = 10,
                    fontStyle = FontStyle.Bold,
                    wordWrap = false,
                    alignment = TextAnchor.MiddleCenter,
                    padding = new RectOffset(8, 8, 2, 2),
                    margin = new RectOffset(0, 0, 0, 0)
                };
                _engineChipStyle.normal.textColor = new Color(1f, 0.78f, 0.3f);
            }
        }

        /// <summary>Trims text with an ellipsis until it fits maxWidth in the given style.</summary>
        private static string TruncateToWidth(string text, GUIStyle style, float maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0)
                return text ?? string.Empty;

            if (style.CalcSize(new GUIContent(text)).x <= maxWidth)
                return text;

            var clipped = text.TrimEnd();
            while (clipped.Length > 1 && style.CalcSize(new GUIContent(clipped.TrimEnd() + "…")).x > maxWidth)
            {
                clipped = clipped.Substring(0, clipped.Length - 1);
            }
            return clipped.TrimEnd() + "…";
        }

        private void DrawVersionManagerRow(Rect rowRect, GameVersionInfo version)
        {
            var isSelected = _versionManagerSelected != null && string.Equals(version.Tag, _versionManagerSelected.Tag, StringComparison.OrdinalIgnoreCase);
            GUI.Box(rowRect, "", isSelected ? _dropdownActiveItemStyle : _panelBoxStyle);

            if (isSelected)
            {
                GUI.DrawTexture(new Rect(rowRect.x, rowRect.y, 3, rowRect.height), Texture2D.whiteTexture, ScaleMode.StretchToFill);
            }

            // Clicking anywhere on the row (except buttons) selects the release.
            if (Event.current.type == EventType.MouseDown && rowRect.Contains(Event.current.mousePosition))
            {
                _versionManagerSelected = version;
                _versionManagerNotesScroll = Vector2.zero;
            }

            EnsureVersionRowStyles();

            const float buttonZoneWidth = 118f;
            var textWidth = rowRect.width - buttonZoneWidth - 24;

            // Title — single line, ellipsized when long so it can never run under
            // the engine chip (which killed the old layout).
            var isLegacy = version.IsLegacyEngine;
            var titleStyle = _rowTitleStyle;
            var titleText = string.IsNullOrWhiteSpace(version.DisplayName) ? version.Tag : version.DisplayName;

            // Engine classification chip sits inline after the title; reserve room for it.
            var chipWidth = 0f;
            if (isLegacy)
            {
                chipWidth = _engineChipStyle.CalcSize(new GUIContent("MONOGAME")).x + 2;
            }

            var maxTitleWidth = textWidth - (isLegacy ? chipWidth + 8 : 0);
            titleText = TruncateToWidth(titleText, titleStyle, maxTitleWidth);
            var titleRect = new Rect(rowRect.x + 12, rowRect.y + 5, maxTitleWidth, 20);
            GUI.Label(titleRect, new GUIContent(titleText), titleStyle);

            if (isLegacy && chipWidth > 0)
            {
                var chipRect = new Rect(titleRect.x + titleRect.width + 8, rowRect.y + 6, chipWidth, 18);
                var prevColor = GUI.color;
                GUI.color = new Color(1f, 0.72f, 0.2f, 0.16f);
                GUI.DrawTexture(chipRect, Texture2D.whiteTexture, ScaleMode.StretchToFill);
                GUI.color = prevColor;
                GUI.Label(chipRect, "MONOGAME", _engineChipStyle);
            }

            // Meta line: release tag (+ prerelease marker).
            var tagText = version.Tag + (version.IsPrerelease ? "   [pre]" : "");
            GUI.Label(new Rect(rowRect.x + 12, rowRect.y + 27, textWidth, 16), tagText, _rowMetaStyle);

            // Status line.
            var statusText = version.IsInstalled
                ? $"INSTALLED  •  {version.SizeDisplay}"
                : $"{version.SizeDisplay}  •  {version.PublishedAt}";
            GUI.Label(new Rect(rowRect.x + 12, rowRect.y + 45, textWidth, 16), statusText, _rowMetaStyle);

            var wasEnabled = GUI.enabled;
            if (_isDownloadingVersion) GUI.enabled = false;

            // INSTALL only for uninstalled versions — reinstalling is just
            // uninstall + install, so installed versions offer UNINSTALL only.
            var rowButtonRect = new Rect(rowRect.x + rowRect.width - 112, rowRect.y + 20, 100, 28);
            if (!version.IsInstalled)
            {
                if (GUI.Button(rowButtonRect, "INSTALL", _buttonStyle))
                {
                    _versionManagerSelected = version;
                    StartVersionDownload(version);
                }
            }
            else if (GUI.Button(rowButtonRect, "UNINSTALL", _buttonStyle))
            {
                UninstallGameVersion(version);
            }

            GUI.enabled = wasEnabled;
        }

        private void RefreshSkinModalPreview()
        {
            try
            {
                if (_stagedSkinTex != null)
                {
                    _currentLoadedSkinTex = _stagedSkinTex;
                }
                else
                {
                    _currentLoadedSkinTex = SkinManager.LoadActiveSkinTexture();
                }
                RenderSkinPreview();
                _skinHeadTexture = ExtractHeadTexture(_currentLoadedSkinTex);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[SkinModal] Failed generating preview: {ex.Message}");
            }
        }

        private void RenderSkinPreview()
        {
            _skin3DPreviewTexture = PlayerSkinPreviewGenerator.GeneratePreview(
                _currentLoadedSkinTex, _skinPreviewYaw, _skinPreviewPitch, _skinPreviewLayers);
        }

        private float leftPanelHeightWithoutStatus(float totalBodyHeight)
        {
            return Mathf.Max(60, totalBodyHeight - 44);
        }

        private void DrawSettingsModal(Rect screenRect)
        {
            // Dimmed background
            GUI.Box(screenRect, "", _dimmerStyle);

            // Modal rect centered - larger size
            float modalWidth = 680;
            float modalHeight = 520;
            var modalRect = new Rect((screenRect.width - modalWidth) * 0.5f + _settingsModalOffset.x, (screenRect.height - modalHeight) * 0.5f + _settingsModalOffset.y, modalWidth, modalHeight);

            // Eat clicks on dimmer to prevent click-through
            if (Event.current.type == EventType.MouseDown && screenRect.Contains(Event.current.mousePosition))
            {
                if (!modalRect.Contains(Event.current.mousePosition))
                {
                    Event.current.Use();
                }
            }

            // Modal background
            GUI.Box(modalRect, "", _boxStyle);

            // Header Bar (sleek dark themed, matching launcher theme)
            var headerRect = new Rect(modalRect.x, modalRect.y, modalRect.width, 42);
            if (_settingsHeaderTex != null)
            {
                GUI.DrawTexture(headerRect, _settingsHeaderTex);
            }
            else
            {
                GUI.Box(headerRect, "", _modalHeaderStyle);
            }

            // Header Title
            var headerTitleRect = new Rect(headerRect.x + 18, headerRect.y + 10, 240, 24);
            GUI.Label(headerTitleRect, "Launcher Settings", _modalHeaderTitleStyle);

            // Movable header (excludes the close button hitbox)
            var settingsHeaderDragRect = new Rect(modalRect.x + 8, modalRect.y + 8, modalRect.width - 70, 30);
            var settingsDragDelta = GetModalDragDelta(settingsHeaderDragRect, ref _draggingSettingsModal);
            _settingsModalOffset += settingsDragDelta;
            modalRect.x += settingsDragDelta.x;
            modalRect.y += settingsDragDelta.y;

            // Header Close Button (X) - distinct hitbox and event consumption
            var closeBtnRect = new Rect(headerRect.x + headerRect.width - 46, headerRect.y + 5, 38, 32);
            if (GUI.Button(closeBtnRect, "X", _modalCloseBtnStyle))
            {
                _showSettingsModal = false;
                Event.current.Use();
            }

            // Tabs Row
            var tabsY = headerRect.y + headerRect.height;
            var tabWidth = 110f;
            var tabHeight = 34f;
            for (int i = 0; i < _settingsTabs.Length; i++)
            {
                var tabRect = new Rect(modalRect.x + (i * tabWidth), tabsY, tabWidth, tabHeight);
                var isSelected = (_settingsTabIndex == i);
                var style = isSelected ? _modalTabActiveStyle : _modalTabInactiveStyle;

                if (GUI.Button(tabRect, _settingsTabs[i], style))
                {
                    _settingsTabIndex = i;
                }
            }

            // Tab Content Body
            var tabBodyY = tabsY + 50;
            var contentX = modalRect.x + 36;
            var contentWidth = modalRect.width - 72;

            if (_settingsTabIndex == 0) // General Tab
            {
                var rowY = tabBodyY;
                const float rowHeight = 44f;

                var newKeepOpen = DrawSwitch(new Rect(contentX, rowY, contentWidth, rowHeight), _settings.KeepLauncherOpen, "Keep Launcher Open", "Leave launcher running after starting the game");
                if (newKeepOpen != _settings.KeepLauncherOpen)
                {
                    _settings.KeepLauncherOpen = newKeepOpen;
                    _settings.Save(_log);
                }

                rowY += rowHeight + 10;
                var newInstantQuit = DrawSwitch(new Rect(contentX, rowY, contentWidth, rowHeight), _settings.InstantQuitEnabled, "Instant Quit", "Close the launcher entirely when the game starts (no return to launcher)");
                if (newInstantQuit != _settings.InstantQuitEnabled)
                {
                    _settings.InstantQuitEnabled = newInstantQuit;
                    _settings.Save(_log);
                }

                rowY += rowHeight + 10;
                var newDarkMode = DrawSwitch(new Rect(contentX, rowY, contentWidth, rowHeight), _settings.DarkMode, "Dark Mode", "Use pure OLED dark color theme");
                if (newDarkMode != _settings.DarkMode)
                {
                    _settings.DarkMode = newDarkMode;
                    _settings.Save(_log);
                }

                rowY += rowHeight + 10;
                var newAutoUpdates = DrawSwitch(new Rect(contentX, rowY, contentWidth, rowHeight), _settings.AutoUpdateChecksEnabled, "Automatic Update Checks", "Check for latest updates when launcher opens");
                if (newAutoUpdates != _settings.AutoUpdateChecksEnabled)
                {
                    _settings.AutoUpdateChecksEnabled = newAutoUpdates;
                    _settings.Save(_log);
                }

                rowY += rowHeight + 10;
                var newAutoInstall = DrawSwitch(new Rect(contentX, rowY, contentWidth, rowHeight), _settings.AutoInstallUpdatesEnabled, "Automatic Update Install", "Download and install the latest game version automatically (no prompt)");
                if (newAutoInstall != _settings.AutoInstallUpdatesEnabled)
                {
                    _settings.AutoInstallUpdatesEnabled = newAutoInstall;
                    _settings.Save(_log);
                }

                rowY += rowHeight + 10;
                var newAutoAssets = DrawSwitch(new Rect(contentX, rowY, contentWidth, rowHeight), _settings.AutoTextureDownloadsEnabled, "Automatic Texture Downloads", "Sync default textures on launch (disable to preserve manual asset edits)");
                if (newAutoAssets != _settings.AutoTextureDownloadsEnabled)
                {
                    _settings.AutoTextureDownloadsEnabled = newAutoAssets;
                    _settings.Save(_log);
                }

                rowY += rowHeight + 16;
                var clearTrayRect = new Rect(contentX, rowY, 220, 36);
                if (GUI.Button(clearTrayRect, "CLEAR TRAY SETTINGS", _buttonStyle))
                {
                    _settings.AlwaysMinimizeLauncherToTray = false;
                    _settings.LauncherCloseButtonAction = "";
                    _settings.Save(_log);
                    _log.Info("Tray settings cleared.");
                }

                rowY += 46;
                var noteRect = new Rect(contentX, rowY, contentWidth, 36);
                GUI.Label(noteRect, "These settings are launcher-only. Game saves and worlds are not changed here.", _labelStyle);
            }
            else if (_settingsTabIndex == 1) // Updates Tab
            {
                var autoUpdatesText = _settings.AutoUpdateChecksEnabled ? "Automatic Update Checks: Enabled" : "Automatic Update Checks: Disabled";
                GUI.Label(new Rect(contentX, tabBodyY, contentWidth, 28), autoUpdatesText, _sectionHeaderStyle);
                GUI.Label(new Rect(contentX, tabBodyY + 28, contentWidth, 24), _settings.AutoInstallUpdatesEnabled
                    ? "Automatic Update Install: Enabled (latest version installs silently)"
                    : "Automatic Update Install: Disabled (prompt on new release)", _labelStyle);
                GUI.Label(new Rect(contentX, tabBodyY + 38, contentWidth, 28), $"Current Channel: {(Paths.IsDevBuild ? "DEV" : "Release")}", _labelStyle);
                GUI.Label(new Rect(contentX, tabBodyY + 70, contentWidth, 28), $"Build Hash: {(_hashVerified ? "Verified Official" : "Unverified")}", _labelStyle);
                GUI.Label(new Rect(contentX, tabBodyY + 102, contentWidth, 28), $"Texture Downloads: {(_settings.AutoTextureDownloadsEnabled ? "Automatic Sync" : "Manual / Preserved")}", _labelStyle);
            }
            else if (_settingsTabIndex == 2) // Cleanup Tab
            {
                GUI.Label(new Rect(contentX, tabBodyY, contentWidth, 28), "Maintenance & Cache Cleanup", _sectionHeaderStyle);
                var cleanCacheBtn = new Rect(contentX, tabBodyY + 40, 220, 38);
                if (GUI.Button(cleanCacheBtn, "Clean Online Cache", _buttonStyle))
                {
                    _log.Info("Online cache cleaned.");
                }
            }
            else if (_settingsTabIndex == 3) // Status Tab
            {
                GUI.Label(new Rect(contentX, tabBodyY, contentWidth, 28), "System & Network Status", _sectionHeaderStyle);
                GUI.Label(new Rect(contentX, tabBodyY + 38, contentWidth, 28), $"Veilnet Auth: {(_veilnetLoggedIn ? $"Logged in ({_veilnetUsername})" : "Not logged in")}", _labelStyle);
                GUI.Label(new Rect(contentX, tabBodyY + 70, contentWidth, 28), $"Build Status: {_hashStatusMessage}", _labelStyle);
            }
        }

        private void DrawSkinModal(Rect screenRect)
        {
            DrawSkinLibraryModal(screenRect);

#if false

            // Dimmed background
            GUI.Box(screenRect, "", _dimmerStyle);

            // Modal rect centered
            float modalWidth = 740;
            float modalHeight = 540;
            var modalRect = new Rect((screenRect.width - modalWidth) * 0.5f, (screenRect.height - modalHeight) * 0.5f, modalWidth, modalHeight);

            // Eat clicks outside modal
            if (Event.current.type == EventType.MouseDown && screenRect.Contains(Event.current.mousePosition))
            {
                if (!modalRect.Contains(Event.current.mousePosition))
                {
                    Event.current.Use();
                }
            }

            // Modal background
            GUI.Box(modalRect, "", _boxStyle);

            bool isConfirmOpen = _showSkinApplyConfirmDialog || _showRevertChoiceDialog;

            // When confirmation popup is open, disable all underlying skin modal controls
            GUI.enabled = !isConfirmOpen;

            // Header (transparent without darker bar behind title, title centered vertically)
            var headerRect = new Rect(modalRect.x, modalRect.y, modalRect.width, 46);

            // Header Title (vertically centered)
            var headerTitleRect = new Rect(headerRect.x + 24, headerRect.y, headerRect.width - 80, headerRect.height);
            GUI.Label(headerTitleRect, "Player Skin Customizer", _modalHeaderTitleStyle);

            // Header Close Button (X)
            var closeBtnRect = new Rect(headerRect.x + headerRect.width - 46, headerRect.y + 7, 38, 32);
            if (GUI.Button(closeBtnRect, "X", _modalCloseBtnStyle))
            {
                _showSkinModal = false;
                _stagedSkinPath = null;
                _stagedSkinTex = null;
                _stagedSkinHash = null;
                _stagedSkinIsReset = false;
                _showSkinApplyConfirmDialog = false;
                _showRevertChoiceDialog = false;
                RefreshSkinModalPreview();
                Event.current.Use();
            }

            var contentY = headerRect.y + headerRect.height + 10;

            // Left column: 3D Player Preview Box (width 260)
            var previewBoxRect = new Rect(modalRect.x + 24, contentY, 260, 450);
            GUI.Box(previewBoxRect, "", _panelBoxStyle);

            // Mouse Drag Interaction for 3D rotation (only when confirm popup is not open)
            if (!isConfirmOpen)
            {
                var e = Event.current;
                if (e.type == EventType.MouseDown && previewBoxRect.Contains(e.mousePosition) && e.button == 0)
                {
                    _isDraggingSkinModel = true;
                    _lastSkinDragMouseX = e.mousePosition.x;
                    _lastSkinDragMouseY = e.mousePosition.y;
                    e.Use();
                }
                else if (e.type == EventType.MouseDrag && _isDraggingSkinModel)
                {
                    float delta = e.mousePosition.x - _lastSkinDragMouseX;
                    float pitchDelta = e.mousePosition.y - _lastSkinDragMouseY;
                    _lastSkinDragMouseX = e.mousePosition.x;
                    _lastSkinDragMouseY = e.mousePosition.y;
                    delta = Mathf.Clamp(delta, -12f, 12f);
                    pitchDelta = Mathf.Clamp(pitchDelta, -12f, 12f);
                    _skinPreviewTargetYaw += delta * 0.75f;
                    _skinPreviewTargetPitch = Mathf.Clamp(_skinPreviewTargetPitch - pitchDelta * 0.55f, -55f, 55f);
                    e.Use();
                }
                else if (e.type == EventType.MouseUp && _isDraggingSkinModel)
                {
                    _isDraggingSkinModel = false;
                    e.Use();
                }
            }

            var previewImgRect = new Rect(previewBoxRect.x + 20, previewBoxRect.y + 14, 220, 310);
            if (_skin3DPreviewTexture != null)
            {
                GUI.DrawTexture(previewImgRect, _skin3DPreviewTexture, ScaleMode.ScaleToFit);
            }
            else
            {
                GUI.Label(previewImgRect, "Generating 3D Preview...", _labelStyle);
            }

            var previewBadgeRect = new Rect(previewBoxRect.x + 10, previewBoxRect.y + 330, previewBoxRect.width - 20, 22);
            GUI.Label(previewBadgeRect, "3D MODEL PREVIEW", _versionSubtitleStyle);

            var previewSubRect = new Rect(previewBoxRect.x + 10, previewBoxRect.y + 354, previewBoxRect.width - 20, 20);
            GUI.Label(previewSubRect, "Drag with mouse to rotate model", _switchSubLabelStyle);

            // Reset Rotation / Position button
            var resetRotBtnRect = new Rect(previewBoxRect.x + 24, previewBoxRect.y + 386, previewBoxRect.width - 48, 32);
            if (GUI.Button(resetRotBtnRect, "Reset Position", _buttonStyle))
            {
                _skinPreviewYaw = 0f;
                _skinPreviewPitch = 0f;
                _skinPreviewTargetYaw = 0f;
                _skinPreviewTargetPitch = 0f;
                _skinPreviewYawVelocity = 0f;
                _skinPreviewPitchVelocity = 0f;
                RefreshSkinModalPreview();
                Event.current.Use();
            }

            // Right column: Actions & Management (width 400)
            var rightColX = previewBoxRect.x + previewBoxRect.width + 24;
            var rightColWidth = modalRect.width - (rightColX - modalRect.x) - 24;
            var rightColY = contentY;

            GUI.Label(new Rect(rightColX, rightColY, rightColWidth, 26), "ACTIVE CHARACTER SKIN", _sectionHeaderStyle);
            rightColY += 30;

            // Info box
            var infoBoxRect = new Rect(rightColX, rightColY, rightColWidth, 68);
            GUI.Box(infoBoxRect, "", _boxStyle);

            var hasBackup = SkinManager.HasBackup();
            GUI.Label(new Rect(infoBoxRect.x + 12, infoBoxRect.y + 10, infoBoxRect.width - 24, 20), $"Backup State: {(hasBackup ? "Previous skin backup saved (Reversible)" : "No backup saved")}", _labelStyle);
            var statusMsg = !string.IsNullOrEmpty(_skinSyncStatusMessage)
                ? _skinSyncStatusMessage
                : (string.IsNullOrEmpty(_skinStatusMessage) ? "Ready to customize." : _skinStatusMessage);
            GUI.Label(new Rect(infoBoxRect.x + 12, infoBoxRect.y + 34, infoBoxRect.width - 24, 24), $"Status: {statusMsg}", _switchSubLabelStyle);
            rightColY += 78;

            // Action Buttons
            const float btnHeight = 42f;
            const float btnGap = 10f;

            // Staging / Apply Button (if a new skin or reset was staged)
            if (_stagedSkinIsReset)
            {
                var applyBtnRect = new Rect(rightColX, rightColY, rightColWidth, btnHeight);
                var applyContent = _iconApply != null ? new GUIContent(" APPLY & RESET SKIN", _iconApply) : new GUIContent("APPLY & RESET SKIN");
                if (GUI.Button(applyBtnRect, applyContent, _logoutButtonStyle))
                {
                    _showSkinApplyConfirmDialog = true;
                }
                rightColY += btnHeight + btnGap;
            }
            else if (!string.IsNullOrEmpty(_stagedSkinPath))
            {
                var applyBtnRect = new Rect(rightColX, rightColY, rightColWidth, btnHeight);
                var applyContent = _iconApply != null ? new GUIContent(" APPLY & SAVE SKIN", _iconApply) : new GUIContent("APPLY & SAVE SKIN");
                if (GUI.Button(applyBtnRect, applyContent, _logoutButtonStyle))
                {
                    _showSkinApplyConfirmDialog = true;
                }
                rightColY += btnHeight + btnGap;
            }

            // 1. Upload new skin
            var uploadBtnRect = new Rect(rightColX, rightColY, rightColWidth, btnHeight);
            var uploadContent = _iconUpload != null ? new GUIContent("  SELECT & UPLOAD SKIN (.PNG)", _iconUpload) : new GUIContent("SELECT & UPLOAD SKIN (.PNG)");
            if (GUI.Button(uploadBtnRect, uploadContent, _buttonStyle))
            {
                var filePath = SkinManager.PromptSelectSkinFile();
                if (!string.IsNullOrEmpty(filePath))
                {
                    if (SkinManager.ValidateSkinFile(filePath, out var tex, out var hash, out var err))
                    {
                        _stagedSkinIsReset = false;
                        _stagedSkinPath = filePath;
                        _stagedSkinTex = tex;
                        _stagedSkinHash = hash;
                        _skinStatusMessage = $"Previewing: {Path.GetFileName(filePath)} (Click Apply to save)";
                        _log?.Info($"Skin selected for preview: {filePath}");
                        RefreshSkinModalPreview();
                    }
                    else
                    {
                        _skinStatusMessage = $"Invalid skin: {err}";
                        _log?.Warn($"Skin selection error: {err}");
                    }
                }
            }
            rightColY += btnHeight + btnGap;

            // 2. Reload online skin
            var prevReloadEnabled = GUI.enabled;
            GUI.enabled = !_skinSyncInProgress && !isConfirmOpen;
            var reloadOnlineBtnRect = new Rect(rightColX, rightColY, rightColWidth, btnHeight);
            var reloadText = _skinSyncInProgress ? "  SYNCING WITH VEILNET..." : "  RELOAD ONLINE SKIN (VEILNET)";
            var reloadContent = _iconOnline != null ? new GUIContent(reloadText, _iconOnline) : new GUIContent(_skinSyncInProgress ? "SYNCING WITH VEILNET..." : "RELOAD ONLINE SKIN (VEILNET)");
            if (GUI.Button(reloadOnlineBtnRect, reloadContent, _buttonStyle))
            {
                var client = GetOrCreateSupabaseSkinClient();
                if (client != null)
                {
                    _skinSyncInProgress = true;
                    _skinSyncStatusMessage = "Fetching skin from Veilnet...";
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var fetchResult = await client.FetchSkinAsync();
                            EnqueueMainThread(() =>
                            {
                                _skinSyncInProgress = false;
                                if (!fetchResult.Success)
                                {
                                    _skinStatusMessage = $"Veilnet fetch error: {fetchResult.Error}";
                                    _skinSyncStatusMessage = "";
                                    _log?.Warn($"[SupabaseSkin] Fetch error: {fetchResult.Error}");
                                    return;
                                }

                                if (!fetchResult.HasSkin || fetchResult.PngBytes == null || fetchResult.PngBytes.Length == 0)
                                {
                                    SkinManager.ClearActiveSkin();
                                    _profile.ActiveSkin = "default_skin";
                                    _profile.Save(_log);
                                    _skinStatusMessage = "Online skin is default (baseline active).";
                                    _skinSyncStatusMessage = "Synced baseline default skin.";
                                    _log?.Info("[SupabaseSkin] Online skin is default; active skin reset.");
                                }
                                else
                                {
                                    var userSkinsDir = Paths.UserSkinsDir;
                                    Directory.CreateDirectory(userSkinsDir);
                                    var tempPath = Path.Combine(userSkinsDir, ".temp_veilnet_skin.png");
                                    try
                                    {
                                        File.WriteAllBytes(tempPath, fetchResult.PngBytes);

                                        if (SkinManager.ImportAndSetActiveSkin(tempPath, out var importErr, out var hash, "Veilnet_skin"))
                                        {
                                            var skinKey = "Veilnet_skin";
                                            _profile.Skins[skinKey] = hash;
                                            _profile.ActiveSkin = skinKey;
                                            _profile.Save(_log);
                                            _skinStatusMessage = "Online skin synced successfully!";
                                            _skinSyncStatusMessage = "Synced from Veilnet.";
                                            _log?.Info($"[SupabaseSkin] Successfully imported online skin as Veilnet_skin: {hash}");
                                        }
                                        else
                                        {
                                            _skinStatusMessage = $"Failed to import online skin: {importErr}";
                                            _skinSyncStatusMessage = "";
                                            _log?.Warn($"[SupabaseSkin] Import error: {importErr}");
                                        }
                                    }
                                    finally
                                    {
                                        try { File.Delete(tempPath); } catch { }
                                    }
                                }

                                _stagedSkinIsReset = false;
                                _stagedSkinPath = null;
                                _stagedSkinTex = null;
                                _stagedSkinHash = null;
                                RefreshSkinModalPreview();
                            });
                        }
                        catch (Exception ex)
                        {
                            EnqueueMainThread(() =>
                            {
                                _skinSyncInProgress = false;
                                _skinSyncStatusMessage = "";
                                _skinStatusMessage = $"Veilnet sync failed: {ex.Message}";
                                _log?.Warn($"[SupabaseSkin] Sync error: {ex.Message}");
                            });
                        }
                    });
                }
                else
                {
                    _skinStatusMessage = "You must be logged in to Veilnet to reload your online skin.";
                }
            }
            GUI.enabled = prevReloadEnabled;
            rightColY += btnHeight + btnGap;

            // 3. Revert to previous backup / Veilnet copy
            var revertBtnRect = new Rect(rightColX, rightColY, rightColWidth, btnHeight);
            var prevGuiEnabled = GUI.enabled;
            GUI.enabled = (hasBackup || (_veilnetLoggedIn && GetOrCreateSupabaseSkinClient() != null)) && !isConfirmOpen;
            var revertContent = _iconRevert != null ? new GUIContent("  REVERT LAST SKIN", _iconRevert) : new GUIContent("REVERT LAST SKIN");
            if (GUI.Button(revertBtnRect, revertContent, _buttonStyle))
            {
                if (_veilnetLoggedIn && GetOrCreateSupabaseSkinClient() != null)
                {
                    _showRevertChoiceDialog = true;
                }
                else
                {
                    if (SkinManager.RevertToBackup(out var err))
                    {
                        _stagedSkinIsReset = false;
                        _stagedSkinPath = null;
                        _stagedSkinTex = null;
                        _stagedSkinHash = null;
                        _skinStatusMessage = "Reverted to previous local backup.";
                        _log?.Info("Reverted skin to previous local backup.");
                        RefreshSkinModalPreview();
                    }
                    else
                    {
                        _skinStatusMessage = $"Revert failed: {err}";
                    }
                }
            }
            GUI.enabled = prevGuiEnabled;
            rightColY += btnHeight + btnGap;

            // 4. Remove skin (reset to baseline)
            var resetBtnRect = new Rect(rightColX, rightColY, rightColWidth, btnHeight);
            var resetContent = _iconReset != null ? new GUIContent("  RESET SKIN (RESET TO BASELINE)", _iconReset) : new GUIContent("RESET SKIN (RESET TO BASELINE)");
            if (GUI.Button(resetBtnRect, resetContent, _buttonStyle))
            {
                _stagedSkinIsReset = true;
                _stagedSkinPath = null;
                _stagedSkinHash = null;
                _stagedSkinTex = DefaultPlayerSkinFactory.CreateTexture();
                _skinStatusMessage = "Previewing: Baseline Default Skin (Click Apply to reset)";
                _log?.Info("Staged baseline skin for reset.");
                RefreshSkinModalPreview();
            }
            rightColY += btnHeight + 14;

            var noteRect = new Rect(rightColX, rightColY, rightColWidth, 38);
            GUI.Label(noteRect, "Skins are saved in your LatticeVeil profile and synchronized across all worlds.", _labelStyle);

            // Confirmation Popup Modal (Apply / Reset)
            if (_showSkinApplyConfirmDialog)
            {
                // Dimmer over entire screen area
                GUI.Box(screenRect, "", _dimmerStyle);

                float confirmW = 480;
                float confirmH = 250;
                var confirmRect = new Rect((screenRect.width - confirmW) * 0.5f, (screenRect.height - confirmH) * 0.5f, confirmW, confirmH);

                // Eat all mouse clicks outside the confirm popup dialog
                if (Event.current.type == EventType.MouseDown && screenRect.Contains(Event.current.mousePosition))
                {
                    if (!confirmRect.Contains(Event.current.mousePosition))
                    {
                        Event.current.Use();
                    }
                }

                GUI.Box(confirmRect, "", _boxStyle);

                var cTitleRect = new Rect(confirmRect.x + 20, confirmRect.y + 16, confirmRect.width - 40, 24);
                var cMsgRect = new Rect(confirmRect.x + 20, confirmRect.y + 48, confirmRect.width - 40, 110);

                var btnW = (confirmRect.width - 56) * 0.5f;
                var confirmBtnRect = new Rect(confirmRect.x + 20, confirmRect.y + confirmH - 52, btnW, 38);
                var cancelBtnRect = new Rect(confirmRect.x + 36 + btnW, confirmRect.y + confirmH - 52, btnW, 38);

                GUI.enabled = true;

                if (_stagedSkinIsReset)
                {
                    GUI.Label(cTitleRect, "Reset Character Skin", _titleStyle);
                    GUI.Label(cMsgRect,
                        "Notice: This will reset your active character skin to the baseline default skin and synchronize this reset with your Veilnet cloud profile.\n\nAre you sure you want to apply this reset?",
                        _labelStyle);

                    if (GUI.Button(confirmBtnRect, "CONFIRM & RESET", _logoutButtonStyle))
                    {
                        SkinManager.ClearActiveSkin();
                        _profile.ActiveSkin = "default_skin";
                        _profile.Save(_log);

                        _stagedSkinIsReset = false;
                        _stagedSkinPath = null;
                        _stagedSkinTex = null;
                        _stagedSkinHash = null;
                        _skinStatusMessage = "Reset to baseline character skin.";
                        _log?.Info("Character skin reset to baseline.");
                        _showSkinApplyConfirmDialog = false;
                        RefreshSkinModalPreview();

                        // Clear active skin on Supabase if logged in
                        if (_veilnetLoggedIn && _supabaseSkinClient != null)
                        {
                            _skinSyncInProgress = true;
                            _skinSyncStatusMessage = "Syncing reset to Veilnet...";
                            _ = Task.Run(async () =>
                            {
                                var (ok, syncErr) = await _supabaseSkinClient.ClearRemoteSkinAsync();
                                EnqueueMainThread(() =>
                                {
                                    _skinSyncInProgress = false;
                                    if (ok)
                                    {
                                        _skinSyncStatusMessage = "Reset synced to Veilnet.";
                                        _log?.Info("[SupabaseSkin] Cleared active skin on Supabase (default baseline active).");
                                    }
                                    else
                                    {
                                        _skinSyncStatusMessage = $"Veilnet sync: {syncErr}";
                                        _log?.Warn($"[SupabaseSkin] ClearRemoteSkin failed: {syncErr}");
                                    }
                                });
                            });
                        }

                        Event.current.Use();
                    }
                }
                else
                {
                    GUI.Label(cTitleRect, "Apply Character Skin", _titleStyle);
                    GUI.Label(cMsgRect,
                        "Apply this skin to your profile? This will save it locally and synchronize it with your Veilnet cloud profile.",
                        _labelStyle);

                    if (GUI.Button(confirmBtnRect, "CONFIRM & APPLY", _logoutButtonStyle))
                    {
                        if (!string.IsNullOrEmpty(_stagedSkinPath))
                        {
                            var skinKey = PlayerProfile.SterilizeSkinKey(Path.GetFileNameWithoutExtension(_stagedSkinPath));
                            byte[] stagedBytes = null;
                            try { stagedBytes = File.ReadAllBytes(_stagedSkinPath); } catch { }

                            if (SkinManager.ImportAndSetActiveSkin(_stagedSkinPath, out var err, out var hash, skinKey))
                            {
                                _profile.Skins[skinKey] = hash;
                                _profile.ActiveSkin = skinKey;
                                _profile.Save(_log);

                                _skinStatusMessage = $"Applied: {skinKey}";
                                _log?.Info($"Skin applied and registered in profile [Skins]: {skinKey} = {hash}");
                                _stagedSkinIsReset = false;
                                _stagedSkinPath = null;
                                _stagedSkinTex = null;
                                _stagedSkinHash = null;
                                _showSkinApplyConfirmDialog = false;
                                RefreshSkinModalPreview();

                                // Upload skin to Supabase if logged in
                                if (_veilnetLoggedIn && _supabaseSkinClient != null && stagedBytes != null && stagedBytes.Length > 0 && !string.IsNullOrWhiteSpace(hash))
                                {
                                    _skinSyncInProgress = true;
                                    _skinSyncStatusMessage = "Uploading skin to Veilnet...";
                                    var uploadHash = hash;
                                    var displayName = skinKey;
                                    _ = Task.Run(async () =>
                                    {
                                        var (ok, uploadErr) = await _supabaseSkinClient.UploadSkinAsync(uploadHash, stagedBytes, displayName);
                                        EnqueueMainThread(() =>
                                        {
                                            _skinSyncInProgress = false;
                                            if (ok)
                                            {
                                                _skinSyncStatusMessage = "Skin synced to Veilnet!";
                                                _log?.Info("[SupabaseSkin] Skin successfully uploaded to Supabase player_skins.");
                                            }
                                            else
                                            {
                                                _skinSyncStatusMessage = $"Veilnet upload: {uploadErr}";
                                                _log?.Warn($"[SupabaseSkin] Upload failed: {uploadErr}");
                                            }
                                        });
                                    });
                                }
                            }
                            else
                            {
                                _skinStatusMessage = $"Apply failed: {err}";
                                _showSkinApplyConfirmDialog = false;
                            }
                        }
                        Event.current.Use();
                    }
                }

                if (GUI.Button(cancelBtnRect, "CANCEL", _buttonStyle))
                {
                    _showSkinApplyConfirmDialog = false;
                    Event.current.Use();
                }
            }

            // Revert Choice Popup Modal (LOCAL vs VEILNET)
            if (_showRevertChoiceDialog)
            {
                // Dimmer over entire screen area
                GUI.Box(screenRect, "", _dimmerStyle);

                float revertW = 500;
                float revertH = 260;
                var revertRect = new Rect((screenRect.width - revertW) * 0.5f, (screenRect.height - revertH) * 0.5f, revertW, revertH);

                // Eat all mouse clicks outside the revert dialog
                if (Event.current.type == EventType.MouseDown && screenRect.Contains(Event.current.mousePosition))
                {
                    if (!revertRect.Contains(Event.current.mousePosition))
                    {
                        Event.current.Use();
                    }
                }

                GUI.Box(revertRect, "", _boxStyle);

                var rTitleRect = new Rect(revertRect.x + 20, revertRect.y + 16, revertRect.width - 40, 24);
                var rMsgRect = new Rect(revertRect.x + 20, revertRect.y + 46, revertRect.width - 40, 115);

                GUI.Label(rTitleRect, "Revert Character Skin", _titleStyle);
                GUI.Label(rMsgRect,
                    "Choose which version of your skin you would like to restore:\n\n• LOCAL BACKUP: Restores the previous backup saved on this machine.\n• VEILNET (ONLINE): Restores the skin currently stored in your Veilnet account.",
                    _labelStyle);

                var rBtnW = (revertRect.width - 52) / 3f;
                var localBtnRect = new Rect(revertRect.x + 16, revertRect.y + revertH - 52, rBtnW, 38);
                var veilnetBtnRect = new Rect(revertRect.x + 24 + rBtnW, revertRect.y + revertH - 52, rBtnW, 38);
                var rCancelBtnRect = new Rect(revertRect.x + 32 + (rBtnW * 2), revertRect.y + revertH - 52, rBtnW, 38);

                GUI.enabled = hasBackup;
                if (GUI.Button(localBtnRect, "LOCAL BACKUP", _buttonStyle))
                {
                    if (SkinManager.RevertToBackup(out var err))
                    {
                        _stagedSkinIsReset = false;
                        _stagedSkinPath = null;
                        _stagedSkinTex = null;
                        _stagedSkinHash = null;
                        _skinStatusMessage = "Reverted to local backup.";
                        _log?.Info("Reverted skin to local backup.");
                        _showRevertChoiceDialog = false;
                        RefreshSkinModalPreview();
                    }
                    else
                    {
                        _skinStatusMessage = $"Local revert failed: {err}";
                        _showRevertChoiceDialog = false;
                    }
                    Event.current.Use();
                }

                GUI.enabled = !_revertVeilnetInProgress;
                var veilnetBtnText = _revertVeilnetInProgress ? "SYNCING..." : "VEILNET (ONLINE)";
                if (GUI.Button(veilnetBtnRect, veilnetBtnText, _logoutButtonStyle))
                {
                    var client = GetOrCreateSupabaseSkinClient();
                    if (client != null)
                    {
                        _revertVeilnetInProgress = true;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var fetchResult = await client.FetchSkinAsync();
                                EnqueueMainThread(() =>
                                {
                                    _revertVeilnetInProgress = false;
                                    _showRevertChoiceDialog = false;

                                    if (!fetchResult.Success)
                                    {
                                        _skinStatusMessage = $"Veilnet fetch failed: {fetchResult.Error}";
                                        _log?.Warn($"[SupabaseSkin] Fetch error: {fetchResult.Error}");
                                        return;
                                    }

                                    if (!fetchResult.HasSkin || fetchResult.PngBytes == null || fetchResult.PngBytes.Length == 0)
                                    {
                                        SkinManager.ClearActiveSkin();
                                        _profile.ActiveSkin = "default_skin";
                                        _profile.Save(_log);
                                        _skinStatusMessage = "Restored baseline default skin from Veilnet.";
                                        _log?.Info("[SupabaseSkin] Restored baseline default skin from Veilnet.");
                                    }
                                    else
                                    {
                                        var userSkinsDir = Paths.UserSkinsDir;
                                        Directory.CreateDirectory(userSkinsDir);
                                        var tempPath = Path.Combine(userSkinsDir, ".temp_veilnet_skin.png");
                                        try
                                        {
                                            File.WriteAllBytes(tempPath, fetchResult.PngBytes);

                                            if (SkinManager.ImportAndSetActiveSkin(tempPath, out var importErr, out var hash, "Veilnet_skin"))
                                            {
                                                var skinKey = "Veilnet_skin";
                                                _profile.Skins[skinKey] = hash;
                                                _profile.ActiveSkin = skinKey;
                                                _profile.Save(_log);
                                                _skinStatusMessage = "Skin restored from Veilnet!";
                                                _log?.Info($"[SupabaseSkin] Successfully restored online skin as Veilnet_skin: {hash}");
                                            }
                                            else
                                            {
                                                _skinStatusMessage = $"Failed to import online skin: {importErr}";
                                                _log?.Warn($"[SupabaseSkin] Import online skin error: {importErr}");
                                            }
                                        }
                                        finally
                                        {
                                            try { File.Delete(tempPath); } catch { }
                                        }
                                    }

                                    _stagedSkinIsReset = false;
                                    _stagedSkinPath = null;
                                    _stagedSkinTex = null;
                                    _stagedSkinHash = null;
                                    RefreshSkinModalPreview();
                                });
                            }
                            catch (Exception ex)
                            {
                                EnqueueMainThread(() =>
                                {
                                    _revertVeilnetInProgress = false;
                                    _showRevertChoiceDialog = false;
                                    _skinStatusMessage = $"Veilnet restore error: {ex.Message}";
                                    _log?.Warn($"[SupabaseSkin] Revert error: {ex.Message}");
                                });
                            }
                        });
                    }
                    else
                    {
                        _showRevertChoiceDialog = false;
                        _skinStatusMessage = "You must be logged in to Veilnet to restore your online skin.";
                    }
                    Event.current.Use();
                }

                GUI.enabled = true;
                if (GUI.Button(rCancelBtnRect, "CANCEL", _buttonStyle))
                {
                    _showRevertChoiceDialog = false;
                    Event.current.Use();
                }
            }

            GUI.enabled = true;
#endif
        }

        private void DrawSkinLibraryModal(Rect screenRect)
        {
            GUI.Box(screenRect, "", _dimmerStyle);

            const float modalWidth = 860f;
            const float modalHeight = 540f;
            var modalRect = new Rect((screenRect.width - modalWidth) * 0.5f + _skinModalOffset.x, (screenRect.height - modalHeight) * 0.5f + _skinModalOffset.y, modalWidth, modalHeight);
            GUI.Box(modalRect, "", _boxStyle);

            var titleRect = new Rect(modalRect.x + 18, modalRect.y + 12, 260, 30);
            GUI.Label(titleRect, "SKIN", _titleStyle);

            // Movable header (excludes the close button hitbox)
            var skinHeaderDragRect = new Rect(modalRect.x + 8, modalRect.y + 8, modalRect.width - 70, 36);
            var skinDragDelta = GetModalDragDelta(skinHeaderDragRect, ref _draggingSkinModal);
            _skinModalOffset += skinDragDelta;
            modalRect.x += skinDragDelta.x;
            modalRect.y += skinDragDelta.y;
            var closeRect = new Rect(modalRect.x + modalRect.width - 48, modalRect.y + 8, 34, 30);
            if (GUI.Button(closeRect, "X", _modalCloseBtnStyle))
            {
                _showSkinModal = false;
                _isDraggingSkinModel = false;
                Event.current.Use();
                return;
            }

            var listRect = new Rect(modalRect.x + 18, modalRect.y + 54, 360, 390);
            var previewRect = new Rect(modalRect.x + 426, modalRect.y + 56, 360, 320);
            GUI.Box(listRect, "", _panelBoxStyle);
            GUI.Box(previewRect, "", _panelBoxStyle);

            var activeHash = ReadActiveSkinHash();
            var skinPaths = GetLocalSkinPaths();
            EnsureSkinLibrarySelection(skinPaths, activeHash);

            var listContent = new Rect(listRect.x + 8, listRect.y + 8, listRect.width - 16, 74 + ((skinPaths.Length + 1) * 82));
            _skinLibraryScroll = GUI.BeginScrollView(listRect, _skinLibraryScroll, listContent, false, true);
            var rowY = listContent.y;
            DrawSkinLibraryRow(new Rect(listContent.x, rowY, listContent.width, 74), "DEFAULT SKIN", null, string.IsNullOrWhiteSpace(activeHash), "DEFAULT", "USE DEFAULT");
            rowY += 82;
            foreach (var path in skinPaths)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var hash = name.Length >= 12 ? name.Substring(0, 12).ToUpperInvariant() : name.ToUpperInvariant();
                var isActive = string.Equals(name, activeHash, StringComparison.OrdinalIgnoreCase);
                var status = isActive ? "ACTIVE" : (IsLayeredSkin(path) ? "LAYERED" : "");
                DrawSkinLibraryRow(new Rect(listContent.x, rowY, listContent.width, 74), name, path, isActive, status, "USE");
                rowY += 82;
            }
            GUI.EndScrollView();

            if (_skin3DPreviewTexture != null)
                GUI.DrawTexture(previewRect, _skin3DPreviewTexture, ScaleMode.ScaleToFit);

            var layersRect = new Rect(previewRect.x, previewRect.y + 326, 92, 26);
            var previousLayers = _skinPreviewLayers;
            _skinPreviewLayers = GUI.Toggle(layersRect, _skinPreviewLayers, "LAYERS", _toggleStyle);
            if (previousLayers != _skinPreviewLayers)
                RenderSkinPreview();
            var recenterRect = new Rect(previewRect.x + 102, previewRect.y + 326, 98, 34);
            if (GUI.Button(recenterRect, "RECENTER", _buttonStyle))
                ResetSkinLibraryPreview();
            var minusRect = new Rect(previewRect.x + 212, previewRect.y + 326, 42, 34);
            if (GUI.Button(minusRect, "-", _buttonStyle))
                _skinPreviewTargetPitch = Mathf.Clamp(_skinPreviewTargetPitch - 5f, -55f, 55f);
            var plusRect = new Rect(previewRect.x + 262, previewRect.y + 326, 42, 34);
            if (GUI.Button(plusRect, "+", _buttonStyle))
                _skinPreviewTargetPitch = Mathf.Clamp(_skinPreviewTargetPitch + 5f, -55f, 55f);

            var animationNames = new[] { "IDLE", "WALK", "RUN", "FLY", "CROUCH", "PUNCH" };
            var animationX = previewRect.x + 14;
            for (int i = 0; i < animationNames.Length; i++)
            {
                var animationRect = new Rect(animationX + ((i % 4) * 80), previewRect.y + 374 + ((i / 4) * 40), 72, 32);
                if (GUI.Button(animationRect, animationNames[i], string.Equals(_skinPreviewAnimation, animationNames[i], StringComparison.OrdinalIgnoreCase) ? _logoutButtonStyle : _buttonStyle))
                {
                    _skinPreviewAnimation = animationNames[i];
                    if (_skinPreviewAnimation == "PUNCH")
                        _skinPreviewTargetYaw += 8f;
                }
            }

            var footerY = modalRect.y + 476;
            var uploadRect = new Rect(modalRect.x + 18, footerY, 110, 38);
            if (GUI.Button(uploadRect, "UPLOAD", _buttonStyle))
                ImportSkinIntoLibrary();
            var folderRect = new Rect(modalRect.x + 140, footerY, 110, 38);
            if (GUI.Button(folderRect, "FOLDER", _buttonStyle))
                OpenSkinLibraryFolder();
            GUI.Label(new Rect(modalRect.x + 18, modalRect.y + 456, 360, 18), "Drop a 64x64 PNG here to import it.", _switchSubLabelStyle);

            HandleSkinLibraryDrag(previewRect);
        }

        private void DrawSkinLibraryRow(Rect rowRect, string displayName, string path, bool isActive, string status, string useText)
        {
            GUI.Box(rowRect, "", _boxStyle);
            var thumbRect = new Rect(rowRect.x + 8, rowRect.y + 8, 58, 58);
            var thumb = LoadSkinLibraryThumb(path);
            if (thumb != null)
                GUI.DrawTexture(thumbRect, thumb, ScaleMode.ScaleToFit);
            GUI.Label(new Rect(rowRect.x + 74, rowRect.y + 8, 150, 22), displayName, _switchLabelStyle);
            GUI.Label(new Rect(rowRect.x + 74, rowRect.y + 32, 150, 18), status, _versionSubtitleStyle);

            var useRect = new Rect(rowRect.x + rowRect.width - 112, rowRect.y + 8, 100, 28);
            GUI.enabled = !isActive;
            if (GUI.Button(useRect, useText, _buttonStyle))
            {
                if (path == null)
                    UseDefaultSkinFromLibrary();
                else
                    UseLocalSkinFromLibrary(path);
            }
            GUI.enabled = true;

            if (path != null)
            {
                var removeRect = new Rect(rowRect.x + rowRect.width - 112, rowRect.y + 42, 100, 24);
                if (GUI.Button(removeRect, "REMOVE", _buttonStyle))
                    RemoveLocalSkinFromLibrary(path);
            }

            if (Event.current.type == EventType.MouseDown && rowRect.Contains(Event.current.mousePosition) && Event.current.button == 0)
            {
                _selectedSkinLibraryPath = path;
                LoadSkinLibraryPreview(path);
                Event.current.Use();
            }
        }

        private string[] GetLocalSkinPaths()
        {
            try
            {
                Directory.CreateDirectory(Paths.UserSkinsDir);
                return Directory.GetFiles(Paths.UserSkinsDir, "*.png", SearchOption.TopDirectoryOnly)
                    .Where(path => !Path.GetFileName(path).StartsWith(".temp_", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private string ReadActiveSkinHash()
        {
            try
            {
                return File.Exists(Paths.ActiveSkinHashPath) ? File.ReadAllText(Paths.ActiveSkinHashPath).Trim() : string.Empty;
            }
            catch { return string.Empty; }
        }

        private void EnsureSkinLibrarySelection(string[] skinPaths, string activeHash)
        {
            if (_selectedSkinLibraryPath == null && !string.IsNullOrWhiteSpace(activeHash))
                _selectedSkinLibraryPath = skinPaths.FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), activeHash, StringComparison.OrdinalIgnoreCase));
            if (_selectedSkinLibraryPath != null && !File.Exists(_selectedSkinLibraryPath))
                _selectedSkinLibraryPath = null;
            if (!string.Equals(_skinLibraryPreviewPathLoaded ?? string.Empty, _selectedSkinLibraryPath ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                || _currentLoadedSkinTex == null || _skin3DPreviewTexture == null)
                LoadSkinLibraryPreview(_selectedSkinLibraryPath);
        }

        private bool IsLayeredSkin(string path)
        {
            try
            {
                var image = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                var result = image.LoadImage(File.ReadAllBytes(path)) && image.height == 64;
                UnityEngine.Object.Destroy(image);
                return result;
            }
            catch { return false; }
        }

        private Texture2D LoadSkinLibraryThumb(string path)
        {
            var cacheKey = path ?? "__default_skin__";
            if (_skinLibraryThumbCache.TryGetValue(cacheKey, out var cached) && cached != null)
                return cached;

            try
            {
                Texture2D fullSkin;
                if (path == null)
                    fullSkin = DefaultPlayerSkinFactory.CreateTexture();
                else
                {
                    fullSkin = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!fullSkin.LoadImage(File.ReadAllBytes(path)))
                    {
                        UnityEngine.Object.Destroy(fullSkin);
                        return null;
                    }
                }

                var thumb = ExtractHeadTexture(fullSkin);
                UnityEngine.Object.Destroy(fullSkin);
                _skinLibraryThumbCache[cacheKey] = thumb;
                return thumb;
            }
            catch
            {
                return null;
            }
        }

        private void LoadSkinLibraryPreview(string path)
        {
            try
            {
                if (path == null)
                    _currentLoadedSkinTex = DefaultPlayerSkinFactory.CreateTexture();
                else
                {
                    _currentLoadedSkinTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    _currentLoadedSkinTex.LoadImage(File.ReadAllBytes(path));
                }
                RenderSkinPreview();
                _skinHeadTexture = ExtractHeadTexture(_currentLoadedSkinTex);
                _skinLibraryPreviewPathLoaded = path;
            }
            catch (Exception ex)
            {
                _log?.Warn($"[SkinLibrary] Preview load failed: {ex.Message}");
            }
        }

        private void ResetSkinLibraryPreview()
        {
            _skinPreviewYaw = 0f;
            _skinPreviewPitch = 0f;
            _skinPreviewTargetYaw = 0f;
            _skinPreviewTargetPitch = 0f;
            _skinPreviewYawVelocity = 0f;
            _skinPreviewPitchVelocity = 0f;
            RenderSkinPreview();
        }

        private void UseDefaultSkinFromLibrary()
        {
            SkinManager.ClearActiveSkin();
            _profile.ActiveSkin = "default_skin";
            _profile.Save(_log);
            _selectedSkinLibraryPath = null;
            LoadSkinLibraryPreview(null);
        }

        private void UseLocalSkinFromLibrary(string path)
        {
            if (!SkinManager.ImportAndSetActiveSkin(path, out var error, out var hash, null))
            {
                _skinStatusMessage = $"Unable to activate skin: {error}";
                return;
            }
            var key = PlayerProfile.SterilizeSkinKey(Path.GetFileNameWithoutExtension(path));
            _profile.Skins[key] = hash;
            _profile.ActiveSkin = key;
            _profile.Save(_log);
            _selectedSkinLibraryPath = path;
            LoadSkinLibraryPreview(path);
        }

        private void RemoveLocalSkinFromLibrary(string path)
        {
            try
            {
                var activeHash = ReadActiveSkinHash();
                if (string.Equals(Path.GetFileNameWithoutExtension(path), activeHash, StringComparison.OrdinalIgnoreCase))
                    UseDefaultSkinFromLibrary();
                File.Delete(path);
                if (string.Equals(_selectedSkinLibraryPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedSkinLibraryPath = null;
                    LoadSkinLibraryPreview(null);
                }
            }
            catch (Exception ex)
            {
                _skinStatusMessage = $"Unable to remove skin: {ex.Message}";
            }
        }

        private void ImportSkinIntoLibrary()
        {
            var path = SkinManager.PromptSelectSkinFile();
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!SkinManager.ImportAndSetActiveSkin(path, out var error, out var hash, null))
            {
                _skinStatusMessage = $"Skin import failed: {error}";
                return;
            }
            _selectedSkinLibraryPath = Path.Combine(Paths.UserSkinsDir, $"{hash}.png");
            LoadSkinLibraryPreview(_selectedSkinLibraryPath);
        }

        private void OpenSkinLibraryFolder()
        {
            try
            {
                Directory.CreateDirectory(Paths.UserSkinsDir);
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{Paths.UserSkinsDir}\"", UseShellExecute = true });
            }
            catch (Exception ex) { _log?.Warn($"Unable to open skin folder: {ex.Message}"); }
        }

        private void HandleSkinLibraryDrag(Rect previewRect)
        {
            if (Event.current.type == EventType.MouseDown && previewRect.Contains(Event.current.mousePosition) && Event.current.button == 0)
            {
                _isDraggingSkinModel = true;
                _lastSkinDragMouseX = Event.current.mousePosition.x;
                _lastSkinDragMouseY = Event.current.mousePosition.y;
                Event.current.Use();
            }
            else if (Event.current.type == EventType.MouseDrag && _isDraggingSkinModel)
            {
                var delta = Mathf.Clamp(Event.current.mousePosition.x - _lastSkinDragMouseX, -12f, 12f);
                var pitchDelta = Mathf.Clamp(Event.current.mousePosition.y - _lastSkinDragMouseY, -12f, 12f);
                _lastSkinDragMouseX = Event.current.mousePosition.x;
                _lastSkinDragMouseY = Event.current.mousePosition.y;
                _skinPreviewTargetYaw += delta * 0.75f;
                _skinPreviewTargetPitch = Mathf.Clamp(_skinPreviewTargetPitch - pitchDelta * 0.55f, -55f, 55f);
                Event.current.Use();
            }
            else if (Event.current.type == EventType.MouseUp && _isDraggingSkinModel)
            {
                _isDraggingSkinModel = false;
                Event.current.Use();
            }
        }

        private bool DrawSwitch(Rect rect, bool value, string label, string subtitle = null)
        {
            var switchWidth = 44f;
            var switchHeight = 22f;
            var switchRect = new Rect(rect.x, rect.y + 4, switchWidth, switchHeight);

            var isHovered = rect.Contains(Event.current.mousePosition);
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && isHovered)
            {
                value = !value;
                Event.current.Use();
            }

            // Track
            var trackTex = value ? _switchTrackOnTex : (isHovered ? _switchTrackOffHoverTex : _switchTrackOffTex);
            if (trackTex != null)
            {
                GUI.DrawTexture(switchRect, trackTex);
            }

            // Knob
            var knobPadding = 3f;
            var knobSize = switchHeight - (knobPadding * 2); // 16px
            var knobX = value ? (switchRect.x + switchWidth - knobSize - knobPadding) : (switchRect.x + knobPadding);
            var knobRect = new Rect(knobX, switchRect.y + knobPadding, knobSize, knobSize);
            if (_switchKnobTex != null)
            {
                GUI.DrawTexture(knobRect, _switchKnobTex);
            }

            // Text
            var textX = switchRect.x + switchWidth + 14;
            var textWidth = rect.width - (switchWidth + 14);
            if (string.IsNullOrEmpty(subtitle))
            {
                var labelRect = new Rect(textX, rect.y + 3, textWidth, 24);
                GUI.Label(labelRect, label, _switchLabelStyle);
            }
            else
            {
                var labelRect = new Rect(textX, rect.y, textWidth, 20);
                var subRect = new Rect(textX, rect.y + 20, textWidth, 18);
                GUI.Label(labelRect, label, _switchLabelStyle);
                GUI.Label(subRect, subtitle, _switchSubLabelStyle);
            }

            return value;
        }

        private void InitializeStyles()
        {
            ApplyTheme(_settings.DarkMode);
        }

        private void ApplyTheme(bool dark)
        {
            // Textures
            _greenBarTex = MakeTexture(4, 4, new Color(0f, 0.9f, 0.46f)); // #00E676 Lime Green
            _amberBarTex = MakeTexture(4, 4, new Color(1f, 0.7f, 0.0f)); // Amber / Gold
            _redBarTex = MakeTexture(4, 4, new Color(0.9f, 0.22f, 0.21f)); // Crimson Red
            _activeTabTex = MakeTexture(4, 4, Color.white);
            _inactiveTabTex = MakeTexture(4, 4, new Color(0.08f, 0.08f, 0.08f));
            _dimmerTex = MakeTexture(2, 2, new Color(0f, 0f, 0f, 0.80f));

            // Pure OLED Black
            var oledBlack = new Color(0f, 0f, 0f, 1f);
            var panelBorder = new Color(0.20f, 0.20f, 0.20f, 1f); // #333333 crisp subtle border
            var textColor = new Color(0.96f, 0.96f, 0.96f, 1f);
            var greenAccent = new Color(0f, 0.9f, 0.46f, 1f); // #00E676
            var forestGreen = new Color(0.05f, 0.20f, 0.12f, 1f); // #0D331F

            _settingsHeaderTex = CreateBorderedTexture(new Color(0.09f, 0.09f, 0.09f, 1f), panelBorder, 16, 16, 1);
            _switchTrackOnTex = CreateBorderedTexture(new Color(0f, 0.65f, 0.32f, 1f), greenAccent, 24, 12, 1);
            _switchTrackOffTex = CreateBorderedTexture(new Color(0.12f, 0.12f, 0.12f, 1f), new Color(0.28f, 0.28f, 0.28f, 1f), 24, 12, 1);
            _switchTrackOffHoverTex = CreateBorderedTexture(new Color(0.18f, 0.18f, 0.18f, 1f), new Color(0.40f, 0.40f, 0.40f, 1f), 24, 12, 1);
            _switchKnobTex = CreateBorderedTexture(Color.white, new Color(0.85f, 0.85f, 0.85f, 1f), 12, 12, 1);

            _switchLabelStyle = new GUIStyle(GUI.skin.label);
            _switchLabelStyle.fontSize = 14;
            _switchLabelStyle.fontStyle = FontStyle.Bold;
            _switchLabelStyle.normal.textColor = textColor;
            _switchLabelStyle.alignment = TextAnchor.MiddleLeft;

            _switchSubLabelStyle = new GUIStyle(GUI.skin.label);
            _switchSubLabelStyle.fontSize = 12;
            _switchSubLabelStyle.fontStyle = FontStyle.Normal;
            _switchSubLabelStyle.normal.textColor = new Color(0.65f, 0.65f, 0.65f, 1f);
            _switchSubLabelStyle.alignment = TextAnchor.MiddleLeft;

            _logoutBtnTex = CreateBorderedTexture(forestGreen, greenAccent, 16, 16, 1);
            _logoutBtnHoverTex = CreateBorderedTexture(new Color(0.08f, 0.28f, 0.17f), greenAccent, 16, 16, 1);
            _skinsBtnTex = CreateBorderedTexture(new Color(0.09f, 0.09f, 0.09f), panelBorder, 16, 16, 1);

            _titleStyle = new GUIStyle(GUI.skin.label);
            _titleStyle.fontSize = 17;
            _titleStyle.fontStyle = FontStyle.Bold;
            _titleStyle.alignment = TextAnchor.MiddleLeft;
            _titleStyle.normal.textColor = textColor;

            _versionSubtitleStyle = new GUIStyle(GUI.skin.label);
            _versionSubtitleStyle.fontSize = 15;
            _versionSubtitleStyle.fontStyle = FontStyle.Bold;
            _versionSubtitleStyle.alignment = TextAnchor.MiddleLeft;
            _versionSubtitleStyle.normal.textColor = greenAccent;

            _sectionHeaderStyle = new GUIStyle(GUI.skin.label);
            _sectionHeaderStyle.fontSize = 14;
            _sectionHeaderStyle.fontStyle = FontStyle.Bold;
            _sectionHeaderStyle.normal.textColor = textColor;
            _sectionHeaderStyle.alignment = TextAnchor.MiddleLeft;

            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.fontSize = 13;
            _labelStyle.fontStyle = FontStyle.Normal;
            _labelStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);

            _statusTextStyle = new GUIStyle(GUI.skin.label);
            _statusTextStyle.fontSize = 14;
            _statusTextStyle.fontStyle = FontStyle.Bold;
            _statusTextStyle.normal.textColor = textColor;

            _buttonStyle = new GUIStyle(GUI.skin.button);
            _buttonStyle.fontSize = 14;
            _buttonStyle.fontStyle = FontStyle.Bold;
            _buttonStyle.padding = new RectOffset(12, 12, 8, 8);
            _buttonStyle.border = new RectOffset(2, 2, 2, 2);
            _buttonStyle.normal.textColor = textColor;
            _buttonStyle.normal.background = CreateBorderedTexture(new Color(0.09f, 0.09f, 0.09f), panelBorder, 16, 16, 1);
            _buttonStyle.hover.background = CreateBorderedTexture(new Color(0.16f, 0.16f, 0.16f), panelBorder, 16, 16, 1);
            _buttonStyle.active.background = CreateBorderedTexture(new Color(0.22f, 0.22f, 0.22f), greenAccent, 16, 16, 1);
            _buttonStyle.alignment = TextAnchor.MiddleCenter;

            _launchButtonStyle = new GUIStyle(_buttonStyle);
            _launchButtonStyle.fontSize = 16;
            _launchButtonStyle.fontStyle = FontStyle.Bold;

            _logoutButtonStyle = new GUIStyle(GUI.skin.button);
            _logoutButtonStyle.fontSize = 14;
            _logoutButtonStyle.fontStyle = FontStyle.Bold;
            _logoutButtonStyle.border = new RectOffset(2, 2, 2, 2);
            _logoutButtonStyle.normal.textColor = Color.white;
            _logoutButtonStyle.normal.background = _logoutBtnTex;
            _logoutButtonStyle.hover.background = _logoutBtnHoverTex;
            _logoutButtonStyle.active.background = _logoutBtnHoverTex;
            _logoutButtonStyle.alignment = TextAnchor.MiddleCenter;

            _skinsButtonStyle = new GUIStyle(GUI.skin.button);
            _skinsButtonStyle.fontSize = 14;
            _skinsButtonStyle.fontStyle = FontStyle.Bold;
            _skinsButtonStyle.border = new RectOffset(2, 2, 2, 2);
            _skinsButtonStyle.normal.textColor = Color.white;
            _skinsButtonStyle.normal.background = _skinsBtnTex;
            _skinsButtonStyle.hover.background = CreateBorderedTexture(new Color(0.16f, 0.16f, 0.16f), panelBorder, 16, 16, 1);
            _skinsButtonStyle.active.background = CreateBorderedTexture(new Color(0.22f, 0.22f, 0.22f), greenAccent, 16, 16, 1);
            _skinsButtonStyle.alignment = TextAnchor.MiddleCenter;

            _windowControlStyle = new GUIStyle(_buttonStyle);
            _windowControlStyle.fontSize = 16;
            _windowControlStyle.fontStyle = FontStyle.Bold;
            _windowControlStyle.alignment = TextAnchor.MiddleCenter;

            _wrenchControlStyle = new GUIStyle(_windowControlStyle);
            _wrenchControlStyle.fontSize = 14;
            _wrenchControlStyle.imagePosition = ImagePosition.ImageOnly;
            _wrenchControlStyle.padding = new RectOffset(8, 8, 7, 7);

            _toggleStyle = new GUIStyle(GUI.skin.toggle);
            _toggleStyle.fontSize = 14;
            _toggleStyle.fontStyle = FontStyle.Bold;
            _toggleStyle.normal.textColor = textColor;
            _toggleStyle.onNormal.textColor = textColor;

            _boxStyle = new GUIStyle(GUI.skin.box);
            _boxStyle.padding = new RectOffset(8, 8, 8, 8);
            _boxStyle.border = new RectOffset(2, 2, 2, 2);
            _boxStyle.normal.background = CreateBorderedTexture(oledBlack, panelBorder, 16, 16, 1);

            _panelBoxStyle = new GUIStyle(_boxStyle);
            _panelBoxStyle.normal.background = CreateBorderedTexture(oledBlack, panelBorder, 16, 16, 1);

            _logStyle = new GUIStyle(GUI.skin.textArea);
            _logStyle.fontSize = 14;
            _logStyle.fontStyle = FontStyle.Normal;
            _logStyle.normal.textColor = new Color(0.95f, 0.95f, 0.95f);
            _logStyle.normal.background = MakeTexture(2, 2, oledBlack);
            _logStyle.padding = new RectOffset(8, 8, 8, 8);

            _topBarStyle = new GUIStyle(GUI.skin.box);
            _topBarStyle.border = new RectOffset(2, 2, 2, 2);
            _topBarStyle.normal.background = CreateBorderedTexture(oledBlack, panelBorder, 16, 16, 1);

            _textFieldStyle = new GUIStyle(GUI.skin.textField);
            _textFieldStyle.fontSize = 13;
            _textFieldStyle.fontStyle = FontStyle.Normal;
            _textFieldStyle.border = new RectOffset(2, 2, 2, 2);
            _textFieldStyle.normal.textColor = textColor;
            _textFieldStyle.normal.background = CreateBorderedTexture(oledBlack, panelBorder, 16, 16, 1);
            _textFieldStyle.padding = new RectOffset(8, 8, 8, 8);

            _dropdownStyle = new GUIStyle(GUI.skin.button);
            _dropdownStyle.fontSize = 13;
            _dropdownStyle.fontStyle = FontStyle.Bold;
            _dropdownStyle.padding = new RectOffset(10, 10, 6, 6);
            _dropdownStyle.border = new RectOffset(2, 2, 2, 2);
            _dropdownStyle.normal.textColor = textColor;
            _dropdownStyle.normal.background = CreateBorderedTexture(new Color(0.08f, 0.08f, 0.08f), panelBorder, 16, 16, 1);
            _dropdownStyle.alignment = TextAnchor.MiddleLeft;

            _dropdownContainerStyle = new GUIStyle(GUI.skin.box);
            _dropdownContainerStyle.border = new RectOffset(2, 2, 2, 2);
            _dropdownContainerStyle.normal.background = CreateBorderedTexture(oledBlack, panelBorder, 16, 16, 1);

            _dropdownItemStyle = new GUIStyle(GUI.skin.button);
            _dropdownItemStyle.fontSize = 13;
            _dropdownItemStyle.fontStyle = FontStyle.Normal;
            _dropdownItemStyle.normal.textColor = textColor;
            _dropdownItemStyle.normal.background = MakeTexture(2, 2, oledBlack);
            _dropdownItemStyle.hover.background = MakeTexture(2, 2, new Color(0.18f, 0.18f, 0.18f));
            _dropdownItemStyle.alignment = TextAnchor.MiddleLeft;

            _dropdownActiveItemStyle = new GUIStyle(_dropdownItemStyle);
            _dropdownActiveItemStyle.fontStyle = FontStyle.Bold;
            _dropdownActiveItemStyle.normal.textColor = greenAccent;

            _modalHeaderStyle = new GUIStyle(GUI.skin.box);
            _modalHeaderStyle.normal.background = _settingsHeaderTex;

            _modalHeaderTitleStyle = new GUIStyle(GUI.skin.label);
            _modalHeaderTitleStyle.fontSize = 14;
            _modalHeaderTitleStyle.fontStyle = FontStyle.Bold;
            _modalHeaderTitleStyle.normal.textColor = Color.white;
            _modalHeaderTitleStyle.alignment = TextAnchor.MiddleLeft;

            _modalCloseBtnStyle = new GUIStyle(GUI.skin.button);
            _modalCloseBtnStyle.fontSize = 14;
            _modalCloseBtnStyle.fontStyle = FontStyle.Bold;
            _modalCloseBtnStyle.normal.textColor = Color.white;
            _modalCloseBtnStyle.normal.background = MakeTexture(2, 2, Color.clear);
            _modalCloseBtnStyle.hover.background = MakeTexture(2, 2, new Color(1f, 1f, 1f, 0.2f));
            _modalCloseBtnStyle.alignment = TextAnchor.MiddleCenter;

            _modalTabActiveStyle = new GUIStyle(GUI.skin.button);
            _modalTabActiveStyle.fontSize = 13;
            _modalTabActiveStyle.fontStyle = FontStyle.Bold;
            _modalTabActiveStyle.normal.textColor = Color.black;
            _modalTabActiveStyle.normal.background = _activeTabTex;
            _modalTabActiveStyle.alignment = TextAnchor.MiddleCenter;

            _modalTabInactiveStyle = new GUIStyle(GUI.skin.button);
            _modalTabInactiveStyle.fontSize = 13;
            _modalTabInactiveStyle.fontStyle = FontStyle.Normal;
            _modalTabInactiveStyle.normal.textColor = Color.white;
            _modalTabInactiveStyle.normal.background = _inactiveTabTex;
            _modalTabInactiveStyle.alignment = TextAnchor.MiddleCenter;

            _dimmerStyle = new GUIStyle(GUI.skin.box);
            _dimmerStyle.normal.background = _dimmerTex;
        }

        private Texture2D MakeTexture(int width, int height, Color col)
        {
            Color[] pix = new Color[width * height];
            for (int i = 0; i < pix.Length; i++)
                pix[i] = col;

            Texture2D result = new Texture2D(width, height, TextureFormat.RGBA32, false);
            result.SetPixels(pix);
            result.Apply();
            return result;
        }

        private Texture2D CreateBorderedTexture(Color fillColor, Color borderColor, int width = 16, int height = 16, int borderWidth = 1)
        {
            Color[] pix = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool isBorder = (x < borderWidth) || (x >= width - borderWidth) || (y < borderWidth) || (y >= height - borderWidth);
                    pix[y * width + x] = isBorder ? borderColor : fillColor;
                }
            }

            Texture2D result = new Texture2D(width, height, TextureFormat.RGBA32, false);
            result.SetPixels(pix);
            result.Apply();
            result.filterMode = FilterMode.Point;
            return result;
        }

        private string GetVeilnetFunctionsBaseUrl()
        {
            // Check environment variable first
            var envUrl = Environment.GetEnvironmentVariable("LV_VEILNET_FUNCTIONS_URL");
            if (!string.IsNullOrWhiteSpace(envUrl))
                return envUrl;

            // Check runtime config
            if (_runtimeConfig != null && !string.IsNullOrWhiteSpace(_runtimeConfig.VeilnetFunctionsBaseUrl))
                return _runtimeConfig.VeilnetFunctionsBaseUrl;

            // Return default
            return DefaultVeilnetFunctionsBaseUrl;
        }

        private string GetVeilnetLauncherPageUrl()
        {
            // Check environment variable first
            var envUrl = Environment.GetEnvironmentVariable("LV_VEILNET_LAUNCHER_URL");
            if (!string.IsNullOrWhiteSpace(envUrl))
                return envUrl;

            // Check runtime config
            if (_runtimeConfig != null && !string.IsNullOrWhiteSpace(_runtimeConfig.VeilnetLauncherPageUrl))
                return _runtimeConfig.VeilnetLauncherPageUrl;

            // Return default
            return DefaultVeilnetLauncherPageUrl;
        }

        private void OnVeilnetPrimaryActionClicked()
        {
            var username = (Environment.GetEnvironmentVariable("LV_VEILNET_USERNAME") ?? string.Empty).Trim();
            if (_veilnetLoggedIn || !string.IsNullOrWhiteSpace(username))
            {
                OnVeilnetResetClicked();
                return;
            }

            OnGoogleLoginClicked();
        }

        private void OnGoogleLoginClicked()
        {
            try
            {
                LauncherProtocolLinking.SetUnityLoginPending(_log);
                var launcherPageUrl = GetVeilnetLauncherPageUrl();
                var url = $"{launcherPageUrl}?autostart=1";
                _log.Info($"Opening Veilnet login page: {url}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to open Veilnet login page: {ex.Message}");
            }
        }

        private string GetGameHashesGetUrl()
        {
            var fromEnv = (Environment.GetEnvironmentVariable("LV_GAME_HASHES_GET_URL") ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return fromEnv;

            if (_runtimeConfig != null && !string.IsNullOrWhiteSpace(_runtimeConfig.GameHashesGetUrl))
                return _runtimeConfig.GameHashesGetUrl;

            return DefaultGameHashesGetUrl;
        }

        private string GetSupabaseAnonKey()
        {
            var fromEnv = (Environment.GetEnvironmentVariable("LV_SUPABASE_ANON_KEY") ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return fromEnv;

            if (_runtimeConfig != null && !string.IsNullOrWhiteSpace(_runtimeConfig.SupabaseAnonKey))
                return _runtimeConfig.SupabaseAnonKey;

            return DefaultSupabaseAnonKey;
        }

        private async Task VerifyBuildHashAsync()
        {
            try
            {
                var channel = Paths.IsDevBuild ? "dev" : "release";
                var exePath = GetGameExecutablePath();
                _log.Info($"Verifying official build hash for channel '{channel}'...");

                var verifier = _officialBuildVerifier ?? new OfficialBuildVerifier(_log, GetGameHashesGetUrl(), GetSupabaseAnonKey());
                var result = await verifier.VerifyAsync(channel, exePath).ConfigureAwait(false);

                _hashVerificationComplete = true;
                _hashVerified = result.Ok;
                _hashStatusMessage = result.Message;

                if (result.Ok)
                {
                    _log.Info($"Official build hash verified ({channel}): {result.ActualHash}");
                    if (_veilnetLoggedIn)
                    {
                        launchMode = "Online";
                        _log.Info("Swapped to Online mode (logged in with verified hash).");
                    }
                }
                else
                {
                    _log.Warn($"Official build hash verification failed ({channel}): {result.Message}. Failure={result.Failure}. Expected={result.ExpectedHash}, Actual={result.ActualHash}");
                    launchMode = "Offline";
                }
            }
            catch (Exception ex)
            {
                _hashVerificationComplete = true;
                _hashVerified = false;
                _hashStatusMessage = ex.Message;
                launchMode = "Offline";
                _log.Warn($"Build hash verification error: {ex.Message}");
            }
        }

        private void OnVeilnetResetClicked()
        {
            try
            {
                TryClearVeilnetAuth();
                LauncherProtocolLinking.ClearUnityLoginPending(_log);
                _veilnetLoggedIn = false;
                _veilnetUsername = "";
                _veilnetToken = "";
                _veilnetUserId = "";
                launchMode = "Offline";
                Environment.SetEnvironmentVariable("LV_VEILNET_USERNAME", null);
                Environment.SetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN", null);
                _log.Info("Veilnet authentication cleared. Launch mode reset to Offline.");
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to clear Veilnet auth: {ex.Message}");
            }
        }

        private async Task ConsumeVeilnetLinkCodeAsync(string code, string source)
        {
            try
            {
                _log.Info($"Consuming Veilnet link code from {source}...");
                var exchange = await _veilnetClient.ExchangeCodeAsync(code).ConfigureAwait(false);
                var me = await _veilnetClient.GetMeAsync(exchange.Token).ConfigureAwait(false);

                var username = string.IsNullOrWhiteSpace(me.Username) ? exchange.Username : me.Username;
                var userId = string.IsNullOrWhiteSpace(me.UserId) ? exchange.UserId : me.UserId;

                Environment.SetEnvironmentVariable("LV_VEILNET_USERNAME", username);
                Environment.SetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN", exchange.Token);
                TrySaveVeilnetAuth(username, exchange.Token, userId);
                LauncherProtocolLinking.ClearUnityLoginPending(_log);

                _veilnetUsername = username;
                _veilnetToken = exchange.Token;
                _veilnetUserId = userId;
                _veilnetLoggedIn = true;
                InitSupabaseSkinClient();

                if (_hashVerified)
                {
                    launchMode = "Online";
                    _log.Info($"Veilnet login complete. Swapped launch mode to Online.");
                }

                _log.Info($"Veilnet link exchange verified. Logged in as: {username}");
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to consume Veilnet link code: {ex.Message}");
            }
        }

        private async Task TryConsumePendingLinkCodesAsync()
        {
            if (_queuedLinkCodeConsumeInProgress)
                return;

            var codes = LauncherProtocolLinking.DequeuePendingLinkCodes(_log);
            if (codes.Length == 0)
                return;

            _queuedLinkCodeConsumeInProgress = true;
            try
            {
                for (var i = 0; i < codes.Length; i++)
                {
                    await ConsumeVeilnetLinkCodeAsync(codes[i], "queued protocol callback").ConfigureAwait(false);
                }
            }
            finally
            {
                _queuedLinkCodeConsumeInProgress = false;
            }
        }

        private void TryLoadVeilnetAuth()
        {
            try
            {
                _log.Info("Checking persisted Veilnet auth...");
                var record = TryReadVeilnetAuth();
                if (record == null)
                {
                    _log.Info("Persisted Veilnet session: absent.");
                    _veilnetLoggedIn = false;
                    return;
                }

                _log.Info($"Persisted Veilnet session: present for user '{record.Username}' (savedAtUtc={record.SavedAtUtc:o})");
                if (!TryValidateTokenLifetime(record.Token, out var expiresUtc, out var rejectionReason))
                {
                    var expText = expiresUtc.HasValue ? expiresUtc.Value.ToString("o") : "n/a";
                    _log.Warn($"Persisted Veilnet session rejected: reason={rejectionReason}; expUtc={expText}; nowUtc={DateTime.UtcNow:o}");
                    TryClearVeilnetAuth();
                    _veilnetLoggedIn = false;
                    return;
                }

                _veilnetUsername = record.Username;
                _veilnetToken = record.Token;
                _veilnetUserId = record.UserId;
                _veilnetLoggedIn = true;
                InitSupabaseSkinClient();

                Environment.SetEnvironmentVariable("LV_VEILNET_USERNAME", _veilnetUsername);
                Environment.SetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN", _veilnetToken);
                _log.Info($"Restored valid Veilnet session for: {_veilnetUsername} (expires {expiresUtc:o})");

                if (_hashVerified)
                {
                    launchMode = "Online";
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Failed to load Veilnet auth: {ex.Message}");
                _veilnetLoggedIn = false;
            }
        }

        /// <summary>
        /// Creates (or re-creates) the SupabaseSkinClient using the current auth token and
        /// the Veilnet Edge Functions base URL.
        /// Called after every successful Veilnet login.
        /// </summary>
        /// <summary>
        /// Creates (or re-creates) the SupabaseSkinClient using the current auth token and
        /// the Veilnet Edge Functions base URL.
        /// Called after every successful Veilnet login.
        /// </summary>
        private void InitSupabaseSkinClient()
        {
            try
            {
                var functionsUrl = GetVeilnetFunctionsBaseUrl();
                var anonKey = GetSupabaseAnonKey();

                _supabaseSkinClient = new SupabaseSkinClient(functionsUrl, anonKey, _veilnetToken, _httpClient);
                _log?.Info($"[SupabaseSkin] Client ready (Functions: {functionsUrl})");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[SupabaseSkin] Init failed: {ex.Message}");
            }
        }

        private SupabaseSkinClient GetOrCreateSupabaseSkinClient()
        {
            if (_supabaseSkinClient != null) return _supabaseSkinClient;
            if (_veilnetLoggedIn && !string.IsNullOrWhiteSpace(_veilnetToken))
            {
                InitSupabaseSkinClient();
            }
            return _supabaseSkinClient;
        }

        private void TrySaveVeilnetAuth(string username, string token, string userId)
        {
            try
            {
                Directory.CreateDirectory(Paths.SystemStateDir);
                var record = new VeilnetTokenRecord
                {
                    Username = (username ?? string.Empty).Trim(),
                    Token = (token ?? string.Empty).Trim(),
                    UserId = (userId ?? string.Empty).Trim(),
                    SavedAtUtc = DateTime.UtcNow
                };

                var serialized = LvcSerializer.WriteToString(LvcSerializer.SerializeObject(record));
                var bytes = Encoding.UTF8.GetBytes(serialized);

                var protectedBytes = DpapiHelper.Protect(bytes);
                var envelope = new ProtectedVeilnetTokenEnvelope
                {
                    PayloadBase64 = Convert.ToBase64String(protectedBytes)
                };

                LvcSerializer.Write(Paths.VeilnetLauncherAuthPath, LvcSerializer.SerializeObject(envelope));
                _log.Info($"Saved Veilnet authentication for: {username}");
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to save Veilnet auth: {ex.Message}");
            }
        }

        private VeilnetTokenRecord TryReadVeilnetAuth()
        {
            try
            {
                if (!File.Exists(Paths.VeilnetLauncherAuthPath))
                    return TryReadLegacyVeilnetAuth();

                var envelopeData = LvcSerializer.Read(Paths.VeilnetLauncherAuthPath);
                var envelope = new ProtectedVeilnetTokenEnvelope();
                LvcSerializer.ApplyObject(envelope, envelopeData);
                if (string.IsNullOrWhiteSpace(envelope.PayloadBase64))
                    return null;

                var protectedBytes = Convert.FromBase64String(envelope.PayloadBase64);
                var bytes = DpapiHelper.Unprotect(protectedBytes);
                var payload = Encoding.UTF8.GetString(bytes);

                var recordData = LvcSerializer.ReadFromString(payload);
                var record = new VeilnetTokenRecord();
                LvcSerializer.ApplyObject(record, recordData);

                record.Username = (record.Username ?? string.Empty).Trim();
                record.Token = (record.Token ?? string.Empty).Trim();
                record.UserId = (record.UserId ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(record.Token))
                    return null;

                return record;
            }
            catch (Exception ex)
            {
                _log.Warn($"Failed to load Veilnet auth: {ex.Message}");
                return null;
            }
        }

        private VeilnetTokenRecord TryReadLegacyVeilnetAuth()
        {
            try
            {
                if (!File.Exists(Paths.LegacyVeilnetAuthPath))
                    return null;

                var protectedBytes = File.ReadAllBytes(Paths.LegacyVeilnetAuthPath);
                var bytes = DpapiHelper.Unprotect(protectedBytes);
                var json = Encoding.UTF8.GetString(bytes);

                var record = new VeilnetTokenRecord();
                var usernameMatch = System.Text.RegularExpressions.Regex.Match(json, @"""Username""\s*:\s*""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var tokenMatch = System.Text.RegularExpressions.Regex.Match(json, @"""Token""\s*:\s*""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var userIdMatch = System.Text.RegularExpressions.Regex.Match(json, @"""UserId""\s*:\s*""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                record.Username = usernameMatch.Success ? usernameMatch.Groups[1].Value.Trim() : "";
                record.Token = tokenMatch.Success ? tokenMatch.Groups[1].Value.Trim() : "";
                record.UserId = userIdMatch.Success ? userIdMatch.Groups[1].Value.Trim() : "";
                record.SavedAtUtc = DateTime.UtcNow;

                if (string.IsNullOrWhiteSpace(record.Token))
                    return null;

                TrySaveVeilnetAuth(record.Username, record.Token, record.UserId);
                try { File.Delete(Paths.LegacyVeilnetAuthPath); } catch { }
                return record;
            }
            catch (Exception ex)
            {
                _log.Warn($"Failed to migrate legacy Veilnet auth: {ex.Message}");
                return null;
            }
        }

        private static bool TryValidateTokenLifetime(string token, out DateTime? expiresUtc, out string reason)
        {
            expiresUtc = null;
            reason = string.Empty;

            if (string.IsNullOrWhiteSpace(token))
            {
                reason = "missing_token";
                return false;
            }

            if (!TryGetJwtExpiryUtc(token, out var expUtc))
            {
                reason = "exp_missing_or_invalid";
                return false;
            }

            expiresUtc = expUtc;
            if (expUtc <= DateTime.UtcNow.AddSeconds(60))
            {
                reason = "expired_or_near_expiry";
                return false;
            }

            reason = "ok";
            return true;
        }

        private static bool TryGetJwtExpiryUtc(string token, out DateTime expiresUtc)
        {
            expiresUtc = DateTime.MinValue;
            try
            {
                var parts = (token ?? string.Empty).Split('.');
                if (parts.Length < 2)
                    return false;

                var payloadBytes = DecodeBase64Url(parts[1]);
                var json = Encoding.UTF8.GetString(payloadBytes);
                var expMatch = System.Text.RegularExpressions.Regex.Match(json, @"""exp""\s*:\s*(\d+)");
                if (expMatch.Success && long.TryParse(expMatch.Groups[1].Value, out var expSeconds))
                {
                    expiresUtc = DateTimeOffset.FromUnixTimeSeconds(expSeconds).UtcDateTime;
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static byte[] DecodeBase64Url(string value)
        {
            var s = (value ?? string.Empty).Trim().Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }

        private void TryClearVeilnetAuth()
        {
            try
            {
                var authPath = Paths.VeilnetLauncherAuthPath;
                if (File.Exists(authPath))
                {
                    File.Delete(authPath);
                    _log.Info("Cleared Veilnet authentication file.");
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Failed to clear Veilnet auth: {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task RefreshGameVersionsAsync()
        {
            if (_gameVersionService == null || _versionsLoading) return;
            _versionsLoading = true;
            _versionStatusMessage = "Checking versions...";
            try
            {
                var versions = await _gameVersionService.GetVersionsAsync().ConfigureAwait(false);
                _mainThreadQueue.Enqueue(() =>
                {
                    _gameVersions = versions ?? new System.Collections.Generic.List<GameVersionInfo>();
                    _versionsLoading = false;

                    // keep or pick a sensible default selection (LATEST wins)
                    if (_selectedVersion == null && _gameVersions.Count > 0)
                    {
                        _selectedVersion = _gameVersions[0];
                        _selectedIsLatest = true;
                    }
                    else if (_selectedVersion != null)
                    {
                        // Refresh installed flags on the selected version
                        foreach (var v in _gameVersions)
                        {
                            if (string.Equals(v.Tag, _selectedVersion.Tag, StringComparison.OrdinalIgnoreCase))
                            {
                                _selectedVersion = v;
                                break;
                            }
                        }
                    }
                    _versionStatusMessage = _gameVersions.Count == 0 ? "No versions found" : "";
                    _log?.Info($"Version list refreshed: {_gameVersions.Count} version(s).");

                    EvaluateLatestInstallPrompt();
                });
            }
            catch (Exception ex)
            {
                _mainThreadQueue.Enqueue(() =>
                {
                    _versionsLoading = false;
                    _versionStatusMessage = "Version check failed";
                });
                _log?.Warn($"RefreshGameVersionsAsync failed: {ex.Message}");
            }
        }

        private void StartSelectedVersionDownload()
        {
            if (_selectedVersion == null || _isDownloadingVersion) return;
            _isDownloadingVersion = true;
            _downloadProgress = 0.0;
            _log?.Info($"Downloading version {_selectedVersion.Tag}...");
            var progress = new Progress<double>(p => _downloadProgress = p);
            var installer = _versionInstaller;
            var version = _selectedVersion;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var exe = await installer.DownloadAndInstallAsync(version, progress).ConfigureAwait(false);
                    _mainThreadQueue.Enqueue(() =>
                    {
                        _isDownloadingVersion = false;
                        _downloadProgress = 1.0;
                        version.IsInstalled = true;
                        version.InstallDirectory = System.IO.Path.GetDirectoryName(exe);
                        _log?.Info($"Version {version.Tag} installed: {exe}");
                        _ = RefreshGameVersionsAsync();
                    });
                }
                catch (Exception ex)
                {
                    _mainThreadQueue.Enqueue(() =>
                    {
                        _isDownloadingVersion = false;
                        _downloadProgress = 0.0;
                        _versionStatusMessage = $"Download failed: {ex.Message}";
                    });
                    _log?.Error($"Download of {version.Tag} failed: {ex.Message}");
                }
            });
        }

        public void LaunchGame()
        {
            if (_isLaunching)
            {
                // Kill the game process if running
                if (_gameProcess != null && !_gameProcess.HasExited)
                {
                    try
                    {
                        _gameProcess.Kill();
                        _log.Info("Game process killed by user.");
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"Failed to kill game process: {ex.Message}");
                    }
                }
                _isLaunching = false;
                return;
            }

            DoLaunchGame();
        }

        private void DoLaunchGame()
        {
            try
            {
                _isLaunching = true;

                if (string.Equals(launchMode, "Online", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_hashVerified)
                    {
                        _log.Error($"Cannot launch in Online mode: Official build hash is not verified ({_hashStatusMessage}).");
                        _isLaunching = false;
                        launchMode = "Offline";
                        return;
                    }

                    if (!_veilnetLoggedIn)
                    {
                        _log.Error("Cannot launch in Online mode: Veilnet login required.");
                        _isLaunching = false;
                        launchMode = "Offline";
                        return;
                    }
                }

                _log.Info($"Launching game in {launchMode} mode...");

                // Build launch arguments
                var isOffline = launchMode.Equals("Offline", StringComparison.OrdinalIgnoreCase);
                var args = isOffline ? "--offline" : "--online";

                // Add renderer argument
                if (!string.IsNullOrWhiteSpace(_settings.RendererBackend))
                {
                    args += $" --renderer={_settings.RendererBackend.ToLowerInvariant()}";
                }

                // Add render distance argument
                args += $" --render-distance={_settings.LauncherRenderDistance}";

                // Resolve the game executable: the selected downloadable version when
                // installed, otherwise the launcher-local fallback.
                string exePath = null;
                if (_selectedVersion != null)
                {
                    exePath = Core.Paths.TryResolveInstalledVersionExe(_selectedVersion.Tag);
                    if (string.IsNullOrWhiteSpace(exePath))
                    {
                        _log.Warn($"Selected version {_selectedVersion.Tag} is not installed; falling back to bundled exe.");
                    }
                }
                if (string.IsNullOrWhiteSpace(exePath))
                    exePath = GetGameExecutablePath();

                if (!File.Exists(exePath))
                {
                    _log.Error($"Game executable not found: {exePath}");
                    _isLaunching = false;
                    return;
                }

                // Verify the installed game exe against its release manifest hash
                // (version.json beside the exe). A mismatched exe never launches in
                // online mode — this is the local half of the anti-spoof chain; the
                // Supabase game-hashes list completes it server-side.
                var gameHashOk = VerifyInstalledGameHash(exePath, out var gameHashStatus);
                if (gameHashOk)
                {
                    _log.Info($"Game hash verified: {gameHashStatus}");
                }
                else
                {
                    _log.Warn($"Game hash check failed: {gameHashStatus}");
                    if (!isOffline)
                    {
                        _log.Error("Online launch blocked: installed game hash does not match its release manifest.");
                        _isLaunching = false;
                        launchMode = "Offline";
                        return;
                    }
                }

                _log.Info($"Launching game from: {exePath}");
                _log.Info($"Launch arguments: {args}");

                // Start the game process
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args,
                    WorkingDirectory = Path.GetDirectoryName(exePath),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // Set environment variables
                startInfo.EnvironmentVariables["LATTICEVEIL_INSTALL_ROOT"] = Core.Paths.InstallRootDir;
                startInfo.EnvironmentVariables["LV_PROCESS_KIND"] = "game";
                startInfo.EnvironmentVariables["LV_LAUNCH_MODE"] = isOffline ? "offline" : "online";
                startInfo.EnvironmentVariables["LV_BUILD_CHANNEL"] = Paths.IsDevBuild ? "dev" : "release";

                if (_veilnetLoggedIn)
                {
                    startInfo.EnvironmentVariables["LV_VEILNET_USERNAME"] = _veilnetUsername;
                    startInfo.EnvironmentVariables["LV_VEILNET_ACCESS_TOKEN"] = _veilnetToken;
                }

                // ONE Task Manager collection: put the launcher into the group
                // job BEFORE starting the game so the game is born inside it —
                // the launcher window ("LatticeVeil") is the group head and the
                // game becomes its sub-process for every game version.
                GameProcessJob.PrepareForLaunch();

                _gameProcess = Process.Start(startInfo);

                // Assign the game into the nested sandbox job (group membership
                // was already inherited) and tie its lifetime to the launcher
                // via kill-on-close.
                GameProcessJob.Attach(_gameProcess);

                _gameWindowRenamed = false;
                _gameWindowRenameElapsed = 0f;
                _gameWindowRenameNextAttempt = 0.5f;

                if (_gameProcess != null)
                {
                    _log.Info("Game process started successfully.");

                    // Instant Quit: the user wants NO launcher at all while
                    // playing — quit the process entirely. The game survives
                    // because the job object only kill-on-closes when the JOB
                    // handle closes, which OnDestroy does only for launcher
                    // teardown paths that should kill the game; here we release
                    // the job instead so the game outlives the launcher.
                    if (_settings.InstantQuitEnabled)
                    {
                        _log.Info("InstantQuitEnabled is on; quitting launcher process (game keeps running).");
                        GameProcessJob.ReleaseForInstantQuit();
                        _settings.Save(_log);
                        _profile?.Save(_log);
                        _instantQuitInProgress = true;
                        Application.Quit();
                        return;
                    }

                    // Hide the launcher completely (screen, taskbar AND alt-tab)
                    // while the game runs when KeepLauncherOpen is false. The
                    // process stays alive watching the game and restores the
                    // window automatically once the game exits.
                    if (!_settings.KeepLauncherOpen)
                    {
                        _log.Info("KeepLauncherOpen is false; hiding launcher until the game closes.");
                        _parkedLauncherForGame = true;
                        Application.runInBackground = true;
                        LauncherWindowInitializer.HideLauncherWindowCompletely();
                    }
                }
                else
                {
                    _log.Error("Failed to start game process.");
                    _isLaunching = false;
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to launch game: {ex.Message}");
                _isLaunching = false;
            }
        }

        private string GetGameExecutablePath()
        {
            // 1. If running as a standalone built process, resolve its OWN running process executable directly.
            try
            {
                var currentProc = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(currentProc) && File.Exists(currentProc) && !IsEditorOrHostProcess(currentProc))
                {
                    return Path.GetFullPath(currentProc);
                }
            }
            catch { }

            try
            {
                var args = Environment.GetCommandLineArgs();
                if (args != null && args.Length > 0)
                {
                    var cmdPath = (args[0] ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(cmdPath) && File.Exists(cmdPath) && !IsEditorOrHostProcess(cmdPath))
                    {
                        return Path.GetFullPath(cmdPath);
                    }
                }
            }
            catch { }

            // 2. Search executable in current directory / AppContext.BaseDirectory
            try
            {
                var baseDir = AppContext.BaseDirectory;
                if (Directory.Exists(baseDir))
                {
                    var exeFiles = Directory.GetFiles(baseDir, "*.exe", SearchOption.TopDirectoryOnly);
                    foreach (var exe in exeFiles)
                    {
                        if (!IsEditorOrHostProcess(exe))
                            return Path.GetFullPath(exe);
                    }
                }
            }
            catch { }

            // 3. Fallback for Unity Editor: check project builds folders or DEV folder
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var candidatePaths = new[]
            {
                Path.Combine(projectRoot, "builds", "LatticeVeil.exe"),
                Path.Combine(projectRoot, "builds", "DEV", "LatticeVeil.exe"),
                Path.Combine(projectRoot, "BUILDS", "DEV", "LatticeVeil.exe"),
                Path.Combine(projectRoot, "builds", "DEV", "LatticeVeilMonoGame.exe"),
                @"C:\Users\Redacted\Documents\LatticeVeil_project\DEV\LatticeVeilMonoGame.exe"
            };

            foreach (var candidate in candidatePaths)
            {
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }

            // Search all *.exe files in projectRoot/builds or projectRoot/Build
            try
            {
                var buildsFolder = Path.Combine(projectRoot, "builds");
                if (Directory.Exists(buildsFolder))
                {
                    var exeFiles = Directory.GetFiles(buildsFolder, "*.exe", SearchOption.AllDirectories);
                    foreach (var exe in exeFiles)
                    {
                        if (!IsEditorOrHostProcess(exe))
                            return Path.GetFullPath(exe);
                    }
                }
            }
            catch { }

            return Path.Combine(projectRoot, "builds", "LatticeVeil.exe");
        }

        private static bool IsEditorOrHostProcess(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            var fileName = Path.GetFileName(path).ToLowerInvariant();
            return fileName.Contains("unity.exe")
                || fileName.Contains("unityeditor")
                || fileName.Contains("unitycrashhandler")
                || fileName.Contains("protocolbridge")
                || fileName == "dotnet.exe"
                || fileName == "dotnet";
        }

        /// <summary>
        /// Verifies the installed game exe against the version.json manifest stored
        /// beside it at install time. Returns true (with the hash as status) when the
        /// manifest is absent or the hash matches; false on any mismatch or read error.
        /// </summary>
        private static bool VerifyInstalledGameHash(string exePath, out string status)
        {
            status = "no manifest";
            try
            {
                var manifestPath = Path.Combine(Path.GetDirectoryName(exePath) ?? "", "version.json");
                if (!File.Exists(manifestPath))
                    return true; // zip-era install without a manifest: nothing to verify against

                var manifestJson = File.ReadAllText(manifestPath);
                var expectedHash = ExtractManifestString(manifestJson, "sha256");
                if (string.IsNullOrWhiteSpace(expectedHash))
                    return true; // manifest without a hash: treat as unverified-but-allowed

                using var sha = System.Security.Cryptography.SHA256.Create();
                using var stream = File.OpenRead(exePath);
                var hashBytes = sha.ComputeHash(stream);
                var sb = new System.Text.StringBuilder(hashBytes.Length * 2);
                foreach (var b in hashBytes) sb.Append(b.ToString("x2"));
                var actualHash = sb.ToString();

                status = actualHash;
                return string.Equals(actualHash, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                status = $"verification error: {ex.Message}";
                return false;
            }
        }

        /// <summary>Minimal string lookup inside a flat json object (no dependency).</summary>
        private static string ExtractManifestString(string json, string key)
        {
            var search = "\"" + key + "\"";
            var idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;

            var colon = json.IndexOf(':', idx + search.Length);
            if (colon < 0) return null;

            var quoteOpen = json.IndexOf('"', colon + 1);
            if (quoteOpen < 0) return null;

            var quoteClose = quoteOpen + 1;
            while (quoteClose < json.Length)
            {
                if (json[quoteClose] == '\\') { quoteClose += 2; continue; }
                if (json[quoteClose] == '"') break;
                quoteClose++;
            }

            if (quoteClose >= json.Length) return null;
            return json.Substring(quoteOpen + 1, quoteClose - quoteOpen - 1);
        }

        private void OpenLogsFolder()
        {
            try
            {
                var logsDir = Paths.LogsDir;
                if (Directory.Exists(logsDir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = logsDir,
                        UseShellExecute = true
                    });
                    _log.Info($"Opened logs directory: {logsDir}");
                }
                else
                {
                    _log.Warn($"Logs directory does not exist: {logsDir}");
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Failed to open logs directory: {ex.Message}");
            }
        }

        private void OpenGameFolder()
        {
            try
            {
                var rootDir = Paths.RootDir;
                if (Directory.Exists(rootDir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = rootDir,
                        UseShellExecute = true
                    });
                    _log.Info($"Opened game directory: {rootDir}");
                }
                else
                {
                    _log.Warn($"Game directory does not exist: {rootDir}");
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Failed to open game directory: {ex.Message}");
            }
        }

        private void HandleLauncherMinimizeButtonRequest()
        {
            _log.Info("Launcher minimize requested.");
            LauncherWindowInitializer.MinimizeLauncherWindow();
        }

        private void HandleLauncherCloseButtonRequest()
        {
            _log.Info("Launcher close requested.");
            Application.Quit();
        }

        private void OnDestroy()
        {
            // Save settings on exit
            if (_settings != null)
            {
                _settings.Save(_log);
            }

            if (_profile != null)
            {
                _profile.Save(_log);
            }

            // Clean up game process — but NOT on the Instant Quit path, where
            // the user explicitly wants the game to outlive the launcher.
            if (!_instantQuitInProgress)
            {
                if (_gameProcess != null && !_gameProcess.HasExited)
                {
                    try
                    {
                        _gameProcess.Kill();
                    }
                    catch { }
                }

                // Close the job object (kill-on-close ends any remaining game processes)
                GameProcessJob.Shutdown();
            }

            // Clean up HTTP client
            _httpClient?.Dispose();

            _log.Info("Unity Launcher shutting down.");
        }

        // Helper classes for Veilnet auth (100% format-compatible with MonoGame LvcSerializer/DPAPI)
        public sealed class VeilnetTokenRecord
        {
            public string Username { get; set; } = string.Empty;
            public string Token { get; set; } = string.Empty;
            public string UserId { get; set; } = string.Empty;
            public DateTime SavedAtUtc { get; set; }
        }

        public sealed class ProtectedVeilnetTokenEnvelope
        {
            public string PayloadBase64 { get; set; } = string.Empty;
        }
    }
}
