using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using LatticeVeil.Core;

namespace LatticeVeil.Game
{
    /// <summary>
    /// CONTROLS page (ports the MonoGame SettingsScreen controls panel):
    ///  - Sub-tab boxes MOVEMENT / GAMEPLAY / INTERFACE / ALL BINDS (active one lit)
    ///  - Scrollable bind list under the tabs; each row shows the action label
    ///    and its current key; clicking the key opens PRESS KEY capture
    ///  - Rewrites [Keybinds] in the controller-owned GameSettings and saves on
    ///    close/APPLY through the shared instance
    /// Binds map action id → OS key name (same strings as options.lvc).
    /// </summary>
    public static class ControlsScreenBuilder
    {
        // Same content metrics as AddonsScreenBuilder (panel-local units).
        public const float ContentScale = 0.84f;
        private const float RowHeight = 30f;

        // Header row: border boxes mirror OptionsScreenBuilder.PanelWidth.
        private const float LeftEdge = -424f;
        private const float RightEdge = 424f;
        private const float HeaderY = 178f;     // same line as the Addons tabs
        private const float HeaderH = 22f;

        // MonoGame DrawControlsSubTabs palette (same as Addons: muted greys).
        private static readonly Color TabActiveBg = new Color(52f / 255f, 58f / 255f, 68f / 255f, 235f / 255f);
        private static readonly Color TabInactiveBg = new Color(24f / 255f, 28f / 255f, 34f / 255f, 220f / 255f);
        private static readonly Color TabActiveBorder = new Color(170f / 255f, 178f / 255f, 190f / 255f, 1f);
        private static readonly Color TabInactiveBorder = new Color(96f / 255f, 104f / 255f, 116f / 255f, 1f);
        private static readonly Color TabActiveText = Color.white;
        private static readonly Color TabInactiveText = new Color(165f / 255f, 172f / 255f, 182f / 255f);

        private static readonly Color TextColor = Color.white;
        private static readonly Color SubText = new Color(0.63f, 0.67f, 0.73f, 1f);
        private static readonly Color RowBg = new Color(18f / 255f, 18f / 255f, 18f / 255f, 0.95f);
        private static readonly Color CaptureBorder = new Color(226f / 255f, 240f / 255f, 1f);

        private static readonly string[] MovementBindActions =
        {
            "MoveForward", "MoveBackward", "MoveLeft", "MoveRight",
            "Jump", "Sneak", "Sprint", "TogglePerspective"
        };
        private static readonly string[] GameplayBindActions =
        {
            "Inventory", "DropItem", "Interact", "UseItem", "Attack", "PickBlock"
        };
        private static readonly string[] InterfaceBindActions =
        {
            "PauseGame", "Chat", "PlayerList", "Screenshot", "ToggleHUD"
        };
        private static readonly string[] AllBindOrder =
        {
            "MoveForward", "MoveBackward", "MoveLeft", "MoveRight",
            "Jump", "Sneak", "Sprint", "TogglePerspective",
            "Inventory", "DropItem", "Interact", "UseItem", "Attack", "PickBlock",
            "PauseGame", "Chat", "PlayerList", "Screenshot", "ToggleHUD"
        };

        /// <summary>Action id → human label (MonoGame GetBindActionLabel parity).</summary>
        private static string GetBindActionLabel(string action)
        {
            return action switch
            {
                "MoveForward" => "MOVE FORWARD",
                "MoveBackward" => "MOVE BACKWARD",
                "MoveLeft" => "MOVE LEFT",
                "MoveRight" => "MOVE RIGHT",
                "Jump" => "JUMP",
                "Sneak" => "CROUCH",
                "Sprint" => "SPRINT",
                "TogglePerspective" => "FLY DESCEND / PERSPECTIVE",
                "Inventory" => "INVENTORY",
                "DropItem" => "DROP ITEM",
                "Interact" => "USE / INTERACT",
                "UseItem" => "USE ITEM",
                "Attack" => "ATTACK / BREAK",
                "PickBlock" => "PICK BLOCK",
                "PauseGame" => "PAUSE",
                "Chat" => "OPEN CHAT",
                "PlayerList" => "PLAYER LIST",
                "Screenshot" => "SCREENSHOT",
                "ToggleHUD" => "TOGGLE HUD",
                _ => action.ToUpperInvariant(),
            };
        }

