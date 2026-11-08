namespace DiscColorStudio;

public sealed class ImageAdjustments
{
    public double Exposure { get; set; } = 1;
    public double Brightness { get; set; }
    public double Contrast { get; set; } = 1;
    public ColorMixer? Colors { get; set; }
    public bool BypassColors { get; set; }
    public double BandingReduction { get; set; } = 1;
    public double AdaptiveMixing { get; set; } = 1;
    public bool PreferDisDark { get; set; }
    public int RasterSize { get; set; } = RasterGrid.DefaultSize;
    public ImageAdjustments Copy() { var copy=(ImageAdjustments)MemberwiseClone();copy.Colors=Colors?.Copy();return copy; }
    public static ImageAdjustments From(Design design, Layer layer) => new()
    {
        Exposure=layer.InputExposure,Brightness=layer.Brightness,Contrast=layer.Contrast,Colors=layer.ColorMixer?.Copy(),
        BypassColors=layer.BypassColorMixer,BandingReduction=layer.BandingReduction,AdaptiveMixing=layer.AdaptiveMixing,
        PreferDisDark=layer.PreferDisDark,RasterSize=design.ImageRasterSize(layer)
    };
    public void Apply(Layer layer)
    {
        layer.InputExposure=Exposure;layer.Brightness=Brightness;layer.Contrast=Contrast;layer.ColorMixer=Colors?.Copy();
        layer.BypassColorMixer=BypassColors;layer.BandingReduction=BandingReduction;layer.AdaptiveMixing=AdaptiveMixing;
        layer.PreferDisDark=PreferDisDark;layer.RasterSize=RasterSize;
    }
    public void Validate()
    {
        if(new[]{Exposure,Brightness,Contrast,BandingReduction,AdaptiveMixing}.Any(v=>!double.IsFinite(v))
            ||Exposure is <.1 or >8||Brightness is <-128 or >128||Contrast is <.1 or >4
            ||BandingReduction is <0 or >1||AdaptiveMixing is <0 or >1||!RasterGrid.Sizes.Contains(RasterSize))
            throw new InvalidDataException(Ui.Text("图片调整参数无效。"));
        Colors?.Validate();
    }
}

public static class ContinuousDesign
{
    public static bool Prepare(Design design)
    {
        bool changed=!AngleColorSettings.IsCurrent(design);
        AngleColorSettings.Attach(design,changed);
        changed|=design.AngleColor!.Enabled==false||design.RegistrationMarks||design.ColorModelVersion!=2||design.QuantizationMode!="adaptive";
        design.AngleColor.Enabled=true;design.RegistrationMarks=false;design.ColorModelVersion=2;design.QuantizationMode="adaptive";
        design.ImageDefaults??=new ImageAdjustments{RasterSize=design.RasterSize};
        foreach(var layer in design.Layers)
        {
            if(layer.Kind=="image")
            {
                changed|=layer.AngleColorEnabled==false||layer.QuantizationMode is not (null or "adaptive")||layer.AngleCenterBeta.HasValue;
                if(layer.QuantizationMode=="dis")layer.PreferDisDark=true;
                layer.QuantizationMode="adaptive";layer.AngleColorEnabled=true;layer.AngleCenterBeta=null;layer.RasterSize??=design.RasterSize;
                layer.AllowedIds=null;layer.OutputCompensation=null;layer.DisTone=null;layer.ColorModelVersion=null;
                layer.NeutralStrength=null;layer.MixNeutralFlats=null;layer.PhotoGamma=null;layer.PhotoChroma=null;layer.DitherTolerance=null;layer.FlatThreshold=null;
            }
            else if(layer.FillArgb is null)
            {
                layer.FillArgb=design.Palette.Single(s=>s.Id==layer.FillId).Color(design.MatchGain).ToArgb();changed=true;
            }
        }
        return changed;
    }
    public static void Synchronize(Design design, Layer source)
    {
        if(source.Kind!="image")return;
        var settings=ImageAdjustments.From(design,source);settings.Validate();
        foreach(var layer in design.Layers.Where(l=>l.Kind=="image"))settings.Apply(layer);
        design.ImageDefaults=settings.Copy();design.RasterSize=settings.RasterSize;
    }
}
