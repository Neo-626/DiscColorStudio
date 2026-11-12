namespace DiscColorStudio;
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            // Resume an already-generated image using the language chosen in the editor.
            if(args.Length==2&&args[0]=="--burn-iso")
            {Ui.SetLanguage(Ui.LoadPreference());using var burn=BurnForm.ForExistingIso(args[1]);Application.Run(burn);return 0;}
            using(var language=new LanguageForm(Ui.LoadPreference()))
            {
                if(language.ShowDialog()!=DialogResult.OK)return 0;
                Ui.SetLanguage(language.SelectedLanguage);
                if(!Ui.SavePreference(Ui.Language))MessageBox.Show(Ui.Text("语言选择未能保存，下次启动请重新选择。"));
            }
            if(args.Length>1 || (args.Length==1 && !File.Exists(args[0])))
                throw new ArgumentException(Ui.Text("请提供一个存在的 .discolor 工程文件。"));
            string? path=args.Length==1?args[0]:null;
            var design=path is null?Design.Create(RasterGrid.DefaultSize):Store.Load(path);
            bool changed=false;
            if(!design.HasDisCalibration)
            {
                using var calibration=new CalibrationForm(design,path,allowOpenProject:true);
                if(calibration.ShowDialog()!=DialogResult.OK)return 0;
                design=calibration.Result;path=calibration.ProjectPath;changed=calibration.CalibrationChanged;
            }
            Application.Run(new MainForm(design,path,changed));return 0;
        }
        catch(Exception error){MessageBox.Show(error.Message,Ui.Text("无法启动 Disc Color Studio"),MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
    }
}
