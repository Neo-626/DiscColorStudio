using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Numerics;
using System.Text.Json;

namespace DiscColorStudio;

public sealed record AngleColorSettings
{
    public bool Enabled { get; set; }=true;
    public string ViewingMode { get; set; }="each-image-rotated-into-calibrated-window";
    public string ProfileSha256 { get; set; }="";
    public string LutSha256 { get; set; }="";
    public double WindowStartDeg { get; set; }=43;
    public double WindowEndDeg { get; set; }=163;
    public const string ProfileName="angle-color/continuous-color-map.json",LutName="angle-color/continuous-color-lut.bin";
    public void Validate(Dictionary<string,byte[]> assets)
    {
        if(ViewingMode!="each-image-rotated-into-calibrated-window"||!double.IsFinite(WindowStartDeg)||!double.IsFinite(WindowEndDeg)||WindowStartDeg<0||WindowEndDeg>360||WindowEndDeg<=WindowStartDeg
           ||!assets.TryGetValue(ProfileName,out var profile)||!assets.TryGetValue(LutName,out var lut)
           ||!Store.Hash(profile).Equals(ProfileSha256,StringComparison.OrdinalIgnoreCase)||!Store.Hash(lut).Equals(LutSha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(Ui.Text("连续颜色配置缺失或校验失败。"));
        using var data=JsonDocument.Parse(profile);
        if(data.RootElement.GetProperty("windowStartDeg").GetDouble()!=WindowStartDeg||data.RootElement.GetProperty("windowEndDeg").GetDouble()!=WindowEndDeg)
            throw new InvalidDataException(Ui.Text("工程观看范围与内嵌色表不一致。"));
    }
    public static bool IsCurrent(Design design)=>design.AngleColor?.ProfileSha256.Equals(Store.Hash(File.ReadAllBytes(ContinuousColorMap.DefaultPath)),StringComparison.OrdinalIgnoreCase)==true;
    public static void Attach(Design design,bool replace=false)
    {
        if(design.AngleColor is not null&&!replace)return;
        string path=ContinuousColorMap.DefaultPath;byte[] profile=File.ReadAllBytes(path),lut=ContinuousColorMap.ReadLut(path);
        using var meta=JsonDocument.Parse(profile);design.AngleColor=new AngleColorSettings{ProfileSha256=Store.Hash(profile),LutSha256=Store.Hash(lut),WindowStartDeg=meta.RootElement.GetProperty("windowStartDeg").GetDouble(),WindowEndDeg=meta.RootElement.GetProperty("windowEndDeg").GetDouble()};
        if(replace)foreach(var layer in design.Layers)if(layer.AngleCenterBeta is {} beta&&(beta<design.AngleColor.WindowStartDeg||beta>design.AngleColor.WindowEndDeg))layer.AngleCenterBeta=null;
        design.AngleProfileAssets=new(){[ProfileName]=profile,[LutName]=lut};
    }
}

public sealed record AngleLayerPlan(string LayerId,string Name,double CenterAlphaDeg,double CenterBetaDeg,double ViewingRotationDeg,
    double BetaMinDeg,double BetaMaxDeg,double SampleSupportedFraction,double MeanWeightedError,bool Automatic,bool FitsWindow,bool FullRectangleModeled,int Samples)
{
    public double Beta(double x,double y)=>CenterBetaDeg+AngleColorEngine.Signed(Math.Atan2(y,x)*180/Math.PI-CenterAlphaDeg);
}

public sealed class AngleColorEngine : IDisposable
{
    private Renderer? planner;
    private sealed record Resources(ContinuousColorMap Map,ContinuousColorMap.Atlas Atlas);
    private static readonly ConcurrentDictionary<string,Lazy<Resources>> resources=new();
    private readonly Dictionary<string,AngleLayerPlan> cache=[];
    private readonly Dictionary<string,Tile> tileCache=[];
    private long tileCacheBytes;
    public AngleLayerPlan[] LastPlans { get; private set; }=[];
    internal static double Signed(double angle)=>(angle+540)%360-180;
    private static Resources Get(Design d)
    {
        var config=d.AngleColor??throw new InvalidOperationException(Ui.Text("未启用连续颜色"));
        return resources.GetOrAdd(config.ProfileSha256+config.LutSha256,_=>new Lazy<Resources>(()=>
        {config.Validate(d.AngleProfileAssets);var map=new ContinuousColorMap(d.AngleProfileAssets[AngleColorSettings.ProfileName],d.AngleProfileAssets[AngleColorSettings.LutName]);return new Resources(map,map.CreateAtlas());})).Value;
    }
    public static ContinuousColorMap MapFor(Design d)=>Get(d).Map;
    internal static Rectangle Bounds(Layer l,int size)
    {
        double r=l.Rotation*Math.PI/180,c=Math.Abs(Math.Cos(r)),s=Math.Abs(Math.Sin(r)),hx=(l.Width*c+l.Height*s)/2,hy=(l.Width*s+l.Height*c)/2;
        return Rectangle.Intersect(new Rectangle(0,0,size,size),Rectangle.FromLTRB((int)Math.Floor((l.X-hx+60)*size/120)-2,(int)Math.Floor((l.Y-hy+60)*size/120)-2,(int)Math.Ceiling((l.X+hx+60)*size/120)+2,(int)Math.Ceiling((l.Y+hy+60)*size/120)+2));
    }
    internal static (double Min,double Max) AngularSpan(Layer l,double center)
    {
        double a=l.Rotation*Math.PI/180,c=Math.Cos(a),s=Math.Sin(a),u=-l.X*c-l.Y*s,v=l.X*s-l.Y*c;
        if(Math.Abs(u)<=l.Width/2&&Math.Abs(v)<=l.Height/2)return(-180,180);
        var angles=new List<double>();foreach(double x in new[]{-l.Width/2,l.Width/2})foreach(double y in new[]{-l.Height/2,l.Height/2})
            angles.Add(Signed(Math.Atan2(l.Y+x*s+y*c,l.X+x*c-y*s)*180/Math.PI-center));
        return(angles.Min(),angles.Max());
    }
    public static bool FitToWindow(Design d,Layer layer)
    {
        if(!d.UsesAngleColor(layer)||layer.X*layer.X+layer.Y*layer.Y<1)return false;
        double center=Math.Atan2(layer.Y,layer.X)*180/Math.PI,limit=d.AngleColor!.WindowEndDeg-d.AngleColor.WindowStartDeg-1;
        var span=AngularSpan(layer,center);if(span.Max-span.Min<=limit)return false;
        double w=layer.Width,h=layer.Height,lo=0,hi=1;var test=layer.Copy();
        for(int i=0;i<45;i++){double factor=(lo+hi)/2;test.Width=w*factor;test.Height=h*factor;span=AngularSpan(test,center);if(span.Max-span.Min<=limit)lo=factor;else hi=factor;}
        layer.Width=w*lo;layer.Height=h*lo;layer.AngleCenterBeta=null;return true;
    }
    public AngleLayerPlan[] Plans(Design d,Renderer renderer)
    {
        if(!d.HasAngleColor)return LastPlans=[];var res=Get(d);var plans=new List<AngleLayerPlan>();
        var planning=planner??=new Renderer();planning.Cancellation=renderer.Cancellation;
        foreach(var layer in d.Layers.Where(d.UsesAngleColor))
        {
            renderer.Cancellation.ThrowIfCancellationRequested();
            int index=d.Layers.IndexOf(layer);
            string key=d.AngleColor!.ProfileSha256+JsonSerializer.Serialize(d.Layers.Take(index+1),Design.Json)+JsonSerializer.Serialize(d.Geometry,Design.Json)+d.BackgroundId+d.MatchGain;
            if(cache.TryGetValue(key,out var plan)){plans.Add(plan);continue;}
            double center=Math.Atan2(layer.Y,layer.X)*180/Math.PI;var span=AngularSpan(layer,center);
            // Small deterministic target sample. Full-resolution source remains untouched.
            int grid=768;var bounds=Bounds(layer,grid);var samples=new List<(Color Rgb,double Delta,double Weight)>();
            if(bounds.Width>0&&bounds.Height>0)
            {
                using var alpha=planning.Composite(d,new RasterRegion(grid,bounds),[layer],true);
                using var rgb=planning.Composite(d,new RasterRegion(grid,bounds),d.Layers.Take(index+1).ToArray());
                int step=Math.Max(1,(int)Math.Ceiling(Math.Max(bounds.Width,bounds.Height)/20d));
                for(int y=step/2;y<bounds.Height;y+=step)for(int x=step/2;x<bounds.Width;x+=step)
                {
                    byte coverage=alpha.GetPixel(x,y).A;if(coverage<16)continue;
                    double px=(bounds.Left+x+.5)*120/grid-60,py=(bounds.Top+y+.5)*120/grid-60,r=px*px+py*py;
                    if(r<d.Geometry.InnerRadiusMm*d.Geometry.InnerRadiusMm||r>d.Geometry.OuterRadiusMm*d.Geometry.OuterRadiusMm)continue;
                    samples.Add((rgb.GetPixel(x,y),Signed(Math.Atan2(py,px)*180/Math.PI-center),coverage/255d));
                }
            }
            // Leave a small margin for native-raster antialiasing at rectangle edges.
            double ws=res.Map.Data.WindowStartDeg,we=res.Map.Data.WindowEndDeg,ms=res.Map.Data.ModelStartDeg,me=res.Map.Data.ModelEndDeg;
            double low=Math.Max(ws,ms+.35-span.Min),high=Math.Min(we,me-.35-span.Max);bool allFits=low<=high;
            if(!allFits){low=Math.Max(ws,ws-span.Min);high=Math.Min(we,we-span.Max);if(low>high){low=ws;high=we;}}
            double best=layer.AngleCenterBeta??Math.Clamp((ws+we)/2,low,high),bestScore=double.PositiveInfinity,bestFraction=0,bestError=0;
            void Try(double candidate)
            {
                double total=0,valid=0,error=0;foreach(var sample in samples)
                {double beta=candidate+sample.Delta;total+=sample.Weight;var predicted=res.Atlas.Map(sample.Rgb.R,sample.Rgb.G,sample.Rgb.B,beta,layer.BandingReduction,layer.AdaptiveMixing);if(predicted is {} v){valid+=sample.Weight;error+=res.Map.Error(res.Map.ApplyDisDark(v,sample.Rgb,beta,layer.PreferDisDark),res.Map.Target(sample.Rgb,beta))*sample.Weight;}}
                double fraction=total>0?valid/total:0,mean=valid>0?error/valid:1,score=(1-fraction)*100+mean;
                if(score<bestScore-1e-12){bestScore=score;best=candidate;bestFraction=fraction;bestError=mean;}
            }
            if(layer.AngleCenterBeta is {} manual)Try(manual);
            else
            {
                for(double beta=low;beta<=high;beta+=1)Try(beta);Try(high);double around=best;
                for(double beta=Math.Max(low,around-1);beta<=Math.Min(high,around+1);beta+=.1)Try(beta);
            }
            plan=new AngleLayerPlan(layer.Id,layer.Name,center,best,Signed(best-center),best+span.Min,best+span.Max,bestFraction,bestError,layer.AngleCenterBeta is null,
                best+span.Min>=ws-1e-7&&best+span.Max<=we+1e-7,best+span.Min>=ms-1e-7&&best+span.Max<=me+1e-7,samples.Count);
            if(cache.Count>80)cache.Clear();cache[key]=plan;plans.Add(plan);
        }
        return LastPlans=plans.ToArray();
    }
    public void Dispose(){planner?.Dispose();planner=null;cache.Clear();tileCache.Clear();tileCacheBytes=0;}
    internal sealed record Tile(int Size,Rectangle Bounds,int[] Rgb,short[] Owner,byte[] Legacy,AngleLayerPlan[] Plans);
    internal Tile BuildTile(Design d,Renderer renderer,int size,Rectangle region,AngleLayerPlan[]? plans=null)
    {
        plans??=Plans(d,renderer);var indices=plans.Select((p,i)=>(p.LayerId,i)).ToDictionary(p=>p.LayerId,p=>p.i);
        string cacheKey=JsonSerializer.Serialize(new{size,region,renderer.InteractivePreview,d.Geometry,d.BackgroundId,d.MatchGain,d.RegistrationMarks,d.RasterSize,d.Palette,
            layers=d.Layers.Select(l=>{var copy=l.Copy();copy.AngleCenterBeta=null;copy.BandingReduction=1;copy.AdaptiveMixing=1;copy.PreferDisDark=false;return copy;}),mappedIds=indices.Keys},Design.Json);
        if(tileCache.TryGetValue(cacheKey,out var hit))return hit with{Plans=plans};
        var legacyDesign=d.Clone();legacyDesign.AngleColor=null;legacyDesign.Layers=legacyDesign.Layers.Where(l=>!indices.ContainsKey(l.Id)&&d.ImageMode(l)!="dis").ToList();
        byte[] legacy=new TiledExport(legacyDesign,size).RenderRegion(renderer,region);int[] rgb=new int[legacy.Length];short[] owner=new short[legacy.Length];Array.Fill(owner,(short)-1);
        var sourceRasters=new Dictionary<int,(Rectangle Bounds,int[] Colors)>();
        for(int i=0;i<d.Layers.Count;i++)
        {
            var layer=d.Layers[i];if(!layer.Visible||layer.Opacity<=0)continue;
            var overlap=Rectangle.Intersect(region,Bounds(layer,size));if(overlap.Width<=0||overlap.Height<=0)continue;
            int native=Math.Min(size,d.ImageRasterSize(layer)),factor=Math.Max(1,size/native);
            var small=Rectangle.FromLTRB(overlap.Left/factor,overlap.Top/factor,(overlap.Right+factor-1)/factor,(overlap.Bottom+factor-1)/factor);
            using var alpha=renderer.Composite(d,new RasterRegion(native,small),[layer],true);var coverage=Renderer.ReadAlpha(alpha);
            bool mapped=indices.TryGetValue(layer.Id,out int which);(Rectangle Bounds,int[] Colors) colors=default;
            if(mapped&&!sourceRasters.TryGetValue(native,out colors))
            {
                var sourceRegion=Rectangle.FromLTRB(region.Left/factor,region.Top/factor,(region.Right+factor-1)/factor,(region.Bottom+factor-1)/factor);
                using var bitmap=renderer.Composite(d,new RasterRegion(native,sourceRegion),d.Layers.Where(l=>Bounds(l,native).IntersectsWith(sourceRegion)).ToArray());
                sourceRasters[native]=colors=(sourceRegion,Pixels(bitmap));
            }
            for(int y=overlap.Top;y<overlap.Bottom;y++)
            {
                renderer.Cancellation.ThrowIfCancellationRequested();for(int x=overlap.Left;x<overlap.Right;x++)
                {int src=(y/factor-small.Top)*small.Width+x/factor-small.Left;if(coverage[src]==0)continue;int dest=(y-region.Top)*region.Width+x-region.Left;owner[dest]=mapped?(short)which:(short)-1;if(mapped)rgb[dest]=colors.Colors![(y/factor-colors.Bounds.Top)*colors.Bounds.Width+x/factor-colors.Bounds.Left];}
            }
        }
        for(int y=0;y<region.Height;y++)for(int x=0;x<region.Width;x++)
        {
            int i=y*region.Width+x;double px=(region.Left+x+.5)*120/size-60,py=(region.Top+y+.5)*120/size-60;
            if(legacy[i]==255||(d.RegistrationMarks&&Renderer.IsRegistration(px,py)))owner[i]=-1;
        }
        var tile=new Tile(size,region,rgb,owner,legacy,plans);long bytes=(long)legacy.Length*7;
        while(tileCache.Count>0&&tileCacheBytes+bytes>64L*1024*1024){var old=tileCache.First();tileCacheBytes-=(long)old.Value.Legacy.Length*7;tileCache.Remove(old.Key);}
        if(bytes<=64L*1024*1024){tileCache[cacheKey]=tile;tileCacheBytes+=bytes;}return tile;
    }
    internal Bitmap Render(Design d,Renderer renderer,int size,Rectangle region,int mode)
    {
        var tile=BuildTile(d,renderer,size,region);var res=Get(d);var output=new int[tile.Owner.Length];
        var gray=d.HasGrayLayers?new RecipeRaster(d,size).Render(renderer,region):null;
        var settings=tile.Plans.Select(p=>d.Layers.Single(l=>l.Id==p.LayerId)).ToArray();
        var disDark=settings.Select(l=>l.PreferDisDark).ToArray();
        var legacyColors=d.Palette.ToDictionary(p=>(byte)p.Id,p=>p.Color(mode==1?1:d.MatchGain).ToArgb());
        double gain=mode==1?1:1/res.Map.Data.ReferenceLinearWhite;byte[] display=new byte[8193];for(int i=0;i<display.Length;i++)display[i]=(byte)Math.Clamp(Math.Round(255*ColorProfileData.Srgb(i/8192d)),0,255);
        byte C(float v)=>display[(int)Math.Clamp(v*gain*8192+.5,0,8192)];
        Parallel.For(0,region.Height,new ParallelOptions{CancellationToken=renderer.Cancellation},y=>
        {
            for(int x=0;x<region.Width;x++)
            {
                int i=y*region.Width+x;if(tile.Legacy[i]==255){output[i]=0;continue;}int id=tile.Owner[i];
                if(id<0)
                {
                    if(gray is not null&&gray.Owners[i]>0&&d.Layers[gray.Owners[i]-1] is {} layer&&d.ImageMode(layer)=="dis")
                    {
                        var tone=layer.DisTone!.Value;int adjusted=tone.AdjustedGray(gray.Gray[i]);double t=tone.DitherStrength==0?(adjusted>=128?1:0):Math.Clamp((adjusted-128)/(tone.DitherStrength*255)+.5,0,1);
                        int g=(int)Math.Round(t*255);output[i]=Color.FromArgb(g,g,g).ToArgb();
                    }
                    else output[i]=legacyColors[tile.Legacy[i]];continue;
                }
                Color rgb=Color.FromArgb(tile.Rgb[i]);double px=(region.Left+x+.5)*120/size-60,py=(region.Top+y+.5)*120/size-60,beta=tile.Plans[id].Beta(px,py);
                if(res.Map.HasJointMapping)beta=Math.Clamp(beta,res.Map.Data.ModelStartDeg,res.Map.Data.ModelEndDeg);
                var v=res.Atlas.Map(rgb.R,rgb.G,rgb.B,beta,settings[id].BandingReduction,settings[id].AdaptiveMixing);
                if(v is {} physical)v=res.Map.ApplyDisDark(physical,rgb,beta,disDark[id]);
                if(v is null){int gray=((region.Left+x+region.Top+y)/6%2==0)?140:195;output[i]=Color.FromArgb(gray,gray,gray).ToArgb();}
                else output[i]=Color.FromArgb(C(v.Value.X),C(v.Value.Y),C(v.Value.Z)).ToArgb();
            }
        });
        return BitmapFrom(output,region.Width,region.Height);
    }
    internal static int[] Pixels(Bitmap bitmap)
    {
        var values=new int[bitmap.Width*bitmap.Height];var data=bitmap.LockBits(new Rectangle(Point.Empty,bitmap.Size),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try{for(int y=0;y<bitmap.Height;y++)System.Runtime.InteropServices.Marshal.Copy(data.Scan0+y*data.Stride,values,y*bitmap.Width,bitmap.Width);}finally{bitmap.UnlockBits(data);}return values;
    }
    internal static Bitmap BitmapFrom(int[] values,int width,int height)
    {
        var image=new Bitmap(width,height,PixelFormat.Format32bppArgb);var data=image.LockBits(new Rectangle(0,0,width,height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
        try{for(int y=0;y<height;y++)System.Runtime.InteropServices.Marshal.Copy(values,y*width,data.Scan0+y*data.Stride,width);}finally{image.UnlockBits(data);}return image;
    }
}

