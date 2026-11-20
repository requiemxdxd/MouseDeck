using System.Windows.Media;

namespace MouseDeck.App;

/// <summary>
/// Provides native Windows TrueType-hinted system fonts (Segoe UI + Consolas)
/// that contain full Microsoft TrueType delta-hinting bytecode (fpgm/prep/cvt tables).
/// Combined with TextFormattingMode.Display and integer font sizes, every stem snaps
/// to an exact 1-pixel physical boundary at 96 DPI (1080p 100%) with zero blur.
/// </summary>
public static class FontLoader
{
    public static FontFamily AppFont { get; } = new("Segoe UI");
    public static FontFamily MonoFont { get; } = new("Consolas, Cascadia Mono");
}
