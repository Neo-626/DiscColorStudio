namespace DiscColorStudio;

public sealed class LanguageForm : ResponsiveForm
{
    private readonly RadioButton english = new() { Text = "English", AutoSize = true };
    private readonly RadioButton chinese = new() { Text = "简体中文", AutoSize = true };
    public string SelectedLanguage => chinese.Checked ? "zh-CN" : "en";
    public LanguageForm(string preferred)
    {
        Text = "Language / 语言"; ClientSize = new Size(500, 310); MinimumSize = new Size(320, 230);
        StartPosition = FormStartPosition.CenterScreen; MaximizeBox = MinimizeBox = false;
        var content = new VerticalStack();
        content.Controls.Add(new Label { Text = "Choose your language / 选择语言", AutoSize = true,
            Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 18) });
        english.Margin = chinese.Margin = new Padding(0, 0, 0, 12);
        content.Controls.Add(english); content.Controls.Add(chinese);
        chinese.Checked = preferred == "zh-CN"; english.Checked = !chinese.Checked;
        content.Controls.Add(new Label { Text = "Change it anytime in Help → Language.\n之后可在「帮助 → 语言」中切换。",
            AutoSize = true, ForeColor = Color.SlateGray, Margin = new Padding(0, 2, 0, 8) });
        var next = new Button { Text = "Continue / 继续", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel / 取消", DialogResult = DialogResult.Cancel };
        Controls.Add(LayoutUi.ScrollBody(content, 24)); Controls.Add(LayoutUi.Buttons(next, cancel));
        AcceptButton = next; CancelButton = cancel; CompleteLayout();
    }
}
