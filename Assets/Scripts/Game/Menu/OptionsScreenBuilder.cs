using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace LatticeVeil.Game
{
    /// <summary>
    /// Options page content built ONLY from standard Unity uGUI components
    /// (Toggle / Slider / Dropdown) — no custom widget code.
    ///
    /// Layout is explicit: each row gets a fixed center position in
    /// panel-local units so the content always fits inside the framed
    /// Settings_GUI panel at any window size (the CanvasScaler scales it).
    /// </summary>
    public static class OptionsScreenBuilder
    {
        // GUI box: a little smaller than the window (16:9 to match the frame art).
        public const float PanelWidth = 960f;
        public const float PanelHeight = 540f;

        // Content metrics (panel-local units, y measured from panel center).
        // Rows live inside a container scaled by ContentScale so everything
        // fits INSIDE the framed panel art (the frame has a thick border).
        public const float ContentScale = 0.84f;
        private const float RowHeight = 34f;
        private const float SliderHeight = 26f;
        private const float LabelOffset = 41f; // label center above control center
        private const float ColumnXLeft = -210f;
        private const float ColumnXRight = 210f;
        private const float ColumnWidth = 410f;

        private static readonly Color TextColor = Color.white;
        private static readonly Color LabelColor = new Color(0.86f, 0.89f, 0.92f, 1f);
        private static readonly Color Accent = new Color(0.55f, 0.8f, 1f, 1f);
        private static readonly Color ControlBg = new Color(0.08f, 0.08f, 0.09f, 1f);

        /// <summary>
        /// Creates the row container that holds the settings widgets, scaled
        /// down so the rows stay inside the framed panel's inner area.
        /// </summary>
        private static RectTransform CreateRows(RectTransform panel)
        {
            var rowsGo = new GameObject("Rows", typeof(RectTransform));
            var rows = (RectTransform)rowsGo.transform;
            rows.SetParent(panel, false);
            Stretch(rows);
            rows.localScale = Vector3.one * ContentScale;
            return rows;
        }

        /// <summary>Builds the Video page under the panel.</summary>
        public static GameObject BuildPage(RectTransform panel, OptionsScreenController controller)
        {
            var root = new GameObject("StandardPage", typeof(RectTransform));
            var rect = (RectTransform)root.transform;
            rect.SetParent(panel, false);
            Stretch(rect);
            var rows = CreateRows(rect);

            // ---- Left column ----
            var fsToggle = CreateToggle(rows, "FULLSCREEN", new Vector2(ColumnXLeft, 168f), ColumnWidth);
            var vsToggle = CreateToggle(rows, "VSYNC", new Vector2(ColumnXLeft, 120f), ColumnWidth);
            var resDropdown = CreateDropdown(rows, "RESOLUTION", new Vector2(ColumnXLeft, 40f), ColumnWidth,
                new[] { "1280 x 720", "1920 x 1080", "2560 x 1440", "3840 x 2160" });

            // ---- Right column ----
            var guiDropdown = CreateDropdown(rows, "GUI SCALE", new Vector2(ColumnXRight, 127f), ColumnWidth,
                new[] { "STANDARD", "LARGE", "LARGER" });
            var qualityDropdown = CreateDropdown(rows, "QUALITY", new Vector2(ColumnXRight, 51f), ColumnWidth,
                new[] { "LOW", "MEDIUM", "HIGH", "ULTRA" });
            var brightness = CreateSlider(rows, "BRIGHTNESS", new Vector2(ColumnXRight, -25f), ColumnWidth,
                v => Mathf.RoundToInt(v * 100f) + "%");
            var fov = CreateSlider(rows, "FOV", new Vector2(ColumnXRight, -101f), ColumnWidth,
                v => Mathf.RoundToInt(Mathf.Lerp(60f, 110f, v)).ToString());
            var renderDistance = CreateSlider(rows, "RENDER DISTANCE", new Vector2(ColumnXRight, -177f), ColumnWidth,
                v => Mathf.RoundToInt(Mathf.Lerp(4f, 16f, v)) + " CHUNKS");

            controller.BindStandardWidgets(fsToggle, vsToggle, resDropdown, guiDropdown,
                qualityDropdown, brightness, fov, renderDistance);

            return root;
        }

        /// <summary>
        /// Builds the Audio page: side-by-side columns like the MonoGame
        /// version, inside a vertically scrollable viewport when the rows run
        /// past the panel. Left column = device + MIXES (channel volumes);
        /// right column = MIC section (device, volume, toggles).
        /// </summary>
        public static RectTransform BuildAudioPage(RectTransform page, OptionsScreenController controller,
            out Slider master, out Slider music, out Slider sfx,
            out Slider game, out Slider animals, out Slider monsters, out Slider environment, out Slider players,
            out Dropdown inputDevice, out Dropdown outputDevice,
            out Slider micVolume, out Toggle micEnabled, out Toggle micMonitor,
            out MicMeterBar meterFill, out Text meterPercent,
            RectTransform screenRoot)
        {
            var root = new GameObject("StandardPageAudio", typeof(RectTransform));
            var rect = (RectTransform)root.transform;
            rect.SetParent(page, false);
            Stretch(rect);

            // Scrollable inner viewport (RectMask2D clips). Top is shaved so
            // the first row clears the frame art; bottom cut is above PLAYERS.
            var viewportGo = new GameObject("AudioViewport", typeof(RectTransform), typeof(RectMask2D));
            var viewport = (RectTransform)viewportGo.transform;
            viewport.SetParent(rect, false);
            viewport.anchorMin = new Vector2(0f, 0f);
            viewport.anchorMax = new Vector2(1f, 1f);
            viewport.pivot = new Vector2(0.5f, 0.5f);
            viewport.offsetMin = new Vector2(0f, 56f);
            viewport.offsetMax = new Vector2(0f, -34f); // shave the top more

            var scrollGo = viewportGo.AddComponent<ScrollRect>();
            scrollGo.horizontal = false;
            scrollGo.vertical = true;
            scrollGo.movementType = ScrollRect.MovementType.Clamped;
            scrollGo.scrollSensitivity = 28f;

            // Drag anywhere on the page scrolls (uGUI only drags from the
            // content's raycast targets — Sliders/Toggles eat the drag).
            viewportGo.AddComponent<ScrollAnywhereDrag>().Scroll = scrollGo;

            // Visible scrollbar just inside the right frame art (left of the
            // old right-edge position), clipped to the exact same top/bottom
            // cut as the viewport (offsets mirror it: +56 / -34).
            var scrollbarGo = new GameObject("Scrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
            var scrollbarRect = (RectTransform)scrollbarGo.transform;
            scrollbarRect.SetParent(rect, false);
            scrollbarRect.anchorMin = new Vector2(0f, 0f);
            scrollbarRect.anchorMax = new Vector2(0f, 1f);
            scrollbarRect.pivot = new Vector2(0f, 0.5f);
            scrollbarRect.offsetMin = new Vector2(424f, 56f);
            scrollbarRect.offsetMax = new Vector2(438f, -34f);
            scrollbarGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.14f);
            var scrollbar = scrollbarGo.GetComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;

            var slidingAreaGo = new GameObject("Sliding Area", typeof(RectTransform));
            var slidingArea = (RectTransform)slidingAreaGo.transform;
            slidingArea.SetParent(scrollbarRect, false);
            Stretch(slidingArea);
            slidingArea.offsetMin = new Vector2(3f, 4f);
            slidingArea.offsetMax = new Vector2(-3f, -4f);


            var handleGo = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            var handleRect = (RectTransform)handleGo.transform;
            handleRect.SetParent(slidingArea, false);
            Stretch(handleRect);
            handleGo.GetComponent<Image>().color = new Color(0.85f, 0.9f, 0.95f, 0.75f);

            scrollbar.handleRect = handleRect;
            scrollbar.targetGraphic = handleGo.GetComponent<Image>();
            scrollGo.verticalScrollbar = scrollbar;
            // Permanent: AutoHideAndExpandViewport re-lays-out the scrollbar
            // every frame (right-anchored per its own convention), which
            // overrides the explicit clip/position below.
            scrollGo.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            scrollGo.verticalScrollbarSpacing = 4f;

            var contentGo = new GameObject("AudioContent", typeof(RectTransform));
            var content = (RectTransform)contentGo.transform;
            content.SetParent(viewport, false);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;

            // Rows-local pitch is 66 (label sits 41 above its control), scaled
            // by ContentScale inside the content — so the unscaled content
            // needs ~500 units; anything past the panel scrolls.
            float contentH = 560f;
            content.sizeDelta = new Vector2(0f, contentH);
            scrollGo.content = content;
            scrollGo.viewport = viewport;

            // Invisible raycast catcher behind the rows: lets the user start a
            // drag on any empty pixel of the page ("scroll wherever I am").
            var catcherGo = new GameObject("ScrollCatcher", typeof(RectTransform), typeof(Image));
            var catcher = (RectTransform)catcherGo.transform;
            catcher.SetParent(content, false);
            catcher.SetAsFirstSibling();
            catcher.anchorMin = new Vector2(0f, 0f);
            catcher.anchorMax = new Vector2(1f, 0f);
            catcher.pivot = new Vector2(0.5f, 0f);
            catcher.sizeDelta = new Vector2(0f, contentH);
            catcherGo.GetComponent<Image>().color = Color.clear;
            catcherGo.GetComponent<Image>().raycastTarget = true;

            var rows = CreateRows(content);

            // ---- LEFT column: devices + channel mix ----
            var label = CreateText(rows, "OUTPUT DEVICE", 15, Accent);
            SetRect((RectTransform)label.transform, new Vector2(ColumnXLeft, 246f), new Vector2(ColumnWidth, 20f));
            label.alignment = TextAnchor.LowerLeft;
            outputDevice = CreateDropdown(rows, "", new Vector2(ColumnXLeft, 214f), ColumnWidth, new[] { "SYSTEM DEFAULT" }, screenRoot);

            master = CreateSlider(rows, "MASTER", new Vector2(ColumnXLeft, 148f), ColumnWidth, v => Mathf.RoundToInt(v * 100f) + "%");
            music = CreateSlider(rows, "MUSIC", new Vector2(ColumnXLeft, 84f), ColumnWidth, v => Mathf.RoundToInt(v * 100f) + "%");
            game = CreateSlider(rows, "GAME", new Vector2(ColumnXLeft, 20f), ColumnWidth, v => Mathf.RoundToInt(v * 100f) + "%");
            animals = CreateSlider(rows, "ANIMALS", new Vector2(ColumnXLeft, -44f), ColumnWidth, v => Mathf.RoundToInt(v * 100f) + "%");
            monsters = CreateSlider(rows, "MONSTERS", new Vector2(ColumnXLeft, -108f), ColumnWidth, v => Mathf.RoundToInt(v * 100f) + "%");
            environment = CreateSlider(rows, "ENVIRONMENT", new Vector2(ColumnXLeft, -172f), ColumnWidth, v => Mathf.RoundToInt(v * 100f) + "%");
            players = CreateSlider(rows, "PLAYERS", new Vector2(ColumnXLeft, -236f), ColumnWidth, v => Mathf.RoundToInt(v * 100f) + "%");

            // ---- RIGHT column: MIC section ----
            var micHeader = CreateText(rows, "MICROPHONE", 15, Accent);
            SetRect((RectTransform)micHeader.transform, new Vector2(ColumnXRight, 246f), new Vector2(ColumnWidth, 20f));
            micHeader.alignment = TextAnchor.LowerLeft;

            // (ENABLE MICROPHONE + INPUT DEVICE + MIC VOLUME + MONITOR laid out
            // below; see the right-column block further down.)

            micEnabled = CreateToggle(rows, "ENABLE MICROPHONE", new Vector2(ColumnXRight, 222f), ColumnWidth);
            inputDevice = CreateDropdown(rows, "INPUT DEVICE", new Vector2(ColumnXRight, 150f), ColumnWidth, new[] { "SYSTEM DEFAULT" }, screenRoot);
            micVolume = CreateSlider(rows, "MIC VOLUME", new Vector2(ColumnXRight, 78f), ColumnWidth, v => Mathf.RoundToInt(v * 100f) + "%");
            micMonitor = CreateToggle(rows, "MONITOR (HEAR YOURSELF)", new Vector2(ColumnXRight, 34f), ColumnWidth);

            var sfxHeader = CreateText(rows, "INTERFACE SOUNDS", 15, Accent);
            SetRect((RectTransform)sfxHeader.transform, new Vector2(ColumnXRight, -4f), new Vector2(ColumnWidth, 20f));
            sfxHeader.alignment = TextAnchor.LowerLeft;
            sfx = CreateSlider(rows, "", new Vector2(ColumnXRight, -68f), ColumnWidth, v => Mathf.RoundToInt(v * 100f) + "%");

            // MIC LEVEL meter — always on while the screen is open. The capture
            // starts regardless of the ENABLE toggle so the bar is viewable as
            // a live preview (MonoGame DrawMicMeter visual, gradient fill).
            var meterHeader = CreateText(rows, "MIC LEVEL", 15, Accent);
            SetRect((RectTransform)meterHeader.transform, new Vector2(ColumnXRight, -84f), new Vector2(ColumnWidth, 20f));
            meterHeader.alignment = TextAnchor.LowerLeft;
            meterFill = CreateMeter(rows, new Vector2(ColumnXRight, -168f), ColumnWidth);
            meterPercent = meterFill.GetComponent<MicMeterBar>().PercentText;

            controller.BindAudioWidgets(master, music, sfx, game, animals, monsters, environment, players,
                inputDevice, outputDevice, micVolume, micEnabled, micMonitor);
            return rect;
        }

        // ------------------------------------------------------------------
        // Standard uGUI factories
        // ------------------------------------------------------------------

        public static Toggle CreateToggle(RectTransform parent, string label, Vector2 center, float width)
        {
            var go = new GameObject("Toggle_" + label, typeof(RectTransform), typeof(Toggle));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            SetRect(rect, center, new Vector2(width, RowHeight));

            var toggle = go.GetComponent<Toggle>();
            toggle.transition = Selectable.Transition.None;

            // Standard checkbox square on the left.
            var bgGo = new GameObject("Background", typeof(RectTransform), typeof(Image));
            var bg = (RectTransform)bgGo.transform;
            bg.SetParent(rect, false);
            bg.anchorMin = new Vector2(0f, 0.5f);
            bg.anchorMax = new Vector2(0f, 0.5f);
            bg.pivot = new Vector2(0f, 0.5f);
            bg.sizeDelta = new Vector2(RowHeight, RowHeight);
            bg.anchoredPosition = new Vector2(2f, 0f);
            bgGo.GetComponent<Image>().color = ControlBg;
            var bgOutline = bgGo.AddComponent<Outline>();
            bgOutline.effectColor = Color.white;
            bgOutline.effectDistance = new Vector2(1.5f, -1.5f);

            var checkGo = new GameObject("Checkmark", typeof(RectTransform), typeof(Image));
            var check = (RectTransform)checkGo.transform;
            check.SetParent(bg, false);
            Stretch(check);
            check.offsetMin = new Vector2(5f, 5f);
            check.offsetMax = new Vector2(-5f, -5f);
            var checkImg = checkGo.GetComponent<Image>();
            checkImg.color = Accent;

            var text = CreateText(rect, label, 18, TextColor);
            var textRect = (RectTransform)text.transform;
            textRect.anchorMin = new Vector2(0f, 0.5f);
            textRect.anchorMax = new Vector2(0f, 0.5f);
            textRect.pivot = new Vector2(0f, 0.5f);
            textRect.sizeDelta = new Vector2(Mathf.Max(80f, width - RowHeight - 16f), RowHeight);
            textRect.anchoredPosition = new Vector2(RowHeight + 14f, 0f);
            text.alignment = TextAnchor.MiddleLeft;

            toggle.targetGraphic = bgGo.GetComponent<Image>();
            toggle.graphic = checkImg;
            toggle.isOn = false;
            return toggle;
        }

        public static Dropdown CreateDropdown(RectTransform parent, string label, Vector2 center, float width, string[] options,
            RectTransform portalRoot = null)
        {
            return CreateDropdownImpl(parent, label, center, width, options, portalRoot);
        }

        private static Dropdown CreateDropdownImpl(RectTransform parent, string label, Vector2 center, float width, string[] options,
            RectTransform portalRoot = null)
        {
            // Label row above the control.
            var labelText = CreateText(parent, label, 17, LabelColor);
            SetRect((RectTransform)labelText.transform, center + new Vector2(0f, LabelOffset), new Vector2(width, 22f));
            labelText.alignment = TextAnchor.LowerLeft;

            var go = new GameObject("Dropdown_" + label, typeof(RectTransform), typeof(Image), typeof(Dropdown));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            SetRect(rect, center, new Vector2(width, RowHeight));

            var bgImg = go.GetComponent<Image>();
            bgImg.color = ControlBg;
            var outline = go.AddComponent<Outline>();
            outline.effectColor = Color.white;
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            // Caption (selected value).
            var captionText = CreateText(rect, options.Length > 0 ? options[0] : "", 17, TextColor);
            var caption = (RectTransform)captionText.transform;
            caption.anchorMin = new Vector2(0f, 0.5f);
            caption.anchorMax = new Vector2(1f, 0.5f);
            caption.offsetMin = new Vector2(12f, -16f);
            caption.offsetMax = new Vector2(-38f, 16f);
            captionText.alignment = TextAnchor.MiddleLeft;

            // Arrow.
            var arrowText = CreateText(rect, "\u25BC", 13, LabelColor);
            var arrow = (RectTransform)arrowText.transform;
            arrow.anchorMin = new Vector2(1f, 0.5f);
            arrow.anchorMax = new Vector2(1f, 0.5f);
            arrow.pivot = new Vector2(0.5f, 0.5f);
            arrow.sizeDelta = new Vector2(28f, RowHeight);
            arrow.anchoredPosition = new Vector2(-14f, 0f);
            arrowText.alignment = TextAnchor.MiddleCenter;

            // Template (open list) — STANDARD uGUI structure: anchored to the
            // dropdown's bottom, pivot top. uGUI's Show() clones it into its own
            // popup canvas overlaying everything, sizes it, blocks clicks, and
            // closes on selection. Max 12 visible rows, scrolls beyond that.
            var templateGo = new GameObject("Template", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            var template = (RectTransform)templateGo.transform;
            template.SetParent(rect, false);
            template.anchorMin = new Vector2(0f, 0f);
            template.anchorMax = new Vector2(1f, 0f);
            template.pivot = new Vector2(0.5f, 1f);
            template.anchoredPosition = new Vector2(0f, 2f);
            // Fixed 12-row height: sized at build time the option list is still
            // the 1-row placeholder, so an options-derived height would stay 1
            // row forever (uGUI's extraSpace shrink keeps it). 12 rows max;
            // longer lists scroll inside the template's ScrollRect.
            template.sizeDelta = new Vector2(0f, 12 * 34f + 8f);
            templateGo.GetComponent<Image>().color = new Color(0.06f, 0.06f, 0.07f, 0.98f);
            templateGo.SetActive(false);

            var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            var viewport = (RectTransform)viewportGo.transform;
            viewport.SetParent(template, false);
            Stretch(viewport);
            viewport.offsetMin = new Vector2(0f, 2f);
            viewport.offsetMax = new Vector2(0f, -2f);
            viewportGo.GetComponent<Image>().color = Color.clear;

            var contentGo = new GameObject("Content", typeof(RectTransform));
            var content = (RectTransform)contentGo.transform;
            content.SetParent(viewport, false);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, options.Length * 34f);

            // Item prototype (Toggle) — the Dropdown clones this per option.
            var itemGo = new GameObject("Item", typeof(RectTransform), typeof(Toggle));
            var item = (RectTransform)itemGo.transform;
            item.SetParent(content, false);
            SetRect(item, new Vector2(0f, -17f), new Vector2(0f, 34f));
            item.anchorMin = new Vector2(0f, 1f);
            item.anchorMax = new Vector2(1f, 1f);

            var itemBgGo = new GameObject("Item Background", typeof(RectTransform), typeof(Image));
            var itemBg = (RectTransform)itemBgGo.transform;
            itemBg.SetParent(item, false);
            Stretch(itemBg);
            itemBgGo.GetComponent<Image>().color = new Color(0.16f, 0.18f, 0.22f, 1f); // highlighted state

            var itemCheckGo = new GameObject("Item Checkmark", typeof(RectTransform), typeof(Image));
            var itemCheck = (RectTransform)itemCheckGo.transform;
            itemCheck.SetParent(item, false);
            itemCheck.anchorMin = new Vector2(0f, 0.5f);
            itemCheck.anchorMax = new Vector2(0f, 0.5f);
            itemCheck.pivot = new Vector2(0f, 0.5f);
            itemCheck.sizeDelta = new Vector2(18f, 18f);
            itemCheck.anchoredPosition = new Vector2(6f, 0f);
            itemCheckGo.GetComponent<Image>().color = Accent;

            var itemText = CreateText(item, "", 17, TextColor);
            var itemLabel = (RectTransform)itemText.transform;
            itemLabel.anchorMin = Vector2.zero;
            itemLabel.anchorMax = Vector2.one;
            itemLabel.offsetMin = new Vector2(32f, 0f);
            itemLabel.offsetMax = new Vector2(-8f, 0f);
            itemText.alignment = TextAnchor.MiddleLeft;

            var scrollRect = templateGo.GetComponent<ScrollRect>();
            scrollRect.viewport = viewport;
            scrollRect.content = content;
            scrollRect.scrollSensitivity = 12f;

            var dropdown = go.GetComponent<Dropdown>();
            dropdown.targetGraphic = bgImg;
            dropdown.transition = Selectable.Transition.None;
            dropdown.captionText = captionText;
            dropdown.itemText = itemText;
            dropdown.template = template;
            dropdown.value = 0;
            dropdown.ClearOptions();
            dropdown.AddOptions(new List<string>(options));
            dropdown.RefreshShownValue();

            // uGUI's Dropdown initializes its alpha-tween runner in Start().
            // These dropdowns are created at runtime, and Show() in the same
            // frame would NRE. Force the lifecycle now: disable + re-enable
            // runs OnEnable/Start immediately.
            go.SetActive(false);
            go.SetActive(true);
            return dropdown;
        }

        public static Slider CreateSlider(RectTransform parent, string label, Vector2 center, float width, Func<float, string> format)
        {
            // Label row above the control.
            var labelText = CreateText(parent, label, 17, LabelColor);
            SetRect((RectTransform)labelText.transform, center + new Vector2(0f, LabelOffset), new Vector2(width, 22f));
            labelText.alignment = TextAnchor.LowerLeft;

            // Value text on the right end of the label row.
            var valueText = CreateText(parent, "", 16, TextColor);
            var valueRect = (RectTransform)valueText.transform;
            valueRect.anchorMin = new Vector2(1f, 0.5f);
            valueRect.anchorMax = new Vector2(1f, 0.5f);
            valueRect.pivot = new Vector2(1f, 0.5f);
            valueRect.sizeDelta = new Vector2(140f, 22f);
            valueRect.anchoredPosition = center + new Vector2(0f, LabelOffset);
            valueText.alignment = TextAnchor.LowerRight;

            var go = new GameObject("Slider_" + label, typeof(RectTransform), typeof(Slider));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            SetRect(rect, center, new Vector2(width, SliderHeight));

            // Background track.
            var bgGo = new GameObject("Background", typeof(RectTransform), typeof(Image));
            var bg = (RectTransform)bgGo.transform;
            bg.SetParent(rect, false);
            Stretch(bg);
            var bgImg = bgGo.GetComponent<Image>();
            bgImg.color = ControlBg;
            var outline = bgGo.AddComponent<Outline>();
            outline.effectColor = Color.white;
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            // Fill area.
            var fillAreaGo = new GameObject("Fill Area", typeof(RectTransform));
            var fillArea = (RectTransform)fillAreaGo.transform;
            fillArea.SetParent(rect, false);
            fillArea.anchorMin = new Vector2(0f, 0.25f);
            fillArea.anchorMax = new Vector2(1f, 0.75f);
            fillArea.offsetMin = new Vector2(4f, 0f);
            fillArea.offsetMax = new Vector2(-4f, 0f);
            var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            var fill = (RectTransform)fillGo.transform;
            fill.SetParent(fillArea, false);
            Stretch(fill);
            fillGo.GetComponent<Image>().color = new Color(0.78f, 0.78f, 0.78f, 1f);

            // Handle area.
            var handleAreaGo = new GameObject("Handle Slide Area", typeof(RectTransform));
            var handleArea = (RectTransform)handleAreaGo.transform;
            handleArea.SetParent(rect, false);
            handleArea.anchorMin = new Vector2(0f, 0f);
            handleArea.anchorMax = new Vector2(1f, 1f);
            handleArea.offsetMin = new Vector2(10f, 0f);
            handleArea.offsetMax = new Vector2(-10f, 0f);
            var handleGo = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            var handle = (RectTransform)handleGo.transform;
            handle.SetParent(handleArea, false);
            handle.anchorMin = new Vector2(0.5f, 0f);
            handle.anchorMax = new Vector2(0.5f, 1f);
            handle.sizeDelta = new Vector2(12f, 0f);
            var handleImg = handleGo.GetComponent<Image>();
            handleImg.color = new Color(0.9f, 0.93f, 0.96f, 1f);

            var slider = go.GetComponent<Slider>();
            slider.targetGraphic = handleImg;
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = 0.5f;

            // Live value label (refreshed on drag AND on programmatic init).
            var updater = go.AddComponent<SliderValueText>();
            updater.Text = valueText;
            updater.Slider = slider;
            updater.Format = format;
            return slider;
        }

        /// <summary>
        /// Horizontal mic level bar (MonoGame DrawMicMeter visual): dark track,
        /// white border, green/amber/red fill driven by SetLevel(0..1), plus a
        /// right-side percent label.
        /// </summary>
        public sealed class MicMeterBar : MonoBehaviour
        {
            public Text PercentText;
            private RectTransform _fill;
            private Image _fillImage;

            private void Awake()
            {
                CacheFill();
            }

            private void CacheFill()
            {
                if (_fill != null)
                    return;
                // Fill lives under Background (built by CreateMeter). This
                // component is created in the GameObject constructor BEFORE
                // the children exist, so Awake caches null — hence lazy cache.
                var bg = transform.Find("Background");
                _fill = (bg != null ? bg.Find("Fill") : transform.Find("Fill")) as RectTransform;
                if (_fill != null) _fillImage = _fill.GetComponent<Image>();
            }

            public void SetLevel(float level)
            {
                level = Mathf.Clamp01(level);
                CacheFill();
                if (_fill != null)
                {
                    // Left-anchored fill: stretch height, set width by level.
                    _fill.anchorMin = new Vector2(0f, 0f);
                    _fill.anchorMax = new Vector2(0f, 1f);
                    _fill.pivot = new Vector2(0f, 0.5f);
                    float w = ((RectTransform)_fill.parent).rect.width - 4f;
                    _fill.sizeDelta = new Vector2(Mathf.Max(0f, level * w), 0f);
                    _fill.anchoredPosition = new Vector2(2f, 0f);
                    if (_fillImage != null)
                        _fillImage.color = LevelColor(level);
                }
                if (PercentText != null)
                    PercentText.text = Mathf.RoundToInt(level * 100f) + "%";
            }

            /// <summary>Green → blue-cyan → amber → red gradient by level.</summary>
            private static Color LevelColor(float level)
            {
                if (level < 0.45f)
                    return Color.Lerp(new Color(0.20f, 0.85f, 0.45f), new Color(0.25f, 0.70f, 0.95f), level / 0.45f);
                if (level < 0.70f)
                    return Color.Lerp(new Color(0.25f, 0.70f, 0.95f), new Color(0.90f, 0.78f, 0.25f), (level - 0.45f) / 0.25f);
                return Color.Lerp(new Color(0.90f, 0.78f, 0.25f), new Color(0.88f, 0.25f, 0.22f), (level - 0.70f) / 0.30f);
            }
        }

        private static MicMeterBar CreateMeter(RectTransform parent, Vector2 center, float width)
        {
            var go = new GameObject("MicMeter", typeof(RectTransform), typeof(MicMeterBar));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            SetRect(rect, center, new Vector2(width, SliderHeight));

            var bgGo = new GameObject("Background", typeof(RectTransform), typeof(Image));
            var bg = (RectTransform)bgGo.transform;
            bg.SetParent(rect, false);
            Stretch(bg);
            var bgImg = bgGo.GetComponent<Image>();
            bgImg.color = new Color(0.07f, 0.07f, 0.07f, 1f);
            var outline = bgGo.AddComponent<Outline>();
            outline.effectColor = Color.white;
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            var fill = (RectTransform)fillGo.transform;
            fill.SetParent(bg, false);
            fill.anchorMin = new Vector2(0f, 0f);
            fill.anchorMax = new Vector2(0f, 1f);
            fill.pivot = new Vector2(0f, 0.5f);
            fill.anchoredPosition = new Vector2(2f, 0f);
            fill.sizeDelta = new Vector2(0f, -4f);
            fillGo.GetComponent<Image>().color = new Color(0.24f, 0.86f, 0.47f);

            var percentText = CreateText(rect, "0%", 15, TextColor);
            var pctRect = (RectTransform)percentText.transform;
            pctRect.anchorMin = new Vector2(1f, 0.5f);
            pctRect.anchorMax = new Vector2(1f, 0.5f);
            pctRect.pivot = new Vector2(0f, 0.5f);
            pctRect.sizeDelta = new Vector2(60f, 20f);
            pctRect.anchoredPosition = new Vector2(10f, 0f);
            percentText.alignment = TextAnchor.MiddleLeft;

            var bar = go.GetComponent<MicMeterBar>();
            bar.PercentText = percentText;
            bar.SetLevel(0f);
            return bar;
        }

        /// <summary>
        /// Drags anywhere inside the viewport scroll the content (uGUI's
        /// ScrollRect only drags from its own target; Sliders/Toggles/Dropdowns
        /// would otherwise eat the gesture).
        /// </summary>
        public sealed class ScrollAnywhereDrag : MonoBehaviour, UnityEngine.EventSystems.IBeginDragHandler,
            UnityEngine.EventSystems.IDragHandler, UnityEngine.EventSystems.IEndDragHandler,
            UnityEngine.EventSystems.IScrollHandler
        {
            public ScrollRect Scroll;

            public void OnBeginDrag(UnityEngine.EventSystems.PointerEventData e) => Scroll?.OnBeginDrag(e);
            public void OnDrag(UnityEngine.EventSystems.PointerEventData e) => Scroll?.OnDrag(e);
            public void OnEndDrag(UnityEngine.EventSystems.PointerEventData e) => Scroll?.OnEndDrag(e);
            public void OnScroll(UnityEngine.EventSystems.PointerEventData e) => Scroll?.OnScroll(e);
        }

        /// <summary>Keeps a slider's right-side value label in sync.</summary>
        public sealed class SliderValueText : MonoBehaviour
        {
            public Text Text;
            public Slider Slider;
            public Func<float, string> Format;

            private void Awake()
            {
                if (Slider != null)
                    Slider.onValueChanged.AddListener(_ => Refresh());
            }

            private void Start()
            {
                Refresh();
            }

            public void Refresh()
            {
                if (Text != null && Format != null)
                    Text.text = Format(Mathf.Clamp01(Slider.value));
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        public static Text CreateText(RectTransform parent, string content, int size, Color color)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.font = OptionsFont;
            // Crisper glyphs: larger source size scaled down renders cleaner
            // than a small size scaled up (no blur when the canvas scales).
            text.fontSize = Mathf.Max(size, 14);
            text.color = color;
            text.alignment = TextAnchor.MiddleCenter;
            text.text = content;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = false;
            return text;
        }

        private static void SetRect(RectTransform rect, Vector2 center, Vector2 size)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = center;
            rect.sizeDelta = size;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static Font _font;
        private static Font OptionsFont => _font != null
            ? _font
            : _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }
}
