using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using LatticeVeil.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LatticeVeil.Game
{
    /// <summary>
    /// Unity rebuild of the MonoGame main menu (MainMenuScreen + UIManager).
    ///
    /// Visual parity notes (keep in sync with the MonoGame version):
    ///  - Background: MainMenu.png stretched over the WHOLE window (MonoGame draws to WindowViewport);
    ///    black when missing.
    ///  - Buttons live in a virtual UI viewport (window size / min(w/1920, h/1080)) centered in the
    ///    window, so the letterboxing math matches UiLayout.
    ///  - Button textures are drawn stretched into their layout rect; on hover the button shifts
    ///    up 2 UI px and a 12-offset additive glow (188,232,255,76) is drawn behind it.
    ///  - Without a texture: flat (30,30,30) panel, 2px white border, centered uppercase label.
    ///  - Singleplayer / Multiplayer / Options / Quit stacked top-to-bottom, Profile bottom-left,
    ///    Screenshots bottom-right.
    ///  - Escape quits (writes exit.request like the MonoGame RequestQuit for launcher keep-open).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MainMenuController : MonoBehaviour
    {
        private static readonly Color GlowColor = new Color32(188, 232, 255, 76);
        private const int DefaultWindowWidth = 1440;
        private const int DefaultWindowHeight = 900;
        private static readonly Color PanelBg = new Color(30f / 255f, 30f / 255f, 30f / 255f);
        private static readonly Color DisabledText = new Color(160f / 255f, 160f / 255f, 160f / 255f);

        private sealed class MenuButton
        {
            public string Label;
            public string TexturePath;
            public Action OnClick;
            public Rect LayoutRect;

            public GameObject Root;
            public RectTransform Transform;
            public RawImage Image;
            public Texture2D Texture;
            public GameObject GlowRoot;
            public Vector2 RestPosition;
        }

        private Canvas _canvas;
        private RectTransform _menuRoot;
        private readonly List<MenuButton> _buttons = new List<MenuButton>();
        private float _lastWindowW;
        private float _lastWindowH;
        private Texture2D _backgroundTexture;
        private static Material _glowMaterial;

        private const float _bootEscGrace = 0.5f;

        private void Awake()
        {
            GameLog.Initialize();
            EnforceWindowedDefault();
            StartCoroutine(SetWindowTitleWhenReady());
            BuildUi();
            SettingsBehavior.Apply(GameSettings.LoadOrCreate(null));
            StartCoroutine(LockResizeWhenReady());
        }

        /// <summary>Waits for the player window, then locks OS resizing (fixed size; GUI scales instead).</summary>
        private System.Collections.IEnumerator LockResizeWhenReady()
        {
            for (var attempt = 0; attempt < 60; attempt++)
            {
                var hwnd = System.IntPtr.Zero;
                try { hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; }
                catch { }
                if (hwnd != System.IntPtr.Zero)
                {
                    SettingsBehavior.LockWindowResize();
                    yield break;
                }
                yield return new WaitForSeconds(0.25f);
            }
        }

        /// <summary>
        /// The game must NOT be borderless by default: force normal windowed
        /// mode (borders + title bar). Borderless/fullscreen returns later as
        /// an explicit user option in the Options screen.
        ///
        /// Verified on Windows: the player can boot with a WS_POPUP (borderless)
        /// window even when the serialized fullscreenMode is Windowed, and
        /// assigning Screen.fullScreenMode alone does NOT recreate the window —
        /// only Screen.SetResolution with the mode reliably applies it.
        /// </summary>
        /// <summary>
        /// The player can boot with a WS_POPUP (borderless) window even when
        /// the serialized fullscreenMode is Windowed, and assigning
        /// Screen.fullScreenMode alone does NOT recreate the window — only
        /// Screen.SetResolution with the mode reliably applies it.
        ///
        /// Boot with the SAVED mode instead of a hardcoded windowed default:
        /// forcing windowed first made fullscreen not stick across relaunches
        /// (and let EnsureWindowFrame staple a title bar onto the borderless
        /// window). SettingsBehavior.Apply (after BuildUi) is still the single
        /// applier for everything else.
        /// </summary>
        private static void EnforceWindowedDefault()
        {
            var settings = GameSettings.LoadOrCreate(null);
            SettingsBehavior.SetFullscreenDesired(settings.Fullscreen);
            var mode = settings.Fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
            Screen.SetResolution(settings.ResolutionWidth, settings.ResolutionHeight, mode);
        }

        /// <summary>
        /// Sets the player window title to "LatticeVeil - V#" (Unity names the
        /// window after the product by default). Retries while the player is
        /// still creating its window; a no-op when embedded, where the title
        /// is not visible and the handle may never resolve.
        /// </summary>
        private System.Collections.IEnumerator SetWindowTitleWhenReady()
        {
            var title = GameVersion.WindowTitle;

            for (var attempt = 0; attempt < 40; attempt++) // up to ~10s
            {
                var hwnd = IntPtr.Zero;
                try { hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; }
                catch { /* retry */ }

                if (hwnd != IntPtr.Zero)
                {
                    if (!SetWindowTextW(hwnd, title))
                        Debug.LogWarning("MainMenuController: SetWindowText failed for the game window title.");

                    EnsureWindowFrame(hwnd);
                    yield break;
                }

                yield return new WaitForSeconds(0.25f);
            }

            Debug.LogWarning("MainMenuController: window handle never appeared; title stays default.");
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetWindowTextW(IntPtr hWnd, string text);

        /// <summary>
        /// Verified on Windows: this player can create its window as WS_POPUP
        /// (no caption, no thickframe) even with FullScreenMode.Windowed set,
        /// and Unity's Screen APIs do not rebuild the frame. As a last-resort
        /// for standalone (non-embedded) play, add the missing WS_CAPTION /
        /// WS_THICKFRAME style to OUR OWN top-level window and reapply it to
        /// refresh the non-client frame. Only runs when the window is borderless,
        /// so a correct player never gets touched, and never runs when hosted
        /// inside the launcher (the launcher owns that window then).
        /// </summary>
        private static void EnsureWindowFrame(IntPtr hwnd)
        {
            // Borderless fullscreen is an explicit user option; the game
            // must not staple a caption/thickframe onto it (that is what made
            // fullscreen not persist across relaunches).
            if (SettingsBehavior.FullscreenDesired)
                return;

            const int GWL_STYLE = -16;
            const long WS_CAPTION = 0x00C00000;
            const long WS_THICKFRAME = 0x00040000;
            const long WS_MINIMIZEBOX = 0x00020000;
            const long WS_MAXIMIZEBOX = 0x00010000;
            const long WS_SYSMENU = 0x00080000;
            const uint SWP_FRAMECHANGED = 0x0020;
            const uint SWP_NOMOVE = 0x0002;
            const uint SWP_NOSIZE = 0x0001;
            const uint SWP_NOZORDER = 0x0004;

            var style = GetWindowLongW(hwnd, GWL_STYLE);
            if ((style & WS_CAPTION) != 0 && (style & WS_THICKFRAME) != 0)
                return; // already framed — nothing to do

            Debug.Log($"MainMenuController: player window is borderless (style 0x{style:X}); adding caption + thickframe.");

            var newStyle = style | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
            SetWindowLongW(hwnd, GWL_STYLE, newStyle);
            SetWindowPosW(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER);
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern long GetWindowLongW(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern long SetWindowLongW(IntPtr hWnd, int index, long newStyle);

        [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
        private static extern bool SetWindowPosW(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private void Update()
        {
            // Re-layout when the OS window changes OR the canvas rect changes
            // (GUI scale edits the CanvasScaler reference, which resizes the
            // canvas without touching Screen.width/height).
            var canvasRect = _canvas != null ? _canvas.GetComponent<RectTransform>().rect : new Rect();
            bool windowChanged = Math.Abs(Screen.width - _lastWindowW) > 0.5f || Math.Abs(Screen.height - _lastWindowH) > 0.5f;
            bool canvasChanged = Math.Abs(canvasRect.width - _lastCanvasW) > 0.5f || Math.Abs(canvasRect.height - _lastCanvasH) > 0.5f;
            if (windowChanged || canvasChanged)
                ApplyLayout();

            // Options / Screenshots own Escape while open (they close the
            // screen, not the game). Also swallow ESC pressed during the
            // boot window: when embedded, a keyup/keydown from the launcher
            // that spawned us can arrive before the player window settles
            // and instantly request a quit (exit.request + Application.Quit),
            // which the launcher then mirrors — dead session.
            if (Input.GetKeyDown(KeyCode.Escape) && !_optionsOpen && !_screenshotsOpen
                && Time.unscaledTime > _bootEscGrace)
                RequestQuit();

            // F2 = screenshot (MonoGame parity: Screenshot bind from options.lvc).
            if (Input.GetKeyDown(KeyCode.F2))
                TakeScreenshotNow();
        }

        /// <summary>Captures everything currently on the game window and
        /// saves it under the screen-context name (mainmenu, options-…, …).</summary>
        private void TakeScreenshotNow()
        {
            ScreenshotService.SetScreenContext(_optionsOpen ? OptionsScreenController.CurrentContextName
                                                            : "mainmenu");
            ScreenshotService.Capture(this);
        }

        private float _lastCanvasW;
        private float _lastCanvasH;

        private bool _optionsOpen;

        private void OnOptions()
        {
            if (_optionsOpen)
                return;
            _optionsOpen = true;
            OptionsScreenController.Open(_canvas, () => _optionsOpen = false);
        }

        private static Material GlowMaterial
        {
            get
            {
                if (_glowMaterial == null)
                {
                    var shader = Shader.Find("LatticeVeil/UIAdditiveGlow");
                    if (shader == null)
                    {
                        // Shader not present in this build — render buttons
                        // unstyled rather than throwing on every button.
                        Debug.LogWarning("MainMenuController: UIAdditiveGlow shader not found; hover glow disabled.");
                        return null;
                    }
                    _glowMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                }
                return _glowMaterial;
            }
        }

        private void BuildUi()
        {
            var canvasGo = new GameObject("MainMenuCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.planeDistance = 10f;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            if (FindFirstObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                DontDestroyOnLoad(es);
            }

            // Background over the whole window (MonoGame draws MainMenu.png to WindowViewport).
            var bgGo = new GameObject("Background", typeof(RectTransform), typeof(CanvasRenderer));
            bgGo.transform.SetParent(canvasGo.transform, false);
            var bgRect = bgGo.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;

            _backgroundTexture = GameAssetLoader.LoadTexture("textures/menu/backgrounds/MainMenu.png");
            if (_backgroundTexture != null)
            {
                var raw = bgGo.AddComponent<RawImage>();
                raw.texture = _backgroundTexture;
            }
            else
            {
                // MonoGame fallback: solid black screen.
                var img = bgGo.AddComponent<Image>();
                img.color = Color.black;
            }

            // Virtual UI viewport root: centered, scaled by the reference scale, letterboxed.
            var rootGo = new GameObject("MenuRoot", typeof(RectTransform));
            rootGo.transform.SetParent(canvasGo.transform, false);
            _menuRoot = rootGo.GetComponent<RectTransform>();
            _menuRoot.anchorMin = new Vector2(0.5f, 0.5f);
            _menuRoot.anchorMax = new Vector2(0.5f, 0.5f);
            _menuRoot.pivot = new Vector2(0.5f, 0.5f);

            CreateButtons();

            if (!GameAssetLoader.MenuAssetsPresent())
            {
                Debug.LogWarning(
                    "Main menu: some menu textures are missing. Install assets to: " +
                    Paths.ToUiPath(Paths.AssetsDir) + " (last looked in: " +
                    Paths.ToUiPath(GameAssetLoader.LastSourceRoot ?? Paths.AssetsDir) + ")");
            }

            ApplyLayout();
        }

        private void CreateButtons()
        {
            AddButton("SINGLEPLAYER", "textures/menu/buttons/Singleplayer.png", OnSingleplayer);
            AddButton("MULTIPLAYER", "textures/menu/buttons/Multiplayer.png", OnMultiplayer);
            AddButton("OPTIONS", "textures/menu/buttons/options.png", OnOptions);
            AddButton("QUIT", "textures/menu/buttons/Quit.png", OnQuit);
            AddButton("PROFILE", "textures/menu/buttons/Profile.png", OnProfile);
            AddButton("SCREENSHOTS", "textures/menu/buttons/screenshots.png", OnScreenshots);
        }

        private void AddButton(string label, string texturePath, Action onClick)
        {
            var rootGo = new GameObject("Btn_" + label, typeof(RectTransform), typeof(CanvasRenderer));
            rootGo.transform.SetParent(_menuRoot, false);
            var rect = rootGo.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0.5f);

            var btn = new MenuButton
            {
                Label = label,
                TexturePath = texturePath,
                OnClick = onClick,
                Root = rootGo,
                Transform = rect,
                Texture = GameAssetLoader.LoadTexture(texturePath)
            };

            // Hover glow: 12 offset copies of the texture with additive blending,
            // same offsets as Button.DrawTextureHoverGlow in the MonoGame version.
            var glowGo = new GameObject("Glow", typeof(RectTransform));
            glowGo.transform.SetParent(rootGo.transform, false);
            glowGo.SetActive(false);
            var glowRect = glowGo.GetComponent<RectTransform>();
            glowRect.anchorMin = Vector2.zero;
            glowRect.anchorMax = Vector2.zero;
            glowRect.sizeDelta = Vector2.zero; // children carry offsets
            btn.GlowRoot = glowGo;

            Vector2[] offsets =
            {
                new Vector2(-3, 0), new Vector2(3, 0), new Vector2(0, -3), new Vector2(0, 3),
                new Vector2(-2, -2), new Vector2(2, -2), new Vector2(-2, 2), new Vector2(2, 2),
                new Vector2(-1, 0), new Vector2(1, 0), new Vector2(0, -1), new Vector2(0, 1)
            };

            foreach (var offset in offsets)
            {
                var go = new GameObject("GlowCopy", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
                go.transform.SetParent(glowGo.transform, false);
                var grt = go.GetComponent<RectTransform>();
                grt.anchorMin = new Vector2(0.5f, 0.5f);
                grt.anchorMax = new Vector2(0.5f, 0.5f);
                grt.pivot = new Vector2(0.5f, 0.5f);
                grt.anchoredPosition = offset;
                var raw = go.GetComponent<RawImage>();
                raw.material = GlowMaterial;
                raw.color = GlowColor;
                raw.enabled = false;
                btn.Image = raw; // reused below only for glow copies; main image set separately
            }

            // Main face: texture stretched to the layout rect, or the MonoGame fallback panel.
            if (btn.Texture != null)
            {
                var faceGo = new GameObject("Face", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
                faceGo.transform.SetParent(rootGo.transform, false);
                var frt = faceGo.GetComponent<RectTransform>();
                SetFullRect(frt);
                var raw = faceGo.GetComponent<RawImage>();
                raw.texture = btn.Texture;
                raw.raycastTarget = true;
                btn.Image = raw;
            }
            else
            {
                BuildFallbackPanel(rootGo, label);
                btn.Image = null;
            }

            // Whole-rect click target + hover tracking.
            var proxyGo = new GameObject("ClickTarget", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            proxyGo.transform.SetParent(rootGo.transform, false);
            var prt = proxyGo.GetComponent<RectTransform>();
            SetFullRect(prt);
            var proxyImg = proxyGo.GetComponent<Image>();
            proxyImg.color = new Color(0f, 0f, 0f, 0f); // invisible hit area
            var unityButton = proxyGo.GetComponent<Button>();
            var captured = btn;
            unityButton.onClick.AddListener(() =>
            {
                if (captured.OnClick != null)
                    captured.OnClick();
            });

            // Hover: shift up 2 UI px + additive glow (Button.Draw hover behavior in MonoGame).
            var trigger = proxyGo.AddComponent<EventTrigger>();
            var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            enter.callback.AddListener(_ => SetHover(captured, true));
            trigger.triggers.Add(enter);
            var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            exit.callback.AddListener(_ => SetHover(captured, false));
            trigger.triggers.Add(exit);

            _buttons.Add(btn);
        }

        private static void SetFullRect(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>MonoGame Button.Draw fallback: flat panel + 2px border + centered label.</summary>
        private static void BuildFallbackPanel(GameObject root, string label)
        {
            var panelGo = new GameObject("FallbackPanel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            panelGo.transform.SetParent(root.transform, false);
            SetFullRect(panelGo.GetComponent<RectTransform>());
            panelGo.GetComponent<Image>().color = PanelBg;

            const float border = 2f;
            CreateBorderEdge(root, "BorderTop", new Vector2(0.5f, 1f), new Vector2(0f, -border * 0.5f), new Vector2(0f, border));
            CreateBorderEdge(root, "BorderBottom", new Vector2(0.5f, 0f), new Vector2(0f, border * 0.5f), new Vector2(0f, border));
            CreateBorderEdge(root, "BorderLeft", new Vector2(0f, 0.5f), new Vector2(border * 0.5f, 0f), new Vector2(border, 0f));
            CreateBorderEdge(root, "BorderRight", new Vector2(1f, 0.5f), new Vector2(-border * 0.5f, 0f), new Vector2(border, 0f));

            var textGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            textGo.transform.SetParent(root.transform, false);
            SetFullRect(textGo.GetComponent<RectTransform>());
            var text = textGo.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 42;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.text = label;
        }

        private static void CreateBorderEdge(GameObject parent, string name, Vector2 anchor, Vector2 offset, Vector2 thickness)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent.transform, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = offset;
            rt.sizeDelta = thickness.x > 0f
                ? new Vector2(thickness.x, 0f)
                : new Vector2(0f, thickness.y);
            // Stretch along the free axis by using a matching parent-driven axis.
            if (thickness.x > 0f)
            {
                rt.anchorMin = new Vector2(0f, anchor.y);
                rt.anchorMax = new Vector2(1f, anchor.y);
            }
            else
            {
                rt.anchorMin = new Vector2(anchor.x, 0f);
                rt.anchorMax = new Vector2(anchor.x, 1f);
            }
            go.GetComponent<Image>().color = Color.white;
        }

        /// <summary>
        /// Lays out every button in CANVAS units. The CanvasScaler already
        /// maps the window to reference units, so the old virtual-viewport
        /// double-scale (MenuRoot sized to window/referenceScale on top of
        /// the scaler) pushed the footer icons off-screen and the button
        /// column past the top edge.
        /// </summary>
        private void ApplyLayout()
        {
            if (_canvas == null || _menuRoot == null)
                return;

            _lastWindowW = Screen.width;
            _lastWindowH = Screen.height;

            // MenuRoot fills the canvas exactly; no extra local scale.
            _menuRoot.anchorMin = Vector2.zero;
            _menuRoot.anchorMax = Vector2.one;
            _menuRoot.offsetMin = Vector2.zero;
            _menuRoot.offsetMax = Vector2.zero;
            _menuRoot.localScale = Vector3.one;

            var canvasRect = _canvas.GetComponent<RectTransform>().rect;
            _lastCanvasW = canvasRect.width;
            _lastCanvasH = canvasRect.height;
            var rects = MainMenuLayout.Create(canvasRect.width, canvasRect.height);
            var map = new Dictionary<string, Rect>
            {
                { "SINGLEPLAYER", rects.Singleplayer },
                { "MULTIPLAYER", rects.Multiplayer },
                { "OPTIONS", rects.Options },
                { "QUIT", rects.Quit },
                { "PROFILE", rects.Profile },
                { "SCREENSHOTS", rects.Screenshots }
            };

            foreach (var btn in _buttons)
            {
                Rect r;
                if (!map.TryGetValue(btn.Label, out r))
                    continue;

                btn.LayoutRect = r;
                // Convert MonoGame top-left rect to centered anchored position (canvas y-up).
                var center = new Vector2(r.x + r.width / 2f, r.y + r.height / 2f);
                var uiCenter = new Vector2(center.x, canvasRect.height - center.y); // Unity y-up
                btn.RestPosition = uiCenter;
                btn.Transform.anchoredPosition = uiCenter;
                btn.Transform.sizeDelta = new Vector2(r.width, r.height);

                if (btn.GlowRoot != null)
                {
                    // Glow copies inherit the button size; give each a full-stretch rect sized to the button.
                    foreach (Transform child in btn.GlowRoot.transform)
                    {
                        var crt = child as RectTransform;
                        if (crt == null) continue;
                        crt.anchorMin = new Vector2(0.5f, 0.5f);
                        crt.anchorMax = new Vector2(0.5f, 0.5f);
                        crt.pivot = new Vector2(0.5f, 0.5f);
                        crt.sizeDelta = new Vector2(r.width, r.height);
                    }
                }
            }
        }

        private void SetHover(MenuButton btn, bool hovered)
        {
            if (btn?.Transform == null)
                return;

            // MonoGame: hovered draw bounds move up 2 UI px.
            btn.Transform.anchoredPosition = btn.RestPosition + (hovered ? new Vector2(0f, 2f) : Vector2.zero);

            if (btn.GlowRoot != null)
                btn.GlowRoot.SetActive(hovered && btn.Texture != null);
        }

        // ------------------------------------------------------------------
        // Actions (screens beyond the main menu come in later versions).
        // ------------------------------------------------------------------

        private void OnSingleplayer()
        {
            Debug.Log("Main menu: Singleplayer (screen not implemented yet).");
        }

        private void OnMultiplayer()
        {
            Debug.Log("Main menu: Multiplayer (screen not implemented yet).");
        }

        private bool _profileOpen;

        private void OnProfile()
        {
            if (_profileOpen)
                return;
            _profileOpen = true;
            // Ensure notification toasts have a coroutine runner.
            NotificationFeed.SetRunner(this);
            NotificationFeed.EnsureHost();
            ProfileScreenBuilder.Open(_canvas, () => _profileOpen = false);
        }

        private bool _screenshotsOpen;

        private void OnScreenshots()
        {
            if (_screenshotsOpen)
                return;
            _screenshotsOpen = true;
            ScreenshotService.SetScreenContext("screenshots");
            ScreenshotsScreenBuilder.Open(_canvas, () => _screenshotsOpen = false);
        }

        private void CloseOwnScreenshots()
        {
            // Callback path: screens close themselves; nothing extra here.
        }

        private void OnQuit()
        {
            RequestQuit();
        }

        /// <summary>MonoGame RequestQuit: write exit.request so a keep-open launcher can react, then exit.</summary>
        private void RequestQuit()
        {
            try
            {
                var path = Paths.GetExitRequestPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Main menu: failed to write exit.request: " + ex.Message);
            }

            Debug.Log("Main menu: Quit requested.");

            if (Application.isPlaying)
                Application.Quit();
#if UNITY_EDITOR
            else
                UnityEditor.EditorApplication.isPlaying = false;
#endif
        }
    }
}
