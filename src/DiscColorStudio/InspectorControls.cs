namespace DiscColorStudio;

internal sealed class InspectorSection : VerticalStack, ILocalizedControl
{
    private readonly Button header;
    private readonly string title;
    internal VerticalStack Body { get; } = new() { Padding = new Padding(10, 8, 10, 12), BackColor = Color.White };
    internal bool Expanded { get; private set; }
    internal InspectorSection(string title, bool expanded)
    {
        this.title = Ui.SourceKey(title) ?? title; Width = 296; Margin = new Padding(0, 0, 0, 8);
        header = new Button { AutoSize = true, MinimumSize = new Size(0, 36), TextAlign = ContentAlignment.MiddleLeft,
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(231,235,241), ForeColor = Color.FromArgb(40,49,63),
            Margin = Padding.Empty, Padding = new Padding(8, 4, 8, 4), AccessibleName = title };
        header.FlatAppearance.BorderSize = 0; header.Click += (_, _) => SetExpanded(!Expanded);
        Controls.Add(header); Controls.Add(Body); SetExpanded(expanded);
    }
    internal void SetExpanded(bool expanded) { Expanded = expanded; Body.Visible = expanded; RefreshLanguage(); }
    public void RefreshLanguage() { header.Text = (Expanded ? "▾  " : "▸  ") + Ui.Text(title); header.AccessibleName = Ui.Text(title); }
}

internal sealed class AdjustmentSlider : UserControl, ILocalizedControl
{
    private readonly Label caption;
    private readonly string labelKey, unit;
    private readonly NumericUpDown number;
    private readonly TrackBar slider;
    private bool syncing, dragging;
    internal event Action? GestureStarted;
    internal event Action<double, bool>? Edited;
    internal double Value => (double)number.Value;
    internal AdjustmentSlider(string label, int min, int max, int initial, string unit = "")
    {
        AutoScaleMode = AutoScaleMode.Inherit; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Width = 276; Margin = new Padding(0, 2, 0, 4); BackColor = Color.White;
        labelKey = Ui.SourceKey(label) ?? label; this.unit = unit;
        caption = new Label { Text = label + (unit.Length > 0 ? " / " + unit : ""), TextAlign = ContentAlignment.MiddleLeft };
        number = new NumericUpDown { Minimum = min, Maximum = max, Value = initial, AccessibleName = label, BorderStyle = BorderStyle.FixedSingle };
        slider = new TrackBar { Minimum = min, Maximum = max, Value = initial, TickStyle = TickStyle.None, AutoSize = false,
            AccessibleName = label + Ui.Text("滑块"), SmallChange = 1, LargeChange = 5 };
        Controls.AddRange([caption, number, slider]);
        number.ValueChanged += (_, _) => Change((int)number.Value);
        slider.MouseDown += (_, _) => { dragging = true; GestureStarted?.Invoke(); };
        slider.MouseUp += (_, _) => dragging = false;
        slider.MouseCaptureChanged += (_, _) => { if (!slider.Capture) dragging = false; };
        slider.ValueChanged += (_, _) => Change(slider.Value);
    }
    private (int numberWidth, int rowHeight, int trackHeight, bool stacked) Metrics(int width)
    {
        int gap = LayoutUi.Px(this, 8);
        int n = Math.Min(Math.Max(LayoutUi.Px(this, 90), TextRenderer.MeasureText("−1000", Font).Width + LayoutUi.Px(this, 28)), Math.Max(40, width / 2));
        bool stacked = caption.GetPreferredSize(Size.Empty).Width > width - n - gap;
        int labelWidth = stacked ? width : Math.Max(1, width - n - gap);
        int labelHeight = caption.GetPreferredSize(new Size(labelWidth, 0)).Height;
        return (n, Math.Max(labelHeight, stacked ? 0 : number.PreferredHeight), Math.Max(number.PreferredHeight, LayoutUi.Px(this, 30)), stacked);
    }
    public override Size GetPreferredSize(Size proposedSize)
    {
        if (caption is null) return base.GetPreferredSize(proposedSize);
        int width = Math.Min(proposedSize.Width > 0 ? proposedSize.Width : Width, MaximumSize.Width > 0 ? MaximumSize.Width : Width);
        var m = Metrics(width); return new Size(width, m.rowHeight + m.trackHeight + LayoutUi.Px(this, 5));
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e); if (caption is null) return;
        var m = Metrics(ClientSize.Width); int gap = LayoutUi.Px(this, 8), below = m.rowHeight + LayoutUi.Px(this, 3);
        caption.SetBounds(0, 0, m.stacked ? ClientSize.Width : Math.Max(1, ClientSize.Width - m.numberWidth - gap), m.rowHeight);
        number.SetBounds(ClientSize.Width - m.numberWidth, m.stacked ? below : 0, m.numberWidth, number.PreferredHeight);
        slider.SetBounds(0, below, m.stacked ? Math.Max(1, ClientSize.Width - m.numberWidth - gap) : ClientSize.Width, m.trackHeight);
    }
    private void Change(int value) { if (syncing) return; SetValue(value); Edited?.Invoke(value, !dragging); }
    public void RefreshLanguage()
    {
        string label = Ui.Text(labelKey); caption.Text = label + (unit.Length > 0 ? " / " + unit : "");
        number.AccessibleName = label; slider.AccessibleName = label + Ui.Text("滑块"); Parent?.PerformLayout();
    }
    internal void SetRange(int min, int max)
    { syncing = true; number.Minimum = min; number.Maximum = max; slider.Minimum = min; slider.Maximum = max; syncing = false; }
    internal void SetValue(double value)
    { syncing = true; number.Value = Math.Clamp((decimal)Math.Round(value), number.Minimum, number.Maximum); slider.Value = (int)number.Value; syncing = false; }
}
