using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscColorStudio;

public sealed class Geometry
{
    public double InnerRadiusMm { get; set; }
    public double OuterRadiusMm { get; set; }
    public long Sectors { get; set; }
    public double PitchLinear { get; set; }
    public double PitchQuadratic { get; set; }
    public double PitchCubic { get; set; }
    public void Validate()
    {
        if (!(InnerRadiusMm > 0 && InnerRadiusMm < OuterRadiusMm && OuterRadiusMm <= 60)
            || Sectors <= 0 || Sectors > 2298496 || Sectors % 16 != 0) throw new InvalidDataException(Ui.Text("单层 DVD-R 几何参数或扇区数无效。"));
        double[] c = [PitchLinear, PitchQuadratic, PitchCubic];
        if (c.Any(v => !double.IsFinite(v))) throw new InvalidDataException(Ui.Text("轨距系数无效。"));
        var xs = new List<double> { 0, 1 };
        if (PitchCubic != 0)
        {
            double d = 4 * PitchQuadratic * PitchQuadratic - 12 * PitchCubic * PitchLinear;
            if (d >= 0) { xs.Add((-2 * PitchQuadratic + Math.Sqrt(d)) / (6 * PitchCubic)); xs.Add((-2 * PitchQuadratic - Math.Sqrt(d)) / (6 * PitchCubic)); }
        }
        else if (PitchQuadratic != 0) xs.Add(-PitchLinear / (2 * PitchQuadratic));
        if (xs.Where(x => x is >= 0 and <= 1).Any(x => 1 + x * (PitchLinear + x * (PitchQuadratic + x * PitchCubic)) <= 0))
            throw new InvalidDataException(Ui.Text("轨距曲线在盘面范围内必须大于零。"));
    }
}

public sealed class Swatch
{
    public int Id { get; set; }
    public string Motif { get; set; } = "";
    public string Name { get; set; } = "";
    public double[] CameraRgb { get; set; } = [];
    public Color Color(double gain = 1) => System.Drawing.Color.FromArgb(
        (int)Math.Clamp(Math.Round(CameraRgb[0] * gain), 0, 255),
        (int)Math.Clamp(Math.Round(CameraRgb[1] * gain), 0, 255),
        (int)Math.Clamp(Math.Round(CameraRgb[2] * gain), 0, 255));
}

public sealed class Layer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = Ui.Text("图层");
    public string Kind { get; set; } = "image";
    public bool Visible { get; set; } = true;
    public string? AssetId { get; set; }
    public string Text { get; set; } = Ui.Text("彩色光盘");
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double X { get; set; }
    public double Y { get; set; } = -40;
    public double Width { get; set; } = 12;
    public double Height { get; set; } = 12;
    public double Rotation { get; set; }
    public double Opacity { get; set; } = 1;
    public int FillId { get; set; } = 4;
    public int? FillArgb { get; set; }
    public bool KeepAspect { get; set; } = true;
    public double Brightness { get; set; }
    public double Contrast { get; set; } = 1;
    public double InputExposure { get; set; } = 1;
    public double? PhotoGamma { get; set; }
    public double? PhotoChroma { get; set; }
    public double? DitherTolerance { get; set; }
    public double? FlatThreshold { get; set; }
    public string? QuantizationMode { get; set; }
    public int? RasterSize { get; set; }
    public List<int>? AllowedIds { get; set; }
    public ColorMixer? ColorMixer { get; set; }
    public bool BypassColorMixer { get; set; }
    public bool? AngleColorEnabled { get; set; }
    public double? AngleCenterBeta { get; set; }
    public bool PreferDisDark { get; set; }
    public double BandingReduction { get; set; }=1;
    public double AdaptiveMixing { get; set; }=1;
    public int? ColorModelVersion { get; set; }
    public double? NeutralStrength { get; set; }
    public bool? MixNeutralFlats { get; set; }
    public OutputCompensation? OutputCompensation { get; set; }
    public DiscImageStudio.Core.ToneProfile? DisTone { get; set; }
    public Layer Copy()
    {
        var copy = (Layer)MemberwiseClone();
        copy.AllowedIds = AllowedIds?.ToList();
        copy.ColorMixer = ColorMixer?.Copy();
        copy.OutputCompensation = OutputCompensation is null ? null : OutputCompensation with {};
        return copy;
    }
    public override string ToString() => Name;
}

