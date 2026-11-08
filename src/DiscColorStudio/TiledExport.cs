
namespace DiscColorStudio;

internal sealed class TiledExport
{
    private readonly Design design;
    private readonly int size;
    private readonly int pad;
    private readonly Rectangle canvas;
    private readonly (Layer Layer, Rectangle Bounds)[] layers;
    private readonly LayeredRaster? layerRaster;

    public TiledExport(Design design, int size)
    {
        this.design = design;
        this.size = size;
        pad = RasterGrid.UsesAdaptiveMapping(design.QuantizationMode) ? RasterGrid.VariationRadius(size) : 2;
        canvas = new Rectangle(0, 0, size, size);
        layers = design.Layers.Where(l => l.Visible && l.Opacity > 0)
            .Select(l => (l, BoundsFor(l))).Where(l => !l.Item2.IsEmpty).ToArray();
        if (design.HasImageOverrides) layerRaster = new LayeredRaster(design, size, layers);
    }

    private Rectangle BoundsFor(Layer layer)
    {
        double angle = layer.Rotation * Math.PI / 180;
        double cos = Math.Abs(Math.Cos(angle)), sin = Math.Abs(Math.Sin(angle));
        double halfWidth = (layer.Width * cos + layer.Height * sin) / 2;
        double halfHeight = (layer.Height * cos + layer.Width * sin) / 2;
        int samplingMargin = design.HasImageOverrides ? size / Math.Min(size, design.ImageRasterSize(layer)) : 0;
        return BoundsFor(layer.X, layer.Y, halfWidth, halfHeight, samplingMargin);
    }

    private Rectangle BoundsFor(double x, double y, double halfWidth, double halfHeight, int samplingMargin = 0)
    {
        double unit = size / (double)RasterGrid.CanvasMm;
        var rect = Rectangle.FromLTRB((int)Math.Floor((x + 60 - halfWidth) * unit) - 2,
            (int)Math.Floor((y + 60 - halfHeight) * unit) - 2,
            (int)Math.Ceiling((x + 60 + halfWidth) * unit) + 2,
            (int)Math.Ceiling((y + 60 + halfHeight) * unit) + 2);
        return Rectangle.Intersect(canvas, Rectangle.Inflate(rect, samplingMargin, samplingMargin));
    }

    internal Bitmap CompositeRegion(Renderer renderer, Rectangle bounds) =>
        renderer.Composite(design, new RasterRegion(size, bounds), ActiveLayers(bounds));

    private Layer[] ActiveLayers(Rectangle bounds) =>
        layers.Where(layer => layer.Bounds.IntersectsWith(bounds)).Select(layer => layer.Layer).ToArray();

    internal byte[] RenderRegion(Renderer renderer, Rectangle core, bool captureDither = false)
    {
        if (layerRaster is not null) return layerRaster.Render(renderer, core, captureDither);
        var bounds = Rectangle.Intersect(canvas, Rectangle.Inflate(core, pad, pad));
        var active = ActiveLayers(bounds);
        var region = new RasterRegion(size, bounds);
        using var bitmap = renderer.Composite(design, region, active);
        return renderer.Quantize(design, bitmap, region, core, active, captureDither);
    }

}
