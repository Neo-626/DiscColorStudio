using System.Diagnostics;
using System.Globalization;

namespace DiscColorStudio;

public sealed class CalibrationForm : Form
{
    private readonly TextBox[] fields = Enumerable.Range(0, 6).Select(_ => new TextBox { Width = 330, ReadOnly = true, TabStop = false }).ToArray();
    private readonly Label selectedFile = new() { AutoSize = true, MaximumSize = new Size(590, 0), ForeColor = Color.SlateGray };
    private readonly CheckBox completed = new() { Text = Ui.Text("我已在 DIS 完成这类盘片的几何校准，并将最终参数应用到此工程后保存。"), AutoSize = true, MaximumSize = new Size(590, 0), Enabled = false };
    private readonly Button apply = new() { Text = Ui.Text("应用校准并开始设计"), AutoSize = true, Enabled = false };
    private Geometry? imported;
    private string importedName = "";
    public Design Result { get; private set; }
    public string? ProjectPath { get; private set; }
    public bool CalibrationChanged { get; private set; }

    public CalibrationForm(Design design, string? projectPath = null, bool allowOpenProject = false)
    {
        Result = design.Clone(); ProjectPath = projectPath;
        Text = Ui.Text("开始之前 · 导入 DIS 盘面几何校准");
        ClientSize = new Size(670, Ui.IsEnglish ? 750 : 660); Font = new Font("Microsoft YaHei UI", 10);
        StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 18, 22, 14), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        Controls.Add(panel);
        void Note(string text, bool bold = false) => panel.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(610, 0), Font = bold ? new Font(Font, FontStyle.Bold) : Font, Margin = new Padding(0, 0, 0, 10) });
        Note(Ui.Text("请先在 DiscImageStudio（DIS）完成 DVD 盘面几何校准"), true);
        Note(Ui.Text("1. 使用准备刻录的同型号盘片，在 DIS 完成边界与精确标定。\n2. 将最终标定参数应用到 DVD 绘图工程，再保存工程（Ctrl+S）。\n3. 在下方导入这个工程，例如「我的DVD校准.discproj」。"));
        Note(Ui.Text("需要的文件：.discproj 绘图工程\nDIS 默认保存位置：文档 → DiscImageStudio Projects"), true);
        var links = new FlowLayoutPanel { Width = 610, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 10) };
        var site = new LinkLabel { Text = Ui.Text("打开 DIS 项目与教程"), AutoSize = true, Margin = new Padding(0, 0, 22, 0) };
        site.LinkClicked += (_, _) => { try { Process.Start(new ProcessStartInfo("https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio") { UseShellExecute = true }); } catch (Exception e) { Error(e); } };
        links.Controls.Add(site);
        if (allowOpenProject)
        {
            var open = new LinkLabel { Text = Ui.Text("打开已有的彩色工程…"), AutoSize = true };
            open.LinkClicked += (_, _) => OpenProject(); links.Controls.Add(open);
        }
        panel.Controls.Add(links);
        var import = new Button { Text = Ui.Text("选择 DIS 校准工程（.discproj）…"), AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        import.Click += (_, _) => ImportProject(); panel.Controls.Add(import);
        selectedFile.Text = design.HasDisCalibration ? Ui.Text("当前校准：") + design.CalibrationName + Ui.Text("；选择新工程后可更换。")
            : design.Layers.Count > 0 ? Ui.Text("彩色设计已读取；请补导入 DIS 校准工程，图层和排版会保留。") : Ui.Text("尚未选择 DIS 工程；导入后才能进入设计。");
        selectedFile.Margin = new Padding(0, 0, 0, 8); panel.Controls.Add(selectedFile);
        string[] labels = [Ui.Text("内半径 / mm"), Ui.Text("外半径 / mm"), Ui.Text("扇区数（2048 字节/扇区）"), Ui.Text("轨距一次系数"), Ui.Text("轨距二次系数"), Ui.Text("轨距三次系数")];
        for (int i = 0; i < 6; i++)
        {
            var row = new FlowLayoutPanel { Width = 610, Height = 31, WrapContents = false, Margin = new Padding(0) };
            row.Controls.Add(new Label { Text = labels[i], Width = 235, Height = 28, TextAlign = ContentAlignment.MiddleLeft });
            fields[i].AccessibleName = labels[i]; row.Controls.Add(fields[i]); panel.Controls.Add(row);
        }
        completed.Margin = new Padding(0, 12, 0, 10); panel.Controls.Add(completed);
        completed.CheckedChanged += (_, _) => apply.Enabled = imported is not null && completed.Checked;
        var buttons = new FlowLayoutPanel { Width = 610, AutoSize = true, Margin = new Padding(0) };
        apply.Click += (_, _) =>
        {
            if (imported is null || !completed.Checked) return;
            CalibrationFiles.ApplyImport(Result, imported, importedName);
            CalibrationChanged = true; DialogResult = DialogResult.OK;
        };
        buttons.Controls.Add(apply);
        var cancel = new Button { Text = allowOpenProject ? Ui.Text("退出") : Ui.Text("取消"), AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel); panel.Controls.Add(buttons); CancelButton = cancel; AcceptButton = apply;
    }

    private void ImportProject()
    {
        using var picker = new OpenFileDialog { Filter = Ui.Text("DIS 校准后的 DVD 绘图工程 (*.discproj)|*.discproj"), InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DiscImageStudio Projects") };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        ResetImport();
        try
        {
            imported = CalibrationFiles.ReadDisProject(picker.FileName);
            importedName = Path.GetFileNameWithoutExtension(picker.FileName);
            double[] values = [imported.InnerRadiusMm, imported.OuterRadiusMm, imported.Sectors, imported.PitchLinear, imported.PitchQuadratic, imported.PitchCubic];
            for (int i = 0; i < values.Length; i++) fields[i].Text = values[i].ToString("G17", CultureInfo.InvariantCulture);
            selectedFile.Text = Ui.Text("已读取：") + Path.GetFileName(picker.FileName) + Ui.Text("\n请确认该工程已应用最终标定参数。");
            selectedFile.ForeColor = Color.FromArgb(25, 105, 75); completed.Enabled = true;
        }
        catch (Exception e) { Error(e); }
    }

    private void OpenProject()
    {
        using var picker = new OpenFileDialog { Filter = Ui.Text("彩色盘面工程|*.discolor;*.zip") };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var design = Store.Load(picker.FileName);
            Result = design; ProjectPath = picker.FileName.EndsWith(".discolor", StringComparison.OrdinalIgnoreCase) ? picker.FileName : null;
            if (design.HasDisCalibration) { DialogResult = DialogResult.OK; return; }
            ResetImport(); selectedFile.Text = Ui.Text("已打开：") + Path.GetFileName(picker.FileName) + Ui.Text("\n请补导入 DIS 校准工程，图层和排版会保留。");
        }
        catch (Exception e) { Error(e); }
    }

    private void ResetImport()
    {
        imported = null; completed.Checked = false; completed.Enabled = false; apply.Enabled = false;
        foreach (var field in fields) field.Clear();
        selectedFile.ForeColor = Color.SlateGray; selectedFile.Text = Ui.Text("尚未导入有效的 DIS DVD 工程。");
    }
    private void Error(Exception e) => MessageBox.Show(this, e.Message, Ui.Text("校准参数未应用"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
