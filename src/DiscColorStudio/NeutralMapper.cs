namespace DiscColorStudio;

// Provisional reflectance model: JPEG samples interpreted as sRGB, NOT an ICC calibration.
// Source white/gray are achromatic. Fixed tone endpoints use the complete reference palette,
// never the currently enabled subset. Areas mix in linear RGB, not in perceptual Lab space.
internal sealed class NeutralMapper
{
    private sealed record Candidate(byte First,byte Second,double Weight,OpticalLab Lab,double Variance);
    private sealed class Node(Candidate value, double[] point, int axis)
    {
        internal Candidate Value=value;
        internal double[] Point=point;
        internal int Axis=axis;
        internal Node? Left,Right;
        internal double[] Minimum=(double[])point.Clone(),Maximum=(double[])point.Clone();
        internal double MinL=value.Lab.L,MaxL=value.Lab.L;
    }
    private readonly PhotoSettings settings;
    private readonly double floor,ceiling,lightWeight;
    private readonly Candidate[] solids;
    private readonly Dictionary<byte,double[]> linear;
    private readonly Node[] trees;
    private readonly double[] chromaWeights;
    private OpticalLab Predict(double r,double g,double b)=>settings.OutputCompensation?.Active==true
        ?settings.OutputCompensation.Predict(r,g,b):OpticalLab.FromLinear(r,g,b);
    private OpticalLab Predict(Color c)=>settings.OutputCompensation?.Active==true
        ?Predict(OpticalLab.Linear(c.R/255d),OpticalLab.Linear(c.G/255d),OpticalLab.Linear(c.B/255d)):OpticalLab.From(c);
    private GpuPreview.Tree? gpuTree;
    private readonly Dictionary<int,AdaptiveMapper.Plan> gpuPlans=[];
    private GpuPreview.Tree GpuTree()
    {
        if(gpuTree is not null)return gpuTree;
        var values=new List<GpuPreview.Node>();
        GpuPreview.Node Pack(Candidate c)=>new(){L=c.Lab.L,A=c.Lab.A,B=c.Lab.B,Weight=c.Weight,First=c.First,Second=c.Second,Left=-1,Right=-1,Parent=-1};
        foreach(var c in solids)values.Add(Pack(c));
        int Add(Node? node,int parent)
        {
            if(node is null)return -1;
            int index=values.Count;var value=Pack(node.Value);
            value.P0=node.Point[0];value.P1=node.Point[1];value.P2=node.Point[2];value.P3=node.Point[3];value.Axis=node.Axis;value.Parent=parent;
            value.Min0=node.Minimum[0];value.Min1=node.Minimum[1];value.Min2=node.Minimum[2];value.Min3=node.Minimum[3];
            value.Max0=node.Maximum[0];value.Max1=node.Maximum[1];value.Max2=node.Maximum[2];value.Max3=node.Maximum[3];value.MinL=node.MinL;value.MaxL=node.MaxL;
            values.Add(value);value.Left=Add(node.Left,index);value.Right=Add(node.Right,index);values[index]=value;return index;
        }
        int[] roots=trees.Select(root=>Add(root,-1)).ToArray();
        return gpuTree=new(values.ToArray(),roots,solids.Length);
    }
    internal Dictionary<int,AdaptiveMapper.Plan>? Batch(int[] colors,CancellationToken cancellation)
    {
        var missing=colors.Where(c=>!gpuPlans.ContainsKey(c)).ToArray();
        if(missing.Length==0)return gpuPlans;
        if(gpuPlans.Count+missing.Length>262144){gpuPlans.Clear();missing=colors;}
        colors=missing;
        var tree=GpuTree();var queries=new GpuPreview.Query[colors.Length];
        for(int i=0;i<colors.Length;i++)
        {
            if((i&1023)==0)cancellation.ThrowIfCancellationRequested();
            var rgb=Color.FromArgb(colors[i]);var target=Target(rgb);double sourceChroma=OpticalLab.From(rgb).Chroma;
            double neutral=Math.Clamp(1-sourceChroma/.08,0,1);neutral=neutral*neutral*(3-2*neutral);
            int bucket=(int)Math.Round(neutral*8);double chroma=chromaWeights[bucket];
            queries[i]=new(){L=target.L,A=target.A,B=target.B,Q0=target.L*Math.Sqrt(lightWeight),
                Q1=target.A*Math.Sqrt(chroma),Q2=target.B*Math.Sqrt(chroma),Light=lightWeight,Chroma=chroma,Root=tree.Roots[bucket]};
        }
        var choices=GpuPreview.Find(tree,queries,cancellation);if(choices is null)return null;
        var result=gpuPlans;
        for(int i=0;i<colors.Length;i++)
        {
            if((i&1023)==0)cancellation.ThrowIfCancellationRequested();
            var q=queries[i];var solid=tree.Nodes[choices[i].Solid];var best=tree.Nodes[choices[i].Best];
            double Error(GpuPreview.Node p)=>lightWeight*Math.Pow(q.L-p.L,2)+q.Chroma*(Math.Pow(q.A-p.A,2)+Math.Pow(q.B-p.B,2))+TonePenalty(p.L,q.L);
            var plan=new AdaptiveMapper.Plan((byte)solid.First,(byte)best.First,(byte)best.Second,best.Weight,Math.Sqrt(Error(solid)),Math.Sqrt(Error(best)));
            result.Add(colors[i],plan with{NeutralFlat=MayMixFlat(Color.FromArgb(colors[i]),plan)});
        }
        return result;
    }
    internal NeutralMapper(Design d, PhotoSettings settings)
    {
        this.settings=settings;lightWeight=settings.PrioritizeLightness?1.8:1.2;
        var reference=d.Palette.Select(s=>OpticalLab.From(s.Color(d.MatchGain))).ToArray();
        floor=reference.Min(c=>c.L);
        // Reference highlight is chosen once from the COMPLETE palette for brightness and
        // neutrality. A vivid red being brighter than gray must not raise the gray-axis ceiling.
        ceiling=reference.MinBy(c=>Math.Pow(1-c.L,2)+32*c.Chroma*c.Chroma).L;
        solids=d.Palette.Where(s=>d.AllowedIds.Contains(s.Id)).OrderBy(s=>s.Id)
            .Select(s=>new Candidate((byte)s.Id,(byte)s.Id,0,Predict(s.Color(d.MatchGain)),0)).ToArray();
        linear=d.Palette.ToDictionary(s=>(byte)s.Id,s=>{
            var c=s.Color(d.MatchGain);return new[]{OpticalLab.Linear(c.R/255d),OpticalLab.Linear(c.G/255d),OpticalLab.Linear(c.B/255d)};});
        var candidates=solids.ToList();
        for(int i=0;i<solids.Length;i++)for(int j=i+1;j<solids.Length;j++)
        {
            var a=solids[i];var b=solids[j];var x=linear[a.First];var y=linear[b.First];
            double variance=Math.Pow(a.Lab.L-b.Lab.L,2)+Math.Pow(a.Lab.A-b.Lab.A,2)+Math.Pow(a.Lab.B-b.Lab.B,2);
            for(int n=1;n<32;n++){double t=n/32d;
                candidates.Add(new(a.First,b.First,t,Predict(x[0]*(1-t)+y[0]*t,x[1]*(1-t)+y[1]*t,x[2]*(1-t)+y[2]*t),.015*t*(1-t)*variance));}
        }
        chromaWeights=Enumerable.Range(0,9).Select(i=>1.25+48*settings.NeutralStrength*i/8d).ToArray();
        trees=chromaWeights.Select(w=>Build(candidates.Select(c=>(c,Point(c,w))).ToArray(),0)!).ToArray();
    }
    internal OpticalLab Target(Color rgb)
    {
        var p=OpticalLab.From(rgb);
        // Keep the neutral axis exact, including numerical Oklab matrix rounding residuals.
        bool neutral=rgb.R==rgb.G && rgb.G==rgb.B;
        return new(floor+(ceiling-floor)*Math.Pow(Math.Clamp(p.L,0,1),settings.Gamma),
            neutral?0:p.A*settings.Chroma,neutral?0:p.B*settings.Chroma);
    }
    private double[] Point(Candidate c,double chroma)=>[c.Lab.L*Math.Sqrt(lightWeight),c.Lab.A*Math.Sqrt(chroma),c.Lab.B*Math.Sqrt(chroma),Math.Sqrt(c.Variance)];
    private static Node? Build((Candidate C,double[] P)[] values,int depth)
    {
        if(values.Length==0)return null;
        int axis=depth%4;Array.Sort(values,(a,b)=>a.P[axis].CompareTo(b.P[axis]));int mid=values.Length/2;
        var node=new Node(values[mid].C,values[mid].P,axis){Left=Build(values[..mid],depth+1),Right=Build(values[(mid+1)..],depth+1)};
        foreach(var child in new[]{node.Left,node.Right})if(child is not null)
        {
            for(int i=0;i<4;i++){node.Minimum[i]=Math.Min(node.Minimum[i],child.Minimum[i]);node.Maximum[i]=Math.Max(node.Maximum[i],child.Maximum[i]);}
            node.MinL=Math.Min(node.MinL,child.MinL);node.MaxL=Math.Max(node.MaxL,child.MaxL);
        }
        return node;
    }
    private static double TonePenalty(double actual,double target)=>
        100*Math.Pow(Math.Max(0,actual-target-.018),2)+36*Math.Pow(Math.Max(0,target-actual-.055),2);
    private static void Search(Node? node,ReadOnlySpan<double> query,double targetL,ref Candidate best,ref double error)
    {
        if(node is null)return;
        double lower=TonePenalty(Math.Clamp(targetL,node.MinL,node.MaxL),targetL);
        for(int i=0;i<4;i++){double v=Math.Max(0,Math.Max(node.Minimum[i]-query[i],query[i]-node.Maximum[i]));lower+=v*v;}
        // Conservative slack preserves equal-distance traversal and floating-point ties.
        if(lower>error+1e-14)return;
        double d=0;for(int i=0;i<4;i++){double v=query[i]-node.Point[i];d+=v*v;}
        d+=TonePenalty(node.Value.Lab.L,targetL);
        if(d<error){error=d;best=node.Value;}
        double delta=query[node.Axis]-node.Point[node.Axis];
        Search(delta<0?node.Left:node.Right,query,targetL,ref best,ref error);
        if(delta*delta<=error)Search(delta<0?node.Right:node.Left,query,targetL,ref best,ref error);
    }
    internal AdaptiveMapper.Plan Choose(Color rgb)
    {
        var target=Target(rgb);double sourceChroma=OpticalLab.From(rgb).Chroma;
        double neutral=Math.Clamp(1-sourceChroma/.08,0,1);neutral=neutral*neutral*(3-2*neutral);
        int bucket=(int)Math.Round(neutral*8);double chroma=chromaWeights[bucket];
        double Error(OpticalLab p)=>lightWeight*Math.Pow(target.L-p.L,2)+chroma*(Math.Pow(target.A-p.A,2)+Math.Pow(target.B-p.B,2))+TonePenalty(p.L,target.L);
        var solid=solids[0];double single=Error(solid.Lab);
        foreach(var c in solids){double e=Error(c.Lab);if(e<single){solid=c;single=e;}}
        var best=solid;double score=single;
        Span<double> query=stackalloc double[4]{target.L*Math.Sqrt(lightWeight),target.A*Math.Sqrt(chroma),target.B*Math.Sqrt(chroma),0};
        Search(trees[bucket],query,target.L,ref best,ref score);
        return new(solid.First,best.First,best.Second,best.Weight,Math.Sqrt(single),Math.Sqrt(Error(best.Lab)));
    }
    internal bool MayMixFlat(Color rgb,AdaptiveMapper.Plan plan)
    {
        if(!settings.MixNeutralFlats || settings.NeutralStrength<=0 || OpticalLab.From(rgb).Chroma>.045 || plan.First==plan.Second)return false;
        // No blanket screen of dots: require meaningful reduction in BOTH cast and total error.
        var solid=solids.Single(c=>c.First==plan.Solid);
        var a=linear[plan.First];var b=linear[plan.Second];double t=plan.Weight;
        var mixed=Predict(a[0]*(1-t)+b[0]*t,a[1]*(1-t)+b[1]*t,a[2]*(1-t)+b[2]*t);
        return solid.Lab.Chroma-mixed.Chroma>.008 && plan.SingleError-plan.MixedError>.006;
    }
}
