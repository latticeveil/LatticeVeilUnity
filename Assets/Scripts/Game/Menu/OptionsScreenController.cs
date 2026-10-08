using System;
using System.Collections.Generic;
using LatticeVeil.Core;
using UnityEngine;
using UnityEngine.UI;

namespace LatticeVeil.Game
{
    /// <summary>
    /// Unity rebuild of the MonoGame OptionsScreen (SettingsScreen.cs), uGUI
    /// edition. Visual parity with the MonoGame version:
    ///  - Settings.png background, Settings_GUI.png panel (1248x680 max,
    ///    centered — same caps and centering as MonoGame OnResize).
    ///  - VIDEO / AUDIO / CONTROLS / PACKS tab buttons (texture buttons with
    ///    *Selected.png variants for the active tab) in a row ABOVE the panel.
    ///  - BACK texture button bottom-left, APPLY bottom-center of the panel.
    ///  - Two-column content layout per MonoGame: checkboxes, dropdowns
    ///    (resolution, GUI scale, quality), sliders (brightness, FOV, render
    ///    distance, mouse sensitivity).
    ///  - Working copy: edits apply only on APPLY (MonoGame _working copy);
    ///    BACK discards. Settings persist to options.lvc via GameSettings.
    /// Smoothness: hover/press color tweens, raycastTarget=false on all
    /// decorative graphics, point-filtered textures, no LayoutGroups (manual
    /// layout like MonoGame so nothing reflows mid-frame).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OptionsScreenController : MonoBehaviour
    {
        private enum Tab { Video, Audio, Controls, Packs }

        private static readonly Color PanelDim = new Color(0f, 0f, 0f, 0.55f);
        private static readonly Color RowHighlight = new Color(1f, 1f, 1f, 0.08f);
        private static readonly Color HoverTint = new Color(1.15f, 1.15f, 1.15f, 1f);
        private static readonly Color ControlBg = new Color(0f, 0f, 0f, 0.45f);
        private static readonly Color ControlFill = new Color(0.55f, 0.8f, 1f, 0.9f);
        private static readonly Color TextColor = Color.white;
        private static readonly Color LabelColor = new Color(0.86f, 0.89f, 0.92f, 1f);

        private static readonly (float value, string label)[] GuiScaleOptions =
        {
            (1.0f, "STANDARD"), (1.5f, "LARGE"), (2.0f, "LARGER")
        };
        private static readonly string[] QualityOptions = { "LOW", "MEDIUM", "HIGH", "ULTRA" };

        private Canvas _canvas;
        private RectTransform _screenRoot;   // full-screen root (background + dim)
        private RectTransform _panelRoot;    // centered panel root
        private Texture2D _bgTexture;
        private Texture2D _panelTexture;
        private Texture2D _backTexture;

        private readonly Dictionary<Tab, GameObject> _tabButtons = new Dictionary<Tab, GameObject>();
        private readonly Dictionary<Tab, Texture2D> _tabTextures = new Dictionary<Tab, Texture2D>();
        private readonly Dictionary<Tab, Texture2D> _tabSelectedTextures = new Dictionary<Tab, Texture2D>();
        private readonly Dictionary<Tab, RectTransform> _tabPages = new Dictionary<Tab, RectTransform>();

        private Tab _tab = Tab.Video;

        // Working copy (applied only on APPLY, like MonoGame's _working).
        private GameSettings _settings;
        private GameSettings _working;

        // Video widgets (state kept here; rebuilt per tab selection).
        private bool _fullscreen;
        private bool _vsync;
        private int _resolutionIndex = -1;
        private int _guiScaleIndex = 1;
        private int _qualityIndex = 1;
        private float _brightness = 1f;
        private int _fov = 70;
        private int _renderDistance = 16;

        private static readonly (int w, int h)[] ResolutionOptions =
        {
            (1280, 720), (1920, 1080), (2560, 1440), (3840, 2160)
        };

        private Action _onClosed;

        /// <summary>
        /// Opens the options screen over the given canvas. Returns the root
        /// GameObject (caller keeps it until closed).
        /// </summary>
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
            _working = CloneSettings(_settings);

            // Load current values into the editable state.
            _fullscreen = _working.Fullscreen;
            _vsync = _working.VSync;
            _guiScaleIndex = FindGuiScaleIndex(_working.GuiScale);
            _qualityIndex = Array.IndexOf(QualityOptions, NormalizeQuality(_working.QualityPreset));
            if (_qualityIndex < 0) _qualityIndex = 1;
            _brightness = _working.Brightness;
            _fov = _working.FieldOfView;
            _renderDistance = _working.RenderDistanceChunks;
            for (int i = 0; i < ResolutionOptions.Length; i++)
            {
                if (ResolutionOptions[i].w == _working.ResolutionWidth && ResolutionOptions[i].h == _working.ResolutionHeight)
                {
                    _resolutionIndex = i;
                    break;
                }
            }

            _canvas = GetComponentInParent<Canvas>();

            _bgTexture = GameAssetLoader.LoadTexture("textures/menu/backgrounds/Settings.png");
            _panelTexture = GameAssetLoader.LoadTexture("textures/menu/GUIS/Settings_GUI.png");
            _backTexture = GameAssetLoader.LoadTexture("textures/menu/buttons/Back.png");

            BuildScreenRoot();
            BuildTabs();
            BuildPages();
            BuildFooter();
            SelectTab(Tab.Video);
            Layout();

            Debug.Log("Options screen opened.");
        }

