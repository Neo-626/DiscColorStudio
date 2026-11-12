using System.Text.Json;
using DvdImageSolver.Solver;
using DvdImageSolver.Encoding;
namespace DiscColorStudio;

internal sealed class LadderField
{
    internal required Layer Layer;internal required Rectangle Bounds;internal required int Grid;
    internal required LadderPose Pose;internal required LadderPixel[] Bands;internal required float[] U;
    internal required byte[] Alpha;
    internal double Azimuth;
    internal static LadderField Build(Design design,Layer layer,Renderer renderer,int grid,Rectangle? crop=null)
    {
        var bounds=Rectangle.Intersect(LayerPreview.Bounds(layer,grid),crop??new Rectangle(0,0,grid,grid));
        var pose=LadderPose.ForLayer(layer);int count=Math.Max(0,bounds.Width)*Math.Max(0,bounds.Height);
        var result=new LadderField{Azimuth=Math.Atan2(layer.Y,layer.X),Layer=layer,Bounds=bounds,Grid=grid,Pose=pose,Bands=new LadderPixel[count],U=new float[count],Alpha=new byte[count]};
        if(count==0)return result;
        var input=design.Clone();var background=input.Palette.Single(s=>s.Id==input.BackgroundId);background.CameraRgb=[0,0,0];
        using var coverage=renderer.Composite(input,new(grid,bounds),[layer],true);
        result.Alpha=Renderer.ReadAlpha(coverage);
        int index=design.Layers.FindIndex(l=>l.Id==layer.Id);
        using var source=renderer.Composite(input,new(grid,bounds),input.Layers.Take(index+1).ToArray());
        var rgb=AngleColorEngine.Pixels(source);var model=LadderMath.Shared.Value;double step=120d/grid;
        Parallel.For(0,bounds.Height,new ParallelOptions{CancellationToken=renderer.Cancellation,MaxDegreeOfParallelism=Math.Min(8,Environment.ProcessorCount)},row=>
        {
            Span<double> nearby=stackalloc double[6];double y=(bounds.Y+row+.5)*step-60;
            for(int col=0;col<bounds.Width;col++)
            {
                int k=row*bounds.Width+col;double x=(bounds.X+col+.5)*step-60,r2=x*x+y*y;
                if(result.Alpha[k]==0||r2<design.Geometry.InnerRadiusMm*design.Geometry.InnerRadiusMm||r2>design.Geometry.OuterRadiusMm*design.Geometry.OuterRadiusMm){result.Alpha[k]=0;continue;}
                double u=pose.At(x,y);pose.Nearby(x,y,nearby);result.U[k]=(float)u;
                result.Bands[k]=model.Fit(rgb[k],u,nearby,layer.BandingReduction);
            }
        });
        return result;
    }
    internal bool Index(double x,double y,out int k)
    {
        int ix=(int)Math.Floor((x+60)*Grid/120),iy=(int)Math.Floor((y+60)*Grid/120);
        if(!Bounds.Contains(ix,iy)){k=0;return false;}k=(iy-Bounds.Top)*Bounds.Width+ix-Bounds.Left;return Alpha[k]>0;
    }
    internal Bitmap Display(CancellationToken token,LadderView? view=null)
    {
        var output=new int[Bands.Length];var model=LadderMath.Shared.Value;var observer=(view??new LadderView()).Pose(Layer);double step=120d/Grid;
        Parallel.For(0,Bounds.Height,new ParallelOptions{CancellationToken=token},row=>
        {for(int col=0;col<Bounds.Width;col++){int k=row*Bounds.Width+col;if(Alpha[k]!=0)output[k]=(model.Display(model.Predict(Bands[k],U[k],observer.At((Bounds.X+col+.5)*step-60,(Bounds.Y+row+.5)*step-60)))&0xffffff)|(255<<24);}});
        return AngleColorEngine.BitmapFrom(output,Bounds.Width,Bounds.Height);
    }
}
public sealed record LadderView(bool Unified=false,double Rotation=0,double TiltX=0,double TiltY=0,LadderReference? Reference=null,double HeadShift=0)
{
    internal LadderPose Pose(Layer l)=>LadderPose.ForLayer(Unified?Reference?.Layer()??new Layer{X=1,Y=0}:l,Rotation,TiltX,TiltY,HeadShift);
}
internal static class LadderEngine
{
    internal static Bitmap Render(Design design,Renderer renderer,int size,Rectangle region,LadderView? view=null)
    {
        var result=new Bitmap(region.Width,region.Height);try { using var graphics=Graphics.FromImage(result);graphics.Clear(Color.FromArgb(35,32,40));
        foreach(var layer in design.Layers.Where(l=>l.Visible&&l.Opacity>0))
        {
            int grid=Math.Min(size,design.ImageRasterSize(layer));double scale=grid/(double)size;
            var crop=Rectangle.FromLTRB((int)Math.Floor(region.Left*scale),(int)Math.Floor(region.Top*scale),(int)Math.Ceiling(region.Right*scale),(int)Math.Ceiling(region.Bottom*scale));
            var bounds=Rectangle.Intersect(LayerPreview.Bounds(layer,grid),crop);if(bounds.IsEmpty||bounds.Width<=0||bounds.Height<=0)continue;
            int index=design.Layers.IndexOf(layer);
            string key="ladder2:"+JsonSerializer.Serialize(new{layers=design.Layers.Take(index+1),grid,bounds,design.Geometry,renderer.InteractivePreview},Design.Json);
            var field=renderer.LadderField(key,()=>LadderField.Build(design,layer,renderer,grid,bounds));
            using var patch=field.Display(renderer.Cancellation,view);
            graphics.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode=System.Drawing.Drawing2D.PixelOffsetMode.Half;
            graphics.DrawImage(patch,new RectangleF((float)(bounds.X/scale-region.X),(float)(bounds.Y/scale-region.Y),(float)(bounds.Width/scale),(float)(bounds.Height/scale)),new Rectangle(0,0,patch.Width,patch.Height),GraphicsUnit.Pixel);
        }
        return result; } catch { result.Dispose(); throw; }
    }
    internal static string Guidance(Design d,Layer? layer)
    {
        if(layer is null)return Ui.Text("选择图片查看观看位置与色域覆盖。");
        var pose=LadderPose.ForLayer(layer);double angle=layer.Rotation*Math.PI/180,c=Math.Cos(angle),s=Math.Sin(angle);int valid=0,good=0;double lo=2,hi=0;
        for(int j=0;j<19;j++)for(int i=0;i<25;i++)
        {double a=((i+.5)/25-.5)*layer.Width,b=((j+.5)/19-.5)*layer.Height,x=layer.X+c*a-s*b,y=layer.Y+s*a+c*b,r=Math.Sqrt(x*x+y*y);if(r<d.Geometry.InnerRadiusMm||r>d.Geometry.OuterRadiusMm)continue;
         double u=pose.At(x,y);lo=Math.Min(lo,u);hi=Math.Max(hi,u);valid++;if(u>=.24270773575746876&&u<=.4126031507876969)good++;}
        double turn=Math.Atan2(layer.Y,layer.X)*180/Math.PI;
        string message=layer.X*layer.X+layer.Y*layer.Y<1e-12?Ui.Text("图片跨盘心：使用 3 点钟方向作为观看参考。"):Ui.Format("观看：将图片中心转到 3 点钟方向（转盘 {0:+0.0;-0.0;0}°）。",-turn);
        if(layer.LadderObservation is {} observation)
            message=Ui.Format("箭头指向观看者：方位 {0:0.0}°，倾角 {1:0.0}°，距离 {2:0} mm。",observation.WorldAzimuth(layer),observation.ResolvedInclination(layer),observation.Distance);
        if(valid==0)return message+"\n"+Ui.Text("当前图片位于可记录环带之外。");
        return message+"\n"+Ui.Format("主要波段覆盖：{0:P0}；导出采样：{1:0.###} μm/格。",good/(double)valid,120000d/d.ImageRasterSize(layer))+(good<valid?"\n"+Ui.Text(layer.LadderObservation is null?"部分区域颜色较弱；缩小图片或沿半径展开可改善覆盖。":"部分区域波段覆盖较弱，可调整箭头、倾角或图片位置并检查模拟颜色。"):"");
    }
}
internal sealed class LadderEncoder:IDisposable
{
    private sealed record Source(Layer Layer,int Grid,Rectangle Bounds);
    private sealed class Worker(int count){internal LadderField?[] Last=new LadderField?[count];}
    private readonly Design design;
    private readonly Source[] sources;
    private readonly LadderMath model=LadderMath.Shared.Value;
    private readonly Renderer renderer=new();
    private readonly object cacheGate=new(),renderGate=new();
    private readonly Dictionary<(int Layer,int X,int Y),Lazy<LadderField>> tiles=[];
    private readonly Queue<(int Layer,int X,int Y)> order=[];
    private readonly ThreadLocal<Worker> workers;
    private const int TileSize=128;
    private const int CacheLimit=1536; // <= 408 MiB of completed field arrays, independent of export grid.
    internal LadderEncoder(Design document,IProgress<EncodingProgress>? progress,CancellationToken token)
    {
        design=document.Clone();renderer.Cancellation=token;
        sources=design.Layers.Where(l=>l.Visible&&l.Opacity>0).Select(l=>new Source(l,design.ImageRasterSize(l),LayerPreview.Bounds(l,design.ImageRasterSize(l)))).ToArray();
        workers=new(()=>new Worker(sources.Length),true);
        progress?.Report(new(Ui.Text("按每张图片的采样精度计算周期梯配方…"),.02));
    }
    // Diagnostic reference is intentionally separate from the bounded production cache.
    internal IReadOnlyList<LadderField> Fields=>sources.Select(s=>LadderField.Build(design,s.Layer,renderer,s.Grid)).ToArray();
    internal int[] Grids=>sources.Select(s=>s.Grid).ToArray();
    private LadderField Tile(int layer,int ix,int iy)
    {
        var key=(layer,ix/TileSize,iy/TileSize);Lazy<LadderField> lazy;
        lock(cacheGate)
        {
            if(!tiles.TryGetValue(key,out lazy!))
            {
                var src=sources[layer];var bounds=Rectangle.Intersect(src.Bounds,new Rectangle(key.Item2*TileSize,key.Item3*TileSize,TileSize,TileSize));
                lazy=new(()=>{lock(renderGate)return LadderField.Build(design,src.Layer,renderer,src.Grid,bounds);});tiles[key]=lazy;order.Enqueue(key);
                // Keep in-flight computations alive; callers retain their own tile references.
                int attempts=order.Count;
                while(tiles.Count>CacheLimit && attempts-->0)
                {var old=order.Dequeue();if(tiles.TryGetValue(old,out var v)&&v.IsValueCreated)tiles.Remove(old);else order.Enqueue(old);}
            }
        }
        return lazy.Value;
    }
    internal bool At(double x,double y,out LadderField? field,out int k)
    {
        var worker=workers.Value!;
        for(int n=sources.Length-1;n>=0;n--)
        {
            var src=sources[n];int ix=(int)Math.Floor((x+60)*src.Grid/120),iy=(int)Math.Floor((y+60)*src.Grid/120);
            if(!src.Bounds.Contains(ix,iy))continue;
            var f=worker.Last[n];if(f is null||!f.Bounds.Contains(ix,iy))worker.Last[n]=f=Tile(n,ix,iy);
            if(f.Index(x,y,out k)){field=f;return true;}
        }
        field=null;k=0;return false;
    }
    internal static byte Code(LadderMath model,LadderPixel bands,double u,double azimuth,double trackAngle)
    {
        long turn=(long)Math.Floor((trackAngle-azimuth)/(2*Math.PI)+.5);
        int slot=(int)((turn%model.Cycle.Length+model.Cycle.Length)%model.Cycle.Length),v=model.Cycle[slot];
        long cycle=(long)Math.Floor(turn/(double)model.Cycle.Length),rank=cycle*model.Data.Carriers[v].Count+model.Rank[slot];
        double level=model.Weights(v,u).Dot(bands.Vector)*model.Data.Carriers[v].Gain;
        double threshold=.5+v*.7548776662466927+rank*.5698402909980532;threshold-=Math.Floor(threshold);
        return level>threshold?(byte)v:(byte)0x66;
    }
    internal byte Sample(DvdCodewordSample point)
    {
        if(!At(point.Xmm,point.Ymm,out var f,out int k))return 0xA5;
        return Code(model,f!.Bands[k],f.U[k],f.Azimuth,point.TrackAngleRadians);
    }
    internal void FinishBlock(uint lba,byte[] payload)
    {
        Span<byte> code=stackalloc byte[2048];
        for(int sector=0;sector<16;sector++)
        {
            var span=payload.AsSpan(sector*2048,2048);span.CopyTo(code);
            ResolveSector(code,span);
        }
    }
    private static bool FrameStart(int p)=>((12+p)%172) is 0 or 91;
    internal void ResolveSector(ReadOnlySpan<byte> code,Span<byte> output)
    {
        int previousStart=0;byte previous=0xA5;
        for(int start=0;start<2048;)
        {
            byte c=code[start];int end=start+1;while(end<2048&&!FrameStart(end)&&code[end]==c)end++;
            bool str=c<30,prevFill=start>0&&previous==0xA5,fs=FrameStart(start),ss=start==0,touching=str||(start>0&&previous<30);
            int behind=FrameStart(previousStart)?0:Math.Clamp(2-(start-previousStart),0,2),need=fs?0:ss?2:prevFill?behind:touching?2:0;
            if(!(str||(c!=0xA5&&touching&&!prevFill&&!fs&&!ss)))need=0;
            if(c==0x66&&ss)need=2;
            if(c==0x66&&prevFill)need=behind;
            int length=end-start-need,trim=str&&length>0&&end<2048&&!FrameStart(end)?model.Data.Carriers[c].Trim[Math.Clamp(length,0,91)]:0;
            for(int i=start;i<end;i++)output[i]=str?(i-start>=need&&i-start<need+length-trim?model.Data.Carriers[c].Bytes[i-start-need]:(byte)0xA5):(i-start<need?(byte)0xA5:c);
            previousStart=start;previous=c;start=end;
        }
    }
    internal static void Write(Design design,Stream output,uint start,uint count,IProgress<EncodingProgress>? progress,CancellationToken token)
    {
        using var sampler=new LadderEncoder(design,progress,token);var g=design.Geometry;
        var mapping=new ImageMappingOptions(checked((uint)g.Sectors),g.InnerRadiusMm,g.OuterRadiusMm,133.3,0,true,128,1,PitchLinear:g.PitchLinear,PitchQuadratic:g.PitchQuadratic,PitchCubic:g.PitchCubic);
        var options=new MultiBlockSolveOptions(start,count,0x30000,1,4,1,64,.25,false,mapping,Algorithm:SolverAlgorithm.Dispersion,FastParallelism:Math.Clamp(Environment.ProcessorCount/2,1,8));
        long last=0;
        FastDispersionImageWriter.WriteScrambledCodewordPatternStream(sampler.Sample,output,options,p=>{long now=Environment.TickCount64;if(now-last<250&&p.CompletedBlocks!=p.TotalBlocks)return;last=now;progress?.Report(new(Ui.Format("生成 DVD 镜像 {0:P1}",p.Fraction),.2+.77*p.Fraction));},token,sampler.FinishBlock);
    }
    public void Dispose(){renderer.Dispose();workers.Dispose();tiles.Clear();}
}
