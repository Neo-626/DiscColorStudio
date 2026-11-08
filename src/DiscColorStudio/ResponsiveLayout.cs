namespace DiscColorStudio;

// All dimensions supplied by the application are authored at 96 DPI.
public class ResponsiveForm : Form
{
    protected ResponsiveForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 10);
    }
    protected void CompleteLayout() => ResumeLayout(true);
    protected override void OnLoad(EventArgs e) { base.OnLoad(e); FitWorkingArea(); }
    protected override void OnShown(EventArgs e) { base.OnShown(e); FitWorkingArea(); }
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)FitWorkingArea);
    }
    private void FitWorkingArea()
    {
        if (WindowState != FormWindowState.Normal) return;
        var area = Screen.FromControl(Owner ?? this).WorkingArea;
        int inset = Math.Min(12, Math.Min(area.Width, area.Height) / 20);
        area.Inflate(-inset, -inset);
        MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
        Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        Location = new Point(Math.Clamp(Left, area.Left, area.Right - Width), Math.Clamp(Top, area.Top, area.Bottom - Height));
    }
}

internal class VerticalStack : FlowLayoutPanel
{
    private bool fitting;
    internal VerticalStack()
    {
        FlowDirection = FlowDirection.TopDown; WrapContents = false;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Dock = DockStyle.Top;
        Margin = Padding.Empty;
    }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); FitChildren(); }
    protected override void OnPaddingChanged(EventArgs e) { base.OnPaddingChanged(e); FitChildren(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitChildren(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); FitChildren(); }
    protected override void OnControlAdded(ControlEventArgs e)
    {
        if (e.Control is { } child)
        {
            child.Dock = DockStyle.None; child.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            if (child is UpDownBase or ComboBox || child is TextBox { Multiline: false }) child.AutoSize = true;
        }
        base.OnControlAdded(e); FitChildren();
    }
    private void FitChildren()
    {
        if (fitting) return;
        fitting = true;
        try
        {
            int available = Math.Max(1, ClientSize.Width - Padding.Horizontal);
            foreach (Control child in Controls)
            {
                int width = Math.Max(1, available - child.Margin.Horizontal);
                int height = child switch
                {
                    UpDownBase input => input.PreferredHeight,
                    ComboBox input => input.PreferredHeight,
                    TextBox input when !input.Multiline => input.PreferredHeight,
                    _ => child.MinimumSize.Height
                };
                // Fixed-height native inputs must retain their specified height in the flow layout.
                var minimum = new Size(width, height);
                if (child.MinimumSize != minimum || child.MaximumSize.Width != width)
                {
                    child.MinimumSize = Size.Empty;
                    child.MaximumSize = new Size(width, 0);
                    child.MinimumSize = minimum;
                }
                if (child.Width != width) child.Width = width;
                if (child is UpDownBase or ComboBox || child is TextBox { Multiline: false }) child.Height = height;
            }
        }
        finally { fitting = false; }
    }
}

internal sealed class WrappingFlowPanel : FlowLayoutPanel
{
    private bool fitting;
    protected override void OnLayout(LayoutEventArgs e)
    {
        if (fitting) return;
        fitting = true;
        try
        {
            int available = Math.Max(1, ClientSize.Width - Padding.Horizontal);
            foreach (Control child in Controls)
                child.MaximumSize = new Size(Math.Max(1, available - child.Margin.Horizontal), 0);
            base.OnLayout(e);
        }
        finally { fitting = false; }
    }
}

internal sealed class WrappingCheckBox : CheckBox
{
    public override Size GetPreferredSize(Size proposedSize)
    {
        int glyph = LayoutUi.Px(this, 22);
        int width = Math.Max(glyph + 1, Math.Min(proposedSize.Width > 0 ? proposedSize.Width : Width, MaximumSize.Width > 0 ? MaximumSize.Width : Width));
        var text = TextRenderer.MeasureText(Text, Font, new Size(Math.Max(1, width - glyph - Padding.Horizontal), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        return new Size(width, Math.Max(LayoutUi.Px(this, 20), text.Height + LayoutUi.Px(this, 4)) + Padding.Vertical);
    }

}

internal static class LayoutUi
{
    internal static int Dpi(Control control) => control.FindForm()?.DeviceDpi ?? control.DeviceDpi;
    internal static int Px(Control control, int logical) => (int)Math.Round(logical * Dpi(control) / 96.0);
    internal static FlowLayoutPanel Buttons(params Control[] buttons)
    {
        var row = new WrappingFlowPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 8, 12, 8), WrapContents = true, Margin = Padding.Empty };
        foreach (var button in buttons)
        {
            button.AutoSize = true; button.MinimumSize = new Size(80, 34); button.Padding = new Padding(8, 3, 8, 3);
            button.Margin = new Padding(3); row.Controls.Add(button);
        }
        return row;
    }
    internal static TableLayoutPanel InputRow(Control input, Control action)
    {
        var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top, Margin = new Padding(0, 3, 0, 5) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        input.Dock = DockStyle.Fill; action.Anchor = AnchorStyles.Left | AnchorStyles.Top; action.AutoSize = true;
        row.Controls.Add(input, 0, 0); row.Controls.Add(action, 1, 0); return row;
    }
    internal static Panel ScrollBody(VerticalStack content, int padding = 20)
    {
        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(padding) };
        panel.Controls.Add(content); return panel;
    }
}

public sealed class TextEditForm : ResponsiveForm
{
    private readonly TextBox input;
    public string EditedText => input.Text;
    public TextEditForm(string text)
    {
        Text = Ui.Text("编辑文字"); ClientSize = new Size(480, 270); MinimumSize = new Size(300, 200);
        StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;
        input = new TextBox { Multiline = true, Dock = DockStyle.Fill, Text = text, ScrollBars = ScrollBars.Vertical };
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) }; body.Controls.Add(input);
        var ok = new Button { Text = Ui.Text("确定"), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = Ui.Text("取消"), DialogResult = DialogResult.Cancel };
        Controls.Add(body); Controls.Add(LayoutUi.Buttons(ok, cancel)); AcceptButton = ok; CancelButton = cancel;
        CompleteLayout();
    }
}