        private static int _subTabIndex;   // 0..3
        private static RectTransform _listRoot;
        private static RectTransform _viewport;
        private static Button[] _tabButtons = new Button[4];
        private static Text _statusText;
        private static ControlsPageController _pageController;
        private static GameSettings _settings;
        private static Action _saveSettings;
        private static string _bindingAction;   // action awaiting a key press

        public static void BindSettings(GameSettings settings, Action saveAction)
        {
            _settings = settings;
            _saveSettings = saveAction;
        }

        public static RectTransform Build(RectTransform page, OptionsScreenController controller)
        {
            var root = new GameObject("StandardPageControls", typeof(RectTransform));
            var rect = (RectTransform)root.transform;
            rect.SetParent(page, false);
            Stretch(rect);

            if (_settings == null)
                _settings = GameSettings.LoadOrCreate(null);
            _subTabIndex = 0;
            _bindingAction = null;

            // ---- Sub-tab row (4 boxes; MonoGame: full content width split in 4) ----
            float gap = 10f;
            float totalW = RightEdge - LeftEdge;
            float tabW = (totalW - gap * 3f) / 4f;
            string[] labels = { "MOVEMENT", "GAMEPLAY", "INTERFACE", "ALL BINDS" };
            for (int i = 0; i < 4; i++)
            {
                var center = new Vector2(LeftEdge + tabW / 2f + i * (tabW + gap), HeaderY);
                _tabButtons[i] = CreateSubTabBox(rect, labels[i], center, new Vector2(tabW, HeaderH), i);
            }
            ApplySubTabVisual();

            // ---- Status line (rebind hints / conflicts) ----
            _statusText = CreateLabel(rect, "", 11, SubText);
            SetRect((RectTransform)_statusText.transform, new Vector2(0f, HeaderY - 20f), new Vector2(totalW, 12f));
            _statusText.alignment = TextAnchor.MiddleCenter;

            // ---- Scrollable bind list ----
            var viewportGo = new GameObject("ControlsViewport", typeof(RectTransform), typeof(RectMask2D));
            _viewport = (RectTransform)viewportGo.transform;
            _viewport.SetParent(rect, false);
            _viewport.anchorMin = new Vector2(0f, 0f);
            _viewport.anchorMax = new Vector2(1f, 1f);
            _viewport.pivot = new Vector2(0.5f, 0.5f);
            _viewport.offsetMin = new Vector2(56f, 44f);
            _viewport.offsetMax = new Vector2(-56f, -104f);   // same cut as Addons/Audio

            var scrollGo = viewportGo.AddComponent<ScrollRect>();
            scrollGo.horizontal = false;
            scrollGo.vertical = true;
            scrollGo.movementType = ScrollRect.MovementType.Clamped;
            scrollGo.scrollSensitivity = 28f;

            // Drag-anywhere helper (same trick as the other pages).
            viewportGo.AddComponent<OptionsScreenBuilder.ScrollAnywhereDrag>().Scroll = scrollGo;

            var contentGo = new GameObject("ControlsContent", typeof(RectTransform));
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

            _pageController = root.AddComponent<ControlsPageController>();
            _pageController.Init(rebuildAction: () =>
            {
                if (_bindingAction == null)
                    RebuildList();
            });

            RebuildList();
            return rect;
        }

        // ------------------------------------------------------------------
        // Sub-tab styling
        // ------------------------------------------------------------------

