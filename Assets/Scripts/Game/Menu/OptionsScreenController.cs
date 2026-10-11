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

        // Standard components (Audio) — side-by-side: mix left, mic right.
        private Slider _master;
        private Slider _music;
        private Slider _sfx;
        private Slider _game;
        private Slider _animals;
        private Slider _monsters;
        private Slider _environment;
        private Slider _players;
        private Dropdown _inputDevice;
        private Dropdown _outputDevice;
        private Slider _micVolume;
        private Toggle _micEnabled;
        private Toggle _micMonitor;
        private OptionsScreenBuilder.MicMeterBar _micMeter;
        private Text _micMeterPercent;
        private List<string> _inputDeviceIds = new List<string>();
        private List<string> _outputDeviceIds = new List<string>();

        private GameSettings _settings;

        // Unsaved-changes guard: snapshot taken when the screen opens; BACK/
        // Escape compare against it and ask before throwing edits away.
        private (bool fs, bool vs, int w, int h, float gui, string quality,
            float brightness, int fov, int chunks,
            float master, float music, float sfx, float game, float animals, float monsters, float environment, float players,
            int inDev, int outDev, float micVol, bool micOn, bool micMon) _snapshot;
        private GameObject _confirmPanel;

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
            TakeSnapshot();

            Debug.Log("Options screen opened.");
        }

        // ------------------------------------------------------------------
        // Unsaved-changes guard
        // ------------------------------------------------------------------

        private void TakeSnapshot()
        {
            // Read from the WIDGETS (not raw settings) so the snapshot matches
            // exactly what HasUnsavedChanges compares against — a saved value
            // that isn't offered in a dropdown (e.g. an odd old resolution)
            // must not read as an unsaved change before the user touches it.
            _snapshot = (_fsToggle.isOn, _vsToggle.isOn,
                Resolutions[Mathf.Clamp(_resDropdown.value, 0, Resolutions.Length - 1)].w,
                Resolutions[Mathf.Clamp(_resDropdown.value, 0, Resolutions.Length - 1)].h,
                GuiScales[Mathf.Clamp(_guiDropdown.value, 0, GuiScales.Length - 1)].value,
                Qualities[Mathf.Clamp(_qualityDropdown.value, 0, Qualities.Length - 1)],
                Mathf.Lerp(0.5f, 1.5f, Mathf.Clamp01(_brightness.value)),
                Mathf.RoundToInt(Mathf.Lerp(60f, 110f, Mathf.Clamp01(_fov.value))),
                Mathf.RoundToInt(Mathf.Lerp(4f, 16f, Mathf.Clamp01(_renderDistance.value))),
                Mathf.Clamp01(_master.value), Mathf.Clamp01(_music.value), Mathf.Clamp01(_sfx.value),
                Mathf.Clamp01(_game.value), Mathf.Clamp01(_animals.value), Mathf.Clamp01(_monsters.value),
                Mathf.Clamp01(_environment.value), Mathf.Clamp01(_players.value),
                _inputDevice.value, _outputDevice.value,
                Mathf.Clamp01(_micVolume.value), _micEnabled.isOn, _micMonitor.isOn);
        }

        private bool HasUnsavedChanges()
        {
            return _fsToggle.isOn != _snapshot.fs
                || _vsToggle.isOn != _snapshot.vs
                || Resolutions[Mathf.Clamp(_resDropdown.value, 0, Resolutions.Length - 1)] != (_snapshot.w, _snapshot.h)
                || !Mathf.Approximately(GuiScales[Mathf.Clamp(_guiDropdown.value, 0, GuiScales.Length - 1)].value, _snapshot.gui)
                || Qualities[Mathf.Clamp(_qualityDropdown.value, 0, Qualities.Length - 1)] != _snapshot.quality
                || !Mathf.Approximately(Mathf.Lerp(0.5f, 1.5f, Mathf.Clamp01(_brightness.value)), _snapshot.brightness)
                || Mathf.RoundToInt(Mathf.Lerp(60f, 110f, Mathf.Clamp01(_fov.value))) != _snapshot.fov
                || Mathf.RoundToInt(Mathf.Lerp(4f, 16f, Mathf.Clamp01(_renderDistance.value))) != _snapshot.chunks
                || !Mathf.Approximately(Mathf.Clamp01(_master.value), _snapshot.master)
                || !Mathf.Approximately(Mathf.Clamp01(_music.value), _snapshot.music)
                || !Mathf.Approximately(Mathf.Clamp01(_sfx.value), _snapshot.sfx)
                || !Mathf.Approximately(Mathf.Clamp01(_game.value), _snapshot.game)
                || !Mathf.Approximately(Mathf.Clamp01(_animals.value), _snapshot.animals)
                || !Mathf.Approximately(Mathf.Clamp01(_monsters.value), _snapshot.monsters)
                || !Mathf.Approximately(Mathf.Clamp01(_environment.value), _snapshot.environment)
                || !Mathf.Approximately(Mathf.Clamp01(_players.value), _snapshot.players)
                || _inputDevice.value != _snapshot.inDev
                || _outputDevice.value != _snapshot.outDev
                || !Mathf.Approximately(Mathf.Clamp01(_micVolume.value), _snapshot.micVol)
                || _micEnabled.isOn != _snapshot.micOn
                || _micMonitor.isOn != _snapshot.micMon;
        }

        /// <summary>BACK/Escape: block when edits exist and ask what to do.</summary>
        private void RequestClose()
        {
            if (_confirmPanel != null)
            {
                Close(); // already asking — a second Esc confirms discard
                return;
            }
            if (!HasUnsavedChanges())
            {
                Close();
                return;
            }
            ShowUnsavedConfirm();
        }

        private void ShowUnsavedConfirm()
        {
            _confirmPanel = new GameObject("UnsavedConfirm", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _confirmPanel.transform.SetParent(_screenRoot.transform, false);
            Stretch((RectTransform)_confirmPanel.transform);
            _confirmPanel.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f); // swallow clicks behind

            var box = new GameObject("Box", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            box.transform.SetParent(_confirmPanel.transform, false);
            var boxRect = (RectTransform)box.transform;
            boxRect.anchorMin = boxRect.anchorMax = boxRect.pivot = new Vector2(0.5f, 0.5f);
            boxRect.sizeDelta = new Vector2(560f, 220f);
            box.GetComponent<Image>().color = new Color(0.12f, 0.12f, 0.14f, 0.98f);

            var msg = CreateLabel(boxRect, "You have unsaved changes.", 22, Color.white);
            var msgRect = (RectTransform)msg.transform;
            msgRect.anchorMin = new Vector2(0f, 1f);
            msgRect.anchorMax = new Vector2(1f, 1f);
            msgRect.pivot = new Vector2(0.5f, 1f);
            msgRect.anchoredPosition = new Vector2(0f, -50f);
            msg.alignment = TextAnchor.MiddleCenter;

            var sub = CreateLabel(boxRect, "Discard your edits, or save them with APPLY?", 16, new Color(0.86f, 0.89f, 0.92f, 1f));
            var subRect = (RectTransform)sub.transform;
            subRect.anchorMin = new Vector2(0f, 1f);
            subRect.anchorMax = new Vector2(1f, 1f);
            subRect.pivot = new Vector2(0.5f, 1f);
            subRect.anchoredPosition = new Vector2(0f, -90f);
            sub.alignment = TextAnchor.MiddleCenter;

            var discard = CreateTextureButton("Btn_Discard", null, "DISCARD", () => Close());
            discard.transform.SetParent(boxRect, false);
            var save = CreateTextureButton("Btn_SaveAndClose", null, "SAVE", () => { Destroy(_confirmPanel); _confirmPanel = null; Apply(); });
            save.transform.SetParent(boxRect, false);

            float btnY = -150f;
            var dRect = (RectTransform)discard.transform;
            dRect.anchorMin = dRect.anchorMax = dRect.pivot = new Vector2(0.5f, 0.5f);
            dRect.sizeDelta = new Vector2(200f, 48f);
            dRect.anchoredPosition = new Vector2(-120f, btnY);
            var sRect = (RectTransform)save.transform;
            sRect.anchorMin = sRect.anchorMax = sRect.pivot = new Vector2(0.5f, 0.5f);
            sRect.sizeDelta = new Vector2(200f, 48f);
            sRect.anchoredPosition = new Vector2(120f, btnY);
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
                out _master, out _music, out _sfx,
                out _game, out _animals, out _monsters, out _environment, out _players,
                out _inputDevice, out _outputDevice,
                out _micVolume, out _micEnabled, out _micMonitor,
                out _micMeter, out _micMeterPercent,
                _screenRoot);

            PopulateAudioValues();

            // CONTROLS: sub-tab bind editor (MonoGame parity).
            ControlsScreenBuilder.BindSettings(_settings, () => { _settings.Save(null); });
            ControlsScreenBuilder.Build(_tabPages[Tab.Controls], this);
            // PACKS & ADDONS: real addon management (MonoGame parity).
            AddonsScreenBuilder.BindSettings(_settings, () => { _settings.Save(null); });
            AddonsScreenBuilder.Build(_tabPages[Tab.Packs], this);
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
            var back = CreateTextureButton("Btn_Back", _backTexture, "BACK", RequestClose);
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
        // Audio widgets (bound by the builder)
        // ------------------------------------------------------------------

        public void BindAudioWidgets(Slider master, Slider music, Slider sfx,
            Slider game, Slider animals, Slider monsters, Slider environment, Slider players,
            Dropdown inputDevice, Dropdown outputDevice,
            Slider micVolume, Toggle micEnabled, Toggle micMonitor)
        {
            _master = master; _music = music; _sfx = sfx;
            _game = game; _animals = animals; _monsters = monsters;
            _environment = environment; _players = players;
            _inputDevice = inputDevice; _outputDevice = outputDevice;
            _micVolume = micVolume; _micEnabled = micEnabled; _micMonitor = micMonitor;
        }

        private static string DefaultDeviceLabel(string name, string id)
        {
            return string.IsNullOrWhiteSpace(id) ? "SYSTEM DEFAULT" : name;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
                RequestClose();

            var rect = _canvas.GetComponent<RectTransform>().rect;
            if (Math.Abs(rect.width - _lastW) > 0.5f || Math.Abs(rect.height - _lastH) > 0.5f)
                Layout();

            // Always-on mic meter while the Options screen exists.
            MicMonitorHost.Poll();
            if (_micMeter != null)
                _micMeter.SetLevel(MicMonitorHost.Level);

            // Drain main-thread callbacks (addon import dialog results).
            UnityMainThread.RunPending();
        }

        private void PopulateAudioValues()
        {
            _master.SetValueWithoutNotify(Mathf.Clamp01(_settings.MasterVolume));
            _music.SetValueWithoutNotify(Mathf.Clamp01(_settings.MusicVolume));
            _sfx.SetValueWithoutNotify(Mathf.Clamp01(_settings.SfxVolume));
            _game.SetValueWithoutNotify(Mathf.Clamp01(_settings.GameVolume));
            _animals.SetValueWithoutNotify(Mathf.Clamp01(_settings.AnimalsVolume));
            _monsters.SetValueWithoutNotify(Mathf.Clamp01(_settings.MonstersVolume));
            _environment.SetValueWithoutNotify(Mathf.Clamp01(_settings.EnvironmentVolume));
            _players.SetValueWithoutNotify(Mathf.Clamp01(_settings.PlayersVolume));
            _micVolume.SetValueWithoutNotify(Mathf.Clamp01(_settings.MicVolume));
            _micEnabled.SetIsOnWithoutNotify(_settings.MicEnabled);
            _micMonitor.SetIsOnWithoutNotify(_settings.MicMonitor);

            // Real device enumeration: mics via Microphone.devices, output
            // (render) devices via the WASAPI MMDevice registry in
            // MicMonitorHost. Each dropdown starts with SYSTEM DEFAULT.
            _inputDeviceIds.Clear();
            _outputDeviceIds.Clear();
            var inputOptions = new List<string>();
            var outputOptions = new List<string>();
            foreach (var (id, name) in MicMonitorHost.EnumerateInputDevices())
            {
                _inputDeviceIds.Add(id);
                inputOptions.Add(name.ToUpperInvariant());
            }
            foreach (var (id, name) in MicMonitorHost.EnumerateOutputDevices())
            {
                _outputDeviceIds.Add(id);
                outputOptions.Add(name.ToUpperInvariant());
            }

            _inputDevice.ClearOptions();
            _inputDevice.AddOptions(inputOptions);
            _inputDevice.SetValueWithoutNotify(Mathf.Max(0, _inputDeviceIds.IndexOf(_settings.AudioInputDeviceId)));
            _outputDevice.ClearOptions();
            _outputDevice.AddOptions(outputOptions);
            _outputDevice.SetValueWithoutNotify(Mathf.Max(0, _outputDeviceIds.IndexOf(_settings.AudioOutputDeviceId)));

            foreach (var s in new[] { _master, _music, _sfx, _game, _animals, _monsters, _environment, _players, _micVolume })
                s.GetComponent<OptionsScreenBuilder.SliderValueText>().Refresh();

            // Always-on live preview: capture starts regardless of the ENABLE
            // toggle so the meter is viewable immediately; sidetone only when
            // both ENABLE and MONITOR are on.
            MicMonitorHost.Configure(_settings.AudioInputDeviceId,
                micEnabled: true,
                _settings.MicVolume,
                _settings.MicMonitor && _settings.MicEnabled);

            // Live re-configure when any mic widget changes.
            _inputDevice.onValueChanged.AddListener(_ => ReconfigureMic());
            _micVolume.onValueChanged.AddListener(_ => ReconfigureMic());
            _micEnabled.onValueChanged.AddListener(_ => ReconfigureMic());
            _micMonitor.onValueChanged.AddListener(_ => ReconfigureMic());
        }

        private void ReconfigureMic()
        {
            var deviceId = _inputDevice.value > 0 && _inputDevice.value < _inputDeviceIds.Count
                ? _inputDeviceIds[_inputDevice.value]
                : "";
            // Meter stays live even when ENABLE is off (view-only preview);
            // sidetone requires both ENABLE and MONITOR.
            MicMonitorHost.Configure(deviceId,
                micEnabled: true,
                _micVolume.value,
                _micMonitor.isOn && _micEnabled.isOn);
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
                apply.anchoredPosition = new Vector2(0, -18f); // below the frame's bottom edge, clear of the GUI box
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
            _settings.GameVolume = Mathf.Clamp01(_game.value);
            _settings.AnimalsVolume = Mathf.Clamp01(_animals.value);
            _settings.MonstersVolume = Mathf.Clamp01(_monsters.value);
            _settings.EnvironmentVolume = Mathf.Clamp01(_environment.value);
            _settings.PlayersVolume = Mathf.Clamp01(_players.value);
            _settings.AudioInputDeviceId = _inputDevice.value > 0 && _inputDevice.value < _inputDeviceIds.Count
                ? _inputDeviceIds[_inputDevice.value]
                : "";
            _settings.AudioOutputDeviceId = _outputDevice.value > 0 && _outputDevice.value < _outputDeviceIds.Count
                ? _outputDeviceIds[_outputDevice.value]
                : "";
            _settings.MicVolume = Mathf.Clamp01(_micVolume.value);
            _settings.MicEnabled = _micEnabled.isOn;
            _settings.MicMonitor = _micMonitor.isOn;

            _settings.Save(null); // same options.lvc format, unchanged
            Debug.Log("Options applied and saved to options.lvc.");

            // Stay on the Options screen and confirm with a toast instead of
            // dropping back to the main menu. Mode/resolution changes run
            // through the deferred SettingsBehavior path, which is safe to
            // trigger from the click handler (it never resizes inside it).
            SettingsBehavior.Apply(_settings);
            ReconfigureMic(); // apply saved mic config to the live monitor
            TakeSnapshot(); // applied values are now the baseline
            ShowAppliedToast();
        }

        // ------------------------------------------------------------------
        // "Settings applied" toast
        // ------------------------------------------------------------------

        private Coroutine _toastRoutine;

        private void ShowAppliedToast()
        {
            if (_toastRoutine != null)
                StopCoroutine(_toastRoutine);
            _toastRoutine = StartCoroutine(ToastRoutine());
        }

        private System.Collections.IEnumerator ToastRoutine()
        {
            var toastGo = new GameObject("AppliedToast", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var toastRect = (RectTransform)toastGo.transform;
            toastRect.SetParent(_screenRoot.transform, false);
            toastRect.anchorMin = toastRect.anchorMax = toastRect.pivot = new Vector2(0.5f, 0f);
            toastRect.sizeDelta = new Vector2(360f, 56f);
            toastRect.anchoredPosition = new Vector2(0f, 40f);
            toastGo.GetComponent<Image>().color = new Color(0.10f, 0.13f, 0.11f, 0.95f);

            var label = CreateLabel(toastRect, "Settings applied", 20, Color.white);
            var labelRect = (RectTransform)label.transform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            label.alignment = TextAnchor.MiddleCenter;
            label.raycastTarget = false;

            // Slide up + hold + fade out.
            var group = toastGo.AddComponent<CanvasGroup>();
            float t = 0f;
            while (t < 0.15f) { t += Time.unscaledDeltaTime; group.alpha = t / 0.15f; yield return null; }
            group.alpha = 1f;
            yield return new WaitForSecondsRealtime(1.2f);
            t = 0f;
            while (t < 0.3f)
            {
                t += Time.unscaledDeltaTime;
                group.alpha = 1f - t / 0.3f;
                toastRect.anchoredPosition += new Vector2(0f, 40f * Time.unscaledDeltaTime);
                yield return null;
            }
            Destroy(toastGo);
            _toastRoutine = null;
        }

        private void Close()
        {
            if (_closed)
                return;
            _closed = true;

            Debug.Log("Options screen closed.");
            MicMonitorHost.Shutdown(); // stop capture + sidetone when leaving options
            if (_screenRoot != null)
                Destroy(_screenRoot.gameObject);
            Destroy(gameObject);
            _onClosed?.Invoke();
        }
    }
}
