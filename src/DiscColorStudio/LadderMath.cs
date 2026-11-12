using System.Text.Json;
namespace DiscColorStudio;

internal readonly record struct LVec(double X,double Y,double Z)
{
    public static LVec operator +(LVec a,LVec b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    public static LVec operator -(LVec a,LVec b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    public static LVec operator *(LVec a,double s)=>new(a.X*s,a.Y*s,a.Z*s);
    public static LVec operator /(LVec a,double s)=>a*(1/s);
    public double Dot(LVec b)=>X*b.X+Y*b.Y+Z*b.Z;
    public LVec Cross(LVec b)=>new(Y*b.Z-Z*b.Y,Z*b.X-X*b.Z,X*b.Y-Y*b.X);
    public double Max=>Math.Max(X,Math.Max(Y,Z));
    public double Norm=>Math.Sqrt(Dot(this));
    public double this[int i]=>i==0?X:i==1?Y:Z;
}
internal readonly record struct LadderPixel(float R,float G,float B)
{public LVec Vector=>new(R,G,B);}
internal sealed class LadderCarrier
{
    public double Period{get;set;} public double Gain{get;set;} public int Count{get;set;}
    public string BytesHex{get;set;}=""; public int[] Trim{get;set;}=[];public double[][] Response{get;set;}=[];
    public byte[] Bytes=[];public LVec[] Values=[];
}
internal sealed class LadderModelData
{
    public string Version{get;set;}="";public double ChannelBitNm{get;set;} public double UStep{get;set;}
    public double[] White{get;set;}=[];public double Exposure{get;set;}
    public double[] EyeLeft{get;set;}=[];public double[] EyeRight{get;set;}=[];public double[] Lamp{get;set;}=[];
    public double[][] OffResponse{get;set;}=[];public LadderCarrier[] Carriers{get;set;}=[];
}
internal sealed class LadderMath
{
    internal static readonly Lazy<LadderMath> Shared=new(()=>new());
    internal readonly LadderModelData Data;
    internal readonly LVec White;
    internal readonly int[] Cycle,Rank;
    private readonly LVec[] off;
    private readonly double halfLog;
    private static readonly LVec D65=new(.9505,1,1.089);
    internal static LVec V(double[] a)=>new(a[0],a[1],a[2]);
    internal LadderMath()
    {
        Data=JsonSerializer.Deserialize<LadderModelData>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"data","ladder-model.json")),Design.Json)!;
        if(Data.Version!="ladder-full-response-1"||Data.Carriers.Length!=30||Data.ChannelBitNm!=133.3)throw new InvalidDataException("Invalid ladder model");
        White=V(Data.White);off=Data.OffResponse.Select(V).ToArray();halfLog=.5*Math.Log(Data.Carriers[0].Period/Data.Carriers[1].Period);
        foreach(var c in Data.Carriers){c.Bytes=Convert.FromHexString(c.BytesHex);c.Values=c.Response.Select(V).ToArray();if(c.Bytes.Length!=91||c.Trim.Length!=92)throw new InvalidDataException("Invalid carrier");}
        var slots=Data.Carriers.SelectMany((c,v)=>Enumerable.Range(0,c.Count).Select(r=>(Position:(r+.5)/c.Count,View:v,Rank:r))).OrderBy(s=>s.Position).ThenBy(s=>s.View).ThenBy(s=>s.Rank).ToArray();
        Cycle=slots.Select(s=>s.View).ToArray();Rank=slots.Select(s=>s.Rank).ToArray();
    }
    private LVec Lookup(LVec[] values,double u)
    {double f=Math.Clamp(Math.Abs(u)/Data.UStep,0,values.Length-1);int a=(int)f,b=Math.Min(a+1,values.Length-1);return values[a]+(values[b]-values[a])*(f-a);}
    internal LVec Weights(int k,double designU)
    {double lam=Math.Log(Math.Max(designU,1e-9)*Data.Carriers[k].Period*Data.ChannelBitNm);double red=Math.Clamp((lam+halfLog-Math.Log(580))/(2*halfLog),0,1),blue=Math.Clamp((Math.Log(490)-lam+halfLog)/(2*halfLog),0,1);return new(red,1-red-blue,blue);}
    internal void Affine(double u,double view,Span<LVec> a,out LVec b)
    {
        b=Lookup(off,view);a.Clear();
        for(int k=0;k<Data.Carriers.Length;k++)
        {
            var c=Data.Carriers[k];LVec w=Weights(k,u),difference=(Lookup(c.Values,view)-b)*(c.Count/(double)Cycle.Length*c.Gain);
            a[0]+=difference*w.X;a[1]+=difference*w.Y;a[2]+=difference*w.Z;
        }
        b/=Data.Exposure;for(int k=0;k<3;k++)a[k]/=Data.Exposure;
    }
    internal static double Linear(double v)=>v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);
    internal static LVec LinearRgb(int argb)=>new(Linear(((argb>>16)&255)/255d),Linear(((argb>>8)&255)/255d),Linear((argb&255)/255d));
    internal static LVec Xyz(LVec c)=>new(.412395588967414*c.X+.357583430763715*c.Y+.180492647381701*c.Z,.212586230785596*c.X+.715170303703411*c.Y+.0722004986433362*c.Z,.0192972154917469*c.X+.119183864580849*c.Y+.95049712513158*c.Z);
    private static double Derivative(double x)=>x>Math.Pow(6d/29,3)?1/(3*Math.Pow(Math.Max(x,1e-12),2d/3)):1/(3*Math.Pow(6d/29,2));
    private static LVec Perceptual(LVec v,LVec derivative)=>new(116*.65*v.Y*derivative.Y,500*(v.X*derivative.X-v.Y*derivative.Y),200*(v.Y*derivative.Y-v.Z*derivative.Z));
    internal LadderPixel Fit(int argb,double u,ReadOnlySpan<double> views,double stability=1)
    {
        var rgb=LinearRgb(argb);if(rgb.Max<=.003)return default;
        Span<LVec> a=stackalloc LVec[3];Affine(u,u,a,out var b);
        var xyz=Xyz(rgb);LVec target=new(xyz.X/D65.X*White.X*.9,xyz.Y*.9,xyz.Z/D65.Z*White.Z*.9);
        double tone=b.Y+Math.Max(.9-b.Y,0)*Math.Pow(xyz.Y,.85),lift=Math.Min(8,tone/Math.Max(target.Y,1e-12));var goal=target*lift;
        LVec derivative=new(Derivative(Math.Max(0,goal.X/White.X))/White.X,Derivative(Math.Max(0,goal.Y/White.Y))/White.Y,Derivative(Math.Max(0,goal.Z/White.Z))/White.Z);
        Span<double> h=stackalloc double[9];Span<double> rhs=stackalloc double[3];Span<double> s=stackalloc double[3];h.Clear();rhs.Clear();
        double det=a[0].Dot(a[1].Cross(a[2]));var d=goal-b;
        if(Math.Abs(det)>1e-14){s[0]=Math.Clamp(d.Dot(a[1].Cross(a[2]))/det,0,1);s[1]=Math.Clamp(a[0].Dot(d.Cross(a[2]))/det,0,1);s[2]=Math.Clamp(a[0].Dot(a[1].Cross(d))/det,0,1);}else s.Fill(.5);
        Accumulate(a,d,derivative,1,h,rhs);
        foreach(double view in views){Affine(u,view,a,out var bv);Accumulate(a,goal-bv,derivative,.25*stability/views.Length,h,rhs);}
        for(int iteration=0;iteration<60;iteration++)
        {double change=0;for(int k=0;k<3;k++){double residual=rhs[k];for(int j=0;j<3;j++)residual-=h[k*3+j]*s[j];double next=Math.Clamp(s[k]+residual/Math.Max(h[k*3+k],1e-15),0,1);change=Math.Max(change,Math.Abs(next-s[k]));s[k]=next;}if(change<2e-8)break;}
        double toe=Math.Clamp((rgb.Max-.003)/(.035-.003),0,1);toe=toe*toe*(3-2*toe);
        return new((float)(s[0]*toe),(float)(s[1]*toe),(float)(s[2]*toe));
    }
    private static void Accumulate(ReadOnlySpan<LVec> a,LVec d,LVec derivative,double weight,Span<double> h,Span<double> rhs)
    {
        Span<LVec> q=stackalloc LVec[3];for(int k=0;k<3;k++)q[k]=Perceptual(a[k],derivative);var target=Perceptual(d,derivative);
        for(int k=0;k<3;k++){rhs[k]+=weight*q[k].Dot(target);for(int j=0;j<3;j++)h[k*3+j]+=weight*q[k].Dot(q[j]);}
    }
    internal LVec Predict(LadderPixel p,double u,double view)
    {Span<LVec>a=stackalloc LVec[3];Affine(u,view,a,out var b);return b+a[0]*p.R+a[1]*p.G+a[2]*p.B;}
    internal int Display(LVec xyz)
    {
        var v=new LVec(xyz.X/White.X*D65.X,xyz.Y,xyz.Z/White.Z*D65.Z);
        var rgb=new LVec(3.2406*v.X-1.5372*v.Y-.4986*v.Z,-.9689*v.X+1.8758*v.Y+.0415*v.Z,.0557*v.X-.204*v.Y+1.057*v.Z);
        double low=Math.Min(rgb.X,Math.Min(rgb.Y,rgb.Z)),t=low<0?v.Y/Math.Max(v.Y-low,1e-12):1;rgb=new LVec(v.Y,v.Y,v.Y)+(rgb-new LVec(v.Y,v.Y,v.Y))*t;
        rgb/=Math.Max(1,rgb.Max);int C(double x)=>(int)Math.Clamp(Math.Round(255*(x<=.0031308?12.92*x:1.055*Math.Pow(Math.Max(0,x),1/2.4)-.055)),0,255);
        return Color.FromArgb(C(rgb.X),C(rgb.Y),C(rgb.Z)).ToArgb();
    }
}
internal sealed class LadderPose
{
    internal readonly double Phi;
    internal readonly LVec Left,Right,Lamp,Eye;
    internal readonly (LVec Eye,LVec Lamp)[] Samples;
    internal LadderPose(double centerX,double centerY,double spin=0,double tiltX=0,double tiltY=0)
    {
        Phi=Math.Atan2(-centerY,centerX)+spin*Math.PI/180;var m=LadderMath.Shared.Value.Data;
        LVec Turn(LVec a)
        {
            // Inverse rigid pose: move the fixed world eye/light into disc coordinates.
            double tx=tiltX*Math.PI/180,ty=tiltY*Math.PI/180;
            var b=new LVec(a.X,Math.Cos(tx)*a.Y-Math.Sin(tx)*a.Z,Math.Sin(tx)*a.Y+Math.Cos(tx)*a.Z);
            var c=new LVec(Math.Cos(ty)*b.X+Math.Sin(ty)*b.Z,b.Y,-Math.Sin(ty)*b.X+Math.Cos(ty)*b.Z);
            return new(Math.Cos(Phi)*c.X-Math.Sin(Phi)*c.Y,Math.Sin(Phi)*c.X+Math.Cos(Phi)*c.Y,c.Z);
        }
        Left=Turn(LadderMath.V(m.EyeLeft));Right=Turn(LadderMath.V(m.EyeRight));Lamp=Turn(LadderMath.V(m.Lamp));Eye=(Left+Right)*.5;
        LVec Tilt(LVec a,double t)=>new(a.X,Math.Cos(t)*a.Y-Math.Sin(t)*a.Z,Math.Sin(t)*a.Y+Math.Cos(t)*a.Z);
        Samples=(from tilt in new[]{-.5,0,.5} from eye in new[]{LadderMath.V(m.EyeLeft),LadderMath.V(m.EyeRight)} select (Turn(Tilt(eye,tilt*Math.PI/180)),Turn(Tilt(LadderMath.V(m.Lamp),tilt*Math.PI/180)))).ToArray();
    }
    private LadderPose(LVec left,LVec right,LVec lamp,(LVec Eye,LVec Lamp)[] samples,double phi)
    {Left=left;Right=right;Eye=(left+right)*.5;Lamp=lamp;Samples=samples;Phi=phi;}
    internal static LadderPose ForLayer(Layer layer,double spin=0,double tiltX=0,double tiltY=0,double headShift=0)
    {
        if(layer.LadderViewing is {} viewing)
        {
            // Construct the viewing scene in world coordinates, then rotate it
            // back into the fixed encoding coordinates of the physical disc.
            double alpha=viewing.DiscRotation,beta=viewing.ObserverAzimuth*Math.PI/180;
            var centre=Transform(new LVec(layer.X,-layer.Y,0),-alpha,0,0);
            double radius=Math.Sqrt(layer.X*layer.X+layer.Y*layer.Y);
            if(radius<1e-9)radius=41; // Reference image radius for hub-centred layers.
            var m=LadderMath.Shared.Value.Data;
            var mid=(LadderMath.V(m.EyeLeft)+LadderMath.V(m.EyeRight))*.5;
            var worldEye=centre+new LVec(radius*Math.Cos(beta),-radius*Math.Sin(beta),mid.Z);
            var worldLeft=worldEye+LadderMath.V(m.EyeLeft)-mid;var worldRight=worldEye+LadderMath.V(m.EyeRight)-mid;
            var worldLamp=LadderMath.V(m.Lamp);
            LVec Turn(LVec v,double extraTilt=0)=>Transform(v,alpha+spin,tiltX+extraTilt,tiltY);
            var viewingSamples=(from tilt in new[]{-.5,0,.5} from e in new[]{worldLeft,worldRight} select (Turn(e,tilt),Turn(worldLamp,tilt))).ToArray();
            var viewingPose=new LadderPose(Turn(worldLeft),Turn(worldRight),Turn(worldLamp),viewingSamples,(alpha+spin)*Math.PI/180);
            return headShift==0?viewingPose:viewingPose.Shift(headShift);
        }
        if(layer.LadderPlacement is {} placement)
        {
            double angle=placement.WorldAngle(layer)*Math.PI/180;
            var reference=new LadderPose(Math.Cos(angle),Math.Sin(angle),spin,tiltX,tiltY);
            return headShift==0?reference:reference.Shift(headShift);
        }
        if(layer.LadderObservation is not {} observation)
        {
            var legacy=new LadderPose(layer.X,layer.Y,spin,tiltX,tiltY);
            return headShift==0?legacy:legacy.Shift(headShift);
        }
        double a=observation.WorldAzimuth(layer)*Math.PI/180,i=observation.ResolvedInclination(layer)*Math.PI/180;
        var n=new LVec(Math.Cos(a),-Math.Sin(a),0);var side=new LVec(-n.Y,n.X,0);
        var eye=new LVec(layer.X,-layer.Y,0)+new LVec(n.X*Math.Sin(i),n.Y*Math.Sin(i),Math.Cos(i))*observation.Distance;
        var left=eye-side*32.5;var right=eye+side*32.5;var lamp=LadderMath.V(LadderMath.Shared.Value.Data.Lamp);
        var samples=(from tilt in new[]{-.5,0,.5} from e in new[]{left,right} select (Transform(e,0,tilt,0),Transform(lamp,0,tilt,0))).ToArray();
        var pose=new LadderPose(Transform(left,spin,tiltX,tiltY),Transform(right,spin,tiltX,tiltY),Transform(lamp,spin,tiltX,tiltY),
            samples.Select(v=>(Transform(v.Item1,spin,tiltX,tiltY),Transform(v.Item2,spin,tiltX,tiltY))).ToArray(),spin*Math.PI/180);
        return headShift==0?pose:pose.Shift(headShift);
    }
    private LadderPose Shift(double mm)
    {
        var side=Right-Left;var delta=side/side.Norm*mm;
        return new(Left+delta,Right+delta,Lamp,Samples.Select(v=>(v.Eye+delta,v.Lamp)).ToArray(),Phi);
    }
    private static LVec Transform(LVec a,double spin,double tiltX,double tiltY)
    {
        double tx=tiltX*Math.PI/180,ty=tiltY*Math.PI/180,p=spin*Math.PI/180;
        var b=new LVec(a.X,Math.Cos(tx)*a.Y-Math.Sin(tx)*a.Z,Math.Sin(tx)*a.Y+Math.Cos(tx)*a.Z);
        var c=new LVec(Math.Cos(ty)*b.X+Math.Sin(ty)*b.Z,b.Y,-Math.Sin(ty)*b.X+Math.Cos(ty)*b.Z);
        return new(Math.Cos(p)*c.X-Math.Sin(p)*c.Y,Math.Sin(p)*c.X+Math.Cos(p)*c.Y,c.Z);
    }
    internal static double U(double x,double y,LVec eye,LVec lamp)
    {double r=Math.Sqrt(x*x+y*y);if(r<1e-9)return 0;var p=new LVec(x,y,0);var e=eye-p;var l=lamp-p;var g=e/e.Norm+l/l.Norm;return Math.Abs((g.X*y-g.Y*x)/r);}
    internal double At(double x,double canvasY)=>U(x,-canvasY,Eye,Lamp);
    internal void Nearby(double x,double canvasY,Span<double> values){for(int i=0;i<Samples.Length;i++)values[i]=U(x,-canvasY,Samples[i].Eye,Samples[i].Lamp);}
}
