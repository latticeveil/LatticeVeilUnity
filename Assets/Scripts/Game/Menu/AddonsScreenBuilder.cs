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
    ///  - Sub-tab boxes MODS / REPACKS on the top-left (active one lit)
    ///  - Action buttons on the top-right: TEMPLATE (mods only), IMPORT
    ///    (green accent), REFRESH, OPEN <MODS|REPACKS>
    ///  - Explanatory banner below the row (text swaps per tab)
    ///  - Scrollable list of discovered addons; rows have an enable toggle
    ///    persisted to options (EnabledMods / EnabledPacks)
    ///  - Empty state shows the MonoGame drag & drop info box (upload arrow
    ///    icon + IMPORT button)
    /// Imports only accept the engine's own file types: .lvmod (mods) and
    /// .lvpack (repacks). The file dialog exposes nothing else.
    /// Discovery follows the MonoGame rules: folders containing manifest.lvc /
    /// pack.lvc under Documents/LatticeVeil/{Mods,Packs}, plus .lvmod/.lvpack
    /// ZIPs.
    /// </summary>
    public static class AddonsScreenBuilder
    {
        // Same content metrics as the other pages (panel-local units,
        // y measured from panel center).
        public const float ContentScale = 0.84f;
        private const float RowHeight = 34f;
        private const float ColumnWidth = 700f;

        // Addons header row: border boxes mirror OptionsScreenBuilder.PanelWidth.
        private const float LeftEdge = -424f;   // content left inside frame art
        private const float RightEdge = 424f;   // content right inside frame art
        private const float HeaderY = 178f;     // sub-tab / action row center (inside the panel art, below the frame rail)
        private const float HeaderH = 22f;
        private const float ActionH = 20f;

        // Plain grey palette (MonoGame layout, muted colors).
        private static readonly Color TabActiveBg = new Color(52f / 255f, 58f / 255f, 68f / 255f, 235f / 255f);
        private static readonly Color TabInactiveBg = new Color(24f / 255f, 28f / 255f, 34f / 255f, 220f / 255f);
        private static readonly Color TabActiveBorder = new Color(170f / 255f, 178f / 255f, 190f / 255f, 1f);
        private static readonly Color TabInactiveBorder = new Color(96f / 255f, 104f / 255f, 116f / 255f, 1f);
        private static readonly Color TabActiveText = Color.white;
        private static readonly Color TabInactiveText = new Color(165f / 255f, 172f / 255f, 182f / 255f);

        private static readonly Color Accent = new Color(0.55f, 0.8f, 1f, 1f);
        private static readonly Color TextColor = Color.white;
        private static readonly Color SubText = new Color(0.63f, 0.67f, 0.73f, 1f);
        private static readonly Color EnabledBorder = new Color(96f / 255f, 132f / 255f, 104f / 255f, 1f);
        private static readonly Color EnabledText = new Color(150f / 255f, 205f / 255f, 160f / 255f);
        private static readonly Color ActionBg = new Color(24f / 255f, 28f / 255f, 36f / 255f);
        private static readonly Color ActionBorder = new Color(140f / 255f, 150f / 255f, 165f / 255f);

        /// <summary>true = MODS sub-tab, false = REPACKS.</summary>
        private static bool _isMods = true;

        private static RectTransform _listRoot;
        private static RectTransform _viewport;
        private static Button _openFolderBtn;
        private static Text _openFolderLabel;
        private static Button _importBtn;
        private static Text _importLabel;
        private static Button _modsTabBtn;
        private static Button _packsTabBtn;
        private static GameObject _templateBtn;
        private static Text _statusText;
        private static GameObject _emptyBox;
        private static Text _emptyCaption;
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

            // ---- Header row: MODS|REPACKS boxes left, action buttons right ----
            CreateSubTabBox(rect, "MODS", new Vector2(LeftEdge + 79f, HeaderY), new Vector2(158f, HeaderH), mods: true);
            CreateSubTabBox(rect, "REPACKS", new Vector2(LeftEdge + 79f + 79f + 6f + 70f, HeaderY), new Vector2(140f, HeaderH), mods: false);
            ApplySubTabVisual();

            // Top-right action row (right-aligned to the right content edge).
            _openFolderBtn = CreateActionButton(rect, "OPEN MODS", new Vector2(RightEdge - 62f, HeaderY), new Vector2(124f, ActionH), ActionBg, ActionBorder, SubText, DoOpenFolder);
            _openFolderLabel = _openFolderBtn.GetComponentInChildren<Text>();
            CreateActionButton(rect, "REFRESH", new Vector2(RightEdge - 174f, HeaderY), new Vector2(88f, ActionH), ActionBg, ActionBorder, SubText, DoRefresh);
            var ig = CreateActionButton(rect, "IMPORT", new Vector2(RightEdge - 274f, HeaderY), new Vector2(76f, ActionH), ActionBg, ActionBorder, SubText, DoImport);
            _importBtn = ig;
            _importLabel = ig.GetComponentInChildren<Text>();
            // TEMPLATE: Mods sub-tab only (copies the built-in template mod).
            _templateBtn = CreateActionButton(rect, "TEMPLATE", new Vector2(RightEdge - 374f, HeaderY), new Vector2(88f, ActionH), ActionBg, ActionBorder, SubText, DoTemplate).gameObject;
            _templateBtn.name = "Btn_TEMPLATE";

                        // ---- Status text (install progress / errors), thin line below header ----
            _statusText = CreateLabel(rect, "", 11, new Color(0.62f, 0.72f, 0.66f, 1f));
            SetRect((RectTransform)_statusText.transform, new Vector2(0f, HeaderY - 20f), new Vector2(ColumnWidth, 12f));
            _statusText.alignment = TextAnchor.MiddleCenter;

            // ---- Scrollable addon list ----
            var viewportGo = new GameObject("AddonsViewport", typeof(RectTransform), typeof(RectMask2D));
            _viewport = (RectTransform)viewportGo.transform;
            _viewport.SetParent(rect, false);
            _viewport.anchorMin = new Vector2(0f, 0f);
            _viewport.anchorMax = new Vector2(1f, 1f);
            _viewport.pivot = new Vector2(0.5f, 0.5f);
            // Left/right insets just inside the frame; top clears the header,
            // bottom cut mirrors the Audio page (above the frame's lower art).
            _viewport.offsetMin = new Vector2(56f, 44f);
            _viewport.offsetMax = new Vector2(-56f, -126f);

            var scrollGo = viewportGo.AddComponent<ScrollRect>();
            scrollGo.horizontal = false;
            scrollGo.vertical = true;
            scrollGo.movementType = ScrollRect.MovementType.Clamped;
            scrollGo.scrollSensitivity = 28f;

            // Scrollbar just inside the GUI frame art, on the right edge of
            // the list; clipped to exactly the viewport's top/bottom cuts.
            var scrollbarGo = new GameObject("Scrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
            var scrollbarRect = (RectTransform)scrollbarGo.transform;
            scrollbarRect.SetParent(rect, false);
            scrollbarRect.anchorMin = new Vector2(1f, 0f);
            scrollbarRect.anchorMax = new Vector2(1f, 1f);
            scrollbarRect.pivot = new Vector2(1f, 0.5f);
            scrollbarRect.offsetMin = new Vector2(-56f, 44f);
            scrollbarRect.offsetMax = new Vector2(-42f, -126f);
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
            scrollGo.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            scrollGo.verticalScrollbarSpacing = 4f;

            var contentGo = new GameObject("AddonsContent", typeof(RectTransform));
            var content = (RectTransform)contentGo.transform;
            content.SetParent(_viewport, false);
            content.anchorMin = new Vector2(0.5f, 1f);
            content.anchorMax = new Vector2(0.5f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, 430f);

            _listRoot = content;
            scrollGo.content = content;
            scrollGo.viewport = _viewport;

            // Drag-anywhere helper (same trick as the Audio page).
            viewportGo.AddComponent<OptionsScreenBuilder.ScrollAnywhereDrag>().Scroll = scrollGo;

            _pageController = root.AddComponent<AddonsPageController>();
            _pageController.Init(refreshAction: () => RefreshAndRebuild());

            // ---- Empty state: MonoGame drag&drop info box (built once,
            // toggled by RefreshAndRebuild) ----
            CreateEmptyState(_viewport);

            // Left caption above the box ("NO MODS FOUND." / "NO REPACKS FOUND.").
            _emptyCaption = CreateLabel(_viewport, "", 13, SubText);
            SetRect((RectTransform)_emptyCaption.transform, new Vector2(0f, -42f), new Vector2(848f, 20f));
            _emptyCaption.alignment = TextAnchor.MiddleCenter;
            _emptyCaption.gameObject.name = "AddonsEmptyCaption";

            RefreshAndRebuild();
            return rect;
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

            for (int i = _listRoot.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_listRoot.GetChild(i).gameObject);

            var content = _listRoot;
            float totalH = Mathf.Max(430f, items.Count * 64f + 8f);
            content.sizeDelta = new Vector2(0f, totalH);

            var emptyVisible = items.Count == 0;
            if (_emptyBox != null)
                _emptyBox.SetActive(emptyVisible);
            if (_emptyCaption != null)
            {
                _emptyCaption.gameObject.SetActive(emptyVisible);
                if (emptyVisible)
                    _emptyCaption.text = _isMods ? "NO MODS FOUND." : "NO REPACKS FOUND.";
            }

            foreach (var entry in items)
            {
                var rowGo = new GameObject("AddonRow_" + entry.Id, typeof(RectTransform), typeof(Image));
                var rowRect = (RectTransform)rowGo.transform;
                rowRect.SetParent(content, false);
                rowRect.anchorMin = new Vector2(0.5f, 1f);
                rowRect.anchorMax = new Vector2(0.5f, 1f);
                rowRect.pivot = new Vector2(0.5f, 1f);
                rowRect.sizeDelta = new Vector2(790f, 58f);
                rowRect.anchoredPosition = new Vector2(0f, -y - 29f);
                y += 64f;

                var bg = rowGo.GetComponent<Image>();
                bg.color = new Color(20f / 255f, 22f / 255f, 26f / 255f, 0.95f);
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
                iconRect.sizeDelta = new Vector2(42f, 42f);
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
                SetRect((RectTransform)title.transform, new Vector2(-58f, 12f), new Vector2(430f, 20f));
                title.alignment = TextAnchor.MiddleLeft;

                var author = CreateLabel(rowRect, "by " + entry.Author, 13, SubText);
                SetRect((RectTransform)author.transform, new Vector2(-58f, -6f), new Vector2(430f, 16f));
                author.alignment = TextAnchor.MiddleLeft;

                var desc = CreateLabel(rowRect, entry.Description, 12, SubText);
                SetRect((RectTransform)desc.transform, new Vector2(-58f, -22f), new Vector2(430f, 14f));
                desc.alignment = TextAnchor.MiddleLeft;

                // Enable/disable right-side button
                var toggle = CreateActionButton(rowRect, entry.Enabled ? "ENABLED" : "DISABLED",
                    new Vector2(288f, 0f), new Vector2(150f, 30f), ActionBg, ActionBorder,
                    entry.Enabled ? EnabledText : SubText, () => ToggleAddon(entry));
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
        // Sub-tab / header styling (MonoGame parity)
        // ------------------------------------------------------------------

        private static void CreateSubTabBox(RectTransform parent, string label, Vector2 center, Vector2 size, bool mods)
        {
            var go = new GameObject("SubTab_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            SetRect(rect, center, size);

            var img = go.GetComponent<Image>();
            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.transition = Selectable.Transition.None; // we restyle statically

            var line = go.AddComponent<Outline>();
            line.effectDistance = new Vector2(1.5f, -1.5f);

            var t = CreateLabel(rect, label, 14, TextColor);
            var tRect = (RectTransform)t.transform;
            Stretch(tRect);
            t.alignment = TextAnchor.MiddleCenter;

            btn.onClick.AddListener(() =>
            {
                _isMods = mods;
                ApplySubTabVisual();
                OnSubTabChanged();
            });

            if (mods) { _modsTabBtn = btn; }
            else { _packsTabBtn = btn; }
        }

        /// <summary>Restyles both sub-tab boxes for the active choice.</summary>
        private static void ApplySubTabVisual()
        {
            StyleSubTab(_modsTabBtn, _isMods);
            StyleSubTab(_packsTabBtn, !_isMods);
            if (_templateBtn != null)
                _templateBtn.SetActive(_isMods); // MonoGame: TEMPLATE only on Mods
        }

        private static void StyleSubTab(Button btn, bool active)
        {
            if (btn == null) return;
            var img = btn.GetComponent<Image>();
            var line = btn.GetComponent<Outline>();
            var text = btn.GetComponentInChildren<Text>();
            img.color = active ? TabActiveBg : TabInactiveBg;
            line.effectColor = active ? TabActiveBorder : TabInactiveBorder;
            text.color = active ? TabActiveText : TabInactiveText;
        }

        private static void OnSubTabChanged()
        {
            if (_openFolderLabel != null)
                _openFolderLabel.text = _isMods ? "OPEN MODS" : "OPEN REPACKS";
            RefreshAndRebuild();
        }

        private static void DoTemplate()
        {
            try
            {
                var templateSrc = FindModTemplate();
                if (templateSrc == null)
                {
                    ShowStatus("Mod template not found in game assets.");
                    return;
                }
                var destDir = Path.Combine(Paths.ModsDir, "MyFirstMod");
                if (!Directory.Exists(destDir))
                {
                    CopyDirectoryRecursive(templateSrc, destDir);
                    ShowStatus("Template mod 'MyFirstMod' created. Open MODS to edit it!");
                }
                else
                    ShowStatus("Template mod already exists.");
                RefreshAndRebuild();
            }
            catch (Exception ex)
            {
                ShowStatus("Failed to create template: " + ex.Message);
            }
        }

        /// <summary>Locates the built-in Mod_Template that ships with the game
        /// assets (dev checkout Defaults dir or installed Assets dir).</summary>
        private static string FindModTemplate()
        {
            var candidates = new[]
            {
                Path.Combine(Paths.GetAssetsDir(), "Mod_Template"),
                Path.Combine(Path.GetDirectoryName(Paths.LocalAssetsDir) ?? "", "Mod_Template"),
                Path.Combine(Paths.RootDir, "Assets", "Mod_Template"),
            };
            foreach (var c in candidates)
                if (!string.IsNullOrEmpty(c) && Directory.Exists(c))
                    return c;
            return null;
        }

        // ------------------------------------------------------------------
        // Actions
        // ------------------------------------------------------------------

        private static void DoImport()
        {
            // Win32 common dialog on a background thread (launcher runs in
            // Windows Player only; mirror SkinManager's P/Invoke approach).
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                var files = FileDialogLauncher.PromptOpenFiles(
                    _isMods ? "Import LatticeVeil Mod (.lvmod)" : "Import LatticeVeil Repack (.lvpack)",
                    _isMods
                        ? "LatticeVeil Mods (*.lvmod)\0*.lvmod\0LatticeVeil Packages (*.lvmod;*.lvpack)\0*.lvmod;*.lvpack\0"
                        : "LatticeVeil Repacks (*.lvpack)\0*.lvpack\0LatticeVeil Packages (*.lvmod;*.lvpack)\0*.lvmod;*.lvpack\0");
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
                // Strict: only the engine's own package types are accepted.
                if (ext != ".lvmod" && ext != ".lvpack")
                {
                    ShowStatus("Unsupported addon type " + (ext.Length > 0 ? ext.ToUpperInvariant() : "(no extension)") + " — use .lvmod or .lvpack files.");
                    return;
                }
                var isMod = ext == ".lvmod";
                var targetDir = isMod ? Paths.ModsDir : Paths.PacksDir;
                Directory.CreateDirectory(targetDir);

                ShowStatus("Installing " + fileName + "...");

                File.Copy(sourceFilePath, Path.Combine(targetDir, fileName), true);

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

        /// <summary>MonoGame drag & drop info box: upload-arrow icon tile,
        /// DRAG & DROP text, and an IMPORT button inside the box. Built once,
        /// toggled by RefreshAndRebuild.</summary>
        private static void CreateEmptyState(RectTransform viewport)
        {
            var boxGo = new GameObject("DragDropBox", typeof(RectTransform), typeof(Image));
            var box = (RectTransform)boxGo.transform;
            box.SetParent(viewport, false);
            box.anchorMin = new Vector2(0.5f, 0.5f);
            box.anchorMax = new Vector2(0.5f, 0.5f);
            box.pivot = new Vector2(0.5f, 0.5f);
            box.sizeDelta = new Vector2(500f, 120f);
            box.anchoredPosition = new Vector2(0f, 30f);
            boxGo.GetComponent<Image>().color = new Color(20f / 255f, 25f / 255f, 35f / 255f, 230f / 255f);
            var boxLine = boxGo.AddComponent<Outline>();
            boxLine.effectColor = new Color(100f / 255f, 110f / 255f, 125f / 255f, 1f);
            boxLine.effectDistance = new Vector2(1.5f, -1.5f);
            _emptyBox = boxGo;

            // Upload icon tile (bordered square with a drawn up-arrow).
            var iconGo = new GameObject("UploadIcon", typeof(RectTransform), typeof(Image));
            var icon = (RectTransform)iconGo.transform;
            icon.SetParent(box, false);
            icon.anchorMin = new Vector2(0.5f, 1f);
            icon.anchorMax = new Vector2(0.5f, 1f);
            icon.pivot = new Vector2(0.5f, 1f);
            icon.sizeDelta = new Vector2(48f, 48f);
            icon.anchoredPosition = new Vector2(0f, -15f);
            iconGo.GetComponent<Image>().color = new Color(30f / 255f, 35f / 255f, 45f / 255f);
            var iconLine = iconGo.AddComponent<Outline>();
            iconLine.effectColor = new Color(120f / 255f, 130f / 255f, 145f / 255f, 1f);
            iconLine.effectDistance = new Vector2(1f, -1f);

            var arrowColor = new Color(140f / 255f, 160f / 255f, 180f / 255f);
            // Arrow shaft (8x20 from icon center downward)
            CreateFlatImage(icon, new Vector2(0f, -4f), new Vector2(8f, 20f), arrowColor);
            // Arrow head (12x8 bar + two 4px steps)
            CreateFlatImage(icon, new Vector2(0f, -14f), new Vector2(12f, 8f), arrowColor);
            CreateFlatImage(icon, new Vector2(-6f, -18f), new Vector2(4f, 4f), arrowColor);
            CreateFlatImage(icon, new Vector2(6f, -18f), new Vector2(4f, 4f), arrowColor);
            // Base line (upload area)
            CreateFlatImage(icon, new Vector2(0f, 38f), new Vector2(30f, 4f), arrowColor);

            // DRAG & DROP text
            var hint = CreateLabel(box, "DRAG & DROP .LVMOD FILES", 13, new Color(200f / 255f, 210f / 255f, 220f / 255f));
            SetRect((RectTransform)hint.transform, new Vector2(0f, 33f), new Vector2(460f, 14f));
            hint.alignment = TextAnchor.MiddleCenter;

            // IMPORT button inside the box
            var import = CreateActionButton(box, "IMPORT",
                new Vector2(0f, 14f), new Vector2(160f, 24f),
                ActionBg, ActionBorder, SubText, DoImport);
            var iLabel = import.GetComponentInChildren<Text>();
            iLabel.fontSize = 12;
        }

        private static void CreateFlatImage(RectTransform parent, Vector2 center, Vector2 size, Color color)
        {
            var go = new GameObject("ArrowPixel", typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = center;
            go.GetComponent<Image>().color = color;
            go.GetComponent<Image>().raycastTarget = false;
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
        private static Button CreateActionButton(RectTransform parent, string label, Vector2 center, Vector2 size, Color bgColor, Color borderColor, Color textColor, Action onClick)
        {
            var go = new GameObject("ActionBtn_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            SetRect(rect, center, size);
            var img = go.GetComponent<Image>();
            img.color = bgColor;
            var outline = go.AddComponent<Outline>();
            outline.effectColor = borderColor;
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.transition = Selectable.Transition.None;

            var t = CreateLabel(rect, label, 13, textColor);
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
    }

    /// <summary>
    /// Lives on the Addons page; re-scans folders every time the page is
    /// re-shown (dropdown/tab selection already triggers refreshes, this is a
    /// safety net for external changes while the screen is open).
    /// </summary>
    public sealed class AddonsPageController : MonoBehaviour
    {
        private Action _refresh;

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
