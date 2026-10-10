using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using LatticeVeil.Core;

namespace LatticeVeil.Game
{
    /// <summary>
    /// PACKS & ADDONS page (ports the MonoGame SettingsScreen addon panel):
    ///  - Sub-tabs MODS / REPACKS with an explanatory banner
    ///  - Action buttons: IMPORT (file dialog), REFRESH, OPEN <MODS|REPACKS>
    ///  - Draggable list of discovered addons; each row has an enable toggle
    ///    persisted to options (EnabledMods / EnabledPacks)
    ///  - Empty state shows the drag & drop info box with an IMPORT button
    /// Discovery follows the MonoGame rules: folders containing manifest.lvc /
    /// pack.lvc under Documents/LatticeVeil/{Mods,Packs}, plus .lvmod/.lvpack
    /// ZIPs.
    /// </summary>
    public static class AddonsScreenBuilder
    {
        // Same content metrics as the other pages (panel-local units).
        public const float ContentScale = 0.84f;
        private const float RowHeight = 34f;
        private const float ColumnWidth = 700f;

        private static readonly Color Accent = new Color(0.55f, 0.8f, 1f, 1f);
        private static readonly Color TextColor = Color.white;
        private static readonly Color SubText = new Color(0.63f, 0.67f, 0.73f, 1f);
        private static readonly Color EnabledBorder = new Color(80f / 255f, 190f / 255f, 100f / 255f, 1f);
        private static readonly Color BannerMods = new Color(40f / 255f, 50f / 255f, 65f / 255f, 0.8f);
        private static readonly Color BannerPacks = new Color(50f / 255f, 40f / 255f, 35f / 255f, 0.8f);

        /// <summary>true = MODS sub-tab, false = REPACKS.</summary>
        private static bool _isMods = true;

        private static Dropdown _subTabDropdown;
        private static RectTransform _listRoot;
        private static Text _bannerText;
        private static Image _bannerBackground;
        private static Button _openFolderBtn;
        private static Text _openFolderLabel;
        private static Text _statusText;
        private static AddonsPageController _pageController;

        /// <summary>State shared with the page controller.</summary>
        private static GameSettings _settings;
        private static Action _saveSettings;

        public static RectTransform Build(RectTransform page, OptionsScreenController controller)
        {
            var root = new GameObject("StandardPageAddons", typeof(RectTransform));
            var rect = (RectTransform)root.transform;
            rect.SetParent(page, false);
            Stretch(rect);

            // _settings comes from BindSettings (controller-owned instance so
            // toggles save through the same object the rest of the screen
            // writes). Fall back to loading one for standalone use.
            if (_settings == null)
                _settings = GameSettings.LoadOrCreate(null);
            _isMods = true;

            // ---- Sub-tab row: MODS | REPACKS (styled boxes like MonoGame) ----
            _subTabDropdown = CreateSubTabSwitch(rect, new Vector2(0f, 196f));
            _subTabDropdown.onValueChanged.AddListener(_ => OnSubTabChanged());

            // ---- Explanatory banner ----
            _bannerText = CreateBanner(rect, new Vector2(0f, 158f));

            // ---- Action row: IMPORT | REFRESH | OPEN MODS (centered) ----
            CreateActionButton(rect, "IMPORT", new Vector2(-170f, 118f), Color.white, DoImport);
            CreateActionButton(rect, "REFRESH", new Vector2(0f, 118f), Color.white, DoRefresh);
            var openGo = CreateActionButton(rect, "OPEN MODS", new Vector2(170f, 118f), Color.white, DoOpenFolder);
            _openFolderBtn = openGo.GetComponent<Button>();
            _openFolderLabel = openGo.GetComponentInChildren<Text>();

            // ---- Status text (install progress / errors), thin line below ----
            _statusText = CreateLabel(rect, "", 14, new Color(0.75f, 0.95f, 0.75f, 1f));
            SetRect((RectTransform)_statusText.transform, new Vector2(0f, 92f), new Vector2(ColumnWidth, 18f));
            _statusText.alignment = TextAnchor.MiddleCenter;

            // ---- Scrollable addon list ----
            var viewportGo = new GameObject("AddonsViewport", typeof(RectTransform), typeof(RectMask2D));
            var viewport = (RectTransform)viewportGo.transform;
            viewport.SetParent(rect, false);
            viewport.anchorMin = new Vector2(0.5f, 0.5f);
            viewport.anchorMax = new Vector2(0.5f, 0.5f);
            viewport.pivot = new Vector2(0.5f, 0.5f);
            viewport.sizeDelta = new Vector2(740f, 320f);
            viewport.anchoredPosition = new Vector2(0f, -88f);

            var scrollGo = viewportGo.AddComponent<ScrollRect>();
            scrollGo.horizontal = false;
            scrollGo.vertical = true;
            scrollGo.movementType = ScrollRect.MovementType.Clamped;
            scrollGo.scrollSensitivity = 28f;

            var contentGo = new GameObject("AddonsContent", typeof(RectTransform));
            var content = (RectTransform)contentGo.transform;
            content.SetParent(viewport, false);
            content.anchorMin = new Vector2(0.5f, 1f);
            content.anchorMax = new Vector2(0.5f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, 360f);

            _listRoot = content;
            scrollGo.content = content;
            scrollGo.viewport = viewport;

            // Drag-anywhere helper (same trick as the Audio page).
            viewportGo.AddComponent<OptionsScreenBuilder.ScrollAnywhereDrag>().Scroll = scrollGo;

            _pageController = root.AddComponent<AddonsPageController>();
            _pageController.Init(refreshAction: () => RefreshAndRebuild());

            var rows = CreateRows(rect);
            // Empty-state hint parked where the list usually is; hidden once
            // any addon exists (RefreshAndRebuild toggles it).
            var empty = CreateLabel(rows, "", 15, SubText);
            empty.name = "AddonsEmptyHint";
            SetRect((RectTransform)empty.transform, new Vector2(0f, -88f), new Vector2(ColumnWidth, 40f));
            empty.alignment = TextAnchor.MiddleCenter;

            RefreshAndRebuild();
            return rect;
        }

