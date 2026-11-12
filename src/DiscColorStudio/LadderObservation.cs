namespace DiscColorStudio;

// Canvas degrees, clockwise; local azimuth follows image rotation. Distances in mm.
public sealed record LadderObservation(double LocalAzimuth=0,double Inclination=15,bool AutoInclination=true,double Distance=500)
{
    public static double Normalize(double angle)=>(angle%360+360)%360;
    public double WorldAzimuth(Layer layer)=>Normalize(LocalAzimuth+layer.Rotation);
    public void Validate()
    {
        if(!double.IsFinite(LocalAzimuth)||LocalAzimuth is <0 or >=360||!double.IsFinite(Inclination)||Inclination is <0 or >60||!double.IsFinite(Distance)||Distance is <100 or >5000)
            throw new InvalidDataException(Ui.Text("观看方向参数无效。"));
    }
    internal double ResolvedInclination(Layer layer)
    {
        if(!AutoInclination)return Inclination;
        double r=Math.Sqrt(layer.X*layer.X+layer.Y*layer.Y);
        if(r<1e-9)return Inclination;
        var p=new LVec(layer.X,-layer.Y,0);var t=new LVec(p.Y/r,-p.X/r,0);
        double angle=WorldAzimuth(layer)*Math.PI/180;
        var n=new LVec(Math.Cos(angle),-Math.Sin(angle),0);
        var light=LadderMath.V(LadderMath.Shared.Value.Data.Lamp)-p;
        double a=t.Dot(n),b=t.Dot(light/light.Norm),limit=Math.Sin(Math.PI/3);
        if(Math.Abs(a)<1e-10)return Inclination;
        // At the centre, u = |b + a sin(inclination)|. Target the calibrated
        // middle of the ladder's visible coverage. This estimates geometry,
        // never a measured optimum or a rotated physical track.
        double[] candidates=[0,limit,Math.Sin(Inclination*Math.PI/180),Math.Clamp((.31-b)/a,0,limit),Math.Clamp((-.31-b)/a,0,limit)];
        double best=candidates.OrderBy(v=>Math.Round(Math.Abs(Math.Abs(b+a*v)-.31),12)).ThenBy(v=>Math.Abs(Math.Asin(v)*180/Math.PI-Inclination)).First();
        return Math.Asin(best)*180/Math.PI;
    }
}
// Immutable shared-preview snapshot: changing the observation never edits recipes.
public sealed record LadderReference(double X,double Y,double Rotation,LadderObservation? Observation,LadderPlacement? Placement=null,LadderViewing? Viewing=null)
{
    public static LadderReference From(Layer layer)=>new(layer.X,layer.Y,layer.Rotation,layer.LadderObservation,layer.LadderPlacement,layer.LadderViewing);
    internal Layer Layer()=>new(){X=X,Y=Y,Rotation=Rotation,LadderObservation=Observation,LadderPlacement=Placement,LadderViewing=Viewing};
}
