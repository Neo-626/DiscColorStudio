namespace DiscColorStudio;

public sealed class LanguageForm : Form
{
    private readonly RadioButton english=new(){Text="English",AutoSize=true};
    private readonly RadioButton chinese=new(){Text="简体中文",AutoSize=true};
    public string SelectedLanguage => chinese.Checked?"zh-CN":"en";
    public LanguageForm(string preferred)
    {
        Text="Language / 语言";ClientSize=new Size(480,290);Font=new Font("Microsoft YaHei UI",10);
        FormBorderStyle=FormBorderStyle.FixedDialog;StartPosition=FormStartPosition.CenterScreen;MaximizeBox=MinimizeBox=false;
        var panel=new FlowLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(28),FlowDirection=FlowDirection.TopDown,WrapContents=false};
        panel.Controls.Add(new Label{Text="Choose your language / 选择语言",AutoSize=true,Font=new Font(Font,FontStyle.Bold),Margin=new Padding(0,0,0,18)});
        english.Margin=chinese.Margin=new Padding(0,0,0,12);panel.Controls.Add(english);panel.Controls.Add(chinese);
        chinese.Checked=preferred=="zh-CN";english.Checked=!chinese.Checked;
        panel.Controls.Add(new Label{Text="Change it anytime in Help → Language.\n之后可在「帮助 → 语言」中切换。",AutoSize=true,MaximumSize=new Size(420,0),ForeColor=Color.SlateGray,Margin=new Padding(0,2,0,16)});
        var row=new FlowLayoutPanel{AutoSize=true,WrapContents=false};
        var next=new Button{Text="Continue / 继续",AutoSize=true,Height=34,DialogResult=DialogResult.OK};
        var cancel=new Button{Text="Cancel / 取消",AutoSize=true,Height=34,DialogResult=DialogResult.Cancel};
        row.Controls.AddRange([next,cancel]);panel.Controls.Add(row);Controls.Add(panel);AcceptButton=next;CancelButton=cancel;
    }
}
