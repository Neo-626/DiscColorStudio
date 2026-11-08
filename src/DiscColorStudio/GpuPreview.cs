using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DiscColorStudio;

// Preview-only CUDA backend. Export keeps the reference CPU pipeline.
// Default math retains double precision and disables fast/approximate math.
public static class GpuPreview
{
    public struct Node
    {
        public double P0,P1,P2,P3,L,A,B,Weight;
        public double Min0,Min1,Min2,Min3,Max0,Max1,Max2,Max3,MinL,MaxL;
        public int First,Second,Axis,Left,Right,Parent;
    }
    public struct Query
    {
        public double L,A,B,Q0,Q1,Q2,Light,Chroma;
        public int Root;
    }
    public struct Choice { public int Solid,Best; }
    internal sealed record Tree(Node[] Nodes,int[] Roots,int SolidCount);
    private static readonly object Gate=new();
    private static Context? context;
    private static Accelerator? accelerator;
    private static Action<Index1D,ArrayView<int>,ArrayView<double>,ArrayView<byte>>? colorKernel;
    private static Action<Index1D,ArrayView<Query>,ArrayView<Node>,ArrayView<Choice>,int>? searchKernel;
    private static bool attempted;
    private static readonly Dictionary<Tree,MemoryBuffer1D<Node,Stride1D.Dense>> treeBuffers=[];
    private static MemoryBuffer1D<Query,Stride1D.Dense>? queryBuffer;
    private static MemoryBuffer1D<Choice,Stride1D.Dense>? choiceBuffer;
    private static string statusKey="CPU 预览";
    internal static string Status => Ui.Text(statusKey);
    internal static bool IsAvailable { get { lock(Gate)return Ensure(); } }
    private static bool Ensure()
    {
        if(attempted)return accelerator is not null;
        attempted=true;
        if(Environment.GetEnvironmentVariable("DISC_COLOR_DISABLE_GPU")=="1")return false;
        try
        {
            context=Context.Create(b=>b.Cuda().EnableAlgorithms().Math(MathMode.Default));
            CudaDevice? device=null;
            foreach(var candidate in context.GetCudaDevices())
                if(device is null || candidate.MemorySize>device.MemorySize)device=candidate;
            if(device is null){statusKey="CPU 预览（无兼容 CUDA 显卡）";return false;}
            accelerator=device.CreateAccelerator(context);
            colorKernel=accelerator.LoadAutoGroupedStreamKernel<Index1D,ArrayView<int>,ArrayView<double>,ArrayView<byte>>(Colors);
            searchKernel=accelerator.LoadAutoGroupedStreamKernel<Index1D,ArrayView<Query>,ArrayView<Node>,ArrayView<Choice>,int>(Search);
            statusKey="GPU · "+accelerator.Name;
            AppDomain.CurrentDomain.ProcessExit+=(_,_)=>{lock(Gate)Release();};
            return true;
        }
        catch(Exception e){Disable(e);return false;}
    }
    private static void Release()
    {
        foreach(var buffer in treeBuffers.Values)buffer.Dispose();treeBuffers.Clear();
        queryBuffer?.Dispose();queryBuffer=null;choiceBuffer?.Dispose();choiceBuffer=null;
        accelerator?.Dispose();accelerator=null;context?.Dispose();context=null;
    }
    private static void Disable(Exception e)
    {
        statusKey="CPU 预览（GPU 不可用，已回退）";
        try{Release();}catch{ /* Device loss must not prevent CPU preview. */ }
    }
    internal static Bitmap RenderColors(Bitmap original,ColorMixer mixer,CancellationToken cancellation)
    {
        if(mixer.IsIdentity)return mixer.Render(original,cancellation);
        lock(Gate)
        {
            cancellation.ThrowIfCancellationRequested();
            if(Ensure())
            {
                Bitmap? result=null;
                try
                {
                    
                    result=original.Clone(new Rectangle(0,0,original.Width,original.Height),PixelFormat.Format32bppArgb);
                    var bands=mixer.Bands.Append(mixer.Master).SelectMany(b=>new[]{b.Hue,b.Saturation,b.Luminance}).ToArray();
                    using var values=accelerator!.Allocate1D(bands);
                    int rows=Math.Max(1,262144/result.Width),count=rows*result.Width;
                    var pixels=new int[count];var output=new int[count];var flags=new byte[count];
                    using var buffer=accelerator!.Allocate1D<int>(count);using var corrections=accelerator.Allocate1D<byte>(count);
                    var bits=result.LockBits(new Rectangle(0,0,result.Width,result.Height),ImageLockMode.ReadWrite,PixelFormat.Format32bppArgb);
                    try
                    {
                        for(int y=0;y<result.Height;y+=rows)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            int nrows=Math.Min(rows,result.Height-y),n=nrows*result.Width;
                            for(int j=0;j<nrows;j++)Marshal.Copy(bits.Scan0+(y+j)*bits.Stride,pixels,j*result.Width,result.Width);
                            buffer.CopyFromCPU(pixels);colorKernel!(n,buffer.View,values.View,corrections.View);
                            buffer.CopyToCPU(output);corrections.CopyToCPU(flags);
                            // Re-evaluate only rounding-boundary pixels on the CPU. A few ulps
                            // from device remainder operations must not turn into a different byte.
                            for(int j=0;j<n;j++)if(flags[j]!=0)output[j]=mixer.Apply(Color.FromArgb(pixels[j])).ToArgb();
                            for(int j=0;j<nrows;j++)Marshal.Copy(output,j*result.Width,bits.Scan0+(y+j)*bits.Stride,result.Width);
                        }
                    }
                    finally{result.UnlockBits(bits);}
                    cancellation.ThrowIfCancellationRequested();return result;
                }
                catch(OperationCanceledException){result?.Dispose();throw;}
                catch(Exception e){result?.Dispose();Disable(e);}
            }
        }
        return mixer.Render(original,cancellation);
    }
    internal static Choice[]? Find(Tree tree,Query[] queries,CancellationToken cancellation)
    {
        lock(Gate)
        {
            cancellation.ThrowIfCancellationRequested();if(!Ensure())return null;
            try
            {
                
                if(!treeBuffers.TryGetValue(tree,out var nodes))
                {
                    if(treeBuffers.Count>=4){var old=treeBuffers.First();old.Value.Dispose();treeBuffers.Remove(old.Key);}
                    treeBuffers[tree]=nodes=accelerator!.Allocate1D(tree.Nodes);
                }
                var result=new Choice[queries.Length];
                const int batch=32768;
                queryBuffer??=accelerator!.Allocate1D<Query>(batch);
                choiceBuffer??=accelerator!.Allocate1D<Choice>(batch);
                for(int offset=0;offset<queries.Length;offset+=batch)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int n=Math.Min(batch,queries.Length-offset);
                    queryBuffer.View.BaseView.SubView(0,n).CopyFromCPU((ReadOnlySpan<Query>)queries.AsSpan(offset,n));
                    searchKernel!(n,queryBuffer.View,nodes.View,choiceBuffer.View,tree.SolidCount);
                    choiceBuffer.View.BaseView.SubView(0,n).CopyToCPU(result.AsSpan(offset,n));
                }
                cancellation.ThrowIfCancellationRequested();return result;
            }
            catch(OperationCanceledException){throw;}
            catch(Exception e){Disable(e);return null;}
        }
    }
    private static double Clamp(double x,double lo,double hi)=>Math.Min(hi,Math.Max(lo,x));
    private static double Center(int i)=>i switch{0=>0,1=>30,2=>60,3=>120,4=>180,5=>240,6=>270,7=>315,_=>360};
    private static int Byte(double x)=>(int)Clamp(Math.Round(x*255),0,255);
    private static void Colors(Index1D index,ArrayView<int> pixels,ArrayView<double> bands,ArrayView<byte> flags)
    {
        flags[index]=0;
        int argb=pixels[index];double r=((argb>>16)&255)/255d,g=((argb>>8)&255)/255d,b=(argb&255)/255d;
        double max=Math.Max(r,Math.Max(g,b)),min=Math.Min(r,Math.Min(g,b)),delta=max-min,l=(max+min)*.5;
        double h=0,s=0;
        if(delta>=1e-10)
        {
            s=delta/(1-Math.Abs(2*l-1));
            h=(max==r?(g-b)/delta:max==g?(b-r)/delta+2:(r-g)/delta+4)*60;h=(h+360)%360;
            int i=0;while(i<7&&h>=Center(i+1))i++;
            double t=(h-Center(i))/(Center(i+1)-Center(i));t=t*t*(3-2*t);int j=(i+1)%8;
            double shift=bands[i*3]*(1-t)+bands[j*3]*t,sat=(bands[i*3+1]*(1-t)+bands[j*3+1]*t)/100,
                lum=(bands[i*3+2]*(1-t)+bands[j*3+2]*t)/100;
            double protection=Clamp(s/.15,0,1);protection=protection*protection*(3-2*protection);
            h=(h+shift+360)%360;s=Clamp(sat<0?s*(1+sat):s+(1-s)*sat,0,1);
            lum*=protection;l=Clamp(lum<0?l*(1+lum):l+(1-l)*lum,0,1);
        }
        else if(bands[26]==0)return;
        if(bands[24]!=0)h=(h+bands[24]+360)%360;
        s=Clamp(s*(1+bands[25]/100),0,1);
        double masterLight=bands[26]/100;
        l=Clamp(masterLight<0?l*(1+masterLight):l+(1-l)*masterLight,0,1);
        double c=(1-Math.Abs(2*l-1))*s,x=c*(1-Math.Abs(h/60%2-1)),m=l-c/2;
        if(h<60){r=c;g=x;b=0;}else if(h<120){r=x;g=c;b=0;}else if(h<180){r=0;g=c;b=x;}
        else if(h<240){r=0;g=x;b=c;}else if(h<300){r=x;g=0;b=c;}else{r=c;g=0;b=x;}
        double rr=(r+m)*255,gg=(g+m)*255,bb=(b+m)*255;
        if(Math.Abs(rr-Math.Floor(rr)-.5)<1e-8 || Math.Abs(gg-Math.Floor(gg)-.5)<1e-8 || Math.Abs(bb-Math.Floor(bb)-.5)<1e-8)flags[index]=1;
        pixels[index]=(argb&unchecked((int)0xff000000))|Byte(r+m)<<16|Byte(g+m)<<8|Byte(b+m);
    }
    private static double Square(double value)=>value*value;
    private static double Tone(double actual,double target)=>100*Square(Math.Max(0,actual-target-.018))+36*Square(Math.Max(0,target-actual-.055));
    private static double Error(Query q,Node n)=>q.Light*Square(q.L-n.L)+q.Chroma*(Square(q.A-n.A)+Square(q.B-n.B))+Tone(n.L,q.L);
    private static double Bound(double q,double min,double max){double v=Math.Max(0,Math.Max(min-q,q-max));return v*v;}
    private static void Search(Index1D index,ArrayView<Query> queries,ArrayView<Node> nodes,ArrayView<Choice> output,int solidCount)
    {
        var q=queries[index];int solid=0;double error=Error(q,nodes[0]);
        for(int i=0;i<solidCount;i++){double e=Error(q,nodes[i]);if(e<error){solid=i;error=e;}}
        int best=solid,current=q.Root,previous=-1;
        // Parent links simulate the CPU recursion, preserving near-first traversal and ties.
        while(current>=0)
        {
            var n=nodes[current];double delta=n.Axis==0?q.Q0-n.P0:n.Axis==1?q.Q1-n.P1:n.Axis==2?q.Q2-n.P2:-n.P3;
            int near=delta<0?n.Left:n.Right,far=delta<0?n.Right:n.Left,next;
            if(previous==n.Parent)
            {
                double lower=Tone(Clamp(q.L,n.MinL,n.MaxL),q.L);
                lower+=Bound(q.Q0,n.Min0,n.Max0);lower+=Bound(q.Q1,n.Min1,n.Max1);
                lower+=Bound(q.Q2,n.Min2,n.Max2);lower+=Bound(0,n.Min3,n.Max3);
                if(lower>error+1e-14){previous=current;current=n.Parent;continue;}
                double v=q.Q0-n.P0,d=v*v;v=q.Q1-n.P1;d+=v*v;v=q.Q2-n.P2;d+=v*v;v=-n.P3;d+=v*v;d+=Tone(n.L,q.L);
                if(d<error){error=d;best=current;}
                next=near>=0?near:delta*delta<=error&&far>=0?far:n.Parent;
            }
            else if(previous==near)next=delta*delta<=error&&far>=0?far:n.Parent;
            else next=n.Parent;
            previous=current;current=next;
        }
        output[index]=new Choice{Solid=solid,Best=best};
    }
}