public sealed class Design
{
    public AngleColorSettings? AngleColor { get; set; }
    [JsonIgnore] public Dictionary<string,byte[]> AngleProfileAssets { get; set; } = [];
    [JsonIgnore] public bool HasAngleColor => Layers.Any(UsesAngleColor);
    public bool UsesAngleColor(Layer layer) => AngleColor?.Enabled==true && (layer.Kind=="image" || layer.FillArgb.HasValue) && layer.Visible && layer.Opacity>0 && layer.AngleColorEnabled!=false && layer.QuantizationMode!="dis";
    public string Format { get; set; } = "disc-color-design-v1";
    // Missing in old files => legacy reproduction. The UI explicitly upgrades a copy.
    public int ColorModelVersion { get; set; } = 1;
    public double NeutralStrength { get; set; } = .65;
    public bool MixNeutralFlats { get; set; } = true;
    [JsonIgnore] public bool HasGrayLayers => Layers.Any(l => ImageMode(l)=="dis");
    [JsonIgnore] public bool RequiresColorV3 => Layers.Any(l=>l.OutputCompensation is not null);
    [JsonIgnore] public bool RequiresColorV2 => ColorModelVersion==2 || Layers.Any(l=>l.ColorModelVersion==2 || l.ColorMixer is not null);
    [JsonIgnore] public IEnumerable<string> UsedAssetIds => Layers.Where(l => l.Kind == "image")
        .Select(l => l.AssetId).Where(id => id is not null).Select(id => id!).Distinct();
    public string Name { get; set; } = Ui.Text("未命名设计");
    public string CalibrationName { get; set; } = Ui.Text("未导入校准");
    public bool CalibrationConfirmed { get; set; }
    public string CalibrationSource { get; set; } = "";
    [JsonIgnore] public bool HasDisCalibration => CalibrationFiles.IsImported(this);
    public Geometry Geometry { get; set; } = new();
    public string GeometryRecordJson { get; set; } = "";
    public string PaletteRecordJson { get; set; } = "";
    public List<Swatch> Palette { get; set; } = [];
    public List<int> AllowedIds { get; set; } = [4, 13, 7, 2, 14];
    public int BackgroundId { get; set; } = 14;
    public bool RegistrationMarks { get; set; }
    public ImageAdjustments? ImageDefaults { get; set; }
    public double MatchGain { get; set; } = 2;
    public string QuantizationMode { get; set; } = "nearest";
    public double PhotoGamma { get; set; } = 1;
    public double PhotoChroma { get; set; } = .7;
    public double DitherTolerance { get; set; } = 2.5;
    public double FlatThreshold { get; set; } = 3;
    public int RasterSize { get; set; } = 4096;
    public List<Layer> Layers { get; set; } = [];
    [JsonIgnore] public Dictionary<string, byte[]> Assets { get; set; } = [];
    [JsonIgnore] public bool HasImageOverrides => Layers.Any(l => l.Visible && l.Opacity > 0 && l.Kind == "image"
        && (ImageMode(l) != QuantizationMode || ImageRasterSize(l) != RasterSize || l.AllowedIds is not null || l.ColorModelVersion.HasValue || l.OutputCompensation is not null));
    [JsonIgnore] public bool HasAdaptiveLayers => RasterGrid.UsesAdaptiveMapping(QuantizationMode)
        || Layers.Any(l => l.Visible && l.Opacity > 0 && l.Kind == "image" && RasterGrid.UsesAdaptiveMapping(l.QuantizationMode));
    [JsonIgnore] public int ExportRasterSize => Layers.Where(l => l.Visible && l.Opacity > 0 && l.Kind == "image")
        .Select(ImageRasterSize).Append(RasterSize).Max();

    public string ImageMode(Layer layer) => layer.Kind == "image" ? layer.QuantizationMode ?? QuantizationMode : QuantizationMode;
    public IReadOnlyList<int> ImageColors(Layer layer) => layer.AllowedIds ?? AllowedIds;
    internal Design WithImageColors(Layer layer)
    {
        if (layer.AllowedIds is null && !layer.ColorModelVersion.HasValue && layer.OutputCompensation?.Active!=true) return this;
        var copy = (Design)MemberwiseClone();
        copy.AllowedIds = layer.AllowedIds ?? AllowedIds;
        copy.ColorModelVersion = layer.OutputCompensation?.Active==true ? 2 : layer.ColorModelVersion ?? ColorModelVersion;
        return copy;
    }