        private static Button CreateSubTabBox(RectTransform parent, string label, Vector2 center, Vector2 size, int index)
        {
            var go = new GameObject("SubTab_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            SetRect(rect, center, size);

            var img = go.GetComponent<Image>();
            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.transition = Selectable.Transition.None; // statically restyled

            var line = go.AddComponent<Outline>();
            line.effectDistance = new Vector2(1.5f, -1.5f);

            var t = CreateLabel(rect, label, 13, TextColor);
            var tRect = (RectTransform)t.transform;
            Stretch(tRect);
            t.alignment = TextAnchor.MiddleCenter;

            btn.onClick.AddListener(() =>
            {
                _subTabIndex = index;
                ApplySubTabVisual();
                var scroll = _viewport.GetComponent<ScrollRect>();
                if (scroll != null)
                    OptionsScreenBuilder.ResetScroll(scroll);
                RebuildList();
            });

            return btn;
        }

        private static void ApplySubTabVisual()
        {
            for (int i = 0; i < 4; i++)
                StyleSubTab(_tabButtons[i], i == _subTabIndex);
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

        // ------------------------------------------------------------------
        // Data + list rebuild
        // ------------------------------------------------------------------

        private static IReadOnlyList<string> VisibleActions()
        {
            return _subTabIndex switch
            {
                0 => MovementBindActions,
                1 => GameplayBindActions,
                2 => InterfaceBindActions,
                _ => AllBindOrder,
            };
        }

        private static void RebuildList()
        {
            if (_listRoot == null) return;

            for (int i = _listRoot.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_listRoot.GetChild(i).gameObject);

            var actions = VisibleActions();
            float totalH = Mathf.Max(430f, actions.Count * RowHeight + 8f);
            _listRoot.sizeDelta = new Vector2(0f, totalH);

            // MonoGame caption above the first row.
            var caption = CreateLabel(_listRoot, "CLICK AN ACTION TO REBIND", 12, SubText);
            SetRect((RectTransform)caption.transform, new Vector2(0f, 22f), new Vector2(788f, 18f));
            caption.alignment = TextAnchor.MiddleLeft;

            float y = 0f;
            foreach (var action in actions)
            {
                var actionLocal = action;
                var rowGo = new GameObject("BindRow_" + actionLocal, typeof(RectTransform), typeof(Image));
                var rowRect = (RectTransform)rowGo.transform;
                rowRect.SetParent(_listRoot, false);
                rowRect.anchorMin = new Vector2(0.5f, 1f);
                rowRect.anchorMax = new Vector2(0.5f, 1f);
                rowRect.pivot = new Vector2(0.5f, 1f);
                rowRect.sizeDelta = new Vector2(788f, RowHeight - 4f);
                rowRect.anchoredPosition = new Vector2(0f, -y - (RowHeight - 4f) / 2f);
                y += RowHeight;
                rowGo.GetComponent<Image>().color = RowBg;
                var outline = rowGo.AddComponent<Outline>();
                outline.effectColor = new Color(70f / 255f, 75f / 255f, 85f / 255f, 1f);
                outline.effectDistance = new Vector2(1.5f, -1.5f);

                // Action label on the left, key button on the right. The label
                // box is left-flushed inside the row (center -94, width 600
                // → text starts at the row's left edge).
                var label = CreateLabel(rowRect, GetBindActionLabel(actionLocal), 14, TextColor);
                SetRect((RectTransform)label.transform, new Vector2(-94f, 0f), new Vector2(600f, 20f));
                label.alignment = TextAnchor.MiddleLeft;

                string keyText = _bindingAction == actionLocal ? "PRESS KEY..." : DisplayKeyFor(actionLocal);
                var keyBtn = CreateActionButton(rowRect, keyText,
                    new Vector2(279f, 0f), new Vector2(210f, 24f), RowBg,
                    _bindingAction == actionLocal ? CaptureBorder : new Color(140f / 255f, 150f / 255f, 165f / 255f, 1f),
                    _bindingAction == actionLocal ? TabActiveText : SubText, () => BeginRebind(actionLocal));
            }
        }

        private static string DisplayKeyFor(string action)
        {
            var key = _settings != null && _settings.Keybinds.TryGetValue(action, out var k) ? k : null;
            if (string.IsNullOrWhiteSpace(key))
                return "UNBOUND";
            return key.ToUpperInvariant();
        }

        private static void BeginRebind(string action)
        {
            _bindingAction = action;
            ShowStatus("PRESS A KEY FOR " + GetBindActionLabel(action) + " (ESC TO CANCEL)");
            RebuildList();
            StartKeyCapture();
        }

        private static void StartKeyCapture()
        {
            var go = new GameObject("KeyCapture").AddComponent<KeyCaptureProbe>();
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.CaptureFinished += key =>
            {
                FinishRebind(key);  // null = cancelled (ESC)
                UnityEngine.Object.Destroy(go);
            };
        }

        private static void FinishRebind(string key)
        {
            if (_bindingAction == null) return;
            var action = _bindingAction;

            if (string.IsNullOrEmpty(key))
            {
                ShowStatus("REBIND CANCELLED.");
            }
            else
            {
                // Conflict check: warn when another action already uses this key.
                string conflict = null;
                foreach (var kv in _settings.Keybinds)
                {
                    if (!string.Equals(kv.Key, action, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(kv.Value, key, StringComparison.OrdinalIgnoreCase)
                        && Array.IndexOf(AllBindOrder, kv.Key) >= 0)
                    {
                        conflict = GetBindActionLabel(kv.Key);
                        break;
                    }
                }

                _settings.Keybinds[action] = key;
                _saveSettings?.Invoke();

                if (conflict != null)
                    ShowStatus(conflict + " ALSO USES " + key.ToUpperInvariant() + ".");
                else
                    ShowStatus(GetBindActionLabel(action) + " BOUND TO " + key.ToUpperInvariant() + ".");
            }

            _bindingAction = null;
            RebuildList();
        }

        private static void ShowStatus(string text)
        {
            if (_statusText != null)
                _statusText.text = text;
        }

        /// <summary>Polls the keyboard once per frame while a rebind is armed.
        /// MonoBehaviour key events don't reach plain static code, so capture
        /// is split out here (same idea as MonoGame's input poller).</summary>
        private sealed class KeyCaptureProbe : MonoBehaviour
        {
            public event Action<string> CaptureFinished;
            private bool _armed;

            private void Update()
            {
                if (!_armed) { _armed = true; return; }   // skip the click frame

                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    CaptureFinished?.Invoke(null);
                    return;
                }

                foreach (KeyCode code in System.Enum.GetValues(typeof(KeyCode)))
                {
                    if (code == KeyCode.None || !Input.GetKeyDown(code))
                        continue;
                    string name = MonoKeyName.FromKeyCode(code);
                    if (name != null)
                        CaptureFinished?.Invoke(name);
                    return;
                }
            }
        }

        // ------------------------------------------------------------------
        // uGUI helpers (mirror AddonsScreenBuilder)
        // ------------------------------------------------------------------

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

            var t = CreateLabel(rect, label, 12, textColor);
            Stretch((RectTransform)t.transform);
            t.alignment = TextAnchor.MiddleCenter;

            btn.onClick.AddListener(() => onClick());
            return btn;
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
    }

    /// <summary>Re-renders the bind list when the page becomes visible.</summary>
    public sealed class ControlsPageController : MonoBehaviour
    {
        private Action _rebuild;

        public void Init(Action rebuildAction)
        {
            _rebuild = rebuildAction;
        }

        private void OnEnable()
        {
            if (_rebuild != null)
                _rebuild();
        }
    }

    /// <summary>Maps Unity KeyCode → the OS-ish key names options.lvc uses
    /// (MoveForward="W", Jump="Space", Attack="Mouse0", Return, F1...).</summary>
    internal static class MonoKeyName
    {
        public static string FromKeyCode(KeyCode code)
        {
            var s = code.ToString();
            if (s.StartsWith("Alpha")) return s.Substring(5);
            if (s.StartsWith("Keypad")) return "Num" + s.Substring(6);
            if (s.StartsWith("Mouse")) return "Mouse" + (int)(code - KeyCode.Mouse0);
            if (s == "Return") return "Return";
            if (s == "Escape") return "Escape";
            if (s == "Space") return "Space";
            if (s == "Tab") return "Tab";
            if (s.StartsWith("F") && s.Length <= 3 && int.TryParse(s.Substring(1), out _)) return s;
            if (s.Length == 1 && char.IsLetter(s[0])) return s;
            if (s.StartsWith("Digit")) return s.Substring(5);
            return s;   // LeftShift, LeftControl, etc. — match options.lvc as-is
        }
    }
}
