using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace DiscColorStudio;

public sealed class Renderer : IDisposable
{
    internal AngleColorEngine AngleColors { get; } = new();
    internal CancellationToken Cancellation { get; set; }
    internal bool UseGpu { get; }
    internal bool InteractivePreview { get; set; }
    internal int AdaptiveMapperBuildCount { get; private set; }
    public Renderer() : this(false) { }
    internal Renderer(bool useGpu) { UseGpu=useGpu; }
    private readonly Dictionary<string, Bitmap> comparisonPreviews = [];
    private readonly Dictionary<string,LadderField> ladderFields=[];
    private long ladderBytes;
    internal LadderField LadderField(string key,Func<LadderField> build)
    {
        if(ladderFields.TryGetValue(key,out var field))return field;
        field=build();long bytes=field.Bands.LongLength*17;
        while(ladderFields.Count>0 && ladderBytes+bytes>128L*1024*1024)
        {var old=ladderFields.First();ladderBytes-=old.Value.Bands.LongLength*17;ladderFields.Remove(old.Key);}
        ladderFields[key]=field;ladderBytes+=bytes;return field;
    }
    private long comparisonBytes;
    internal int ComparisonPatchCount { get; private set; }
    internal bool TryLayerPreview(string key, out Bitmap bitmap) => comparisonPreviews.TryGetValue(key, out bitmap!);
    internal void CacheLayerPreview(string key, Bitmap bitmap)
    {
        ComparisonPatchCount++;
        long bytes = (long)bitmap.Width * bitmap.Height * 4;
        while (comparisonPreviews.Count > 0 && comparisonBytes + bytes > 96L * 1024 * 1024)
        { var entry = comparisonPreviews.First(); comparisonBytes -= (long)entry.Value.Width * entry.Value.Height * 4; entry.Value.Dispose(); comparisonPreviews.Remove(entry.Key); }
        comparisonPreviews[key] = bitmap; comparisonBytes += bytes;
    }
    private readonly Dictionary<string, Bitmap> images = [];
    private readonly Dictionary<string, Bitmap> adjustedImages = [];
    private readonly Dictionary<string, Bitmap> previewSources = [];
    private long previewSourceBytes;
    private Bitmap PreviewSource(string asset, Bitmap original, float width, float height)
    {
        if(!InteractivePreview)return original;
        int edge=64;
        while(edge<Math.Max(width,height)*1.2f && edge<1024)edge*=2;
        int maximum=Math.Max(original.Width,original.Height);
        if(edge>=maximum)return original;
        string key=asset+":"+edge;
        if(previewSources.TryGetValue(key,out var cached))return cached;
        double scale=edge/(double)maximum;
        var small=new Bitmap(Math.Max(1,(int)Math.Ceiling(original.Width*scale)),Math.Max(1,(int)Math.Ceiling(original.Height*scale)),PixelFormat.Format32bppArgb);
        using(var g=Graphics.FromImage(small))
        {
            g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
            using var attributes=new ImageAttributes();attributes.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(original,new Rectangle(0,0,small.Width,small.Height),0,0,original.Width,original.Height,GraphicsUnit.Pixel,attributes);
        }
        long bytes=(long)small.Width*small.Height*4;
        while(previewSources.Count>0 && previewSourceBytes+bytes>64L*1024*1024)
        {var old=previewSources.First();previewSourceBytes-=(long)old.Value.Width*old.Value.Height*4;old.Value.Dispose();previewSources.Remove(old.Key);}
        previewSources[key]=small;previewSourceBytes+=bytes;return small;
    }
    private long adjustedBytes;
    internal int ColorEditRenderCount { get; private set; }
    private Bitmap Adjusted(string asset, Bitmap image, ColorMixer? mixer)
    {
        if(mixer is null || mixer.IsIdentity)return image;
        string key=asset+":"+image.Width+":"+image.Height+":"+System.Text.Json.JsonSerializer.Serialize(mixer,Design.Json);
        if(adjustedImages.TryGetValue(key,out var existing))return existing;
        long bytes=(long)image.Width*image.Height*4;
        while(adjustedImages.Count>0 && adjustedBytes+bytes>128L*1024*1024)
        {var entry=adjustedImages.First();adjustedBytes-=(long)entry.Value.Width*entry.Value.Height*4;entry.Value.Dispose();adjustedImages.Remove(entry.Key);}
        var result=UseGpu ? GpuPreview.RenderColors(image,mixer,Cancellation) : mixer.Render(image,Cancellation);
        if(Cancellation.IsCancellationRequested){result.Dispose();Cancellation.ThrowIfCancellationRequested();}
        adjustedImages[key]=result;adjustedBytes+=bytes;ColorEditRenderCount++;return result;
    }
    private string lutKey = "";
    private readonly byte[] lut = new byte[32768];
    private string photoKey = "";
    private PhotoMapper? photoMapper;
    private AdaptiveMapper? adaptiveMapper;
    private readonly Dictionary<string, PhotoMapper> profileCache = [];
    private readonly Dictionary<string, AdaptiveMapper> adaptiveCache = [];
    public AdaptiveMapper.Statistics? AdaptiveStatistics => adaptiveMapper?.LastStatistics;
    private byte[]? layerDitherMask;
    public byte[]? AdaptiveDitherMask => layerDitherMask ?? adaptiveMapper?.LastDitherMask;
    internal void SetLayerDitherMask(byte[] mask) => layerDitherMask = mask;
    internal long DecodedImageBytes => images.Values.Sum(image => (long)image.Width * image.Height * 5);
    private readonly Dictionary<string, (byte[] Alpha, bool Opaque)> alphaCache = [];
    public Bitmap Composite(Design d, int size, bool reviewPreview = false) => Composite(d, RasterRegion.Full(size), reviewPreview: reviewPreview);