    public int ImageRasterSize(Layer layer)
    {
        int size = layer.Kind == "image" ? layer.RasterSize ?? RasterSize : RasterSize;
        return UsesAngleColor(layer) || RasterGrid.SupportsTiles(ImageMode(layer)) ? size : Math.Min(size, RasterGrid.MaxLegacySize);
    }

    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true, WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static Design Create(int rasterSize = 4096)
    {
        string palette = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"data","palette.json"));
        using var p = JsonDocument.Parse(palette);
        var names = new Dictionary<int,string> { [4]=Ui.Text("橙红"),[13]=Ui.Text("青绿"),[7]=Ui.Text("黄褐"),[2]=Ui.Text("紫红"),[14]=Ui.Text("底色") };
        var result = new Design { Geometry = new Geometry { InnerRadiusMm=24,OuterRadiusMm=58,Sectors=2297888 },
            PaletteRecordJson=palette,RasterSize=rasterSize };
        foreach (var s in p.RootElement.GetProperty("motifs").EnumerateArray())
        {
            int id = s.GetProperty("id").GetInt32();
            result.Palette.Add(new Swatch { Id = id, Name = names.GetValueOrDefault(id, Ui.Text("纹理 ") + id),
                Motif = s.GetProperty("scrambledPayloadMotif").GetString()!, CameraRgb = s.GetProperty("jpegRgb").Deserialize<double[]>()! });
        }
        result.MatchGain = 255.0 / result.Palette.Max(s => s.CameraRgb.Max()); result.Validate(); return result;
    }
    public Design Clone()
    {
        var copy = (Design)MemberwiseClone();
        copy.ImageDefaults=ImageDefaults?.Copy();
        copy.AngleColor=AngleColor is null?null:AngleColor with{};
        copy.AngleProfileAssets=new Dictionary<string,byte[]>(AngleProfileAssets);
        copy.Geometry = new Geometry { InnerRadiusMm = Geometry.InnerRadiusMm, OuterRadiusMm = Geometry.OuterRadiusMm,
            Sectors = Geometry.Sectors, PitchLinear = Geometry.PitchLinear, PitchQuadratic = Geometry.PitchQuadratic, PitchCubic = Geometry.PitchCubic };
        copy.Palette = Palette.Select(s => new Swatch { Id = s.Id, Motif = s.Motif, Name = s.Name, CameraRgb = (double[])s.CameraRgb.Clone() }).ToList();
        copy.AllowedIds = AllowedIds.ToList();
        copy.Layers = Layers.Select(l => l.Copy()).ToList();
        copy.Assets = new Dictionary<string, byte[]>(Assets); return copy;
    }
    internal Design ForStorage()
    {
        if(AngleColor is not null){var v4=Clone();v4.Format="disc-color-design-v4";return v4;}
        if(RequiresColorV3){var v3=Clone();v3.Format="disc-color-design-v3";return v3;}
        if(!RequiresColorV2 || Format.EndsWith("-v2",StringComparison.Ordinal))return this;
        var copy=Clone();copy.Format="disc-color-design-v2";return copy;
    }
    public void Validate()
    {
        if (Format is not ("disc-color-design-v1" or "disc-color-design-v2" or "disc-color-design-v3" or "disc-color-design-v4")) throw new InvalidDataException(Ui.Text("不是受支持的彩色盘面工程。"));
        AngleColor?.Validate(AngleProfileAssets);
        Geometry.Validate();
        ImageDefaults?.Validate();
        if(ColorModelVersion is <1 or >2 || !double.IsFinite(NeutralStrength) || NeutralStrength is <0 or >1)
            throw new InvalidDataException(Ui.Text("颜色模型版本或中性色补偿参数无效。"));
        if((RequiresColorV2||RequiresColorV3) && Palette.Count>32)throw new InvalidDataException(Ui.Text("中性色模型最多支持 32 种实测纹理。"));
        if (QuantizationMode is not ("nearest" or "dither" or "photo" or "adaptive" or "detail") || !double.IsFinite(PhotoGamma) || PhotoGamma is < .4 or > 2.5
            || !double.IsFinite(PhotoChroma) || PhotoChroma is < 0 or > 1) throw new InvalidDataException(Ui.Text("限色或明暗参数无效。"));
        if(!double.IsFinite(DitherTolerance) || DitherTolerance is < 0 or > 20 || !double.IsFinite(FlatThreshold) || FlatThreshold is < 0 or > 64)
            throw new InvalidDataException(Ui.Text("网点阈值无效。"));
        if (!RasterGrid.Sizes.Contains(RasterSize)) throw new InvalidDataException(Ui.Text("设计采样精度无效。"));
        if (RasterGrid.IsTiled(RasterSize) && !RasterGrid.SupportsTiles(QuantizationMode))
            throw new InvalidDataException(Ui.Text("旧版误差扩散最高支持 8192 格。请选择直接限色或局部互色进行高精导出。"));
        if (Palette.Count is < 2 or > 256 || Palette.Select(s => s.Id).Distinct().Count() != Palette.Count
            || Palette.Any(s => s.Id is < 1 or > 254 || s.CameraRgb.Length != 3 || s.CameraRgb.Any(v => !double.IsFinite(v) || v is < 0 or > 255)
                || s.Motif.Length is < 2 or > 64 || s.Motif.Length % 2 != 0 || !s.Motif.All(Uri.IsHexDigit)))
            throw new InvalidDataException(Ui.Text("工程色表无效。"));
        if (AllowedIds.Count < 2 || AllowedIds.Distinct().Count() != AllowedIds.Count || AllowedIds.Any(i => Palette.All(s => s.Id != i))
            || !AllowedIds.Contains(BackgroundId) || !double.IsFinite(MatchGain) || MatchGain is < .1 or > 8)
            throw new InvalidDataException(Ui.Text("可用颜色或底色无效。"));
        if (Layers.Count > 200 || Layers.Select(l => l.Id).Distinct().Count() != Layers.Count) throw new InvalidDataException(Ui.Text("图层数量或编号无效。"));
        foreach (var l in Layers)
        {
            if(!double.IsFinite(l.BandingReduction)||l.BandingReduction is <0 or >1||!double.IsFinite(l.AdaptiveMixing)||l.AdaptiveMixing is <0 or >1)throw new InvalidDataException(Ui.Text("单图混色强度无效。"));
            if(l.AngleCenterBeta is double beta && (!double.IsFinite(beta)||beta<(AngleColor?.WindowStartDeg??0)||beta>(AngleColor?.WindowEndDeg??360)))throw new InvalidDataException(Ui.Text("单图观看角度无效。"));
            l.ColorMixer?.Validate();
            l.OutputCompensation?.Validate();
            if(l.OutputCompensation?.Active==true && (l.Kind!="image" || ImageMode(l)=="dis"))throw new InvalidDataException(Ui.Text("全图补偿仅适用于彩色图片。"));
            if(l.ColorModelVersion is not (null or 1 or 2))throw new InvalidDataException(Ui.Text("单图颜色模型版本无效。"));
            if(l.NeutralStrength is double strength && (!double.IsFinite(strength) || strength is <0 or >1))
                throw new InvalidDataException(Ui.Text("单图中性色补偿参数无效。"));
            if (l.AllowedIds is { } ids && (l.Kind != "image" || ids.Count < 1 || ids.Distinct().Count() != ids.Count
                || ids.Any(id => Palette.All(s => s.Id != id)))) throw new InvalidDataException(Ui.Text("单图纹理选择无效：") + l.Name);
            if (l.Kind is not ("image" or "text" or "rectangle" or "ellipse")
                || new[] { l.X, l.Y, l.Width, l.Height, l.Rotation, l.Opacity, l.Brightness, l.Contrast, l.InputExposure }.Any(v => !double.IsFinite(v))
                || l.Width is < RasterGrid.MinLayerSizeMm or > 240 || l.Height is < RasterGrid.MinLayerSizeMm or > 240
                || Math.Abs(l.X) > 180 || Math.Abs(l.Y) > 180
                || l.Opacity is < 0 or > 1 || l.Contrast is < .1 or > 4 || l.InputExposure is < .1 or > 8 || l.Brightness is < -128 or > 128
                || !AllowedIds.Contains(l.FillId) && l.Kind != "image") throw new InvalidDataException(Ui.Text("图层参数无效：") + l.Name);
            if (l.Kind == "image" && (l.AssetId is null || !Assets.ContainsKey(l.AssetId))) throw new InvalidDataException(Ui.Text("缺少图片素材：") + l.Name);
            if ((l.PhotoGamma is double gamma && (!double.IsFinite(gamma) || gamma is < .4 or > 2.5))
                || (l.PhotoChroma is double chroma && (!double.IsFinite(chroma) || chroma is < 0 or > 1)))
                throw new InvalidDataException(Ui.Text("单图明暗或色彩参数无效：") + l.Name);
            if((l.DitherTolerance is double tolerance && (!double.IsFinite(tolerance) || tolerance is < 0 or > 20))
                || (l.FlatThreshold is double flat && (!double.IsFinite(flat) || flat is < 0 or > 64))) throw new InvalidDataException(Ui.Text("单图网点阈值无效：")+l.Name);
            l.DisTone?.Validate();
            if (l.QuantizationMode == "dis" && l.DisTone is null) throw new InvalidDataException(Ui.Text("DIS 灰度参数缺失。"));
            if (l.QuantizationMode is not (null or "nearest" or "dither" or "photo" or "adaptive" or "detail" or "dis")
                || l.RasterSize.HasValue && !RasterGrid.Sizes.Contains(l.RasterSize.Value)
                || l.Kind != "image" && (l.QuantizationMode is not null || l.RasterSize.HasValue))
                throw new InvalidDataException(Ui.Text("单图限色方式或采样精度无效：") + l.Name);
        }
    }
}
