using System;
using System.Collections.Generic;
using System.IO;
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

        private void Awake()
        {
            BuildUi();
        }

        private void Update()
        {
            if (Math.Abs(Screen.width - _lastWindowW) > 0.5f || Math.Abs(Screen.height - _lastWindowH) > 0.5f)
                ApplyLayout();

            if (Input.GetKeyDown(KeyCode.Escape))
                RequestQuit();
        }

        private static Material GlowMaterial
        {
            get
            {
                if (_glowMaterial == null)
                {
                    var shader = Shader.Find("LatticeVeil/UIAdditiveGlow");
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
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

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

        /// <summary>Recomputes the virtual viewport and positions every button (MonoGame OnResize).</summary>
        private void ApplyLayout()
        {
            if (_canvas == null || _menuRoot == null)
                return;

            _lastWindowW = Screen.width;
            _lastWindowH = Screen.height;

            var virtualSize = MainMenuLayout.ViewportSize(_lastWindowW, _lastWindowH);
            var scale = MainMenuLayout.ReferenceScale(_lastWindowW, _lastWindowH);

            _menuRoot.sizeDelta = virtualSize;
            _menuRoot.anchoredPosition = Vector2.zero;
            _menuRoot.localScale = new Vector3(scale, scale, 1f);

            var rects = MainMenuLayout.Create(virtualSize.x, virtualSize.y);
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
                // Convert MonoGame top-left rect to centered anchored position.
                var center = new Vector2(r.x + r.width / 2f, r.y + r.height / 2f);
                var uiCenter = new Vector2(center.x, virtualSize.y - center.y); // Unity y-up
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

        private void OnOptions()
        {
            Debug.Log("Main menu: Options (screen not implemented yet).");
        }

        private void OnProfile()
        {
            Debug.Log("Main menu: Profile (screen not implemented yet).");
        }

        private void OnScreenshots()
        {
            Debug.Log("Main menu: Screenshots (screen not implemented yet).");
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
                var path = Path.Combine(Paths.RootDir, "exit.request");
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
