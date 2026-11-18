using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Buffers.Binary;
using System.Text.Json;
namespace DiscColorStudio;

// Immutable pixels are shared; every concurrent GDI call owns a separate Bitmap
// handle. The backing bitmap stays locked until all of its views are released.
internal sealed class ExportImagePool(Design design,LadderWorkQueue work):IDisposable
{
    private readonly object gate=new();
    private readonly Dictionary<string,Entry> entries=[];
    private readonly long colorLimit=Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes/8,128L<<20,1L<<30);
    private long colorBytes,stamp;
    private int colorRenders;
    internal int ColorRenders=>Volatile.Read(ref colorRenders);
    private sealed class Entry(bool original)
    {internal readonly bool Original=original;internal Shared? Pixels;internal int Complete,Users;internal long Stamp;}
    private sealed class Shared:IDisposable
    {
        private readonly Bitmap owner;
        private readonly BitmapData bits;
        internal int Width {get;}internal int Height {get;}
        internal long Bytes=>(long)Width*Height*4;
        internal bool Opaque {get;}
        internal unsafe Shared(Bitmap bitmap)
        {
            owner=bitmap;
            try{Width=owner.Width;Height=owner.Height;bits=owner.LockBits(new(0,0,Width,Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);}
            catch{owner.Dispose();throw;}
            bool opaque=true;
            for(int y=0;y<Height&&opaque;y++)
            {byte* row=(byte*)bits.Scan0+y*bits.Stride;for(int x=0;x<Width;x++)if(row[x*4+3]!=255){opaque=false;break;}}
            Opaque=opaque;
        }
        internal Bitmap View()=>new(Width,Height,bits.Stride,PixelFormat.Format32bppArgb,bits.Scan0);
        public void Dispose(){try{owner.UnlockBits(bits);}finally{owner.Dispose();}}
    }
    internal sealed class Lease:IDisposable
    {
        private Action? release;
        internal Bitmap Image {get;}
        internal bool Opaque {get;}
        internal bool ToneApplied;
        internal Lease(Bitmap image,bool opaque,Action release){Image=image;Opaque=opaque;this.release=release;}
        public void Dispose(){var done=Interlocked.Exchange(ref release,null);if(done is null)return;try{Image.Dispose();}finally{done();}}
    }
    private Lease Get(string key,bool original,Func<Bitmap> build)
    {
        while(true)
        {
            work.Check();Entry entry;bool create=false;
            lock(gate)
            {
                if(!entries.TryGetValue(key,out entry!)){entries[key]=entry=new(original);create=true;}
                else if(Volatile.Read(ref entry.Complete)!=0){work.Check();entry.Users++;entry.Stamp=++stamp;return View(entry);}
            }
            if(!create){work.Until(()=>Volatile.Read(ref entry.Complete)!=0);continue;}
            Shared? owned=null;
            try
            {
                owned=new(build());work.Check();
                lock(gate)
                {
                    entry.Pixels=owned;owned=null;entry.Users++;entry.Stamp=++stamp;
                    if(!original)colorBytes+=entry.Pixels.Bytes;
                    Volatile.Write(ref entry.Complete,1);Trim();
                }
                work.Wake();return View(entry);
            }
            catch(Exception ex){owned?.Dispose();work.Fail(ex);Volatile.Write(ref entry.Complete,1);work.Wake();throw;}
        }
    }
    private Lease View(Entry entry)
    {
        try{return new(entry.Pixels!.View(),entry.Pixels.Opaque,()=>Release(entry));}
        catch{Release(entry);throw;}
    }
    private void Release(Entry entry){lock(gate){entry.Users--;Trim();}}
    private void Trim()
    {
        while(colorBytes>colorLimit)
        {
            var candidates=entries.Where(p=>!p.Value.Original&&p.Value.Users==0&&p.Value.Pixels is not null&&Volatile.Read(ref p.Value.Complete)!=0);
            if(!candidates.Any())break;
            var old=candidates.MinBy(p=>p.Value.Stamp);entries.Remove(old.Key);colorBytes-=old.Value.Pixels!.Bytes;old.Value.Pixels.Dispose();
        }
    }
    private Lease Raw(string asset)=>Get("raw:"+asset,true,()=>
    {using var bytes=new MemoryStream(design.Assets[asset]);using var image=Image.FromStream(bytes);return new Bitmap(image);});
    internal bool IsOpaque(string asset){using var image=Raw(asset);return image.Opaque;}
    internal Lease Acquire(Layer layer,bool coverageOnly)
    {
        string asset=layer.AssetId!;var raw=Raw(asset);
        if(coverageOnly)return raw;
        var mixer=layer.BypassColorMixer?null:layer.ColorMixer;bool edit=mixer is not null&&!mixer.IsIdentity;
        bool tone=raw.Opaque&&layer.Opacity==1;
        float contrast=(float)(layer.Contrast*layer.InputExposure),offset=(1-(float)layer.Contrast)*.5f+(float)layer.Brightness/255f;
        if(!edit&&(!tone||(contrast==1&&offset==0))){raw.ToneApplied=tone;return raw;}
        raw.Dispose();
        string key="color:"+asset+":"+(edit?JsonSerializer.Serialize(mixer,Design.Json):"")+":"+(tone?$"{contrast:R}:{offset:R}":"unbaked");
        var result=Get(key,false,()=>
        {
            using var input=Raw(asset);Bitmap? adjusted=null;
            try
            {
                var image=edit?(adjusted=mixer!.Render(input.Image,work.Token,1,work.ForColors)):input.Image;
                Bitmap output;
                if(tone)output=Tone(image,contrast,offset);
                else{output=adjusted!;adjusted=null;}
                Interlocked.Increment(ref colorRenders);return output;
            }
            finally{adjusted?.Dispose();}
        });
        result.ToneApplied=tone;return result;
    }
    private static Bitmap Tone(Bitmap input,float contrast,float offset)
    {
        var output=new Bitmap(input.Width,input.Height,PixelFormat.Format32bppArgb);
        try
        {
            using var g=Graphics.FromImage(output);g.CompositingMode=CompositingMode.SourceCopy;
            g.InterpolationMode=InterpolationMode.NearestNeighbor;g.PixelOffsetMode=PixelOffsetMode.Half;
            using var attributes=new ImageAttributes();attributes.SetColorMatrix(new ColorMatrix(new float[][]{
                [contrast,0,0,0,0],[0,contrast,0,0,0],[0,0,contrast,0,0],[0,0,0,1,0],[offset,offset,offset,0,1]}));
            g.DrawImage(input,new Rectangle(0,0,input.Width,input.Height),0,0,input.Width,input.Height,GraphicsUnit.Pixel,attributes);
            return output;
        }
        catch{output.Dispose();throw;}
    }
    internal static int RenderingWorkers(Design design,int workers)
    {
        long scratch=32L<<20;
        foreach(var layer in design.Layers.Where(l=>l.Visible&&l.Kind=="image"&&l.AssetId is not null))
        {
            byte[] data=design.Assets[layer.AssetId!];long pixels=0;
            if(data.Length>=24&&data[0]==137&&data[1]==80) pixels=(long)BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16,4))*BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20,4));
            int grid=design.ImageRasterSize(layer);
            if(grid<65536)pixels=Math.Max(pixels,(long)Math.Ceiling(layer.Width*grid/120)* (long)Math.Ceiling(layer.Height*grid/120));
            scratch=Math.Max(scratch,checked(pixels*8+(32L<<20)));
        }
        var memory=GC.GetGCMemoryInfo();long available=Math.Max(64L<<20,memory.TotalAvailableMemoryBytes-memory.MemoryLoadBytes);
        return (int)Math.Clamp(Math.Min(workers,available/2/scratch),1,8);
    }
    public void Dispose()
    {lock(gate){foreach(var entry in entries.Values)entry.Pixels?.Dispose();entries.Clear();colorBytes=0;}}
}
