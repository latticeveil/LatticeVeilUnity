using System;
using System.Collections.Generic;
using LatticeVeil.Core;
using UnityEngine;
using UnityEngine.UI;

namespace LatticeVeil.Game
{
    /// <summary>
    /// Unity Options screen (uGUI). Structure: Settings.png background,
    /// Settings_GUI.png framed panel (smaller than the window), texture tabs,
    /// Back/Apply texture buttons. The page content is STANDARD Unity
    /// components (Toggle / Slider / Dropdown) laid out by
    /// OptionsScreenBuilder; this controller owns tabs, settings state,
    /// APPLY persistence and live application through SettingsBehavior.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OptionsScreenController : MonoBehaviour
    {
        private enum Tab { Video, Audio, Controls, Packs }

        private static readonly Color PanelDim = new Color(0f, 0f, 0f, 0.55f);

        private static readonly (int w, int h)[] Resolutions =
        {
            (1280, 720), (1920, 1080), (2560, 1440), (3840, 2160)
        };
        // GUI scale applies to the WHOLE UI (buttons + layout together) via the
        // shared CanvasScaler, so corner-pinned buttons scale in place.
        private static readonly (float value, string label)[] GuiScales =
        {
            (0.9f, "SMALL"), (1.0f, "STANDARD"), (1.25f, "LARGER")
        };
        private static readonly string[] Qualities = { "LOW", "MEDIUM", "HIGH", "ULTRA" };

        private Canvas _canvas;
        private RectTransform _screenRoot;
        private RectTransform _panelRoot;
        private RectTransform _videoRoot;
        private RectTransform _audioRoot;

        private Texture2D _bgTexture;
        private Texture2D _panelTexture;
        private Texture2D _backTexture;
        private Texture2D _applyTexture;

        private readonly Dictionary<Tab, GameObject> _tabButtons = new Dictionary<Tab, GameObject>();
        private readonly Dictionary<Tab, Texture2D> _tabTextures = new Dictionary<Tab, Texture2D>();
        private readonly Dictionary<Tab, Texture2D> _tabSelectedTextures = new Dictionary<Tab, Texture2D>();
        private readonly Dictionary<Tab, RectTransform> _tabPages = new Dictionary<Tab, RectTransform>();

        private Tab _tab = Tab.Video;

        // Standard components (Video).
        private Toggle _fsToggle;
        private Toggle _vsToggle;
        private Dropdown _resDropdown;
        private Dropdown _guiDropdown;
        private Dropdown _qualityDropdown;
        private Slider _brightness;
        private Slider _fov;
        private Slider _renderDistance;

        // Standard components (Audio).
        private Slider _master;
        private Slider _music;
        private Slider _sfx;

        private GameSettings _settings;

        private Action _onClosed;
        private bool _closed;

        public static OptionsScreenController Open(Canvas canvas, Action onClosed)
        {
            var go = new GameObject("OptionsScreen", typeof(OptionsScreenController));
            go.transform.SetParent(canvas.transform, false);
            var controller = go.GetComponent<OptionsScreenController>();
            controller._onClosed = onClosed;
            controller.Build();
            return controller;
        }

        private void Build()
        {
            _settings = GameSettings.LoadOrCreate(null);

            _canvas = GetComponentInParent<Canvas>();

            _bgTexture = GameAssetLoader.LoadTexture("textures/menu/backgrounds/Settings.png");
            _panelTexture = GameAssetLoader.LoadTexture("textures/menu/GUIS/Settings_GUI.png");
            _backTexture = GameAssetLoader.LoadTexture("textures/menu/buttons/Back.png");
            _applyTexture = GameAssetLoader.LoadTexture("textures/menu/buttons/Apply.png");

            BuildScreenRoot();
            BuildTabs();
            BuildPages();
            BuildFooter();
            SelectTab(Tab.Video);
            Layout();

            Debug.Log("Options screen opened.");
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
                Close();

            var rect = _canvas.GetComponent<RectTransform>().rect;
            if (Math.Abs(rect.width - _lastW) > 0.5f || Math.Abs(rect.height - _lastH) > 0.5f)
                Layout();
        }

        private float _lastW;
        private float _lastH;

        // ------------------------------------------------------------------
        // Structure
        // ------------------------------------------------------------------

        private void BuildScreenRoot()
        {
            var rootGo = new GameObject("ScreenRoot", typeof(RectTransform));
            _screenRoot = (RectTransform)rootGo.transform;
            _screenRoot.SetParent(_canvas.transform, false);
            Stretch(_screenRoot);

            AddStretchedImage(_screenRoot, _bgTexture, Color.black, swallow: true);
            AddStretchedImage(_screenRoot, null, PanelDim, swallow: true);

            var panelGo = new GameObject("PanelRoot", typeof(RectTransform), typeof(CanvasRenderer));
            _panelRoot = (RectTransform)panelGo.transform;
            _panelRoot.SetParent(_screenRoot, false);
            _panelRoot.anchorMin = new Vector2(0.5f, 0.5f);
            _panelRoot.anchorMax = new Vector2(0.5f, 0.5f);
            _panelRoot.pivot = new Vector2(0.5f, 0.5f);
            if (_panelTexture != null)
            {
                var raw = panelGo.AddComponent<RawImage>();
                raw.texture = _panelTexture;
                raw.raycastTarget = true;
            }
            else
            {
                var img = panelGo.AddComponent<Image>();
                img.color = new Color(0.12f, 0.12f, 0.14f, 0.96f);
                img.raycastTarget = true;
            }
        }

        private void BuildTabs()
        {
            string[] paths =
            {
                "textures/menu/buttons/Video.png",
                "textures/menu/buttons/Audio.png",
                "textures/menu/buttons/Controls.png",
                "textures/menu/buttons/packs.png"
            };
            string[] selectedPaths =
            {
                "textures/menu/buttons/VideoSelected.png",
                "textures/menu/buttons/AudioSelected.png",
                "textures/menu/buttons/ControlsSelected.png",
                "textures/menu/buttons/PacksSelected.png"
            };
            var tabs = new[] { Tab.Video, Tab.Audio, Tab.Controls, Tab.Packs };
            var labels = new[] { "VIDEO", "AUDIO", "CONTROLS", "PACKS" };

            for (int i = 0; i < tabs.Length; i++)
            {
                var tab = tabs[i];
                _tabTextures[tab] = GameAssetLoader.LoadTexture(paths[i]);
                _tabSelectedTextures[tab] = GameAssetLoader.LoadTexture(selectedPaths[i]);
                var captured = tab;
                var go = CreateTextureButton("Tab_" + labels[i], _tabTextures[tab], labels[i], () => SelectTab(captured));
                go.transform.SetParent(_screenRoot, false);
                _tabButtons[tab] = go;
            }
        }

        private void BuildPages()
        {
            foreach (Tab tab in (Tab[])Enum.GetValues(typeof(Tab)))
            {
                var pageGo = new GameObject("Page_" + tab, typeof(RectTransform), typeof(RectMask2D));
                var page = (RectTransform)pageGo.transform;
                page.SetParent(_panelRoot, false);
                Stretch(page);
                _tabPages[tab] = page;
            }

            // Video + Audio: standard uGUI widgets from the builder.
            _videoRoot = (RectTransform)OptionsScreenBuilder.BuildPage(_tabPages[Tab.Video], this).transform;
            _audioRoot = OptionsScreenBuilder.BuildAudioPage(_tabPages[Tab.Audio], this,
                out _master, out _music, out _sfx);

            _master.SetValueWithoutNotify(Mathf.Clamp01(_settings.MasterVolume));
            _music.SetValueWithoutNotify(Mathf.Clamp01(_settings.MusicVolume));
            _sfx.SetValueWithoutNotify(Mathf.Clamp01(_settings.SfxVolume));
            _master.GetComponent<OptionsScreenBuilder.SliderValueText>().Refresh();
            _music.GetComponent<OptionsScreenBuilder.SliderValueText>().Refresh();
            _sfx.GetComponent<OptionsScreenBuilder.SliderValueText>().Refresh();

            BuildNotePage(_tabPages[Tab.Controls], "CONTROLS", "Keybind remapping arrives with the controls update.\nCurrent binds live in options.lvc.");
            BuildNotePage(_tabPages[Tab.Packs], "PACKS & ADDONS", "Packs & mods management arrives with the packs update.\nDrop content packs into Documents/LatticeVeil/Packs.");
        }

        private void BuildNotePage(RectTransform page, string title, string text)
        {
            // Inside the frame's inner area (panel-local units, y from center).
            var t = CreateLabel(page, title, 20, Color.white);
            Anchor((RectTransform)t.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-300f, 150f));
            var body = CreateLabel(page, text, 17, new Color(0.86f, 0.89f, 0.92f, 1f));
            var bodyRect = (RectTransform)body.transform;
            bodyRect.anchorMin = new Vector2(0.5f, 0.5f);
            bodyRect.anchorMax = new Vector2(0.5f, 0.5f);
            bodyRect.pivot = new Vector2(0f, 0.5f);
            bodyRect.anchoredPosition = new Vector2(-300f, 118f);
        }

