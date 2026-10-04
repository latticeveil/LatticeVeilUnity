using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace LatticeVeil.Launcher
{
    /// <summary>
    /// Generates a crisp 3D preview texture of the player model
    /// from a standard 64x64 or 64x32 skin texture with arbitrary 360-degree rotation.
    /// Handles base and overlay/jacket layers with realistic voxel/box projection.
    /// </summary>
    public static class PlayerSkinPreviewGenerator
    {
        private const int OutputWidth = 360;
        private const int OutputHeight = 320;

        /// <summary>MonoGame preview parity: output is also exposed as raw RGBA for GDI panels.</summary>
        public static int PreviewWidth => OutputWidth;
        public static int PreviewHeight => OutputHeight;

        /// <summary>Renders the preview once and writes a 32bpp top-down BGRA buffer for GDI,
        /// with the row flip and channel swap fused into a single pass (no intermediates).
        /// Returns false when the skin cannot render; destination must be W*H*4 bytes.</summary>
        public static bool TryBakePreviewBGRA(Texture2D skin, float yawDegrees, float pitchDegrees,
            bool showLayers, float zoom, byte[] destination, out int width, out int height)
        {
            width = PreviewWidth;
            height = PreviewHeight;
            if (destination == null || destination.Length < width * height * 4)
                return false;

            var tex = GeneratePreview(skin, yawDegrees, pitchDegrees, showLayers, zoom);
            if (tex == null)
                return false;

            var pixels = tex.GetPixels32();
            int w = tex.width, h = tex.height;
            for (int y = 0; y < h; y++) // flip: Unity rows are bottom-up, GDI wants top-down
            {
                var src = (h - 1 - y) * w;
                var dst = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    var c = pixels[src + x];
                    int i = dst + x * 4;
                    destination[i + 0] = c.b; destination[i + 1] = c.g;
                    destination[i + 2] = c.r; destination[i + 3] = c.a;
                }
            }
            UnityEngine.Object.Destroy(tex);
            return true;
        }

        public static Texture2D GeneratePreview(Texture2D skin, float yawDegrees = 0f)
        {
            return GeneratePreview(skin, yawDegrees, 0f);
        }

        public static Texture2D GeneratePreview(Texture2D skin, float yawDegrees, float pitchDegrees)
        {
            return GeneratePreview(skin, yawDegrees, pitchDegrees, true);
        }

        public static Texture2D GeneratePreview(Texture2D skin, float yawDegrees, float pitchDegrees, bool showLayers)
        {
            return GeneratePreview(skin, yawDegrees, pitchDegrees, showLayers, 1f);
        }

        /// <summary>Core renderer. zoom &lt;= 0 keeps the previous hard-wired scale (130).</summary>
        public static Texture2D GeneratePreview(Texture2D skin, float yawDegrees, float pitchDegrees, bool showLayers, float zoom)
        {
#if UNITY_EDITOR
            var fbxModelPreview = TryGenerateEditorFbxPreview(skin, yawDegrees, pitchDegrees);
            if (fbxModelPreview != null)
                return fbxModelPreview;
#endif

            var preview = new Texture2D(OutputWidth, OutputHeight, TextureFormat.RGBA32, false);
            preview.filterMode = FilterMode.Point;

            var pixels = new Color[OutputWidth * OutputHeight];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color(18f / 255f, 20f / 255f, 24f / 255f, 1f);

            if (skin == null)
            {
                skin = DefaultPlayerSkinFactory.CreateTexture();
            }

            var skinPixels = skin.GetPixels();
            var skinW = skin.width;
            var skinH = skin.height;

            Color GetSkinPixel(int x, int y)
            {
                if (x < 0 || x >= 64 || y < 0 || y >= 64) return Color.clear;
                if (skinH == 32 && y >= 32) return Color.clear;
                int unityY = (skinH - 1) - (y % skinH);
                int index = unityY * skinW + (x % skinW);
                return (index >= 0 && index < skinPixels.Length) ? skinPixels[index] : Color.clear;
            }

            DrawPreviewGrid(pixels);
            DrawPedestalShadow(pixels, OutputWidth / 2, OutputHeight - 58, 58, 14);

            RenderContinuousModel(pixels, GetSkinPixel, yawDegrees, pitchDegrees, showLayers, zoom);

            preview.SetPixels(pixels);
            preview.Apply();
            return preview;
        }

#if UNITY_EDITOR
        private static Texture2D TryGenerateEditorFbxPreview(Texture2D skin, float yawDegrees, float pitchDegrees)
        {
            if (skin == null)
                skin = DefaultPlayerSkinFactory.CreateTexture();

            var modelPath = Path.Combine(Application.dataPath, "model.fbx");
            if (!File.Exists(modelPath))
                return null;

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/model.fbx");
            if (asset == null)
                return null;

            var root = new GameObject("ModelPreviewRoot");
            var instance = UnityEngine.Object.Instantiate(asset, root.transform);
            instance.name = "ModelPreviewInstance";

            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                UnityEngine.Object.DestroyImmediate(root);
                return null;
            }

            var previewMaterial = new Material(Shader.Find("Standard"));
            previewMaterial.mainTexture = skin;
            previewMaterial.SetFloat("_Glossiness", 0.1f);
            previewMaterial.SetColor("_Color", Color.white);

            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    var mat = materials[i] ?? new Material(Shader.Find("Standard"));
                    mat.mainTexture = skin;
                    mat.SetFloat("_Glossiness", 0.1f);
                    mat.SetColor("_Color", Color.white);
                    materials[i] = mat;
                }
                renderer.sharedMaterials = materials;
            }

            var bounds = GetRendererBounds(instance.transform);
            var targetSize = bounds.size.magnitude;
            if (targetSize <= 0.0001f)
            {
                targetSize = 1f;
            }

            var cameraGo = new GameObject("ModelPreviewCamera");
            var camera = cameraGo.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(18f / 255f, 20f / 255f, 24f / 255f, 1f);
            camera.fieldOfView = 18f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 50f;
            camera.allowHDR = false;
            camera.allowMSAA = false;

            var renderTexture = new RenderTexture(OutputWidth, OutputHeight, 24, RenderTextureFormat.ARGB32);
            renderTexture.filterMode = FilterMode.Bilinear;
            renderTexture.antiAliasing = 2;
            camera.targetTexture = renderTexture;

            var center = bounds.center;
            var distance = targetSize * 4.5f / Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f);
            var desiredPos = center + new Vector3(0f, 0f, -distance);
            camera.transform.position = desiredPos;
            camera.transform.LookAt(center);

            instance.transform.localRotation = Quaternion.Euler(pitchDegrees, yawDegrees + 90f, 0f);
            instance.transform.position = Vector3.zero;

            var prev = RenderTexture.active;
            RenderTexture.active = renderTexture;
            camera.Render();
            var output = new Texture2D(OutputWidth, OutputHeight, TextureFormat.RGBA32, false);
            output.ReadPixels(new Rect(0, 0, OutputWidth, OutputHeight), 0, 0);
            output.Apply();
            RenderTexture.active = prev;

            output = FlipTextureVertically(output);

            UnityEngine.Object.DestroyImmediate(instance);
            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(cameraGo);
            UnityEngine.Object.Destroy(renderTexture);

            return output;
        }

        private static Bounds GetRendererBounds(Transform root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return new Bounds(Vector3.zero, Vector3.one);

            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        private static Texture2D FlipTextureVertically(Texture2D source)
        {
            var pixels = source.GetPixels();
            var flipped = new Color[pixels.Length];
            var width = source.width;
            var height = source.height;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var srcIndex = y * width + x;
                    var dstIndex = (height - 1 - y) * width + x;
                    flipped[dstIndex] = pixels[srcIndex];
                }
            }

            var result = new Texture2D(width, height, TextureFormat.RGBA32, false);
            result.SetPixels(flipped);
            result.Apply();
            return result;
        }
