using System.Security.Cryptography;
using DiscImageStudio.Burning;
namespace DiscColorStudio;
public sealed class BurnForm:ResponsiveForm
{
    private sealed record DeviceChoice(OpticalBurnDevice Device){public override string ToString()=>Device.DisplayName;}
    private sealed record SpeedChoice(OpticalWriteSpeed Speed){public override string ToString()=>$"{Speed.GetMultiplier(OpticalBurnMediaKind.DvdData):0.#}×";}
    private readonly Design design;private readonly WindowsImapiBurner backend=new();
    private readonly ComboBox devices=new(){Width=440,DropDownStyle=ComboBoxStyle.DropDownList},speeds=new(){Width=140,DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly TextBox imagePath=new(){Width=475};
    private readonly Label medium=new(){AutoSize=true},status=new(){AutoSize=true};
    private readonly ProgressBar progress=new(){Width=620,Height=24,Maximum=1000};
    private readonly Button refresh=new(){Text=Ui.Text("刷新光驱"),AutoSize=true},browse=new(){Text=Ui.Text("选择…"),AutoSize=true},generate=new(){Text=Ui.Text("生成 ISO"),AutoSize=true},burn=new(){Text=Ui.Text("烧录光盘"),AutoSize=true},cancel=new(){Text=Ui.Text("取消生成"),AutoSize=true,Enabled=false},close=new(){Text=Ui.Text("关闭"),AutoSize=true};
    private bool busy,writing,loading;private int refreshVersion;private EncodedImage? encoded;private CancellationTokenSource? cancellation;
    public BurnForm(Design snapshot)
    {
        if(!snapshot.HasDisCalibration)throw new InvalidOperationException(Ui.Text("请先导入 DIS 校准后的 DVD 绘图工程（.discproj）。"));
        design=snapshot;Text=Ui.Text("烧录光盘");ClientSize=new Size(680,540);StartPosition=FormStartPosition.CenterParent;MinimumSize=new Size(420,340);
        var panel=new VerticalStack();Controls.Add(LayoutUi.ScrollBody(panel,22));
        panel.Controls.Add(new Label{Text=Ui.Format("{0}\n{1} 张图片 · {2:N0} 格 · {3:0.00} GB\n校准：{4}",design.Name,design.Layers.Count(l=>l.Kind=="image"),design.ExportRasterSize,design.Geometry.Sectors*2048/1e9,design.CalibrationName),AutoSize=true,Margin=new Padding(0,0,0,12)});
        void Row(Control input,Control action)=>panel.Controls.Add(LayoutUi.InputRow(input,action));
        Row(devices,refresh);Row(new Label{Text=Ui.Text("刻录速度"),AutoSize=true},speeds);panel.Controls.Add(medium);
        panel.Controls.Add(new Label{Text=Ui.Text("镜像保存位置"),AutoSize=true,Margin=new Padding(0,15,0,0)});Row(imagePath,browse);
        imagePath.Text=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Disc Color Studio",$"disc-{DateTime.Now:yyyyMMdd-HHmmss}.iso");
        panel.Controls.Add(new Label{Text=Ui.Text("放入空白 DVD-R，选择光驱和速度。点击“烧录光盘”，程序会生成镜像并完成刻录。"),AutoSize=true,Margin=new Padding(0,10,0,14)});
        panel.Controls.Add(generate);Controls.Add(LayoutUi.Buttons(burn,cancel,close));panel.Controls.Add(progress);panel.Controls.Add(status);CancelButton=close;
        browse.Click+=(_,_)=>{using var dialog=new SaveFileDialog{Filter=Ui.Text("DVD 镜像|*.iso"),FileName=Path.GetFileName(imagePath.Text),OverwritePrompt=false};if(dialog.ShowDialog(this)==DialogResult.OK){imagePath.Text=dialog.FileName;encoded=null;}};
        imagePath.TextChanged+=(_,_)=>encoded=null;
        refresh.Click+=async(_,_)=>await RefreshDevices();devices.SelectedIndexChanged+=async(_,_)=>{if(!loading)await RefreshMedium();};
        generate.Click+=async(_,_)=>await Run(false);burn.Click+=async(_,_)=>await Run(true);cancel.Click+=(_,_)=>cancellation?.Cancel();close.Click+=(_,_)=>Close();
        FormClosing+=(_,e)=>{if(!busy)return;e.Cancel=true;if(!writing)cancellation?.Cancel();status.Text=writing?Ui.Text("正在写盘，请等待光驱收尾。"):Ui.Text("正在取消生成，请稍候…");};
        Shown+=async(_,_)=>await RefreshDevices();
        CompleteLayout();
    }
    private async Task RefreshDevices()
    {
        if(busy)return;int version=++refreshVersion;refresh.Enabled=false;loading=true;
        try
        {
            var found=await backend.GetDevicesAsync();if(IsDisposed||version!=refreshVersion)return;
            devices.Items.Clear();foreach(var device in found)devices.Items.Add(new DeviceChoice(device));if(devices.Items.Count>0)devices.SelectedIndex=0;
            else medium.Text=Ui.Text("未发现刻录机；仍可生成 ISO。");
        }
        catch(Exception e){medium.Text=Ui.BurnError(e.Message);}
        finally{loading=false;if(!IsDisposed&&!busy)refresh.Enabled=true;}
        if(!IsDisposed)await RefreshMedium();
    }
    private async Task RefreshMedium()
    {
        if(busy)return;int version=++refreshVersion;speeds.Items.Clear();burn.Enabled=false;
        if(devices.SelectedItem is not DeviceChoice choice)return;
        devices.Enabled=refresh.Enabled=false;
        try
        {
            var value=await BurnMedia.Inspect(choice.Device.Id);value.RequireBlank(design.Geometry.Sectors);
            var available=await backend.GetSupportedWriteSpeedsAsync(choice.Device.Id,OpticalBurnMediaKind.DvdData);
            if(IsDisposed||version!=refreshVersion)return;
            foreach(var speed in available.OrderBy(s=>s.SectorsPerSecond))speeds.Items.Add(new SpeedChoice(speed));
            if(speeds.Items.Count==0)throw new InvalidOperationException(Ui.Text("没有可用的刻录速度。"));
            speeds.SelectedIndex=0;for(int i=0;i<speeds.Items.Count;i++)if(speeds.Items[i] is SpeedChoice candidate&&Math.Abs(candidate.Speed.GetMultiplier(OpticalBurnMediaKind.DvdData)-4)<.05)speeds.SelectedIndex=i;
            medium.Text=Ui.Format("空白 DVD-R · {0:N0} 扇区 · 与校准容量一致",value.TotalSectors);burn.Enabled=true;
        }
        catch(Exception e){medium.Text=Ui.BurnError(e.Message);}
        finally{if(!IsDisposed&&!busy){devices.Enabled=true;refresh.Enabled=true;}}
    }
    private void Busy(bool value)
    {busy=value;if(value)refreshVersion++;generate.Enabled=browse.Enabled=imagePath.Enabled=devices.Enabled=speeds.Enabled=refresh.Enabled=close.Enabled=!value;burn.Enabled=!value&&speeds.SelectedItem is SpeedChoice;cancel.Enabled=value&&!writing;}
    private async Task Run(bool write)
    {
        if(busy)return;
        var device=devices.SelectedItem as DeviceChoice;var speed=speeds.SelectedItem as SpeedChoice;
        if(write&&(device is null||speed is null)){status.Text=Ui.Text("请选择空白 DVD-R 和刻录速度。");return;}
        using var cts=new CancellationTokenSource();cancellation=cts;Busy(true);progress.Value=0;
        bool attempted=false;long written=0;string stage="";
        try
        {
            if(write)(await BurnMedia.Inspect(device!.Device.Id)).RequireBlank(design.Geometry.Sectors);
            if(encoded is null)
            {
                string target=imagePath.Text.Trim();var updates=new Progress<EncodingProgress>(p=>{status.Text=p.Message;progress.Value=(int)Math.Clamp(p.Fraction*1000,0,1000);});
                encoded=await Task.Run(()=>DvdImageEncoder.Generate(design,target,updates,cts.Token));
            }
            status.Text=Ui.Text("镜像已生成：")+encoded.Path;progress.Value=1000;
            if(!write)return;
            (await BurnMedia.Inspect(device!.Device.Id)).RequireBlank(design.Geometry.Sectors);
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
            try{await BurnMedia.Eject(device.Device.Id);status.Text+=Ui.Text(" 光盘已弹出。");}catch{status.Text+=Ui.Text(" 请手动弹出光盘。");}
        }
        catch(OperationCanceledException){status.Text=Ui.Text("已取消镜像生成。");}
        catch(Exception error)
        {
            status.Text=attempted&&written==design.Geometry.Sectors*2048&&stage=="finalize"?Ui.Text("刻录收尾失败：")+Ui.BurnError(error.Message):((attempted?Ui.Text("刻录失败："):Ui.Text("准备失败："))+Ui.BurnError(error.Message));
            if(encoded is not null)status.Text+=Ui.Text("\n镜像位置：")+encoded.Path;
        }
        finally{writing=false;cancellation=null;Busy(false);if(attempted){burn.Enabled=false;medium.Text=Ui.Text("本次写盘已结束。下一次请放入新空白盘并刷新。");}}
    }
}
