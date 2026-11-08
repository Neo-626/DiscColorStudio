using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using DiscColorStudio;

// Exact DCCLUT03 tetrahedral RGB interpolation and continuous observation angle.
// Per-layer strengths are fixed for this immutable job. No spatial/angle quantization.
internal sealed class FastRecipes
{
    [InlineArray(16)] internal struct Ids16 { private ushort value; }
    [InlineArray(16)] internal struct Weights16 { private float value; }
    [StructLayout(LayoutKind.Sequential,Pack=1)] internal struct Entry { public Ids16 Ids; public Weights16 Weights; }
    private readonly ContinuousColorMap map; private readonly Entry[] lut; private readonly byte[][] motifs;
    private readonly double[] bankWeights;
    private int lastRgb=int.MinValue,lastLo=-1,n; private bool lastDis;
    private readonly int[] ids=new int[1154];
    private readonly double[] dense0=new double[1154],dense1=new double[1154],cdf0=new double[1154],cdf1=new double[1154];
    private readonly bool[] used=new bool[1154];
    private double sum0,sum1;
    public FastRecipes(ContinuousColorMap model,Entry[] entries,double stable,double adaptive)
    {
        ContinuousColorMap.ValidateStrengths(stable,adaptive);map=model;lut=entries;
        if(!model.HasAdaptiveMapping||Marshal.SizeOf<Entry>()!=96)throw new InvalidDataException("Expected DCCLUT03 and 96-byte entries");
        motifs=model.Data.Textures.Select(t=>Convert.FromHexString(t.Motif)).ToArray();
        bankWeights=[(1-stable)*(1-adaptive),stable*(1-adaptive),(1-stable)*adaptive,stable*adaptive];
    }
    private void Add(int id,double a,int node)
    {
        if(!used[id]){used[id]=true;ids[n++]=id;}
        if(node==0)dense0[id]+=a;else dense1[id]+=a;
    }
    private void Prepare(int argb,int lo,bool dis)
    {
        for(int j=0;j<n;j++){int id=ids[j];dense0[id]=dense1[id]=0;used[id]=false;}n=0;
        var c=Color.FromArgb(argb);const int size=17,count=4913;
        float x=c.R*16/255f,y=c.G*16/255f,z=c.B*16/255f;
        int a=Math.Min((int)x,15),b=Math.Min((int)y,15),d=Math.Min((int)z,15);float p=x-a,q=y-b,t=z-d;int s0=289,s1=17,s2=1;
        if(p<q){(p,q)=(q,p);(s0,s1)=(s1,s0);}if(q<t){(q,t)=(t,q);(s1,s2)=(s2,s1);}if(p<q){(p,q)=(q,p);(s0,s1)=(s1,s0);}
        Span<int> ix=stackalloc int[4];Span<float> w=stackalloc float[4];ix[0]=a*size*size+b*size+d;ix[1]=ix[0]+s0;ix[2]=ix[1]+s1;ix[3]=ix[2]+s2;w[0]=1-p;w[1]=p-q;w[2]=q-t;w[3]=t;
        for(int bank=0;bank<4;bank++)
        {
            double bw=bankWeights[bank];if(bw<=0)continue;
            for(int node=0;node<2;node++)for(int v=0;v<4;v++)
            {
                double weight=bw*w[v];if(weight<=0)continue;
                ref readonly Entry e=ref lut[((bank*map.Data.Nodes.Length)+lo+node)*count+ix[v]];
                for(int k=0;k<16;k++)if(e.Weights[k]>0)Add(e.Ids[k],weight*e.Weights[k],node);
            }
        }
        float mask=map.DisDarkWeight(c,dis);
        if(mask>0){for(int k=0;k<n;k++){dense0[ids[k]]*=1-mask;dense1[ids[k]]*=1-mask;}Add(1152,mask,0);Add(1152,mask,1);}
        Array.Sort(ids,0,n);sum0=sum1=0;
        for(int k=0;k<n;k++){sum0+=dense0[ids[k]];sum1+=dense1[ids[k]];cdf0[k]=sum0;cdf1[k]=sum1;}
        lastRgb=argb;lastLo=lo;lastDis=dis;
    }
    public byte Sample(int argb,double beta,ulong turn,ulong logical,bool dis=false)
    {
        if(!double.IsFinite(beta))throw new InvalidDataException("Nonfinite angle");
        beta=Math.Clamp(beta,map.Data.ModelStartDeg,map.Data.ModelEndDeg);
        double x=beta-map.Data.ModelStartDeg;int lo=Math.Min((int)x,map.Data.Nodes.Length-2);double f=x-lo;
        if(argb!=lastRgb||lo!=lastLo||dis!=lastDis)Prepare(argb,lo,dis);
        double u=((unchecked((uint)turn*40503u+map.Data.DitherSeed)&65535u)+.5)/65536*(sum0*(1-f)+sum1*f);
        int left=0,right=n-1;while(left<right){int mid=(left+right)/2;if(u<cdf0[mid]*(1-f)+cdf1[mid]*f)right=mid;else left=mid+1;}
        byte[] motif=motifs[ids[left]];return motif[logical%(ulong)motif.Length];
    }
}
