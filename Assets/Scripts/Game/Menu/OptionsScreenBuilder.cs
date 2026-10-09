using LatticeVeil.Core;
using UnityEngine;
using UnityEngine.UI;

namespace LatticeVeil.Game
{
    /// <summary>
    /// Options screen built from STANDARD Unity uGUI components:
    /// Toggle, Slider, Dropdown (no custom widget code). A vertical layout
    /// group inside the framed panel keeps everything in place at any
    /// resolution; the GUI CanvasScaler handles scaling.
    /// </summary>
    public static class OptionsScreenBuilder
    {
        public const float PanelWidth = 1080f;
        public const float PanelHeight = 600f;

        private static readonly Color TextColor = Color.white;
        private static readonly Color LabelColor = new Color(0.86f, 0.89f, 0.92f, 1f);
        private static readonly Color Accent = new Color(0.55f, 0.8f, 1f, 1f);

        /// <summary>Builds the standard-component page content under the panel.</summary>
        public static GameObject BuildPage(RectTransform panel, OptionsScreenController controller)
        {
            var root = new GameObject("StandardPage", typeof(RectTransform));
            var rect = (RectTransform)root.transform;
            rect.SetParent(panel, false);
            Stretch(rect);

            // Two columns of rows, driven by a vertical layout group each.
            var left = CreateColumn(rect, 0);
            var right = CreateColumn(rect, 1);

            // ---- Left column (standard components) ----
            var fsToggle = CreateToggle(left, "FULLSCREEN");
            var vsToggle = CreateToggle(left, "VSYNC");
            var resDropdown = CreateDropdown(left, "RESOLUTION",
                new[] { "1280 x 720", "1920 x 1080", "2560 x 1440", "3840 x 2160" });

            // ---- Right column ----
            var guiDropdown = CreateDropdown(right, "GUI SCALE",
                new[] { "STANDARD", "LARGE", "LARGER" });
            var qualityDropdown = CreateDropdown(right, "QUALITY",
                new[] { "LOW", "MEDIUM", "HIGH", "ULTRA" });
            var brightness = CreateSlider(right, "BRIGHTNESS");
            var fov = CreateSlider(right, "FOV");
            var renderDistance = CreateSlider(right, "RENDER DISTANCE");

            controller.BindStandardWidgets(fsToggle, vsToggle, resDropdown, guiDropdown,
                qualityDropdown, brightness, fov, renderDistance);

            return root;
        }

