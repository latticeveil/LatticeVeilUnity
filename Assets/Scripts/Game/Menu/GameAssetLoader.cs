using System;
using System.IO;
using LatticeVeil.Core;
using UnityEngine;

namespace LatticeVeil.Game
{
    /// <summary>
    /// Runtime game-asset loader for the Unity version.
    ///
    /// Resolution order for a relative path such as "textures/menu/buttons/Quit.png":
    ///   1. Documents\LatticeVeil\Assets   (the installed game-asset location; always used by the exe)
    ///   2. Development MonoGame Defaults\Assets (handy while iterating in-editor)
    ///   3. StreamingAssets (bundled fallback)
    ///
    /// Textures are loaded from PNG bytes so the exe can read the loose asset folder
    /// the same way the MonoGame version does, without baking textures into the build.
    /// </summary>
    public static class GameAssetLoader
    {
        /// <summary>Returns the directory the asset was actually loaded from, or null.</summary>
        public static string LastSourceRoot { get; private set; }

        public static bool TryResolve(string relativePath, out string fullPath)
        {
            fullPath = null;
            if (string.IsNullOrWhiteSpace(relativePath))
                return false;

            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);

            string[] roots =
            {
                Paths.AssetsDir,                                  // Documents\LatticeVeil\Assets
                Paths.LocalAssetsDir,                             // dev MonoGame Defaults\Assets
                Application.streamingAssetsPath                   // bundled fallback
            };

            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root))
                    continue;

                var candidate = Path.Combine(root, normalized);
                if (File.Exists(candidate))
                {
                    fullPath = candidate;
                    LastSourceRoot = root;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Loads a PNG texture as a point-filtered Texture2D (pixel-art rendering,
        /// matching the MonoGame PointClamp sampler). Returns null when missing.
        /// </summary>
        public static Texture2D LoadTexture(string relativePath)
        {
            if (!TryResolve(relativePath, out var fullPath))
                return null;

            try
            {
                var bytes = File.ReadAllBytes(fullPath);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(bytes))
                {
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }

                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.Apply(false, true);
                return tex;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"GameAssetLoader: failed to load '{fullPath}': {ex.Message}");
                return null;
            }
        }

        /// <summary>True when every asset the main menu needs is present.</summary>
        public static bool MenuAssetsPresent()
        {
            string[] critical =
            {
                "textures/menu/backgrounds/MainMenu.png",
                "textures/menu/buttons/Singleplayer.png",
                "textures/menu/buttons/Multiplayer.png",
                "textures/menu/buttons/options.png",
                "textures/menu/buttons/Quit.png",
                "textures/menu/buttons/Profile.png",
                "textures/menu/buttons/screenshots.png"
            };

            foreach (var asset in critical)
            {
                if (!TryResolve(asset, out _))
                    return false;
            }

            return true;
        }
    }
}
