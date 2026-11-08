using System.Runtime.InteropServices;
namespace DiscColorStudio;
public sealed record DvdMedium(int Type,int[] Profiles,bool Supported,int State,long FreeSectors,long TotalSectors)
{
    public void RequireBlank(long sectors)
    {
        if(!Supported||Type!=9||!Profiles.Contains(0x11)||(State&2)==0)throw new InvalidOperationException(Ui.Text("请放入新的空白单层 DVD-R。本软件不追加写入，也不擦除光盘。"));
        if(TotalSectors!=sectors||FreeSectors<sectors)throw new InvalidOperationException(Ui.Format("盘片容量与校准不一致：盘片 {0:N0} 扇区，校准 {1:N0} 扇区。",TotalSectors,sectors));
    }
}
public static class BurnMedia
{
    public static Task<DvdMedium> Inspect(string deviceId)=>Sta(()=>
    {
        object? recorderObject=null,formatObject=null;
        try
        {
            recorderObject=Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2",true)!);dynamic recorder=recorderObject!;recorder.InitializeDiscRecorder(deviceId);
            formatObject=Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2Data",true)!);dynamic format=formatObject!;format.Recorder=recorder;format.ClientName="Disc Color Studio";
            return new DvdMedium(Convert.ToInt32(format.CurrentPhysicalMediaType),((Array)recorder.CurrentProfiles).Cast<object>().Select(Convert.ToInt32).ToArray(),Convert.ToBoolean(format.IsCurrentMediaSupported(recorder)),Convert.ToInt32(format.CurrentMediaStatus),Convert.ToInt64(format.FreeSectorsOnMedia),Convert.ToInt64(format.TotalSectorsOnMedia));
        }
        finally{Release(formatObject);Release(recorderObject);}
    });
    public static Task<bool> Eject(string deviceId)=>Sta(()=>
    {
        object? value=null;try{value=Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2",true)!);dynamic recorder=value!;recorder.InitializeDiscRecorder(deviceId);recorder.EjectMedia();return true;}finally{Release(value);}
    });
    private static void Release(object? value){if(value is not null&&Marshal.IsComObject(value))Marshal.FinalReleaseComObject(value);}
    private static Task<T> Sta<T>(Func<T> action)
    {
        var completion=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{completion.SetResult(action());}catch(Exception e){completion.SetException(e);}}){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();return completion.Task;
    }
}
