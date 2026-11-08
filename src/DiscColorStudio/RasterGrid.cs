using System.Drawing;

namespace DiscColorStudio;

public static class RasterGrid
{
    public const int TileSize = 1024;
    public const int MaxLegacySize = 8192;
    public const int DefaultSize = 32768;
    public const int PreviewSize = 4096;
    public const int CanvasMm = 120;
    public const double MinLayerSizeMm = .0005;
    public static IReadOnlyList<int> Sizes { get; } =
        Array.AsReadOnly(new[] { 4096, 8192, 16384, 32768, 65536, 131072, 262144 });

    public static bool IsTiled(int size) => size > MaxLegacySize;
    public static bool UsesAdaptiveMapping(string? mode) => mode is "adaptive" or "detail";
    public static bool SupportsTiles(string mode) => mode is "nearest" or "dis" || UsesAdaptiveMapping(mode);
    public static int VariationRadius(int size) => Math.Max(1, (int)Math.Round(size * .12 / CanvasMm));
}

internal readonly record struct RasterRegion(int Size, Rectangle Bounds)
{
    public double Step => RasterGrid.CanvasMm / (double)Size;
    public static RasterRegion Full(int size) => new(size, new Rectangle(0, 0, size, size));
}
