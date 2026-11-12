namespace DiscColorStudio;

// Two independent settings, both clockwise in the observation plane (X right, Y down).
// DiscRotation rotates the physical disc about its hub. ObserverAzimuth points
// from the rotated image centre toward the viewer, in fixed world coordinates.
public sealed record LadderViewing(double DiscRotation=0,double ObserverAzimuth=180)
{
    private static double Normalize(double angle){double result=angle%360;return result<0?result+360:result;}
    public void Validate()
    {
        if(!double.IsFinite(DiscRotation)||DiscRotation is <0 or >=360||!double.IsFinite(ObserverAzimuth)||ObserverAzimuth is <0 or >=360)
            throw new InvalidDataException(Ui.Text("观看方向参数无效。"));
    }
    internal static LadderViewing Current(Layer layer)
    {
        if(layer.LadderViewing is {} viewing)return viewing;
        double centre=Math.Atan2(layer.Y,layer.X)*180/Math.PI;
        if(layer.LadderPlacement is {} old)
        {
            double disc=Normalize(-old.WorldAngle(layer));
            return new(disc,Normalize(centre+disc+180));
        }
        if(layer.LadderObservation is {} observer)return new(0,observer.WorldAzimuth(layer));
        return new(Normalize(-centre),180);
    }
    internal static void SetDisc(Layer layer,double angle)=>Apply(layer,Current(layer) with{DiscRotation=Normalize(angle)});
    internal static void SetObserver(Layer layer,double angle)=>Apply(layer,Current(layer) with{ObserverAzimuth=Normalize(angle)});
    private static void Apply(Layer layer,LadderViewing viewing)
    {
        layer.LadderViewing=viewing;layer.LadderPlacement=null;layer.LadderObservation=null;
    }
    internal static double ArrowAngle(Layer layer)
    {
        if(layer.LadderViewing is {} viewing)return Normalize(viewing.ObserverAzimuth-viewing.DiscRotation);
        if(layer.LadderObservation is {} old)return old.WorldAzimuth(layer);
        var current=Current(layer);return Normalize(current.ObserverAzimuth-current.DiscRotation);
    }
    internal static void SetArrow(Layer layer,double canvasAngle)=>SetObserver(layer,canvasAngle+Current(layer).DiscRotation);
}
