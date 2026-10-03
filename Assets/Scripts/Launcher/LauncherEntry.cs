using UnityEngine;

namespace LatticeVeil.Launcher
{
    /// <summary>
    /// Entry point for the Unity launcher.
    /// This script automatically sets up the launcher at runtime.
    /// Attach this to any GameObject in your scene (or create an empty GameObject with this script).
    /// </summary>
    public class LauncherEntry : MonoBehaviour
    {
        [Header("Launcher Configuration")]
        public bool autoSetupUI = true;

        private void Awake()
        {
            // Set application to run in background
            Application.runInBackground = true;

            // Prevent sleep mode
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

            // Set target frame rate
            Application.targetFrameRate = 60;

            // Floating panel mode: the same exe relaunched with --panel=versions
            // or --panel=skins shows that panel in its own real topmost window.
            if (FloatingPanelProgram.TryRunFromArgs())
            {
                gameObject.AddComponent<FloatingPanelProgram>();
                return;
            }

            if (autoSetupUI)
            {
                // Add the launcher UI component
                var launcherUI = gameObject.AddComponent<LauncherUI>();
            }
        }

        private void Start()
        {
            Debug.Log("LatticeVeil Unity Launcher starting...");
        }
    }
}