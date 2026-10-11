using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using LatticeVeil.Core;

namespace LatticeVeil.Game
{
    /// <summary>
    /// SCREENSHOTS screen (main-menu button): modern stuff — thumbnail grid,
    /// friendly file names, OPEN FOLDER, DELETE, click-to-preview large image,
    /// and proper multi-select (Ctrl+click toggles, Shift+click ranges,
    /// Ctrl+A selects all). Esc / BACK closes.
    /// Files come from Documents/LatticeVeil/Screenshots (ScreenshotService).
    /// </summary>
    public static class ScreenshotsScreenBuilder
    {
        // Metrics (panel-local units, same frame as the other screens).
        private const float LeftEdge = -424f;
        private const float RightEdge = 424f;
        private const float HeaderY = 190f;     // OPEN FOLDER / title row
        private const float TileW = 190f;
        private const float TileH = 112f;       // 16:9-ish thumbnail
        private const float TileGap = 14f;
        private const float LabelH = 22f;

        private static readonly Color TextColor = Color.white;
        private static readonly Color SubText = new Color(0.63f, 0.67f, 0.73f, 1f);
        private static readonly Color Accent = new Color(0.55f, 0.8f, 1f, 1f);
        private static readonly Color ActionBg = new Color(24f / 255f, 28f / 255f, 36f / 255f);
        private static readonly Color ActionBorder = new Color(140f / 255f, 150f / 255f, 165f / 255f);
        private static readonly Color SelectedBorder = new Color(0.45f, 0.75f, 1f);
        private static readonly Color RowBg = new Color(20f / 255f, 22f / 255f, 26f / 255f, 0.97f);

        private static RectTransform _screenRoot;
        private static RectTransform _viewport;
        private static RectTransform _gridRoot;
        private static Text _statusText;
        private static Button _deleteBtn;
        private static ScreenshotsScreenController _controller;

        public static void Open(Canvas canvas, Action onClosed)
        {
            var go = new GameObject("ScreenshotsScreen", typeof(RectTransform), typeof(ScreenshotsScreenController));
            var root = (RectTransform)go.transform;
            root.SetParent(canvas.transform, false);
            Stretch(root);

            // Dim background over the whole window.
            var dimGo = new GameObject("Dim", typeof(RectTransform), typeof(Image));
            var dim = (RectTransform)dimGo.transform;
            dim.SetParent(root, false);
            Stretch(dim);
            dimGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);

            // Framed panel (reuses Settings.png frame, like Options).
            var frameGo = new GameObject("Frame", typeof(RectTransform), typeof(Image));
            var frame = (RectTransform)frameGo.transform;
            frame.SetParent(root, false);
            frame.anchorMin = new Vector2(0.5f, 0.5f);
            frame.anchorMax = new Vector2(0.5f, 0.5f);
            frame.pivot = new Vector2(0.5f, 0.5f);
            frame.sizeDelta = new Vector2(960f, 540f);
            var frameTex = GameAssetLoader.LoadTexture("textures/menu/GUIS/Settings_GUI.png");
            if (frameTex != null)
            {
                var img = frameGo.GetComponent<Image>();
                img.sprite = Sprite.Create(frameTex, new Rect(0, 0, frameTex.width, frameTex.height),
                    new Vector2(0.5f, 0.5f), 100f);
            }
            else
            {
                frameGo.GetComponent<Image>().color = RowBg;
                var outline = frameGo.AddComponent<Outline>();
                outline.effectColor = ActionBorder;
                outline.effectDistance = new Vector2(2f, -2f);
            }

            var controller = go.GetComponent<ScreenshotsScreenController>();
            _controller = controller;

            // ---- Header: title left, action buttons right (inside the frame) ----
            var title = CreateLabel(frame, "SCREENSHOTS", 20, TextColor);
            SetRect((RectTransform)title.transform, new Vector2(LeftEdge + 150f, HeaderY), new Vector2(300f, 28f));
            title.alignment = TextAnchor.MiddleLeft;