    internal Bitmap Composite(Design d, RasterRegion region, IReadOnlyList<Layer>? layers = null, bool transparent = false, bool reviewPreview = false)
    {
        var bitmap = new Bitmap(region.Bounds.Width, region.Bounds.Height, PixelFormat.Format32bppArgb);
        try
        {
            DrawComposite(bitmap, d, region, layers, transparent, reviewPreview);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private void DrawComposite(Bitmap bitmap, Design d, RasterRegion region, IReadOnlyList<Layer>? layers, bool transparent, bool reviewPreview)
    {
        using var g = Graphics.FromImage(bitmap);
        g.Clear(transparent ? Color.Transparent : d.Palette.Single(s => s.Id == d.BackgroundId).Color(d.MatchGain));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float unit = region.Size / 120f;
        foreach (var layer in (layers ?? d.Layers).Where(l => l.Visible))
        {
            Cancellation.ThrowIfCancellationRequested();
            var saved = g.Save();
            if (RasterGrid.IsTiled(region.Size))
                g.TranslateTransform((float)((layer.X + 60) / region.Step - region.Bounds.X),
                    (float)((layer.Y + 60) / region.Step - region.Bounds.Y));
            else
                g.TranslateTransform(region.Size / 2f + (float)layer.X * unit - region.Bounds.X,
                    region.Size / 2f + (float)layer.Y * unit - region.Bounds.Y);
            g.RotateTransform((float)layer.Rotation);
            var rect = new RectangleF(-(float)layer.Width * unit / 2, -(float)layer.Height * unit / 2,
                (float)layer.Width * unit, (float)layer.Height * unit);
            if (layer.Kind == "image" && layer.AssetId is not null)
            {
                string asset = layer.AssetId;
                if (!images.TryGetValue(asset, out var image))
                {
                    using var memory = new MemoryStream(d.Assets[asset]); using var loaded = Image.FromStream(memory);
                    images[asset] = image = new Bitmap(loaded);
                }
                image=PreviewSource(asset,image,rect.Width,rect.Height);
                image=Adjusted(asset,image,layer.BypassColorMixer?null:layer.ColorMixer);
                float contrast = (float)(layer.Contrast * layer.InputExposure);
                float offset = (1 - (float)layer.Contrast) * .5f + (float)layer.Brightness / 255f;
                using var attributes = new ImageAttributes();
                attributes.SetColorMatrix(new ColorMatrix(new float[][] {
                    [contrast,0,0,0,0],[0,contrast,0,0,0],[0,0,contrast,0,0],[0,0,0,(float)layer.Opacity,0],[offset,offset,offset,0,1] }));
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                // GDI+ cannot reliably rasterize a rotated destination tens of thousands
                // of pixels wide. Clip the source to this tile, retaining interpolation
                // neighbours and the original affine transform (no resampling shortcut).
                var src = new RectangleF(0, 0, image.Width, image.Height);
                if (region.Size >= 65536)
                {
                    PointF[] corners = [new(0,0),new(bitmap.Width,0),new(0,bitmap.Height),new(bitmap.Width,bitmap.Height)];
                    using var inverse = g.Transform; inverse.Invert(); inverse.TransformPoints(corners);
                    float left = Math.Max(0, (float)Math.Floor((corners.Min(p=>p.X)-rect.Left)/rect.Width*image.Width)-4);
                    float top = Math.Max(0, (float)Math.Floor((corners.Min(p=>p.Y)-rect.Top)/rect.Height*image.Height)-4);
                    float right = Math.Min(image.Width, (float)Math.Ceiling((corners.Max(p=>p.X)-rect.Left)/rect.Width*image.Width)+4);
                    float bottom = Math.Min(image.Height, (float)Math.Ceiling((corners.Max(p=>p.Y)-rect.Top)/rect.Height*image.Height)+4);
                    if (right<=left || bottom<=top) { g.Restore(saved); continue; }
                    src = RectangleF.FromLTRB(left,top,right,bottom);
                }
                var dest = new RectangleF(rect.Left+src.Left/image.Width*rect.Width,rect.Top+src.Top/image.Height*rect.Height,
                    src.Width/image.Width*rect.Width,src.Height/image.Height*rect.Height);
                g.DrawImage(image,[new PointF(dest.Left,dest.Top),new PointF(dest.Right,dest.Top),new PointF(dest.Left,dest.Bottom)],
                    src,GraphicsUnit.Pixel,attributes);
            }
            else
            {
                Color color = layer.FillArgb is {} argb ? Color.FromArgb(argb) : d.Palette.Single(s => s.Id == layer.FillId).Color(d.MatchGain);
                using var brush = new SolidBrush(Color.FromArgb((int)Math.Round(layer.Opacity * 255), color));
                if (layer.Kind == "rectangle") g.FillRectangle(brush, rect);
                else if (layer.Kind == "ellipse") g.FillEllipse(brush, rect);
                else if (layer.Kind == "text")
                {
                    using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
                        Trimming = StringTrimming.EllipsisCharacter };
                    if (RasterGrid.IsTiled(region.Size))
                        DrawVectorText(g, layer, brush, rect, format, region.Size);
                    else
                    {
                        using var font = new Font(layer.FontFamily, Math.Max(1, rect.Height * .65f), FontStyle.Bold, GraphicsUnit.Pixel);
                        g.DrawString(layer.Text, font, brush, rect, format);
                    }
                }
            }
            g.Restore(saved);
        }
    }

    private static void DrawVectorText(Graphics graphics, Layer layer, Brush brush, RectangleF rect, StringFormat format, int size)
    {
        if (string.IsNullOrEmpty(layer.Text)) return;
        float scale = size / (float)RasterGrid.MaxLegacySize;
        var layout = new RectangleF(rect.X / scale, rect.Y / scale, rect.Width / scale, rect.Height / scale);
        using var font = new Font(layer.FontFamily, Math.Max(1, layout.Height * .65f), FontStyle.Bold, GraphicsUnit.Pixel);
        using var family = font.FontFamily;
        using var outline = new GraphicsPath();
        outline.AddString(layer.Text, family, (int)font.Style, font.Size, layout, format);
        using var transform = new Matrix();
        transform.Scale(scale, scale);
        outline.Transform(transform);
        graphics.SetClip(rect, CombineMode.Intersect);
        graphics.FillPath(brush, outline);
    }
    private void BuildLut(Design d)
    {
        string key = d.MatchGain.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":"
            + string.Join(";", d.AllowedIds.Select(id => id + ":" + d.Palette.Single(p => p.Id == id).Color(d.MatchGain).ToArgb()));
        if (key == lutKey) return;
        var candidates = d.Palette.Where(s => d.AllowedIds.Contains(s.Id)).Select(s => (s.Id, Color: s.Color(d.MatchGain))).ToArray();
        for (int index = 0; index < lut.Length; index++)
        {
            int r = ((index >> 10) & 31) * 8 + 4, g = ((index >> 5) & 31) * 8 + 4, b = (index & 31) * 8 + 4;
            double best = double.MaxValue; int id = d.BackgroundId;
            foreach (var c in candidates)
            {
                double dr = r - c.Color.R, dg = g - c.Color.G, db = b - c.Color.B;
                double distance = .30 * dr * dr + .59 * dg * dg + .11 * db * db;
                if (distance < best) { best = distance; id = c.Id; }
            }
            lut[index] = (byte)id;
        }
        lutKey = key;
    }
    public byte[] Quantize(Design d, Bitmap source) =>
        Quantize(d, source, RasterRegion.Full(source.Width), new Rectangle(0, 0, source.Width, source.Height));

    internal unsafe byte[] Quantize(Design d, Bitmap source, RasterRegion region, Rectangle core,
        IReadOnlyList<Layer>? layers = null, bool captureDither = true, Layer? owner = null)
    {
        layerDitherMask = null;
        if (owner is not null) d = d.WithImageColors(owner);
        string mode = owner is null ? d.QuantizationMode : d.ImageMode(owner);
        bool applyDiscMask = owner is null;
        if (d.ColorModelVersion>=2 || mode == "photo" || RasterGrid.UsesAdaptiveMapping(mode))
        {
            string key = System.Text.Json.JsonSerializer.Serialize(new { d.Palette, d.AllowedIds, d.MatchGain, d.PhotoGamma, d.PhotoChroma, d.DitherTolerance, d.FlatThreshold, d.QuantizationMode, d.ColorModelVersion,d.NeutralStrength,d.MixNeutralFlats });
            if(key!=photoKey){photoMapper=null;adaptiveMapper=null;photoKey=key;profileCache.Clear();}
            if (d.ColorModelVersion>=2 || RasterGrid.UsesAdaptiveMapping(mode))
            {
                AdaptiveMapper Profile(PhotoSettings settings)
                {
                    string cacheKey=key+System.Text.Json.JsonSerializer.Serialize(settings,Design.Json);
                    if(adaptiveCache.Remove(cacheKey,out var mapper)) { adaptiveCache.Add(cacheKey,mapper);return mapper; }
                    Cancellation.ThrowIfCancellationRequested();
                    mapper=new AdaptiveMapper(d,settings);
                    AdaptiveMapperBuildCount++;
                    if(adaptiveCache.Count>=12)adaptiveCache.Remove(adaptiveCache.First().Key);
                    adaptiveCache.Add(cacheKey,mapper);return mapper;
                }
                if (owner is not null)
                {
                    var mapper = adaptiveMapper = Profile(PhotoSettings.From(d, owner.Kind == "image" ? owner : null));
                    var map = mapper.Quantize(d, source, region, core, null, null, captureDither, applyDiscMask: false, cancellation: Cancellation, useGpu: UseGpu);
                    layerDitherMask = mapper.LastDitherMask;
                    return map;
                }
                adaptiveMapper=Profile(PhotoSettings.From(d));
                var (owners,profiles)=PhotoOwners(d,region,adaptiveMapper,Profile,layers);
                return adaptiveMapper.Quantize(d,source,region,core,owners,profiles,captureDither,cancellation:Cancellation,useGpu:UseGpu);
            }
            else
            {
                photoMapper ??= new PhotoMapper(d);
                if(profileCache.Count>64)profileCache.Clear();
                PhotoMapper Profile(PhotoSettings settings)
                {string k=FormattableString.Invariant($"{settings.Gamma:R}/{settings.Chroma:R}");if(!profileCache.TryGetValue(k,out var mapper))profileCache[k]=mapper=new PhotoMapper(d,settings.Gamma,settings.Chroma);return mapper;}
                if (owner is not null)
                    return Profile(PhotoSettings.From(d, owner.Kind == "image" ? owner : null))
                        .Quantize(d, source, region, null, null, applyDiscMask: false);
                var (owners,profiles)=PhotoOwners(d,region,photoMapper,Profile,layers);
                return photoMapper.Quantize(d,source,owners,profiles);
            }
        }
        BuildLut(d); int size = region.Size;
        var exact = d.Palette.Where(s => d.AllowedIds.Contains(s.Id)).GroupBy(s => s.Color(d.MatchGain).ToArgb())
            .ToDictionary(g => g.Key, g => (byte)g.First().Id);
        byte[] result = new byte[core.Width * core.Height];
        bool diffuse = mode == "dither";
        var colors = diffuse ? d.Palette.ToDictionary(s => (byte)s.Id, s => s.Color(d.MatchGain)) : null;
        float[] current = diffuse ? new float[(core.Width + 2) * 3] : [], next = diffuse ? new float[(core.Width + 2) * 3] : [];
        var locked = source.LockBits(new Rectangle(0, 0, source.Width, source.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            double ri2 = d.Geometry.InnerRadiusMm * d.Geometry.InnerRadiusMm, ro2 = d.Geometry.OuterRadiusMm * d.Geometry.OuterRadiusMm;
            for (int row = core.Top; row < core.Bottom; row++)
            {
                Cancellation.ThrowIfCancellationRequested();
                bool reverse = diffuse && row % 2 == 1;
                int direction = reverse ? -1 : 1;
                byte* scan = (byte*)locked.Scan0 + (row - region.Bounds.Top) * locked.Stride;
                double y = ((row + .5) / size - .5) * 120;
                for (int step = 0; step < core.Width; step++)
                {
                    int col = reverse ? core.Right - 1 - step : core.Left + step;
                    double x = ((col + .5) / size - .5) * 120, r2 = x * x + y * y;
                    byte id;
                    if (applyDiscMask && (r2 < ri2 || r2 > ro2)) id = 255;
                    else if (applyDiscMask && d.RegistrationMarks && IsRegistration(x, y)) id = 13;
                    else
                    {
                        byte* p = scan + (col - region.Bounds.Left) * 4;
                        int argb = unchecked((int)0xff000000) | p[2] << 16 | p[1] << 8 | p[0];
                        if (!exact.TryGetValue(argb, out id))
                        {
                            if (!diffuse) id = lut[(p[2] >> 3) << 10 | (p[1] >> 3) << 5 | (p[0] >> 3)];
                            else
                            {
                                int offset = (col - core.Left + 1) * 3;
                                float r = Math.Clamp(p[2] + current[offset], 0, 255);
                                float g = Math.Clamp(p[1] + current[offset + 1], 0, 255);
                                float b = Math.Clamp(p[0] + current[offset + 2], 0, 255);
                                id = lut[((int)r >> 3) << 10 | ((int)g >> 3) << 5 | ((int)b >> 3)];
                                var c = colors![id];
                                for (int channel = 0; channel < 3; channel++)
                                {
                                    float error = Math.Clamp(channel switch { 0 => r - c.R, 1 => g - c.G, _ => b - c.B }, -64, 64);
                                    current[offset + direction * 3 + channel] += error * (7f / 16);
                                    next[offset - direction * 3 + channel] += error * (3f / 16);
                                    next[offset + channel] += error * (5f / 16);
                                    next[offset + direction * 3 + channel] += error * (1f / 16);
                                }
                            }
                        }
                    }
                    result[(row - core.Top) * core.Width + col - core.Left] = id;
                }
                if (diffuse) { (current, next) = (next, current); Array.Clear(next); }
            }
        }
        finally { source.UnlockBits(locked); }
        return result;
    }

    internal static unsafe byte[] ReadAlpha(Bitmap bitmap)
    {
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var alpha = new byte[bounds.Width * bounds.Height];
        var bits = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int row = 0; row < bounds.Height; row++)
            {
                byte* scan = (byte*)bits.Scan0 + row * bits.Stride;
                for (int col = 0; col < bounds.Width; col++)
                    alpha[row * bounds.Width + col] = scan[col * 4 + 3];
            }
        }
        finally { bitmap.UnlockBits(bits); }
        return alpha;
    }
    public static (int Id, Color Color, double Distance) WhiteMapping(Design d)
    {
        return d.Palette.Where(s => d.AllowedIds.Contains(s.Id)).Select(s =>
        {
            var c = s.Color(d.MatchGain);
            double distance = .30 * (255 - c.R) * (255 - c.R) + .59 * (255 - c.G) * (255 - c.G) + .11 * (255 - c.B) * (255 - c.B);
            return (s.Id, Color: c, Distance: distance);
        }).MinBy(s => s.Distance);
    }
    public static bool IsRegistration(double x, double y)
    {
        double r2 = x * x + y * y;
        if (r2 < 51 * 51 || r2 > 53 * 53) return false;
        for (int q = 0; q < 4; q++)
        {
            (double u, double v) = q switch { 0 => (x, y), 1 => (y, -x), 2 => (-x, -y), _ => (-y, x) };
            if (Math.Abs(u - 52) >= .55) continue;
            for (int k = 0; k <= q; k++) if (Math.Abs(v - (k - q / 2.0) * 1.7) < .55) return true;
        }
        return false;
    }
    public static double SuggestExposure(Bitmap image)
    {
        int maximum = 0;
        int stride = Math.Max(1, (int)Math.Sqrt((long)image.Width * image.Height / 150000.0));
        for (int y = 0; y < image.Height; y += stride)
            for (int x = 0; x < image.Width; x += stride)
            { var p = image.GetPixel(x, y); if (p.A > 32) maximum = Math.Max(maximum, Math.Max(p.R, Math.Max(p.G, p.B))); }
        return maximum < 8 ? 1 : Math.Clamp(255.0 / maximum, 1, 8);
    }
    public static Bitmap Colorize(Design d, byte[] map, int size, double gain) => Colorize(d, map, size, size, gain);