        private void BuildFooter()
        {
            var back = CreateTextureButton("Btn_Back", _backTexture, "BACK", Close);
            back.transform.SetParent(_screenRoot, false);

            var apply = CreateTextureButton("Btn_APPLY", _applyTexture, "APPLY", Apply);
            apply.transform.SetParent(_panelRoot, false);
        }

        // ------------------------------------------------------------------
        // Widget binding (called by the builder)
        // ------------------------------------------------------------------

        public void BindStandardWidgets(Toggle fs, Toggle vs, Dropdown res, Dropdown gui,
            Dropdown quality, Slider brightness, Slider fov, Slider renderDistance)
        {
            _fsToggle = fs;
            _vsToggle = vs;
            _resDropdown = res;
            _guiDropdown = gui;
            _qualityDropdown = quality;
            _brightness = brightness;
            _fov = fov;
            _renderDistance = renderDistance;

            _fsToggle.SetIsOnWithoutNotify(_settings.Fullscreen);
            _vsToggle.SetIsOnWithoutNotify(_settings.VSync);

            int resIdx = 0;
            for (int i = 0; i < Resolutions.Length; i++)
                if (Resolutions[i].w == _settings.ResolutionWidth && Resolutions[i].h == _settings.ResolutionHeight)
                    resIdx = i;
            _resDropdown.SetValueWithoutNotify(resIdx);

            int guiIdx = 0;
            for (int i = 0; i < GuiScales.Length; i++)
                if (Mathf.Approximately(GuiScales[i].value, _settings.GuiScale))
                    guiIdx = i;
            _guiDropdown.SetValueWithoutNotify(guiIdx);

            var normalized = string.IsNullOrWhiteSpace(_settings.QualityPreset) ? "MEDIUM" : _settings.QualityPreset.Trim().ToUpperInvariant();
            int qIdx = Array.IndexOf(Qualities, normalized);
            if (qIdx < 0) qIdx = 1;
            _qualityDropdown.SetValueWithoutNotify(qIdx);

            _brightness.SetValueWithoutNotify(Mathf.Clamp01(Mathf.InverseLerp(0.5f, 1.5f, _settings.Brightness)));
            _fov.SetValueWithoutNotify(Mathf.InverseLerp(60f, 110f, _settings.FieldOfView));
            _renderDistance.SetValueWithoutNotify(Mathf.InverseLerp(4f, 16f, _settings.RenderDistanceChunks));
        }