        /// <summary>Rows container scaled like the other pages.</summary>
        private static RectTransform CreateRows(RectTransform panel)
        {
            var rowsGo = new GameObject("Rows", typeof(RectTransform));
            var rows = (RectTransform)rowsGo.transform;
            rows.SetParent(panel, false);
            Stretch(rows);
            rows.localScale = Vector3.one * ContentScale;
            return rows;
        }

        // ------------------------------------------------------------------
        // Data: discovery (MonoGame parity)
        // ------------------------------------------------------------------

        public sealed class AddonEntry
        {
            public string Id, Name, Description, Author, Version, Path;
            public bool IsZip, IsMod, Enabled;
        }

        private static readonly List<AddonEntry> _entries = new List<AddonEntry>();

        private static void RefreshAndRebuild()
        {
            RefreshEntries();
            RebuildList();
        }

        private static void RefreshEntries()
        {
            _entries.Clear();
            ScanDirectory(Paths.ModsDir, isMod: true);
            ScanDirectory(Paths.PacksDir, isMod: false);
        }

        private static void ScanDirectory(string dir, bool isMod)
        {
            try
            {
                if (!Directory.Exists(dir))
                    return;
                foreach (var sub in Directory.GetDirectories(dir))
                    _entries.Add(LoadAddonFolder(sub, isMod));
                var ext = isMod ? "*.lvmod" : "*.lvpack";
                foreach (var zip in Directory.GetFiles(dir, ext))
                    _entries.Add(LoadAddonZip(zip, isMod));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Addons scan failed: " + ex.Message);
            }
        }

        private static AddonEntry LoadAddonFolder(string folderPath, bool isMod)
        {
            // manifest.lvc for mods (with pack.lvc fallback), pack.lvc first for packs.
            var manifest = Path.Combine(folderPath, isMod ? "manifest.lvc" : "pack.lvc");
            if (!File.Exists(manifest) && File.Exists(Path.Combine(folderPath, "manifest.lvc")))
                manifest = Path.Combine(folderPath, "manifest.lvc");

            var values = new Dictionary<string, string>();
            if (File.Exists(manifest))
                values = LvcSerializer.ReadFromString(ReadFileText(manifest));

            string Get(string key, string fallback) =>
                values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

            var id = Get("id", Path.GetFileName(folderPath)).ToLowerInvariant();
            return new AddonEntry
            {
                Id = id,
                Name = Get("name", Path.GetFileName(folderPath)),
                Description = Get("description", isMod ? "Custom LatticeVeil Mod" : "Custom Texture / Resource Pack"),
                Author = Get("author", "Community Creator"),
                Version = Get("version", "1.0.0"),
                Path = folderPath,
                IsZip = false,
                IsMod = isMod,
                Enabled = isMod
                    ? _settings.EnabledMods.Contains(id)
                    : _settings.EnabledPacks.Contains(id)
            };
        }

