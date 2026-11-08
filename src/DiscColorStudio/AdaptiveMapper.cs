using System.Buffers.Binary;
using System.Drawing.Imaging;

namespace DiscColorStudio;

public readonly record struct PhotoSettings(double Gamma, double Chroma, double Tolerance, double FlatThreshold,
    bool PrioritizeLightness = false, double NeutralStrength = .65, bool MixNeutralFlats = true, bool SolidOnly = false,
    OutputCompensation? OutputCompensation = null)
{
    public static PhotoSettings From(Design d, Layer? l = null) => new(l?.PhotoGamma ?? d.PhotoGamma,
        l?.PhotoChroma ?? d.PhotoChroma, l?.DitherTolerance ?? d.DitherTolerance, l?.FlatThreshold ?? d.FlatThreshold,
        (l is null ? d.QuantizationMode : d.ImageMode(l)) == "detail", l?.NeutralStrength ?? d.NeutralStrength,
        l?.MixNeutralFlats ?? d.MixNeutralFlats, (l is null ? d.QuantizationMode : d.ImageMode(l)) == "nearest",l?.OutputCompensation);
}

public sealed class AdaptiveMapper
{
    private readonly record struct Lab(double L, double A, double B);
    private readonly record struct Swatch(byte Id, Lab Lab);
    internal readonly record struct Plan(byte Solid, byte First, byte Second, double Weight,
        double SingleError, double MixedError, bool NeutralFlat = false);
    public sealed record Statistics(long PhotoPixels, long FlatPixels, long SolidColorPixels, long EdgePixels, long DitherPixels);
    private readonly Swatch[] colors;
    private readonly Dictionary<int, byte> exact;
    private readonly Lab black, white;
    private readonly PhotoSettings settings;
    private readonly NeutralMapper? neutralMapper;
    private readonly double lightnessWeight, chromaWeight;
    private int[]? planKeys;
    private Plan[]? planValues;
    private static readonly double[] Linear = Enumerable.Range(0,256).Select(i =>
    {double v=i/255.0; return v<=.04045 ? v/12.92 : Math.Pow((v+.055)/1.055,2.4);}).ToArray();
    private static readonly ushort[] Noise = LoadNoise();
    public Statistics LastStatistics { get; private set; } = new(0,0,0,0,0);
    public byte[] LastDitherMask { get; private set; } = [];

