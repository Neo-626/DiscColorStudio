namespace DiscColorStudio;

internal sealed class EditorWorkspace : UserControl
{
    private readonly SplitContainer left = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, Panel1MinSize = 0, Panel2MinSize = 0, TabStop = true };
    private readonly SplitContainer right = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, Panel1MinSize = 0, Panel2MinSize = 0, TabStop = true };
    private double preferredLeft = 200, preferredRight = 340;
    private bool arranging, dragging;
    internal EditorWorkspace(Control layers, Control canvas, Control inspector)
    {
        AutoScaleMode = AutoScaleMode.Inherit; Dock = DockStyle.Fill;
        left.Size = right.Size = new Size(1200, 700);
        left.AccessibleName = Ui.Text("图层"); right.AccessibleName = Ui.Text("纹理与细节");
        left.Panel1.Controls.Add(layers); left.Panel2.Controls.Add(right);
        right.Panel1.Controls.Add(canvas); right.Panel2.Controls.Add(inspector); Controls.Add(left);
        left.SplitterMoving += (_, e) => { dragging = true; e.SplitX = Math.Clamp(e.SplitX, LeftMinimum, Math.Max(LeftMinimum, left.Width - left.SplitterWidth - RightMinimum - CanvasMinimum)); };
        right.SplitterMoving += (_, e) => { dragging = true; e.SplitX = Math.Clamp(e.SplitX, CanvasMinimum, Math.Max(CanvasMinimum, right.Width - right.SplitterWidth - RightMinimum)); };
        left.SplitterMoved += (_, _) => Moved(); right.SplitterMoved += (_, _) => Moved();
    }
    private int LeftMinimum => Math.Min(LayoutUi.Px(this, 130), Math.Max(0, Width / 5));
    private int RightMinimum => Math.Min(LayoutUi.Px(this, 230), Math.Max(0, Width * 2 / 5));
    private int CanvasMinimum => Math.Min(LayoutUi.Px(this, 90), Math.Max(0, Width / 6));
    private void Moved()
    {
        if (arranging || !dragging) return; dragging = false;
        preferredLeft = left.SplitterDistance * 96.0 / LayoutUi.Dpi(this);
        preferredRight = right.Panel2.Width * 96.0 / LayoutUi.Dpi(this);
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e); if (arranging || dragging || Width < 30) return;
        arranging = true;
        try
        {
            left.SplitterWidth = right.SplitterWidth = Math.Max(6, LayoutUi.Px(this, 7));
            int available = Math.Max(0, left.Width - left.SplitterWidth);
            left.SplitterDistance = Math.Clamp((int)Math.Round(preferredLeft * LayoutUi.Dpi(this) / 96), Math.Min(LeftMinimum, available), Math.Max(Math.Min(LeftMinimum, available), available - RightMinimum - CanvasMinimum));
            available = Math.Max(0, right.Width - right.SplitterWidth);
            int desiredRight = Math.Clamp((int)Math.Round(preferredRight * LayoutUi.Dpi(this) / 96), Math.Min(RightMinimum, available), Math.Max(Math.Min(RightMinimum, available), available - CanvasMinimum));
            right.SplitterDistance = available - desiredRight;
        }
        finally { arranging = false; }
    }
}
