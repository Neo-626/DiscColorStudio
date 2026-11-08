using System.Text.Json;
namespace DiscColorStudio;
public static class CalibrationFiles
{
    public const string DisProjectSource = "dis-discproj-v1";
    public static Geometry ReadDisProject(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".discproj", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(Ui.Text("请选择 DIS 保存的 DVD 绘图工程（.discproj）。"));
        if (new FileInfo(path).Length > 8_000_000) throw new InvalidDataException(Ui.Text("DIS 工程文件过大。"));
        string json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version is < 1 or > 2
            || !root.TryGetProperty("discFamily", out var family) || family.ValueKind != JsonValueKind.String
            || !string.Equals(family.GetString(), "dvd", StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(Ui.Text("需要 DIS 保存的 DVD 绘图工程（.discproj，版本 1 或 2）。请将最终标定参数应用到 DVD 工程后保存。"));
        return Parse(json);
    }
    public static void ApplyImport(Design design, Geometry geometry, string name)
    {
        string record = Serialize(geometry);
        design.Geometry = Parse(record);
        design.GeometryRecordJson = record;
        design.CalibrationName = name;
        design.CalibrationSource = DisProjectSource;
        design.CalibrationConfirmed = true;
    }
    public static bool IsImported(Design design)
    {
        if (!design.CalibrationConfirmed || design.CalibrationSource != DisProjectSource) return false;
        try
        {
            var saved = Parse(design.GeometryRecordJson);
            design.Geometry.Validate();
            return saved.InnerRadiusMm == design.Geometry.InnerRadiusMm && saved.OuterRadiusMm == design.Geometry.OuterRadiusMm
                && saved.Sectors == design.Geometry.Sectors && saved.PitchLinear == design.Geometry.PitchLinear
                && saved.PitchQuadratic == design.Geometry.PitchQuadratic && saved.PitchCubic == design.Geometry.PitchCubic;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or InvalidOperationException or FormatException or OverflowException)
        { return false; }
    }
    public static Geometry Read(string path)
    {
        if(new FileInfo(path).Length>8_000_000)throw new InvalidDataException(Ui.Text("校准文件过大。"));
        return Parse(File.ReadAllText(path));
    }
    public static Geometry Parse(string json)
    {
        using var document=JsonDocument.Parse(json);var root=document.RootElement;
        if(root.ValueKind!=JsonValueKind.Object)throw new InvalidDataException(Ui.Text("校准参数必须为 JSON 对象。"));
        if(root.TryGetProperty("discFamily",out var family)&&!string.Equals(family.GetString(),"dvd",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(Ui.Text("请选择 DVD 校准工程；当前仅支持单层 DVD-R。"));
        var element=root.TryGetProperty("geometry",out var geometry)?geometry:root;
        if(element.ValueKind!=JsonValueKind.Object)throw new InvalidDataException(Ui.Text("几何参数格式无效。"));
        foreach(string field in new[]{"innerRadiusMm","outerRadiusMm","sectors","pitchLinear","pitchQuadratic","pitchCubic"})
            if(!element.TryGetProperty(field,out var value)||value.ValueKind!=JsonValueKind.Number)throw new InvalidDataException(Ui.Text("缺少最终几何参数：")+field+Ui.Text("。请在 DIS 中保存校准后的绘图工程。"));
        var result=element.Deserialize<Geometry>(Design.Json)??throw new InvalidDataException(Ui.Text("几何参数为空。"));result.Validate();return result;
    }
    public static string Serialize(Geometry geometry)
    {geometry.Validate();return JsonSerializer.Serialize(new{format="disc-color-calibration-v1",discFamily="dvd",geometry},Design.Json);}
}
