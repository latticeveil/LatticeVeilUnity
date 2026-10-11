using System;
using System.IO;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using LatticeVeil.Core;

namespace LatticeVeil.Game
{
    /// <summary>
    /// Takes game screenshots of the FULL game window (everything the user
    /// sees) at the game's current resolution, named for the screen the user
    /// is on (mainmenu, options-video, addons, gameplay, ...).
    ///
    /// MonoGame parity: PNG saved to Documents/LatticeVeil/Screenshots with a
    /// timestamp name, plus a toast ("SCREENSHOT SAVED / <file>"). The Unity
    /// version adds the screen context to the name.
    /// </summary>
    public static class ScreenshotService
    {
        /// <summary>Active screen context for naming (set by active screens).</summary>
        public static string CurrentScreen = "game";

        /// <summary>Called by screens when they open so captures get named
        /// after what the user is currently looking at.</summary>
        public static void SetScreenContext(string name)
        {
            CurrentScreen = string.IsNullOrWhiteSpace(name) ? "game" : name.ToLowerInvariant();
        }

        /// <summary>Derives a context token: "options-video", "options-addons",
        /// "mainmenu", "gameplay", ...</summary>
        public static string ContextFor(string screen, string subPage = null)
        {
            if (string.IsNullOrEmpty(subPage))
                return screen.ToLowerInvariant();
            return $"{screen.ToLowerInvariant()}-{subPage.ToLowerInvariant()}";
        }

        /// <summary>Filename the next capture would produce.</summary>
        public static string NextFileName()
        {
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            return $"{CurrentScreen}_{stamp}.png";
        }

        /// <summary>Finds a free path: <screen>_<stamp>.png, adding -2, -3...
        /// when several shots land in the same second.</summary>
        public static string GetCapturePath()
        {
            Directory.CreateDirectory(Paths.ScreenshotsDir);
            var basePath = Path.Combine(Paths.ScreenshotsDir, NextFileName());
            var path = basePath;
            int index = 2;
            while (File.Exists(path))
            {
                path = Path.Combine(Paths.ScreenshotsDir,
                    $"{CurrentScreen}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}-{index}.png");
                index++;
            }
            return path;
        }

        /// <summary>Requests a capture. Waits for end-of-frame so everything
        /// drawn this frame is in, then reads the full backbuffer.</summary>
        public static void Capture(MonoBehaviour host, Action<string> onSaved = null)
        {
            if (host == null) return;
            host.StartCoroutine(CaptureRoutine(onSaved));
        }

        private static IEnumerator CaptureRoutine(Action<string> onSaved)
        {
            yield return new WaitForEndOfFrame();

            var path = GetCapturePath();
            try
            {
                // Full game window at its current resolution — the whole
                // screen the user sees, not a cropped/camera-only shot.
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                var bytes = tex.EncodeToPNG();
                UnityEngine.Object.Destroy(tex);
                File.WriteAllBytes(path, bytes);

                Debug.Log($"ScreenshotService saved {path} ({Screen.width}x{Screen.height})");
                ScreenshotToast.Show($"SCREENSHOT SAVED\n{Path.GetFileName(path)}");
                onSaved?.Invoke(path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ScreenshotService failed: {ex.Message}");
                ScreenshotToast.Show("SCREENSHOT FAILED");
            }
        }
    }

    /// <summary>In-game toast like MonoGame's screenshot toast: dark box,
    /// white text, fades after ~2.4s.</summary>
    public sealed class ScreenshotToast : MonoBehaviour
    {
        private static ScreenshotToast _active;

        public static void Show(string message)
        {
            if (_active != null)
            {
                Destroy(_active.gameObject);
                _active = null;
            }

            var go = new GameObject("ScreenshotToast", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            GameObject.DontDestroyOnLoad(go);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var boxGo = new GameObject("Box", typeof(RectTransform), typeof(Image));
            var box = (RectTransform)boxGo.transform;
            box.SetParent(canvas.transform, false);
            box.anchorMin = new Vector2(0.5f, 0.5f);
            box.anchorMax = new Vector2(0.5f, 0.5f);
            box.pivot = new Vector2(0.5f, 0.5f);
            box.sizeDelta = new Vector2(560f, 110f);
            var bg = boxGo.GetComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.65f);
            bg.raycastTarget = false;
            var outline = boxGo.AddComponent<Outline>();
            outline.effectColor = Color.white;
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
            var tr = (RectTransform)textGo.transform;
            tr.SetParent(box, false);
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero;
            tr.offsetMax = Vector2.zero;
            var text = textGo.GetComponent<Text>();
            text.text = message;
            text.alignment = TextAnchor.MiddleCenter;
            text.fontSize = 24;
            text.color = Color.white;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.raycastTarget = false;

            _active = go.AddComponent<ScreenshotToast>();
        }

        private float _life = 2.4f;

        private void Update()
        {
            _life -= Time.unscaledDeltaTime;
            if (_life <= 0f)
            {
                Destroy(gameObject);
                if (_active == this) _active = null;
            }
        }
    }
}