            var folderBtn = CreateActionButton(frame, "OPEN FOLDER", new Vector2(RightEdge - 70f, HeaderY), new Vector2(140f, 28f), DoOpenFolder);
            _deleteBtn = CreateActionButton(frame, "DELETE", new Vector2(RightEdge - 70f - 140f - 8f, HeaderY), new Vector2(120f, 28f), DoDelete);
            SetDeleteEnabled(false);

            // BACK button at the bottom-left, matching the Options screen's
            // BACK (texture style with fallback text).
            var backGo = new GameObject("Btn_BACK", typeof(RectTransform), typeof(Image), typeof(Button));
            var backRect = (RectTransform)backGo.transform;
            backRect.SetParent(root, false);
            backRect.anchorMin = new Vector2(0f, 0f);
            backRect.anchorMax = new Vector2(0f, 0f);
            backRect.pivot = new Vector2(0f, 0f);
            backRect.sizeDelta = new Vector2(150f, 48f);
            backRect.anchoredPosition = new Vector2(14f, 14f);
            var backTex = GameAssetLoader.LoadTexture("textures/menu/buttons/Back.png");
            if (backTex != null)
            {
                var bImg = backGo.GetComponent<Image>();
                bImg.sprite = Sprite.Create(backTex, new Rect(0, 0, backTex.width, backTex.height),
                    new Vector2(0.5f, 0.5f), 100f);
                bImg.preserveAspect = true;
                // Slightly wider for the texture's aspect.
                backRect.sizeDelta = new Vector2(170f, 48f);
            }
            else
            {
                backGo.GetComponent<Image>().color = ActionBg;
                var bOutline = backGo.AddComponent<Outline>();
                bOutline.effectColor = ActionBorder;
                bOutline.effectDistance = new Vector2(1.5f, -1.5f);
                var bLabel = CreateLabel(backRect, "BACK", 15, TextColor);
                Stretch(bLabel.transform as RectTransform);
                bLabel.alignment = TextAnchor.MiddleCenter;
            }
            backGo.GetComponent<Button>().onClick.AddListener(DoBack);

            _statusText = CreateLabel(frame, "", 11, SubText);
            SetRect((RectTransform)_statusText.transform, new Vector2(0f, HeaderY - 20f), new Vector2(RightEdge - LeftEdge, 14f));
            _statusText.alignment = TextAnchor.MiddleCenter;

            // ---- Scrollable thumbnail grid (inside the frame) ----
            var viewportGo = new GameObject("Grid", typeof(RectTransform), typeof(RectMask2D));
            _viewport = (RectTransform)viewportGo.transform;
            _viewport.SetParent(frame, false);
            _viewport.anchorMin = new Vector2(0f, 0f);
            _viewport.anchorMax = new Vector2(1f, 1f);
            _viewport.pivot = new Vector2(0.5f, 0.5f);
            _viewport.offsetMin = new Vector2(56f, 44f);
            _viewport.offsetMax = new Vector2(-56f, -122f); // below the header

            var scrollGo = viewportGo.AddComponent<ScrollRect>();
            scrollGo.horizontal = false;
            scrollGo.vertical = true;
            scrollGo.movementType = ScrollRect.MovementType.Clamped;
            scrollGo.scrollSensitivity = 30f;
            viewportGo.AddComponent<OptionsScreenBuilder.ScrollAnywhereDrag>().Scroll = scrollGo;

            var gridGo = new GameObject("GridContent", typeof(RectTransform));
            _gridRoot = (RectTransform)gridGo.transform;
            _gridRoot.SetParent(_viewport, false);
            _gridRoot.anchorMin = new Vector2(0.5f, 1f);
            _gridRoot.anchorMax = new Vector2(0.5f, 1f);
            _gridRoot.pivot = new Vector2(0.5f, 1f);
            _gridRoot.anchoredPosition = Vector2.zero;   // content top == viewport top
            _gridRoot.sizeDelta = new Vector2(0f, 10f);
            scrollGo.content = _gridRoot;
            scrollGo.viewport = _viewport;

