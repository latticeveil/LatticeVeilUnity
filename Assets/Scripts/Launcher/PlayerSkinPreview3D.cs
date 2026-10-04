using System;
using UnityEngine;

namespace LatticeVeil.Launcher
{
    /// <summary>
    /// GPU-accelerated 3D skin preview: renders the voxel player model with Unity's own
    /// renderer (offscreen camera -> RenderTexture -> one small readback per pose change),
    /// replacing the per-bake CPU rasterizer for smooth drag rotation. The scene is built
    /// once and reused; a pose change is a transform write plus a 360x320 readback.
    /// Returns false when GPU rendering is unavailable so callers can fall back to the
    /// CPU path in PlayerSkinPreviewGenerator.
    /// </summary>
    public static class PlayerSkinPreview3D
    {
        // Mirrors PlayerSkinPreviewGenerator's output size (kept literal so both stay
        // independent compile-time constants).
        private const int Width = 360;
        private const int Height = 320;
        private const float TexSize = 64f;       // skin textures are 64x64 nor 64x32
        private const float FrameHalfHeight = 320f / (2f * 130f); // CPU scale-parity framing (~1.2308)
        private const float OverlayScale = 1.05f; // CPU adds ~0.026 absolute per part
        private static readonly Color BackgroundColor = new Color(18f / 255f, 20f / 255f, 24f / 255f, 1f);
        private static readonly Color GridColor = new Color(34f / 255f, 40f / 255f, 48f / 255f, 1f);

        private class PreviewScene
        {
            public GameObject Root;       // rotated per pose; scaled by zoom
            public GameObject Overlay;    // second model tree, scaled up, toggled by LAYERS
            public Camera Camera;
            public RenderTexture Target;
            public Texture2D Readback;
            public Material ModelMaterial;  // texture * vertex color
            public Material FlatMaterial;   // vertex color only (floor grid)
            public Texture2D BoundSkin;
        }

        private static PreviewScene _scene;