    public AdaptiveMapper(Design d, PhotoSettings? profile = null)
    {
        settings=profile ?? PhotoSettings.From(d);
        if(d.ColorModelVersion>=2)neutralMapper=new NeutralMapper(d,settings);
        lightnessWeight = settings.PrioritizeLightness ? 1.8 : 1.2;
        chromaWeight = settings.PrioritizeLightness ? .2 : 1;
        colors=d.Palette.Where(p=>d.AllowedIds.Contains(p.Id)).Select(p=>new Swatch((byte)p.Id,ToLab(p.Color(d.MatchGain)))).ToArray();
        black=colors.MinBy(p=>p.Lab.L).Lab;
        white=colors.Single(p=>p.Id==Renderer.WhiteMapping(d).Id).Lab;
        exact=d.Palette.Where(p=>d.AllowedIds.Contains(p.Id)).GroupBy(p=>p.Color(d.MatchGain).ToArgb()).ToDictionary(g=>g.Key,g=>(byte)g.First().Id);
        if(settings.OutputCompensation?.Active==true)exact.Clear();
    }
    private static ushort[] LoadNoise()
    {
        byte[] data=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"data","blue-noise-64.bin"));
        if(data.Length!=8192) throw new InvalidDataException(Ui.Text("网点采样表无效。"));
        return Enumerable.Range(0,4096).Select(i=>BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i*2,2))).ToArray();
    }
    private static Lab ToLab(Color c)=>ToLab(c.R,c.G,c.B);
    private static Lab ToLab(int r,int g,int b)
    {
        double rr=Linear[r],gg=Linear[g],bb=Linear[b];
        double l=Math.Cbrt(.4122214708*rr+.5363325363*gg+.0514459929*bb);
        double m=Math.Cbrt(.2119034982*rr+.6806995451*gg+.1073969566*bb);
        double s=Math.Cbrt(.0883024619*rr+.2817188376*gg+.6299787005*bb);
        return new(.2104542553*l+.7936177850*m-.0040720468*s,
            1.9779984951*l-2.4285922050*m+.4505937099*s,.0259040371*l+.7827717662*m-.8086757660*s);
    }
    private Lab Target(int r,int g,int b)
    {
        var p=ToLab(r,g,b); double t=Math.Pow(Math.Clamp(p.L,0,1),settings.Gamma);
        // Neutral endpoints are actual palette colors; a flat white/black needs no artificial headroom dots.
        return new(black.L+(white.L-black.L)*t,black.A+(white.A-black.A)*t+p.A*settings.Chroma,
            black.B+(white.B-black.B)*t+p.B*settings.Chroma);
    }
    private double Distance2(Lab p, Lab q) => lightnessWeight * (p.L - q.L) * (p.L - q.L)
        + chromaWeight * ((p.A - q.A) * (p.A - q.A) + (p.B - q.B) * (p.B - q.B));
    internal Plan PlanForTarget(Color target)=>Choose(ToLab(target));
    private Plan Choose(Lab target)
    {
        Span<int> nearest=stackalloc int[4]; Span<double> distances=stackalloc double[4];
        nearest.Fill(-1); distances.Fill(double.MaxValue);
        for(int i=0;i<colors.Length;i++)
        {
            double distance=Distance2(target,colors[i].Lab);
            for(int j=0;j<4;j++) if(distance<distances[j])
            {for(int k=3;k>j;k--){distances[k]=distances[k-1];nearest[k]=nearest[k-1];} distances[j]=distance;nearest[j]=i;break;}
        }
        byte solid=colors[nearest[0]].Id; var result=new Plan(solid,solid,solid,0,Math.Sqrt(distances[0]),Math.Sqrt(distances[0]));
        double score=distances[0], maximumEndpoint=Math.Sqrt(distances[0])+.08;
        for(int i=0;i<4;i++) for(int j=i+1;j<4;j++)
        {
            if(nearest[j]<0 || distances[j]>maximumEndpoint*maximumEndpoint) continue;
            var a=colors[nearest[i]]; var b=colors[nearest[j]];
            double dl=b.Lab.L-a.Lab.L,da=b.Lab.A-a.Lab.A,db=b.Lab.B-a.Lab.B;
            double length = lightnessWeight * dl * dl + chromaWeight * (da * da + db * db);
            if (length < 1e-10) continue;
            double t = Math.Clamp((lightnessWeight * (target.L - a.Lab.L) * dl
                + chromaWeight * ((target.A - a.Lab.A) * da + (target.B - a.Lab.B) * db)) / length, 0, 1);
            if(t<.08 || t>.92)continue;
            var mixed=new Lab(a.Lab.L+dl*t,a.Lab.A+da*t,a.Lab.B+db*t);
            double error=Distance2(target,mixed), candidate=error+.04*t*(1-t)*length;
            if(candidate>=score)continue;
            score=candidate;
            result=a.Id<b.Id ? new(solid,a.Id,b.Id,t,result.SingleError,Math.Sqrt(error))
                : new(solid,b.Id,a.Id,1-t,result.SingleError,Math.Sqrt(error));
        }
        return result;
    }
    private bool WorthDithering(Plan plan)=>plan.First!=plan.Second && plan.SingleError>settings.Tolerance/100
        && plan.SingleError-plan.MixedError>Math.Max(.001,settings.Tolerance/500);
    private static double NoiseAt(int x,int y,byte first,byte second)
    {int xx=(x+first*7+second*13)&63,yy=(y+first*11+second*3)&63;return (Noise[yy*64+xx]+.5)/4096;}

    internal unsafe byte[] Quantize(Design d, Bitmap source, RasterRegion region, Rectangle core,
        byte[]? owners, AdaptiveMapper[]? profiles, bool captureDither, bool applyDiscMask = true,
        CancellationToken cancellation = default, bool useGpu = false)
    {
        int size = region.Size, width = source.Width, height = source.Height;
        var bounds = region.Bounds;
        bool tiled = RasterGrid.IsTiled(size);
        int dotScale = tiled ? size / RasterGrid.MaxLegacySize : 1;
        byte[] result = new byte[core.Width * core.Height];
        byte[] ditherMask = captureDither ? new byte[result.Length] : [];
        var bits=source.LockBits(new Rectangle(0,0,width,height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        long total=0,flat=0,solid=0,edge=0,dots=0;
        try
        {
            Dictionary<int,Plan>? gpuPlans=null;
            if(useGpu && neutralMapper is not null && profiles is null && core.Width*core.Height>=4096 && GpuPreview.IsAvailable)
            {
                var unique=new HashSet<int>();
                for(int row=core.Top;row<core.Bottom;row++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    byte* scan=(byte*)bits.Scan0+(row-bounds.Top)*bits.Stride;
                    for(int col=core.Left;col<core.Right;col++)
                    {
                        byte* p=scan+(col-bounds.Left)*4;int rgb=unchecked((int)0xff000000)|p[2]<<16|p[1]<<8|p[0];
                        if(!exact.ContainsKey(rgb))unique.Add(rgb);
                    }
                }
                if(unique.Count>=256)gpuPlans=neutralMapper.Batch(unique.ToArray(),cancellation);
            }
            float[]? sigma = null;
            double ri2=d.Geometry.InnerRadiusMm*d.Geometry.InnerRadiusMm,ro2=d.Geometry.OuterRadiusMm*d.Geometry.OuterRadiusMm;
            for(int row=core.Top;row<core.Bottom;row++)
            {
                cancellation.ThrowIfCancellationRequested();
                int localRow = row - bounds.Top;
                byte* scan=(byte*)bits.Scan0+localRow*bits.Stride; double y=((row+.5)/size-.5)*120;
                for(int col=core.Left;col<core.Right;col++)
                {
                    int localCol = col - bounds.Left;
                    int localIndex = localRow * width + localCol;
                    int index = (row - core.Top) * core.Width + col - core.Left;
                    double x=((col+.5)/size-.5)*120,r2=x*x+y*y;byte id;
                    if(applyDiscMask && (r2<ri2 || r2>ro2))id=255;
                    else if(applyDiscMask && d.RegistrationMarks&&Renderer.IsRegistration(x,y))id=13;
                    else
                    {
                        byte* p=scan+localCol*4;int argb=unchecked((int)0xff000000)|p[2]<<16|p[1]<<8|p[0];
                        if(!exact.TryGetValue(argb,out id))
                        {
                            total++;var mapper=profiles is null ? this : profiles[owners?[localIndex]??0];
                            var plan=gpuPlans is not null ? gpuPlans[argb] : mapper.PlanForPixel(argb,true);id=plan.Solid;
                            if(!mapper.settings.SolidOnly)sigma ??= LocalVariation(bits,width,height,RasterGrid.VariationRadius(size),cancellation);
                            if(mapper.settings.SolidOnly)solid++;
                            else if(sigma![localIndex]<=mapper.settings.FlatThreshold && !plan.NeutralFlat)flat++;
                            else if(!mapper.WorthDithering(plan))solid++;
                            else if(IsEdge(bits,width,height,localRow,localCol,dotScale,Math.Max(18,mapper.settings.FlatThreshold*4)))edge++;
                            else
                            {
                                id = NoiseAt(col / dotScale, row / dotScale, plan.First, plan.Second) < plan.Weight
                                    ? plan.Second : plan.First;
                                if (captureDither) ditherMask[index] = 255;
                                dots++;
                            }
                        }
                    }
                    result[index]=id;
                }
            }
        }
        finally {source.UnlockBits(bits);}
        LastDitherMask=ditherMask;LastStatistics=new(total,flat,solid,edge,dots);return result;
    }
    private Plan PlanForPixel(int argb, bool cache)
    {
        Plan Compute()
        {
            if(neutralMapper is null)return Choose(Target((argb >> 16) & 255, (argb >> 8) & 255, argb & 255));
            var rgb=Color.FromArgb(argb);var plan=neutralMapper.Choose(rgb);
            return plan with {NeutralFlat=neutralMapper.MayMixFlat(rgb,plan)};
        }
        if (!cache) return Compute();
        planKeys ??= new int[65536];
        planValues ??= new Plan[planKeys.Length];
        int index = (int)(unchecked((uint)argb * 2654435761u) >> 16);
        if (planKeys[index] != argb)
        {
            planValues[index] = Compute();
            planKeys[index] = argb;
        }
        return planValues[index];
    }

    private static unsafe bool IsEdge(BitmapData bits,int width,int height,int row,int col,int distance,double threshold)
    {
        byte* p=(byte*)bits.Scan0+row*bits.Stride+col*4;
        foreach(var (dy,dx) in Neighbors)
        {
            int yy=row+dy*distance,xx=col+dx*distance;if((uint)yy>=(uint)height || (uint)xx>=(uint)width)continue;
            byte* q=(byte*)bits.Scan0+yy*bits.Stride+xx*4;
            double dr=p[2]-q[2],dg=p[1]-q[1],db=p[0]-q[0];
            if(.30*dr*dr+.59*dg*dg+.11*db*db>threshold*threshold)return true;
        }
        return false;
    }
    private static readonly (int Y,int X)[] Neighbors=[(0,-1),(0,1),(-1,0),(1,0)];
    private static unsafe float[] LocalVariation(BitmapData bits,int width,int height,int radius,CancellationToken cancellation)
    {
        double[] r=new double[width],g=new double[width],b=new double[width],q=new double[width];
        void AddRow(int row,int direction)
        {
            byte* scan=(byte*)bits.Scan0+row*bits.Stride;
            for(int col=0;col<width;col++)
            {byte* p=scan+col*4;double rr=p[2],gg=p[1],bb=p[0];r[col]+=direction*rr;g[col]+=direction*gg;b[col]+=direction*bb;q[col]+=direction*(30*rr*rr+59*gg*gg+11*bb*bb);}
        }
        for(int row=0;row<=Math.Min(height-1,radius);row++)AddRow(row,1);
        float[] result=new float[width*height];
        for(int row=0;row<height;row++)
        {
            cancellation.ThrowIfCancellationRequested();
            double sr=0,sg=0,sb=0,sq=0;
            for(int col=0;col<=Math.Min(width-1,radius);col++){sr+=r[col];sg+=g[col];sb+=b[col];sq+=q[col];}
            int rows=Math.Min(height-1,row+radius)-Math.Max(0,row-radius)+1;
            for(int col=0;col<width;col++)
            {
                int columns=Math.Min(width-1,col+radius)-Math.Max(0,col-radius)+1;double n=rows*columns;
                double variance = (sq * n - (30 * sr * sr + 59 * sg * sg + 11 * sb * sb)) / (100 * n * n);
                result[row*width+col]=(float)Math.Sqrt(Math.Max(0,variance));
                if(col-radius>=0){sr-=r[col-radius];sg-=g[col-radius];sb-=b[col-radius];sq-=q[col-radius];}
                if(col+radius+1<width){sr+=r[col+radius+1];sg+=g[col+radius+1];sb+=b[col+radius+1];sq+=q[col+radius+1];}
            }
            if(row-radius>=0)AddRow(row-radius,-1);
            if(row+radius+1<height)AddRow(row+radius+1,1);
        }
        return result;
    }
}