            controller.Init(root, _viewport, ReloadGrid);
            controller.Closed = onClosed;
            ReloadGrid();
        }

        // ------------------------------------------------------------------
        // Data
        // ------------------------------------------------------------------

        public sealed class ShotItem
        {
            public string Path;
            public string Name;        // friendly: without extension
            public string GroupName;   // context prefix ("mainmenu", "gameplay", ...)
            public DateTime Taken;
            public long SizeBytes;
        }

        public static List<ShotItem> LoadItems()
        {
            var list = new List<ShotItem>();
            try
            {
                Directory.CreateDirectory(Paths.ScreenshotsDir);
                foreach (var file in Directory.GetFiles(Paths.ScreenshotsDir, "*.png"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    int us = name.IndexOf('_');
                    var group = us > 0 ? name.Substring(0, us).ToLowerInvariant() : "";
                    DateTime taken = File.GetLastWriteTime(file);
                    var fi = new FileInfo(file);
                    list.Add(new ShotItem
                    {
                        Path = file,
                        Name = name,
                        GroupName = group,
                        Taken = taken,
                        SizeBytes = fi.Length,
                    });
                }
                // Newest first.
                list.Sort((a, b) => b.Taken.CompareTo(a.Taken));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Screenshots list failed: " + ex.Message);
            }
            return list;
        }

        /// <summary>NOT WORKING / "mainmenu_2026-10-10_..." → "MAINMENU · 2026-10-10 22:41".</summary>
        private static string FriendlyLabel(ShotItem item)
        {
            return item.Name;
        }

        // ------------------------------------------------------------------
        // Grid
        // ------------------------------------------------------------------

        private static void ReloadGrid()
        {
            if (_gridRoot == null || _controller == null) return;

            for (int i = _gridRoot.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_gridRoot.GetChild(i).gameObject);

            var items = LoadItems();
            _controller.Items.Clear();
            _controller.Items.AddRange(items);
            _controller.Selection.Clear();

            float cols = 4f;
            float tileTotalW = TileW + TileGap;
            int rows = Mathf.Max(1, Mathf.CeilToInt(items.Count / cols));
            float totalH = rows * (TileH + LabelH + TileGap) + 90f;
            _gridRoot.sizeDelta = new Vector2(0f, totalH);

            if (items.Count == 0)
            {
                var empty = CreateLabel(_gridRoot, "NO SCREENSHOTS YET. PRESS F2 IN GAME TO CAPTURE.", 14, SubText);
                SetRect((RectTransform)empty.transform, new Vector2(0f, -160f), new Vector2(820f, 22f));
                empty.alignment = TextAnchor.MiddleCenter;
                SetStatus("");
                return;
            }

            int playedIndex = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                int col = i % (int)cols;
                int row = i / (int)cols;
                var itemLocal = item;
                var idx = i;

                var tileGo = new GameObject("Shot_" + item.Name, typeof(RectTransform), typeof(Image));
                var tileRect = (RectTransform)tileGo.transform;
                tileRect.SetParent(_gridRoot, false);
                tileRect.anchorMin = new Vector2(0.5f, 1f);
                tileRect.anchorMax = new Vector2(0.5f, 1f);
                tileRect.pivot = new Vector2(0.5f, 1f);
                tileRect.sizeDelta = new Vector2(TileW, TileH + LabelH);
                float x = -((cols - 1) * tileTotalW / 2f) + col * tileTotalW;
                tileRect.anchoredPosition = new Vector2(x, -row * (TileH + LabelH + TileGap));
                tileGo.GetComponent<Image>().color = new Color(35f / 255f, 38f / 255f, 45f / 255f, 1f);
                var tileBtn = tileGo.AddComponent<Button>();
                tileBtn.transition = Selectable.Transition.None;
                tileBtn.onClick.AddListener(() => _controller.OnTileClicked(idx, itemLocal));

                // Thumbnail (fills top part, centered aspect-fit).
                var thumbGo = new GameObject("Thumb", typeof(RectTransform), typeof(Image));
                var thumbRect = (RectTransform)thumbGo.transform;
                thumbRect.SetParent(tileRect, false);
                thumbRect.anchorMin = new Vector2(0.5f, 1f);
                thumbRect.anchorMax = new Vector2(0.5f, 1f);
                thumbRect.pivot = new Vector2(0.5f, 1f);
                thumbRect.sizeDelta = new Vector2(TileW - 8f, TileH - 8f);
                thumbRect.anchoredPosition = new Vector2(0f, -4f);
                thumbGo.GetComponent<Image>().color = new Color(50f / 255f, 54f / 255f, 62f / 255f, 1f);
                thumbGo.GetComponent<Image>().raycastTarget = false;
                thumbGo.AddComponent<ThumbnailLoader>().Load(item.Path);

                // Label under the thumbnail.
                var label = CreateLabel(tileRect, item.Name, 11, TextColor);
                var lRect = (RectTransform)label.transform;
                lRect.anchorMin = new Vector2(0.5f, 1f);
                lRect.anchorMax = new Vector2(0.5f, 1f);
                lRect.pivot = new Vector2(0f, 1f);
                lRect.sizeDelta = new Vector2(TileW - 8f - 26f, LabelH);
                lRect.anchoredPosition = new Vector2(-(TileW - 8f) / 2f + 26f, -TileH);
                label.alignment = TextAnchor.MiddleLeft;
                label.fontSize = 10;
                label.horizontalOverflow = HorizontalWrapMode.Wrap;

                // Checkbox in the thumbnail's top-left corner: toggles selection
                // without opening the preview (multi-select friendliness).
                var checkGo = new GameObject("Check", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
                var checkRect = (RectTransform)checkGo.transform;
                checkRect.SetParent(tileRect, false);
                checkRect.anchorMin = new Vector2(0f, 1f);
                checkRect.anchorMax = new Vector2(0f, 1f);
                checkRect.pivot = new Vector2(0f, 1f);
                checkRect.sizeDelta = new Vector2(22f, 22f);
                checkRect.anchoredPosition = new Vector2(6f, -6f);
                var checkImg = checkGo.GetComponent<Image>();
                checkImg.color = new Color(16f / 255f, 18f / 255f, 22f / 255f, 230f / 255f);
                checkGo.GetComponent<Outline>().effectColor = new Color(140f / 255f, 150f / 255f, 165f / 255f, 1f);
                checkGo.GetComponent<Outline>().effectDistance = new Vector2(1f, -1f);
                var checkBtn = checkGo.GetComponent<Button>();
                checkBtn.transition = Selectable.Transition.None;
                var checkMarkGo = new GameObject("Mark", typeof(RectTransform), typeof(Text));
                var markRect = (RectTransform)checkMarkGo.transform;
                markRect.SetParent(checkRect, false);
                Stretch(markRect);
                var markText = checkMarkGo.GetComponent<Text>();
                markText.font = Font();
                markText.fontSize = 15;
                markText.text = "";   // hidden until checked
                markText.color = Accent;
                markText.alignment = TextAnchor.MiddleCenter;
                markText.raycastTarget = false;
                var checkIdx = i;
                var checkItem = item;
                checkBtn.onClick.AddListener(() =>
                {
                    _controller.ToggleChecked(checkIdx, checkItem, checkMarkGo);
                });
                _controller.RegisterCheckbox(checkIdx, checkMarkGo);

                // Selection border overlay (hidden until selected).
                var selGo = new GameObject("Selected", typeof(RectTransform), typeof(Outline));
                var selRect = (RectTransform)selGo.transform;
                Stretch(selRect, tileRect);
                selGo.GetComponent<Outline>().effectColor = SelectedBorder;
                selGo.GetComponent<Outline>().effectDistance = new Vector2(2f, -2f);
                selGo.SetActive(false);
                _controller.RegisterTile(idx, itemLocal, selGo);
                playedIndex++;
            }

            SetStatus($"{items.Count} SHOT{(items.Count == 1 ? "" : "S")} · CLICK TO PREVIEW · CTRL/SHIFT FOR MULTI-SELECT · CTRL+A ALL");
        }

        private static void SetStatus(string text)
        {
            if (_statusText != null)
                _statusText.text = text;
        }

        // ------------------------------------------------------------------
        // Actions
        // ------------------------------------------------------------------

        private static void DoBack()
        {
            if (_controller != null)
                _controller.CloseScreenSafe();
        }

        private static void DoOpenFolder()
        {
            try
            {
                Directory.CreateDirectory(Paths.ScreenshotsDir);
                System.Diagnostics.Process.Start("explorer.exe", $"\"{Paths.ScreenshotsDir}\"");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Open folder failed: " + ex.Message);
            }
        }

        private static void DoDelete()
        {
            if (_controller == null) return;
            _controller.DeleteSelected();
        }

        // ------------------------------------------------------------------
        // Controller (selection behavior + preview modal)
        // ------------------------------------------------------------------

        public sealed class ScreenshotsScreenController : MonoBehaviour
        {
            public readonly List<ShotItem> Items = new List<ShotItem>();
            public readonly HashSet<int> Selection = new HashSet<int>();
            private int _lastClicked = -1;
            private GameObject _previewPanel;

            private readonly Dictionary<int, GameObject> _tiles = new Dictionary<int, GameObject>();
            private RectTransform _root;

            public void Init(RectTransform root, RectTransform viewport, Action rebuild)
            {
                _root = root;
                _rebuild = rebuild;
            }

            private Action _rebuild;

            /// <summary>Invoked when the screen closes (BACK or Esc) so the
            /// main menu re-enables its SCREENSHOTS button / Escape handling.</summary>
            public Action Closed;

            public void RegisterTile(int index, ShotItem item, GameObject selectionBorder)
            {
                _tiles[index] = selectionBorder;
            }

            private readonly Dictionary<int, GameObject> _checkboxes = new Dictionary<int, GameObject>();

            public void RegisterCheckbox(int index, GameObject checkMark)
            {
                _checkboxes[index] = checkMark;
            }

            /// <summary>Checkbox click: toggles this shot's selection only
            /// (never opens the preview, never clears other checks).</summary>
            public void ToggleChecked(int index, ShotItem item, GameObject checkMark)
            {
                if (!Selection.Add(index))
                    Selection.Remove(index);
                _lastClicked = index;
                RefreshTileVisuals();
            }

            public void OnTileClicked(int index, ShotItem item)
            {
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

                if (shift && _lastClicked >= 0)
                {
                    // Range select: last clicked → this one.
                    int lo = Mathf.Min(_lastClicked, index);
                    int hi = Mathf.Max(_lastClicked, index);
                    if (!ctrl) Selection.Clear();
                    for (int i = lo; i <= hi; i++)
                        Selection.Add(i);
                }
                else if (ctrl)
                {
                    if (!Selection.Add(index))
                        Selection.Remove(index);
                    _lastClicked = index;
                }
                else
                {
                    // Plain click: if exactly this one is selected, open the
                    // preview; otherwise select just it (standard file-explorer
                    // behavior: first click selects, second opens).
                    if (Selection.Count == 1 && Selection.Contains(index))
                    {
                        OpenPreview(item);
                        return;
                    }
                    Selection.Clear();
                    Selection.Add(index);
                    _lastClicked = index;
                }

                RefreshTileVisuals();
            }

            public void SelectAll()
            {
                for (int i = 0; i < Items.Count; i++)
                    Selection.Add(i);
                RefreshTileVisuals();
            }

            public void ClearSelection()
            {
                Selection.Clear();
                _lastClicked = -1;
                RefreshTileVisuals();
            }

            private void RefreshTileVisuals()
            {
                foreach (var pair in _tiles)
                    pair.Value.SetActive(Selection.Contains(pair.Key));
                foreach (var pair in _checkboxes)
                {
                    var t = pair.Value.GetComponent<Text>();
                    if (t != null)
                        t.text = Selection.Contains(pair.Key) ? "X" : "";
                }
                SetDeleteEnabled(Selection.Count > 0 && _deleteBtn != null);
                if (_statusTextRef != null)
                {
                    _statusTextRef.text = Selection.Count > 0
                        ? $"{Selection.Count} SELECTED · ESC CLEAR / CLOSE"
                        : "CLICK TO PREVIEW · CHECKBOX OR CTRL/SHIFT FOR MULTI-SELECT · CTRL+A ALL";
                }
            }

            public void DeleteSelected()
            {
                if (Selection.Count == 0) return;
                var paths = new List<string>();
                foreach (var idx in Selection)
                {
                    if (idx >= 0 && idx < Items.Count)
                        paths.Add(Items[idx].Path);
                }
                Selection.Clear();

                int deleted = 0;
                foreach (var p in paths)
                {
                    try { File.Delete(p); deleted++; }
                    catch (Exception ex) { Debug.LogWarning("Delete failed " + p + ": " + ex.Message); }
                }

                if (_rebuild != null)
                    _rebuild();   // re-list + clears selection
            }

            // ---------------- Preview modal ----------------

            private void OpenPreview(ShotItem item)
            {
                ClosePreview();

                var dimGo = new GameObject("PreviewDim", typeof(RectTransform), typeof(Image), typeof(Button));
                var dimRect = (RectTransform)dimGo.transform;
                dimRect.SetParent(_root, false);
                Stretch(dimRect);
                dimGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.8f);
                dimGo.GetComponent<Button>().onClick.AddListener(ClosePreview);

                // Big image, aspect-fit inside the panel.
                var tex = LoadTexture(item.Path);
                if (tex != null)
                {
                    float maxW = 820f;
                    float maxH = 400f;
                    float s = Mathf.Min(maxW / tex.width, maxH / tex.height);
                    float w = tex.width * s;
                    float h = tex.height * s;

                    var imgGo = new GameObject("PreviewImage", typeof(RectTransform), typeof(Image));
                    var imgRect = (RectTransform)imgGo.transform;
                    imgRect.SetParent(dimRect, false);
                    imgRect.anchorMin = new Vector2(0.5f, 0.5f);
                    imgRect.anchorMax = new Vector2(0.5f, 0.5f);
                    imgRect.pivot = new Vector2(0.5f, 0.5f);
                    imgRect.sizeDelta = new Vector2(w, h);
                    var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                        new Vector2(0.5f, 0.5f), 100f);
                    imgGo.GetComponent<Image>().sprite = sprite;
                }

                // Filename below the image.
                var nameGo = new GameObject("PreviewName", typeof(RectTransform), typeof(Text));
                var nameRect = (RectTransform)nameGo.transform;
                nameRect.SetParent(dimRect, false);
                nameRect.anchorMin = new Vector2(0.5f, 0f);
                nameRect.anchorMax = new Vector2(0.5f, 0f);
                nameRect.pivot = new Vector2(0.5f, 0f);
                nameRect.sizeDelta = new Vector2(840f, 28f);
                nameRect.anchoredPosition = new Vector2(0f, 30f);
                var nameText = nameGo.GetComponent<Text>();
                nameText.text = item.Path;
                nameText.fontSize = 13;
                nameText.color = SubText;
                nameText.alignment = TextAnchor.MiddleCenter;
                nameText.font = Font();
                nameText.raycastTarget = false;

                _previewPanel = dimGo;
            }

