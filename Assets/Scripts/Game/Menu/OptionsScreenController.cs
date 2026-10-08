using System;
using System.Collections.Generic;
using LatticeVeil.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LatticeVeil.Game
{
    /// <summary>
    /// Unity rebuild of the MonoGame OptionsScreen, uGUI edition.
    ///
    /// Visual rules (matching the MonoGame look with the same assets):
    ///  - Settings.png background, Settings_GUI.png framed panel, texture tabs
    ///    (VIDEO / AUDIO / CONTROLS / PACKS with *Selected variants), Apply.png
    ///    and Back.png texture buttons.
    ///  - ALL controls live INSIDE the panel frame: the page is masked
    ///    (RectMask2D) and rows grow DOWNWARD from the content top, so every
    ///    row stays inside both the visible frame and the mask that hit-tests.
    ///  - Sliders are MonoGame-style: label above, dark track with white
    ///    border, light fill; click-or-drag anywhere on the track.
    ///  - Boolean settings are toggle switches; RESOLUTION is a traditional
    ///    click-to-open dropdown; GUI SCALE and QUALITY are cycle buttons.
    ///  - APPLY persists to options.lvc (format unchanged) and applies live
    ///    behavior through SettingsBehavior.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OptionsScreenController : MonoBehaviour
    {
        private enum Tab { Video, Audio, Controls, Packs }

        private static readonly Color PanelDim = new Color(0f, 0f, 0f, 0.55f);
        private static readonly Color ControlBg = new Color(0.08f, 0.08f, 0.08f, 1f);
        private static readonly Color ControlFill = new Color(0.78f, 0.78f, 0.78f, 1f);
        private static readonly Color Accent = new Color(0.55f, 0.8f, 1f, 1f);
        private static readonly Color TextColor = Color.white;
        private static readonly Color LabelColor = new Color(0.86f, 0.89f, 0.92f, 1f);
        private static readonly Color HoverTint = new Color(1.15f, 1.15f, 1.15f, 1f);

        private static readonly (int w, int h)[] Resolutions =
        {
            (1280, 720), (1920, 1080), (2560, 1440), (3840, 2160)
        };
        private static readonly (float value, string label)[] GuiScales =
        {
            (1.0f, "STANDARD"), (1.5f, "LARGE"), (2.0f, "LARGER")
        };
        private static readonly string[] Qualities = { "LOW", "MEDIUM", "HIGH", "ULTRA" };

        private Canvas _canvas;
        private RectTransform _screenRoot;
        private RectTransform _panelRoot;

        private Texture2D _bgTexture;
        private Texture2D _panelTexture;
        private Texture2D _backTexture;
        private Texture2D _applyTexture;

        private readonly Dictionary<Tab, GameObject> _tabButtons = new Dictionary<Tab, GameObject>();
        private readonly Dictionary<Tab, Texture2D> _tabTextures = new Dictionary<Tab, Texture2D>();
        private readonly Dictionary<Tab, Texture2D> _tabSelectedTextures = new Dictionary<Tab, Texture2D>();
        private readonly Dictionary<Tab, RectTransform> _tabPages = new Dictionary<Tab, RectTransform>();

        private Tab _tab = Tab.Video;
        private GameObject _openDropdownList;
        private GameObject _dropdownBlocker;

        private GameSettings _settings;
        private GameSettings _working;

        // Editable (working) values.
        private bool _fullscreen;
        private bool _vsync;
        private int _resolutionIndex;
        private int _guiScaleIndex;
        private int _qualityIndex;
        private float _brightness = 1f;
        private int _fov = 70;
        private int _renderDistance = 16;

        private float _master = 1f;
        private float _music = 1f;
        private float _sfx = 1f;

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
            _working = new GameSettings();
            CopyFrom(_settings, _working);

            _fullscreen = _working.Fullscreen;
            _vsync = _working.VSync;
            _guiScaleIndex = FindGuiScaleIndex(_working.GuiScale);
            _qualityIndex = Math.Max(0, Array.IndexOf(Qualities, NormalizeQuality(_working.QualityPreset)));
            _brightness = _working.Brightness;
            _fov = _working.FieldOfView;
            _renderDistance = _working.RenderDistanceChunks;
            _master = _working.MasterVolume;
            _music = _working.MusicVolume;
            _sfx = _working.SfxVolume;
            _resolutionIndex = FindResolutionIndex(_working.ResolutionWidth, _working.ResolutionHeight);

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

            // Re-layout when the resolution/window size changes so nothing
            // ever overflows the panel.
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

            BuildVideoPage(_tabPages[Tab.Video]);
            BuildAudioPage(_tabPages[Tab.Audio]);
            BuildNotePage(_tabPages[Tab.Controls], "CONTROLS", "Keybind remapping arrives with the controls update.\nCurrent binds live in options.lvc.");
            BuildNotePage(_tabPages[Tab.Packs], "PACKS & ADDONS", "Packs & mods management arrives with the packs update.\nDrop content packs into Documents/LatticeVeil/Packs.");
        }

        private void BuildFooter()
        {
            var back = CreateTextureButton("Btn_Back", _backTexture, "BACK", Close);
            back.transform.SetParent(_screenRoot, false);

            var apply = CreateTextureButton("Btn_APPLY", _applyTexture, "APPLY", Apply);
            apply.transform.SetParent(_panelRoot, false);
        }

        // ------------------------------------------------------------------
        // Video page
        // ------------------------------------------------------------------

        private void BuildVideoPage(RectTransform page)
        {
            // Left column
            AddToggle(page, "FULLSCREEN", () => _fullscreen, v => _fullscreen = v, Col.Left, 0);
            AddToggle(page, "VSYNC", () => _vsync, v => _vsync = v, Col.Left, 1);
            AddDropdown(page, "RESOLUTION", () => _resolutionIndex,
                i => i >= 0 && i < Resolutions.Length ? Resolutions[i].w + " x " + Resolutions[i].h : "CURRENT",
                Resolutions.Length, i => _resolutionIndex = i, Col.Left, 2);

            // Right column
            AddCycler(page, "GUI SCALE", () => _guiScaleIndex, GuiScales.Length,
                i => GuiScales[Mathf.Clamp(i, 0, GuiScales.Length - 1)].label, i => _guiScaleIndex = i, Col.Right, 0);
            AddCycler(page, "QUALITY", () => _qualityIndex, Qualities.Length,
                i => Qualities[Mathf.Clamp(i, 0, Qualities.Length - 1)], i => _qualityIndex = i, Col.Right, 1);
            AddSlider(page, "BRIGHTNESS", () => _brightness, v => _brightness = v, Col.Right, 2,
                () => Mathf.RoundToInt(Mathf.Clamp(_brightness, 0.5f, 1.5f) * 100) + "%");
            AddSlider(page, "FOV", () => (_fov - 60) / 50f, v => _fov = Mathf.RoundToInt(60f + v * 50f), Col.Right, 3,
                () => _fov.ToString());
            AddSlider(page, "RENDER DISTANCE", () => (_renderDistance - 4) / 12f,
                v => _renderDistance = Mathf.RoundToInt(4f + v * 12f), Col.Right, 4,
                () => _renderDistance + " CHUNKS");
        }

        private void BuildAudioPage(RectTransform page)
        {
            AddSlider(page, "MASTER", () => _master, v => _master = v, Col.Left, 0,
                () => Mathf.RoundToInt(_master * 100) + "%");
            AddSlider(page, "MUSIC", () => _music, v => _music = v, Col.Left, 1,
                () => Mathf.RoundToInt(_music * 100) + "%");
            AddSlider(page, "SFX", () => _sfx, v => _sfx = v, Col.Left, 2,
                () => Mathf.RoundToInt(_sfx * 100) + "%");
        }

        private void BuildNotePage(RectTransform page, string title, string text)
        {
            var t = CreateLabel(page, title, 22, TextColor);
            Anchor((RectTransform)t.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0, -8));
            var body = CreateLabel(page, text, 18, LabelColor);
            var bodyRect = (RectTransform)body.transform;
            bodyRect.anchorMin = new Vector2(0f, 1f);
            bodyRect.anchorMax = new Vector2(1f, 1f);
            bodyRect.pivot = new Vector2(0f, 1f);
            bodyRect.anchoredPosition = new Vector2(0, -40);
        }

        private enum Col { Left, Right }

        // ------------------------------------------------------------------
        // Widgets — rows are TOP-anchored and grow DOWNWARD (pivot 0,1), so
        // they always stay inside the page's RectMask2D (which both draws and
        // hit-tests) and inside the panel frame.
        // ------------------------------------------------------------------

        private sealed class Widget
        {
            public RectTransform Root;
            public Col Column;
            public int Row;
            public float Height;
        }

        private readonly List<Widget> _videoWidgets = new List<Widget>();
        private readonly List<Widget> _audioWidgets = new List<Widget>();

        private Widget Track(RectTransform page, Col col, int row, float height)
        {
            var go = new GameObject("W_" + col + "_" + row, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(page, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            var widget = new Widget { Root = rect, Column = col, Row = row, Height = height };
            (page == _tabPages[Tab.Audio] ? _audioWidgets : _videoWidgets).Add(widget);
            return widget;
        }

        private void AddToggle(RectTransform page, string label, Func<bool> get, Action<bool> set, Col col, int row)
        {
            var widget = Track(page, col, row, 40f);
            var rect = widget.Root;

            // Switch track — the raycast target (its Image receives clicks).
            var trackGo = new GameObject("Switch", typeof(RectTransform), typeof(Image));
            var track = (RectTransform)trackGo.transform;
            track.SetParent(rect, false);
            track.anchorMin = new Vector2(0f, 1f);
            track.anchorMax = new Vector2(0f, 1f);
            track.pivot = new Vector2(0f, 0.5f);
            track.sizeDelta = new Vector2(58, 26);
            track.anchoredPosition = new Vector2(0, -13);
            var trackImg = trackGo.GetComponent<Image>();
            trackImg.color = get() ? Accent : ControlBg;
            var trackOutline = trackGo.AddComponent<Outline>();
            trackOutline.effectColor = Color.white;
            trackOutline.effectDistance = Vector2.one;

            // Knob.
            var knobGo = new GameObject("Knob", typeof(RectTransform), typeof(Image));
            var knob = (RectTransform)knobGo.transform;
            knob.SetParent(track, false);
            knob.sizeDelta = new Vector2(20, 20);
            var knobImg = knobGo.GetComponent<Image>();
            knobImg.color = ControlFill;
            knobImg.raycastTarget = false;
            knob.anchoredPosition = get() ? new Vector2(15, 0) : new Vector2(-15, 0);

            var labelText = CreateLabel(rect, label, 19, TextColor);
            var labelRect = (RectTransform)labelText.transform;
            labelRect.anchorMin = new Vector2(0f, 1f);
            labelRect.anchorMax = new Vector2(0f, 1f);
            labelRect.pivot = new Vector2(0f, 0.5f);
            labelRect.anchoredPosition = new Vector2(70, -13);

            // Click target: a transparent Image over the whole row + the switch,
            // so clicking the label or the switch both toggle.
            var hitGo = new GameObject("Hit", typeof(RectTransform), typeof(Image), typeof(Button));
            var hit = (RectTransform)hitGo.transform;
            hit.SetParent(rect, false);
            Stretch(hit);
            var hitImg = hitGo.GetComponent<Image>();
            hitImg.color = Color.clear;
            var btn = hitGo.GetComponent<Button>();
            btn.targetGraphic = hitImg;
            btn.transition = Selectable.Transition.ColorTint;
            var colors = Block(HoverTint);
            colors.highlightedColor = new Color(1f, 1f, 1f, 0.06f);
            btn.colors = colors;
            btn.onClick.AddListener(() =>
            {
                var next = !get();
                set(next);
                trackImg.color = next ? Accent : ControlBg;
                knob.anchoredPosition = next ? new Vector2(15, 0) : new Vector2(-15, 0);
                Debug.Log($"Option changed: {label} = {next}");
            });
        }

        private void AddSlider(RectTransform page, string label, Func<float> get, Action<float> set,
            Col col, int row, Func<string> valueLabel)
        {
            var widget = Track(page, col, row, 50f);
            var rect = widget.Root;

            var labelText = CreateLabel(rect, label, 18, LabelColor);
            var labelRect = (RectTransform)labelText.transform;
            labelRect.anchorMin = new Vector2(0f, 1f);
            labelRect.anchorMax = new Vector2(0f, 1f);
            labelRect.pivot = new Vector2(0f, 1f);
            labelRect.anchoredPosition = new Vector2(0, 0);

            var valueText = CreateLabel(rect, valueLabel(), 16, TextColor);
            var valueRect = (RectTransform)valueText.transform;
            valueRect.anchorMin = new Vector2(1f, 1f);
            valueRect.anchorMax = new Vector2(1f, 1f);
            valueRect.pivot = new Vector2(1f, 1f);
            valueRect.anchoredPosition = new Vector2(0, 0);

            // Track (MonoGame style: dark bar, white border, light fill). The
            // track Image itself is the raycast/drag target — a Graphic is
            // required for uGUI raycasts to ever reach the handler.
            var trackGo = new GameObject("Track", typeof(RectTransform), typeof(Image));
            var track = (RectTransform)trackGo.transform;
            track.SetParent(rect, false);
            track.anchorMin = new Vector2(0f, 1f);
            track.anchorMax = new Vector2(1f, 1f);
            track.pivot = new Vector2(0.5f, 1f);
            track.sizeDelta = new Vector2(0, 16);
            track.anchoredPosition = new Vector2(0, -26);
            var trackImg = trackGo.GetComponent<Image>();
            trackImg.color = ControlBg;

            var outline = trackGo.AddComponent<Outline>();
            outline.effectColor = Color.white;
            outline.effectDistance = Vector2.one;

            // Fill (non-raycast).
            var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            var fill = (RectTransform)fillGo.transform;
            fill.SetParent(track, false);
            fill.anchorMin = new Vector2(0f, 0f);
            fill.anchorMax = new Vector2(0f, 0f);
            fill.pivot = new Vector2(0f, 0f);
            fill.sizeDelta = new Vector2(0, 14);
            fill.anchorMin = new Vector2(0f, 0f);
            fill.anchorMax = new Vector2(0f, 1f);
            fill.pivot = new Vector2(0f, 0.5f);
            fill.offsetMin = new Vector2(1, 1);
            fill.offsetMax = new Vector2(0, -1);
            var fillImg = fillGo.GetComponent<Image>();
            fillImg.color = ControlFill;
            fillImg.raycastTarget = false;

            void UpdateFill()
            {
                var v = Mathf.Clamp01(get());
                fill.anchorMax = new Vector2(v, 1f);
                fill.offsetMin = new Vector2(1, 1);
                fill.offsetMax = new Vector2(0, -1);
            }

            var handler = trackGo.AddComponent<SliderDragHandler>();
            handler.Track = track;
            handler.Get = get;
            handler.Set = set;
            handler.OnChanged = UpdateFill;
            handler.ValueText = valueText;
            handler.ValueLabel = valueLabel;
            handler.Label = label;

            UpdateFill();
        }

        private void AddDropdown(RectTransform page, string label, Func<int> get, Func<int, string> labelOf,
            int count, Action<int> set, Col col, int row)
        {
            var widget = Track(page, col, row, 48f);
            var rect = widget.Root;

            var labelText = CreateLabel(rect, label, 18, LabelColor);
            var labelRect = (RectTransform)labelText.transform;
            labelRect.anchorMin = new Vector2(0f, 1f);
            labelRect.anchorMax = new Vector2(0f, 1f);
            labelRect.pivot = new Vector2(0f, 1f);
            labelRect.anchoredPosition = new Vector2(0, 0);

            // Value box with arrow, click-to-open (traditional dropdown).
            var boxGo = new GameObject("Box", typeof(RectTransform), typeof(Image), typeof(Button));
            var box = (RectTransform)boxGo.transform;
            box.SetParent(rect, false);
            box.anchorMin = new Vector2(0f, 1f);
            box.anchorMax = new Vector2(1f, 1f);
            box.pivot = new Vector2(0.5f, 1f);
            box.sizeDelta = new Vector2(0, 30);
            box.anchoredPosition = new Vector2(0, -18);
            var boxImg = boxGo.GetComponent<Image>();
            boxImg.color = ControlBg;
            var boxOutline = boxGo.AddComponent<Outline>();
            boxOutline.effectColor = Color.white;
            boxOutline.effectDistance = Vector2.one;
            var boxBtn = boxGo.GetComponent<Button>();
            boxBtn.targetGraphic = boxImg;
            boxBtn.transition = Selectable.Transition.ColorTint;
            boxBtn.colors = Block(HoverTint);

            var valueText = CreateLabel(box, labelOf(get()), 17, TextColor);
            var valueRect = (RectTransform)valueText.transform;
            valueRect.anchorMin = new Vector2(0f, 0.5f);
            valueRect.anchorMax = new Vector2(0f, 0.5f);
            valueRect.pivot = new Vector2(0f, 0.5f);
            valueRect.anchoredPosition = new Vector2(10, 0);

            var arrow = CreateLabel(box, "\u25BC", 14, LabelColor);
            var arrowRect = (RectTransform)arrow.transform;
            arrowRect.anchorMin = new Vector2(1f, 0.5f);
            arrowRect.anchorMax = new Vector2(1f, 0.5f);
            arrowRect.pivot = new Vector2(1f, 0.5f);
            arrowRect.anchoredPosition = new Vector2(-8, 0);

            boxBtn.onClick.AddListener(() => ToggleDropdownList(box, label, get, labelOf, count, set, valueText));
        }

        private void ToggleDropdownList(RectTransform box, string label, Func<int> get,
            Func<int, string> labelOf, int count, Action<int> set, Text valueText)
        {
            CloseDropdownList();

            // Blocker: click anywhere else closes the list.
            _dropdownBlocker = new GameObject("DropdownBlocker", typeof(RectTransform), typeof(Image), typeof(Button));
            var blockerRect = (RectTransform)_dropdownBlocker.transform;
            blockerRect.SetParent(_screenRoot, false);
            Stretch(blockerRect);
            var blockerImg = _dropdownBlocker.GetComponent<Image>();
            blockerImg.color = new Color(0, 0, 0, 0.3f);
            _dropdownBlocker.GetComponent<Button>().onClick.AddListener(CloseDropdownList);

            _openDropdownList = new GameObject("DropdownList", typeof(RectTransform), typeof(RectMask2D));
            var list = (RectTransform)_openDropdownList.transform;
            list.SetParent(_screenRoot, false);
            list.anchorMin = new Vector2(0f, 0f);
            list.anchorMax = new Vector2(0f, 0f);
            list.pivot = new Vector2(0f, 1f);

            // Position under the box (world-space -> local-space of screenRoot).
            var corners = new Vector3[4];
            box.GetWorldCorners(corners);
            var screenPos = RectTransformUtility.WorldToScreenPoint(null, corners[3]);
            var local = _screenRoot.InverseTransformPoint(new Vector3(screenPos.x, screenPos.y, 0f));
            list.anchoredPosition = new Vector2(local.x, local.y);
            list.sizeDelta = new Vector2(box.rect.width, Mathf.Min(count * 34 + 8, 220));

            var listImg = _openDropdownList.AddComponent<Image>();
            listImg.color = new Color(0.07f, 0.07f, 0.08f, 0.98f);
            listImg.raycastTarget = true;
            var listOutline = _openDropdownList.AddComponent<Outline>();
            listOutline.effectColor = Color.white;
            listOutline.effectDistance = Vector2.one;

            for (int i = 0; i < count; i++)
            {
                int index = i;
                var itemGo = new GameObject("Item_" + i, typeof(RectTransform), typeof(Image), typeof(Button));
                var item = (RectTransform)itemGo.transform;
                item.SetParent(list, false);
                item.anchorMin = new Vector2(0f, 1f);
                item.anchorMax = new Vector2(1f, 1f);
                item.pivot = new Vector2(0f, 1f);
                item.sizeDelta = new Vector2(-8, 30);
                item.anchoredPosition = new Vector2(4, -4 - i * 34);
                var itemImg = itemGo.GetComponent<Image>();
                itemImg.color = i == get() ? new Color(1f, 1f, 1f, 0.14f) : Color.clear;

                var itemBtn = itemGo.GetComponent<Button>();
                itemBtn.targetGraphic = itemImg;
                itemBtn.transition = Selectable.Transition.ColorTint;
                itemBtn.colors = Block(new Color(1f, 1f, 1f, 0.14f));
                var itemText = CreateLabel(item, labelOf(i), 16, i == get() ? Accent : TextColor);
                var itemTextRect = (RectTransform)itemText.transform;
                itemTextRect.anchorMin = new Vector2(0f, 0.5f);
                itemTextRect.anchorMax = new Vector2(0f, 0.5f);
                itemTextRect.pivot = new Vector2(0f, 0.5f);
                itemTextRect.anchoredPosition = new Vector2(10, 0);

                itemBtn.onClick.AddListener(() =>
                {
                    set(index);
                    valueText.text = labelOf(index);
                    Debug.Log($"Option changed: {label} = {labelOf(index)}");
                    CloseDropdownList();
                });
            }
        }

        private void CloseDropdownList()
        {
            if (_openDropdownList != null)
            {
                Destroy(_openDropdownList);
                _openDropdownList = null;
            }
            if (_dropdownBlocker != null)
            {
                Destroy(_dropdownBlocker);
                _dropdownBlocker = null;
            }
        }

        private void AddCycler(RectTransform page, string label, Func<int> get, int count,
            Func<int, string> labelOf, Action<int> set, Col col, int row)
        {
            var widget = Track(page, col, row, 48f);
            var rect = widget.Root;

            var labelText = CreateLabel(rect, label, 18, LabelColor);
            var labelRect = (RectTransform)labelText.transform;
            labelRect.anchorMin = new Vector2(0f, 1f);
            labelRect.anchorMax = new Vector2(0f, 1f);
            labelRect.pivot = new Vector2(0f, 1f);
            labelRect.anchoredPosition = new Vector2(0, 0);

            var boxGo = new GameObject("Box", typeof(RectTransform), typeof(Image));
            var box = (RectTransform)boxGo.transform;
            box.SetParent(rect, false);
            box.anchorMin = new Vector2(0f, 1f);
            box.anchorMax = new Vector2(1f, 1f);
            box.pivot = new Vector2(0.5f, 1f);
            box.sizeDelta = new Vector2(0, 30);
            box.anchoredPosition = new Vector2(0, -18);
            var boxImg = boxGo.GetComponent<Image>();
            boxImg.color = ControlBg;
            var boxOutline = boxGo.AddComponent<Outline>();
            boxOutline.effectColor = Color.white;
            boxOutline.effectDistance = Vector2.one;

            var prev = CreateTextButton("Prev", "<", null);
            prev.transform.SetParent(box, false);
            var next = CreateTextButton("Next", ">", null);
            next.transform.SetParent(box, false);

            var valueText = CreateLabel(box, labelOf(get()), 17, TextColor);
            var valueRect = (RectTransform)valueText.transform;
            valueRect.anchorMin = new Vector2(0.5f, 0.5f);
            valueRect.anchorMax = new Vector2(0.5f, 0.5f);
            valueRect.pivot = new Vector2(0.5f, 0.5f);

            var prevBtn = prev.GetComponent<Button>();
            var nextBtn = next.GetComponent<Button>();
            prevBtn.onClick.AddListener(() =>
            {
                var idx = Mathf.Max(0, get() - 1);
                set(idx);
                valueText.text = labelOf(idx);
                Debug.Log($"Option changed: {label} = {valueText.text}");
            });
            nextBtn.onClick.AddListener(() =>
            {
                var idx = Mathf.Min(count - 1, get() + 1);
                set(idx);
                valueText.text = labelOf(idx);
                Debug.Log($"Option changed: {label} = {valueText.text}");
            });

            // Positions are set in LayoutRows via CyclerRefs.
            box.gameObject.AddComponent<CyclerRefs>().Init(prev.GetComponent<RectTransform>(), next.GetComponent<RectTransform>(), valueRect);
        }

        // ------------------------------------------------------------------
        // Layout — everything inside the panel frame; rows grow downward.
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

            float panelW = Mathf.Min(1248f, w - 20f);
            float panelH = Mathf.Min(680f, h - 30f);
            _panelRoot.sizeDelta = new Vector2(panelW, panelH);
            _panelRoot.anchoredPosition = Vector2.zero;

            var layoutScale = Mathf.Clamp(Mathf.Min(panelW / 1180f, panelH / 680f), 0.82f, 1f);

            // Tabs above the panel.
            float tabHeight = Mathf.Max(44f, Mathf.Min(70f, panelW * 0.095f) * layoutScale);
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
            float tabY = panelH / 2f + Mathf.Max(12f, 16f * layoutScale) - tabHeight / 2f;
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
                float bh = Mathf.Max(48f, 62f * layoutScale);
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
                float ah = Mathf.Max(34f, 44f * layoutScale);
                float aw = Mathf.Min(ah * aspect, panelW * 0.4f);
                apply.sizeDelta = new Vector2(aw, ah);
                apply.anchorMin = new Vector2(0.5f, 0f);
                apply.anchorMax = new Vector2(0.5f, 0f);
                apply.pivot = new Vector2(0.5f, 0f);
                apply.anchoredPosition = new Vector2(0, Mathf.Max(14f, 18f * layoutScale));
            }

            // Content: inset INSIDE the frame so nothing touches the border.
            float insetX = Mathf.Max(52f, 118f * layoutScale);
            float contentTop = Mathf.Max(56f, 78f * layoutScale);
            float contentBottom = Mathf.Max(64f, 84f * layoutScale);
            float contentW = panelW - insetX * 2f;
            float columnGap = Mathf.Max(24f, 40f * layoutScale);
            float columnW = (contentW - columnGap) / 2f;
            float rowGap = Mathf.Max(12f, 16f * layoutScale);

            LayoutPage(_videoWidgets, insetX, contentTop, contentBottom, columnW, columnGap, rowGap);
            LayoutPage(_audioWidgets, insetX, contentTop, contentBottom, columnW, columnGap, rowGap);
            LayoutStaticPage(_tabPages[Tab.Controls], insetX, contentTop);
            LayoutStaticPage(_tabPages[Tab.Packs], insetX, contentTop);
        }

        private void LayoutPage(List<Widget> widgets, float insetX, float contentTop,
            float contentBottom, float columnW, float columnGap, float rowGap)
        {
            float panelH = _panelRoot.rect.height;
            foreach (var widget in widgets)
            {
                float colX = insetX + (widget.Column == Col.Right ? columnW + columnGap : 0f);
                float rowY = contentTop + widget.Row * (widget.Height + rowGap);

                // Overflow guard: never lay a row past the content bottom.
                float maxRows = Mathf.Max(1f, Mathf.Floor((panelH - contentTop - contentBottom + rowGap) / (widget.Height + rowGap)));
                widget.Root.gameObject.SetActive(widget.Row < maxRows);

                widget.Root.sizeDelta = new Vector2(columnW, widget.Height);
                widget.Root.anchoredPosition = new Vector2(colX, -rowY);

                var cycler = widget.Root.GetComponentInChildren<CyclerRefs>(true);
                if (cycler != null)
                {
                    cycler.Prev.sizeDelta = new Vector2(34, 26);
                    cycler.Prev.anchoredPosition = new Vector2(4, -15);
                    cycler.Next.sizeDelta = new Vector2(34, 26);
                    cycler.Next.anchoredPosition = new Vector2(columnW - 38, -15);
                    cycler.Value.sizeDelta = new Vector2(columnW - 90, 26);
                    cycler.Value.anchoredPosition = new Vector2(columnW / 2f, -15);
                }
            }
        }

        private void LayoutStaticPage(RectTransform page, float insetX, float contentTop)
        {
            if (page == null)
                return;
            foreach (Transform child in page)
            {
                var rect = (RectTransform)child;
                if (rect.anchorMax.x > 0.9f)
                {
                    rect.anchoredPosition = new Vector2(insetX, -contentTop - 34f);
                    rect.offsetMin = new Vector2(insetX, 0);
                    rect.offsetMax = new Vector2(-insetX, 0);
                }
                else
                {
                    rect.anchoredPosition = new Vector2(insetX, -contentTop);
                }
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
            // A GameObject can carry only ONE Graphic: RawImage for textures,
            // plain Image for flat colors.
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
            text.font = OptionsFont;
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAnchor.MiddleLeft;
            text.text = content;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            return text;
        }

        private static Font _optionsFont;
        private static Font OptionsFont => _optionsFont != null
            ? _optionsFont
            : _optionsFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

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
                var label = CreateLabel(rect, fallbackLabel, 22, TextColor);
                Stretch((RectTransform)label.transform);
                label.alignment = TextAnchor.MiddleCenter;
            }

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = target;
            btn.transition = Selectable.Transition.ColorTint;
            btn.colors = Block(HoverTint);
            btn.onClick.AddListener(() => onClick());
            return go;
        }

        private GameObject CreateTextButton(string name, string label, Action onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            var img = go.GetComponent<Image>();
            img.color = new Color(0.13f, 0.13f, 0.15f, 1f);
            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.transition = Selectable.Transition.ColorTint;
            btn.colors = Block(HoverTint);
            if (onClick != null)
                btn.onClick.AddListener(() => onClick());

            var text = CreateLabel((RectTransform)go.transform, label, 17, TextColor);
            Stretch((RectTransform)text.transform);
            text.alignment = TextAnchor.MiddleCenter;
            return go;
        }

        private static ColorBlock Block(Color highlight)
        {
            var cb = ColorBlock.defaultColorBlock;
            cb.normalColor = Color.white;
            cb.highlightedColor = highlight;
            cb.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            cb.fadeDuration = 0.08f;
            return cb;
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
            CloseDropdownList();
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
            _working.Fullscreen = _fullscreen;
            _working.VSync = _vsync;
            _working.GuiScale = GuiScales[Mathf.Clamp(_guiScaleIndex, 0, GuiScales.Length - 1)].value;
            _working.QualityPreset = Qualities[Mathf.Clamp(_qualityIndex, 0, Qualities.Length - 1)];
            _working.Brightness = Mathf.Clamp(_brightness, 0.5f, 1.5f);
            _working.FieldOfView = Mathf.Clamp(_fov, 60, 110);
            _working.RenderDistanceChunks = Mathf.Clamp(_renderDistance, 4, 16);
            _working.MasterVolume = Mathf.Clamp01(_master);
            _working.MusicVolume = Mathf.Clamp01(_music);
            _working.SfxVolume = Mathf.Clamp01(_sfx);
            if (_resolutionIndex >= 0 && _resolutionIndex < Resolutions.Length)
            {
                _working.ResolutionWidth = Resolutions[_resolutionIndex].w;
                _working.ResolutionHeight = Resolutions[_resolutionIndex].h;
            }

            CopyInto(_working, _settings);
            _settings.Save(null); // same options.lvc format, unchanged
            Debug.Log("Options applied and saved to options.lvc.");

            SettingsBehavior.Apply(_settings);
            Close();
        }

        private void Close()
        {
            if (_closed)
                return;
            _closed = true;

            CloseDropdownList();
            Debug.Log("Options screen closed.");
            if (_screenRoot != null)
                Destroy(_screenRoot.gameObject);
            Destroy(gameObject);
            _onClosed?.Invoke();
        }

        private static void CopyFrom(GameSettings from, GameSettings to)
        {
            CopyInto(from, to);
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
            for (int i = 0; i < GuiScales.Length; i++)
            {
                if (Mathf.Approximately(GuiScales[i].value, value))
                    return i;
            }
            return 1;
        }

        private static int FindResolutionIndex(int width, int height)
        {
            for (int i = 0; i < Resolutions.Length; i++)
            {
                if (Resolutions[i].w == width && Resolutions[i].h == height)
                    return i;
            }
            return 0;
        }

        private static string NormalizeQuality(string value)
        {
            var quality = string.IsNullOrWhiteSpace(value) ? "MEDIUM" : value.Trim().ToUpperInvariant();
            return quality is "LOW" or "MEDIUM" or "HIGH" or "ULTRA" ? quality : "MEDIUM";
        }

        /// <summary>Per-widget helper: cycler buttons + value text.</summary>
        private sealed class CyclerRefs : MonoBehaviour
        {
            public RectTransform Prev;
            public RectTransform Next;
            public RectTransform Value;

            public void Init(RectTransform prev, RectTransform next, RectTransform value)
            {
                Prev = prev;
                Next = next;
                Value = value;
            }
        }

        /// <summary>
        /// MonoGame-style slider drag: clicking/dragging the track sets the
        /// value from the pointer position (Slider.Update semantics). Lives on
        /// the track Image so the raycast always reaches it.
        /// </summary>
        private sealed class SliderDragHandler : MonoBehaviour, IPointerDownHandler, IDragHandler
        {
            public RectTransform Track;
            public Text ValueText;
            public Func<float> Get;
            public Action<float> Set;
            public Action OnChanged;
            public Func<string> ValueLabel;
            public string Label;

            public void OnPointerDown(PointerEventData eventData) { Drag(eventData); }
            public void OnDrag(PointerEventData eventData) { Drag(eventData); }

            private void Drag(PointerEventData eventData)
            {
                if (Track == null)
                    return;
                Vector2 local;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    Track, eventData.position, eventData.pressEventCamera, out local);
                var v = Mathf.Clamp01((local.x + Track.rect.width / 2f) / Mathf.Max(1f, Track.rect.width));
                Set(v);
                OnChanged?.Invoke();
                if (ValueText != null && ValueLabel != null)
                    ValueText.text = ValueLabel();
                Debug.Log($"Option changed: {Label} = {Mathf.RoundToInt(Mathf.Clamp01(Get()) * 100f) / 100f:0.00}");
            }
        }
    }
}