        // ------------------------------------------------------------------
        // Layout — smaller panel, tabs above, content inside the frame
        // ------------------------------------------------------------------

        public void Layout()
        {
            if (_canvas == null || _screenRoot == null)
                return;

            var canvasRect = _canvas.GetComponent<RectTransform>().rect;
            float w = canvasRect.width;
            float h = canvasRect.height;
            if (w <= 0f || h <= 0f)
                return;
            _lastW = w;
            _lastH = h;

            // Smaller GUI box than the window, 16:9 to match the frame art.
            float panelW = Mathf.Min(OptionsScreenBuilder.PanelWidth, w - 70f);
            float maxH = h - 170f; // leave room for tabs above + BACK below
            panelW = Mathf.Min(panelW, maxH * OptionsScreenBuilder.PanelWidth / OptionsScreenBuilder.PanelHeight);
            float panelH = panelW * OptionsScreenBuilder.PanelHeight / OptionsScreenBuilder.PanelWidth;
            _panelRoot.sizeDelta = new Vector2(panelW, panelH);
            _panelRoot.anchoredPosition = new Vector2(0f, -12f); // a little lower

            var layoutScale = Mathf.Clamp(Mathf.Min(panelW / OptionsScreenBuilder.PanelWidth, panelH / OptionsScreenBuilder.PanelHeight), 0.7f, 1f);

            // Tabs above the panel.
            float tabHeight = Mathf.Max(40f, Mathf.Min(64f, panelW * 0.09f) * layoutScale);
            float tabGap = Mathf.Max(6f, 8f * layoutScale);
            float totalTabsW = 0f;
            var tabOrder = new[] { Tab.Video, Tab.Audio, Tab.Controls, Tab.Packs };
            var tabAspects = new Dictionary<Tab, float>();
            foreach (var tab in tabOrder)
            {
                var tex = _tabTextures[tab];
                tabAspects[tab] = tex != null && tex.height > 0 ? tex.width / (float)tex.height : 3.25f;
                totalTabsW += tabHeight * tabAspects[tab];
            }
            totalTabsW += tabGap * (tabOrder.Length - 1);
            float tabX = -totalTabsW / 2f;
            float tabY = panelH / 2f + Mathf.Max(18f, 30f * layoutScale) - tabHeight / 2f;
            foreach (var tab in tabOrder)
            {
                var rect = (RectTransform)_tabButtons[tab].transform;
                float tabW = tabHeight * tabAspects[tab];
                rect.sizeDelta = new Vector2(tabW, tabHeight);
                rect.anchoredPosition = new Vector2(tabX + tabW / 2f, tabY);
                UpdateTabVisual(tab);
                tabX += tabW + tabGap;
            }

            // BACK bottom-left; APPLY bottom-center of panel.
            var back = _screenRoot.Find("Btn_Back") as RectTransform;
            if (back != null)
            {
                float aspect = _backTexture != null && _backTexture.height > 0 ? _backTexture.width / (float)_backTexture.height : 3.6f;
                float bh = Mathf.Max(44f, 56f * layoutScale);
                back.sizeDelta = new Vector2(bh * aspect, bh);
                back.anchorMin = new Vector2(0f, 0f);
                back.anchorMax = new Vector2(0f, 0f);
                back.pivot = new Vector2(0f, 0f);
                back.anchoredPosition = new Vector2(Mathf.Max(10f, 12f * layoutScale), Mathf.Max(10f, 12f * layoutScale));
            }

            var apply = _panelRoot.Find("Btn_APPLY") as RectTransform;
            if (apply != null)
            {
                float aspect = _applyTexture != null && _applyTexture.height > 0 ? _applyTexture.width / (float)_applyTexture.height : 5f;
                float ah = Mathf.Max(32f, 40f * layoutScale);
                float aw = Mathf.Min(ah * aspect, panelW * 0.35f);
                apply.sizeDelta = new Vector2(aw, ah);
                apply.anchorMin = new Vector2(0.5f, 0f);
                apply.anchorMax = new Vector2(0.5f, 0f);
                apply.pivot = new Vector2(0.5f, 0f);
                apply.anchoredPosition = new Vector2(0, 2f); // tight to the frame's bottom edge
            }
        }