        private static AddonEntry LoadAddonZip(string zipPath, bool isMod)
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var entry = zip.GetEntry(isMod ? "manifest.lvc" : "pack.lvc")
                          ?? zip.GetEntry("manifest.lvc");
                var text = entry != null ? ReadZipEntry(entry) : "";
                var values = LvcSerializer.ReadFromString(text);

                string Get(string key, string fallback) =>
                    values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

                var id = Get("id", Path.GetFileNameWithoutExtension(zipPath)).ToLowerInvariant();
                return new AddonEntry
                {
                    Id = id,
                    Name = Get("name", Path.GetFileNameWithoutExtension(zipPath)),
                    Description = Get("description", isMod ? "Packaged LatticeVeil Mod" : "Packaged Resource Pack"),
                    Author = Get("author", "Community Creator"),
                    Version = Get("version", "1.0.0"),
                    Path = zipPath,
                    IsZip = true,
                    IsMod = isMod,
                    Enabled = isMod
                        ? _settings.EnabledMods.Contains(id)
                        : _settings.EnabledPacks.Contains(id)
                };
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Addons: failed to read " + zipPath + ": " + ex.Message);
                return null;
            }
        }

        private static string ReadFileText(string path)
        {
            try { using var r = new StreamReader(path, Encoding.UTF8); return r.ReadToEnd(); }
            catch { return ""; }
        }

        private static string ReadZipEntry(ZipArchiveEntry entry)
        {
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        // ------------------------------------------------------------------
        // Rebuild list UI
        // ------------------------------------------------------------------

        private static void RebuildList()
        {
            if (_listRoot == null) return;

            float y = 0f;
            // Show only the sub-tab's items (MonoGame filter behavior).
            var items = _entries.FindAll(e => e.IsMod == _isMods);
            // empty hint lives on the page root's Rows container
            UnityEngine.Transform emptyHint = null;
            var page = _listRoot.GetComponentInParent<Transform>().root;
            foreach (var t2 in UnityEngine.Object.FindObjectsOfType<Transform>(true))
                if (t2.name == "AddonsEmptyHint" && t2.parent != null && t2.parent.name == "Rows") { emptyHint = t2; break; }

            for (int i = _listRoot.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_listRoot.GetChild(i).gameObject);

            var content = _listRoot;
            float totalH = Mathf.Max(360f, items.Count * 66f + 8f);
            content.sizeDelta = new Vector2(0f, totalH);

            if (emptyHint != null)
            {
                var label = emptyHint.GetComponent<Text>();
                if (items.Count == 0)
                {
                    label.text = _isMods
                        ? "NO MODS FOUND.\nDRAG & DROP .LVMOD FILES HERE — OR PRESS IMPORT."
                        : "NO REPACKS FOUND.\nDRAG & DROP .LVPACK FILES HERE — OR PRESS IMPORT.";
                }
                else
                    label.text = "";
            }

            foreach (var entry in items)
            {
                var rowGo = new GameObject("AddonRow_" + entry.Id, typeof(RectTransform), typeof(Image));
                var rowRect = (RectTransform)rowGo.transform;
                rowRect.SetParent(content, false);
                rowRect.anchorMin = new Vector2(0.5f, 1f);
                rowRect.anchorMax = new Vector2(0.5f, 1f);
                rowRect.pivot = new Vector2(0.5f, 1f);
                rowRect.sizeDelta = new Vector2(720f, 60f);
                rowRect.anchoredPosition = new Vector2(0f, -y - 30f);
                y += 66f;

                var bg = rowGo.GetComponent<Image>();
                bg.color = entry.Enabled ? new Color(28f / 255f, 38f / 255f, 30f / 255f, 0.95f)
                                          : new Color(20f / 255f, 22f / 255f, 26f / 255f, 0.95f);
                var outline = rowGo.AddComponent<Outline>();
                outline.effectColor = entry.Enabled ? EnabledBorder : new Color(70f / 255f, 75f / 255f, 85f / 255f, 1f);
                outline.effectDistance = new Vector2(1.5f, -1.5f);

                // Icon stub (initial letter tile)
                var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                var iconRect = (RectTransform)iconGo.transform;
                iconRect.SetParent(rowRect, false);
                iconRect.anchorMin = new Vector2(0f, 0.5f);
                iconRect.anchorMax = new Vector2(0f, 0.5f);
                iconRect.pivot = new Vector2(0f, 0.5f);
                iconRect.sizeDelta = new Vector2(44f, 44f);
                iconRect.anchoredPosition = new Vector2(8f, 0f);
                iconGo.GetComponent<Image>().color = new Color(35f / 255f, 38f / 255f, 45f / 255f, 1f);
                var initialGo = new GameObject("Initial", typeof(RectTransform), typeof(Text));
                var initialRect = (RectTransform)initialGo.transform;
                initialRect.SetParent(iconRect, false);
                Stretch(initialRect);
                var initial = initialGo.GetComponent<Text>();
                initial.font = Font();
                initial.fontSize = 20;
                initial.color = TextColor;
                initial.alignment = TextAnchor.MiddleCenter;
                var initialEntry = entry.Name.Length > 0 ? entry.Name.Substring(0, 1).ToUpperInvariant() : "M";
                TextSetter(initial, initialEntry);

                // Title / author / description (text starts right of the icon;
                // icon occupies the left edge, toggle the right edge)
                var title = CreateLabel(rowRect, entry.Name + "  v" + entry.Version, 16, TextColor);
                SetRect((RectTransform)title.transform, new Vector2(-55f, 12f), new Vector2(420f, 20f));
                title.alignment = TextAnchor.MiddleLeft;

                var author = CreateLabel(rowRect, "by " + entry.Author, 13, SubText);
                SetRect((RectTransform)author.transform, new Vector2(-55f, -6f), new Vector2(420f, 16f));
                author.alignment = TextAnchor.MiddleLeft;

                var desc = CreateLabel(rowRect, entry.Description, 12, SubText);
                SetRect((RectTransform)desc.transform, new Vector2(-55f, -22f), new Vector2(420f, 14f));
                desc.alignment = TextAnchor.MiddleLeft;

                // Enable/disable right-side button
                var toggle = CreateActionButton(rowRect, entry.Enabled ? "ENABLED" : "DISABLED",
                    new Vector2(280f, 0f), TextColor, () => ToggleAddon(entry));
                var tRect = (RectTransform)toggle.transform;
                tRect.sizeDelta = new Vector2(150f, 30f);
            }
        }

        private static void TextSetter(Text t, string v) { t.text = v; t.horizontalOverflow = HorizontalWrapMode.Overflow; }

        private static void ToggleAddon(AddonEntry entry)
        {
            var list = entry.IsMod ? _settings.EnabledMods : _settings.EnabledPacks;
            if (entry.Enabled) list.Remove(entry.Id);
            else list.Add(entry.Id);
            entry.Enabled = !entry.Enabled;

            // Persist through the controller's settings (shared instance).
            _saveSettings?.Invoke();
            RebuildList();
            ShowStatus(entry.Name + (entry.Enabled ? " enabled" : " disabled"));
        }

        private static void ShowStatus(string text)
        {
            if (_statusText != null)
                _statusText.text = text;
        }

        // ------------------------------------------------------------------
        // Actions
        // ------------------------------------------------------------------

        private static void OnSubTabChanged()
        {
            _isMods = _subTabDropdown.value == 0;
            if (_bannerText != null)
                _bannerText.text = _isMods
                    ? "Mods add new features and modify core gameplay aspects"
                    : "Repacks change or replace current loaded textures";
            if (_bannerBackground != null)
                _bannerBackground.color = _isMods ? BannerMods : BannerPacks;
            if (_openFolderLabel != null)
                _openFolderLabel.text = _isMods ? "OPEN MODS" : "OPEN REPACKS";
            RefreshAndRebuild();
        }

        private static void DoImport()
        {
            // Win32 common dialog on a background thread (launcher runs in
            // Windows Player only; mirror SkinManager's P/Invoke approach).
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                var files = FileDialogLauncher.PromptOpenFiles(
                    "Import LatticeVeil Mod or Resource Pack",
                    "LatticeVeil Packages\0*.lvmod;*.lvpack;*.zip\0All Files\0*.*\0");
                if (files == null || files.Length == 0)
                    return;
                UnityMainThread.Enqueue(() =>
                {
                    foreach (var f in files)
                        InstallAddonFile(f);
                    RefreshAndRebuild();
                });
            });
        }

        private static void InstallAddonFile(string sourceFilePath)
        {
            try
            {
                if (!File.Exists(sourceFilePath) && !Directory.Exists(sourceFilePath))
                    return;
                var fileName = Path.GetFileName(sourceFilePath);
                var ext = Path.GetExtension(sourceFilePath).ToLowerInvariant();
                var isMod = ext == ".lvmod" || Directory.Exists(sourceFilePath);
                var targetDir = isMod ? Paths.ModsDir : Paths.PacksDir;
                Directory.CreateDirectory(targetDir);

                ShowStatus("Installing " + fileName + "...");

                if (File.Exists(sourceFilePath))
                    File.Copy(sourceFilePath, Path.Combine(targetDir, fileName), true);
                else if (Directory.Exists(sourceFilePath))
                {
                    var destDir = Path.Combine(targetDir, fileName);
                    CopyDirectoryRecursive(sourceFilePath, destDir);
                }

                ShowStatus("Successfully installed " + fileName + "!");
            }
            catch (Exception ex)
            {
                ShowStatus("Failed to install: " + ex.Message);
                Debug.LogWarning("Addon install failed: " + ex.Message);
            }
        }

        private static void CopyDirectoryRecursive(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(src, dst));
            foreach (var file in Directory.GetFiles(src, "*.*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(src, dst), true);
        }

        private static void DoRefresh()
        {
            RefreshEntries();
            RebuildList();
            ShowStatus("Addons refreshed.");
        }

        private static void DoOpenFolder()
        {
            try
            {
                var dir = _isMods ? Paths.ModsDir : Paths.PacksDir;
                Directory.CreateDirectory(dir);
                // Il2Cpp-safe shell open: cmd start with absolute path.
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                ShowStatus("Could not open folder: " + ex.Message);
            }
        }

        /// <summary>Set at build time by the controller so addon rows can persist
        /// enabled state through the same GameSettings file.</summary>
        public static void BindSettings(GameSettings settings, Action saveAction)
        {
            _settings = settings ?? GameSettings.LoadOrCreate(null);
            _saveSettings = saveAction;
        }

        // ------------------------------------------------------------------
        // uGUI helpers
        // ------------------------------------------------------------------

        /// <summary>Explanatory banner under the sub-tabs (MonoGame parity).</summary>
        private static Text CreateBanner(RectTransform parent, Vector2 center)
        {
            var go = new GameObject("AddonsBanner", typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            SetRect(rect, center, new Vector2(ColumnWidth, 28f));
            var img = go.GetComponent<Image>();
            img.color = BannerMods;
            img.raycastTarget = false;
            var outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(100f / 255f, 120f / 255f, 140f / 255f, 1f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);
            _bannerBackground = img; // swapped by sub-tab toggle

            var t = CreateLabel(rect, "Mods add new features and modify core gameplay aspects", 15,
                new Color(200f / 255f, 210f / 255f, 220f / 255f, 1f));
            Stretch((RectTransform)t.transform);
            t.alignment = TextAnchor.MiddleCenter;
            return t;
        }

        private static Text CreateLabel(RectTransform parent, string content, int size, Color color)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = Font();
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAnchor.MiddleLeft;
            text.text = content;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            return text;
        }

        private static Font _fontCache2;
        private static Font Font()
        {
            if (_fontCache2 == null)
                _fontCache2 = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return _fontCache2;
        }

        private static void SetRect(RectTransform rect, Vector2 center, Vector2 size)
        {
            rect.anchorMin = Vector2.one * 0.5f;
            rect.anchorMax = Vector2.one * 0.5f;
            rect.pivot = Vector2.one * 0.5f;
            rect.sizeDelta = size;
            rect.anchoredPosition = center;
        }

        /// <summary>Square bordered button like MonoGame DrawBorder panels.</summary>
        private static Button CreateActionButton(RectTransform parent, string label, Vector2 center, Color textColor, Action onClick)
        {
            var go = new GameObject("ActionBtn_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            SetRect(rect, center, new Vector2(140f, 30f));
            var img = go.GetComponent<Image>();
            img.color = new Color(24f / 255f, 28f / 255f, 36f / 255f, 0.95f);
            var outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(140f / 255f, 150f / 255f, 165f / 255f, 1f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;

            var t = CreateLabel(rect, label, 15, textColor);
            Stretch((RectTransform)t.transform);
            t.alignment = TextAnchor.MiddleCenter;

            btn.onClick.AddListener(() => onClick());
            return btn;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// Sub-tab switch: a compact two-option
        /// dropdown (MODS / REPACKS) in place of the double bordered boxes,
        /// keeping uGUI widgets only.
        /// </summary>
        private static Dropdown CreateSubTabSwitch(RectTransform parent, Vector2 center)
        {
            var go = new GameObject("SubTabDropdown", typeof(RectTransform), typeof(Image), typeof(Dropdown));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            SetRect(rect, center, new Vector2(300f, 30f));
            var img = go.GetComponent<Image>();
            img.color = new Color(0.08f, 0.08f, 0.09f, 1f);
            var outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(120f / 255f, 132f / 255f, 150f / 255f, 1f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            var dd = go.GetComponent<Dropdown>();
            dd.targetGraphic = img;
            dd.template = null; // built manually below

            var label = CreateLabel(rect, "MODS", 16, TextColor);
            var labelRect = (RectTransform)label.transform;
            labelRect.anchorMin = new Vector2(0f, 0f);
            labelRect.anchorMax = new Vector2(1f, 1f);
            labelRect.offsetMin = new Vector2(14f, 0f);
            labelRect.offsetMax = new Vector2(-40f, 0f);
            label.alignment = TextAnchor.MiddleLeft;
            dd.captionText = label;

            var arrow = CreateLabel(rect, "▼", 13, SubText);
            var arrowRect = (RectTransform)arrow.transform;
            arrowRect.anchorMin = new Vector2(1f, 0f);
            arrowRect.anchorMax = new Vector2(1f, 1f);
            arrowRect.pivot = new Vector2(1f, 0.5f);
            arrowRect.sizeDelta = new Vector2(26f, 0f);
            arrowRect.anchoredPosition = new Vector2(-8f, 0f);
            arrow.alignment = TextAnchor.MiddleCenter;

            var templateGo = new GameObject("Template", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            var template = (RectTransform)templateGo.transform;
            template.SetParent(rect, false);
            template.anchorMin = new Vector2(0f, 0f);
            template.anchorMax = new Vector2(1f, 0f);
            template.pivot = new Vector2(0.5f, 1f);
            template.anchoredPosition = new Vector2(0f, 2f);
            template.sizeDelta = new Vector2(0f, 62f);
            templateGo.GetComponent<Image>().color = new Color(0.08f, 0.08f, 0.09f, 1f);
            templateGo.SetActive(false);
            var tmplOutline = templateGo.AddComponent<Outline>();
            tmplOutline.effectColor = new Color(120f / 255f, 132f / 255f, 150f / 255f, 1f);

            var itemGo = new GameObject("Item", typeof(RectTransform), typeof(Toggle));
            var item = (RectTransform)itemGo.transform;
            item.SetParent(template, false);
            item.anchorMin = new Vector2(0f, 1f);
            item.anchorMax = new Vector2(1f, 1f);
            item.pivot = new Vector2(0.5f, 1f);
            item.sizeDelta = new Vector2(0f, 30f);
            var itemToggle = itemGo.GetComponent<Toggle>();
            itemToggle.targetGraphic = null;

            var itemBg = new GameObject("Item Background", typeof(RectTransform), typeof(Image));
            var itemBgRect = (RectTransform)itemBg.transform;
            itemBgRect.SetParent(item, false);
            Stretch(itemBgRect);
            itemBg.GetComponent<Image>().color = new Color(0.10f, 0.10f, 0.12f, 1f);

            var itemLabel = CreateLabel(item, "", 15, TextColor);
            Stretch((RectTransform)itemLabel.transform);
            ((RectTransform)itemLabel.transform).offsetMin = new Vector2(14f, 0f);
            ((RectTransform)itemLabel.transform).offsetMax = new Vector2(-14f, 0f);
            itemToggle.isOn = false;

            dd.itemText = itemLabel;
            dd.template = template;
            dd.options.Clear();
            dd.options.Add(new Dropdown.OptionData("MODS"));
            dd.options.Add(new Dropdown.OptionData("REPACKS"));
            dd.value = 0;
            dd.RefreshShownValue();

            // spacer item between items so the popup covers both entries
            var item2 = UnityEngine.Object.Instantiate(itemGo, template);
            item2.name = "Item2";
            ((RectTransform)item2.transform).anchoredPosition = new Vector2(0f, -30f);

            return dd;
        }
    }

    /// <summary>
    /// Lives on the Addons page; re-scans folders every time the page is
    /// re-shown (dropdown/tab selection already triggers refreshes, this is a
    /// safety net for external changes while the screen is open).
    /// </summary>
    public sealed class AddonsPageController : MonoBehaviour
    {
        private Action _refresh;
        private bool _subscribed;

        public void Init(Action refreshAction)
        {
            _refresh = refreshAction;
        }

        private void OnEnable()
        {
            // Refresh when the page becomes visible so externally-dropped
            // files (drag & drop into Documents/Mods) show up immediately.
            if (_refresh != null)
                _refresh();
        }
    }

    /// <summary>Windows file dialog via comdlg32 (same P/Invoke as SkinManager),
    /// returning multiple files; UI thread is never blocked.</summary>
    internal static class FileDialogLauncher
    {
        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private struct OpenFileName
        {
            public int structSize;
            public IntPtr hwnd;
            public IntPtr hinst;
            public string filter;
            public string customFilter;
            public int maxCustFilter;
            public int filterIndex;
            public string file;
            public int maxFile;
            public string fileTitle;
            public int maxFileTitle;
            public string initialDir;
            public string title;
            public int flags;
            public short fileOffset;
            public short fileExtension;
            public string defExt;
            public IntPtr custData;
            public IntPtr hook;
            public string templateName;
            public IntPtr reservedPtr;
            public int reservedInt;
            public int flagsEx;
        }

        [System.Runtime.InteropServices.DllImport("comdlg32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern bool GetOpenFileName([In, Out] ref OpenFileName ofn);

        public static string[] PromptOpenFiles(string title, string filter)
        {
            var ofn = new OpenFileName();
            ofn.structSize = System.Runtime.InteropServices.Marshal.SizeOf(ofn);
            ofn.filter = filter;
            ofn.file = new string(new char[4096]);
            ofn.maxFile = ofn.file.Length;
            ofn.fileTitle = new string(new char[512]);
            ofn.maxFileTitle = ofn.fileTitle.Length;
            ofn.initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            ofn.title = title;
            ofn.flags = 0x00080000 | 0x00001000 | 0x00000800 // EXPLORER | FILEMUSTEXIST | PATHMUSTEXIST
                      | 0x00000200 | 0x00000004; // ALLOWMULTISELECT | NOCHANGEDIR

            if (!GetOpenFileName(ref ofn))
                return Array.Empty<string>();

            var raw = ofn.file.TrimEnd('\0');
            if (string.IsNullOrEmpty(raw))
                return Array.Empty<string>();

            if (raw.IndexOf('\0') >= 0)
            {
                // Multi-select: dir then names (comdlg put them one after the other)
                var parts = raw.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
                var dir = parts[0];
                var result = new string[parts.Length - 1];
                for (int i = 1; i < parts.Length; i++)
                    result[i - 1] = Path.Combine(dir, parts[i]);
                return result;
            }
            return new[] { raw };
        }
    }

    /// <summary>Simple main-thread dispatcher for background-thread callbacks
    /// (import dialog results, etc).</summary>
    internal static class UnityMainThread
    {
        private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> _queue = new();

        public static void Enqueue(Action action) => _queue.Enqueue(action);

        internal static void RunPending()
        {
            while (_queue.TryDequeue(out var action))
            {
                try { action(); } catch (Exception ex) { Debug.LogException(ex); }
            }
        }
    }
}
