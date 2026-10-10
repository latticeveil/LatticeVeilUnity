using UnityEngine;

namespace LatticeVeil.Game
{
    /// <summary>Editor/play-mode utility used by automated verification: captures
    /// a full-screen screenshot after a short delay so transient UI is settled.</summary>
    public sealed class ScreenshotHelper : MonoBehaviour
    {
        public string path = "screenshot.png";
        public float delay = 0.6f;

        private void Start()
        {
            StartCoroutine(Capture());
        }

        private System.Collections.IEnumerator Capture()
        {
            yield return new WaitForSeconds(delay);
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            var bytes = tex.EncodeToPNG();
            System.IO.File.WriteAllBytes(path, bytes);
            Object.Destroy(tex);
            Debug.Log("ScreenshotHelper saved " + path);
            Object.Destroy(gameObject);
        }
    }
}