        // ------------------------------------------------------------------
        // Shared UI helpers
        // ------------------------------------------------------------------

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void Anchor(RectTransform rect, Vector2 anchor, Vector2 anchorMax, Vector2 pos)
        {
            rect.anchorMin = anchor;
            rect.anchorMax = anchorMax;
            rect.pivot = anchor;
            rect.anchoredPosition = pos;
        }

        private Graphic AddStretchedImage(RectTransform parent, Texture2D texture, Color color, bool swallow)
        {
            var go = new GameObject(texture != null ? "ImgTex" : "ImgColor", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            Stretch(rect);

            if (texture != null)
            {
                var raw = go.AddComponent<RawImage>();
                raw.texture = texture;
                raw.raycastTarget = swallow;
                return raw;
            }

            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = swallow;
            return img;
        }

        private Text CreateLabel(RectTransform parent, string content, int size, Color color)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.font = OptionsScreenBuilderFont();
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAnchor.MiddleLeft;
            text.text = content;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            return text;
        }

        private static Font OptionsScreenBuilderFont()
        {
            // Same built-in font the builder uses.
            return OptionsScreenBuilderFontImpl();
        }

        private static Font _fontCache;
        private static Font OptionsScreenBuilderFontImpl()
        {
            if (_fontCache == null)
                _fontCache = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return _fontCache;
        }