        private static RectTransform CreateColumn(RectTransform parent, int column)
        {
            var go = new GameObject("Column" + column, typeof(RectTransform), typeof(VerticalLayoutGroup));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(column == 0 ? 0.06f : 0.53f, 0.08f);
            rect.anchorMax = new Vector2(column == 0 ? 0.47f : 0.94f, 0.92f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var group = go.GetComponent<VerticalLayoutGroup>();
            group.childAlignment = TextAnchor.UpperLeft;
            group.spacing = 18f;
            group.childForceExpandHeight = false;
            group.childForceExpandWidth = true;
            group.childControlHeight = true;
            group.childControlWidth = true;
            group.padding = new RectOffset(0, 0, 8, 0);
            return rect;
        }

        // ------------------------------------------------------------------
        // Standard uGUI factories
        // ------------------------------------------------------------------

        public static Toggle CreateToggle(RectTransform column, string label)
        {
            var go = new GameObject("Toggle_" + label, typeof(RectTransform), typeof(Toggle));
            var rect = (RectTransform)go.transform;
            rect.SetParent(column, false);
            var layout = go.AddComponent<LayoutElement>();
            layout.minHeight = 34f;
            layout.preferredHeight = 34f;

            var toggle = go.GetComponent<Toggle>();

            // Background box (standard Toggle structure).
            var bgGo = new GameObject("Background", typeof(RectTransform), typeof(Image));
            var bg = (RectTransform)bgGo.transform;
            bg.SetParent(rect, false);
            bg.anchorMin = new Vector2(0f, 0.5f);
            bg.anchorMax = new Vector2(0f, 0.5f);
            bg.pivot = new Vector2(0f, 0.5f);
            bg.sizeDelta = new Vector2(56, 28);
            bg.anchoredPosition = Vector2.zero;
            bgGo.GetComponent<Image>().color = new Color(0.08f, 0.08f, 0.08f);
            var bgOutline = bgGo.AddComponent<Outline>();
            bgOutline.effectColor = Color.white;

            var checkGo = new GameObject("Checkmark", typeof(RectTransform), typeof(Image));
            var check = (RectTransform)checkGo.transform;
            check.SetParent(bg, false);
            Stretch(check);
            var checkImg = checkGo.GetComponent<Image>();
            checkImg.color = Accent;

            var labelGo = new GameObject("Label", typeof(RectTransform));
            var labelRect = (RectTransform)labelGo.transform;
            labelRect.SetParent(rect, false);
            labelRect.anchorMin = new Vector2(0f, 0.5f);
            labelRect.anchorMax = new Vector2(0f, 0.5f);
            labelRect.pivot = new Vector2(0f, 0.5f);
            labelRect.sizeDelta = new Vector2(360, 30);
            labelRect.anchoredPosition = new Vector2(70, 0);
            var text = labelGo.AddComponent<Text>();
            text.font = OptionsFont;
            text.fontSize = 19;
            text.color = TextColor;
            text.alignment = TextAnchor.MiddleLeft;
            text.text = label;
            text.raycastTarget = false;

            toggle.targetGraphic = bgGo.GetComponent<Image>();
            toggle.graphic = checkImg;
            toggle.isOn = false;
            return toggle;
        }

        public static Dropdown CreateDropdown(RectTransform column, string label, string[] options)
        {
            // Label row first (standard label-above-control pattern).
            CreateRowLabel(column, label);

            var go = new GameObject("Dropdown_" + label, typeof(RectTransform), typeof(Image), typeof(Dropdown));
            var rect = (RectTransform)go.transform;
            rect.SetParent(column, false);
            var layout = go.AddComponent<LayoutElement>();
            layout.minHeight = 36f;
            layout.preferredHeight = 36f;

            var bgImg = go.GetComponent<Image>();
            bgImg.color = new Color(0.08f, 0.08f, 0.08f);
            var outline = go.AddComponent<Outline>();
            outline.effectColor = Color.white;

            // Caption label (selected value).
            var captionGo = new GameObject("Caption", typeof(RectTransform));
            var caption = (RectTransform)captionGo.transform;
            caption.SetParent(rect, false);
            caption.anchorMin = new Vector2(0f, 0.5f);
            caption.anchorMax = new Vector2(1f, 0.5f);
            caption.offsetMin = new Vector2(12, -16);
            caption.offsetMax = new Vector2(-36, 16);
            var captionText = captionGo.AddComponent<Text>();
            captionText.font = OptionsFont;
            captionText.fontSize = 18;
            captionText.color = TextColor;
            captionText.alignment = TextAnchor.MiddleLeft;
            captionText.text = options.Length > 0 ? options[0] : "";

            // Arrow.
            var arrowGo = new GameObject("Arrow", typeof(RectTransform));
            var arrow = (RectTransform)arrowGo.transform;
            arrow.SetParent(rect, false);
            arrow.anchorMin = new Vector2(1f, 0.5f);
            arrow.anchorMax = new Vector2(1f, 0.5f);
            arrow.pivot = new Vector2(1f, 0.5f);
            arrow.sizeDelta = new Vector2(28, 28);
            arrow.anchoredPosition = new Vector2(-8, 0);
            var arrowText = arrowGo.AddComponent<Text>();
            arrowText.font = OptionsFont;
            arrowText.fontSize = 14;
            arrowText.color = LabelColor;
            arrowText.alignment = TextAnchor.MiddleCenter;
            arrowText.text = "\u25BC";
            arrowText.raycastTarget = false;

            // Template (the open list). Standard Dropdown structure.
            var templateGo = new GameObject("Template", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            var template = (RectTransform)templateGo.transform;
            template.SetParent(rect, false);
            template.anchorMin = new Vector2(0f, 0f);
            template.anchorMax = new Vector2(1f, 0f);
            template.pivot = new Vector2(0.5f, 1f);
            template.anchoredPosition = new Vector2(0, 2);
            template.sizeDelta = new Vector2(0, 160);
            templateGo.GetComponent<Image>().color = new Color(0.06f, 0.06f, 0.07f, 0.98f);
            templateGo.SetActive(false);

            var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            var viewport = (RectTransform)viewportGo.transform;
            viewport.SetParent(template, false);
            Stretch(viewport);
            viewportGo.GetComponent<Image>().color = Color.clear;

            var contentGo = new GameObject("Content", typeof(RectTransform));
            var content = (RectTransform)contentGo.transform;
            content.SetParent(viewport, false);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0, 120);
            var contentGroup = contentGo.AddComponent<VerticalLayoutGroup>();
            contentGroup.childForceExpandHeight = false;
            contentGroup.childForceExpandWidth = true;
            contentGroup.childControlHeight = false;
            contentGroup.childControlWidth = true;
            contentGroup.spacing = 2f;

            // Item prototype (Toggle) — the Dropdown clones this per option.
            var itemGo = new GameObject("Item", typeof(RectTransform), typeof(Toggle));
            var item = (RectTransform)itemGo.transform;
            item.SetParent(content, false);
            var itemLayout = itemGo.AddComponent<LayoutElement>();
            itemLayout.minHeight = 32f;
            itemLayout.preferredHeight = 32f;
            var itemBgGo = new GameObject("Item Background", typeof(RectTransform), typeof(Image));
            var itemBg = (RectTransform)itemBgGo.transform;
            itemBg.SetParent(item, false);
            Stretch(itemBg);
            itemBgGo.GetComponent<Image>().color = Color.clear;
            var itemLabelGo = new GameObject("Item Label", typeof(RectTransform));
            var itemLabel = (RectTransform)itemLabelGo.transform;
            itemLabel.SetParent(item, false);
            Stretch(itemLabel);
            itemLabel.offsetMin = new Vector2(12, 0);
            itemLabel.offsetMax = new Vector2(-8, 0);
            var itemText = itemLabelGo.AddComponent<Text>();
            itemText.font = OptionsFont;
            itemText.fontSize = 17;
            itemText.color = TextColor;
            itemText.alignment = TextAnchor.MiddleLeft;

            var dropdown = go.GetComponent<Dropdown>();
            dropdown.targetGraphic = bgImg;
            dropdown.captionText = captionText;
            dropdown.itemText = itemText;
            dropdown.template = template;
            dropdown.value = 0;
            dropdown.ClearOptions();
            dropdown.AddOptions(new System.Collections.Generic.List<string>(options));
            dropdown.RefreshShownValue();
            return dropdown;
        }

        public static Slider CreateSlider(RectTransform column, string label)
        {
            CreateRowLabel(column, label);

            var go = new GameObject("Slider_" + label, typeof(RectTransform), typeof(Slider));
            var rect = (RectTransform)go.transform;
            rect.SetParent(column, false);
            var layout = go.AddComponent<LayoutElement>();
            layout.minHeight = 20f;
            layout.preferredHeight = 20f;

            // Background track.
            var bgGo = new GameObject("Background", typeof(RectTransform), typeof(Image));
            var bg = (RectTransform)bgGo.transform;
            bg.SetParent(rect, false);
            Stretch(bg);
            var bgImg = bgGo.GetComponent<Image>();
            bgImg.color = new Color(0.08f, 0.08f, 0.08f);
            var outline = bgGo.AddComponent<Outline>();
            outline.effectColor = Color.white;

            // Fill area.
            var fillAreaGo = new GameObject("Fill Area", typeof(RectTransform));
            var fillArea = (RectTransform)fillAreaGo.transform;
            fillArea.SetParent(rect, false);
            fillArea.anchorMin = new Vector2(0f, 0.25f);
            fillArea.anchorMax = new Vector2(1f, 0.75f);
            fillArea.offsetMin = new Vector2(4, 0);
            fillArea.offsetMax = new Vector2(-4, 0);
            var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            var fill = (RectTransform)fillGo.transform;
            fill.SetParent(fillArea, false);
            Stretch(fill);
            fillGo.GetComponent<Image>().color = new Color(0.78f, 0.78f, 0.78f);

            // Handle area.
            var handleAreaGo = new GameObject("Handle Slide Area", typeof(RectTransform));
            var handleArea = (RectTransform)handleAreaGo.transform;
            handleArea.SetParent(rect, false);
            handleArea.anchorMin = new Vector2(0f, 0f);
            handleArea.anchorMax = new Vector2(1f, 1f);
            handleArea.offsetMin = new Vector2(10, 0);
            handleArea.offsetMax = new Vector2(-10, 0);
            var handleGo = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            var handle = (RectTransform)handleGo.transform;
            handle.SetParent(handleArea, false);
            handle.sizeDelta = new Vector2(14, 0);
            var handleImg = handleGo.GetComponent<Image>();
            handleImg.color = new Color(0.78f, 0.78f, 0.78f);

            var slider = go.GetComponent<Slider>();
            slider.targetGraphic = handleImg;
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = 0.5f;
            return slider;
        }

        private static void CreateRowLabel(RectTransform column, string label)
        {
            var go = new GameObject("RowLabel_" + label, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(column, false);
            var layout = go.AddComponent<LayoutElement>();
            layout.minHeight = 24f;
            layout.preferredHeight = 24f;
            var text = go.AddComponent<Text>();
            text.font = OptionsFont;
            text.fontSize = 18;
            text.color = LabelColor;
            text.alignment = TextAnchor.LowerLeft;
            text.text = label;
            text.raycastTarget = false;
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