    public static unsafe Bitmap Colorize(Design d, byte[] map, int width, int height, double gain, OutputCompensation? output = null)
    {
        var colors = d.Palette.ToDictionary(s => (byte)s.Id, s => (output?.Predict(s.Color(gain))??s.Color(gain)).ToArgb());
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var locked = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < height; y++)
            {
                int* scan = (int*)((byte*)locked.Scan0 + y * locked.Stride);
                for (int x = 0; x < width; x++) scan[x] = map[y * width + x] == 255 ? 0 : colors[map[y * width + x]];
            }
        }
        finally { bitmap.UnlockBits(locked); }
        return bitmap;
    }
    public static Bitmap BinaryMask(byte[] mask,int size) => BinaryMask(mask, size, size);

    public static unsafe Bitmap BinaryMask(byte[] mask,int width,int height)
    {
        var bitmap=new Bitmap(width,height,PixelFormat.Format32bppArgb);
        var bits=bitmap.LockBits(new Rectangle(0,0,width,height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
        try {for(int row=0;row<height;row++){int* line=(int*)((byte*)bits.Scan0+row*bits.Stride);for(int col=0;col<width;col++)line[col]=mask[row*width+col]!=0?unchecked((int)0xb0ffdf00):0;}}
        finally{bitmap.UnlockBits(bits);} return bitmap;
    }
    private unsafe (byte[]? Owners, T[]? Profiles) PhotoOwners<T>(Design d, RasterRegion region, T defaults,
        Func<PhotoSettings,T> create, IReadOnlyList<Layer>? candidates) where T:class
    {
        var layers = (candidates ?? d.Layers).Where(l => l.Visible && l.Kind == "image" && l.Opacity > 0 && l.AssetId is not null).ToArray();
        if (!layers.Any(l => l.PhotoGamma.HasValue || l.PhotoChroma.HasValue || l.DitherTolerance.HasValue || l.FlatThreshold.HasValue || l.NeutralStrength.HasValue || l.MixNeutralFlats.HasValue)) return (null, null);
        var bounds = region.Bounds;
        byte[] owners = new byte[bounds.Width * bounds.Height]; var profiles = new List<T> { defaults };
        var profileIds = new Dictionary<PhotoSettings, byte>(); var defaultSettings=PhotoSettings.From(d);
        double step = region.Step;
        foreach (var layer in layers)
        {
            var settings=PhotoSettings.From(d,layer); byte owner = 0;
            if (settings!=defaultSettings)
            {
                if (!profileIds.TryGetValue(settings, out owner))
                {
                    var mapper=create(settings);
                    owner = checked((byte)profiles.Count); profileIds[settings] = owner; profiles.Add(mapper);
                }
            }
            string asset = layer.AssetId!;
            if (!images.TryGetValue(asset, out var image))
            {
                using var memory = new MemoryStream(d.Assets[asset]); using var loaded = Image.FromStream(memory);
                images[asset] = image = new Bitmap(loaded);
            }
            if (!alphaCache.TryGetValue(asset, out var alpha))
            {
                byte[] values = new byte[image.Width*image.Height]; bool opaque = true;
                var bits = image.LockBits(new Rectangle(0,0,image.Width,image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int row = 0; row < image.Height; row++)
                    {
                        byte* scan = (byte*)bits.Scan0+row*bits.Stride;
                        for (int col = 0; col < image.Width; col++) { byte a = scan[col*4+3]; values[row*image.Width+col] = a; opaque &= a == 255; }
                    }
                }
                finally { image.UnlockBits(bits); }
                alphaCache[asset] = alpha = (values, opaque);
            }
            double angle = layer.Rotation*Math.PI/180, cos = Math.Cos(angle), sin = Math.Sin(angle);
            double hw = layer.Width/2, hh = layer.Height/2;
            double extentX = Math.Abs(cos)*hw+Math.Abs(sin)*hh, extentY = Math.Abs(sin)*hw+Math.Abs(cos)*hh;
            int left = Math.Clamp((int)Math.Floor((layer.X-extentX+60)/step),bounds.Left,bounds.Right);
            int right = Math.Clamp((int)Math.Ceiling((layer.X+extentX+60)/step),bounds.Left,bounds.Right);
            int top = Math.Clamp((int)Math.Floor((layer.Y-extentY+60)/step),bounds.Top,bounds.Bottom);
            int bottom = Math.Clamp((int)Math.Ceiling((layer.Y+extentY+60)/step),bounds.Top,bounds.Bottom);
            for (int row = top; row < bottom; row++)
            {
                double dx = (left+.5)*step-60-layer.X, dy = (row+.5)*step-60-layer.Y;
                double x = cos*dx+sin*dy, y = -sin*dx+cos*dy;
                for (int col = left; col < right; col++, x += cos*step, y -= sin*step)
                {
                    if (x < -hw || x >= hw || y < -hh || y >= hh) continue;
                    int tx = Math.Clamp((int)((x/hw+1)*.5*image.Width),0,image.Width-1);
                    int ty = Math.Clamp((int)((y/hh+1)*.5*image.Height),0,image.Height-1);
                    if (alpha.Opaque || alpha.Alpha[ty*image.Width+tx] != 0)
                        owners[(row-bounds.Top)*bounds.Width+col-bounds.Left] = owner;
                }
            }
        }
        return (owners, profiles.ToArray());
    }
    public void Dispose() { ladderFields.Clear();ladderBytes=0;AngleColors.Dispose();foreach (var b in images.Values) b.Dispose(); foreach(var b in adjustedImages.Values)b.Dispose();adjustedImages.Clear();foreach(var b in previewSources.Values)b.Dispose();previewSources.Clear();foreach (var b in comparisonPreviews.Values) b.Dispose(); comparisonPreviews.Clear(); images.Clear(); alphaCache.Clear(); profileCache.Clear(); adaptiveCache.Clear(); }
}
