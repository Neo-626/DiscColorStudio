using System.Drawing.Drawing2D;

namespace DiscColorStudio;

public sealed class DiscCanvas : Control
{
    public const int RasterSize = RasterGrid.PreviewSize;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Design Document { get; set; } = null!;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? SelectedId { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int ViewMode { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public LadderView Simulation { get; set; } = new();
    public event Action<string?>? SelectionChanged;
    public bool DrawingObservation { get; private set; }
    public event Action? ObservationEdited;
    private string? directionLayerId;
    private PointF directionEnd;
    public void BeginObservationArrow()
    {
        if(!Document.UsesLadder || Selected is null)return;
        CancelObservationArrow();DrawingObservation=true;directionLayerId=SelectedId;Cursor=Cursors.Cross;Focus();Invalidate();
    }
    public void CancelObservationArrow()
    {
        if(operation=="direction")operation="";
        DrawingObservation=false;directionLayerId=null;Capture=false;Cursor=Cursors.Default;Invalidate();
    }
    private PointF ArrowEnd(Layer layer)
    {
        double a=LadderViewing.ArrowAngle(layer)*Math.PI/180,length=Math.Clamp(Math.Min(layer.Width,layer.Height)*.45,8,22);
        return new((float)(layer.X+Math.Cos(a)*length),(float)(layer.Y+Math.Sin(a)*length));
    }
    public event Action? BeginEdit;
    public event Action? LayerEdited;
    public event Action? ViewportChanged;
    public event Action<string>? CursorPositionChanged;
    public event Action<string>? RenderStatus;
    public event Action<AngleLayerPlan[]>? AnglePlansReady;
    private Bitmap? preview;
    private int previewGrid;
    private bool previewInteractive,activeInteractive;
    private long lastShownRevision;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ProgressivePreview { get; set; } = true;
    private readonly System.Windows.Forms.Timer refine = new(){Interval=350};
    private RectangleF previewWorld = new(-60, -60, 120, 120);
    private readonly Renderer renderer = new(true);
    private CancellationTokenSource? activePreview;
    private sealed record PreviewRequest(Design Source,Design Design,int Size,Rectangle Region,int Mode,long Revision,bool Interactive,LadderView Simulation);
    private PreviewRequest? pendingPreview;
    private PreviewRequest? refinement;
    private long revision;
    private bool rendering, closing;
    public bool IsRendering => rendering;
    private float zoom = 1;
    private PointF pan;
    private Point downScreen;
    private PointF downWorld;
    private string operation = "";
    private Layer? start;
    public Layer? Selected => Document.Layers.FirstOrDefault(l => l.Id == SelectedId);
    private float Unit => Math.Max(1, Math.Min(ClientSize.Width, ClientSize.Height) - 70) / 120f * zoom;
    private PointF Origin => new(ClientSize.Width / 2f + pan.X, ClientSize.Height / 2f + pan.Y);
    public DiscCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(235, 239, 244); TabStop = true;
        refine.Tick+=(_,_)=>
        {
            refine.Stop();
            if(refinement is not {} request || request.Revision!=revision || closing)return;
            pendingPreview=request;
            if(!rendering)_=ProcessPreviewQueue();
        };
    }
    public PointF ToWorld(Point p) => new((p.X - Origin.X) / Unit, (p.Y - Origin.Y) / Unit);
    public PointF ToScreen(PointF p) => new(Origin.X + p.X * Unit, Origin.Y + p.Y * Unit);
    public void FitDisc() { zoom = 1; pan = PointF.Empty; NotifyViewport(); Invalidate(); }
    public void FocusDesign()
    {
        zoom = 4;
        if (Selected is { } inspection)
        {
            double radians = inspection.Rotation * Math.PI / 180;
            double width = Math.Abs(Math.Cos(radians)) * inspection.Width + Math.Abs(Math.Sin(radians)) * inspection.Height;
            double height = Math.Abs(Math.Sin(radians)) * inspection.Width + Math.Abs(Math.Cos(radians)) * inspection.Height;
            float baseUnit = Math.Max(1, Math.Min(ClientSize.Width, ClientSize.Height) - 70) / 120f;
            zoom = (float)Math.Clamp(Math.Min((ClientSize.Width - 100) / width, (ClientSize.Height - 100) / height) / baseUnit, 1, 32);
        }
        var center = Selected is { } l ? new PointF((float)l.X, (float)l.Y) : new PointF(0, -40);
        pan = new PointF(-center.X * Unit, -center.Y * Unit); NotifyViewport(); Invalidate();
    }
    public void RefreshArtwork()
    {
        if (closing || Document is null || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        int[] previewSizes = [1024, 2048, .. RasterGrid.Sizes];
        int requested = previewSizes.FirstOrDefault(s => s >= Unit * 120 * 1.4, RasterGrid.Sizes[^1]);
        int size = Math.Min(requested, Document.ExportRasterSize);
        var from = ToWorld(Point.Empty); var to = ToWorld(new Point(ClientSize.Width, ClientSize.Height));
        Rectangle Region(int grid)
        {
            double unit=grid/120.0;
            return Rectangle.FromLTRB(Math.Clamp((int)Math.Floor((from.X + 60) * unit), 0, grid),
                Math.Clamp((int)Math.Floor((from.Y + 60) * unit), 0, grid),
                Math.Clamp((int)Math.Ceiling((to.X + 60) * unit), 0, grid),
                Math.Clamp((int)Math.Ceiling((to.Y + 60) * unit), 0, grid));
        }
        if(!activeInteractive)activePreview?.Cancel();
        int quick=InteractiveGrid(size,Unit);
        var request=new PreviewRequest(Document,Document.Clone(),size,Region(size),ViewMode,++revision,false,Simulation);
        bool progressive=ProgressivePreview;
        pendingPreview = progressive ? request with {Size=quick,Region=Region(quick),Interactive=true} : request;
        refine.Stop();refinement=progressive?request:null;
        if(progressive)refine.Start();
        if (!rendering) _ = ProcessPreviewQueue();
    }
    internal static int InteractiveGrid(int exact,float pixelsPerMm)
    {
        // Intermediate grids below 4096 are safe: every supported image export
        // grid is >=4096, so LayeredRaster's native-size ratio remains exactly 1.
        int grid=384;double target=pixelsPerMm*120*.65;
        foreach(int candidate in new[]{512,768,1024,1536,2048,3072,4096,8192})
            if(candidate<=target)grid=candidate;
        return Math.Min(grid,exact);
    }
    public void InvalidateArtwork()
    {
        revision++;pendingPreview=null;refinement=null;refine.Stop();
        // Short interactive frames may finish so a continuous drag does not starve
        // the display. Heavy refinements are cancelled immediately. The queue holds
        // only the newest snapshot, and frames are never shown out of order.
        if(!activeInteractive)activePreview?.Cancel();
    }
    private async Task ProcessPreviewQueue()
    {
        rendering = true;
        try
        {
            while (!closing && pendingPreview is { } request)
            {
                pendingPreview = null;
                RenderStatus?.Invoke(request.Interactive?Ui.Text("正在更新交互预览…"):Ui.Text("正在补全精细预览，可继续编辑…"));
                Bitmap? next = null;
                using var cancellation=new CancellationTokenSource();
                activePreview=cancellation;
                activeInteractive=request.Interactive;
                renderer.Cancellation=cancellation.Token;
                renderer.InteractivePreview=request.Interactive;
                try
                {
                    next = await Task.Run(() => RenderPreview(request.Design, renderer, request.Size, request.Region, request.Mode,request.Simulation));
                    if (closing || !ReferenceEquals(Document,request.Source) || (request.Mode!=ViewMode || request.Simulation!=Simulation)
                        || request.Revision<lastShownRevision || (!request.Interactive && request.Revision != revision))
                    { next?.Dispose(); continue; }
                    double unit = request.Size / 120.0;
                    previewInteractive=request.Interactive;lastShownRevision=request.Revision;
                    SetPreview(next, request.Size, new RectangleF((float)(request.Region.X / unit - 60), (float)(request.Region.Y / unit - 60),
                        (float)(request.Region.Width / unit), (float)(request.Region.Height / unit)));
                    if(request.Design.HasAngleColor)AnglePlansReady?.Invoke(renderer.AngleColors.LastPlans);
                    string engineStatus=Ui.Text(request.Design.UsesLadder?"周期梯保色":"连续彩色");
                    RenderStatus?.Invoke(request.Interactive
                        ? Ui.Format("正在更新预览 · {0}",engineStatus)
                        : request.Design.UsesLadder?Ui.Format("周期梯模拟 · 导出 {0:N0} 格",request.Design.ExportRasterSize):Ui.Format("精细预览 · {0} · 导出 {1:N0} 格",engineStatus,request.Design.ExportRasterSize));
                }
                catch (OperationCanceledException) { next?.Dispose(); }
                catch (Exception ex) { next?.Dispose(); if (!closing) RenderStatus?.Invoke(Ui.Text("预览未完成：") + ex.Message); }
                finally { activePreview=null;activeInteractive=false;renderer.Cancellation=default;renderer.InteractivePreview=false; }
            }
        }
        finally { rendering = false; if (closing) renderer.Dispose(); }
    }
    internal static Bitmap? RenderPreview(Design design, Renderer renderer, int size, Rectangle region, int mode,LadderView? simulation=null)
    {
        if (region.Width <= 0 || region.Height <= 0) return null;
        if(design.UsesLadder && mode!=2)return LadderEngine.Render(design,renderer,size,region,simulation);
        if(design.HasAngleColor){if(mode!=2)return renderer.AngleColors.Render(design,renderer,size,region,mode);renderer.AngleColors.Plans(design,renderer);}
        var detail = new TiledExport(design, size);
        if (design.HasGrayLayers || mode==4) return LayerPreview.Preview(design, renderer, size, region, mode);
        if (mode == 2) return detail.CompositeRegion(renderer, region);
        byte[] pixels = detail.RenderRegion(renderer, region);
        var next = Renderer.Colorize(design, pixels, region.Width, region.Height, mode == 1 ? 1 : design.MatchGain);
        return next;
    }

    private void SetPreview(Bitmap? bitmap, int size, RectangleF bounds)
    {
        var old = preview;
        preview = bitmap;
        previewGrid = size;
        previewWorld = bounds;
        old?.Dispose();
        Invalidate();
    }

    private void NotifyViewport()
    {
        if (Document is not null)
            ViewportChanged?.Invoke();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        NotifyViewport();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); if (Document is null) return;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var o = Origin; float unit = Unit;
        g.TranslateTransform(o.X, o.Y); g.ScaleTransform(unit, unit);
        using var body = new SolidBrush(Color.FromArgb(213, 220, 230));
        g.FillEllipse(body, -60, -60, 120, 120);
        using var ring = new GraphicsPath(FillMode.Alternate);
        float ro = (float)Document.Geometry.OuterRadiusMm, ri = (float)Document.Geometry.InnerRadiusMm;
        ring.AddEllipse(-ro, -ro, ro * 2, ro * 2); ring.AddEllipse(-ri, -ri, ri * 2, ri * 2);
        var state = g.Save(); g.SetClip(ring);
        using var background = new SolidBrush(Document.Palette.Single(p => p.Id == Document.BackgroundId).Color(ViewMode == 1 ? 1 : Document.MatchGain));
        g.FillRectangle(background, -60, -60, 120, 120);
        if (preview is not null)
        {
            g.InterpolationMode = previewInteractive ? InterpolationMode.HighQualityBilinear : unit * previewWorld.Width >= preview.Width
                ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.DrawImage(preview, previewWorld);
        }
        g.Restore(state);
        using var edge = new Pen(Color.FromArgb(152, 165, 184), 1 / unit);
        g.DrawEllipse(edge, -60, -60, 120, 120); g.DrawEllipse(edge, -ro, -ro, ro * 2, ro * 2); g.DrawEllipse(edge, -ri, -ri, ri * 2, ri * 2);
        using var hole = new SolidBrush(BackColor); g.FillEllipse(hole, -7.5f, -7.5f, 15, 15); g.DrawEllipse(edge, -7.5f, -7.5f, 15, 15);
        if (Selected is { } selected)
        {
            var s = g.Save(); g.TranslateTransform((float)selected.X, (float)selected.Y); g.RotateTransform((float)selected.Rotation);
            float w = (float)selected.Width, h = (float)selected.Height;
            using var outline = new Pen(Color.FromArgb(45, 134, 229), 1.7f / unit);
            g.DrawRectangle(outline, -w / 2, -h / 2, w, h);
            float handle = 8 / unit;
            foreach (var p in new[] { new PointF(-w / 2, -h / 2), new PointF(w / 2, -h / 2), new PointF(w / 2, h / 2), new PointF(-w / 2, h / 2) })
            { g.FillRectangle(Brushes.White, p.X - handle / 2, p.Y - handle / 2, handle, handle); g.DrawRectangle(outline, p.X - handle / 2, p.Y - handle / 2, handle, handle); }
            g.DrawLine(outline, 0, -h / 2, 0, -h / 2 - 20 / unit);
            g.FillEllipse(Brushes.White, -handle / 2, -h / 2 - 24 / unit, handle, handle);
            g.DrawEllipse(outline, -handle / 2, -h / 2 - 24 / unit, handle, handle);
            g.Restore(s);
            if(Document.UsesLadder && (selected.LadderViewing is not null || selected.LadderPlacement is not null || selected.LadderObservation is not null || operation=="direction"))
            {
                bool draft=operation=="direction" && directionLayerId==selected.Id;
                PointF from=draft?downWorld:new((float)selected.X,(float)selected.Y),to=draft?directionEnd:ArrowEnd(selected);
                using var border=new Pen(Color.FromArgb(210,255,255,255),5/unit);
                using var arrow=new Pen(Color.FromArgb(0,155,170),2.6f/unit){EndCap=LineCap.ArrowAnchor};
                g.DrawLine(border,from,to);g.DrawLine(arrow,from,to);
                g.FillEllipse(Brushes.White,from.X-3/unit,from.Y-3/unit,6/unit,6/unit);
                g.DrawEllipse(arrow,to.X-4/unit,to.Y-4/unit,8/unit,8/unit);
            }
        }
        g.ResetTransform();
        using var font = new Font("Microsoft YaHei UI", 9);
        string quality=previewInteractive?Ui.Text("交互预览 · 等待精细"):Ui.Text("精细预览");
        string status = Ui.Format("{0:0}% · {1} · 预览 {2:N0} · 导出 {3:N0} · 滚轮缩放 / 中键平移",zoom*100,quality,previewGrid,Document.ExportRasterSize);
        g.DrawString(status, font, Brushes.SlateGray, 12, ClientSize.Height - 28);
        float rulerWidth = 10 * unit;
        if (rulerWidth < ClientSize.Width / 2)
        { float x = ClientSize.Width - rulerWidth - 25, y = ClientSize.Height - 45; g.DrawLine(Pens.SlateGray, x, y, x + rulerWidth, y); g.DrawLine(Pens.SlateGray, x, y - 3, x, y + 3); g.DrawLine(Pens.SlateGray, x + rulerWidth, y - 3, x + rulerWidth, y + 3); g.DrawString("10 mm", font, Brushes.SlateGray, x, y - 20); }
    }
    private static PointF Local(Layer l, PointF p)
    {
        double angle = -l.Rotation * Math.PI / 180, x = p.X - l.X, y = p.Y - l.Y;
        return new PointF((float)(x * Math.Cos(angle) - y * Math.Sin(angle)), (float)(x * Math.Sin(angle) + y * Math.Cos(angle)));
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); Focus(); downScreen = e.Location; downWorld = ToWorld(e.Location);
        if (e.Button is MouseButtons.Middle or MouseButtons.Right) { operation = "pan"; Capture = true; return; }
        if (e.Button != MouseButtons.Left) return;
        if(Document.UsesLadder && Selected is {} directionLayer)
        {
            bool handle=false;
            if(directionLayer.LadderViewing is not null || directionLayer.LadderPlacement is not null || directionLayer.LadderObservation is not null)
            {var end=ToScreen(ArrowEnd(directionLayer));handle=Math.Pow(end.X-e.X,2)+Math.Pow(end.Y-e.Y,2)<144;}
            if(DrawingObservation && directionLayerId==SelectedId || handle)
            {
                var p=Local(directionLayer,downWorld);
                if(!handle && (Math.Abs(p.X)>directionLayer.Width/2 || Math.Abs(p.Y)>directionLayer.Height/2))return;
                directionLayerId=SelectedId;DrawingObservation=true;
                if(handle)downWorld=new((float)directionLayer.X,(float)directionLayer.Y);
                directionEnd=ToWorld(e.Location);operation="direction";Capture=true;Invalidate();return;
            }
        }
        if (Selected is { } layer)
        {
            PointF p = Local(layer, downWorld); double tolerance = 11 / Unit;
            if (Math.Abs(p.X) < tolerance && Math.Abs(p.Y + layer.Height / 2 + 20 / Unit) < tolerance) operation = "rotate";
            else if (Math.Abs(Math.Abs(p.X) - layer.Width / 2) < tolerance && Math.Abs(Math.Abs(p.Y) - layer.Height / 2) < tolerance) operation = "resize";
        }
        if (operation.Length == 0)
        {
            var hit = Document.Layers.AsEnumerable().Reverse().FirstOrDefault(l => l.Visible && Math.Abs(Local(l, downWorld).X) <= l.Width / 2 && Math.Abs(Local(l, downWorld).Y) <= l.Height / 2);
            SelectedId = hit?.Id; SelectionChanged?.Invoke(SelectedId); operation = hit is null ? "" : "move";
        }
        if (operation.Length > 0 && Selected is { } selected)
        { start = selected.Copy(); BeginEdit?.Invoke(); Capture = true; }
        Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); var world = ToWorld(e.Location); CursorPositionChanged?.Invoke($"X {world.X:0.00}   Y {world.Y:0.00} mm");
        if (operation == "pan") { pan.X += e.X - downScreen.X; pan.Y += e.Y - downScreen.Y; downScreen = e.Location; NotifyViewport(); Invalidate(); return; }
        if(operation=="direction"){directionEnd=world;Invalidate();return;}
        if (start is null || Selected is not { } l || operation.Length == 0) return;
        if (operation == "move") { l.X = Math.Clamp(start.X + world.X - downWorld.X, -150, 150); l.Y = Math.Clamp(start.Y + world.Y - downWorld.Y, -150, 150); }
        if (operation == "rotate")
        {
            double a = Math.Atan2(world.Y - l.Y, world.X - l.X) - Math.Atan2(downWorld.Y - l.Y, downWorld.X - l.X);
            l.Rotation = start.Rotation + a * 180 / Math.PI;
            if ((ModifierKeys & Keys.Shift) != 0) l.Rotation = Math.Round(l.Rotation / 15) * 15;
        }
        if (operation == "resize")
        {
            var p = Local(start, world);
            double w = Math.Clamp(2 * Math.Abs(p.X), RasterGrid.MinLayerSizeMm, 200);
            double h = Math.Clamp(2 * Math.Abs(p.Y), RasterGrid.MinLayerSizeMm, 200);
            if (l.KeepAspect || (ModifierKeys & Keys.Shift) != 0)
            {
                double scale = Math.Max(w / start.Width, h / start.Height);
                w = Math.Clamp(start.Width * scale, RasterGrid.MinLayerSizeMm, 200);
                h = Math.Clamp(start.Height * scale, RasterGrid.MinLayerSizeMm, 200);
            }
            l.Width = w; l.Height = h;
        }
        LayerEdited?.Invoke(); Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if(operation=="direction")
        {
            var end=ToWorld(e.Location);double dx=end.X-downWorld.X,dy=end.Y-downWorld.Y;
            if(e.Button==MouseButtons.Left && Selected is {} layer && layer.Id==directionLayerId && Math.Sqrt(dx*dx+dy*dy)*Unit>=8)
            {
                BeginEdit?.Invoke();double a=Math.Atan2(dy,dx)*180/Math.PI;
                if((ModifierKeys&Keys.Shift)!=0)a=Math.Round(a/15)*15;
                LadderViewing.SetArrow(layer,a);
                LayerEdited?.Invoke();ObservationEdited?.Invoke();
            }
            CancelObservationArrow();
        }
        operation="";start=null;Capture=false;
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {base.OnMouseCaptureChanged(e);if(!Capture && operation=="direction")CancelObservationArrow();}
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    { base.OnMouseDoubleClick(e); if (Selected is not null) FocusDesign(); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var before = ToWorld(e.Location);
        zoom = Math.Clamp(zoom * (e.Delta > 0 ? 1.15f : 1 / 1.15f), .5f,
            RasterGrid.IsTiled(Document.ExportRasterSize) ? 512 : 20);
        var after = ToScreen(before);
        pan.X += e.X - after.X;
        pan.Y += e.Y - after.Y;
        NotifyViewport();
        Invalidate();
    }
    protected override void Dispose(bool disposing)
    { if (disposing) { closing = true; revision++;pendingPreview=null;refinement=null;refine.Dispose();activePreview?.Cancel(); preview?.Dispose(); if (!rendering) renderer.Dispose(); } base.Dispose(disposing); }
}
