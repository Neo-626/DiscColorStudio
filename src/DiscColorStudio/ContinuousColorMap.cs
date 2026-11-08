using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace DiscColorStudio;

// Camera-relative, angle-dependent convex recipes for the bundled viewing conditions.
public sealed class ContinuousColorMap
{
    public sealed class Vertex { public int Texture { get; set; } public double[] Linear { get; set; } = []; }
    public sealed class Node
    {
        public double Angle { get; set; }
        public int CandidateCount { get; set; }
        public Vertex[] Vertices { get; set; } = [];
        public double[][] Equations { get; set; } = [];
        public double? GridReachableFraction { get; set; }
        public double ToneBlackY { get; set; }
        public double ToneWhiteY { get; set; }
        public double[]? GrayYRange { get; set; }
    }
    public sealed class Profile
    {
        public string Format { get; set; } = "";
        public string MappingMode { get; set; } = "";
        public string SourceSha256 { get; set; } = "";
        public string LutSha256 { get; set; } = "";
        public string LutFile { get; set; } = "";
        public int GridSize { get; set; }
        public double ReferenceLinearWhite { get; set; }
        public int RecipeComponents { get; set; }=4;
        public int RecipeBanks { get; set; }=1;
        public double SharedBlackY { get; set; }
        public double SharedWhiteY { get; set; }
        public double PurpleChromaBoost { get; set; }
        public double NeutralCoolShift { get; set; }
        public double MaxResponseGapDeg { get; set; }
        public uint DitherSeed { get; set; }
        public double WindowStartDeg { get; set; }
        public double WindowEndDeg { get; set; }
        public double ModelStartDeg { get; set; }
        public double ModelEndDeg { get; set; }
        public Node[] Nodes { get; set; } = [];
        public ColorProfileData.Texture[] Textures { get; set; } = [];
    }
    private readonly record struct Entry(ushort[] Indices,float[] Weights,int Offset)
    {
        public int Id(int i)=>Indices[Offset+i];
        public float Weight(int i)=>Weights[Offset+i];
    }
    public sealed record Recipe(double Angle, Color Source, ColorProfileData.Component[] Components, Vector3? Linear, Vector3 Target, double? Error);
    public sealed class Frame(ContinuousColorMap owner, double angle, Vector3[] values)
    {
        public double Angle { get; } = angle;
        public Vector3 Map(byte r, byte g, byte b)
        {
            Span<int> ix=stackalloc int[4];Span<float> w=stackalloc float[4];owner.Tetra(r,g,b,ix,w);
            return values[ix[0]]*w[0]+values[ix[1]]*w[1]+values[ix[2]]*w[2]+values[ix[3]]*w[3];
        }
    }
    public sealed class Atlas(ContinuousColorMap owner, Frame[][] frames)
    {
        public Vector3? Map(byte r,byte g,byte b,double beta,double bandingReduction=1,double adaptiveMixing=1)
        {
            if(!double.IsFinite(beta)||beta<owner.Data.ModelStartDeg||beta>owner.Data.ModelEndDeg)return null;
            double a=(beta-owner.Data.ModelStartDeg)*4;int lo=Math.Min((int)a,frames[0].Length-2);float f=(float)(a-lo);var result=Vector3.Zero;
            for(int bank=0;bank<frames.Length;bank++){float w=(float)owner.BankWeight(bank,bandingReduction,adaptiveMixing);if(w<=0)continue;result+=Vector3.Lerp(frames[bank][lo].Map(r,g,b),frames[bank][lo+1].Map(r,g,b),f)*w;}
            return result;
        }
    }
    public static void ValidateStrengths(double bandingReduction,double adaptiveMixing)
    {if(!double.IsFinite(bandingReduction)||bandingReduction is <0 or >1||!double.IsFinite(adaptiveMixing)||adaptiveMixing is <0 or >1)throw new ArgumentOutOfRangeException(nameof(adaptiveMixing));}
    private double BankWeight(int bank,double stability,double mixing)=>banks==1?1:((bank&1)==1?stability:1-stability)*((bank&2)==2?mixing:1-mixing);
    public Profile Data { get; }
    public bool HasAdaptiveMapping => Data.Format=="dvd-continuous-color-map-v5";
    public bool HasJointMapping => HasAdaptiveMapping||Data.Format=="dvd-continuous-color-map-v4";
    public bool HasTonalMapping => HasJointMapping||Data.Format=="dvd-continuous-color-map-v3";
    public string FilePath { get; }
    private readonly Entry[] entries;
    private readonly int size, count, components,banks;
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory,"data","continuous-color-map.json");
    public ContinuousColorMap(string path) : this(File.ReadAllBytes(path),ReadLut(path)) { FilePath=Path.GetFullPath(path); }
    internal static byte[] ReadLut(string path)
    {
        using var json=JsonDocument.Parse(File.ReadAllBytes(path));string file=json.RootElement.GetProperty("lutFile").GetString()!;
        if(Path.GetFileName(file)!=file)throw new InvalidDataException(Ui.Text("LUT路径无效"));
        string full=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,file);
        if(!file.EndsWith(".gz",StringComparison.OrdinalIgnoreCase))return File.ReadAllBytes(full);
        using var input=File.OpenRead(full);using var gz=new System.IO.Compression.GZipStream(input,System.IO.Compression.CompressionMode.Decompress);
        using var output=new MemoryStream();byte[] buffer=new byte[65536];int n;
        while((n=gz.Read(buffer))>0){if(output.Length+n>128_000_000)throw new InvalidDataException(Ui.Text("色表超过大小限制"));output.Write(buffer,0,n);}return output.ToArray();
    }
    public ContinuousColorMap(byte[] profile,byte[] lutBytes)
    {
        FilePath="";
        Data=JsonSerializer.Deserialize<Profile>(profile,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new InvalidDataException(Ui.Text("色表为空"));
        size=Data.GridSize;count=size*size*size;components=HasJointMapping?16:4;banks=HasAdaptiveMapping?4:1;
        if(Data.Format is not ("dvd-continuous-color-map-v1" or "dvd-continuous-color-map-v2" or "dvd-continuous-color-map-v3" or "dvd-continuous-color-map-v4" or "dvd-continuous-color-map-v5")||size!=17||Data.Nodes.Length<2||Data.Textures.Length!=(HasTonalMapping?1154:1152)
           ||!double.IsFinite(Data.WindowStartDeg)||!double.IsFinite(Data.WindowEndDeg)||!double.IsFinite(Data.ModelStartDeg)||!double.IsFinite(Data.ModelEndDeg)
           ||Data.WindowStartDeg<0||Data.WindowEndDeg>360||Data.ModelStartDeg<Data.WindowStartDeg||Data.ModelEndDeg>Data.WindowEndDeg||Data.ModelEndDeg<=Data.ModelStartDeg||Data.ModelEndDeg-Data.ModelStartDeg+1!=Data.Nodes.Length
           ||!double.IsFinite(Data.ReferenceLinearWhite)||Data.ReferenceLinearWhite<=0||Data.ReferenceLinearWhite>1
           ||Data.NeutralCoolShift!=(HasTonalMapping?0:.06)||Data.MaxResponseGapDeg!=7.5||Path.GetFileName(Data.LutFile)!=Data.LutFile)
            throw new InvalidDataException(Ui.Text("连续彩色映射格式或范围无效"));
        if(HasTonalMapping&&(Data.MappingMode!=(HasAdaptiveMapping?"adaptive-angular-banks-v1":HasJointMapping?"joint-angular-oklab-v1":"luminance-slice-hue-v1")||Data.Nodes.Any(n=>!double.IsFinite(n.ToneBlackY)||!double.IsFinite(n.ToneWhiteY)||n.ToneBlackY<0||n.ToneWhiteY<=n.ToneBlackY||n.ToneWhiteY>1)
            ||Data.Textures[1152].Motif!="A5"||Data.Textures[1153].Motif!="92"))throw new InvalidDataException(Ui.Text("明暗映射或DIS纹理无效"));
        if(HasJointMapping&&(Data.RecipeComponents!=16||!double.IsFinite(Data.SharedBlackY)||!double.IsFinite(Data.SharedWhiteY)||Data.SharedBlackY<=0||Data.SharedWhiteY<=Data.SharedBlackY||Data.SharedWhiteY>1||!double.IsFinite(Data.PurpleChromaBoost)||Data.PurpleChromaBoost<0||Data.PurpleChromaBoost>1))throw new InvalidDataException(Ui.Text("联合配方参数无效"));
        if(HasAdaptiveMapping&&Data.RecipeBanks!=4)throw new InvalidDataException(Ui.Text("配方组数量无效"));
        byte[] bytes=lutBytes;int header=HasAdaptiveMapping?28:HasJointMapping?24:20,stride=components*6;
        if(!Convert.ToHexString(SHA256.HashData(bytes)).Equals(Data.LutSha256,StringComparison.OrdinalIgnoreCase)
           ||bytes.Length!=header+stride*count*Data.Nodes.Length*banks||!bytes.AsSpan(0,8).SequenceEqual(HasAdaptiveMapping?"DCCLUT03"u8:HasJointMapping?"DCCLUT02"u8:"DCCLUT01"u8)
           ||BitConverter.ToUInt32(bytes,8)!=size||BitConverter.ToUInt32(bytes,12)!=Data.Nodes.Length||BitConverter.ToUInt32(bytes,16)!=count
           ||(HasJointMapping&&BitConverter.ToUInt32(bytes,20)!=components)||(HasAdaptiveMapping&&BitConverter.ToUInt32(bytes,24)!=banks))throw new InvalidDataException(Ui.Text("彩色查找表校验失败"));
        entries=new Entry[banks*count*Data.Nodes.Length];var ids=new ushort[entries.Length*components];var weights=new float[ids.Length];
        for(int e=0;e<entries.Length;e++)
        {
            int offset=e*components,at=header+e*stride;double sum=0;
            for(int k=0;k<components;k++)
            {
                ids[offset+k]=BitConverter.ToUInt16(bytes,at+k*2);float w=BitConverter.ToSingle(bytes,at+components*2+k*4);weights[offset+k]=w;
                if(!float.IsFinite(w)||w<0||(w>0&&ids[offset+k]>=Data.Textures.Length))throw new InvalidDataException(Ui.Text("纹理权重无效"));sum+=w;
            }
            if(Math.Abs(sum-1)>3e-6)throw new InvalidDataException(Ui.Text("纹理权重不守恒"));
            entries[e]=new Entry(ids,weights,offset);
        }
        for(int i=0;i<Data.Nodes.Length;i++)if(Data.Nodes[i].Angle!=Data.ModelStartDeg+i)throw new InvalidDataException(Ui.Text("角度节点无效"));
        for(int i=0;i<Data.Textures.Length;i++)
        {
            var t=Data.Textures[i];t.Bytes=Convert.FromHexString(t.Motif);
            if(t.Index!=i||t.Bytes.Length is <1 or >128)throw new InvalidDataException(Ui.Text("纹理无效"));
            for(int j=0;j<t.Samples.Length;j++)
            {var s=t.Samples[j];if(!double.IsFinite(s.Angle)||s.Linear.Length!=3||s.Linear.Any(x=>!double.IsFinite(x)||x<0||x>1)||(j>0&&s.Angle<=t.Samples[j-1].Angle))throw new InvalidDataException(Ui.Text("观测记录无效"));}
        }
    }
    private static float Linear(double s) => (float)(s<=.04045?s/12.92:Math.Pow((s+.055)/1.055,2.4));
    public Vector3 Target(Color color,double? beta=null)
    {
        var v=new Vector3(Linear(color.R/255d),Linear(color.G/255d),Linear(color.B/255d));float mx=Math.Max(v.X,Math.Max(v.Y,v.Z));
        if(HasJointMapping)
        {
            var q=Oklab(v);float black=(float)Math.Cbrt(Data.SharedBlackY),white=(float)Math.Cbrt(Data.SharedWhiteY);
            float hue=MathF.Atan2(q.Z,q.Y)*180/MathF.PI,chroma=MathF.Sqrt(q.Y*q.Y+q.Z*q.Z);
            static float Smooth(float x){x=Math.Clamp(x,0,1);return x*x*(3-2*x);}
            float boost=1+(float)Data.PurpleChromaBoost*Smooth((hue+90)/20)*Smooth((10-hue)/20)*Smooth(chroma/.025f);
            return FromOklab(new Vector3(black+(white-black)*q.X,white*q.Y*boost,white*q.Z*boost));
        }
        if(HasTonalMapping)
        {
            var (lo,f)=Bracket(beta??(Data.ModelStartDeg+Data.ModelEndDeg)/2);
            double black=Data.Nodes[lo].ToneBlackY*(1-f)+Data.Nodes[lo+1].ToneBlackY*f,white=Data.Nodes[lo].ToneWhiteY*(1-f)+Data.Nodes[lo+1].ToneWhiteY*f;
            float sourceY=Y(v);double y=Math.Pow(Math.Cbrt(black)+(Math.Cbrt(white)-Math.Cbrt(black))*Math.Cbrt(sourceY),3);
            return sourceY>0?v*(float)(y/sourceY):new Vector3((float)y);
        }
        if(mx==0)return v;float ch=(mx-Math.Min(v.X,Math.Min(v.Y,v.Z)))/mx;
        float shift=(float)(Math.Pow(Math.Max(0,1-ch/.2),2)*Data.NeutralCoolShift);
        var n=new Vector3(v.X*(1-shift),v.Y,v.Z*(1+shift));
        return n*(Y(v)/Y(n))*(float)Data.ReferenceLinearWhite;
    }
    public static Vector3 Oklab(Vector3 v)
    {
        float l=MathF.Cbrt(.4122214708f*v.X+.5363325363f*v.Y+.0514459929f*v.Z),m=MathF.Cbrt(.2119034982f*v.X+.6806995451f*v.Y+.1073969566f*v.Z),s=MathF.Cbrt(.0883024619f*v.X+.2817188376f*v.Y+.6299787005f*v.Z);
        return new(.2104542553f*l+.793617785f*m-.0040720468f*s,1.9779984951f*l-2.428592205f*m+.4505937099f*s,.0259040371f*l+.7827717662f*m-.808675766f*s);
    }
    private static Vector3 FromOklab(Vector3 q)
    {
        float l=q.X+.3963377774f*q.Y+.2158037573f*q.Z,m=q.X-.1055613458f*q.Y-.0638541728f*q.Z,s=q.X-.0894841775f*q.Y-1.291485548f*q.Z;l=l*l*l;m=m*m*m;s=s*s*s;
        return new(4.0767416621f*l-3.3077115913f*m+.2309699292f*s,-1.2684380046f*l+2.6097574011f*m-.3413193965f*s,-.0041960863f*l-.7034186147f*m+1.707614701f*s);
    }
    public static float Y(Vector3 v)=>Vector3.Dot(v,new Vector3(.2126f,.7152f,.0722f));
    public double Error(Vector3 actual,Vector3 target)
    {if(HasJointMapping){var q=Oklab(actual)-Oklab(target);return Math.Sqrt(q.X*q.X+3.2*q.Y*q.Y+3.2*q.Z*q.Z)/Math.Cbrt(Data.ReferenceLinearWhite);}var d=actual-target;return Math.Sqrt(.25*d.X*d.X+.5*d.Y*d.Y+.25*d.Z*d.Z)/Data.ReferenceLinearWhite;}
    public Color Display(Vector3 v,bool normalized=true)
    {
        double gain=normalized?1/Data.ReferenceLinearWhite:1;
        int C(float x)=>(int)Math.Clamp(Math.Round(255*ColorProfileData.Srgb(Math.Max(0,x)*gain)),0,255);
        return Color.FromArgb(C(v.X),C(v.Y),C(v.Z));
    }
    internal void Tetra(byte r,byte g,byte b,Span<int> ix,Span<float> w)
    {
        float x=r*(size-1)/255f,y=g*(size-1)/255f,z=b*(size-1)/255f;
        int a=Math.Min((int)x,size-2),c=Math.Min((int)y,size-2),d=Math.Min((int)z,size-2);
        float p=x-a,q=y-c,t=z-d;int s0=size*size,s1=size,s2=1;
        if(p<q){(p,q)=(q,p);(s0,s1)=(s1,s0);}if(q<t){(q,t)=(t,q);(s1,s2)=(s2,s1);}if(p<q){(p,q)=(q,p);(s0,s1)=(s1,s0);}
        ix[0]=a*size*size+c*size+d;ix[1]=ix[0]+s0;ix[2]=ix[1]+s1;ix[3]=ix[2]+s2;
        w[0]=1-p;w[1]=p-q;w[2]=q-t;w[3]=t;
    }
    public Vector3? ResponseAt(int texture,double angle)
    {
        var s=Data.Textures[texture].Samples;if(s.Length<2||angle<s[0].Angle||angle>s[^1].Angle)return null;
        int lo=0,hi=s.Length-1;while(hi-lo>1){int m=(lo+hi)/2;if(s[m].Angle<=angle)lo=m;else hi=m;}
        Vector3 V(int i)=>new((float)s[i].Linear[0],(float)s[i].Linear[1],(float)s[i].Linear[2]);
        if(Math.Abs(angle-s[lo].Angle)<1e-9)return V(lo);if(Math.Abs(angle-s[hi].Angle)<1e-9)return V(hi);
        double span=s[hi].Angle-s[lo].Angle;if(span>Data.MaxResponseGapDeg)return null;
        return Vector3.Lerp(V(lo),V(hi),(float)((angle-s[lo].Angle)/span));
    }
    private (int Lo,double Fraction) Bracket(double beta)
    {double x=Math.Clamp(beta,Data.ModelStartDeg,Data.ModelEndDeg)-Data.ModelStartDeg;int lo=Math.Min((int)x,Data.Nodes.Length-2);return(lo,x-lo);}
    public Recipe Resolve(Color source,double beta,bool preferDisDark=false,double bandingReduction=1,double adaptiveMixing=1)
    {
        if(!double.IsFinite(beta)||beta<Data.WindowStartDeg||beta>Data.WindowEndDeg)throw new ArgumentOutOfRangeException(nameof(beta));
        var (lo,f)=Bracket(beta);Span<int> ix=stackalloc int[4];Span<float> w=stackalloc float[4];Tetra(source.R,source.G,source.B,ix,w);
        var weights=new Dictionary<int,double>();
        ValidateStrengths(bandingReduction,adaptiveMixing);
        for(int bank=0;bank<banks;bank++)for(int a=0;a<2;a++)for(int p=0;p<4;p++)
        {
            double factor=BankWeight(bank,bandingReduction,adaptiveMixing)*(a==0?1-f:f)*w[p];if(factor<=0)continue;var e=entries[((bank*Data.Nodes.Length)+lo+a)*count+ix[p]];
            for(int j=0;j<components;j++)if(e.Weight(j)>0)weights[e.Id(j)]=weights.GetValueOrDefault(e.Id(j))+factor*e.Weight(j);
        }
        float dis=DisDarkWeight(source,preferDisDark);
        if(dis>0){foreach(int id in weights.Keys.ToArray())weights[id]*=1-dis;weights[1152]=weights.GetValueOrDefault(1152)+dis;}
        double sum=weights.Values.Sum();var cc=weights.OrderBy(p=>p.Key).Where(p=>p.Value>0).Select(p=>new ColorProfileData.Component(p.Key,p.Value/sum)).ToArray();
        Vector3? v=null;
        if(beta>=Data.ModelStartDeg&&beta<=Data.ModelEndDeg)
        {
            v=Vector3.Zero;foreach(var c in cc){var response=ResponseAt(c.Texture,beta);if(response is null)throw new InvalidDataException(Ui.Text("配方缺少当前角度的观测支撑"));v+=response.Value*(float)c.Weight;}
        }
        var target=Target(source,beta);return new Recipe(beta,source,cc,v,target,v is null?null:Error(v.Value,target));
    }
    // Optional low-chroma shadow substitution. Same weight and response in preview and raw encoding.
    public float DisDarkWeight(Color rgb,bool enabled)
    {
        if(!enabled||!HasTonalMapping)return 0;
        float max=Math.Max(rgb.R,Math.Max(rgb.G,rgb.B)),min=Math.Min(rgb.R,Math.Min(rgb.G,rgb.B));
        static float Smooth(float x){x=Math.Clamp(x,0,1);return x*x*(3-2*x);}
        return Smooth((97-max)/64)*(1-Smooth((max-min-24)/40));
    }
    public Vector3 ApplyDisDark(Vector3 value,Color source,double beta,bool enabled)
    {
        float w=DisDarkWeight(source,enabled);if(w==0)return value;
        var dark=ResponseAt(1152,beta)??throw new InvalidDataException(Ui.Text("DIS暗部缺少当前角度的测量"));return Vector3.Lerp(value,dark,w);
    }
    public Frame CreateFrame(double beta,double bandingReduction=1,double adaptiveMixing=1)
    {
        if(!double.IsFinite(beta)||beta<Data.ModelStartDeg||beta>Data.ModelEndDeg)throw new ArgumentOutOfRangeException(nameof(beta));
        ValidateStrengths(bandingReduction,adaptiveMixing);
        var (lo,f)=Bracket(beta);var responses=Data.Textures.Select(t=>ResponseAt(t.Index,beta)).ToArray();var result=new Vector3[count];
        for(int p=0;p<count;p++)
        {
            double total=0;for(int bank=0;bank<banks;bank++)for(int a=0;a<2;a++)
            {double factor=BankWeight(bank,bandingReduction,adaptiveMixing)*(a==0?1-f:f);if(factor<=0)continue;var e=entries[(bank*Data.Nodes.Length+lo+a)*count+p];for(int j=0;j<components;j++)
             {double w=e.Weight(j)*factor;if(w<=0)continue;var v=responses[e.Id(j)]??throw new InvalidDataException(Ui.Text("查找表含缺测纹理"));result[p]+=v*(float)w;total+=w;}}
            result[p]/=(float)total;
        }
        return new Frame(this,beta,result);
    }
    public Atlas CreateAtlas()
    {
        int n=(int)((Data.ModelEndDeg-Data.ModelStartDeg)*4)+1;var frames=Enumerable.Range(0,banks).Select(_=>new Frame[n]).ToArray();
        Parallel.For(0,banks*n,i=>{int bank=i/n,angle=i%n;frames[bank][angle]=CreateFrame(Data.ModelStartDeg+angle*.25,bank%2,bank/2);});
        return new Atlas(this,frames);
    }
    public int SelectTexture(Recipe recipe,ulong turn)
    {
        double u=((unchecked((uint)turn*40503u+Data.DitherSeed)&65535u)+.5)/65536;double sum=0;
        foreach(var c in recipe.Components){sum+=c.Weight;if(u<sum)return c.Texture;}return recipe.Components[^1].Texture;
    }
    public byte SampleByte(Color rgb,double beta,ulong spiralTurn,ulong globalLogicalPayloadByte,bool allowUnmeasured=false,bool preferDisDark=false,double bandingReduction=1,double adaptiveMixing=1)
    {
        var recipe=Resolve(rgb,beta,preferDisDark,bandingReduction,adaptiveMixing);if(recipe.Linear is null&&!allowUnmeasured)throw new InvalidOperationException(Ui.Text("该观察角度没有可用颜色测量"));
        byte[] motif=Data.Textures[SelectTexture(recipe,spiralTurn)].Bytes;return motif[globalLogicalPayloadByte%(ulong)motif.Length];
    }
}
