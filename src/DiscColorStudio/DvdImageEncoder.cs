using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DvdImageSolver.Solver;

namespace DiscColorStudio;
public sealed record EncodingProgress(string Message,double Fraction);
public sealed record EncodedImage(string Path,long Bytes,string Sha256);

public static class DvdImageEncoder
{
    private static readonly int[] Slots=BuildSlots();
    private static int[] BuildSlots()
    {
        var result=Enumerable.Repeat(-1,619008/16).ToArray();
        for(int p=0;p<32768;p++){int f=12+p%2048,r=p/2048*12+f/172,rec=(r+r/12)*182+f%172;result[rec/91*93+2+rec%91]=p;}return result;
    }
    internal static ulong Logical(ulong word)
    {int slot=Slots[word%(ulong)Slots.Length];if(slot<0)throw new InvalidDataException(Ui.Text("无效的 DVD 载荷位置。"));return word/(ulong)Slots.Length*32768+(uint)slot;}
    public static EncodedImage Generate(Design document,string path,IProgress<EncodingProgress>? progress=null,CancellationToken cancellation=default)
    {
        document.Validate();if(!document.HasDisCalibration)throw new InvalidOperationException(Ui.Text("请先导入 DIS 校准后的 DVD 绘图工程（.discproj）。"));
        if(!document.Layers.Any(l=>l.Visible&&l.Opacity>0))throw new InvalidOperationException(Ui.Text("请先添加图案。"));
        string full=Path.GetFullPath(path),temporary=full+".partial-"+Guid.NewGuid().ToString("N");
        if(File.Exists(full))throw new IOException(Ui.Text("目标镜像已存在，请选择新文件名。"));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        long bytes=checked(document.Geometry.Sectors*2048);
        var drive=new DriveInfo(Path.GetPathRoot(full)!);if(drive.IsReady&&drive.AvailableFreeSpace<bytes+128*1024*1024)throw new IOException(Ui.Text("镜像所在磁盘空间不足。"));
        try
        {
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.Read,1<<20))
            {Write(document,output,0,checked((uint)document.Geometry.Sectors),progress,cancellation);output.Flush(true);if(output.Length!=bytes)throw new InvalidDataException(Ui.Text("镜像长度不完整。"));}
            cancellation.ThrowIfCancellationRequested();progress?.Report(new(Ui.Text("检查镜像完整性…"),.98));
            using var input=File.OpenRead(temporary);string hash=Convert.ToHexString(SHA256.HashData(input));input.Dispose();
            cancellation.ThrowIfCancellationRequested();File.Move(temporary,full);return new(full,bytes,hash);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    internal static void Write(Design design,Stream output,uint start,uint count,IProgress<EncodingProgress>? progress,CancellationToken cancellation)
    {
        if(design.UsesLadder){LadderEncoder.Write(design,output,start,count,progress,cancellation);return;}
        using var sampler=new Sampler(design,cancellation);sampler.Prepare(progress,cancellation);
        var g=design.Geometry;
        var mapping=new ImageMappingOptions(checked((uint)g.Sectors),g.InnerRadiusMm,g.OuterRadiusMm,133.3,0,true,128,1,PitchLinear:g.PitchLinear,PitchQuadratic:g.PitchQuadratic,PitchCubic:g.PitchCubic);
        var options=new MultiBlockSolveOptions(start,count,0x30000,1,4,1,64,.25,false,mapping,Algorithm:SolverAlgorithm.Dispersion,FastParallelism:Math.Clamp(Environment.ProcessorCount/2,1,8));
        long last=0;
        FastDispersionImageWriter.WriteScrambledCodewordPatternStream(sampler.Sample,output,options,p=>
        {long now=Environment.TickCount64;if(now-last<250&&p.CompletedBlocks!=p.TotalBlocks)return;last=now;progress?.Report(new(Ui.Format("生成 DVD 镜像 {0:P1}",p.Fraction),.2+.77*p.Fraction));},cancellation);
    }
    private sealed record Pixels(Rectangle Bounds,int[] Rgb,short[] Owner,byte[] Legacy,RecipePixels? Gray);
    private sealed class Worker(FastRecipes[]? recipes)
    {public FastRecipes[]? Recipes=recipes;public (int,int) Key=(-1,-1);public Pixels? Tile;}
    private sealed class Sampler:IDisposable
    {
        private readonly Design design;private readonly int size;private readonly Renderer renderer=new();private readonly AngleColorEngine engine=new();
        private readonly AngleLayerPlan[] plans;private readonly ContinuousColorMap? map;private readonly Layer[] settings;
        private readonly Dictionary<int,byte[]> motifs;private readonly RecipeRaster? grayRaster;private readonly ThreadLocal<Worker> workers;
        private readonly ConcurrentDictionary<(int,int),Lazy<Pixels>> tiles=new();
        private readonly Queue<(int,int)> order=[];private readonly object gate=new(),renderGate=new();
        private readonly int cacheTiles;
        private Pixels? full;private const int TileSize=256;
        public Sampler(Design source,CancellationToken cancellation)
        {
            design=source.Clone();size=design.ExportRasterSize;renderer.Cancellation=cancellation;
            // A circle crosses at most 8*r/tile grid boundaries. Retain a full outer turn
            // plus the neighbouring tiles visited by the parallel encoder batches.
            cacheTiles=Math.Max(256,checked((int)Math.Ceiling(8*design.Geometry.OuterRadiusMm*size/120/TileSize))+128);
            plans=design.HasAngleColor?engine.Plans(design,renderer):[];map=design.HasAngleColor?AngleColorEngine.MapFor(design):null;
            settings=plans.Select(p=>design.Layers.Single(l=>l.Id==p.LayerId)).ToArray();motifs=design.Palette.ToDictionary(s=>s.Id,s=>Convert.FromHexString(s.Motif));
            grayRaster=design.HasGrayLayers?new RecipeRaster(design,size):null;
            FastRecipes.Entry[]? lut=map?.HasAdaptiveMapping==true?MemoryMarshal.Cast<byte,FastRecipes.Entry>(design.AngleProfileAssets[AngleColorSettings.LutName].AsSpan(28)).ToArray():null;
            workers=new(()=>new Worker(lut is null?null:settings.Select(l=>new FastRecipes(map!,lut,l.BandingReduction,l.AdaptiveMixing)).ToArray()),true);
        }
        private Pixels Build(Rectangle rect)
        {
            if(map is null)
            {
                var plain=design.Clone();plain.Layers=plain.Layers.Where(l=>design.ImageMode(l)!="dis").ToList();
                var legacy=new TiledExport(plain,size).RenderRegion(renderer,rect);
                var owner=new short[legacy.Length];Array.Fill(owner,(short)-1);
                return new(rect,new int[legacy.Length],owner,legacy,grayRaster?.Render(renderer,rect));
            }
            var tile=engine.BuildTile(design,renderer,size,rect,plans);
            return new(rect,tile.Rgb,tile.Owner,tile.Legacy,grayRaster?.Render(renderer,rect));
        }
        public void Prepare(IProgress<EncodingProgress>? progress,CancellationToken token)
        {
            progress?.Report(new(Ui.Text("计算每张图片的观看角度与纹理…"),0));
            if(size>8192){progress?.Report(new(Ui.Text("使用分块采样生成高精度镜像…"),.2));return;}
            int length=checked(size*size);full=new(new Rectangle(0,0,size,size),new int[length],new short[length],new byte[length],grayRaster is null?null:new RecipePixels(new byte[length],new byte[length],new byte[length]));
            for(int y=0;y<size;y+=TileSize)
            {
                token.ThrowIfCancellationRequested();
                for(int x=0;x<size;x+=TileSize)
                {
                    var p=Build(new(x,y,Math.Min(TileSize,size-x),Math.Min(TileSize,size-y)));
                    for(int row=0;row<p.Bounds.Height;row++)
                    {
                        int src=row*p.Bounds.Width,dst=(y+row)*size+x,n=p.Bounds.Width;
                        Array.Copy(p.Rgb,src,full.Rgb,dst,n);Array.Copy(p.Owner,src,full.Owner,dst,n);Array.Copy(p.Legacy,src,full.Legacy,dst,n);
                        if(p.Gray is {} g&&full.Gray is {} f){Array.Copy(g.Motifs,src,f.Motifs,dst,n);Array.Copy(g.Gray,src,f.Gray,dst,n);Array.Copy(g.Owners,src,f.Owners,dst,n);}
                    }
                }
                progress?.Report(new(Ui.Format("准备图像 {0}/{1}",Math.Min(y+TileSize,size),size),.2*Math.Min(y+TileSize,size)/size));
            }
        }
        private Pixels Tile(int x,int y,Worker worker)
        {
            if(full is not null)return full;var key=(x/TileSize,y/TileSize);if(worker.Key==key)return worker.Tile!;
            if(!tiles.TryGetValue(key,out var found))
            {
                lock(gate)
                {
                    if(!tiles.TryGetValue(key,out found))
                    {
                        while(tiles.Count>=cacheTiles)tiles.TryRemove(order.Dequeue(),out _);
                        var rect=new Rectangle(key.Item1*TileSize,key.Item2*TileSize,Math.Min(TileSize,size-key.Item1*TileSize),Math.Min(TileSize,size-key.Item2*TileSize));
                        // GDI renderers are shared; serialize new renders without blocking cache hits.
                        found=new Lazy<Pixels>(()=>{lock(renderGate)return Build(rect);});
                        tiles[key]=found;order.Enqueue(key);
                    }
                }
            }
            var pixels=found.Value;worker.Key=key;return worker.Tile=pixels;
        }
        public byte Sample(DvdCodewordSample sample)
        {
            int x=Math.Clamp((int)((sample.Xmm+60)*size/120),0,size-1),y=Math.Clamp((int)((sample.Ymm+60)*size/120),0,size-1);
            var worker=workers.Value!;var tile=Tile(x,y,worker);int i=(y-tile.Bounds.Top)*tile.Bounds.Width+x-tile.Bounds.Left,o=tile.Owner[i];ulong logical=Logical(sample.GlobalCodewordIndex);
            if(o<0)
            {
                if(tile.Gray is {} recipe&&recipe.Owners[i]>0)
                {var layer=design.Layers[recipe.Owners[i]-1];if(design.ImageMode(layer)=="dis")return layer.DisTone!.Value.UseWhite(recipe.Gray[i],checked((long)logical),128)?DvdGrayscalePalette.WhiteScrambledByte:DvdGrayscalePalette.BlackScrambledByte;}
                int id=tile.Legacy[i];if(id==255)id=design.BackgroundId;var motif=motifs[id];return motif[logical%(ulong)motif.Length];
            }
            double beta=Math.Clamp(plans[o].Beta(sample.Xmm,sample.Ymm),map!.Data.ModelStartDeg,map.Data.ModelEndDeg);ulong turn=checked((ulong)Math.Floor(sample.TrackAngleRadians/(2*Math.PI)));
            if(worker.Recipes is {} fast)return fast[o].Sample(tile.Rgb[i],beta,turn,logical,settings[o].PreferDisDark);
            return map.SampleByte(Color.FromArgb(tile.Rgb[i]),beta,turn,logical,preferDisDark:settings[o].PreferDisDark,bandingReduction:settings[o].BandingReduction,adaptiveMixing:settings[o].AdaptiveMixing);
        }
        public void Dispose(){workers.Dispose();renderer.Dispose();engine.Dispose();tiles.Clear();full=null;}
    }
}
