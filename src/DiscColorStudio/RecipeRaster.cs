using System.Drawing.Imaging;

namespace DiscColorStudio;

// Source recipe for the DVD backend. Gray values stay separate from their tone profiles:
// the official DIS decision must be made at the actual logical payload-byte index.
public sealed record RecipePixels(byte[] Motifs, byte[] Gray, byte[] Owners);
public sealed class RecipeRaster
{
    private readonly Design design;
    private readonly int size;
    private readonly (Layer Layer,Rectangle Bounds,int Index)[] layers;
    private readonly Dictionary<int,(Rectangle Bounds,byte[] Alpha,byte[] Values)> legacy=[];
    public RecipeRaster(Design design,int size)
    {
        this.design=design;this.size=size;
        layers=design.Layers.Select((l,i)=>(l,LayerPreview.Bounds(l,size),i))
            .Where(p=>p.l.Visible && p.l.Opacity>0 && !p.Item2.IsEmpty).ToArray();
    }
    public RecipePixels Render(Renderer renderer,Rectangle core)
    {
        var motifs=new byte[core.Width*core.Height];Array.Fill(motifs,(byte)design.BackgroundId);
        var gray=new byte[motifs.Length];var owners=new byte[motifs.Length];
        foreach(var (layer,layerBounds,index) in layers)
        {
            var overlap=Rectangle.Intersect(core,layerBounds);if(overlap.Width<=0 || overlap.Height<=0)continue;
            int native=Math.Min(size,design.ImageRasterSize(layer)),factor=size/native;
            Rectangle Down(Rectangle r)=>Rectangle.FromLTRB(r.Left/factor,r.Top/factor,(r.Right+factor-1)/factor,(r.Bottom+factor-1)/factor);
            bool dis=design.ImageMode(layer)=="dis",diffusion=!RasterGrid.SupportsTiles(design.ImageMode(layer));
            Rectangle bounds=Down(diffusion?layerBounds:overlap);
            (Rectangle Bounds,byte[] Alpha,byte[] Values) patch;
            if(diffusion && legacy.TryGetValue(index,out var cached))patch=cached;
            else
            {
                using var coverage=renderer.Composite(design,new RasterRegion(native,bounds),[layer],transparent:true);
                byte[] alpha=Renderer.ReadAlpha(coverage);
                if(alpha.AsSpan().IndexOfAnyExcept((byte)0)<0)continue;
                int pad=RasterGrid.UsesAdaptiveMapping(design.ImageMode(layer))?RasterGrid.VariationRadius(native):0;
                var padded=Rectangle.Intersect(new Rectangle(0,0,native,native),Rectangle.Inflate(bounds,pad,pad));
                var search=new Rectangle(padded.X*factor,padded.Y*factor,padded.Width*factor,padded.Height*factor);
                var underlays=layers.Where(p=>p.Index<=index && p.Bounds.IntersectsWith(search)).Select(p=>p.Layer).ToArray();
                using var source=renderer.Composite(design,new RasterRegion(native,padded),underlays);
                byte[] values=dis?Gray(source):renderer.Quantize(design,source,new RasterRegion(native,padded),bounds,owner:layer,captureDither:false);
                patch=(bounds,alpha,values);if(diffusion)legacy[index]=patch;
            }
            for(int y=overlap.Top;y<overlap.Bottom;y++)for(int x=overlap.Left;x<overlap.Right;x++)
            {
                int p=(y/factor-patch.Bounds.Top)*patch.Bounds.Width+x/factor-patch.Bounds.Left;if(patch.Alpha[p]==0)continue;
                int dst=(y-core.Top)*core.Width+x-core.Left;owners[dst]=checked((byte)(index+1));
                if(dis)gray[dst]=patch.Values[p];else motifs[dst]=patch.Values[p];
            }
        }
        for(int y=0;y<core.Height;y++)for(int x=0;x<core.Width;x++)
        {
            double xx=(core.X+x+.5)/size*120-60,yy=(core.Y+y+.5)/size*120-60,r=xx*xx+yy*yy;int i=y*core.Width+x;
            if(r<design.Geometry.InnerRadiusMm*design.Geometry.InnerRadiusMm || r>design.Geometry.OuterRadiusMm*design.Geometry.OuterRadiusMm){owners[i]=0;motifs[i]=255;}
            else if(design.RegistrationMarks && Renderer.IsRegistration(xx,yy)){owners[i]=0;motifs[i]=13;}
        }
        return new RecipePixels(motifs,gray,owners);
    }
    private static unsafe byte[] Gray(Bitmap bitmap)
    {
        byte[] gray=new byte[bitmap.Width*bitmap.Height];var data=bitmap.LockBits(new Rectangle(0,0,bitmap.Width,bitmap.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try{for(int y=0;y<bitmap.Height;y++){byte* row=(byte*)data.Scan0+y*data.Stride;for(int x=0;x<bitmap.Width;x++)gray[y*bitmap.Width+x]=(byte)Math.Round(.2126*row[x*4+2]+.7152*row[x*4+1]+.0722*row[x*4]);}}
        finally{bitmap.UnlockBits(data);}return gray;
    }
}
