using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using LatticeVeil.Core;

namespace LatticeVeil.Game
{
    /// <summary>
    /// Notification feed: friend requests and (future) world invites appear
    /// as compact toasts pinned top-left. Displays the newest request now,
    /// then the rest queue as each is answered. Holding Y (≈0.6s) accepts,
    /// holding N (≈0.6s) declines, with a live on-toast progress bar so the
    /// "quick reply holding" is visible. Answers go straight to the gate and
    /// the toast advances.
    /// </summary>
    public static class NotificationFeed
    {
        private static readonly List<VeilnetClient.GateFriendRequestEntry> _pending =
            new List<VeilnetClient.GateFriendRequestEntry>();

        private static NotificationToastHost _host;
        private static MonoBehaviour _runner;

        public static void SetRunner(MonoBehaviour runner) => _runner = runner;

        /// <summary>Called after friends sync so new incoming requests spawn toasts.</summary>
        public static void SyncFriendsInformations(List<VeilnetClient.GateFriendRequestEntry> incoming)
        {
            if (incoming == null) return;
            foreach (var r in incoming)
            {
                if (r == null || string.IsNullOrEmpty(r.ProductUserId)) continue;
                if (_pending.Exists(p => p.ProductUserId == r.ProductUserId)) continue;
                _pending.Add(r);
                Debug.Log("NotificationFeed: queued request from " + (r.User?.Username ?? r.ProductUserId));
            }
            EnsureHost();
        }

        public static void EnsureHost()
        {
            if (_host != null) return;
            if (_runner == null)
            {
                var go = new GameObject("NotificationFeedHost");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _runner = go.AddComponent<NotificationFeedRunner>();
            }
            _host = _runner.gameObject.AddComponent<NotificationToastHost>();
        }

        public static bool HasPending => _pending.Count > 0;

        public static VeilnetClient.GateFriendRequestEntry CurrentPending =>
            _pending.Count > 0 ? _pending[0] : null;

        public static void RemoveCurrent()
        {
            if (_pending.Count > 0)
                _pending.RemoveAt(0);
            if (_host != null)
                _host.RefreshVisual();
        }

        /// <summary>Accepts/declines the currently-focused request (toast host calls).</summary>
        internal static void Respond(bool accept)
        {
            var current = CurrentPending;
            if (current == null) return;
            var id = current.ProductUserId;
            RemoveCurrent();
            if (_runner != null)
                _runner.StartCoroutine(RespondRoutine(id, accept));
        }

        private static IEnumerator RespondRoutine(string id, bool accept)
        {
            var result = new VeilnetClient.MutationResult();
            yield return VeilnetClient.RespondToFriendRequest(id, accept, false, result, () => { });
            Debug.Log("NotificationFeed: responded " + (accept ? "ACCEPT" : "DECLINE") +
                      " (" + id + "): " + (result.Ok ? "ok" : result.Error));
        }
    }

    /// <summary>Dummy MonoBehaviour so NotificationFeed can start coroutines.</summary>
    internal sealed class NotificationFeedRunner : MonoBehaviour { }

    /// <summary>
    /// The visual toast host: canvas overlay top-left using the same frame
    /// language as the game (dark box, accent border). Shows the current
    /// request; hold Y / N with a progress fill. ESC defers (toast hides for
    /// this session of the screen).
    /// </summary>
    public sealed class NotificationToastHost : MonoBehaviour
    {
        private Canvas _canvas;
        private RectTransform _toastBox;
        private Text _title;
        private Text _sub;
        private Image _progressY;
        private Image _progressN;
        private Text _hint;
        private float _holdTime;
        private const float HoldSeconds = 0.6f;

        private void Start()
        {
            Build();
            RefreshVisual();
        }

