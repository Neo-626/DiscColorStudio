using System.Diagnostics;

namespace DiscColorStudio;

public sealed class MainForm : ResponsiveForm
{
    private sealed record GridChoice(int Size) { public override string ToString()=>Ui.Format("{0:N0} 格 · {1:0.##} μm/格",Size,120000.0/Size); }
    public Design Document { get; private set; } = null!;
    public DiscCanvas Canvas { get; } = new() { Dock=DockStyle.Fill };
    private readonly CheckedListBox layers=new(){Dock=DockStyle.Fill,CheckOnClick=false,IntegralHeight=false,BorderStyle=BorderStyle.None};
    private readonly Panel inspector=new(){Dock=DockStyle.Fill,AutoScroll=true,Padding=new Padding(10),BackColor=Color.FromArgb(244,246,249)};
    private readonly VerticalStack inspectorContent=new();
    private readonly InspectorSection lightSection=new(Ui.Text("光线"),true),colorSection=new(Ui.Text("颜色"),false),textureSection=new(Ui.Text("纹理与细节"),false),layoutSection=new(Ui.Text("布局"),false);
    private readonly Label selection=new(){AutoSize=true,Margin=new Padding(0,2,0,10),ForeColor=Color.FromArgb(55,68,86)};
    private readonly Button sync=new(){Text=Ui.Text("同步到全部图片"),AutoSize=true,MinimumSize=new Size(0,34),Padding=new Padding(6,4,6,4),Margin=new Padding(0,0,0,12)};
    private readonly ToolStripStatusLabel status=new(){Spring=true,TextAlign=ContentAlignment.MiddleLeft},cursor=new();
    private readonly AdjustmentSlider exposure=new(Ui.Text("曝光"),10,800,100,"%"),brightness=new(Ui.Text("亮度"),-128,128,0),contrast=new(Ui.Text("对比度"),10,400,100,"%");
    private readonly AdjustmentSlider hue=new(Ui.Text("色相"),-60,60,0,"°"),saturation=new(Ui.Text("饱和度"),-100,100,0,"%"),luminance=new(Ui.Text("明亮度"),-100,100,0,"%");
    private readonly AdjustmentSlider banding=new(Ui.Text("条带平滑"),0,100,100,"%"),mixing=new(Ui.Text("自适应混色"),0,100,100,"%");
    private readonly Button[] bands=new Button[8];
    private readonly Button allColors=new(){Text=Ui.Text("全部颜色"),AutoSize=true,MinimumSize=new Size(0,32),Padding=new Padding(4,3,4,3),FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(231,235,241),Margin=new Padding(0,0,0,6)};
    private readonly WrappingCheckBox enableColors=new(){Text=Ui.Text("启用颜色调整"),AutoSize=true},preferDark=new(){Text=Ui.Text("暗部增强 · DIS 灰阶纹理"),AutoSize=true};
    private readonly ComboBox colourMethod=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=190};
    private readonly Label simulationCaption=new(){AutoSize=true,Text=Ui.Text("模拟预览"),Margin=new Padding(0,6,8,0),ForeColor=Color.SlateGray};
    private Label precisionCaption=null!;
    private readonly ComboBox previewMode=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=210};
    private readonly ComboBox observation=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=210};
    private readonly NumericUpDown viewRotation=Numeric(-180,180,1),viewTiltX=Numeric(-10,10,1),viewTiltY=Numeric(-10,10,1);
    private readonly WrappingFlowPanel viewing=new(){Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=true,Padding=new Padding(10,2,8,4),BackColor=Color.White};
    private bool viewingLoading;
    private LadderReference? sharedReference;
    private readonly NumericUpDown headShift=Numeric(-150,150,1);
    private readonly InspectorSection directionSection=new(Ui.Text("观看设置"),true);
    private readonly NumericUpDown directionAzimuth=Numeric(0,359.9m,1);
    private readonly NumericUpDown observerAzimuth=Numeric(0,359.9m,1);
    private Button drawDirection=null!;

    private readonly ComboBox precision=new(){Width=276,DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly TextBox name=new(){Width=276};
    private readonly NumericUpDown x=Numeric(-180,180,3),y=Numeric(-180,180,3),width=Numeric((decimal)RasterGrid.MinLayerSizeMm,240,3),height=Numeric((decimal)RasterGrid.MinLayerSizeMm,240,3),angle=Numeric(-36000,36000,2),opacity=Numeric(0,100,0);
    private readonly WrappingCheckBox aspect=new(){Text=Ui.Text("保持宽高比"),AutoSize=true};
    private Button editText=null!,fillColor=null!;
    private readonly ToolTip hints=new();
    private readonly UiBindings captions;
    private readonly System.Windows.Forms.Timer redraw=new(){Interval=16};
    private readonly Stack<Design> undo=new(),redo=new();
    private string? projectPath;
    private bool loading,dirty;
    private int band=-1;
    private long lastInspectorUpdate;

    public MainForm(Design initial,string? path=null,bool calibrationChanged=false)
    {
        if(!initial.HasDisCalibration)throw new InvalidOperationException(Ui.Text("请先导入 DIS 校准后的 DVD 绘图工程（.discproj）。"));
        Font=new Font("Microsoft YaHei UI",9.5f);BackColor=Color.FromArgb(244,247,251);StartPosition=FormStartPosition.CenterScreen;AllowDrop=true;
        ClientSize=new Size(1460,940);MinimumSize=new Size(760,460);
        var menu=BuildMenu();
        var toolbar=new WrappingFlowPanel{Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,MinimumSize=new Size(0,48),Padding=new Padding(10,6,8,6),WrapContents=true,BackColor=Color.White};
        toolbar.Controls.AddRange([Button(Ui.Text("导入图片"),Import),Button(Ui.Text("添加文字"),()=>Add("text")),Button(Ui.Text("矩形"),()=>Add("rectangle")),Button(Ui.Text("椭圆"),()=>Add("ellipse")),Button(Ui.Text("撤销"),Undo),Button(Ui.Text("重做"),Redo),Button(Ui.Text("整盘"),Canvas.FitDisc),Button(Ui.Text("设计区"),Canvas.FocusDesign)]);
        toolbar.Controls.Add(colourMethod);
        colourMethod.SelectedIndexChanged+=(_,_)=>{if(loading||Document is null)return;PushUndo();Document.ColourMethod=colourMethod.SelectedIndex==0?"ladder":"measured";ContinuousDesign.Prepare(Document);Document.Format=Document.Layers.Any(l=>l.LadderViewing is not null)?"disc-color-design-v8":Document.Layers.Any(l=>l.LadderPlacement is not null)?"disc-color-design-v7":Document.Layers.Any(l=>l.LadderObservation is not null)?"disc-color-design-v6":Document.UsesLadder?"disc-color-design-v5":"disc-color-design-v4";RefreshInspector();Changed();};
        toolbar.Controls.Add(Button(Ui.Text("保存工程"),()=>Save(false)));
        var burn=Button(Ui.Text("烧录光盘…"),BurnDisc);burn.BackColor=Color.FromArgb(37,110,210);burn.ForeColor=Color.White;burn.FlatStyle=FlatStyle.Flat;toolbar.Controls.Add(burn);
        toolbar.Controls.Add(Button(Ui.Text("DIS 校准…"),ImportGeometry));
        var body=new Panel{Dock=DockStyle.Fill,Padding=new Padding(10,10,10,0)};
        var layerPanel=new Panel{Dock=DockStyle.Fill,BackColor=Color.White,Padding=new Padding(12),Margin=new Padding(0,0,10,0)};
        var title=new Label{Text=Ui.Text("图层"),Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(0,0,0,10),Font=new Font(Font,FontStyle.Bold)};
        var layerTools=new WrappingFlowPanel{Dock=DockStyle.Bottom,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,MinimumSize=new Size(0,76),WrapContents=true};
        layerTools.Controls.AddRange([Button(Ui.Text("上移"),()=>MoveLayer(1)),Button(Ui.Text("下移"),()=>MoveLayer(-1)),Button(Ui.Text("复制"),Duplicate),Button(Ui.Text("删除"),Delete)]);
        layerPanel.Controls.Add(layers);layerPanel.Controls.Add(layerTools);layerPanel.Controls.Add(title);
        layers.HorizontalScrollbar=true;
        inspector.Controls.Add(inspectorContent);
        body.Controls.Add(new EditorWorkspace(layerPanel,Canvas,inspector));BuildInspector();
        var footer=new StatusStrip{SizingGrip=true};footer.Items.Add(status);footer.Items.Add(cursor);
        BuildViewing();Controls.Add(body);Controls.Add(viewing);Controls.Add(toolbar);Controls.Add(menu);Controls.Add(footer);MainMenuStrip=menu;
        layers.SelectedIndexChanged+=(_,_)=>{if(!loading&&layers.SelectedItem is Layer l)Select(l.Id);};
        layers.ItemCheck+=(_,e)=>{if(!loading){PushUndo();((Layer)layers.Items[e.Index]).Visible=e.NewValue==CheckState.Checked;Changed();}};
        Canvas.SelectionChanged+=Select;Canvas.BeginEdit+=PushUndo;Canvas.ObservationEdited+=()=>{RefreshInspector();ResetViewing();};
        Canvas.LayerEdited+=()=>{if(Environment.TickCount64-lastInspectorUpdate>80){RefreshInspector();lastInspectorUpdate=Environment.TickCount64;}Changed(false);};
        Canvas.RenderStatus+=text=>status.Text=text;Canvas.ViewportChanged+=Schedule;Canvas.CursorPositionChanged+=text=>cursor.Text=text;
        redraw.Tick+=(_,_)=>{redraw.Stop();try{Canvas.RefreshArtwork();}catch(Exception e){status.Text=e.Message;}};
        DragEnter+=(_,e)=>{if(e.Data?.GetDataPresent(DataFormats.FileDrop)==true)e.Effect=DragDropEffects.Copy;};
        DragDrop+=(_,e)=>{if(e.Data?.GetData(DataFormats.FileDrop) is not string[] files)return;foreach(var file in files)try{if(file.EndsWith(".discolor",StringComparison.OrdinalIgnoreCase))OpenPath(file);else ImportPath(file);}catch(Exception error){Fail(error);}};
        FormClosing+=(_,e)=>{if(!ConfirmLeave())e.Cancel=true;};
        captions=new UiBindings(this);
        SetDocument(initial,path);if(calibrationChanged){dirty=true;UpdateTitle();}
        CompleteLayout();
    }

    private void BuildViewing()
    {
        viewing.Controls.Add(simulationCaption);viewing.Controls.Add(previewMode);viewing.Controls.Add(observation);
        void Number(string title,NumericUpDown n)
        {var group=new FlowLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=false,Margin=new Padding(5,0,0,0)};n.Width=84;
         group.Controls.Add(new Label{Text=Ui.Text(title),AutoSize=true,Margin=new Padding(0,6,2,0)});group.Controls.Add(n);viewing.Controls.Add(group);}
        Number("转盘 / °",viewRotation);Number("倾斜 X / °",viewTiltX);Number("倾斜 Y / °",viewTiltY);Number("左右移动 / mm",headShift);
        viewing.Controls.Add(Button(Ui.Text("对准选中图片"),()=>{if(Canvas.Selected is {} l){viewingLoading=true;observation.SelectedIndex=1;sharedReference=LadderReference.From(l);viewRotation.Value=viewTiltX.Value=viewTiltY.Value=headShift.Value=0;viewingLoading=false;UpdateViewing();}}));
        viewing.Controls.Add(Button(Ui.Text("重置视角"),ResetViewing));
        previewMode.SelectedIndexChanged+=(_,_)=>UpdateViewing();observation.SelectedIndexChanged+=(_,_)=>UpdateViewing();
        viewRotation.ValueChanged+=(_,_)=>UpdateViewing();viewTiltX.ValueChanged+=(_,_)=>UpdateViewing();viewTiltY.ValueChanged+=(_,_)=>UpdateViewing();headShift.ValueChanged+=(_,_)=>UpdateViewing();
        hints.SetToolTip(observation,Ui.Text("逐图参考视角分别展示每张图片的设计视角；统一视角用同一转盘姿态观察整盘。"));
        hints.SetToolTip(viewRotation,Ui.Text("改变观察姿态，使用既有配方预测颜色；导出配方保持不变。"));
    }
    private void ResetViewing()
    {viewingLoading=true;sharedReference=null;observation.SelectedIndex=0;viewRotation.Value=viewTiltX.Value=viewTiltY.Value=headShift.Value=0;previewMode.SelectedIndex=0;viewingLoading=false;UpdateViewing();}
    private void RefreshViewing()
    {
        viewingLoading=true;int mode=Math.Max(0,previewMode.SelectedIndex),obs=Math.Max(0,observation.SelectedIndex);
        previewMode.Items.Clear();previewMode.Items.AddRange([Ui.Text("模拟颜色"),Ui.Text("调整后的原图")]);previewMode.SelectedIndex=mode;
        observation.Items.Clear();observation.Items.AddRange([Ui.Text("逐图参考视角"),Ui.Text("统一转盘视角")]);observation.SelectedIndex=obs;
        foreach(Control c in viewing.Controls.Cast<Control>().Where(c=>c!=previewMode && c!=simulationCaption))c.Visible=Document.UsesLadder;
        viewingLoading=false;
    }
    private void UpdateViewing()
    {
        if(loading||viewingLoading||Document is null)return;
        Canvas.ViewMode=previewMode.SelectedIndex==1?2:0;
        Canvas.Simulation=new(observation.SelectedIndex==1,(double)viewRotation.Value,(double)viewTiltX.Value,(double)viewTiltY.Value,observation.SelectedIndex==1?sharedReference:null,(double)headShift.Value);
        Canvas.InvalidateArtwork();Schedule();
    }
    private MenuStrip BuildMenu()
    {
        var menu=new MenuStrip{BackColor=Color.White};var file=new ToolStripMenuItem(Ui.Text("文件"));var edit=new ToolStripMenuItem(Ui.Text("编辑"));var help=new ToolStripMenuItem(Ui.Text("帮助"));
        file.DropDownItems.Add(Ui.Text("新建设计"),null,(_,_)=>New());file.DropDownItems.Add(Ui.Text("打开工程…"),null,(_,_)=>Open());
        file.DropDownItems.Add(Ui.Text("保存工程"),null,(_,_)=>Save(false));file.DropDownItems.Add(Ui.Text("另存为…"),null,(_,_)=>Save(true));
        file.DropDownItems.Add(Ui.Text("烧录光盘…"),null,(_,_)=>BurnDisc());file.DropDownItems.Add(Ui.Text("DIS 校准…"),null,(_,_)=>ImportGeometry());
        edit.DropDownItems.Add(Ui.Text("撤销  Ctrl+Z"),null,(_,_)=>Undo());edit.DropDownItems.Add(Ui.Text("重做  Ctrl+Y"),null,(_,_)=>Redo());
        edit.DropDownItems.Add(Ui.Text("复制图层  Ctrl+D"),null,(_,_)=>Duplicate());edit.DropDownItems.Add(Ui.Text("删除图层  Delete"),null,(_,_)=>Delete());
        help.DropDownItems.Add(Ui.Text("使用方法"),null,(_,_)=>Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,Ui.HelpFileName)){UseShellExecute=true}));
        help.DropDownItems.Add(Ui.Text("周期梯说明"),null,(_,_)=>Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,Ui.IsEnglish?"LadderGuide.html":"周期梯说明.html")){UseShellExecute=true}));
        help.DropDownItems.Add(Ui.Text("语言…"),null,(_,_)=>ChangeLanguage());
        help.DropDownItems.Add(Ui.Text("关于"),null,(_,_)=>MessageBox.Show(this,Ui.Format("Disc Color Studio {0}\n基于 DiscImageStudio 的 DVD 编码与刻录组件。\n许可证：GNU GPL v3。",typeof(MainForm).Assembly.GetName().Version!.ToString(3)),Ui.Text("关于")));
        menu.Items.AddRange([file,edit,help]);return menu;
    }
    private static NumericUpDown Numeric(decimal min,decimal max,int places)=>new(){Minimum=min,Maximum=max,DecimalPlaces=places,Increment=places>0?.1m:1,Width=276};
    private void ChangeLanguage()
    {
        using var dialog=new LanguageForm(Ui.Language);
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        Ui.SetLanguage(dialog.SelectedLanguage);
        if(!Ui.SavePreference(Ui.Language))MessageBox.Show(this,Ui.Text("语言选择未能保存，下次启动请重新选择。"));
        RefreshLanguage();
    }
    public void RefreshLanguage()
    {
        SuspendLayout();inspector.SuspendLayout();
        try
        {
            captions.Apply();
            for(int i=0;i<bands.Length;i++)
            {
                bands[i].AccessibleName=ColorMixer.Names[i]+Ui.Text("色通道");
                bands[i].Font=new Font(Ui.IsEnglish?"Segoe UI":Font.FontFamily.Name,Ui.IsEnglish?8.5f:9.5f);
            }
            hints.SetToolTip(sync,Ui.Text("同步光线、颜色、纹理与采样设置，并用于之后导入的图片。"));
            hints.SetToolTip(banding,Ui.Text("提高强度，让相邻角度的纹理配方过渡更平滑。"));
            hints.SetToolTip(mixing,Ui.Text("提高强度，按图片颜色与盘面位置混合相近纹理。"));
            loading=true;precision.Items.Clear();precision.Items.AddRange(RasterGrid.Sizes.Select(size=>new GridChoice(size)).ToArray());
            RefreshInspector();status.Text=Ui.Text("连续彩色");Canvas.Invalidate();
        }
        finally{loading=false;inspector.ResumeLayout(true);ResumeLayout(true);}
    }
    private Button Button(string text,Action action)
    {
        var button=new Button{Text=text,AutoSize=true,Height=32,MinimumSize=new Size(62,32),Margin=new Padding(2,0,4,2),FlatStyle=FlatStyle.System};
        button.Click+=(_,_)=>{try{action();}catch(Exception e){Fail(e);}};return button;
    }
    private static void Field(Control parent,string label,Control control)
    {parent.Controls.Add(new Label{Text=label,AutoSize=true,ForeColor=Color.SlateGray,Margin=new Padding(0,6,0,3)});control.Margin=new Padding(0,0,0,5);parent.Controls.Add(control);}
    private void Bind(AdjustmentSlider control,Action<Layer,double> setter)
    {
        control.GestureStarted+=()=>{if(Canvas.Selected?.Kind=="image")PushUndo();};
        control.Edited+=(value,saveUndo)=>EditAppearance(l=>setter(l,value),saveUndo);
    }
    private void BuildInspector()
    {
        inspectorContent.Controls.Add(selection);inspectorContent.Controls.Add(sync);sync.Click+=(_,_)=>Synchronize();
        hints.SetToolTip(sync,Ui.Text("同步光线、颜色、纹理与采样设置，并用于之后导入的图片。"));
        inspectorContent.Controls.AddRange([directionSection,lightSection,colorSection,textureSection,layoutSection]);
        Field(directionSection.Body,Ui.Text("观察时盘面摆位 / °"),directionAzimuth);
        directionSection.Body.Controls.Add(new Label{AutoSize=true,Text=Ui.Text("3 点钟为 0°，顺时针度数增加。"),ForeColor=Color.SlateGray,Margin=new Padding(0,0,0,8)});
        Field(directionSection.Body,Ui.Text("观察者方向 / °"),observerAzimuth);
        hints.SetToolTip(directionAzimuth,Ui.Text("观看这张图片时，整张光盘绕盘心顺时针旋转的角度。"));
        hints.SetToolTip(observerAzimuth,Ui.Text("图片转到所设摆位后，指向观察者的方向：右 0°、下 90°、左 180°、上 270°。"));
        drawDirection=Button(Ui.Text("拖箭头指向观察者"),()=>{Canvas.BeginObservationArrow();status.Text=Ui.Text("从图片向观察者方位拖箭头；Esc 取消，Shift 吸附 15°。");});
        directionSection.Body.Controls.Add(drawDirection);
        directionSection.Body.Controls.Add(new Label{AutoSize=true,Text=Ui.Text("两个设置可随时调整；箭头从图片指向观察者。"),ForeColor=Color.SlateGray,Margin=new Padding(0,6,0,8)});
        directionSection.Body.Controls.Add(Button(Ui.Text("恢复默认观看设置"),()=>{Edit(l=>{l.LadderViewing=null;l.LadderPlacement=null;l.LadderObservation=null;});ResetViewing();}));
        directionAzimuth.ValueChanged+=(_,_)=>EditDirection(true,(double)directionAzimuth.Value);
        observerAzimuth.ValueChanged+=(_,_)=>EditDirection(false,(double)observerAzimuth.Value);
        lightSection.Body.Controls.AddRange([exposure,brightness,contrast]);
        lightSection.Body.Controls.Add(Button(Ui.Text("重置光线"),()=>{EditAppearance(l=>{l.InputExposure=1;l.Brightness=0;l.Contrast=1;});RefreshInspector();}));
        Bind(exposure,(l,v)=>l.InputExposure=v/100);Bind(brightness,(l,v)=>l.Brightness=v);Bind(contrast,(l,v)=>l.Contrast=v/100);
        colorSection.Body.Controls.Add(allColors);
        allColors.Click+=(_,_)=>{band=-1;RefreshColors();};
        var bandRow=new TableLayoutPanel{ColumnCount=4,RowCount=2,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Margin=new Padding(0,0,0,4)};
        for(int column=0;column<4;column++)bandRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25));
        for(int row=0;row<2;row++)bandRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Color[] colors=[Color.IndianRed,Color.Peru,Color.DarkGoldenrod,Color.SeaGreen,Color.Teal,Color.SteelBlue,Color.MediumPurple,Color.PaleVioletRed];
        for(int i=0;i<8;i++)
        {
            int index=i;var button=new Button{Text=ColorMixer.Names[i],Dock=DockStyle.Fill,AutoSize=true,MinimumSize=new Size(0,32),Padding=new Padding(0,3,0,3),Margin=new Padding(0,2,0,2),FlatStyle=FlatStyle.Flat,Font=new Font(Ui.IsEnglish?"Segoe UI":Font.FontFamily.Name,Ui.IsEnglish?8.5f:9.5f),BackColor=colors[i],ForeColor=Color.White,AccessibleName=ColorMixer.Names[i]+Ui.Text("色通道")};
            button.Click+=(_,_)=>{band=index;RefreshColors();};bands[i]=button;bandRow.Controls.Add(button,i%4,i/4);
        }
        colorSection.Body.Controls.Add(bandRow);colorSection.Body.Controls.AddRange([hue,saturation,luminance,enableColors]);
        Bind(hue,(l,v)=>{l.ColorMixer??=new();SelectedColor(l.ColorMixer).Hue=v;});
        Bind(saturation,(l,v)=>{l.ColorMixer??=new();SelectedColor(l.ColorMixer).Saturation=v;});
        Bind(luminance,(l,v)=>{l.ColorMixer??=new();SelectedColor(l.ColorMixer).Luminance=v;});
        enableColors.CheckedChanged+=(_,_)=>{if(!loading)EditAppearance(l=>l.BypassColorMixer=!enableColors.Checked);};
        var resets=new WrappingFlowPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=true,Margin=new Padding(0,6,0,0)};
        resets.Controls.Add(Button(Ui.Text("重置当前调整"),()=>{EditAppearance(l=>{if(l.ColorMixer is {} mixer){if(band<0)mixer.Master=new();else mixer.Bands[band]=new();}});RefreshColors();}));
        resets.Controls.Add(Button(Ui.Text("重置全部颜色"),()=>{EditAppearance(l=>{l.ColorMixer=null;l.BypassColorMixer=false;});RefreshColors();}));colorSection.Body.Controls.Add(resets);
        textureSection.Body.Controls.AddRange([banding,mixing,preferDark]);
        Bind(banding,(l,v)=>l.BandingReduction=v/100);Bind(mixing,(l,v)=>l.AdaptiveMixing=v/100);
        hints.SetToolTip(banding,Ui.Text("提高强度，让相邻角度的纹理配方过渡更平滑。"));hints.SetToolTip(mixing,Ui.Text("提高强度，按图片颜色与盘面位置混合相近纹理。"));
        preferDark.CheckedChanged+=(_,_)=>{if(!loading)EditAppearance(l=>l.PreferDisDark=preferDark.Checked);};
        precision.Items.AddRange(RasterGrid.Sizes.Select(size=>new GridChoice(size)).ToArray());Field(textureSection.Body,Ui.Text("采样精度"),precision);
        precisionCaption=(Label)textureSection.Body.Controls[textureSection.Body.Controls.Count-2];
        precision.SelectedIndexChanged+=(_,_)=>{if(!loading&&precision.SelectedItem is GridChoice choice)EditAppearance(l=>l.RasterSize=choice.Size);};
        textureSection.Body.Controls.Add(Button(Ui.Text("重置纹理设置"),()=>{EditAppearance(l=>{l.BandingReduction=1;l.AdaptiveMixing=1;l.PreferDisDark=false;l.RasterSize=RasterGrid.DefaultSize;});RefreshInspector();}));
        Field(layoutSection.Body,Ui.Text("名称"),name);Field(layoutSection.Body,Ui.Text("中心 X / mm"),x);Field(layoutSection.Body,Ui.Text("中心 Y / mm"),y);
        Field(layoutSection.Body,Ui.Text("宽度 / mm"),width);Field(layoutSection.Body,Ui.Text("高度 / mm"),height);layoutSection.Body.Controls.Add(aspect);
        Field(layoutSection.Body,Ui.Text("旋转 / °"),angle);Field(layoutSection.Body,Ui.Text("不透明度 / %"),opacity);
        fillColor=Button(Ui.Text("填充颜色…"),ChooseFill);editText=Button(Ui.Text("编辑文字…"),EditText);layoutSection.Body.Controls.AddRange([fillColor,editText]);
        name.TextChanged+=(_,_)=>Edit(l=>l.Name=name.Text,refreshList:true);
        x.ValueChanged+=(_,_)=>Edit(l=>l.X=(double)x.Value);y.ValueChanged+=(_,_)=>Edit(l=>l.Y=(double)y.Value);
        width.ValueChanged+=(_,_)=>Edit(l=>{double ratio=(double)width.Value/l.Width;l.Width=(double)width.Value;if(l.KeepAspect)l.Height=Math.Clamp(l.Height*ratio,RasterGrid.MinLayerSizeMm,240);});
        height.ValueChanged+=(_,_)=>Edit(l=>{double ratio=(double)height.Value/l.Height;l.Height=(double)height.Value;if(l.KeepAspect)l.Width=Math.Clamp(l.Width*ratio,RasterGrid.MinLayerSizeMm,240);});
        angle.ValueChanged+=(_,_)=>Edit(l=>l.Rotation=(double)angle.Value);opacity.ValueChanged+=(_,_)=>Edit(l=>l.Opacity=(double)opacity.Value/100);
        aspect.CheckedChanged+=(_,_)=>Edit(l=>l.KeepAspect=aspect.Checked);
    }
    private void EditDirection(bool disc,double angle)
    {
        if(loading || !Document.UsesLadder)return;
        Edit(l=>{if(disc)LadderViewing.SetDisc(l,angle);else LadderViewing.SetObserver(l,angle);});ResetViewing();
    }
    private void EditAppearance(Action<Layer> apply,bool saveUndo=true)
    {
        if(loading||Canvas.Selected is not {Kind:"image"} layer)return;
        if(saveUndo)PushUndo();apply(layer);Changed();
    }
    private void Edit(Action<Layer> apply,bool refreshList=false)
    {
        if(loading||Canvas.Selected is not {} layer)return;
        PushUndo();apply(layer);if(refreshList)RefreshLayers();RefreshInspector();Changed();
    }
    private void Synchronize()
    {
        if(Canvas.Selected is not {Kind:"image"} layer)return;
        PushUndo();ContinuousDesign.Synchronize(Document,layer);RefreshInspector();Changed();
        status.Text=Ui.Format("已同步到 {0} 张图片，并设为后续导入的默认调整。",Document.Layers.Count(l=>l.Kind=="image"));
    }
    private void ChooseFill()
    {
        if(Canvas.Selected is not {Kind:not "image"} layer)return;
        using var dialog=new ColorDialog{FullOpen=true,Color=Color.FromArgb(layer.FillArgb??Color.Black.ToArgb())};
        if(dialog.ShowDialog(this)==DialogResult.OK)Edit(l=>l.FillArgb=dialog.Color.ToArgb());
    }
    private void RefreshInspector()
    {
        loading=true;var l=Canvas.Selected;bool image=l?.Kind=="image";
        colourMethod.Items.Clear();colourMethod.Items.AddRange([Ui.Text("周期梯保色"),Ui.Text("实测彩色")]);colourMethod.SelectedIndex=Document.UsesLadder?0:1;
        banding.SetLabel(Document.UsesLadder?"视角平衡":"条带平滑");mixing.Visible=preferDark.Visible=!Document.UsesLadder;precision.Visible=precisionCaption.Visible=true;RefreshViewing();
        hints.SetToolTip(banding,Ui.Text(Document.UsesLadder?"兼顾双眼与轻微倾斜时的颜色。提高强度会在中心颜色与相邻视角之间折中。":"提高强度，让相邻角度的纹理配方过渡更平滑。"));
        directionSection.Visible=Document.UsesLadder;directionSection.Body.Enabled=l is not null;
        directionAzimuth.Value=(decimal)Math.Min(359.9,l is null?0:LadderViewing.Current(l).DiscRotation);
        observerAzimuth.Value=(decimal)Math.Min(359.9,l is null?180:LadderViewing.Current(l).ObserverAzimuth);
        selection.Text=l is null?Ui.Text("选择图片开始调整"):l.Name;sync.Enabled=image;
        lightSection.Body.Enabled=colorSection.Body.Enabled=textureSection.Body.Enabled=image;layoutSection.Body.Enabled=l is not null;
        static void Set(NumericUpDown control,double v)=>control.Value=Math.Clamp((decimal)v,control.Minimum,control.Maximum);
        name.Text=l?.Name??"";Set(x,l?.X??0);Set(y,l?.Y??0);Set(width,l?.Width??12);Set(height,l?.Height??12);Set(angle,l?.Rotation??0);Set(opacity,(l?.Opacity??1)*100);aspect.Checked=l?.KeepAspect??true;
        exposure.SetValue((l?.InputExposure??1)*100);brightness.SetValue(l?.Brightness??0);contrast.SetValue((l?.Contrast??1)*100);
        banding.SetValue((l?.BandingReduction??1)*100);mixing.SetValue((l?.AdaptiveMixing??1)*100);preferDark.Checked=l?.PreferDisDark??false;
        int size=l is null?RasterGrid.DefaultSize:Document.ImageRasterSize(l);precision.SelectedIndex=Array.IndexOf(RasterGrid.Sizes.ToArray(),size);
        fillColor.Visible=l is {Kind:not "image"};editText.Visible=l?.Kind=="text";
        if(fillColor.Visible){var color=Color.FromArgb(l!.FillArgb??Color.Black.ToArgb());fillColor.BackColor=color;fillColor.ForeColor=color.GetBrightness()<.5?Color.White:Color.Black;}
        RefreshColors();loading=false;
    }
    private ColorBand SelectedColor(ColorMixer mixer)=>band<0?mixer.Master:mixer.Bands[band];
    private void RefreshColors()
    {
        bool prior=loading;loading=true;var layer=Canvas.Selected;var value=layer?.ColorMixer is {} mixer?SelectedColor(mixer):new();
        hue.SetRange(band<0?-180:-60,band<0?180:60);
        allColors.FlatAppearance.BorderSize=band<0?3:1;allColors.FlatAppearance.BorderColor=band<0?Color.FromArgb(25,45,70):Color.White;
        hue.SetValue(value.Hue);saturation.SetValue(value.Saturation);luminance.SetValue(value.Luminance);enableColors.Checked=layer?.BypassColorMixer!=true;
        for(int i=0;i<bands.Length;i++){bands[i].FlatAppearance.BorderSize=i==band?3:1;bands[i].FlatAppearance.BorderColor=i==band?Color.FromArgb(25,45,70):Color.White;}
        loading=prior;
    }
    private void RefreshLayers()
    {
        bool prior=loading;loading=true;layers.Items.Clear();foreach(var layer in Document.Layers.AsEnumerable().Reverse())layers.Items.Add(layer,layer.Visible);
        if(Canvas.SelectedId is not null)layers.SelectedIndex=layers.Items.Cast<Layer>().ToList().FindIndex(l=>l.Id==Canvas.SelectedId);loading=prior;
    }
    private void Select(string? id){if(Canvas.SelectedId!=id)Canvas.CancelObservationArrow();Canvas.SelectedId=id;RefreshLayers();RefreshInspector();Canvas.Invalidate();}
    private void SetDocument(Design design,string? path)
    {
        bool migrated=ContinuousDesign.Prepare(design);design.Format=design.Layers.Any(l=>l.LadderViewing is not null)?"disc-color-design-v8":design.Layers.Any(l=>l.LadderPlacement is not null)?"disc-color-design-v7":design.Layers.Any(l=>l.LadderObservation is not null)?"disc-color-design-v6":design.UsesLadder?"disc-color-design-v5":"disc-color-design-v4";
        Canvas.CancelObservationArrow();sharedReference=null;headShift.Value=0;Document=design;Canvas.Document=design;RefreshViewing();Canvas.ViewMode=0;Canvas.Simulation=new();viewingLoading=true;previewMode.SelectedIndex=0;observation.SelectedIndex=0;viewRotation.Value=viewTiltX.Value=viewTiltY.Value=0;viewingLoading=false;Canvas.SelectedId=design.Layers.LastOrDefault()?.Id;
        projectPath=path;dirty=false;undo.Clear();redo.Clear();RefreshLayers();RefreshInspector();Canvas.FitDisc();Schedule();UpdateTitle();
        if(migrated&&path is not null){projectPath=null;dirty=true;UpdateTitle();status.Text=Ui.Text("已切换到连续彩色，保存时将创建新工程。");}
    }
    private void PushUndo() { undo.Push(Document.Clone()); if (undo.Count > 50) { var keep = undo.Take(40).Reverse().ToArray(); undo.Clear(); foreach (var d in keep) undo.Push(d); } redo.Clear(); }
    private void Restore(Design d)
    { string? selected = Canvas.SelectedId; Document = d; Canvas.Document = d; Canvas.SelectedId = d.Layers.Any(l => l.Id == selected) ? selected : d.Layers.LastOrDefault()?.Id; RefreshLayers(); RefreshInspector(); Changed(); }
    private void Undo() { if (undo.Count == 0) return; redo.Push(Document.Clone()); Restore(undo.Pop()); }
    private void Redo() { if (redo.Count == 0) return; undo.Push(Document.Clone()); Restore(redo.Pop()); }
    private void Changed(bool title = true) { bool wasDirty = dirty; dirty = true; if (title || !wasDirty) UpdateTitle(); Schedule(); }
    private void Schedule() { Canvas.InvalidateArtwork(); if(!redraw.Enabled)redraw.Start(); }
    private void UpdateTitle() => Text = $"{(dirty ? "● " : "")}{Document.Name} — Disc Color Studio {typeof(MainForm).Assembly.GetName().Version!.ToString(3)}";

    public void ImportPath(string path)
    {
        using var source = Image.FromFile(path);
        if (source.Width > 20000 || source.Height > 20000 || (long)source.Width * source.Height > 100_000_000) throw new InvalidDataException(Ui.Text("图片尺寸过大，请先缩小到一亿像素以内。"));
        using var png = new Bitmap(source);
        string asset = Guid.NewGuid().ToString("N"); PushUndo(); Document.Assets[asset] = Store.Png(png);
        double ratio = source.Width / (double)source.Height;
        var layer = new Layer { Kind = "image", Name = Path.GetFileNameWithoutExtension(path), AssetId = asset,
            Width = ratio >= 1 ? 12 : 12 * ratio, Height = ratio >= 1 ? 12 / ratio : 12, InputExposure = 1,
            QuantizationMode = "adaptive", AngleColorEnabled = true };
        (Document.ImageDefaults ?? new ImageAdjustments { RasterSize = Document.RasterSize }).Apply(layer);
        Document.Layers.Add(layer); Select(layer.Id); Changed(); Canvas.FocusDesign();
    }
    private void Import()
    { using var dialog = new OpenFileDialog { Filter = Ui.Text("图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff"), Multiselect = true }; if (dialog.ShowDialog(this) == DialogResult.OK) foreach (var f in dialog.FileNames) ImportPath(f); }
    private void Add(string kind)
    {
        PushUndo(); var layer = new Layer { Kind = kind, Name = kind switch { "text" => Ui.Text("文字"), "rectangle" => Ui.Text("矩形"), _ => Ui.Text("椭圆") },
            FillId = Document.AllowedIds[0], FillArgb = Color.Black.ToArgb(), Width = kind == "text" ? 16 : 6, Height = kind == "text" ? 4 : 6, KeepAspect = kind != "text" };
        Document.Layers.Add(layer); Select(layer.Id); Changed(); Canvas.FocusDesign(); if (kind == "text") EditText();
    }
    private void EditText()
    {
        if (Canvas.Selected is not { Kind: "text" } l) return;
        using var dialog = new TextEditForm(l.Text);
        if (dialog.ShowDialog(this) == DialogResult.OK) { PushUndo(); l.Text = dialog.EditedText; Changed(); }
    }
    private void Delete() { if (Canvas.Selected is not { } l) return; PushUndo(); Document.Layers.Remove(l); Select(Document.Layers.LastOrDefault()?.Id); Changed(); }
    private void Duplicate() { if (Canvas.Selected is not { } l) return; PushUndo(); var copy = Document.Clone().Layers.Single(v => v.Id == l.Id); copy.Id = Guid.NewGuid().ToString("N"); copy.Name += Ui.Text(" 副本"); copy.X += 1; copy.Y += 1; Document.Layers.Add(copy); Select(copy.Id); Changed(); }
    private void MoveLayer(int offset)
    { if (Canvas.Selected is not { } l) return; int index = Document.Layers.IndexOf(l), target = index + offset; if (target < 0 || target >= Document.Layers.Count) return; PushUndo(); Document.Layers.RemoveAt(index); Document.Layers.Insert(target, l); RefreshLayers(); Changed(); }
    private void New()
    {
        if(!ConfirmLeave())return;
        using var calibration=new CalibrationForm(Design.Create(RasterGrid.DefaultSize));
        if(calibration.ShowDialog(this)!=DialogResult.OK)return;
        SetDocument(calibration.Result,null);Changed();
    }
    private void Open() { using var d = new OpenFileDialog { Filter = Ui.Text("彩色盘面工程|*.discolor;*.zip") }; if (d.ShowDialog(this) == DialogResult.OK) OpenPath(d.FileName); }
    public void OpenPath(string path)
    {
        if(!ConfirmLeave())return;
        var d=Store.Load(path);bool imported=false;
        if(!d.HasDisCalibration)
        {
            using var calibration=new CalibrationForm(d);
            if(calibration.ShowDialog(this)!=DialogResult.OK)return;
            d=calibration.Result;imported=true;
        }
        SetDocument(d,path.EndsWith(".discolor",StringComparison.OrdinalIgnoreCase)?path:null);
        if(imported)Changed();
    }
    private bool Save(bool saveAs)
    {
        try
        {
            string? path = saveAs ? null : projectPath;
            if (path is null)
            {
                using var dialog = new SaveFileDialog { Filter = Ui.Text("彩色盘面工程|*.discolor"), DefaultExt = "discolor", FileName = Document.Name is "未命名设计" or "Untitled design" ? Ui.Text("我的彩色盘面.discolor") : Document.Name + ".discolor" };
                if (dialog.ShowDialog(this) != DialogResult.OK) return false; path = dialog.FileName;
            }
            Document.Name = Path.GetFileNameWithoutExtension(path); Store.Save(path, Document); projectPath = path; dirty = false; UpdateTitle(); status.Text = Ui.Text("工程已保存，图片素材已内嵌：") + path; return true;
        }
        catch (Exception e) { Fail(e); return false; }
    }
    private void BurnDisc()
    {
        try { if(!Document.HasDisCalibration){ImportGeometry();if(!Document.HasDisCalibration)return;}
            using var window=new BurnForm(Document.Clone());window.ShowDialog(this); }
        catch(Exception e){Fail(e);}
    }
    private void ImportGeometry()
    {
        using var dialog=new CalibrationForm(Document);
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        PushUndo();CalibrationFiles.ApplyImport(Document,dialog.Result.Geometry,dialog.Result.CalibrationName);RefreshInspector();Changed();
    }
    private bool ConfirmLeave()
    {
        if (!dirty) return true;
        var result = MessageBox.Show(this, Ui.Text("保存当前设计后再继续？"), Ui.Text("未保存的设计"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return result == DialogResult.No || result == DialogResult.Yes && Save(false);
    }
    private void Fail(Exception error) => MessageBox.Show(this, error.Message, Ui.Text("操作未完成"), MessageBoxButtons.OK, MessageBoxIcon.Error);
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if(keyData==Keys.Escape && Canvas.DrawingObservation){Canvas.CancelObservationArrow();return true;}
        if (keyData == (Keys.Control | Keys.S)) { Save(false); return true; }
        if (keyData == (Keys.Control | Keys.O)) { Open(); return true; }
        if (keyData == (Keys.Control | Keys.N)) { New(); return true; }
        if (!inspector.ContainsFocus)
        {
            if (keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
            if (keyData == (Keys.Control | Keys.Y)) { Redo(); return true; }
            if (keyData == (Keys.Control | Keys.D)) { Duplicate(); return true; }
            if (keyData == Keys.Delete) { Delete(); return true; }
            if (Canvas.Selected is { } layer && keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
            { PushUndo(); if (keyData == Keys.Left) layer.X -= .1; if (keyData == Keys.Right) layer.X += .1; if (keyData == Keys.Up) layer.Y -= .1; if (keyData == Keys.Down) layer.Y += .1; RefreshInspector(); Changed(); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void Dispose(bool disposing) { if (disposing) { redraw.Dispose(); hints.Dispose(); } base.Dispose(disposing); }
}
