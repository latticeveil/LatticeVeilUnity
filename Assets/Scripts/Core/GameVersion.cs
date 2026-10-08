using UnityEngine;

namespace LatticeVeil.Core
{
    /// <summary>
    /// Display version of the Unity game. Used for the window title
    /// ("LatticeVeil - V#") and later for update checks. Bump when shipping
    /// a new game build.
    /// </summary>
    public static class GameVersion
    {
        public const string Current = "0.1";

        /// <summary>Window title for the game, including the version.</summary>
        public static string WindowTitle =>
            (Debug.isDebugBuild ? "[DEV] " : string.Empty) + "LatticeVeil - V" + Current;
    }
}
