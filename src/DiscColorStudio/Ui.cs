using System.Globalization;
using System.Text.Json;

namespace DiscColorStudio;

public static class Ui
{
    private static readonly Dictionary<string,string> English = LoadStrings();
    private static readonly Dictionary<string,string> Sources = English.GroupBy(pair=>pair.Value).ToDictionary(group=>group.Key,group=>group.First().Key);
    public static string Language { get; private set; } = "en";
    public static bool IsEnglish => Language == "en";
    public static string HelpFileName => IsEnglish ? "UserGuide.html" : "使用说明.html";
    public static string PreferencePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DiscColorStudio","language.json");
    public static string Text(string source) => IsEnglish && English.TryGetValue(source,out var translated) ? translated : source;
    public static string Format(string source,params object?[] arguments) => string.Format(CultureInfo.GetCultureInfo(IsEnglish?"en-US":"zh-CN"),Text(source),arguments);
    public static string BurnError(string message)
    {
        if(!IsEnglish)return message;
        message=Text(message);
        // Translate upstream diagnostic fragments while preserving drive error
        // codes and the native Windows exception details.
        foreach(var pair in English.Where(pair=>pair.Key.StartsWith("burn|")).OrderByDescending(pair=>pair.Key.Length))
            message=message.Replace(pair.Key[5..],pair.Value,StringComparison.Ordinal);
        return message;
    }
    internal static string? SourceKey(string? text) => text is null?null:English.ContainsKey(text)?text:Sources.GetValueOrDefault(text);
    private static Dictionary<string,string> LoadStrings()
    {
        using var stream=typeof(Ui).Assembly.GetManifestResourceStream("DiscColorStudio.Strings.en.json") ?? throw new InvalidOperationException("Missing English language resource.");
        return JsonSerializer.Deserialize<Dictionary<string,string>>(stream) ?? throw new InvalidOperationException("Invalid English language resource.");
    }
    public static void SetLanguage(string language)
    {
        if(language is not ("en" or "zh-CN"))throw new ArgumentException("Unsupported interface language.",nameof(language));
        Language=language;
        var culture=CultureInfo.GetCultureInfo(IsEnglish?"en-US":"zh-CN");
        CultureInfo.DefaultThreadCurrentCulture=CultureInfo.DefaultThreadCurrentUICulture=culture;
        CultureInfo.CurrentCulture=CultureInfo.CurrentUICulture=culture;
    }
    public static string LoadPreference(string? path=null)
    {
        try
        {
            path??=PreferencePath;
            if(!File.Exists(path)||new FileInfo(path).Length>4096)return "en";
            using var data=JsonDocument.Parse(File.ReadAllText(path));
            return data.RootElement.TryGetProperty("language",out var value)&&value.GetString()=="zh-CN"?"zh-CN":"en";
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException){return "en";}
    }
    public static bool SavePreference(string language,string? path=null)
    {
        if(language is not ("en" or "zh-CN"))throw new ArgumentException("Unsupported interface language.",nameof(language));
        path??=PreferencePath;string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(temporary,JsonSerializer.Serialize(new{language}));File.Move(temporary,path,true);return true;
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){return false;}
        finally {try{if(File.Exists(temporary))File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}}
    }
}

internal interface ILocalizedControl { void RefreshLanguage(); }

// Capture static captions before user data populates the inspector. Text boxes,
// layer names, project names and progress messages are deliberately not bound.
internal sealed class UiBindings
{
    private readonly List<Action> updates=[];
    internal UiBindings(Control root){Capture(root);}
    private void Capture(Control control)
    {
        if(control is ILocalizedControl localized)updates.Add(localized.RefreshLanguage);
        if(control is Label or ButtonBase)
        {
            if(Ui.SourceKey(control.Text) is {} key)updates.Add(()=>control.Text=Ui.Text(key));
        }
        if(control is ToolStrip strip)foreach(ToolStripItem item in strip.Items)CaptureItem(item);
        foreach(Control child in control.Controls)Capture(child);
    }
    private void CaptureItem(ToolStripItem item)
    {
        if(Ui.SourceKey(item.Text) is {} key)updates.Add(()=>item.Text=Ui.Text(key));
        if(item is ToolStripDropDownItem menu)foreach(ToolStripItem child in menu.DropDownItems)CaptureItem(child);
    }
    internal void Apply(){foreach(var update in updates)update();}
}
