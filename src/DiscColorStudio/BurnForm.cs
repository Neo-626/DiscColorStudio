using System.Security.Cryptography;
using DiscImageStudio.Burning;
namespace DiscColorStudio;
internal sealed class BurnServices
{
    internal IOpticalDiscBurner Backend {get;init;}=new WindowsImapiBurner();
    internal Func<string,Task<DvdMedium>> Inspect {get;init;}=BurnMedia.Inspect;
    internal Func<string,Task<bool>> Eject {get;init;}=BurnMedia.Eject;
    internal Func<Design,string,IProgress<EncodingProgress>,CancellationToken,Task<EncodedImage>> Generate {get;init;}
        =(d,path,p,token)=>Task.Run(()=>DvdImageEncoder.Generate(d,path,p,token));
}
public sealed class BurnForm:ResponsiveForm
{
    private sealed record DeviceChoice(OpticalBurnDevice Device){public override string ToString()=>Device.DisplayName;}
    private sealed record SpeedChoice(OpticalWriteSpeed Speed){public override string ToString()=>$"{Speed.GetMultiplier(OpticalBurnMediaKind.DvdData):0.#}×";}
    private readonly Design design;private readonly BurnServices services;private readonly IOpticalDiscBurner backend;private readonly bool existingOnly;
    private readonly ComboBox devices=new(){Width=440,DropDownStyle=ComboBoxStyle.DropDownList},speeds=new(){Width=140,DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly TextBox imagePath=new(){Width=475};
    private readonly Label medium=new(){AutoSize=true},status=new(){AutoSize=true};
    private readonly ProgressBar progress=new(){Width=620,Height=24,Maximum=1000};
    private readonly Button refresh=new(){Text=Ui.Text("刷新光驱"),AutoSize=true},browse=new(){Text=Ui.Text("选择…"),AutoSize=true},generate=new(){Text=Ui.Text("生成 ISO"),AutoSize=true},burn=new(){Text=Ui.Text("烧录光盘"),AutoSize=true},cancel=new(){Text=Ui.Text("取消生成"),AutoSize=true,Enabled=false},close=new(){Text=Ui.Text("关闭"),AutoSize=true};
    private readonly Button existing=new(){Text=Ui.Text("使用已有 ISO…"),AutoSize=true};
    private readonly System.Windows.Forms.Timer mediaPoll=new(){Interval=2500};
    private bool busy,writing,loading,checking,mediaReady;private int refreshVersion;private Task? detection;private EncodedImage? encoded;private CancellationTokenSource? cancellation;
    public BurnForm(Design snapshot):this(snapshot,new BurnServices()) { }
    public static BurnForm ForExistingIso(string path)
    {
        long bytes=IsoLength(path);return new BurnForm(new Design{Name=Path.GetFileName(path),Geometry=new Geometry{Sectors=bytes/2048}},new BurnServices(),Path.GetFullPath(path));
    }
    internal BurnForm(Design snapshot,BurnServices services,string? existingIso=null)
    {
        this.services=services;backend=services.Backend;existingOnly=existingIso is not null;
        if(!existingOnly&&!snapshot.HasDisCalibration)throw new InvalidOperationException(Ui.Text("请先导入 DIS 校准后的 DVD 绘图工程（.discproj）。"));
        design=snapshot;Text=Ui.Text("烧录光盘");ClientSize=new Size(680,540);StartPosition=FormStartPosition.CenterParent;MinimumSize=new Size(420,340);
        var panel=new VerticalStack();Controls.Add(LayoutUi.ScrollBody(panel,22));
        panel.Controls.Add(new Label{Text=existingOnly?Ui.Text("烧录已有 DVD 镜像")+"\n"+design.Name:Ui.Format("{0}\n{1} 张图片 · {2:N0} 格 · {3:0.00} GB\n校准：{4}",design.Name,design.Layers.Count(l=>l.Kind=="image"),design.ExportRasterSize,design.Geometry.Sectors*2048/1e9,design.CalibrationName),AutoSize=true,Margin=new Padding(0,0,0,12)});
        void Row(Control input,Control action)=>panel.Controls.Add(LayoutUi.InputRow(input,action));
        Row(devices,refresh);Row(new Label{Text=Ui.Text("刻录速度"),AutoSize=true},speeds);panel.Controls.Add(medium);
        panel.Controls.Add(new Label{Text=Ui.Text("镜像保存位置"),AutoSize=true,Margin=new Padding(0,15,0,0)});Row(imagePath,browse);
        imagePath.Text=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Disc Color Studio",$"disc-{DateTime.Now:yyyyMMdd-HHmmss}.iso");
        panel.Controls.Add(new Label{Text=existingOnly?Ui.Text("已载入现有镜像。选择光驱和速度，点击“烧录光盘”。"):Ui.Text("放入空白 DVD-R，选择光驱和速度。点击“烧录光盘”，程序会生成镜像并完成刻录。"),AutoSize=true,Margin=new Padding(0,10,0,14)});
        panel.Controls.Add(LayoutUi.Buttons(generate,existing));Controls.Add(LayoutUi.Buttons(burn,cancel,close));panel.Controls.Add(progress);panel.Controls.Add(status);CancelButton=close;
        browse.Click+=(_,_)=>{using var dialog=new SaveFileDialog{Filter=Ui.Text("DVD 镜像|*.iso"),FileName=Path.GetFileName(imagePath.Text),OverwritePrompt=false};if(dialog.ShowDialog(this)==DialogResult.OK){imagePath.Text=dialog.FileName;encoded=null;}};
        imagePath.TextChanged+=(_,_)=>encoded=null;
        refresh.Click+=async(_,_)=>await RefreshDevices();devices.SelectedIndexChanged+=async(_,_)=>{if(!loading)await RefreshDevices();};
        generate.Click+=async(_,_)=>await Run(false);burn.Click+=async(_,_)=>await Run(true);cancel.Click+=(_,_)=>cancellation?.Cancel();close.Click+=(_,_)=>Close();
        FormClosing+=(_,e)=>{if(!busy)return;e.Cancel=true;if(!writing)cancellation?.Cancel();status.Text=writing?Ui.Text("正在写盘，请等待光驱收尾。"):Ui.Text("正在取消生成，请稍候…");};
        existing.Click+=async(_,_)=>{using var dialog=new OpenFileDialog{Filter=Ui.Text("DVD 镜像|*.iso"),CheckFileExists=true};if(dialog.ShowDialog(this)==DialogResult.OK)await LoadExisting(dialog.FileName);};
        Shown+=async(_,_)=>{mediaPoll.Start();if(existingIso is not null)await LoadExisting(existingIso);else await RefreshDevices();};
        mediaPoll.Tick+=async(_,_)=>{if(!busy&&!checking&&!mediaReady)await RefreshDevices();};
        FormClosed+=(_,_)=>{mediaPoll.Stop();mediaPoll.Dispose();refreshVersion++;};
        UpdateControls();CompleteLayout();
    }
    private Task RefreshDevices()
    {
        if(busy||IsDisposed)return Task.CompletedTask;
        if(detection is {IsCompleted:false})return detection;
        return detection=DetectDevices();
    }
    private bool Current(int version)=>!IsDisposed&&version==refreshVersion&&!busy;
    private async Task DetectDevices()
    {
        int version=++refreshVersion;checking=true;mediaReady=false;UpdateControls();
        string? selectedId=(devices.SelectedItem as DeviceChoice)?.Device.Id;var selectedSpeed=(speeds.SelectedItem as SpeedChoice)?.Speed;
        try
        {
            var found=await backend.GetDevicesAsync();if(!Current(version))return;
            loading=true;devices.Items.Clear();speeds.Items.Clear();foreach(var d in found)devices.Items.Add(new DeviceChoice(d));
            if(devices.Items.Count==0){medium.Text=Ui.Text("未发现刻录机；连接后自动检测。ISO 可先生成或载入。");return;}
            devices.SelectedIndex=Math.Max(0,found.ToList().FindIndex(d=>d.Id==selectedId));loading=false;
            var choice=(DeviceChoice)devices.SelectedItem!;
            var value=await services.Inspect(choice.Device.Id);if(!Current(version))return;value.RequireBlank(design.Geometry.Sectors);
            var available=await backend.GetSupportedWriteSpeedsAsync(choice.Device.Id,OpticalBurnMediaKind.DvdData);if(!Current(version))return;
            foreach(var speed in available.OrderBy(s=>s.SectorsPerSecond))speeds.Items.Add(new SpeedChoice(speed));
            if(speeds.Items.Count==0)throw new InvalidOperationException(Ui.Text("没有可用的刻录速度。"));
            int preferred=Array.FindIndex(speeds.Items.Cast<SpeedChoice>().ToArray(),s=>s.Speed==selectedSpeed);
            if(preferred<0)preferred=Array.FindIndex(speeds.Items.Cast<SpeedChoice>().ToArray(),s=>Math.Abs(s.Speed.GetMultiplier(OpticalBurnMediaKind.DvdData)-4)<.05);
            speeds.SelectedIndex=Math.Max(0,preferred);mediaReady=true;
            medium.Text=Ui.Format("空白 DVD-R · {0:N0} 扇区 · 可烧录",value.TotalSectors);
        }
        catch(Exception e){if(Current(version))medium.Text=Ui.BurnError(e.Message)+"\n"+Ui.Text("放入空白 DVD-R 后将自动检测。");}
        finally{loading=false;checking=false;if(!IsDisposed)UpdateControls();}
    }
    private async Task RefreshAfterWork()
    {
        if(detection is {IsCompleted:false})await detection;
        if(!IsDisposed)await RefreshDevices();
    }
    private void UpdateControls()
    {
        generate.Enabled=!busy&&!existingOnly;existing.Enabled=!busy;browse.Enabled=!busy&&!existingOnly;imagePath.ReadOnly=existingOnly;imagePath.Enabled=!busy;
        devices.Enabled=refresh.Enabled=!busy&&!checking;speeds.Enabled=!busy&&mediaReady;close.Enabled=!busy;
        burn.Enabled=!busy&&!checking&&mediaReady&&devices.SelectedItem is DeviceChoice&&speeds.SelectedItem is SpeedChoice&&(!existingOnly||encoded is not null);
        cancel.Enabled=busy&&!writing;
    }
    private void Busy(bool value)
    {busy=value;if(value)refreshVersion++;UpdateControls();}
    private static long IsoLength(string path)
    {
        long bytes=new FileInfo(path).Length;
        if(bytes<=0||bytes%32768!=0||bytes>2298496L*2048)throw new InvalidDataException(Ui.Text("请选择完整的单层 DVD-R 镜像。"));
        return bytes;
    }
    private async Task<EncodedImage> ReadExisting(string path,CancellationToken token)
    {
        path=Path.GetFullPath(path);long bytes=IsoLength(path);
        if(existingOnly)design.Geometry.Sectors=bytes/2048;
        else if(bytes!=design.Geometry.Sectors*2048)throw new InvalidDataException(Ui.Text("ISO 容量与当前工程不一致。"));
        status.Text=Ui.Text("正在读取已有 ISO…");progress.Style=ProgressBarStyle.Marquee;
        try
        {using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,1<<20,FileOptions.Asynchronous|FileOptions.SequentialScan);
         string hash=Convert.ToHexString(await SHA256.HashDataAsync(input,token));return new(path,bytes,hash);}
        finally{progress.Style=ProgressBarStyle.Continuous;}
    }
    private async Task LoadExisting(string path)
    {
        if(busy)return;using var cts=new CancellationTokenSource();cancellation=cts;Busy(true);encoded=null;mediaReady=false;
        try{imagePath.Text=Path.GetFullPath(path);encoded=await ReadExisting(path,cts.Token);status.Text=Ui.Text("镜像已就绪：")+encoded.Path;progress.Value=1000;}
        catch(OperationCanceledException){status.Text=Ui.Text("已取消读取。");}
        catch(Exception e){status.Text=Ui.BurnError(e.Message);}
        finally{cancellation=null;Busy(false);await RefreshAfterWork();}
    }
    private async Task Run(bool write)
    {
        if(busy)return;
        var device=devices.SelectedItem as DeviceChoice;var speed=speeds.SelectedItem as SpeedChoice;
        if(write&&(device is null||speed is null)){status.Text=Ui.Text("请选择空白 DVD-R 和刻录速度。");return;}
        using var cts=new CancellationTokenSource();cancellation=cts;Busy(true);progress.Value=0;
        bool attempted=false;long written=0;string stage="";
        try
        {
            if(write)(await services.Inspect(device!.Device.Id)).RequireBlank(design.Geometry.Sectors);
            if(encoded is null)
            {
                string target=imagePath.Text.Trim();var updates=new Progress<EncodingProgress>(p=>{status.Text=p.Message;progress.Value=(int)Math.Clamp(p.Fraction*1000,0,1000);});
                encoded=File.Exists(target)?await ReadExisting(target,cts.Token):await services.Generate(design,target,updates,cts.Token);
            }
            status.Text=Ui.Text("镜像已就绪：")+encoded.Path;progress.Value=1000;
            if(!write)return;
            (await services.Inspect(device!.Device.Id)).RequireBlank(design.Geometry.Sectors);
            var currentSpeeds=await backend.GetSupportedWriteSpeedsAsync(device.Device.Id,OpticalBurnMediaKind.DvdData);
            if(!currentSpeeds.Contains(speed!.Speed))throw new InvalidOperationException(Ui.Text("盘片或支持速度已改变，请刷新光驱后重试。"));
            using var input=new FileStream(encoded.Path,FileMode.Open,FileAccess.Read,FileShare.Read,1<<20);
            string hash=await Task.Run(()=>Convert.ToHexString(SHA256.HashData(input)));
            if(input.Length!=encoded.Bytes||hash!=encoded.Sha256)throw new InvalidDataException(Ui.Text("镜像已改变，请重新生成。"));input.Position=0;
            cts.Token.ThrowIfCancellationRequested();
            writing=true;cancel.Enabled=false;attempted=true;progress.Value=0;
            var physical=new Progress<OpticalBurnProgress>(p=>{if(p.Stage is "write" or "finalize" or "complete"){written=p.CompletedBytes;stage=p.Stage;progress.Value=(int)Math.Clamp(p.Fraction*1000,0,1000);}status.Text=Ui.IsEnglish?p.Stage switch{"write"=>Ui.Format("正在写入光盘 {0:P1}",p.Fraction),"finalize"=>Ui.Text("正在关闭光盘会话…"),"complete"=>Ui.Text("写盘完成"),_=>Ui.Text("正在准备光驱…")}:p.Message;});
            await backend.BurnAsync(new OpticalBurnRequest(device.Device.Id,OpticalBurnMediaKind.DvdData,input.Length,(output,token)=>
            {byte[] buffer=new byte[1<<20];int n;while((n=input.Read(buffer))>0){token.ThrowIfCancellationRequested();output.Write(buffer,0,n);}},speed.Speed),physical);
            progress.Value=1000;status.Text=Ui.Text("刻录完成，光驱已成功关闭会话。");
            try{await services.Eject(device.Device.Id);status.Text+=Ui.Text(" 光盘已弹出。");}catch{status.Text+=Ui.Text(" 请手动弹出光盘。");}
        }
        catch(OperationCanceledException){status.Text=Ui.Text("已取消镜像生成。");}
        catch(Exception error)
        {
            status.Text=attempted&&written==design.Geometry.Sectors*2048&&stage=="finalize"?Ui.Text("刻录收尾失败：")+Ui.BurnError(error.Message):((attempted?Ui.Text("刻录失败："):Ui.Text("准备失败："))+Ui.BurnError(error.Message));
            if(encoded is not null)status.Text+=Ui.Text("\n镜像位置：")+encoded.Path;
        }
        finally{writing=false;cancellation=null;mediaReady=false;Busy(false);await RefreshAfterWork();}
    }
}
