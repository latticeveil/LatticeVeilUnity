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

        /// <summary>Builds the Audio page under the panel.</summary>
        public static RectTransform BuildAudioPage(RectTransform page, OptionsScreenController controller,
            out Slider master, out Slider music, out Slider sfx)
        {
            var root = new GameObject("StandardPageAudio", typeof(RectTransform));
            var rect = (RectTransform)root.transform;
            rect.SetParent(page, false);
            Stretch(rect);
            var rows = CreateRows(rect);

            master = CreateSlider(rows, "MASTER", new Vector2(ColumnXLeft, 127f), ColumnWidth, v => Mathf.RoundToInt(v * 100f) + "%");
            music = CreateSlider(rows, "MUSIC", new Vector2(ColumnXLeft, 51f), ColumnWidth, v => Mathf.RoundToInt(v * 100f) + "%");
            sfx = CreateSlider(rows, "SFX", new Vector2(ColumnXLeft, -25f), ColumnWidth, v => Mathf.RoundToInt(v * 100f) + "%");

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

        public static Dropdown CreateDropdown(RectTransform parent, string label, Vector2 center, float width, string[] options)
        {
            return CreateDropdownImpl(parent, label, center, width, options);
        }

        private static Dropdown CreateDropdownImpl(RectTransform parent, string label, Vector2 center, float width, string[] options)
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

            // Template (open list): standard dropdown structure, positioned
            // below the control per the uGUI docs (anchor bottom, pivot top).
            var templateGo = new GameObject("Template", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            var template = (RectTransform)templateGo.transform;
            template.SetParent(rect, false);
            template.anchorMin = new Vector2(0f, 0f);
            template.anchorMax = new Vector2(1f, 0f);
            template.pivot = new Vector2(0.5f, 1f);
            template.anchoredPosition = new Vector2(0f, 2f);
            template.sizeDelta = new Vector2(0f, options.Length * 34f + 8f);
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
