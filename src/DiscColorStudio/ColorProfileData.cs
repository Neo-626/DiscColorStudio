namespace DiscColorStudio;
public static class ColorProfileData
{
    public sealed class Response { public double Angle {get;set;} public double[] Linear {get;set;}=[]; }
    public sealed class Texture { public int Index {get;set;} public string CellId {get;set;}=""; public string TextureId {get;set;}=""; public string Motif {get;set;}=""; public Response[] Samples {get;set;}=[]; internal byte[] Bytes=[]; }
    public sealed record Component(int Texture,double Weight);
    public static double Srgb(double x)=>x<=.0031308?12.92*x:1.055*Math.Pow(x,1/2.4)-.055;
}
