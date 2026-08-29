using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.Msagl.Drawing;
using DrawingColor = System.Drawing.Color;

namespace DependencyGraph.UI
{
    /// <summary>
    /// Colours for the graph, picked to sit on whichever Visual Studio theme is active. MSAGL
    /// draws nodes and edges itself, so it cannot pick up the WPF theme brushes - the palette is
    /// chosen from the tool window background instead.
    /// </summary>
    internal class GraphPalette
    {
        public Color Background     { get; private set; }
        public Color NodeFill       { get; private set; }
        public Color NodeStroke     { get; private set; }
        public Color NodeText       { get; private set; }
        public Color RootFill       { get; private set; }
        public Color RootStroke     { get; private set; }
        public Color RootText       { get; private set; }
        public Color Edge           { get; private set; }

        /// <summary>Fill for projects that reference the selected one.</summary>
        public Color ReferencingFill { get; private set; }

        /// <summary>Fill for projects the selected one references.</summary>
        public Color ReferencedFill { get; private set; }

        public Color SelectedStroke  { get; private set; }
        public Color SelectedText    { get; private set; }

        /// <summary>Status bar field name - muted, so it reads as a caption.</summary>
        public Color StatLabel { get; private set; }

        /// <summary>Status bar field value - the accent, so the numbers carry the emphasis.</summary>
        public Color StatValue { get; private set; }

        public bool IsDark { get; private set; }

        /// <summary>Builds the palette for the theme Visual Studio is currently using.</summary>
        public static GraphPalette ForVsTheme()
        {
            // Catching out here, not inside IsDarkTheme: away from the shell (designer, a test
            // harness) the shell assembly may not load at all, and that failure is raised when
            // IsDarkTheme is jitted - too early for any try block inside it to help.
            try { return IsDarkTheme() ? Dark() : Light(); }
            catch { return Light(); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsDarkTheme()
        {
            DrawingColor background = VSColorTheme.GetThemedColor(
                EnvironmentColors.ToolWindowBackgroundColorKey);

            // Rec. 601 luma - good enough to tell a dark theme from a light one.
            double luma = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
            return luma < 0.5;
        }

        private static GraphPalette Light()
        {
            return new GraphPalette
            {
                IsDark          = false,
                Background      = Rgb(0xF5, 0xF5, 0xF5),
                NodeFill        = Rgb(0xDC, 0xE9, 0xF7),
                NodeStroke      = Rgb(0x6B, 0x8E, 0xB8),
                NodeText        = Rgb(0x1E, 0x1E, 0x1E),
                RootFill        = Rgb(0x3B, 0x6E, 0x35),
                RootStroke      = Rgb(0x2A, 0x50, 0x26),
                RootText        = Rgb(0xFF, 0xFF, 0xFF),
                Edge            = Rgb(0x70, 0x70, 0x70),
                ReferencingFill = Rgb(0xBF, 0xDD, 0xF5),
                ReferencedFill  = Rgb(0xFB, 0xD8, 0xA0),
                SelectedStroke  = Rgb(0xC0, 0x5A, 0x00),
                SelectedText    = Rgb(0x1E, 0x1E, 0x1E),
                StatLabel       = Rgb(0x55, 0x55, 0x55),
                StatValue       = Rgb(0x0E, 0x63, 0x8B),
            };
        }

        private static GraphPalette Dark()
        {
            return new GraphPalette
            {
                IsDark          = true,
                Background      = Rgb(0x25, 0x25, 0x26),
                NodeFill        = Rgb(0x2D, 0x4A, 0x6B),
                NodeStroke      = Rgb(0x56, 0x8A, 0xC4),
                NodeText        = Rgb(0xF1, 0xF1, 0xF1),
                RootFill        = Rgb(0x55, 0x6B, 0x2F),
                RootStroke      = Rgb(0x8A, 0xA8, 0x55),
                RootText        = Rgb(0xFF, 0xFF, 0xFF),
                Edge            = Rgb(0x9A, 0x9A, 0x9A),
                ReferencingFill = Rgb(0x1F, 0x5F, 0x7A),
                ReferencedFill  = Rgb(0x7A, 0x50, 0x1F),
                SelectedStroke  = Rgb(0xE8, 0x9B, 0x2E),
                SelectedText    = Rgb(0xF1, 0xF1, 0xF1),
                StatLabel       = Rgb(0x9A, 0x9A, 0x9A),
                StatValue       = Rgb(0x4E, 0xC9, 0xB0),
            };
        }

        private static Color Rgb(byte r, byte g, byte b)
        {
            return new Color(r, g, b);
        }
    }
}
