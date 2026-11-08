using System.Drawing.Imaging;
using System.Text.Json;
using DiscImageStudio.Core;
namespace DiscColorStudio;
internal static class LayerPreview
{
    internal static Rectangle Bounds(Layer l, int size)
    {
        double a = l.Rotation * Math.PI / 180, ex = (Math.Abs(Math.Cos(a))*l.Width+Math.Abs(Math.Sin(a))*l.Height)/2,
            ey = (Math.Abs(Math.Sin(a))*l.Width+Math.Abs(Math.Cos(a))*l.Height)/2, scale = size/120.0;
        return Rectangle.Intersect(new Rectangle(0,0,size,size), Rectangle.FromLTRB((int)Math.Floor((l.X-ex+60)*scale)-2,
            (int)Math.Floor((l.Y-ey+60)*scale)-2,(int)Math.Ceiling((l.X+ex+60)*scale)+2,(int)Math.Ceiling((l.Y+ey+60)*scale)+2));
    }
    internal static Bitmap Preview(Design d, Renderer renderer, int size, Rectangle region, int mode)
    {
        if (mode == 2) return renderer.Composite(d, new RasterRegion(size,region));
        var result = new Bitmap(region.Width,region.Height,PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(result);
            graphics.Clear(d.Palette.Single(s=>s.Id==d.BackgroundId).Color(mode==1?1:d.MatchGain));
            foreach(var layer in d.Layers.Where(l=>l.Visible && l.Opacity>0))
            {
                renderer.Cancellation.ThrowIfCancellationRequested();
                var bounds=Rectangle.Intersect(Bounds(layer,size),region); if(bounds.Width<=0 || bounds.Height<=0)continue;
                string key=JsonSerializer.Serialize(new{layer,size,bounds,mode,renderer.InteractivePreview,d.Palette,d.AllowedIds,d.MatchGain,d.Geometry,
                    d.QuantizationMode,d.PhotoGamma,d.PhotoChroma,d.DitherTolerance,d.FlatThreshold,d.ColorModelVersion,d.NeutralStrength,d.MixNeutralFlats},Design.Json);
                if(!renderer.TryLayerPreview(key,out var patch))
                {
                    patch=RenderLayer(d,layer,renderer,size,bounds,mode);
                    renderer.CacheLayerPreview(key,patch);
                }
                graphics.DrawImageUnscaled(patch,bounds.X-region.X,bounds.Y-region.Y);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    private static Bitmap RenderLayer(Design d,Layer layer,Renderer renderer,int size,Rectangle bounds,int mode)
    {
        var single=d.Clone();single.Format="disc-color-design-v1";single.Layers=[layer.Copy()];single.RegistrationMarks=false;
        var region=new RasterRegion(size,bounds);
        using var coverage=renderer.Composite(single,region,transparent:true);
        byte[] alpha=Renderer.ReadAlpha(coverage);
        Bitmap patch;
        if(d.ImageMode(layer)=="dis")
        {
            int native=Math.Min(size,d.ImageRasterSize(layer)),factor=size/native;
            var lower=Rectangle.FromLTRB(bounds.Left/factor,bounds.Top/factor,(bounds.Right+factor-1)/factor,(bounds.Bottom+factor-1)/factor);
            using var source=renderer.Composite(single,new RasterRegion(native,lower));
            using var density=Density(source,layer.DisTone!.Value,d,mode);
            patch=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format32bppArgb);
            using var painter=Graphics.FromImage(patch);painter.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            painter.PixelOffsetMode=System.Drawing.Drawing2D.PixelOffsetMode.Half;
            painter.DrawImage(density,new Rectangle(lower.X*factor-bounds.X,lower.Y*factor-bounds.Y,lower.Width*factor,lower.Height*factor));
        }
        else
        {
            byte[] map=new TiledExport(single,size).RenderRegion(renderer,bounds);
            patch=Renderer.Colorize(d,map,bounds.Width,bounds.Height,mode==1?1:d.MatchGain,
                mode==4 && layer.OutputCompensation is {} output ? output with{Strength=100}:null);
        }
        ApplyCoverage(patch,alpha,d,layer,size,bounds,mode);
        return patch;
    }
    internal static unsafe Bitmap Density(Bitmap source,ToneProfile tone,Design d,int mode)
    {
        var result=new Bitmap(source.Width,source.Height,PixelFormat.Format32bppArgb);
        var input=source.LockBits(new Rectangle(0,0,source.Width,source.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        var output=result.LockBits(new Rectangle(0,0,result.Width,result.Height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
        Color black=mode==1?d.Palette.Single(s=>s.Id==13).Color():Color.Black,white=mode==1?d.Palette.Single(s=>s.Id==14).Color():Color.White;
        try{for(int y=0;y<source.Height;y++){byte* src=(byte*)input.Scan0+y*input.Stride;int* dst=(int*)((byte*)output.Scan0+y*output.Stride);
            for(int x=0;x<source.Width;x++){byte gray=(byte)Math.Round(.2126*src[x*4+2]+.7152*src[x*4+1]+.0722*src[x*4]);
                int adjusted=tone.AdjustedGray(gray);double t=tone.DitherStrength==0?(adjusted>=128?1:0):Math.Clamp((adjusted-128)/(tone.DitherStrength*255)+.5,0,1);
                dst[x]=Color.FromArgb((int)Math.Round(black.R+(white.R-black.R)*t),(int)Math.Round(black.G+(white.G-black.G)*t),(int)Math.Round(black.B+(white.B-black.B)*t)).ToArgb();}}}
        finally{source.UnlockBits(input);result.UnlockBits(output);}return result;
    }
    private static unsafe void ApplyCoverage(Bitmap bitmap,byte[] alpha,Design d,Layer layer,int size,Rectangle bounds,int mode)
    {
        var data=bitmap.LockBits(new Rectangle(0,0,bitmap.Width,bitmap.Height),ImageLockMode.ReadWrite,PixelFormat.Format32bppArgb);
        double step=120.0/size,a=layer.Rotation*Math.PI/180,cos=Math.Cos(a),sin=Math.Sin(a);
        Color black=mode==1?d.Palette.Single(s=>s.Id==13).Color():Color.Black,white=mode==1?d.Palette.Single(s=>s.Id==14).Color():Color.White;
        try{for(int row=0;row<bitmap.Height;row++){int* scan=(int*)((byte*)data.Scan0+row*data.Stride);double y=(bounds.Y+row+.5)*step-60;
            for(int col=0;col<bitmap.Width;col++){double x=(bounds.X+col+.5)*step-60,r=x*x+y*y;
                if(r<d.Geometry.InnerRadiusMm*d.Geometry.InnerRadiusMm || r>d.Geometry.OuterRadiusMm*d.Geometry.OuterRadiusMm){scan[col]=0;continue;}
                double dx=x-layer.X,dy=y-layer.Y,u=dx*cos+dy*sin,v=-dx*sin+dy*cos;
                scan[col]=(scan[col]&0xffffff)|(alpha[row*bitmap.Width+col]<<24);
            }}}
        finally{bitmap.UnlockBits(data);}
    }
}
