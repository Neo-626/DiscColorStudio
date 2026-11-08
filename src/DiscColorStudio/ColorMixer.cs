using System.Drawing.Imaging;
using System.Text.Json.Serialization;

namespace DiscColorStudio;

public sealed class ColorBand
{
    public double Hue { get; set; }
    public double Saturation { get; set; }
    public double Luminance { get; set; }
    public ColorBand Copy() => (ColorBand)MemberwiseClone();
}

public sealed class ColorMixer
{
    public ColorBand Master { get; set; } = new();
    public ColorBand[] Bands { get; set; } = Enumerable.Range(0,8).Select(_=>new ColorBand()).ToArray();
    [JsonIgnore] public bool IsIdentity => Master.Hue==0 && Master.Saturation==0 && Master.Luminance==0 && Bands.All(b=>b.Hue==0 && b.Saturation==0 && b.Luminance==0);
    public static string[] Names => [Ui.Text("红"), Ui.Text("橙"), Ui.Text("黄"), Ui.Text("绿"), Ui.Text("青"), Ui.Text("蓝"), Ui.Text("紫"), Ui.Text("洋红")];
    private static readonly double[] Centers = [0,30,60,120,180,240,270,315,360];
    public ColorMixer Copy() => new() { Master=Master.Copy(),Bands=Bands.Select(b=>b.Copy()).ToArray() };
    public void Validate()
    {
        if(Master is null || !double.IsFinite(Master.Hue) || !double.IsFinite(Master.Saturation) || !double.IsFinite(Master.Luminance)
            || Math.Abs(Master.Hue)>180 || Math.Abs(Master.Saturation)>100 || Math.Abs(Master.Luminance)>100)
            throw new InvalidDataException(Ui.Text("整体 HSL 参数无效。"));
        if(Bands is null || Bands.Length!=8 || Bands.Any(b=>b is null || !double.IsFinite(b.Hue)
            || !double.IsFinite(b.Saturation) || !double.IsFinite(b.Luminance)
            || Math.Abs(b.Hue)>60 || Math.Abs(b.Saturation)>100 || Math.Abs(b.Luminance)>100))
            throw new InvalidDataException(Ui.Text("分色 HSL 参数无效。"));
    }
    // A smooth, periodic partition by the ORIGINAL hue; edits never cascade between bands.
    public Color Apply(Color color)
    {
        if(IsIdentity) return color;
        return ApplyCore(color);
    }
    private Color ApplyCore(Color color)
    {
        double r=color.R/255.0,g=color.G/255.0,b=color.B/255.0;
        double max=Math.Max(r,Math.Max(g,b)),min=Math.Min(r,Math.Min(g,b)),delta=max-min,l=(max+min)*.5;
        double h=0,s=0;
        if(delta>=1e-10)
        {
            s=delta/(1-Math.Abs(2*l-1));
            h=(max==r ? (g-b)/delta : max==g ? (b-r)/delta+2 : (r-g)/delta+4)*60;
            h=(h+360)%360;
            int i=0;while(i<7 && h>=Centers[i+1])i++;
            double t=(h-Centers[i])/(Centers[i+1]-Centers[i]);t=t*t*(3-2*t);
            var a=Bands[i];var z=Bands[(i+1)%8];
            double shift=a.Hue*(1-t)+z.Hue*t, sat=(a.Saturation*(1-t)+z.Saturation*t)/100,
                lum=(a.Luminance*(1-t)+z.Luminance*t)/100;
            // Fade band luminance near the neutral axis to keep gray pixels stable.
            double protection=Math.Clamp(s/.15,0,1);
            protection=protection*protection*(3-2*protection);
            h=(h+shift+360)%360;
            s=Math.Clamp(sat<0?s*(1+sat):s+(1-s)*sat,0,1);
            lum*=protection;l=Math.Clamp(lum<0?l*(1+lum):l+(1-l)*lum,0,1);
        }
        else if(Master.Luminance==0)return color;
        // Master adjustments follow the original-hue bands; one final RGB conversion.
        if(Master.Hue!=0)h=(h+Master.Hue+360)%360;
        s=Math.Clamp(s*(1+Master.Saturation/100),0,1);
        double masterLight=Master.Luminance/100;
        l=Math.Clamp(masterLight<0?l*(1+masterLight):l+(1-l)*masterLight,0,1);
        double c=(1-Math.Abs(2*l-1))*s,x=c*(1-Math.Abs(h/60%2-1)),m=l-c/2;
        (r,g,b)=h switch {<60=>(c,x,0d),<120=>(x,c,0d),<180=>(0d,c,x),<240=>(0d,x,c),<300=>(x,0d,c),_=>(c,0d,x)};
        static int Byte(double v)=>(int)Math.Clamp(Math.Round(v*255),0,255);
        return Color.FromArgb(color.A,Byte(r+m),Byte(g+m),Byte(b+m));
    }
    public Bitmap Render(Bitmap original) => Render(original,default);
    internal unsafe Bitmap Render(Bitmap original, CancellationToken cancellation)
    {
        var result=original.Clone(new Rectangle(0,0,original.Width,original.Height),PixelFormat.Format32bppArgb);
        if(IsIdentity)return result;
        var bits=result.LockBits(new Rectangle(0,0,result.Width,result.Height),ImageLockMode.ReadWrite,PixelFormat.Format32bppArgb);
        // Exact 24-bit keys, direct-mapped cache. No 5-bit quantization in source editing.
        var keys=new int[65536];Array.Fill(keys,-1);var values=new int[65536];
        try { for(int y=0;y<result.Height;y++) {cancellation.ThrowIfCancellationRequested();int* row=(int*)((byte*)bits.Scan0+y*bits.Stride);
            for(int x=0;x<result.Width;x++) {int rgb=row[x]&0xffffff,k=(int)((unchecked((uint)rgb*2654435761u))>>16);
                if(keys[k]!=rgb){keys[k]=rgb;values[k]=ApplyCore(Color.FromArgb(unchecked((int)0xff000000)|rgb)).ToArgb()&0xffffff;}
                row[x]=(row[x]&unchecked((int)0xff000000))|values[k];}}
        } catch {result.UnlockBits(bits);result.Dispose();throw;} result.UnlockBits(bits);return result;
    }
}

internal readonly record struct OpticalLab(double L,double A,double B)
{
    internal static double Linear(double v) => v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);
    private static readonly double[] ByteLinear=Enumerable.Range(0,256).Select(i=>Linear(i/255.0)).ToArray();
    internal static OpticalLab From(Color c)=>FromLinear(ByteLinear[c.R],ByteLinear[c.G],ByteLinear[c.B]);
    internal static OpticalLab FromLinear(double r,double g,double b)
    {
        double l=Math.Cbrt(.4122214708*r+.5363325363*g+.0514459929*b),
            m=Math.Cbrt(.2119034982*r+.6806995451*g+.1073969566*b),s=Math.Cbrt(.0883024619*r+.2817188376*g+.6299787005*b);
        return new(.2104542553*l+.7936177850*m-.0040720468*s,
            1.9779984951*l-2.4285922050*m+.4505937099*s,.0259040371*l+.7827717662*m-.8086757660*s);
    }
    internal double Chroma=>Math.Sqrt(A*A+B*B);
}