        /// <summary>Renders the preview on the GPU and writes a 32bpp top-down BGRA buffer
        /// for GDI. Same contract as PlayerSkinPreviewGenerator.TryBakePreviewBGRA.</summary>
        public static bool TryBakePreviewBGRA(Texture2D skin, float yawDegrees, float pitchDegrees,
            bool showLayers, float zoom, byte[] destination, out int width, out int height)
        {
            width = Width;
            height = Height;
            if (skin == null || destination == null || destination.Length < width * height * 4)
                return false;

            try
            {
                if (!EnsureScene()) return false;

                var s = _scene;
                if (s.Root == null || s.Camera == null || s.Target == null || !s.Target.IsCreated())
                {
                    Shutdown(); // scene objects lost (e.g. after a domain reload)
                    if (!EnsureScene()) return false;
                    s = _scene;
                }
                if (s.BoundSkin != skin)
                {
                    s.ModelMaterial.mainTexture = skin;
                    s.BoundSkin = skin;
                }

                // Same rotation formula as the CPU renderer so drag direction and the
                // rest pose feel identical: Euler(pitch, 90 - yaw, 0) == yaw * pitch.
                s.Root.transform.localRotation = Quaternion.Euler(pitchDegrees, 90f - yawDegrees, 0f);
                float z = Mathf.Clamp(zoom, 0.2f, 3f);
                s.Root.transform.localScale = new Vector3(z, z, z);
                s.Overlay.SetActive(showLayers);

                // Activate only for our own render call: the launcher's main camera
                // must never see this scene in the normal frame loop.
                s.Root.SetActive(true);
                s.Camera.Render();
                s.Root.SetActive(false);

                // Small, throttled readback (only runs on pose change).
                var prevActive = RenderTexture.active;
                RenderTexture.active = s.Target;
                s.Readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                s.Readback.Apply(false);
                RenderTexture.active = prevActive;

                // GetRawTextureData<byte> is zero-alloc (Colors via GetPixels32 would allocate).
                // Layout: RGBA rows bottom-up (row 0 = image bottom); GDI wants top-down BGRA.
                var raw = s.Readback.GetRawTextureData<byte>();
                for (int y = 0; y < Height; y++)
                {
                    int srcRow = (Height - 1 - y) * Width * 4;
                    int dstRow = y * Width * 4;
                    for (int x = 0; x < Width; x++)
                    {
                        int sIdx = srcRow + x * 4;
                        int dIdx = dstRow + x * 4;
                        destination[dIdx + 0] = raw[sIdx + 2]; // B
                        destination[dIdx + 1] = raw[sIdx + 1]; // G
                        destination[dIdx + 2] = raw[sIdx + 0]; // R
                        destination[dIdx + 3] = raw[sIdx + 3]; // A
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[PlayerSkinPreview3D] GPU preview unavailable, using CPU fallback: {e.Message}");
                Shutdown();
                return false;
            }
        }

        /// <summary>Tears the GPU scene down (panel closed).</summary>
        public static void Shutdown()
        {
            if (_scene == null) return;
            var s = _scene;
            _scene = null;
            try
            {
                if (s.Camera != null) UnityEngine.Object.Destroy(s.Camera.gameObject);
                if (s.Target != null) UnityEngine.Object.Destroy(s.Target);
                if (s.Readback != null) UnityEngine.Object.Destroy(s.Readback);
                if (s.ModelMaterial != null) UnityEngine.Object.Destroy(s.ModelMaterial);
                if (s.FlatMaterial != null) UnityEngine.Object.Destroy(s.FlatMaterial);
            }
            catch (Exception) { /* shutting down; nothing to recover */ }
        }

        // ---------------- scene construction ----------------

        private static bool EnsureScene()
        {
            if (_scene != null) return true;

            // Built-in unlit shader that multiplies mesh vertex colors with the texture.
            // Falls back to the CPU renderer when stripping removed it from the build.
            var shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                UnityEngine.Debug.LogWarning("[PlayerSkinPreview3D] Sprites/Default shader unavailable; using CPU preview.");
                return false;
            }

            var modelMat = new Material(shader) { hideFlags = HideFlags.DontSave };
            var flatMat = new Material(shader) { hideFlags = HideFlags.DontSave };
            flatMat.mainTexture = Texture2D.whiteTexture;

            var root = new GameObject("SkinPreviewRoot");
            root.hideFlags = HideFlags.DontSave;

            var baseTree = new GameObject("Base").transform;
            baseTree.SetParent(root.transform, false);
            BuildModelTree(baseTree, modelMat, overlay: false);

            var overlayTree = new GameObject("Overlay").transform;
            overlayTree.SetParent(root.transform, false);
            overlayTree.localScale = new Vector3(OverlayScale, OverlayScale, OverlayScale);
            BuildModelTree(overlayTree, modelMat, overlay: true);

            // Radial floor grid at the feet (CPU-renderer parity; the grid it draws
            // at model feet level as a fan of thin lines).
            var gridGo = new GameObject("FloorGrid");
            gridGo.transform.SetParent(root.transform, false);
            var gridFilter = gridGo.AddComponent<MeshFilter>();
            gridFilter.sharedMesh = BuildFloorGridMesh();
            var gridRenderer = gridGo.AddComponent<MeshRenderer>();
            gridRenderer.sharedMaterial = flatMat;
            gridRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            gridRenderer.receiveShadows = false;

            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32)
            {
                hideFlags = HideFlags.DontSave,
                filterMode = FilterMode.Bilinear,
            };

            var cameraGo = new GameObject("SkinPreviewCamera");
            cameraGo.hideFlags = HideFlags.DontSave;
            var camera = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = FrameHalfHeight;
            camera.aspect = (float)Width / Height;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 50f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = BackgroundColor;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.cullingMask = ~0;
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            camera.targetTexture = rt;
            camera.transform.position = new Vector3(0f, 0f, -10f);
            camera.transform.rotation = Quaternion.identity;

            // Hidden by default (and the camera component disabled): the scene is
            // only rendered on demand inside TryBakePreviewBGRA.
            root.SetActive(false);
            camera.enabled = false;

            _scene = new PreviewScene
            {
                Root = root,
                Overlay = overlayTree.gameObject,
                Camera = camera,
                Target = rt,
                Readback = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.DontSave,
                    filterMode = FilterMode.Point,
                },
                ModelMaterial = modelMat,
                FlatMaterial = flatMat,
            };
            return true;
        }