        private GameObject CreateTextureButton(string name, Texture2D texture, string fallbackLabel, Action onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);

            Graphic target;
            if (texture != null)
            {
                var raw = go.AddComponent<RawImage>();
                raw.texture = texture;
                target = raw;
            }
            else
            {
                var img = go.AddComponent<Image>();
                img.color = new Color(0.16f, 0.16f, 0.18f, 0.95f);
                target = img;
                var label = CreateLabel(rect, fallbackLabel, 22, Color.white);
                Stretch((RectTransform)label.transform);
                label.alignment = TextAnchor.MiddleCenter;
            }

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = target;
            btn.transition = Selectable.Transition.ColorTint;
            btn.onClick.AddListener(() => onClick());
            return go;
        }

        private void UpdateTabVisual(Tab tab)
        {
            var raw = _tabButtons[tab].GetComponent<RawImage>();
            if (raw == null)
                return;
            raw.texture = _tab == tab && _tabSelectedTextures[tab] != null
                ? _tabSelectedTextures[tab]
                : _tabTextures[tab];
        }

        private void SelectTab(Tab tab)
        {
            _tab = tab;
            foreach (var pair in _tabButtons)
                UpdateTabVisual(pair.Key);
            foreach (var pair in _tabPages)
                pair.Value.gameObject.SetActive(pair.Key == tab);
        }

        // ------------------------------------------------------------------
        // Apply / close
        // ------------------------------------------------------------------

        private void Apply()
        {
            _settings.Fullscreen = _fsToggle.isOn;
            _settings.VSync = _vsToggle.isOn;

            int resIdx = _resDropdown.value;
            if (resIdx >= 0 && resIdx < Resolutions.Length)
            {
                _settings.ResolutionWidth = Resolutions[resIdx].w;
                _settings.ResolutionHeight = Resolutions[resIdx].h;
            }

            _settings.GuiScale = Mathf.Clamp(GuiScales[Mathf.Clamp(_guiDropdown.value, 0, GuiScales.Length - 1)].value, 0.9f, 1.25f);
            _settings.QualityPreset = Qualities[Mathf.Clamp(_qualityDropdown.value, 0, Qualities.Length - 1)];
            _settings.Brightness = Mathf.Lerp(0.5f, 1.5f, Mathf.Clamp01(_brightness.value));
            _settings.FieldOfView = Mathf.RoundToInt(Mathf.Lerp(60f, 110f, Mathf.Clamp01(_fov.value)));
            _settings.RenderDistanceChunks = Mathf.RoundToInt(Mathf.Lerp(4f, 16f, Mathf.Clamp01(_renderDistance.value)));
            _settings.MasterVolume = Mathf.Clamp01(_master.value);
            _settings.MusicVolume = Mathf.Clamp01(_music.value);
            _settings.SfxVolume = Mathf.Clamp01(_sfx.value);

            _settings.Save(null); // same options.lvc format, unchanged
            Debug.Log("Options applied and saved to options.lvc.");

            // Close FIRST so the window/mode change happens outside the UI
            // click handler; SettingsBehavior defers + retries the resize and
            // temporarily re-enables the window resize style, so the player
            // never presents into a stale swap chain (white screen).
            Close();
            SettingsBehavior.Apply(_settings);
        }

        private void Close()
        {
            if (_closed)
                return;
            _closed = true;

            Debug.Log("Options screen closed.");
            if (_screenRoot != null)
                Destroy(_screenRoot.gameObject);
            Destroy(gameObject);
            _onClosed?.Invoke();
        }
    }
}
