using UnityEngine;

namespace LatticeVeil.Game
{
    /// <summary>
    /// Faithful port of the MonoGame UIManager.CreateMainMenuLayout math.
    ///
    /// All rects are in UI-layout space (the virtual viewport the MonoGame version
    /// lays out in): window size divided by the reference-resolution scale
    /// (min(w/1920, h/1080)). At 1920x1080 this yields exactly the same numbers
    /// as the MonoGame version's log ("Current UI Layout").
    /// </summary>
    public static class MainMenuLayout
    {
        public const float ReferenceWidth = 1920f;
        public const float ReferenceHeight = 1080f;

        public sealed class Rects
        {
            public Rect Singleplayer;
            public Rect Multiplayer;
            public Rect Options;
            public Rect Quit;
            public Rect Profile;
            public Rect Screenshots;
        }

        /// <summary>
        /// The MonoGame reference-resolution scale: fit entirely inside the window
        /// (min of the width/height factors), letterboxing the other axis.
        /// </summary>
        public static float ReferenceScale(float windowWidth, float windowHeight)
        {
            if (windowWidth <= 0f || windowHeight <= 0f)
                return 1f;

            return Mathf.Min(windowWidth / ReferenceWidth, windowHeight / ReferenceHeight);
        }

        /// <summary>Size of the virtual UI viewport for a given window size.</summary>
        public static Vector2 ViewportSize(float windowWidth, float windowHeight)
        {
            var scale = ReferenceScale(windowWidth, windowHeight);
            return new Vector2(
                Mathf.Max(1f, Mathf.Round(windowWidth / scale)),
                Mathf.Max(1f, Mathf.Round(windowHeight / scale)));
        }

        /// <summary>Port of UIManager.CreateMainMenuLayout. x/y are top-left based, like MonoGame.</summary>
        public static Rects Create(float uiWidth, float uiHeight)
        {
            var width = Mathf.Max(1f, uiWidth);
            var height = Mathf.Max(1f, uiHeight);
            var centerX = width / 2f;

            // Footer icons: BIGGER overall, hard-pinned to the exact corners
            // of the window (small fixed margin, generous icon size).
            var footerMarginX = 14f;
            var footerMarginY = 14f;
            var footerButtonSize = Mathf.Clamp(Mathf.Min(width, height) / 6.5f, 116f, 180f);

            var topMargin = Mathf.Clamp(height / 12f, 60f, 130f);
            var gapToFooter = Mathf.Clamp(height / 24f, 24f, 42f);
            var spacing = Mathf.Clamp(height / 60f, 14f, 24f);
            const int buttonsCount = 4;
            var bottomReserved = footerButtonSize + footerMarginY + gapToFooter;
            var availableHeight = Mathf.Max(300f, height - topMargin - bottomReserved);
            var buttonHeight = Mathf.Clamp((availableHeight - spacing * (buttonsCount - 1)) / buttonsCount, 88f, 136f);
            var preferredButtonWidth = Mathf.Round(buttonHeight * 3.1f);
            var maxButtonWidth = Mathf.Max(320f, Mathf.Min(560f, width - 140f));
            var minButtonWidth = Mathf.Min(320f, maxButtonWidth);
            var buttonWidth = Mathf.Clamp(preferredButtonWidth, minButtonWidth, maxButtonWidth);
            var totalHeight = buttonHeight * buttonsCount + spacing * (buttonsCount - 1);
            var startY = Mathf.Max(topMargin, (height - bottomReserved - totalHeight) / 2f);

            float ButtonX() => centerX - buttonWidth / 2f;

            return new Rects
            {
                Singleplayer = new Rect(ButtonX(), startY, buttonWidth, buttonHeight),
                Multiplayer = new Rect(ButtonX(), startY + (buttonHeight + spacing), buttonWidth, buttonHeight),
                Options = new Rect(ButtonX(), startY + 2f * (buttonHeight + spacing), buttonWidth, buttonHeight),
                Quit = new Rect(ButtonX(), startY + 3f * (buttonHeight + spacing), buttonWidth, buttonHeight),

                Profile = new Rect(
                    footerMarginX,
                    height - footerButtonSize - footerMarginY,
                    footerButtonSize,
                    footerButtonSize),

                Screenshots = new Rect(
                    width - footerButtonSize - footerMarginX,
                    height - footerButtonSize - footerMarginY,
                    footerButtonSize,
                    footerButtonSize)
            };
        }
    }
}
