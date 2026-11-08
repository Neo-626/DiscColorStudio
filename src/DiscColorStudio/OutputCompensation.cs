using System.Text.Json;

namespace DiscColorStudio;

// An explicitly provisional forward output model, not a camera/ICC calibration.
// Positive Warmth asks to remove a warm cast; positive Tint asks to remove magenta.
// Model gains are therefore warm/magenta, so matching selects a cooler/greener recipe.
public sealed record OutputCompensation
{
    public double Warmth { get; set; }
    public double Tint { get; set; }
    public double Strength { get; set; }
    public string Reference { get; set; } = Ui.Text("手动估计");
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Active => Strength>0 && (Warmth!=0 || Tint!=0);
    public void Validate()
    {
        if(!double.IsFinite(Warmth)||!double.IsFinite(Tint)||!double.IsFinite(Strength)
            ||Math.Abs(Warmth)>100||Math.Abs(Tint)>100||Strength is <0 or >100)
            throw new InvalidDataException(Ui.Text("成品偏色补偿参数无效。"));
    }
    internal (double R,double G,double B) Gains()
    {
        double w=Warmth/50*Strength/100,t=Tint/50*Strength/100;
        return (Math.Pow(2,w+t/2),Math.Pow(2,-t),Math.Pow(2,-w+t/2));
    }
    internal OpticalLab Predict(double r,double g,double b)
    {
        if(!Active)return OpticalLab.FromLinear(r,g,b);
        var gains=Gains();return OpticalLab.FromLinear(r*gains.R,g*gains.G,b*gains.B);
    }
    internal Color Predict(Color color)
    {
        if(!Active)return color;
        var gains=Gains();
        static int Channel(double value)
        {double v=value<=.0031308?value*12.92:1.055*Math.Pow(value,1/2.4)-.055;return (int)Math.Clamp(Math.Round(v*255),0,255);}
        return Color.FromArgb(color.A,Channel(OpticalLab.Linear(color.R/255d)*gains.R),
            Channel(OpticalLab.Linear(color.G/255d)*gains.G),Channel(OpticalLab.Linear(color.B/255d)*gains.B));
    }

}