        // ------------------------------------------------------------------
        // Structure
        // ------------------------------------------------------------------

        private void BuildScreenRoot()
        {
            var rootGo = new GameObject("ScreenRoot", typeof(RectTransform));
            _screenRoot = (RectTransform)rootGo.transform;
            _screenRoot.SetParent(_canvas.transform, false);
            _screenRoot.anchorMin = Vector2.zero;
            _screenRoot.anchorMax = Vector2.one;
            _screenRoot.offsetMin = Vector2.zero;
            _screenRoot.offsetMax = Vector2.zero;

            // Background (Settings.png stretched like MonoGame; black fallback).
            var bgGo = new GameObject("Background", typeof(RectTransform), typeof(CanvasRenderer));
            bgGo.transform.SetParent(_screenRoot, false);
            var bgRect = (RectTransform)bgGo.transform;
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;
            if (_bgTexture != null)
            {
                var raw = bgGo.AddComponent<RawImage>();
                raw.texture = _bgTexture;
                raw.raycastTarget = true; // swallow clicks under the panel
            }
            else
            {
                var img = bgGo.AddComponent<Image>();
                img.color = Color.black;
                img.raycastTarget = true;
            }

            // Dim between background and panel so the panel reads clearly
            // (small visual upgrade over MonoGame, which drew the panel raw).
            var dimGo = new GameObject("Dim", typeof(RectTransform));
            dimGo.transform.SetParent(_screenRoot, false);
            var dimRect = (RectTransform)dimGo.transform;
            dimRect.anchorMin = Vector2.zero;
            dimRect.anchorMax = Vector2.one;
            dimRect.offsetMin = Vector2.zero;
            dimRect.offsetMax = Vector2.zero;
            var dim = dimGo.AddComponent<Image>();
            dim.color = PanelDim;
            dim.raycastTarget = true;

            // Centered panel root — actual size set in Layout().
            var panelGo = new GameObject("PanelRoot", typeof(RectTransform));
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
                "textures/menu/buttons/packsSelected.png"
            };
            var tabs = new[] { Tab.Video, Tab.Audio, Tab.Controls, Tab.Packs };
            var labels = new[] { "VIDEO", "AUDIO", "CONTROLS", "PACKS" };