            private void ClosePreview()
            {
                if (_previewPanel != null)
                {
                    Destroy(_previewPanel);
                    _previewPanel = null;
                }
            }

            private void Update()
            {
                // ESC: close the preview if it's open, else clear/close screen.
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    if (_previewPanel != null)
                    {
                        ClosePreview();
                        return;
                    }
                    if (Selection.Count > 0)
                    {
                        ClearSelection();
                        return;
                    }
                    CloseScreen();
                    return;
                }

                if (Input.GetKeyDown(KeyCode.A)
                    && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
                {
                    SelectAll();
                }
            }

            private void CloseScreen()
            {
                Closed?.Invoke();
                Destroy(gameObject);
            }

            /// <summary>BACK button path (public for the static builder).</summary>
            public void CloseScreenSafe()
            {
                ClosePreview();
                CloseScreen();
            }

            private void OnDestroy()
            {
                if (_controller == this)
                    ScreenshotsScreenBuilder._controller = null;
            }
        }

        private static Text _statusTextRef;  // aliased for RefreshTileVisuals

        // ------------------------------------------------------------------
        // Thumbnail async loading (decode PNGs in a frame; several MB is fine)
        // ------------------------------------------------------------------

        private sealed class ThumbnailLoader : MonoBehaviour
        {
            public void Load(string path)
            {
                _path = path;
            }

            private string _path;
            private bool _done;

            private void Start()
            {
                TryAndApply();
            }

            private void TryAndApply()
            {
                if (_done || string.IsNullOrEmpty(_path) || !File.Exists(_path)) return;
                try
                {
                    var bytes = File.ReadAllBytes(_path);
                    var tex = new Texture2D(2, 2);
                    if (tex.LoadImage(bytes))
                    {
                        var img = GetComponent<Image>();
                        img.preserveAspect = true;
                        img.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                            new Vector2(0.5f, 0.5f), 100f);
                    }
                    else
                    {
                        Destroy(tex);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("Thumb failed " + _path + ": " + ex.Message);
                }
                _done = true;
            }
        }

        // ------------------------------------------------------------------
        // uGUI helpers
        // ------------------------------------------------------------------

        private static Texture2D LoadTexture(string path)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2);
                return tex.LoadImage(bytes) ? tex : null;
            }
            catch
            {
                return null;
            }
        }

        private static Button CreateActionButton(RectTransform parent, string label, Vector2 center, Vector2 size, Action onClick)
        {
            var go = new GameObject("ActionBtn_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            SetRect(rect, center, size);
            var img = go.GetComponent<Image>();
            img.color = ActionBg;
            var outline = go.AddComponent<Outline>();
            outline.effectColor = ActionBorder;
            outline.effectDistance = new Vector2(1.5f, -1.5f);
            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.transition = Selectable.Transition.None;
            var t = CreateLabel(rect, label, 12, TextColor);
            Stretch(t.transform as RectTransform);
            t.alignment = TextAnchor.MiddleCenter;
            btn.onClick.AddListener(() => onClick());
            return btn;
        }

        private static void SetDeleteEnabled(bool on)
        {
            if (_deleteBtn == null) return;
            _deleteBtn.interactable = on;
            var img = _deleteBtn.GetComponent<Image>();
            var c = img.color;
            c.a = on ? 1f : 0.35f;
            img.color = c;
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

        private static Font _fontCache;
        private static Font Font()
        {
            if (_fontCache == null)
                _fontCache = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return _fontCache;
        }

        private static void SetRect(RectTransform rect, Vector2 center, Vector2 size)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = center;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void Stretch(RectTransform rect, RectTransform parent)
        {
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