        // ---------------- model construction ----------------

        private static void BuildModelTree(Transform parent, Material material, bool overlay)
        {
            // Same part layout, UV slots and face shading as the CPU renderer
            // (centers are already offset by the model's -1 center-of-rotation).
            (Vector3 center, Vector3 size, RectInt top, RectInt bottom, RectInt left,
                RectInt front, RectInt right, RectInt back, float shade)[] parts =
            {
                (new Vector3(0f, 0.59f, 0f),   new Vector3(0.42f, 0.42f, 0.42f),
                    new RectInt(8, 0, 8, 8),  new RectInt(16, 0, 8, 8), new RectInt(0, 8, 8, 8),
                    new RectInt(8, 8, 8, 8),  new RectInt(16, 8, 8, 8), new RectInt(24, 8, 8, 8), 1.00f),
                (new Vector3(0f, 0.07f, 0f),   new Vector3(0.52f, 0.70f, 0.30f),
                    new RectInt(20, 16, 8, 4), new RectInt(28, 16, 8, 4), new RectInt(16, 20, 4, 12),
                    new RectInt(20, 20, 8, 12), new RectInt(28, 20, 4, 12), new RectInt(32, 20, 8, 12), 0.96f),
                (new Vector3(-0.38f, 0.06f, 0f), new Vector3(0.24f, 0.72f, 0.24f),
                    new RectInt(44, 16, 4, 4), new RectInt(48, 16, 4, 4), new RectInt(40, 20, 4, 12),
                    new RectInt(44, 20, 4, 12), new RectInt(48, 20, 4, 12), new RectInt(52, 20, 4, 12), 0.92f),
                (new Vector3(0.38f, 0.06f, 0f), new Vector3(0.24f, 0.72f, 0.24f),
                    new RectInt(36, 48, 4, 4), new RectInt(40, 48, 4, 4), new RectInt(32, 52, 4, 12),
                    new RectInt(36, 52, 4, 12), new RectInt(40, 52, 4, 12), new RectInt(44, 52, 4, 12), 0.92f),
                (new Vector3(-0.13f, -0.64f, 0f), new Vector3(0.22f, 0.72f, 0.24f),
                    new RectInt(4, 16, 4, 4),  new RectInt(8, 16, 4, 4),  new RectInt(0, 20, 4, 12),
                    new RectInt(4, 20, 4, 12),  new RectInt(8, 20, 4, 12), new RectInt(12, 20, 4, 12), 0.88f),
                (new Vector3(0.13f, -0.64f, 0f), new Vector3(0.22f, 0.72f, 0.24f),
                    new RectInt(20, 48, 4, 4), new RectInt(24, 48, 4, 4), new RectInt(16, 52, 4, 12),
                    new RectInt(20, 52, 4, 12), new RectInt(24, 52, 4, 12), new RectInt(28, 52, 4, 12), 0.88f),
            };

            foreach (var part in parts)
            {
                var go = new GameObject(overlay ? "PartOverlay" : "Part");
                go.transform.SetParent(parent, false);
                var mesh = BuildPartMesh(part.center, part.size, part.top, part.bottom, part.left,
                    part.front, part.right, part.back, part.shade, overlay);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }

        /// <summary>Six submeshes in the CPU renderer's face order: +z(back), -z(front),
        /// -x(left), +x(right), +y(top), -y(bottom). Vertex colors carry the per-face shade.</summary>
        private static Mesh BuildPartMesh(Vector3 center, Vector3 size,
            RectInt top, RectInt bottom, RectInt left, RectInt front, RectInt right, RectInt back,
            float partShade, bool overlay)
        {
            float hx = size.x * 0.5f, hy = size.y * 0.5f, hz = size.z * 0.5f;

            // Corners per face in (bl, br, tr, tl) order viewed from outside.
            var faceCorners = new[]
            {
                new[] { new Vector3(-hx,-hy, hz), new Vector3( hx,-hy, hz), new Vector3( hx, hy, hz), new Vector3(-hx, hy, hz) }, // +z -> back rect
                new[] { new Vector3( hx,-hy,-hz), new Vector3(-hx,-hy,-hz), new Vector3(-hx, hy,-hz), new Vector3( hx, hy,-hz) }, // -z -> front rect
                new[] { new Vector3(-hx,-hy, hz), new Vector3(-hx,-hy,-hz), new Vector3(-hx, hy,-hz), new Vector3(-hx, hy, hz) }, // -x -> left rect
                new[] { new Vector3( hx,-hy,-hz), new Vector3( hx,-hy, hz), new Vector3( hx, hy, hz), new Vector3( hx, hy,-hz) }, // +x -> right rect
                new[] { new Vector3(-hx, hy, hz), new Vector3( hx, hy, hz), new Vector3( hx, hy,-hz), new Vector3(-hx, hy,-hz) }, // +y -> top rect
                new[] { new Vector3(-hx,-hy,-hz), new Vector3( hx,-hy,-hz), new Vector3( hx,-hy, hz), new Vector3(-hx,-hy, hz) }, // -y -> bottom rect
            };

            var rects = new[] { back, front, left, right, top, bottom };
            var shades = new[] { 1.00f, 0.72f, 0.82f, 0.92f, 1.05f, 0.62f };

            var verts = new Vector3[24];
            var uvs = new Vector2[24];
            var colors = new Color[24];
            for (int f = 0; f < 6; f++)
            {
                var rect = rects[f];
                float u0 = rect.x / TexSize;
                float u1 = (rect.x + rect.width) / TexSize;
                float vTop = 1f - rect.y / TexSize;                  // image-top row of the rect
                float vBottom = 1f - (rect.y + rect.height) / TexSize;
                var uvCorners = new[]
                {
                    new Vector2(u0, vBottom), new Vector2(u1, vBottom),
                    new Vector2(u1, vTop), new Vector2(u0, vTop),
                };

                // Overlay layer stacks the same texture rows 16px lower (jacket layer).
                if (overlay)
                {
                    float dy = 16f / TexSize;
                    for (int c = 0; c < 4; c++)
                        uvCorners[c] = new Vector2(uvCorners[c].x, uvCorners[c].y - dy);
                }

                float shade = partShade * shades[f];
                for (int c = 0; c < 4; c++)
                {
                    int i = f * 4 + c;
                    verts[i] = center + faceCorners[f][c];
                    uvs[i] = uvCorners[c];
                    colors[i] = new Color(shade, shade, shade, 1f);
                }
            }

            var mesh = new Mesh { hideFlags = HideFlags.DontSave };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.colors = colors;
            mesh.subMeshCount = 6;
            for (int f = 0; f < 6; f++)
            {
                int b = f * 4;
                mesh.SetIndices(new[] { b, b + 1, b + 2, b, b + 2, b + 3 }, MeshTopology.Triangles, f, false, 0);
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Radial fan of thin floor lines under the feet, matching the CPU grid.</summary>
        private static Mesh BuildFloorGridMesh()
        {
            const float y = -1.001f;      // feet plane
            const float radius = 0.95f;
            const float thickness = 0.012f;

            var verts = new System.Collections.Generic.List<Vector3>(13 * 8);
            var indices = new System.Collections.Generic.List<int>(13 * 12);
            for (int i = 0; i < 13; i++)
            {
                float ang = Mathf.PI * (i / 12f) + Mathf.PI; // back half keeping the fan subtle
                var dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang) * 0.45f);
                var perp = new Vector3(-dir.z, 0f, dir.x).normalized * thickness;
                var center = new Vector3(0f, y, 0f);
                var tip = dir * radius;

                int b = verts.Count;
                verts.Add(center - perp); verts.Add(center + perp);
                verts.Add(tip - perp);    verts.Add(tip + perp);
                indices.Add(b + 0); indices.Add(b + 1); indices.Add(b + 2);
                indices.Add(b + 0); indices.Add(b + 2); indices.Add(b + 3);
            }

            var colors = new Color[verts.Count];
            for (int i = 0; i < colors.Length; i++)
                colors[i] = GridColor;

            var mesh = new Mesh { hideFlags = HideFlags.DontSave };
            mesh.SetVertices(verts);
            mesh.SetIndices(indices, MeshTopology.Triangles, 0);
            mesh.colors = colors;
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