            for (int i = 0; i < tabs.Length; i++)
            {
                _tabTextures[tabs[i]] = GameAssetLoader.LoadTexture(paths[i]);
                _tabSelectedTextures[tabs[i]] = GameAssetLoader.LoadTexture(selectedPaths[i]);

                var tab = tabs[i];
                var go = CreateTexturedButton(
                    "Tab_" + labels[i],
                    _tabTextures[tabs[i]],
                    labels[i],
                    () => SelectTab(tab));
                go.transform.SetParent(_screenRoot, false);
                _tabButtons[tabs[i]] = go;
            }
        }

        private void BuildPages()
        {
            foreach (Tab tab in (Tab[])Enum.GetValues(typeof(Tab)))
            {
                var pageGo = new GameObject("Page_" + tab, typeof(RectTransform));
                var page = (RectTransform)pageGo.transform;
                page.SetParent(_panelRoot, false);
                page.anchorMin = Vector2.zero;
                page.anchorMax = Vector2.one;
                page.offsetMin = Vector2.zero;
                page.offsetMax = Vector2.zero;
                _tabPages[tab] = page;
            }

            BuildVideoPage(_tabPages[Tab.Video]);
            BuildAudioPage(_tabPages[Tab.Audio]);
            BuildControlsPage(_tabPages[Tab.Controls]);
            BuildPacksPage(_tabPages[Tab.Packs]);
        }

        private void BuildFooter()
        {
            // BACK texture button — bottom-left of the screen (MonoGame position).
            var back = CreateTexturedButton("Btn_Back", _backTexture, "BACK", Close);
            back.transform.SetParent(_screenRoot, false);

            // APPLY — bottom-center of the panel (text button like MonoGame's fallback).
            var apply = CreateTextButton("Btn_APPLY", "APPLY", Apply);
            apply.transform.SetParent(_panelRoot, false);
        }

        // ------------------------------------------------------------------
        // Pages (Video tab mirrors the MonoGame two-column layout)
        // ------------------------------------------------------------------

        private void BuildVideoPage(RectTransform page)
        {
            AddCheckbox(page, "FULLSCREEN", _fullscreen, v => _fullscreen = v, 0);
            AddCheckbox(page, "VSYNC", _vsync, v => _vsync = v, 1);
            AddDropdown(page, "RESOLUTION", () => _resolutionIndex, ResolutionLabel, i => _resolutionIndex = i, 2);
            AddDropdown(page, "GUI SCALE", () => _guiScaleIndex,
                i => GuiScaleOptions[Mathf.Clamp(i, 0, GuiScaleOptions.Length - 1)].label,
                i => _guiScaleIndex = i, 3);
            AddSlider(page, "BRIGHTNESS", _brightness, v => _brightness = v, 4);
            AddSlider(page, "FOV", (_fov - 60) / 50f, v => _fov = Mathf.RoundToInt(60f + v * 50f), 5,
                () => _fov.ToString());
            AddSlider(page, "RENDER DISTANCE", (_renderDistance - 4) / 12f, v => _renderDistance = Mathf.RoundToInt(4f + v * 12f), 6,
                () => _renderDistance + " CHUNKS");
            AddDropdown(page, "QUALITY", () => _qualityIndex, i => QualityOptions[i], i => _qualityIndex = i, 7);
        }

        private void BuildAudioPage(RectTransform page)
        {
            AddSlider(page, "MASTER", _working.MasterVolume, v => _working.MasterVolume = v, 0,
                () => Mathf.RoundToInt(_working.MasterVolume * 100) + "%");
            AddSlider(page, "MUSIC", _working.MusicVolume, v => _working.MusicVolume = v, 1,
                () => Mathf.RoundToInt(_working.MusicVolume * 100) + "%");
            AddSlider(page, "SFX", _working.SfxVolume, v => _working.SfxVolume = v, 2,
                () => Mathf.RoundToInt(_working.SfxVolume * 100) + "%");
        }

        private void BuildControlsPage(RectTransform page)
        {
            AddNote(page, "Keybinds arrive with the controls update.\nCurrent binds live in options.lvc.");
        }

        private void BuildPacksPage(RectTransform page)
        {
            AddNote(page, "Packs & mods management arrives with the packs update.\nDrop content packs into Documents/LatticeVeil/Packs.");
        }

        // ------------------------------------------------------------------
        // Widgets (manual layout — rows computed in Layout())
        // ------------------------------------------------------------------

        private sealed class WidgetRow
        {
            public RectTransform Root;
            public int Order;
            public string Kind; // "checkbox", "dropdown", "slider", "note"
            public string Label;
            public Func<int> GetIndex;
            public Action<int> SetIndex;
            public Func<int, string> IndexLabel;
            public Action<bool> SetBool;
            public float InitialValue;
            public Action<float> SetValue;
            public Func<string> ValueLabel;
        }

        private readonly List<WidgetRow> _rows = new List<WidgetRow>();

        private WidgetRow AddRow(RectTransform page, string kind, string label, int order)
        {
            var rowGo = new GameObject("Row_" + label, typeof(RectTransform));
            var row = (RectTransform)rowGo.transform;
            row.SetParent(page, false);
            row.anchorMin = new Vector2(0f, 1f);
            row.anchorMax = new Vector2(1f, 1f);
            row.pivot = new Vector2(0f, 1f);

            var widget = new WidgetRow { Root = row, Order = order, Kind = kind, Label = label };
            _rows.Add(widget);
            return widget;
        }

        private void AddCheckbox(RectTransform page, string label, bool initial, Action<bool> set, int order)
        {
            var row = AddRow(page, "checkbox", label, order);
            var rowRect = row.Root;
            var rowGo = rowRect.gameObject;

            // Checkbox square.
            var boxGo = new GameObject("Box", typeof(RectTransform), typeof(Image));
            var box = (RectTransform)boxGo.transform;
            box.SetParent(rowRect, false);
            box.anchorMin = new Vector2(0f, 0.5f);
            box.anchorMax = new Vector2(0f, 0.5f);
            box.pivot = new Vector2(0f, 0.5f);
            box.sizeDelta = new Vector2(26, 26);
            var boxImg = boxGo.GetComponent<Image>();
            boxImg.color = ControlBg;

            var checkGo = new GameObject("Check", typeof(RectTransform), typeof(Image));
            var check = (RectTransform)checkGo.transform;
            check.SetParent(box, false);
            check.anchorMin = Vector2.zero;
            check.anchorMax = Vector2.one;
            check.offsetMin = new Vector2(4, 4);
            check.offsetMax = new Vector2(-4, -4);
            var checkImg = checkGo.GetComponent<Image>();
            checkImg.color = initial ? ControlFill : Color.clear;
            checkImg.raycastTarget = false;

            var labelText = CreateLabel(rowRect, label.ToUpperInvariant(), 20, TextColor);
            ((RectTransform)labelText.transform).anchoredPosition = new Vector2(38, 0);
            ((RectTransform)labelText.transform).anchorMin = new Vector2(0f, 0.5f);
            ((RectTransform)labelText.transform).anchorMax = new Vector2(0f, 0.5f);

            // Click target = the whole row.
            var btn = rowGo.AddComponent<Button>();
            btn.transition = Selectable.Transition.ColorTint;
            btn.targetGraphic = boxImg;
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = HoverTint;
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f);
            colors.fadeDuration = 0.08f;
            btn.colors = colors;
            btn.onClick.AddListener(() =>
            {
                set(!(checkImg.color.a > 0.5f));
                checkImg.color = checkImg.color.a > 0.5f ? Color.clear : ControlFill;
                Debug.Log($"Option changed: {label} = {checkImg.color.a > 0.5f}");
            });

            // Re-apply saved value on rebuild.
            if (!initial)
                checkImg.color = Color.clear;
        }

        private void AddDropdown(RectTransform page, string label, Func<int> getIndex, Func<int, string> indexLabel, Action<int> setIndex, int order)
        {
            var row = AddRow(page, "dropdown", label, order);
            row.GetIndex = getIndex;
            row.SetIndex = setIndex;
            row.IndexLabel = indexLabel;

            var labelText = CreateLabel(row.Root, label.ToUpperInvariant(), 18, LabelColor);
            var labelRect = (RectTransform)labelText.transform;
            labelRect.anchorMin = new Vector2(0f, 1f);
            labelRect.anchorMax = new Vector2(0f, 1f);
            labelRect.pivot = new Vector2(0f, 1f);
            labelRect.anchoredPosition = new Vector2(0, -20);

            var valueText = CreateLabel(row.Root, indexLabel(getIndex()), 18, TextColor);

            var rowRef = row.Root.gameObject.AddComponent<WidgetRowRef>();
            rowRef.Store(valueText);

            // Prev / next cycle buttons around the value (simpler and smoother
            // than a popup list; same options as MonoGame's dropdowns).
            var prev = CreateTextButton("Prev", "<", null);
            prev.transform.SetParent(row.Root, false);
            var next = CreateTextButton("Next", ">", null);

            var prevBtn = prev.GetComponent<Button>();
            var nextBtn = next.GetComponent<Button>();

            prevBtn.onClick.AddListener(() =>
            {
                var count = OptionCount(label);
                var idx = Mathf.Max(0, getIndex() - 1);
                setIndex(idx);
                valueText.text = indexLabel(Mathf.Clamp(idx, 0, count - 1));
                Debug.Log($"Option changed: {label} = {valueText.text}");
            });
            nextBtn.onClick.AddListener(() =>
            {
                var count = OptionCount(label);
                var idx = Mathf.Min(count - 1, getIndex() + 1);
                setIndex(idx);
                valueText.text = indexLabel(Mathf.Clamp(idx, 0, count - 1));
                Debug.Log($"Option changed: {label} = {valueText.text}");
            });

            rowRef.Prev = prev.GetComponent<RectTransform>();
            rowRef.Next = next.GetComponent<RectTransform>();
        }

        private int OptionCount(string label)
        {
            switch (label)
            {
                case "RESOLUTION": return ResolutionOptions.Length;
                case "GUI SCALE": return GuiScaleOptions.Length;
                case "QUALITY": return QualityOptions.Length;
                default: return 1;
            }
        }

        private string ResolutionLabel(int i)
        {
            if (i < 0 || i >= ResolutionOptions.Length)
                return "CURRENT";
            return $"{ResolutionOptions[i].w} x {ResolutionOptions[i].h}";
        }

        private void AddSlider(RectTransform page, string label, float initial, Action<float> set, int order, Func<string> valueLabel = null)
        {
            var row = AddRow(page, "slider", label, order);
            var rowRect = row.Root;

            var labelText = CreateLabel(rowRect, label.ToUpperInvariant(), 18, LabelColor);
            var labelRect = (RectTransform)labelText.transform;
            labelRect.anchorMin = new Vector2(0f, 1f);
            labelRect.anchorMax = new Vector2(0f, 1f);
            labelRect.pivot = new Vector2(0f, 1f);
            labelRect.anchoredPosition = new Vector2(0, -20);

            var sliderGo = new GameObject("Slider", typeof(RectTransform), typeof(Slider));
            var sliderRect = (RectTransform)sliderGo.transform;
            sliderRect.SetParent(rowRect, false);
            sliderRect.anchorMin = new Vector2(0f, 1f);
            sliderRect.anchorMax = new Vector2(1f, 1f);
            sliderRect.pivot = new Vector2(0f, 0.5f);

            var slider = sliderGo.GetComponent<Slider>();
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = Mathf.Clamp01(initial);

            // Background track.
            var trackGo = new GameObject("Track", typeof(RectTransform), typeof(Image));
            var track = (RectTransform)trackGo.transform;
            track.SetParent(sliderRect, false);
            track.anchorMin = new Vector2(0f, 0.5f);
            track.anchorMax = new Vector2(1f, 0.5f);
            track.offsetMin = Vector2.zero;
            track.offsetMax = Vector2.zero;
            track.sizeDelta = new Vector2(0, 8);
            var trackImg = trackGo.GetComponent<Image>();
            trackImg.color = ControlBg;
            trackImg.raycastTarget = false;

            // Fill (stretches with the value — smooth drag feedback).
            var fillAreaGo = new GameObject("Fill Area", typeof(RectTransform));
            var fillArea = (RectTransform)fillAreaGo.transform;
            fillArea.SetParent(sliderRect, false);
            fillArea.anchorMin = new Vector2(0f, 0.5f);
            fillArea.anchorMax = new Vector2(1f, 0.5f);
            fillArea.offsetMin = Vector2.zero;
            fillArea.offsetMax = Vector2.zero;

            var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            var fill = (RectTransform)fillGo.transform;
            fill.SetParent(fillArea, false);
            fill.anchorMin = Vector2.zero;
            fill.anchorMax = Vector2.zero;
            fill.sizeDelta = new Vector2(0, 8);
            var fillImg = fillGo.GetComponent<Image>();
            fillImg.color = ControlFill;
            fillImg.raycastTarget = false;

            slider.fillRect = fill;

            // Handle.
            var handleAreaGo = new GameObject("Handle Area", typeof(RectTransform));
            var handleArea = (RectTransform)handleAreaGo.transform;
            handleArea.SetParent(sliderRect, false);
            handleArea.anchorMin = new Vector2(0f, 0.5f);
            handleArea.anchorMax = new Vector2(1f, 0.5f);
            handleArea.offsetMin = Vector2.zero;
            handleArea.offsetMax = Vector2.zero;

            var handleGo = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            var handle = (RectTransform)handleGo.transform;
            handle.SetParent(handleArea, false);
            handle.sizeDelta = new Vector2(14, 22);
            var handleImg = handleGo.GetComponent<Image>();
            handleImg.color = Color.white;

            slider.handleRect = handle;
            slider.targetGraphic = handleImg;
            slider.transition = Selectable.Transition.ColorTint;
            var colors = slider.colors;
            colors.fadeDuration = 0.08f;
            slider.colors = colors;

            // Value label on the right.
            var rowRef = rowRect.gameObject.AddComponent<WidgetRowRef>();
            if (valueLabel != null)
            {
                var valueText = CreateLabel(rowRect, valueLabel(), 16, TextColor);
                var valueRect = (RectTransform)valueText.transform;
                valueRect.anchorMin = new Vector2(1f, 0f);
                valueRect.anchorMax = new Vector2(1f, 0f);
                valueRect.pivot = new Vector2(1f, 0.5f);
                rowRef.Store(valueText);
                rowRef.ValueLabel = valueLabel;
            }
            rowRef.Slider = slider;

            slider.onValueChanged.AddListener(v =>
            {
                set(v);
                if (rowRef.ValueLabel != null)
                    rowRef.ValueText.text = rowRef.ValueLabel();
            });
        }

        private void AddNote(RectTransform page, string text)
        {
            var noteGo = new GameObject("Note", typeof(RectTransform));
            var note = (RectTransform)noteGo.transform;
            note.SetParent(page, false);
            note.anchorMin = new Vector2(0f, 1f);
            note.anchorMax = new Vector2(1f, 1f);
            note.pivot = new Vector2(0f, 1f);

            var noteText = CreateLabel(note, text, 18, LabelColor);
            var noteRect = (RectTransform)noteText.transform;
            noteRect.anchorMin = new Vector2(0f, 1f);
            noteRect.anchorMax = Vector2.one;
            noteRect.pivot = new Vector2(0f, 1f);
            noteRect.anchoredPosition = new Vector2(0, -14);

            _rows.Add(new WidgetRow { Root = note, Kind = "note", Order = 0 });
        }

        // ------------------------------------------------------------------
        // Layout (MonoGame OnResize math, UI-space)
        // ------------------------------------------------------------------

        public void Layout()
        {
            if (_canvas == null || _screenRoot == null)
                return;

            float w = _canvas.GetComponent<RectTransform>().rect.width;
            float h = _canvas.GetComponent<RectTransform>().rect.height;
            if (w <= 0f || h <= 0f)
                return;

            // Panel caps + centering straight from MonoGame OnResize.
            float panelW = Mathf.Min(1248f, w - 20f);
            float panelH = Mathf.Min(680f, h - 30f);
            _panelRoot.sizeDelta = new Vector2(panelW, panelH);

            var layoutScale = Mathf.Clamp(Mathf.Min(panelW / 1180f, panelH / 680f), 0.82f, 1f);

            // Tabs: row above the panel, grouped around the panel center.
            float tabHeight = Mathf.Max(44f, Mathf.Min(70f, panelW * 0.095f) * layoutScale);
            float tabGap = Mathf.Max(6f, 8f * layoutScale);
            var tabRects = new Dictionary<Tab, Rect>();
            var tabOrder = new[] { Tab.Video, Tab.Audio, Tab.Controls, Tab.Packs };
            float totalTabsW = 0f;
            foreach (var tab in tabOrder)
            {
                var tex = _tabTextures[tab];
                float aspect = tex != null && tex.height > 0 ? tex.width / (float)tex.height : 3.25f;
                float tabW = tabHeight * aspect;
                tabRects[tab] = new Rect(0, 0, tabW, tabHeight);
                totalTabsW += tabW;
            }
            totalTabsW += tabGap * (tabOrder.Length - 1);
            float tabX = -totalTabsW / 2f;
            float tabY = panelH / 2f + Mathf.Max(34f, 42f * layoutScale) - tabHeight;
            foreach (var tab in tabOrder)
            {
                var r = tabRects[tab];
                var go = _tabButtons[tab];
                var rect = (RectTransform)go.transform;
                rect.sizeDelta = new Vector2(r.width, r.height);
                rect.anchoredPosition = new Vector2(tabX + r.width / 2f, tabY + tabHeight / 2f);
                UpdateTabVisual(tab);
                tabX += r.width + tabGap;
            }

            // BACK: bottom-left; APPLY: bottom-center of the panel.
            var back = _screenRoot.Find("Btn_Back") as RectTransform;
            if (back != null)
            {
                float backAspect = _backTexture != null && _backTexture.height > 0
                    ? _backTexture.width / (float)_backTexture.height : 3.6f;
                float backH = Mathf.Max(48f, 62f * layoutScale);
                float backW = Mathf.Min(Mathf.Max(260f, 320f * layoutScale), backH * backAspect);
                back.sizeDelta = new Vector2(backW, backH);
                back.anchorMin = new Vector2(0f, 0f);
                back.anchorMax = new Vector2(0f, 0f);
                back.pivot = new Vector2(0f, 0f);
                back.anchoredPosition = new Vector2(Mathf.Max(10f, 12f * layoutScale), Mathf.Max(10f, 12f * layoutScale));
            }

            var apply = _panelRoot.Find("Btn_APPLY") as RectTransform;
            if (apply != null)
            {
                float applyH = Mathf.Max(36f, 42f * layoutScale);
                float applyW = Mathf.Max(220f, 280f * layoutScale);
                float frameInsetBottom = Mathf.Max(24f, 30f * layoutScale);
                apply.anchorMin = new Vector2(0.5f, 0f);
                apply.anchorMax = new Vector2(0.5f, 0f);
                apply.pivot = new Vector2(0.5f, 0f);
                apply.sizeDelta = new Vector2(applyW, applyH);
                apply.anchoredPosition = new Vector2(0, frameInsetBottom);
            }

            // Content rows: two columns inside the panel frame.
            float frameInsetX = Mathf.Max(44f, 104f * layoutScale);
            float contentTop = Mathf.Max(48f, 58f * layoutScale);
            float contentW = panelW - frameInsetX * 2f;
            float columnGap = Mathf.Max(20f, 28f * layoutScale);
            float columnW = (contentW - columnGap) / 2f;
            float rowGap = Mathf.Max(12f, 18f * layoutScale);
            float rowH = Mathf.Max(34f, 38f * layoutScale);

            foreach (var pagePair in _tabPages)
                LayoutPage(pagePair.Value, frameInsetX, contentTop, columnW, columnGap, rowGap, rowH);
        }

        private void LayoutPage(RectTransform page, float insetX, float contentTop, float columnW, float columnGap, float rowGap, float rowH)
        {
            // Split rows: even order -> left column, odd order -> right column
            // for the Video tab (MonoGame layout); others flow in one column.
            int slot = 0;
            foreach (Transform child in page)
            {
                var rect = child as RectTransform;
                if (rect == null) continue;

                var rowRef = child.GetComponent<WidgetRowRef>();
                var isNote = child.name == "Note";
                if (isNote)
                {
                    rect.anchoredPosition = new Vector2(insetX, -contentTop);
                    continue;
                }

                int col = slot % 2;
                int rowIdx = slot / 2;
                float x = insetX + col * (columnW + columnGap);
                float y = contentTop + rowIdx * (rowH + rowGap);
                rect.anchoredPosition = new Vector2(x, -y);

                // Size children of the row.
                if (rowRef != null)
                {
                    if (rowRef.Prev != null)
                    {
                        rowRef.Prev.sizeDelta = new Vector2(40, rowH);
                        rowRef.Prev.anchorMin = new Vector2(0f, 0f);
                        rowRef.Prev.anchorMax = new Vector2(0f, 0f);
                        rowRef.Prev.pivot = new Vector2(0f, 0f);
                        rowRef.Prev.anchoredPosition = new Vector2(columnW * 0.45f, 0);

                        rowRef.Next.sizeDelta = new Vector2(40, rowH);
                        rowRef.Next.anchorMin = new Vector2(1f, 0f);
                        rowRef.Next.anchorMax = new Vector2(1f, 0f);
                        rowRef.Next.pivot = new Vector2(1f, 0f);
                        rowRef.Next.anchoredPosition = new Vector2(0, 0);

                        var valueRect = rowRef.ValueRect;
                        if (valueRect != null)
                        {
                            valueRect.anchorMin = new Vector2(0f, 0f);
                            valueRect.anchorMax = new Vector2(0f, 0f);
                            valueRect.pivot = new Vector2(0.5f, 0.5f);
                            valueRect.sizeDelta = new Vector2(columnW * 0.35f, rowH);
                            valueRect.anchoredPosition = new Vector2(columnW * 0.625f, rowH / 2f);
                        }
                    }
                    else if (rowRef.Slider != null)
                    {
                        var sliderRect = (RectTransform)rowRef.Slider.transform;
                        sliderRect.anchorMin = new Vector2(0f, 0f);
                        sliderRect.anchorMax = new Vector2(1f, 0f);
                        sliderRect.pivot = new Vector2(0f, 0f);
                        sliderRect.offsetMin = new Vector2(0, 0);
                        sliderRect.offsetMax = new Vector2(0, 8);
                    }
                }

                slot++;
            }
        }

        /// <summary>RectTransforms created without WidgetRowRef get one lazily for layout.</summary>
        private Text CreateLabel(RectTransform parent, string content, int size, Color color)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.font = OptionsFont;
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAnchor.MiddleLeft;
            text.text = content;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            return text;
        }

        /// <summary>
        /// Legacy Text needs a font. Arial (the built-in dynamic default) is
        /// used when present so we never generate/ship a font — Unity provides it.
        /// </summary>
        private static Font _optionsFont;
        private static Font OptionsFont
        {
            get
            {
                if (_optionsFont == null)
                    _optionsFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                return _optionsFont;
            }
        }

        private GameObject CreateTexturedButton(string name, Texture2D texture, string fallbackLabel, Action onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);

            if (texture != null)
            {
                var raw = go.AddComponent<RawImage>();
                raw.texture = texture;
                var btn = go.AddComponent<Button>();
                btn.targetGraphic = raw;
                btn.transition = Selectable.Transition.ColorTint;
                var colors = btn.colors;
                colors.normalColor = Color.white;
                colors.highlightedColor = HoverTint;
                colors.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
                colors.fadeDuration = 0.08f;
                btn.colors = colors;
                btn.onClick.AddListener(() => onClick());
            }
            else
            {
                var img = go.AddComponent<Image>();
                img.color = new Color(0.16f, 0.16f, 0.18f, 0.95f);
                var btn = go.AddComponent<Button>();
                btn.targetGraphic = img;
                btn.transition = Selectable.Transition.ColorTint;
                btn.colors = new ColorBlock
                {
                    normalColor = Color.white,
                    highlightedColor = HoverTint,
                    pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f),
                    disabledColor = Color.gray,
                    colorMultiplier = 1f,
                    fadeDuration = 0.08f
                };
                var label = CreateLabel(rect, fallbackLabel, 22, TextColor);
                var labelRect = (RectTransform)label.transform;
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = Vector2.zero;
                labelRect.offsetMax = Vector2.zero;
                label.alignment = TextAnchor.MiddleCenter;
                btn.onClick.AddListener(() => onClick());
            }

            return go;
        }

        private GameObject CreateTextButton(string name, string label, Action onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            var rect = (RectTransform)go.transform;
            var img = go.GetComponent<Image>();
            img.color = ControlBg;
            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.transition = Selectable.Transition.ColorTint;
            btn.colors = new ColorBlock
            {
                normalColor = Color.white,
                highlightedColor = HoverTint,
                pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f),
                disabledColor = Color.gray,
                colorMultiplier = 1f,
                fadeDuration = 0.08f
            };
            if (onClick != null)
                btn.onClick.AddListener(() => onClick());

            var text = CreateLabel(rect, label, 18, TextColor);
            var textRect = (RectTransform)text.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            text.alignment = TextAnchor.MiddleCenter;
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
        // Apply / close (MonoGame Apply/ExitOptions semantics)
        // ------------------------------------------------------------------

        private void Apply()
        {
            _working.Fullscreen = _fullscreen;
            _working.VSync = _vsync;
            _working.GuiScale = GuiScaleOptions[Mathf.Clamp(_guiScaleIndex, 0, GuiScaleOptions.Length - 1)].value;
            _working.QualityPreset = QualityOptions[Mathf.Clamp(_qualityIndex, 0, QualityOptions.Length - 1)];
            _working.Brightness = _brightness;
            _working.FieldOfView = _fov;
            _working.RenderDistanceChunks = _renderDistance;
            if (_resolutionIndex >= 0 && _resolutionIndex < ResolutionOptions.Length)
            {
                _working.ResolutionWidth = ResolutionOptions[_resolutionIndex].w;
                _working.ResolutionHeight = ResolutionOptions[_resolutionIndex].h;
            }

            CopyInto(_working, _settings);
            _settings.Save(null);
            Debug.Log("Options applied and saved to options.lvc.");

            // Window mode/resolution now (fullscreen comes back as an option;
            // for now keep windowed to honor the non-borderless default).
            if (!_working.Fullscreen)
                Screen.SetResolution(_working.ResolutionWidth, _working.ResolutionHeight, FullScreenMode.Windowed);

            Close();
        }

        private bool _closed;

        private void Close()
        {
            if (_closed)
                return;
            _closed = true;

            Debug.Log("Options screen closed.");
            // The UI lives under _screenRoot (parented to the canvas, not to
            // this component), so tearing down the screen means destroying it.
            if (_screenRoot != null)
                Destroy(_screenRoot.gameObject);
            Destroy(gameObject);
            _onClosed?.Invoke();
        }

        // ------------------------------------------------------------------
        // Settings helpers
        // ------------------------------------------------------------------

        private static GameSettings CloneSettings(GameSettings source)
        {
            return new GameSettings
            {
                Fullscreen = source.Fullscreen,
                VSync = source.VSync,
                ResolutionWidth = source.ResolutionWidth,
                ResolutionHeight = source.ResolutionHeight,
                GuiScale = source.GuiScale,
                QualityPreset = source.QualityPreset,
                Brightness = source.Brightness,
                FieldOfView = source.FieldOfView,
                RenderDistanceChunks = source.RenderDistanceChunks,
                MasterVolume = source.MasterVolume,
                MusicVolume = source.MusicVolume,
                SfxVolume = source.SfxVolume
            };
        }

        private static void CopyInto(GameSettings from, GameSettings to)
        {
            to.Fullscreen = from.Fullscreen;
            to.VSync = from.VSync;
            to.ResolutionWidth = from.ResolutionWidth;
            to.ResolutionHeight = from.ResolutionHeight;
            to.GuiScale = from.GuiScale;
            to.QualityPreset = from.QualityPreset;
            to.Brightness = from.Brightness;
            to.FieldOfView = from.FieldOfView;
            to.RenderDistanceChunks = from.RenderDistanceChunks;
            to.MasterVolume = from.MasterVolume;
            to.MusicVolume = from.MusicVolume;
            to.SfxVolume = from.SfxVolume;
        }

        private static int FindGuiScaleIndex(float value)
        {
            for (int i = 0; i < GuiScaleOptions.Length; i++)
            {
                if (Mathf.Approximately(GuiScaleOptions[i].value, value))
                    return i;
            }
            return 1; // LARGE default
        }

        private static string NormalizeQuality(string value)
        {
            var quality = string.IsNullOrWhiteSpace(value) ? "MEDIUM" : value.Trim().ToUpperInvariant();
            return quality is "LOW" or "MEDIUM" or "HIGH" or "ULTRA" ? quality : "MEDIUM";
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
                Close();
        }

        /// <summary>Lazily attached per-row helper so Layout() can reach widget parts.</summary>
        private sealed class WidgetRowRef : MonoBehaviour
        {
            public Text ValueText;
            public RectTransform ValueRect => ValueText != null ? (RectTransform)ValueText.transform : null;
            public RectTransform Prev;
            public RectTransform Next;
            public Slider Slider;
            public Func<string> ValueLabel;

            public void Store(Text valueText) { ValueText = valueText; }
        }
    }
}
