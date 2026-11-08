namespace DiscColorStudio;

internal sealed class LayeredRaster
{
    private const long LegacyCacheLimit = 192L * 1024 * 1024;
    private readonly Design design;
    private readonly int size;
    private readonly (Layer Layer, Rectangle Bounds)[] layers;
    private readonly Dictionary<int, (Patch Patch, long LastUsed)> legacy = [];
    private long legacyBytes, cacheClock;

    private sealed record Patch(Rectangle Bounds, byte[] Alpha, byte[] Ids, byte[]? Dither)
    {
        public long Bytes => Alpha.LongLength + Ids.LongLength;
    }

    public LayeredRaster(Design design, int size, (Layer Layer, Rectangle Bounds)[] layers)
    {
        this.design = design;
        this.size = size;
        this.layers = layers;
    }

    public byte[] Render(Renderer renderer, Rectangle core, bool captureDither)
    {
        var result = new byte[core.Width * core.Height];
        Array.Fill(result, (byte)design.BackgroundId);
        byte[] dots = captureDither && design.HasAdaptiveLayers ? new byte[result.Length] : [];
        for (int i = 0; i < layers.Length; i++)
        {
            var (layer, layerBounds) = layers[i];
            var overlap = Rectangle.Intersect(core, layerBounds);
            if (overlap.Width <= 0 || overlap.Height <= 0) continue;

            int nativeSize = Math.Min(size, design.ImageRasterSize(layer));
            int factor = size / nativeSize;
            var bounds = Downsample(overlap, factor);
            string mode = design.ImageMode(layer);
            Patch patch = RasterGrid.SupportsTiles(mode)
                ? RenderPatch(renderer, i, nativeSize, bounds, dots.Length != 0)
                : LegacyPatch(renderer, i, nativeSize, Downsample(layerBounds, factor));

            for (int y = overlap.Top; y < overlap.Bottom; y++)
            {
                int sourceRow = (y / factor - patch.Bounds.Top) * patch.Bounds.Width;
                int targetRow = (y - core.Top) * core.Width - core.Left;
                for (int x = overlap.Left; x < overlap.Right; x++)
                {
                    int source = sourceRow + x / factor - patch.Bounds.Left;
                    if (patch.Alpha[source] == 0) continue;
                    int target = targetRow + x;
                    result[target] = patch.Ids[source];
                    if (dots.Length > 0) dots[target] = patch.Dither?[source] ?? 0;
                }
            }
        }
        ApplyDiscMask(core, result, dots);
        renderer.SetLayerDitherMask(dots);
        return result;
    }

    private Patch LegacyPatch(Renderer renderer, int index, int nativeSize, Rectangle bounds)
    {
        lock (legacy)
        {
            if (legacy.TryGetValue(index, out var entry))
            {
                legacy[index] = (entry.Patch, ++cacheClock);
                return entry.Patch;
            }
            // Keep a whole layer's diffusion rows together, even when the disc is exported in tiles.
            var patch = RenderPatch(renderer, index, nativeSize, bounds, false);
            while (legacy.Count > 0 && legacyBytes + patch.Bytes > LegacyCacheLimit)
            {
                var oldest = legacy.MinBy(pair => pair.Value.LastUsed);
                legacy.Remove(oldest.Key);
                legacyBytes -= oldest.Value.Patch.Bytes;
            }
            legacy[index] = (patch, ++cacheClock);
            legacyBytes += patch.Bytes;
            return patch;
        }
    }

    private Patch RenderPatch(Renderer renderer, int index, int nativeSize, Rectangle bounds, bool captureDither)
    {
        var layer = layers[index].Layer;
        byte[] alpha;
        using (var coverage = renderer.Composite(design, new RasterRegion(nativeSize, bounds), [layer], transparent: true))
            alpha = Renderer.ReadAlpha(coverage);
        if (alpha.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            return new Patch(bounds, alpha, [], null);

        bool adaptive = RasterGrid.UsesAdaptiveMapping(design.ImageMode(layer));
        int pad = adaptive ? RasterGrid.VariationRadius(nativeSize) : 0;
        var padded = Rectangle.Intersect(new Rectangle(0, 0, nativeSize, nativeSize), Rectangle.Inflate(bounds, pad, pad));
        int factor = size / nativeSize;
        var search = new Rectangle(padded.X * factor, padded.Y * factor, padded.Width * factor, padded.Height * factor);
        var underlays = layers.Take(index + 1).Where(l => l.Bounds.IntersectsWith(search)).Select(l => l.Layer).ToArray();
        var region = new RasterRegion(nativeSize, padded);
        using var colors = renderer.Composite(design, region, underlays);
        var ids = renderer.Quantize(design, colors, region, bounds, captureDither: captureDither && adaptive, owner: layer);
        return new Patch(bounds, alpha, ids, captureDither && adaptive ? renderer.AdaptiveDitherMask : null);
    }

    private static Rectangle Downsample(Rectangle bounds, int factor) =>
        Rectangle.FromLTRB(bounds.Left / factor, bounds.Top / factor,
            (bounds.Right + factor - 1) / factor, (bounds.Bottom + factor - 1) / factor);

    private void ApplyDiscMask(Rectangle core, byte[] result, byte[] dots)
    {
        double step = RasterGrid.CanvasMm / (double)size;
        double inner = design.Geometry.InnerRadiusMm, outer = design.Geometry.OuterRadiusMm;
        for (int y = core.Top; y < core.Bottom; y++)
        {
            double py = (y + .5) * step - 60;
            int row = (y - core.Top) * core.Width - core.Left;
            for (int x = core.Left; x < core.Right; x++)
            {
                double px = (x + .5) * step - 60;
                double r = px * px + py * py;
                if (r < inner * inner || r > outer * outer) result[row + x] = 255;
                else if (design.RegistrationMarks && Renderer.IsRegistration(px, py)) result[row + x] = 13;
                else continue;
                if (dots.Length > 0) dots[row + x] = 0;
            }
        }
    }
}
