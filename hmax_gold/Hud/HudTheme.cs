namespace cAlgo.Robots
{
    // Holographic-glass palette for the on-chart HUD.
    //
    // Colours are built through the explicit FromArgb(alpha, r, g, b) overload
    // rather than Color.FromHex: the panel needs a translucent fill, and the
    // hex string widths FromHex accepts are not documented for this platform
    // version, so an explicit alpha is both clearer and safer.
    //
    // State colours follow the MT4 EA's own border rule (MT4EAv4.txt:1371-1382)
    // so the escalation is familiar: cyan = stable, amber = caution, red = halt.
    internal static class HudTheme
    {
        // Surfaces. The panel backdrop is the most opaque layer and the section
        // cards the least, so the stack reads as lit glass over the chart rather
        // than a stack of opaque cards.
        public static readonly Color PanelFill = Rgba(7, 10, 18, 228);
        public static readonly Color HeaderFill = Rgba(16, 30, 52, 190);
        public static readonly Color SectionFill = Rgba(12, 20, 36, 150);
        public static readonly Color SectionBorder = Rgba(34, 56, 88, 225);
        public static readonly Color Track = Rgba(27, 43, 69, 190);
        public static readonly Color Hairline = Rgba(45, 70, 105, 210);

        // Text
        public static readonly Color TextPrimary = Rgba(230, 244, 255, 255);
        public static readonly Color TextDim = Rgba(124, 147, 176, 255);
        public static readonly Color TextFaint = Rgba(84, 104, 132, 255);

        // State
        public static readonly Color Cyan = Rgba(0, 229, 255, 255);
        public static readonly Color Amber = Rgba(255, 176, 32, 255);
        public static readonly Color Red = Rgba(255, 61, 113, 255);
        public static readonly Color Green = Rgba(0, 255, 163, 255);
        public static readonly Color Violet = Rgba(177, 76, 255, 255);

        // Metric weights: the three "levels" the panel escalates through.
        public static Color Level(int level)
        {
            switch (level)
            {
                case 1: return Amber;
                case 2: return Red;
                default: return Cyan;
            }
        }

        private static Color Rgba(int r, int g, int b, int a)
        {
            return Color.FromArgb(a, r, g, b);
        }
    }
}