#endif

        private static void DrawPreviewGrid(Color[] pixels)
        {
            var gridColor = new Color(34f / 255f, 40f / 255f, 48f / 255f, 1f);
            for (int x = 0; x < OutputWidth; x += 24)
                DrawGridLine(pixels, x, OutputHeight - 44, OutputWidth / 2, OutputHeight - 12, gridColor);
            for (int x = OutputWidth; x >= 0; x -= 24)
                DrawGridLine(pixels, x, OutputHeight - 44, OutputWidth / 2, OutputHeight - 12, gridColor);
        }

        private static void DrawGridLine(Color[] pixels, int x0, int y0, int x1, int y1, Color color)
        {
            int steps = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0));
            for (int i = 0; i <= steps; i++)
            {
                float t = steps == 0 ? 0f : i / (float)steps;
                int x = Mathf.RoundToInt(Mathf.Lerp(x0, x1, t));
                int y = Mathf.RoundToInt(Mathf.Lerp(y0, y1, t));
                if (x >= 0 && x < OutputWidth && y >= 0 && y < OutputHeight)
                {
                    // Input coords are top-down screen space (like the pedestal
                    // shadow); the texture rasterizer is bottom-first, so flip
                    // the row or the grid lands above the model's head.
                    int ty = (OutputHeight - 1) - y;
                    pixels[ty * OutputWidth + x] = color;
                }
            }
        }

        private readonly struct PreviewFace
        {
            public readonly Vector3 A;
            public readonly Vector3 B;
            public readonly Vector3 C;
            public readonly Vector3 D;
            public readonly int SrcX;
            public readonly int SrcY;
            public readonly int SrcW;
            public readonly int SrcH;
            public readonly float Shade;
            public readonly float Depth;

            public PreviewFace(Vector3 a, Vector3 b, Vector3 c, Vector3 d,
                int srcX, int srcY, int srcW, int srcH, float shade)
            {
                A = a;
                B = b;
                C = c;
                D = d;
                SrcX = srcX;
                SrcY = srcY;
                SrcW = srcW;
                SrcH = srcH;
                Shade = shade;
                Depth = (a.z + b.z + c.z + d.z) * 0.25f;
            }
        }

        private static void RenderContinuousModel(Color[] pixels, Func<int, int, Color> getPixel, float yawDegrees, float pitchDegrees, bool showLayers, float zoom)
        {
            var faces = new List<PreviewFace>(48);
            var rootYaw = Mathf.PI / 2f - yawDegrees * Mathf.Deg2Rad;

            // Fixed orientation: model stands upright, rotated around Y for yaw, with slight pitch
            var bodyRotation = Quaternion.Euler(pitchDegrees, 0f, 0f);
            var rootRotation = Quaternion.Euler(0f, rootYaw * Mathf.Rad2Deg, 0f);

            // Center the model around its geometric center (not feet) for proper rotation
            // Model height is ~2.0 units, so center at Y=1.0
            var modelCenterOffset = new Vector3(0f, -1.0f, 0f);

            AddMappedBox(faces, rootRotation * bodyRotation, new Vector3(0f, 1.59f, 0f) + modelCenterOffset, new Vector3(0.42f, 0.42f, 0.42f),
                SkinMap(new RectInt(8, 0, 8, 8), new RectInt(16, 0, 8, 8), new RectInt(0, 8, 8, 8), new RectInt(8, 8, 8, 8), new RectInt(16, 8, 8, 8), new RectInt(24, 8, 8, 8)),
                SkinMap(new RectInt(40, 0, 8, 8), new RectInt(48, 0, 8, 8), new RectInt(32, 8, 8, 8), new RectInt(40, 8, 8, 8), new RectInt(48, 8, 8, 8), new RectInt(56, 8, 8, 8)), 1.0f, showLayers);
            AddMappedBox(faces, rootRotation * bodyRotation, new Vector3(0f, 1.07f, 0f) + modelCenterOffset, new Vector3(0.52f, 0.70f, 0.30f),
                SkinMap(new RectInt(20, 16, 8, 4), new RectInt(28, 16, 8, 4), new RectInt(16, 20, 4, 12), new RectInt(20, 20, 8, 12), new RectInt(28, 20, 4, 12), new RectInt(32, 20, 8, 12)),
                SkinMap(new RectInt(20, 32, 8, 4), new RectInt(28, 32, 8, 4), new RectInt(16, 36, 4, 12), new RectInt(20, 36, 8, 12), new RectInt(28, 36, 4, 12), new RectInt(32, 36, 8, 12)), 0.96f, showLayers);
            AddMappedBox(faces, rootRotation * bodyRotation, new Vector3(-0.38f, 1.06f, 0f) + modelCenterOffset, new Vector3(0.24f, 0.72f, 0.24f),
                SkinMap(new RectInt(44, 16, 4, 4), new RectInt(48, 16, 4, 4), new RectInt(40, 20, 4, 12), new RectInt(44, 20, 4, 12), new RectInt(48, 20, 4, 12), new RectInt(52, 20, 4, 12)),
                SkinMap(new RectInt(44, 32, 4, 4), new RectInt(48, 32, 4, 4), new RectInt(40, 36, 4, 12), new RectInt(44, 36, 4, 12), new RectInt(48, 36, 4, 12), new RectInt(52, 36, 4, 12)), 0.92f, showLayers);
            AddMappedBox(faces, rootRotation * bodyRotation, new Vector3(0.38f, 1.06f, 0f) + modelCenterOffset, new Vector3(0.24f, 0.72f, 0.24f),
                SkinMap(new RectInt(36, 48, 4, 4), new RectInt(40, 48, 4, 4), new RectInt(32, 52, 4, 12), new RectInt(36, 52, 4, 12), new RectInt(40, 52, 4, 12), new RectInt(44, 52, 4, 12)),
                SkinMap(new RectInt(52, 48, 4, 4), new RectInt(56, 48, 4, 4), new RectInt(48, 52, 4, 12), new RectInt(52, 52, 4, 12), new RectInt(56, 52, 4, 12), new RectInt(60, 52, 4, 12)), 0.92f, showLayers);
            AddMappedBox(faces, rootRotation * bodyRotation, new Vector3(-0.13f, 0.36f, 0f) + modelCenterOffset, new Vector3(0.22f, 0.72f, 0.24f),
                SkinMap(new RectInt(4, 16, 4, 4), new RectInt(8, 16, 4, 4), new RectInt(0, 20, 4, 12), new RectInt(4, 20, 4, 12), new RectInt(8, 20, 4, 12), new RectInt(12, 20, 4, 12)),
                SkinMap(new RectInt(4, 32, 4, 4), new RectInt(8, 32, 4, 4), new RectInt(0, 36, 4, 12), new RectInt(4, 36, 4, 12), new RectInt(8, 36, 4, 12), new RectInt(12, 36, 4, 12)), 0.88f, showLayers);
            AddMappedBox(faces, rootRotation * bodyRotation, new Vector3(0.13f, 0.36f, 0f) + modelCenterOffset, new Vector3(0.22f, 0.72f, 0.24f),
                SkinMap(new RectInt(20, 48, 4, 4), new RectInt(24, 48, 4, 4), new RectInt(16, 52, 4, 12), new RectInt(20, 52, 4, 12), new RectInt(24, 52, 4, 12), new RectInt(28, 52, 4, 12)),
                SkinMap(new RectInt(4, 48, 4, 4), new RectInt(8, 48, 4, 4), new RectInt(0, 52, 4, 12), new RectInt(4, 52, 4, 12), new RectInt(8, 52, 4, 12), new RectInt(12, 52, 4, 12)), 0.88f, showLayers);

            _zoomFactor = zoom; // applied inside Project(); reset when leaving scope
            try
            {
                RenderContinuousModelInner(pixels, getPixel, faces);
            }
            finally
            {
                _zoomFactor = 1f;
            }
        }

        private static void RenderContinuousModelInner(Color[] pixels, Func<int, int, Color> getPixel, List<PreviewFace> faces)
        {
            var depthBuffer = new float[OutputWidth * OutputHeight];
            for (int i = 0; i < depthBuffer.Length; i++) depthBuffer[i] = float.PositiveInfinity;

            foreach (var face in faces)
            {
                RasterizeFace(pixels, depthBuffer, getPixel, face);
            }
        }

        private static RectInt[] SkinMap(RectInt top, RectInt bottom, RectInt left, RectInt front, RectInt right, RectInt back)
        {
            return new[]
            {
                back, front, left, right, top, bottom
            };
        }

        private static void AddMappedBox(List<PreviewFace> faces, Quaternion rotation, Vector3 center, Vector3 size,
            RectInt[] baseMap, RectInt[] overlayMap, float shade, bool showLayers)
        {
            AddTexturedBox(faces, rotation, center, size, baseMap, shade);
            if (showLayers)
                AddTexturedBox(faces, rotation, center, size + new Vector3(0.026f, 0.026f, 0.026f), overlayMap, shade * 1.02f);
        }

        private static void AddTexturedBox(List<PreviewFace> faces, Quaternion rotation, Vector3 center, Vector3 size, RectInt[] uv, float shade)
        {
            var half = size * 0.5f;
            var vertices = new[]
            {
                new Vector3(-half.x, -half.y, -half.z),
                new Vector3( half.x, -half.y, -half.z),
                new Vector3( half.x,  half.y, -half.z),
                new Vector3(-half.x,  half.y, -half.z),
                new Vector3(-half.x, -half.y,  half.z),
                new Vector3( half.x, -half.y,  half.z),
                new Vector3( half.x,  half.y,  half.z),
                new Vector3(-half.x,  half.y,  half.z)
            };

            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = rotation * (vertices[i] + center);

            // Match the MonoGame preview face ordering exactly so the model does not flip upside-down.
            faces.Add(new PreviewFace(vertices[4], vertices[5], vertices[6], vertices[7], uv[0].x, uv[0].y, uv[0].width, uv[0].height, shade));
            faces.Add(new PreviewFace(vertices[1], vertices[0], vertices[3], vertices[2], uv[1].x, uv[1].y, uv[1].width, uv[1].height, shade * 0.72f));
            faces.Add(new PreviewFace(vertices[0], vertices[4], vertices[7], vertices[3], uv[2].x, uv[2].y, uv[2].width, uv[2].height, shade * 0.82f));
            faces.Add(new PreviewFace(vertices[5], vertices[1], vertices[2], vertices[6], uv[3].x, uv[3].y, uv[3].width, uv[3].height, shade * 0.92f));
            faces.Add(new PreviewFace(vertices[7], vertices[6], vertices[2], vertices[3], uv[4].x, uv[4].y, uv[4].width, uv[4].height, shade * 1.05f));
            faces.Add(new PreviewFace(vertices[0], vertices[1], vertices[5], vertices[4], uv[5].x, uv[5].y, uv[5].width, uv[5].height, shade * 0.62f));
        }

        private static void RasterizeFace(Color[] pixels, float[] depthBuffer, Func<int, int, Color> getPixel, PreviewFace face)
        {
            var a = Project(face.A);
            var b = Project(face.B);
            var c = Project(face.C);
            var d = Project(face.D);
            RasterizeTriangle(pixels, depthBuffer, getPixel, a, b, c, face, false);
            RasterizeTriangle(pixels, depthBuffer, getPixel, a, c, d, face, true);
        }

        private static Vector3 Project(Vector3 point)
        {
            float scale = 130f * Mathf.Max(0.2f, _zoomFactor);
            var projectedX = OutputWidth * 0.5f + point.x * scale;
            var projectedY = OutputHeight * 0.5f + point.y * scale - point.z * scale * 0.08f;
            return new Vector3(projectedX, projectedY, point.z);
        }

        [ThreadStatic] private static float _zoomFactor = 1f;

        private static void RasterizeTriangle(Color[] pixels, float[] depthBuffer, Func<int, int, Color> getPixel,
            Vector3 a, Vector3 b, Vector3 c, PreviewFace face, bool secondTriangle)
        {
            float minX = Mathf.Max(0f, Mathf.Min(a.x, Mathf.Min(b.x, c.x)));
            float maxX = Mathf.Min(OutputWidth - 1f, Mathf.Max(a.x, Mathf.Max(b.x, c.x)));
            float minY = Mathf.Max(0f, Mathf.Min(a.y, Mathf.Min(b.y, c.y)));
            float maxY = Mathf.Min(OutputHeight - 1f, Mathf.Max(a.y, Mathf.Max(b.y, c.y)));
            float area = Edge(a, b, c);
            if (Mathf.Abs(area) < 0.001f) return;

            int startX = Mathf.FloorToInt(minX);
            int endX = Mathf.CeilToInt(maxX);
            int startY = Mathf.FloorToInt(minY);
            int endY = Mathf.CeilToInt(maxY);
            for (int y = startY; y <= endY; y++)
            for (int x = startX; x <= endX; x++)
            {
                var sample = new Vector3(x + 0.5f, y + 0.5f, 0f);
                float w0 = Edge(b, c, sample) / area;
                float w1 = Edge(c, a, sample) / area;
                float w2 = Edge(a, b, sample) / area;
                if (w0 < 0f || w1 < 0f || w2 < 0f) continue;

                float depth = a.z * w0 + b.z * w1 + c.z * w2;
                int index = y * OutputWidth + x;
                if (depth >= depthBuffer[index]) continue;
                depthBuffer[index] = depth;

                float u = secondTriangle ? w1 : w1 + w2;
                // Texture rectangles use top-left image coordinates; the projected
                // box vertices are bottom-left first, so the vertical coordinate is inverted.
                float v = secondTriangle ? w0 : w0 + w1;
                int sourceX = face.SrcX + Mathf.Clamp(Mathf.FloorToInt(u * face.SrcW), 0, face.SrcW - 1);
                int sourceY = face.SrcY + Mathf.Clamp(Mathf.FloorToInt(v * face.SrcH), 0, face.SrcH - 1);
                var color = getPixel(sourceX, sourceY);
                if (color.a < 0.05f) continue;
                depthBuffer[index] = depth;
                color.r *= face.Shade;
                color.g *= face.Shade;
                color.b *= face.Shade;
                pixels[index] = new Color(color.r, color.g, color.b, color.a);
            }
        }

        private static float Edge(Vector3 a, Vector3 b, Vector3 point)
        {
            return (point.x - a.x) * (b.y - a.y) - (point.y - a.y) * (b.x - a.x);
        }

        private static void RenderFrontView(Color[] pixels, Func<int, int, Color> getPixel, int cx, int cy, int s)
        {
            // Left Arm
            DrawFace(pixels, getPixel, cx + 28, cy + 56, s, 36, 52, 4, 12, 1.0f);
            DrawFace(pixels, getPixel, cx + 28, cy + 56, s, 52, 52, 4, 12, 1.0f);

            // Right Arm
            DrawFace(pixels, getPixel, cx - 56, cy + 56, s, 44, 20, 4, 12, 1.0f);
            DrawFace(pixels, getPixel, cx - 56, cy + 56, s, 44, 36, 4, 12, 1.0f);

            // Left Leg
            DrawFace(pixels, getPixel, cx, cy + 140, s, 20, 52, 4, 12, 1.0f);
            DrawFace(pixels, getPixel, cx, cy + 140, s, 4, 52, 4, 12, 1.0f);

            // Right Leg
            DrawFace(pixels, getPixel, cx - 28, cy + 140, s, 4, 20, 4, 12, 1.0f);
            DrawFace(pixels, getPixel, cx - 28, cy + 140, s, 4, 36, 4, 12, 1.0f);

            // Torso
            DrawFace(pixels, getPixel, cx - 28, cy + 56, s, 20, 20, 8, 12, 1.0f);
            DrawFace(pixels, getPixel, cx - 28, cy + 56, s, 20, 36, 8, 12, 1.0f);

            // Head
            DrawFace(pixels, getPixel, cx - 28, cy, s, 8, 8, 8, 8, 1.0f);
            DrawFace(pixels, getPixel, cx - 28, cy, s, 40, 8, 8, 8, 1.0f);
        }

        private static void RenderBackView(Color[] pixels, Func<int, int, Color> getPixel, int cx, int cy, int s)
        {
            // Right Arm (shows back on left)
            DrawFace(pixels, getPixel, cx + 28, cy + 56, s, 52, 20, 4, 12, 0.95f);
            DrawFace(pixels, getPixel, cx + 28, cy + 56, s, 52, 36, 4, 12, 0.95f);

            // Left Arm (shows back on right)
            DrawFace(pixels, getPixel, cx - 56, cy + 56, s, 44, 52, 4, 12, 0.95f);
            DrawFace(pixels, getPixel, cx - 56, cy + 56, s, 60, 52, 4, 12, 0.95f);

            // Right Leg (back)
            DrawFace(pixels, getPixel, cx + 0, cy + 140, s, 12, 20, 4, 12, 0.95f);
            DrawFace(pixels, getPixel, cx + 0, cy + 140, s, 12, 36, 4, 12, 0.95f);

            // Left Leg (back)
            DrawFace(pixels, getPixel, cx - 28, cy + 140, s, 28, 52, 4, 12, 0.95f);
            DrawFace(pixels, getPixel, cx - 28, cy + 140, s, 12, 52, 4, 12, 0.95f);

            // Torso (back)
            DrawFace(pixels, getPixel, cx - 28, cy + 56, s, 32, 20, 8, 12, 0.95f);
            DrawFace(pixels, getPixel, cx - 28, cy + 56, s, 32, 36, 8, 12, 0.95f);

            // Head (back)
            DrawFace(pixels, getPixel, cx - 28, cy, s, 24, 8, 8, 8, 0.95f);
            DrawFace(pixels, getPixel, cx - 28, cy, s, 56, 8, 8, 8, 0.95f);
        }

        private static void RenderRightProfileView(Color[] pixels, Func<int, int, Color> getPixel, int cx, int cy, int s)
        {
            // Right Leg
            DrawFace(pixels, getPixel, cx - 14, cy + 140, s, 0, 20, 4, 12, 1.0f);
            DrawFace(pixels, getPixel, cx - 14, cy + 140, s, 0, 36, 4, 12, 1.0f);

            // Torso
            DrawFace(pixels, getPixel, cx - 14, cy + 56, s, 16, 20, 4, 12, 0.85f);
            DrawFace(pixels, getPixel, cx - 14, cy + 56, s, 16, 36, 4, 12, 0.85f);

            // Right Arm
            DrawFace(pixels, getPixel, cx - 14, cy + 56, s, 40, 20, 4, 12, 1.0f);
            DrawFace(pixels, getPixel, cx - 14, cy + 56, s, 40, 36, 4, 12, 1.0f);

            // Head
            DrawFace(pixels, getPixel, cx - 28, cy, s, 0, 8, 8, 8, 1.0f);
            DrawFace(pixels, getPixel, cx - 28, cy, s, 32, 8, 8, 8, 1.0f);
        }

        private static void RenderLeftProfileView(Color[] pixels, Func<int, int, Color> getPixel, int cx, int cy, int s)
        {
            // Left Leg
            DrawFace(pixels, getPixel, cx - 14, cy + 140, s, 24, 52, 4, 12, 1.0f);
            DrawFace(pixels, getPixel, cx - 14, cy + 140, s, 8, 52, 4, 12, 1.0f);

            // Torso
            DrawFace(pixels, getPixel, cx - 14, cy + 56, s, 28, 20, 4, 12, 0.85f);
            DrawFace(pixels, getPixel, cx - 14, cy + 56, s, 28, 36, 4, 12, 0.85f);

            // Left Arm
            DrawFace(pixels, getPixel, cx - 14, cy + 56, s, 40, 52, 4, 12, 1.0f);
            DrawFace(pixels, getPixel, cx - 14, cy + 56, s, 56, 52, 4, 12, 1.0f);

            // Head
            DrawFace(pixels, getPixel, cx - 28, cy, s, 16, 8, 8, 8, 1.0f);
            DrawFace(pixels, getPixel, cx - 28, cy, s, 48, 8, 8, 8, 1.0f);
        }

        private static void RenderFrontRightView(Color[] pixels, Func<int, int, Color> getPixel, int cx, int cy, int s)
        {
            // Left Arm
            DrawFace(pixels, getPixel, cx + 24, cy + 58, s, 36, 52, 4, 12, 0.9f);
            DrawFace(pixels, getPixel, cx + 24, cy + 58, s, 52, 52, 4, 12, 0.9f);

            // Left Leg
            DrawFace(pixels, getPixel, cx + 6, cy + 142, s, 20, 52, 4, 12, 0.95f);
            DrawFace(pixels, getPixel, cx + 6, cy + 142, s, 4, 52, 4, 12, 0.95f);

            // Right Leg
            DrawFace(pixels, getPixel, cx - 22, cy + 142, s, 4, 20, 4, 12, 1.0f);
            DrawFace(pixels, getPixel, cx - 22, cy + 142, s, 4, 36, 4, 12, 1.0f);

            // Torso
            DrawFace(pixels, getPixel, cx - 28, cy + 58, s, 20, 20, 8, 12, 1.0f);
            DrawFace(pixels, getPixel, cx - 28, cy + 58, s, 20, 36, 8, 12, 1.0f);

            // Head
            DrawFace(pixels, getPixel, cx - 28, cy, s, 8, 8, 8, 8, 1.0f);
            DrawFace(pixels, getPixel, cx - 28, cy, s, 40, 8, 8, 8, 1.0f);

            // Right Arm
            DrawFace(pixels, getPixel, cx - 56, cy + 58, s, 44, 20, 4, 12, 1.0f);
            DrawFace(pixels, getPixel, cx - 56, cy + 58, s, 44, 36, 4, 12, 1.0f);
        }

        private static void RenderFrontLeftView(Color[] pixels, Func<int, int, Color> getPixel, int cx, int cy, int s)
        {
            // Right Arm
            DrawFace(pixels, getPixel, cx - 52, cy + 58, s, 44, 20, 4, 12, 0.9f);
            DrawFace(pixels, getPixel, cx - 52, cy + 58, s, 44, 36, 4, 12, 0.9f);

            // Right Leg
            DrawFace(pixels, getPixel, cx - 34, cy + 142, s, 4, 20, 4, 12, 0.95f);
            DrawFace(pixels, getPixel, cx - 34, cy + 142, s, 4, 36, 4, 12, 0.95f);

            // Left Leg
            DrawFace(pixels, getPixel, cx - 6, cy + 142, s, 20, 52, 4, 12, 1.0f);
            DrawFace(pixels, getPixel, cx - 6, cy + 142, s, 4, 52, 4, 12, 1.0f);

            // Torso
            DrawFace(pixels, getPixel, cx - 28, cy + 58, s, 20, 20, 8, 12, 1.0f);
            DrawFace(pixels, getPixel, cx - 28, cy + 58, s, 20, 36, 8, 12, 1.0f);

            // Head
            DrawFace(pixels, getPixel, cx - 28, cy, s, 8, 8, 8, 8, 1.0f);
            DrawFace(pixels, getPixel, cx - 28, cy, s, 40, 8, 8, 8, 1.0f);

            // Left Arm
            DrawFace(pixels, getPixel, cx + 24, cy + 58, s, 36, 52, 4, 12, 1.0f);
            DrawFace(pixels, getPixel, cx + 24, cy + 58, s, 52, 52, 4, 12, 1.0f);
        }

        private static void RenderBackRightView(Color[] pixels, Func<int, int, Color> getPixel, int cx, int cy, int s)
        {
            // Left Arm
            DrawFace(pixels, getPixel, cx - 52, cy + 58, s, 44, 52, 4, 12, 0.9f);
            DrawFace(pixels, getPixel, cx - 52, cy + 58, s, 60, 52, 4, 12, 0.9f);

            // Left Leg
            DrawFace(pixels, getPixel, cx - 34, cy + 142, s, 28, 52, 4, 12, 0.95f);
            DrawFace(pixels, getPixel, cx - 34, cy + 142, s, 12, 52, 4, 12, 0.95f);

            // Right Leg
            DrawFace(pixels, getPixel, cx - 6, cy + 142, s, 12, 20, 4, 12, 0.95f);
            DrawFace(pixels, getPixel, cx - 6, cy + 142, s, 12, 36, 4, 12, 0.95f);

            // Torso
            DrawFace(pixels, getPixel, cx - 28, cy + 58, s, 32, 20, 8, 12, 0.95f);
            DrawFace(pixels, getPixel, cx - 28, cy + 58, s, 32, 36, 8, 12, 0.95f);

            // Head
            DrawFace(pixels, getPixel, cx - 28, cy, s, 24, 8, 8, 8, 0.95f);
            DrawFace(pixels, getPixel, cx - 28, cy, s, 56, 8, 8, 8, 0.95f);

            // Right Arm
            DrawFace(pixels, getPixel, cx + 24, cy + 58, s, 52, 20, 4, 12, 0.95f);
            DrawFace(pixels, getPixel, cx + 24, cy + 58, s, 52, 36, 4, 12, 0.95f);
        }

        private static void RenderBackLeftView(Color[] pixels, Func<int, int, Color> getPixel, int cx, int cy, int s)
        {
            // Right Arm
            DrawFace(pixels, getPixel, cx + 24, cy + 58, s, 52, 20, 4, 12, 0.9f);
            DrawFace(pixels, getPixel, cx + 24, cy + 58, s, 52, 36, 4, 12, 0.9f);

            // Right Leg
            DrawFace(pixels, getPixel, cx + 6, cy + 142, s, 12, 20, 4, 12, 0.95f);
            DrawFace(pixels, getPixel, cx + 6, cy + 142, s, 12, 36, 4, 12, 0.95f);

            // Left Leg
            DrawFace(pixels, getPixel, cx - 22, cy + 142, s, 28, 52, 4, 12, 0.95f);
            DrawFace(pixels, getPixel, cx - 22, cy + 142, s, 12, 52, 4, 12, 0.95f);

            // Torso
            DrawFace(pixels, getPixel, cx - 28, cy + 58, s, 32, 20, 8, 12, 0.95f);
            DrawFace(pixels, getPixel, cx - 28, cy + 58, s, 32, 36, 8, 12, 0.95f);

            // Head
            DrawFace(pixels, getPixel, cx - 28, cy, s, 24, 8, 8, 8, 0.95f);
            DrawFace(pixels, getPixel, cx - 28, cy, s, 56, 8, 8, 8, 0.95f);

            // Left Arm
            DrawFace(pixels, getPixel, cx - 56, cy + 58, s, 44, 52, 4, 12, 0.95f);
            DrawFace(pixels, getPixel, cx - 56, cy + 58, s, 60, 52, 4, 12, 0.95f);
        }

        private static void DrawFace(Color[] target, Func<int, int, Color> getPixel,
            int screenX, int screenY, int scale,
            int srcX, int srcY, int srcW, int srcH, float shade)
        {
            for (int sy = 0; sy < srcH; sy++)
            {
                for (int sx = 0; sx < srcW; sx++)
                {
                    var col = getPixel(srcX + sx, srcY + sy);
                    if (col.a < 0.05f) continue;

                    var litCol = new Color(col.r * shade, col.g * shade, col.b * shade, col.a);

                    for (int py = 0; py < scale; py++)
                    {
                        for (int px = 0; px < scale; px++)
                        {
                            int tx = screenX + (sx * scale) + px;
                            int topDownY = screenY + (sy * scale) + py;
                            int ty = (OutputHeight - 1) - topDownY;

                            if (tx >= 0 && tx < OutputWidth && ty >= 0 && ty < OutputHeight)
                            {
                                int idx = ty * OutputWidth + tx;
                                float a = litCol.a;
                                target[idx] = new Color(
                                    litCol.r * a + target[idx].r * (1f - a),
                                    litCol.g * a + target[idx].g * (1f - a),
                                    litCol.b * a + target[idx].b * (1f - a),
                                    1f
                                );
                            }
                        }
                    }
                }
            }
        }

        private static void DrawPedestalShadow(Color[] target, int cx, int cy, int rx, int ry)
        {
            var shadowCol = new Color(0f, 0f, 0f, 0.45f);
            for (int y = -ry; y <= ry; y++)
            {
                for (int x = -rx; x <= rx; x++)
                {
                    float normX = (float)x / rx;
                    float normY = (float)y / ry;
                    float distSq = normX * normX + normY * normY;
                    if (distSq <= 1.0f)
                    {
                        int tx = cx + x;
                        int topDownY = cy + y;
                        int ty = (OutputHeight - 1) - topDownY;
                        if (tx >= 0 && tx < OutputWidth && ty >= 0 && ty < OutputHeight)
                        {
                            int idx = ty * OutputWidth + tx;
                            float a = shadowCol.a * (1.0f - Mathf.Sqrt(distSq) * 0.4f);
                            target[idx] = new Color(
                                target[idx].r * (1f - a),
                                target[idx].g * (1f - a),
                                target[idx].b * (1f - a),
                                target[idx].a + a
                            );
                        }
                    }
                }
            }
        }
    }
}

