using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DiscColorStudio;
public static class Store
{
    public static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    public static void Save(string path,Design design)
    {
        design=design.ForStorage();design.Validate();string temporary=path+".tmp-"+Guid.NewGuid().ToString("N");
        try
        {
            using(var zip=ZipFile.Open(temporary,ZipArchiveMode.Create))
            {
                Write(zip,"project.json",Encoding.UTF8.GetBytes(JsonSerializer.Serialize(design,Design.Json)));
                foreach(string id in design.UsedAssetIds)Write(zip,"assets/"+id+".png",design.Assets[id]);
                if(design.AngleColor is not null)foreach(var pair in design.AngleProfileAssets)Write(zip,pair.Key,pair.Value);
            }
            File.Move(temporary,path,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public static Design Load(string path)
    {
        using var zip=ZipFile.OpenRead(path);
        byte[] Read(string name,long limit)
        {
            var entry=zip.GetEntry(name)??throw new InvalidDataException(Ui.Text("工程缺少：")+name);
            if(entry.Length>limit)throw new InvalidDataException(Ui.Text("工程资源过大。"));
            using var input=entry.Open();using var output=new MemoryStream();byte[] buffer=new byte[65536];int n;
            while((n=input.Read(buffer))>0){if(output.Length+n>limit)throw new InvalidDataException(Ui.Text("工程资源过大。"));output.Write(buffer,0,n);}return output.ToArray();
        }
        var d=JsonSerializer.Deserialize<Design>(Read("project.json",8_000_000),Design.Json)??throw new InvalidDataException(Ui.Text("空工程。"));
        // Older project containers are accepted, without loading their archived review assets.
        if(d.Format.StartsWith("disc-visibility-layout-v",StringComparison.Ordinal))d.Format="disc-color-design-v4";
        long total=0;
        foreach(string id in d.UsedAssetIds)
        {
            if(!Guid.TryParseExact(id,"N",out _))throw new InvalidDataException(Ui.Text("素材编号无效。"));
            byte[] bytes=Read("assets/"+id+".png",100_000_000);total+=bytes.Length;
            if(total>400_000_000)throw new InvalidDataException(Ui.Text("工程素材总量过大。"));d.Assets[id]=bytes;
        }
        if(d.AngleColor is not null)foreach(string name in new[]{AngleColorSettings.ProfileName,AngleColorSettings.LutName})d.AngleProfileAssets[name]=Read(name,128_000_000);
        // Legacy confirmation flags alone do not establish a DIS project import.
        d.CalibrationConfirmed=d.HasDisCalibration;
        d.Validate();return d;
    }
    internal static void Write(ZipArchive zip,string name,byte[] bytes)
    {using var output=zip.CreateEntry(name,CompressionLevel.Optimal).Open();output.Write(bytes);}
    public static byte[] Png(Bitmap bitmap)
    {using var stream=new MemoryStream();bitmap.Save(stream,System.Drawing.Imaging.ImageFormat.Png);return stream.ToArray();}
}