        private void Build()
        {
            var go = new GameObject("NotificationCanvas", typeof(Canvas), typeof(CanvasScaler));
            UnityEngine.Object.DontDestroyOnLoad(go);
            _canvas = go.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 31000;   // under screenshot toast (32000)
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var boxGo = new GameObject("Toast", typeof(RectTransform), typeof(Image), typeof(Outline));
            _toastBox = (RectTransform)boxGo.transform;
            _toastBox.SetParent(_canvas.transform, false);
            _toastBox.anchorMin = new Vector2(0f, 1f);
            _toastBox.anchorMax = new Vector2(0f, 1f);
            _toastBox.pivot = new Vector2(0f, 0f);
            _toastBox.sizeDelta = new Vector2(340f, 96f);
            _toastBox.anchoredPosition = new Vector2(16f, -16f);
            boxGo.GetComponent<Image>().color = new Color(0.06f, 0.07f, 0.10f, 0.94f);
            boxGo.GetComponent<Outline>().effectColor = new Color(0.49f, 0.36f, 1f);
            boxGo.GetComponent<Outline>().effectDistance = new Vector2(1.5f, -1.5f);

            _title = MakeLabel(_toastBox, "", 17, Color.white, new Vector2(14f, -14f), new Vector2(312f, 26f));
            _sub = MakeLabel(_toastBox, "", 12, new Color(0.72f, 0.75f, 0.82f), new Vector2(14f, -42f), new Vector2(312f, 18f));

            // Hold-Y progress bar (top edge accent fill).
            var py = NewBar(_toastBox, "ProgressY", new Vector2(14f, -66f), new Color(0.35f, 0.9f, 0.5f));
            _progressY = py;
            var pn = NewBar(_toastBox, "ProgressN", new Vector2(14f, -78f), new Color(1f, 0.42f, 0.42f));
            _progressN = pn;

            _hint = MakeLabel(_toastBox, "HOLD Y — ACCEPT     HOLD N — DECLINE     ESC — LATER",
                10, new Color(0.62f, 0.66f, 0.74f), new Vector2(14f, -76f), new Vector2(312f, 16f));
        }

        private Image NewBar(RectTransform parent, string name, Vector2 pos, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            var r = (RectTransform)go.transform;
            r.SetParent(parent, false);
            r.anchorMin = new Vector2(0f, 1f);
            r.anchorMax = new Vector2(0f, 1f);
            r.pivot = new Vector2(0f, 1f);
            r.sizeDelta = new Vector2(0f, 6f);
            r.anchoredPosition = pos;
            go.GetComponent<Image>().color = color;
            go.GetComponent<Image>().raycastTarget = false;
            go.SetActive(false);
            return go.GetComponent<Image>();
        }

        private Text MakeLabel(RectTransform parent, string text, int size, Color color, Vector2 pos, Vector2 sizeV)
        {
            var go = new GameObject("ToastLabel_" + text, typeof(RectTransform), typeof(Text));
            var r = (RectTransform)go.transform;
            r.SetParent(parent, false);
            r.anchorMin = new Vector2(0f, 1f);
            r.anchorMax = new Vector2(0f, 1f);
            r.pivot = new Vector2(0f, 1f);
            r.sizeDelta = sizeV;
            r.anchoredPosition = pos;
            var t = go.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = size;
            t.color = color;
            t.text = text;
            t.alignment = TextAnchor.MiddleLeft;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            return t;
        }

        private void Update()
        {
            if (_toastBox == null) return;
            var current = NotificationFeed.CurrentPending;

            if (current == null)
            {
                if (_toastBox.gameObject.activeSelf)
                    _toastBox.gameObject.SetActive(false);
                _holdTime = 0f;
                return;
            }

            if (!_toastBox.gameObject.activeSelf)
            {
                RefreshVisual();
                _toastBox.gameObject.SetActive(true);
            }

            // Progress + quick reply.
            bool yHeld = Input.GetKey(KeyCode.Y);
            bool nHeld = Input.GetKey(KeyCode.N);
            if (yHeld == nHeld) growthHelper = 0f;
            else growthHelper += Time.unscaledDeltaTime;
            _holdTime = growthHelper;

            float p = Mathf.Clamp01(_holdTime / HoldSeconds);
            UpdateBar(_progressY, yHeld ? p : 0f);
            UpdateBar(_progressN, nHeld && !yHeld ? p : 0f);

            if (p >= 1f)
            {
                growthHelper = 0f;
                _holdTime = 0f;
                NotificationFeed.Respond(yHeld);
            }
        }
        private float growthHelper;

        private void UpdateBar(Image bar, float t)
        {
            if (bar == null) return;
            var rt = (RectTransform)bar.transform;
            var size = rt.sizeDelta;
            size.x = 312f * Mathf.Clamp01(t);
            rt.sizeDelta = size;
            bar.gameObject.SetActive(t > 0.02f);
        }

        public void RefreshVisual()
        {
            var current = NotificationFeed.CurrentPending;
            if (_toastBox == null)
            {
                Build();
            }
            if (current == null)
            {
                if (_toastBox != null)
                    _toastBox.gameObject.SetActive(false);
                return;
            }

            _toastBox.gameObject.SetActive(true);
            if (_title == null) return;

            _title.text = "FRIEND REQUEST";
            _sub.text = (current.User?.Username ?? current.ProductUserId) + " WANTS TO BE YOUR FRIEND";
            _hint.text = "HOLD Y — ACCEPT     HOLD N — DECLINE     ESC — LATER";
        }
    }
}
