using System.Drawing.Imaging;

namespace DiscColorStudio;

// A relative photographic rendering, not a measured physical mixing model.
// Oklab conversion: https://bottosson.github.io/posts/oklab/ (public-domain implementation).
public sealed class PhotoMapper
{
    private readonly record struct Sample(byte Id, double R, double G, double B, double Y);
    private readonly Sample[] colors;
    private readonly (int Low, int High)[] pairs = new (int, int)[32768];
    private readonly Dictionary<int, byte> exact;
    private readonly double darkest, brightest, gamma, chroma;
    private static readonly double[] Linear = Enumerable.Range(0, 256).Select(v =>
    { double x = v / 255.0; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); }).ToArray();

    public PhotoMapper(Design design, double? photoGamma = null, double? photoChroma = null)
    {
        colors = design.Palette.Where(s => design.AllowedIds.Contains(s.Id)).Select(s =>
        {
            var c = s.Color(design.MatchGain); double r = Linear[c.R], g = Linear[c.G], b = Linear[c.B];
            return new Sample((byte)s.Id, r, g, b, .2126*r + .7152*g + .0722*b);
        }).ToArray();
        darkest = Lightness(colors.Min(s => s.Y));
        // The neutral highlight anchor is the existing swatch nearest white, rather than a
        // saturated red that happens to have the largest luminance in this photographed set.
        int whiteId = Renderer.WhiteMapping(design).Id;
        double whiteY = colors.Single(s => s.Id == whiteId).Y;
        brightest = Lightness(whiteY > colors.Min(s => s.Y)+1e-6 ? whiteY : colors.Max(s => s.Y));
        gamma = photoGamma ?? design.PhotoGamma; chroma = photoChroma ?? design.PhotoChroma;
        exact = design.Palette.Where(s => design.AllowedIds.Contains(s.Id)).GroupBy(s => s.Color(design.MatchGain).ToArgb())
            .ToDictionary(g => g.Key, g => (byte)g.First().Id);
        int low = Array.FindIndex(colors, c => c.Y == colors.Min(s => s.Y));
        int high = Array.FindIndex(colors, c => c.Y == colors.Max(s => s.Y));
        var paletteLab = colors.Select(s => Oklab(s.R, s.G, s.B)).ToArray();
        for (int key = 0; key < pairs.Length; key++)
        {
            int r = ((key >> 10) & 31)*8+4, g = ((key >> 5) & 31)*8+4, b = (key & 31)*8+4;
            double y = TargetY(r, g, b); var lab = Oklab(Linear[r], Linear[g], Linear[b]);
            double minimumY = TargetY(r-4, g-4, b-4), maximumY = TargetY(Math.Min(255,r+3), Math.Min(255,g+3), Math.Min(255,b+3));
            double best = double.MaxValue; var chosen = (low, high);
            for (int i = 0; i < colors.Length; i++) for (int j = 0; j < colors.Length; j++)
            {
                var a = colors[i]; var z = colors[j];
                if (a.Y > minimumY || z.Y < maximumY || z.Y - a.Y < 1e-8) continue;
                double t = (y - a.Y) / (z.Y - a.Y);
                var mixed = Oklab(a.R*(1-t)+z.R*t, a.G*(1-t)+z.G*t, a.B*(1-t)+z.B*t);
                double da = mixed.A - lab.A*chroma, db = mixed.B - lab.B*chroma;
                double deltaA = paletteLab[i].A-paletteLab[j].A, deltaB = paletteLab[i].B-paletteLab[j].B;
                // Prefer quieter, similar-hue dots over strong complementary-color speckle.
                double distance = da*da + db*db + .65*t*(1-t)*(deltaA*deltaA+deltaB*deltaB);
                if (distance < best) { best = distance; chosen = (i, j); }
            }
            pairs[key] = chosen;
        }
    }
    public static double Luminance(Color c) => .2126*Linear[c.R] + .7152*Linear[c.G] + .0722*Linear[c.B];
    private static double Lightness(double y) => y <= 216.0/24389 ? y*(24389.0/27)/100 : (116*Math.Cbrt(y)-16)/100;
    private static double InverseLightness(double l) => l <= .08 ? l*100/(24389.0/27) : Math.Pow((l*100+16)/116, 3);
    private double TargetY(int r, int g, int b)
    {
        double source = Lightness(.2126*Linear[r] + .7152*Linear[g] + .0722*Linear[b]);
        // Retain headroom at both ends; no hard clipping to a single endpoint swatch.
        return InverseLightness(darkest + (.025 + .95*Math.Pow(source, gamma))*(brightest-darkest));
    }
    private static (double L, double A, double B) Oklab(double r, double g, double b)
    {
        double l = Math.Cbrt(.4122214708*r + .5363325363*g + .0514459929*b);
        double m = Math.Cbrt(.2119034982*r + .6806995451*g + .1073969566*b);
        double s = Math.Cbrt(.0883024619*r + .2817188376*g + .6299787005*b);
        return (.2104542553*l + .7936177850*m - .0040720468*s,
            1.9779984951*l - 2.4285922050*m + .4505937099*s,
            .0259040371*l + .7827717662*m - .8086757660*s);
    }
    public byte[] Quantize(Design design, Bitmap source, byte[]? owners = null, PhotoMapper[]? profiles = null) =>
        Quantize(design, source, RasterRegion.Full(source.Width), owners, profiles);

    internal unsafe byte[] Quantize(Design design, Bitmap source, RasterRegion region, byte[]? owners,
        PhotoMapper[]? profiles, bool applyDiscMask = true)
    {
        int width = source.Width, height = source.Height;
        byte[] result = new byte[width * height];
        double[] current = new double[width+2], next = new double[width+2];
        int low = Array.FindIndex(colors, c => c.Y == colors.Min(s => s.Y));
        int high = Array.FindIndex(colors, c => c.Y == colors.Max(s => s.Y));
        var locked = source.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            double ri2 = Math.Pow(design.Geometry.InnerRadiusMm, 2), ro2 = Math.Pow(design.Geometry.OuterRadiusMm, 2);
            for (int row = 0; row < height; row++)
            {
                byte* scan = (byte*)locked.Scan0 + row*locked.Stride;
                int direction = (row + region.Bounds.Top) % 2 == 0 ? 1 : -1;
                double y = ((row + region.Bounds.Top + .5) / region.Size - .5) * 120;
                for (int step = 0; step < width; step++)
                {
                    int col = direction == 1 ? step : width-1-step;
                    double x = ((col + region.Bounds.Left + .5) / region.Size - .5) * 120, radius2 = x*x+y*y;
                    byte id;
                    if (applyDiscMask && (radius2 < ri2 || radius2 > ro2)) id = 255;
                    else if (applyDiscMask && design.RegistrationMarks && Renderer.IsRegistration(x, y)) id = 13;
                    else
                    {
                        byte* p = scan+col*4;
                        int argb = unchecked((int)0xff000000) | p[2]<<16 | p[1]<<8 | p[0];
                        if (!exact.TryGetValue(argb, out id))
                        {
                            int owner = owners?[row*width+col] ?? 0;
                            var mapper = profiles is null ? this : profiles[owner];
                            double target = mapper.TargetY(p[2], p[1], p[0]);
                            var pair = mapper.pairs[(p[2]>>3)<<10 | (p[1]>>3)<<5 | (p[0]>>3)];
                            // LUT selects hue. Full 8-bit input sets the tone independently of its bucket.
                            if (target < colors[pair.Low].Y || target > colors[pair.High].Y) pair = (low, high);
                            double adjusted = target + current[col+1];
                            var a = colors[pair.Low]; var b = colors[pair.High];
                            var chosen = Math.Abs(adjusted-a.Y) <= Math.Abs(adjusted-b.Y) ? a : b;
                            id = chosen.Id; double error = adjusted-chosen.Y;
                            // Different image profiles must not exchange quantization error.
                            if (col+direction >= 0 && col+direction < width
                                && (owners is null || owners[row*width+col+direction] == owner)) current[col+1+direction] += error*7/16;
                            if (row+1 < height)
                            {
                                if (col-direction >= 0 && col-direction < width
                                    && (owners is null || owners[(row+1)*width+col-direction] == owner)) next[col+1-direction] += error*3/16;
                                if (owners is null || owners[(row+1)*width+col] == owner) next[col+1] += error*5/16;
                                if (col+direction >= 0 && col+direction < width
                                    && (owners is null || owners[(row+1)*width+col+direction] == owner)) next[col+1+direction] += error/16;
                            }
                        }
                    }
                    result[row*width+col] = id;
                }
                (current, next) = (next, current); Array.Clear(next);
            }
        }
        finally { source.UnlockBits(locked); }
        return result;
    }
}
