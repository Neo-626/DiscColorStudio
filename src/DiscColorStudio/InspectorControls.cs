namespace DiscColorStudio;

internal sealed class InspectorSection : FlowLayoutPanel, ILocalizedControl
{
    private readonly Button header;
    private readonly string title;
    internal FlowLayoutPanel Body { get; } = new() { Width=296,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(10,8,10,12),Margin=Padding.Empty,BackColor=Color.White };
    internal bool Expanded { get; private set; }
    internal InspectorSection(string title, bool expanded)
    {
        this.title=Ui.SourceKey(title)??title;Width=296;AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;FlowDirection=FlowDirection.TopDown;WrapContents=false;Margin=new Padding(0,0,0,8);
        header=new Button{Width=296,Height=36,TextAlign=ContentAlignment.MiddleLeft,FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(231,235,241),ForeColor=Color.FromArgb(40,49,63),Margin=Padding.Empty,Padding=new Padding(8,0,0,0),AccessibleName=title};
        header.FlatAppearance.BorderSize=0;header.Click+=(_,_)=>SetExpanded(!Expanded);
        Controls.Add(header);Controls.Add(Body);SetExpanded(expanded);
    }
    internal void SetExpanded(bool expanded){Expanded=expanded;Body.Visible=expanded;RefreshLanguage();}
    public void RefreshLanguage(){header.Text=(Expanded?"▾  ":"▸  ")+Ui.Text(title);header.AccessibleName=Ui.Text(title);}
}

internal sealed class AdjustmentSlider : UserControl, ILocalizedControl
{
    private readonly Label caption;
    private readonly string labelKey,unit;
    private readonly NumericUpDown number;
    private readonly TrackBar slider;
    private bool syncing,dragging;
    internal event Action? GestureStarted;
    internal event Action<double,bool>? Edited;
    internal double Value => (double)number.Value;
    internal AdjustmentSlider(string label,int min,int max,int initial,string unit="")
    {
        Width=276;Height=66;Margin=new Padding(0,2,0,4);BackColor=Color.White;
        labelKey=Ui.SourceKey(label)??label;this.unit=unit;
        caption=new Label{Text=label+(unit.Length>0?" / "+unit:""),Left=0,Top=3,Width=176,Height=24,TextAlign=ContentAlignment.MiddleLeft};Controls.Add(caption);
        number=new NumericUpDown{Left=184,Top=0,Width=90,Minimum=min,Maximum=max,Value=initial,AccessibleName=label,BorderStyle=BorderStyle.FixedSingle};
        slider=new TrackBar{Left=-3,Top=29,Width=282,Height=30,Minimum=min,Maximum=max,Value=initial,TickStyle=TickStyle.None,AutoSize=false,AccessibleName=label+Ui.Text("滑块"),SmallChange=1,LargeChange=5};
        Controls.Add(number);Controls.Add(slider);
        number.ValueChanged+=(_,_)=>Change((int)number.Value);
        slider.MouseDown+=(_,_)=>{dragging=true;GestureStarted?.Invoke();};
        slider.MouseUp+=(_,_)=>dragging=false;
        slider.MouseCaptureChanged+=(_,_)=>{if(!slider.Capture)dragging=false;};
        slider.ValueChanged+=(_,_)=>Change(slider.Value);
    }
    private void Change(int value){if(syncing)return;SetValue(value);Edited?.Invoke(value,!dragging);}
    public void RefreshLanguage(){string label=Ui.Text(labelKey);caption.Text=label+(unit.Length>0?" / "+unit:"");number.AccessibleName=label;slider.AccessibleName=label+Ui.Text("滑块");}
    internal void SetRange(int min,int max)
    {
        syncing=true;
        number.Minimum=min;number.Maximum=max;slider.Minimum=min;slider.Maximum=max;
        syncing=false;
    }
    internal void SetValue(double value)
    {
        syncing=true;number.Value=Math.Clamp((decimal)Math.Round(value),number.Minimum,number.Maximum);slider.Value=(int)number.Value;syncing=false;
    }
}
