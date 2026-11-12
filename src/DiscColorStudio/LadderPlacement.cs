namespace DiscColorStudio;

// One in-plane reference-frame direction. The original optical setup is rotated
// rigidly with this frame; no eye inclination/distance is inferred from the angle.
public sealed record LadderPlacement(double LocalAngle=0)
{
    public double WorldAngle(Layer layer)=>LadderObservation.Normalize(LocalAngle+layer.Rotation);
    public void Validate()
    {if(!double.IsFinite(LocalAngle)||LocalAngle is <0 or >=360)throw new InvalidDataException(Ui.Text("观看摆位角无效。"));}
    internal static double CurrentAngle(Layer layer)=>layer.LadderPlacement?.WorldAngle(layer)
        ??layer.LadderObservation?.WorldAzimuth(layer)
        ??LadderObservation.Normalize(Math.Atan2(layer.Y,layer.X)*180/Math.PI);
    internal static void SetAngle(Layer layer,double angle)
    {
        layer.LadderPlacement=new(LadderObservation.Normalize(angle-layer.Rotation));
        layer.LadderObservation=null; // Explicit edit replaces the old 3-D observer mode.
    }
}
