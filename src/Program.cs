using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
namespace BrightnessToggle;

internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        L.Language=Config.Load().Language;
        if(args.Length == 2 && args[0] == "--self-test") { Tests.Run(args[1]); return; }
        using var mutex = new Mutex(true, @"Local\BrightnessToggle.WinForms", out bool first);
        if(!first) { if(StartupSettings.IsStartup(args))return;MessageBox.Show(L.T("이미 실행 중입니다. 트레이의 밝기 전환 아이콘을 확인하세요."), "Brightness Toggle", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        try { Application.Run(new MainForm(startInTray:StartupSettings.IsStartup(args))); }
        catch(Exception ex) { MessageBox.Show(L.T("실행 실패: ") + ex.Message, "Brightness Toggle", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
internal sealed class Preset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = L.T("새 프리셋");
    public int Amount { get; set; } = 25;
    public int BlackPreservation { get; set; }
    public int Contrast { get; set; }
    public string BuiltIn { get; set; } = "";
    public string DisplayName => BuiltIn switch { "basic"=>L.T("기본"), "level1"=>L.T("밝기 1단계"), "level2"=>L.T("밝기 2단계"), "level3"=>L.T("밝기 3단계"), _=>Name };
    public override string ToString() => $"{DisplayName}   ({Amount:+0;-0;0}%)";
}
internal sealed class Config
{
    public List<Preset> Presets { get; set; } = Defaults();
    public int PresetSchema { get; set; }
    internal static List<Preset> Defaults() => [new(){Name="default",BuiltIn="basic",Amount=0},new(){Name="Brightness level 1",BuiltIn="level1",Amount=25,BlackPreservation=15,Contrast=5},new(){Name="Brightness level 2",BuiltIn="level2",Amount=50,BlackPreservation=25,Contrast=10},new(){Name="Brightness level 3",BuiltIn="level3",Amount=75,BlackPreservation=35,Contrast=15}];
    public bool AutoSwitchEnabled { get; set; }
    public string AutoProcessName { get; set; } = "";
    public string AutoPresetId { get; set; } = "";
    public int Hotkey { get; set; } = (int)Keys.F9;
    public string Monitor { get; set; } = "";
    public string Language { get; set; } = "en";
    internal static readonly string FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BrightnessToggle","settings.json");
    internal static Config Load()
    {
        try { var c=JsonSerializer.Deserialize<Config>(File.ReadAllText(FileName)) ?? new(); c.Normalize(); return c; } catch { return new(); }
    }
    internal void Normalize()
    {
        Language=Language=="ko"?"ko":"en";
        Presets ??= []; Presets=Presets.Where(p=>p!=null).ToList();
        if(Presets.Count==0 || (Presets[0].Name!="default" && Presets[0].BuiltIn!="basic")) Presets.Insert(0,Defaults()[0]);
        if(PresetSchema<1)
        {
            // Replace only the exact legacy built-in; preserve all custom presets and their order.
            if(Presets.Count>1 && Presets[1].BuiltIn=="" && ((Presets[1].Name=="+80%" && Presets[1].Amount==80)||(Presets[1].Name=="+25%" && Presets[1].Amount==25))) Presets.RemoveAt(1);
            var defaults=Defaults();
            for(int i=1;i<defaults.Count;i++) if(!Presets.Any(p=>p.BuiltIn==defaults[i].BuiltIn)) Presets.Insert(i,defaults[i]);
            PresetSchema=1;
        }
        if(PresetSchema<2)
        {
            if(!Presets.Any(p=>p.BuiltIn=="level3"))
            {
                int after=Presets.FindLastIndex(p=>p.BuiltIn is "level1" or "level2");
                Presets.Insert(Math.Max(1,after+1),Defaults()[3]);
            }
            PresetSchema=2;
        }
        Presets[0].BuiltIn="basic";Presets[0].Amount=Presets[0].BlackPreservation=Presets[0].Contrast=0;
        foreach(var p in Presets) { p.Name=string.IsNullOrWhiteSpace(p.Name)?L.T("프리셋"):p.Name.Trim(); p.Amount=Math.Clamp(p.Amount,-50,100);p.BlackPreservation=Math.Clamp(p.BlackPreservation,0,100);p.Contrast=Math.Clamp(p.Contrast,0,50); }
        var ids=new HashSet<string>();
        foreach(var p in Presets)if(string.IsNullOrWhiteSpace(p.Id)||!ids.Add(p.Id)){p.Id=Guid.NewGuid().ToString("N");ids.Add(p.Id);}
        AutoProcessName=ProcessSelection.Normalize(AutoProcessName);
        if(!Presets.Any(p=>p.Id==AutoPresetId))AutoPresetId=Presets[Math.Min(2,Presets.Count-1)].Id;
        if(AutoProcessName.Length==0)AutoSwitchEnabled=false;
        if(!ValidHotkey(Hotkey)) Hotkey=(int)Keys.F9;
    }
    internal static bool ValidHotkey(int key) => key >= (int)Keys.F1 && key <= (int)Keys.F11;
    internal void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FileName)!);
        File.WriteAllText(FileName+".tmp",JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true}));
        File.Move(FileName+".tmp",FileName,true);
    }
}
internal interface IBrightness : IDisposable { void Apply(string device,Preset preset); void RestoreAll(); }
internal sealed class GammaBrightness : IBrightness
{
    private readonly Dictionary<string,ushort[]> original=new();
    internal static ushort[] Curve(ushort[] baseline,int amount,int blackPreservation=0,int contrast=0)
    {
        if(baseline.Length!=768) throw new ArgumentException("Invalid ramp");
        if(amount==0 && blackPreservation==0 && contrast==0) return (ushort[])baseline.Clone();
        double factor=1+Math.Clamp(amount,-50,100)/100.0;
        var ramp=new ushort[768];
        for(int channel=0;channel<3;channel++) for(int i=0;i<256;i++)
        {
            double y=Math.Pow(i/255.0,1/factor);
            // Monotone toe keeps near-black tones darker, then a normalized contrast curve
            // separates shadows and highlights without clipping either endpoint.
            double toe=Math.Clamp(blackPreservation,0,100)/300.0;
            y=y==0?0:y*y/(y+toe*(1-y));
            double power=1+Math.Clamp(contrast,0,50)/100.0;
            double bright=Math.Pow(y,power),dark=Math.Pow(1-y,power);
            double x=bright/(bright+dark)*255;
            x=Math.Clamp(x,0,255);
            int lo=(int)x,hi=Math.Min(255,lo+1);
            ramp[channel*256+i]=(ushort)Math.Round(baseline[channel*256+lo]*(1-(x-lo))+baseline[channel*256+hi]*(x-lo));
        }
        return ramp;
    }
    public void Apply(string device,Preset preset)
    {
        IntPtr dc=Native.CreateDC("DISPLAY",device,null,IntPtr.Zero);
        if(dc==IntPtr.Zero) throw new InvalidOperationException(L.T("디스플레이에 접근할 수 없습니다."));
        try
        {
            if(!original.TryGetValue(device,out var baseline))
            {
                baseline=new ushort[768];
                if(!Native.GetDeviceGammaRamp(dc,baseline)) throw new InvalidOperationException(L.T("이 디스플레이의 감마 보정을 읽을 수 없습니다."));
                original.Add(device,baseline);
            }
            var before=new ushort[768];
            if(!Native.GetDeviceGammaRamp(dc,before))throw new InvalidOperationException(L.T("현재 화면 상태를 읽을 수 없습니다."));
            var target=Curve(baseline,preset.Amount,preset.BlackPreservation,preset.Contrast);
            if(!Native.SetDeviceGammaRamp(dc,target))throw new InvalidOperationException(L.T("밝기 적용 실패: 디스플레이·HDR·그래픽 드라이버 설정을 확인하세요."));
            var actual=new ushort[768];
            if(!Native.GetDeviceGammaRamp(dc,actual)||!Matches(target,actual))
            {
                bool restored=Native.SetDeviceGammaRamp(dc,before);
                throw new InvalidOperationException(L.T("드라이버가 밝기 적용을 확인하지 못했습니다. HDR·게임 화면 모드를 확인하세요.")+(restored?"":L.T(" 이전 상태 복원도 실패했습니다.")));
            }
        }
        finally { Native.DeleteDC(dc); }
    }
    private static bool Matches(ushort[] a,ushort[] b)=>a.Zip(b).All(p=>Math.Abs((int)p.First-p.Second)<=512);
    public void RestoreAll()
    {
        var errors=new List<string>();
        foreach(var item in original)
        {
            IntPtr dc=Native.CreateDC("DISPLAY",item.Key,null,IntPtr.Zero);
            try
            {
                var actual=new ushort[768];
                if(dc==IntPtr.Zero || !Native.SetDeviceGammaRamp(dc,item.Value) || !Native.GetDeviceGammaRamp(dc,actual) || !Matches(item.Value,actual))errors.Add(item.Key);
            }
            finally { if(dc!=IntPtr.Zero) Native.DeleteDC(dc); }
        }
        if(errors.Count>0) throw new InvalidOperationException(L.T("원래 밝기 복원 실패: ")+string.Join(", ",errors));
    }
    public void Dispose() { }
}
internal sealed class Osd : Form
{
    private readonly Label title=new(){Dock=DockStyle.Top,Height=34,ForeColor=Color.White,Font=new Font("맑은 고딕",15,FontStyle.Bold)};
    private readonly Label detail=new(){Dock=DockStyle.Fill,ForeColor=Color.LightGray,Font=new Font("맑은 고딕",10)};
    private readonly System.Windows.Forms.Timer timer=new(){Interval=2200};
    internal Osd()
    {
        FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; TopMost=true; BackColor=Color.FromArgb(30,34,42);
        ClientSize=new Size(320,100); Padding=new Padding(18,14,18,10); Controls.Add(detail); Controls.Add(title);
        timer.Tick+=(_,_)=>{timer.Stop();Hide();};
    }
    protected override bool ShowWithoutActivation=>true;
    protected override CreateParams CreateParams { get { var p=base.CreateParams;p.ExStyle|=0x08000000|0x20|0x80;return p; } }
    internal void Notify(Screen screen,string name,int amount)
    {
        title.Text=name; detail.Text=amount==0?L.T("밝기 보정: 0% · 원래 화면"):L.F("밝기 보정: {0:+0;-0;0}%",amount);
        var area=screen.WorkingArea; Location=new Point(area.Right-Width-24,area.Bottom-Height-24);
        Show(); Native.SetWindowPos(Handle,new IntPtr(-1),Left,Top,Width,Height,0x10|0x40);
        timer.Stop();timer.Start();
    }
    protected override void Dispose(bool disposing) {if(disposing)timer.Dispose();base.Dispose(disposing);}
}
internal sealed class MainForm : Form
{
    private readonly Config config;
    private readonly IBrightness brightness;
    private readonly bool testing;
    private readonly IStartupSettings startupSettings;
    private readonly bool startInTray;
    private bool startupLoaded,changingStartup,runtimeInitialized;
    private readonly CheckBox runAtStartup=new(){Text=L.T("Windows 시작 시 자동 실행"),AutoSize=true};
    private readonly ListBox list=new(){Dock=DockStyle.Fill,IntegralHeight=false,HorizontalScrollbar=true};
    private readonly TextBox nameBox=new(){Dock=DockStyle.Fill,MaxLength=36};
    private readonly TrackBar slider=new(){Dock=DockStyle.Fill,Minimum=-50,Maximum=100,TickFrequency=25,AutoSize=false,Height=44};
    private readonly TrackBar blacks=new(){Dock=DockStyle.Fill,Minimum=0,Maximum=100,TickFrequency=25,AutoSize=false,Height=36};
    private readonly TrackBar contrast=new(){Dock=DockStyle.Fill,Minimum=0,Maximum=50,TickFrequency=10,AutoSize=false,Height=36};
    private readonly Label blackLabel=new(){AutoSize=true};
    private readonly Label contrastLabel=new(){AutoSize=true};
    private readonly Label amount=new(){AutoSize=true};
    private readonly Label current=new(){AutoSize=true,Text=L.T("현재: default · 보정 0%")};
    private readonly Label status=new(){AutoSize=true,ForeColor=Color.DimGray};
    private readonly ComboBox monitorBox=new(){Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly ComboBox hotkeys=new(){Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly CheckBox autoSwitch=new(){Text=L.T("활성 앱에만 자동 적용"),AutoSize=false,Dock=DockStyle.Top,Height=52};
    private readonly PictureBox processIcon=new(){Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,Margin=new Padding(0,3,6,3)};
    private readonly TextBox processName=new(){Dock=DockStyle.Fill,ReadOnly=true};
    private readonly ComboBox autoPreset=new(){Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly System.Windows.Forms.Timer foregroundTimer=new(){Interval=250};
    private bool loadingAutomation;
    private string appliedPresetId="";
    private int appliedBlack,appliedContrast;
    private DateTime retryAutoAt=DateTime.MinValue;
    private readonly CheckBox preview=new(){Text=L.T("조정 시 바로 미리보기"),Checked=true,AutoSize=true};
    private readonly NotifyIcon tray;
    private readonly Icon brightnessIcon;
    private readonly Label trayHint=new(){AutoSize=true,Text=L.T("X를 누르면 시스템 트레이로 숨겨집니다. 완전 종료: 트레이 우클릭 → 종료"),ForeColor=Color.DimGray};
    private readonly Osd osd=new();
    private readonly Button english=new(){Text="English",AutoSize=true,TextImageRelation=TextImageRelation.ImageBeforeText,AccessibleName="English language"};
    private readonly Button korean=new(){Text="한국어",AutoSize=true,TextImageRelation=TextImageRelation.ImageBeforeText,AccessibleName="한국어 언어"};
    private bool loading,quitting,registered,resourcesDisposed;
    private int active=-1;
    private string appliedName=L.T("원래 화면");
    private int appliedAmount;
    private bool changingMonitor,changingHotkey;
    internal int Active=>active;
    internal void VerifyAutomationLayout()
    {
        int textWidth=Math.Max(20,autoSwitch.ClientSize.Width-28);
        int textHeight=TextRenderer.MeasureText(autoSwitch.Text,autoSwitch.Font,new Size(textWidth,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl).Height;
        if(autoSwitch.Height<textHeight+8||autoSwitch.Right>autoSwitch.Parent!.ClientSize.Width)throw new Exception("Automation checkbox text clipped");
    }
    internal MainForm(Config? supplied=null,IBrightness? backend=null,bool test=false,bool startInTray=false,IStartupSettings? startup=null)
    {
        testing=test;this.startInTray=startInTray;startupSettings=startup??new StartupSettings();config=supplied??Config.Load();config.Normalize();L.Language=config.Language;brightness=backend??new GammaBrightness();
        Text=L.T("Brightness Toggle 1.0.12 · 밝기 프리셋");Font=new Font("맑은 고딕",10);ClientSize=new Size(1040,960);
        MinimumSize=new Size(1040,960);StartPosition=FormStartPosition.CenterScreen;
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(22),ColumnCount=2,RowCount=6};
        root.ColumnStyles.Add(new(SizeType.Absolute,360));root.ColumnStyles.Add(new(SizeType.Percent,100));
        root.RowStyles.Add(new(SizeType.Absolute,64));root.RowStyles.Add(new(SizeType.Percent,100));root.RowStyles.Add(new(SizeType.Absolute,56));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.AutoSize));
        var intro=new Label{Text=L.T("한 번 눌러 밝기 전환"),Font=new Font(Font.FontFamily,17,FontStyle.Bold),AutoSize=true};var header=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2};
        header.ColumnStyles.Add(new(SizeType.Percent,100));header.ColumnStyles.Add(new(SizeType.AutoSize));
        intro.Font=new Font(Font.FontFamily,16,FontStyle.Bold);header.Controls.Add(intro,0,0);
        var languages=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,WrapContents=false};
        english.Image=Flag("en");korean.Image=Flag("ko");languages.Controls.Add(english);languages.Controls.Add(korean);
        header.Controls.Add(languages,1,0);root.Controls.Add(header,0,0);root.SetColumnSpan(header,2);
        english.Click+=(_,_)=>SetLanguage("en");korean.Click+=(_,_)=>SetLanguage("ko");
        GroupBox Section(string text,Control content)
        {
            var group=new GroupBox{Text=L.T(text),Dock=DockStyle.Fill,AutoSize=true,Padding=new Padding(10,6,10,8),Margin=new Padding(0,0,12,12)};
            group.Controls.Add(content);return group;
        }
        var scrollArea=new Panel{Dock=DockStyle.Fill,AutoScroll=true};
        var content=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
        content.ColumnStyles.Add(new(SizeType.Absolute,360));content.ColumnStyles.Add(new(SizeType.Percent,100));content.RowStyles.Add(new(SizeType.AutoSize));
        scrollArea.Controls.Add(content);root.Controls.Add(scrollArea,0,1);root.SetColumnSpan(scrollArea,2);
        var left=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,RowCount=2,Margin=Padding.Empty};
        left.ColumnStyles.Add(new(SizeType.Percent,100));left.RowStyles.Add(new(SizeType.Absolute,260));left.RowStyles.Add(new(SizeType.AutoSize));
        var presets=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};presets.ColumnStyles.Add(new(SizeType.Percent,100));
        presets.RowStyles.Add(new(SizeType.Percent,100));presets.RowStyles.Add(new(SizeType.AutoSize));presets.Controls.Add(list,0,0);
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true};
        AddButton(buttons,L.T("추가"),(_,_)=>AddPreset());AddButton(buttons,L.T("삭제"),(_,_)=>RemovePreset());presets.Controls.Add(buttons,0,1);
        left.Controls.Add(Section("프리셋 목록",presets),0,0);
        var automation=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=1,Padding=Padding.Empty};
        automation.ColumnStyles.Add(new(SizeType.Percent,100));
        automation.Controls.Add(autoSwitch);
        automation.Controls.Add(new Label{Text=L.T("대상 프로세스"),AutoSize=true});var processField=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
        processField.ColumnStyles.Add(new(SizeType.Absolute,30));processField.ColumnStyles.Add(new(SizeType.Percent,100));processField.Controls.Add(processIcon,0,0);processField.Controls.Add(processName,1,0);automation.Controls.Add(processField);
        var choose=new Button{Text=L.T("프로세스 선택"),AutoSize=true};automation.Controls.Add(choose);
        automation.Controls.Add(new Label{Text=L.T("자동 적용 프리셋"),AutoSize=true});automation.Controls.Add(autoPreset);
        var autoHint=new Label{Text=L.T("다른 앱은 기본으로 복원합니다.\n선택한 모니터 전체에 적용됩니다."),AutoSize=true,Dock=DockStyle.Top,ForeColor=Color.DimGray};
        automation.Controls.Add(autoHint);
        void WrapAutomation()
        {
            int width=Math.Max(40,automation.ClientSize.Width-automation.Padding.Horizontal-6);
            autoHint.MaximumSize=new Size(width,0);
            autoSwitch.Height=Math.Max(28,TextRenderer.MeasureText(autoSwitch.Text,autoSwitch.Font,new Size(Math.Max(20,width-28),int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl).Height+12);
        }
        automation.SizeChanged+=(_,_)=>WrapAutomation();autoSwitch.TextChanged+=(_,_)=>WrapAutomation();autoSwitch.FontChanged+=(_,_)=>WrapAutomation();
        left.Controls.Add(Section("프로그램 자동 전환",automation),0,1);content.Controls.Add(left,0,0);
        choose.Click+=(_,_)=>{var selected=ProcessSelection.Choose(this);if(selected!=null){config.AutoProcessName=selected;processName.Text=selected+".exe";UpdateProcessIcon();Save();retryAutoAt=DateTime.MinValue;CheckForeground();}};
        processName.Text=config.AutoProcessName.Length>0?config.AutoProcessName+".exe":"";UpdateProcessIcon();
        autoSwitch.Checked=config.AutoSwitchEnabled;
        autoSwitch.CheckedChanged+=(_,_)=>
        {
            if(autoSwitch.Checked&&config.AutoProcessName.Length==0){autoSwitch.Checked=false;status.Text=L.T("먼저 대상 프로세스를 선택하세요.");return;}
            config.AutoSwitchEnabled=autoSwitch.Checked;retryAutoAt=DateTime.MinValue;Save();
            if(config.AutoSwitchEnabled)CheckForeground();else Apply(0);
        };
        autoPreset.SelectedIndexChanged+=(_,_)=>{if(loadingAutomation||autoPreset.SelectedItem is not Preset p)return;config.AutoPresetId=p.Id;retryAutoAt=DateTime.MinValue;Save();CheckForeground();};
        foregroundTimer.Tick+=(_,_)=>CheckForeground();
        var right=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,RowCount=2,Margin=Padding.Empty};right.ColumnStyles.Add(new(SizeType.Percent,100));right.RowStyles.Add(new(SizeType.AutoSize));right.RowStyles.Add(new(SizeType.AutoSize));
        var edit=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=10,AutoSize=true};edit.ColumnStyles.Add(new(SizeType.Percent,100));for(int row=0;row<10;row++)edit.RowStyles.Add(new(SizeType.AutoSize));
        edit.Controls.Add(new Label{Text=L.T("프리셋 이름"),AutoSize=true});edit.Controls.Add(nameBox);
        edit.Controls.Add(amount);edit.Controls.Add(slider);edit.Controls.Add(blackLabel);edit.Controls.Add(blacks);edit.Controls.Add(contrastLabel);edit.Controls.Add(contrast);edit.Controls.Add(preview);
        var apply=new Button{Text=L.T("선택한 프리셋 적용"),Dock=DockStyle.Fill,Height=34};apply.Click+=(_,_)=>ApplySelected();edit.Controls.Add(apply);
        right.Controls.Add(Section("밝기 조절",edit),0,0);
        var devices=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=1,RowCount=4};devices.ColumnStyles.Add(new(SizeType.Percent,100));
        devices.Controls.Add(new Label{Text=L.T("대상 디스플레이"),AutoSize=true});devices.Controls.Add(monitorBox);
        devices.Controls.Add(new Label{Text=L.T("전환 단축키 (한 번 누르면 다음 프리셋)"),AutoSize=true});devices.Controls.Add(hotkeys);devices.Controls.Add(runAtStartup);runAtStartup.Enabled=false;runAtStartup.CheckedChanged+=async(_,_)=>await ChangeStartup();right.Controls.Add(Section("디스플레이 및 단축키",devices),0,1);
        content.Controls.Add(right,1,0);
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill};AddButton(actions,L.T("다음 프리셋"),(_,_)=>Next());AddButton(actions,L.T("기본 (0%)"),(_,_)=>Apply(0));root.Controls.Add(actions,0,2);root.SetColumnSpan(actions,2);
        root.Controls.Add(current,0,3);root.SetColumnSpan(current,2);root.Controls.Add(status,0,4);root.SetColumnSpan(status,2);root.Controls.Add(trayHint,0,5);root.SetColumnSpan(trayHint,2);Controls.Add(root);
        void WrapFooter(){int width=Math.Max(40,root.ClientSize.Width-root.Padding.Horizontal-6);foreach(var label in new[]{current,status,trayHint})label.MaximumSize=new Size(width,0);}
        root.SizeChanged+=(_,_)=>WrapFooter();WrapFooter();
        foreach(var screen in Screen.AllScreens) monitorBox.Items.Add(screen.DeviceName);
        if(monitorBox.Items.Count==0) throw new InvalidOperationException(L.T("디스플레이를 찾을 수 없습니다."));
        monitorBox.SelectedItem=config.Monitor;if(monitorBox.SelectedIndex<0)monitorBox.SelectedItem=(Screen.PrimaryScreen??Screen.AllScreens[0]).DeviceName;
        for(int i=1;i<=11;i++)hotkeys.Items.Add((Keys)((int)Keys.F1+i-1));hotkeys.SelectedItem=(Keys)config.Hotkey;
        var menu=new ContextMenuStrip();menu.Items.Add(L.T("다음 프리셋"),null,(_,_)=>Next());menu.Items.Add(L.T("기본 (0%)"),null,(_,_)=>Apply(0));menu.Items.Add(L.T("설정 열기"),null,(_,_)=>OpenSettings());menu.Items.Add(new ToolStripSeparator());menu.Items.Add(L.T("종료 (원래 밝기 복원)"),null,(_,_)=>Exit());
        using(var stream=typeof(MainForm).Assembly.GetManifestResourceStream("BrightnessToggle.Assets.brightness.ico") ?? throw new InvalidOperationException(L.T("밝기 아이콘을 읽을 수 없습니다.")))
        using(var sourceIcon=new Icon(stream)) brightnessIcon=(Icon)sourceIcon.Clone();
        Icon=brightnessIcon;
        tray=new NotifyIcon{Icon=brightnessIcon,Text=L.T("밝기: default (0%)"),ContextMenuStrip=menu,Visible=!testing};tray.MouseClick+=(_,e)=>{if(e.Button==MouseButtons.Left)OpenSettings();};
        list.SelectedIndexChanged+=(_,_)=>LoadSelection();nameBox.TextChanged+=(_,_)=>EditName();slider.ValueChanged+=(_,_)=>EditAmount();blacks.ValueChanged+=(_,_)=>EditAmount();contrast.ValueChanged+=(_,_)=>EditAmount();
        preview.CheckedChanged+=(_,_)=>{if(preview.Checked)ApplySelected();};
        monitorBox.SelectedIndexChanged+=(_,_)=>ChangeMonitor();hotkeys.SelectedIndexChanged+=(_,_)=>RegisterKey();
        FormClosing+=HandleClosing;SystemEvents.SessionEnding+=SessionEnding;
        RefreshList(0);config.Monitor=(string)monitorBox.SelectedItem!;
        ApplyLanguage();
        Shown+=async(_,_)=>await InitializeRuntime();
    }
    protected override void SetVisibleCore(bool value)
    {
        if(value&&startInTray&&!startupLoaded)
        {
            // Initialize controls and the message loop without showing the settings window.
            if(!IsHandleCreated)CreateHandle();
            startupLoaded=true;
            BeginInvoke(async()=>await InitializeRuntime());
            base.SetVisibleCore(false);return;
        }
        base.SetVisibleCore(value);
    }
    private async Task InitializeRuntime()
    {
        if(runtimeInitialized)return;runtimeInitialized=true;RegisterKey();Apply(0);
        if(!testing){CheckForeground();foregroundTimer.Start();}await LoadStartup();
    }
    internal async Task LoadStartup()
    {
        changingStartup=true;
        try{runAtStartup.Checked=await startupSettings.IsEnabled();runAtStartup.Enabled=true;}
        catch(Exception ex){status.Text=L.T("자동 실행 설정 실패: ")+ex.Message;}
        finally{changingStartup=false;}
    }
    private async Task ChangeStartup()
    {
        if(changingStartup)return;bool requested=runAtStartup.Checked;runAtStartup.Enabled=false;
        try
        {
            bool enabled=await startupSettings.SetEnabled(requested);changingStartup=true;runAtStartup.Checked=enabled;
            if(enabled!=requested)status.Text=L.T("Windows 설정 → 앱 → 시작 프로그램에서 변경하세요.");
        }
        catch(Exception ex){changingStartup=true;runAtStartup.Checked=!requested;status.Text=L.T("자동 실행 설정 실패: ")+ex.Message;}
        finally{changingStartup=false;runAtStartup.Enabled=true;}
    }
    private void UpdateProcessIcon()
    {
        var previous=processIcon.Image;processIcon.Image=ProcessSelection.LoadIcon(config.AutoProcessName);previous?.Dispose();
    }
    private static Image Flag(string language)
    {
        using var stream=typeof(MainForm).Assembly.GetManifestResourceStream("BrightnessToggle.Assets.flag-"+language+".png")??throw new InvalidOperationException("Missing flag icon");
        using var image=Image.FromStream(stream);return new Bitmap(image,new Size(24,16));
    }
    internal void SetLanguage(string language)
    {
        config.Language=language=="ko"?"ko":"en";L.Language=config.Language;
        ApplyLanguage();Save();
    }
    private void ApplyLanguage()
    {
        if(active>=0)appliedName=config.Presets[active].DisplayName;
        L.Apply(this);L.ApplyMenu(tray.ContextMenuStrip!.Items);
        int selected=list.SelectedIndex;RefreshList(selected<0?0:selected);UpdateCurrent();
        status.Text=L.Language=="ko"?"언어: 한국어":"Language: English";
        english.FlatStyle=L.Language=="en"?FlatStyle.Popup:FlatStyle.Standard;
        korean.FlatStyle=L.Language=="ko"?FlatStyle.Popup:FlatStyle.Standard;
        if(osd.Visible)osd.Notify(TargetScreen(),appliedName,appliedAmount);
    }
    private static void AddButton(Control parent,string text,EventHandler handler) {var b=new Button{Text=text,AutoSize=true,Height=34};b.Click+=handler;parent.Controls.Add(b);}
    private void RefreshList(int selected) {loading=true;list.Items.Clear();foreach(var p in config.Presets)list.Items.Add(p);list.SelectedIndex=Math.Clamp(selected,0,list.Items.Count-1);loading=false;LoadSelection();RefreshAutoPresets();}
    private void LoadSelection()
    {
        if(loading||list.SelectedIndex<0)return;loading=true;var p=config.Presets[list.SelectedIndex];nameBox.Text=p.DisplayName;slider.Value=p.Amount;blacks.Value=p.BlackPreservation;contrast.Value=p.Contrast;blackLabel.Text=L.F("검정 유지: {0}%",p.BlackPreservation);contrastLabel.Text=L.F("대비: {0}%",p.Contrast);amount.Text=L.F("밝기 보정: {0:+0;-0;0}%",p.Amount);nameBox.Enabled=slider.Enabled=blacks.Enabled=contrast.Enabled=list.SelectedIndex!=0;loading=false;
    }
    private void EditName() {if(loading||list.SelectedIndex<=0)return;int i=list.SelectedIndex;config.Presets[i].BuiltIn="";config.Presets[i].Name=string.IsNullOrWhiteSpace(nameBox.Text)?L.T("프리셋"):nameBox.Text.Trim();RefreshItem(i);Save();}
    private void RefreshItem(int i) {loading=true;list.Items[i]=config.Presets[i];loading=false;RefreshAutoPresets();}
    private void EditAmount() {if(loading||list.SelectedIndex<=0)return;int i=list.SelectedIndex;config.Presets[i].Amount=slider.Value;config.Presets[i].BlackPreservation=blacks.Value;config.Presets[i].Contrast=contrast.Value;blackLabel.Text=L.F("검정 유지: {0}%",blacks.Value);contrastLabel.Text=L.F("대비: {0}%",contrast.Value);amount.Text=L.F("밝기 보정: {0:+0;-0;0}%",slider.Value);RefreshItem(i);Save();if(preview.Checked&&!config.AutoSwitchEnabled)Apply(i);retryAutoAt=DateTime.MinValue;CheckForeground();}
    internal void AddPreset() {config.Presets.Add(new(){Name=L.T("프리셋")+" "+config.Presets.Count,Amount=25});RefreshList(config.Presets.Count-1);Save();}
    internal void RemovePreset() {int i=list.SelectedIndex;if(i<=0)return;if(active==i&&!Apply(0))return;config.Presets.RemoveAt(i);if(active>i)active--;if(!config.Presets.Any(p=>p.Id==config.AutoPresetId)){config.AutoPresetId=config.Presets[0].Id;retryAutoAt=DateTime.MinValue;}RefreshList(Math.Min(i,config.Presets.Count-1));Save();}
    internal void OpenSettings(){Show();WindowState=FormWindowState.Normal;Activate();}
    internal void Next()
    {
        if(config.AutoSwitchEnabled)
        {
            if(!ProcessSelection.Matches(config.AutoProcessName,ProcessSelection.ForegroundName())){EvaluateAuto(null);return;}
            int next=(config.Presets.FindIndex(p=>p.Id==config.AutoPresetId)+1)%config.Presets.Count;
            config.AutoPresetId=config.Presets[next].Id;RefreshAutoPresets();Save();retryAutoAt=DateTime.MinValue;CheckForeground();return;
        }
        Apply((active+1)%config.Presets.Count);
    }
    private void ApplySelected()
    {
        if(list.SelectedIndex<0)return;
        if(config.AutoSwitchEnabled){config.AutoPresetId=config.Presets[list.SelectedIndex].Id;RefreshAutoPresets();Save();retryAutoAt=DateTime.MinValue;CheckForeground();}
        else Apply(list.SelectedIndex);
    }
    private void RefreshAutoPresets()
    {
        loadingAutomation=true;autoPreset.Items.Clear();foreach(var p in config.Presets)autoPreset.Items.Add(p);
        autoPreset.SelectedItem=config.Presets.FirstOrDefault(p=>p.Id==config.AutoPresetId);loadingAutomation=false;
    }
    private void CheckForeground(){if(!testing&&config.AutoSwitchEnabled&&!quitting)EvaluateAuto(ProcessSelection.ForegroundName());}
    internal void EvaluateAuto(string? foreground)
    {
        if(!config.AutoSwitchEnabled||quitting)return;
        int desired=ProcessSelection.Matches(config.AutoProcessName,foreground)?config.Presets.FindIndex(p=>p.Id==config.AutoPresetId):0;
        desired=Math.Max(0,desired);var p=config.Presets[desired];
        if(active==desired&&appliedPresetId==p.Id&&appliedAmount==p.Amount&&appliedBlack==p.BlackPreservation&&appliedContrast==p.Contrast)return;
        if(DateTime.UtcNow<retryAutoAt)return;
        retryAutoAt=Apply(desired)?DateTime.MinValue:DateTime.UtcNow.AddSeconds(5);
    }
    internal bool Apply(int index)
    {
        try {var p=config.Presets[index];brightness.Apply((string)monitorBox.SelectedItem!,p);active=index;appliedName=p.DisplayName;appliedAmount=p.Amount;appliedPresetId=p.Id;appliedBlack=p.BlackPreservation;appliedContrast=p.Contrast;UpdateCurrent();status.Text="";if(!testing)osd.Notify(TargetScreen(),p.DisplayName,p.Amount);return true;}
        catch(Exception ex){status.Text=ex.Message;if(!testing)tray.ShowBalloonTip(3000,L.T("밝기 적용 실패"),ex.Message,ToolTipIcon.Warning);return false;}
    }
    private Screen TargetScreen()=>Screen.AllScreens.FirstOrDefault(s=>s.DeviceName==(string)monitorBox.SelectedItem!)??Screen.PrimaryScreen??Screen.AllScreens[0];
    private void UpdateCurrent()
    {
        current.Text=L.F("현재: {0} · 보정 {1:+0;-0;0}%",appliedName,appliedAmount);
        string text=L.F("밝기: {0} ({1:+0;-0;0}%)",appliedName,appliedAmount);
        tray.Text=text[..Math.Min(63,text.Length)];
    }
    private void ChangeMonitor()
    {
        if(changingMonitor)return;
        string old=config.Monitor;
        try {brightness.RestoreAll();active=-1;appliedName=L.T("원래 화면");appliedAmount=0;config.Monitor=(string)monitorBox.SelectedItem!;UpdateCurrent();Save();Apply(0);retryAutoAt=DateTime.MinValue;CheckForeground();}
        catch(Exception ex)
        {
            status.Text=ex.Message;
            changingMonitor=true;
            if(monitorBox.Items.Contains(old))monitorBox.SelectedItem=old;
            changingMonitor=false;
        }
    }
    private void Save(){if(testing)return;try{config.Save();}catch(Exception ex){status.Text=L.T("설정 저장 실패: ")+ex.Message;}}
    private void RegisterKey()
    {
        if(testing||changingHotkey)return;
        int chosen=(int)(Keys)hotkeys.SelectedItem!;int previous=config.Hotkey;
        Native.UnregisterHotKey(Handle,1);registered=Native.RegisterHotKey(Handle,1,0x4000,(uint)chosen);
        if(registered){config.Hotkey=chosen;Save();status.Text=((Keys)chosen)+L.T(": 다음 프리셋");}
        else {registered=Native.RegisterHotKey(Handle,1,0x4000,(uint)previous);status.Text=L.T("단축키 사용 중입니다. 다른 F키를 선택하세요.");changingHotkey=true;hotkeys.SelectedItem=(Keys)previous;changingHotkey=false;}
    }
    protected override void WndProc(ref Message m){if(m.Msg==0x312&&m.WParam.ToInt32()==1){Next();return;}base.WndProc(ref m);}
    private void HandleClosing(object? sender,FormClosingEventArgs e)
    {
        if(e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)quitting=true;
        if(!quitting)
        {
            e.Cancel=true;Hide();
            if(!testing)tray.ShowBalloonTip(3500,L.T("밝기 프로그램이 트레이에서 실행 중입니다"), L.T("작업 표시줄 오른쪽의 해 아이콘을 확인하세요. 숨겨져 있으면 ∧를 누르세요. 설정/종료: 아이콘 우클릭"),ToolTipIcon.Info);
            return;
        }
        try{brightness.RestoreAll();}catch(Exception ex){if(e.CloseReason!=CloseReason.WindowsShutDown&&!testing){e.Cancel=true;quitting=false;Show();MessageBox.Show(this,ex.Message+L.T("\n복원을 다시 시도한 뒤 종료하세요."),"Brightness Toggle",MessageBoxButtons.OK,MessageBoxIcon.Warning);}}
    }
    private void SessionEnding(object? sender,SessionEndingEventArgs e){try{brightness.RestoreAll();}catch{}}
    internal void Exit(){quitting=true;Close();}
    protected override void Dispose(bool disposing){if(disposing&&!resourcesDisposed){resourcesDisposed=true;foregroundTimer.Stop();foregroundTimer.Dispose();SystemEvents.SessionEnding-=SessionEnding;if(IsHandleCreated)Native.UnregisterHotKey(Handle,1);tray.Visible=false;tray.ContextMenuStrip?.Dispose();tray.Dispose();osd.Dispose();brightness.Dispose();brightnessIcon.Dispose();english.Image?.Dispose();korean.Image?.Dispose();processIcon.Image?.Dispose();}base.Dispose(disposing);}
}
internal static class Native
{
    internal delegate bool EnumWindowProc(IntPtr window,IntPtr data);
    [DllImport("user32.dll")]internal static extern bool EnumWindows(EnumWindowProc callback,IntPtr data);
    [DllImport("user32.dll")]internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]internal static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]internal static extern int GetWindowText(IntPtr window,System.Text.StringBuilder text,int count);
    [DllImport("gdi32.dll",CharSet=CharSet.Unicode)]internal static extern IntPtr CreateDC(string driver,string device,string? output,IntPtr init);
    [DllImport("gdi32.dll")]internal static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")]internal static extern bool GetDeviceGammaRamp(IntPtr dc,[Out]ushort[] ramp);
    [DllImport("gdi32.dll")]internal static extern bool SetDeviceGammaRamp(IntPtr dc,[In]ushort[] ramp);
    [DllImport("user32.dll")]internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]internal static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint processId);
    [DllImport("user32.dll",SetLastError=true)]internal static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")]internal static extern bool UnregisterHotKey(IntPtr hwnd,int id);
    [DllImport("user32.dll")]internal static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
}
internal static class Tests
{
    private sealed class FakeStartup : IStartupSettings
    {
        internal bool Enabled;public Task<bool> IsEnabled()=>Task.FromResult(Enabled);
        public Task<bool> SetEnabled(bool enabled){Enabled=enabled;return Task.FromResult(enabled);}
    }
    private sealed class Fake : IBrightness {internal int Amount;internal int Calls;internal int Attempts;internal int Restores;internal bool Fail;public void Apply(string d,Preset p){Attempts++;if(Fail)throw new Exception("expected failure");Amount=p.Amount;Calls++;}public void RestoreAll(){Restores++;}public void Dispose(){}}
    internal static void Run(string path)
    {
        try
        {
            using(var fallbackIcon=ProcessSelection.LoadIcon("nonexistent-process-for-icon-test"))if(fallbackIcon.Width<=0||fallbackIcon.Height<=0)throw new Exception("process fallback icon");
            if(StartupSettings.Command(@"C:\My Apps\BrightnessToggle.exe")!="\"C:\\My Apps\\BrightnessToggle.exe\" --startup"||!StartupSettings.IsStartup(["--startup"])||StartupSettings.IsStartup([]))throw new Exception("startup command");
            var fakeStartup=new FakeStartup();if(fakeStartup.IsEnabled().Result||!fakeStartup.SetEnabled(true).Result||fakeStartup.SetEnabled(false).Result)throw new Exception("startup toggle");
            string obscured="A\uFEFFR\uFEFFC\u200B\u2005Ra\u200Bid\uFEFFers";
            if(!ProcessSelection.SearchMatches("PioneerGame",obscured,"ARC Raiders")||!ProcessSelection.SearchMatches("PioneerGame",obscured,"ARC")||!ProcessSelection.SearchMatches("PioneerGame",obscured,"pioneer"))throw new Exception("invisible title search");
            if(!ProcessSelection.SearchMatches("PioneerGame","","ARC")||!ProcessSelection.SearchMatches("PioneerGame","","PioneerGame.exe")||!ProcessSelection.SearchMatches("PioneerGame",obscured,"Pioneer Raiders")||!ProcessSelection.SearchMatches("sample","Second window title","Second"))throw new Exception("process and title search");
            if(ProcessSelection.SearchMatches("PioneerGame",obscured,"nonexistent"))throw new Exception("unrelated title search");
            ushort[] baseline=Enumerable.Range(0,768).Select(i=>(ushort)((i%256)*257)).ToArray();
            if(!GammaBrightness.Curve(baseline,0).SequenceEqual(baseline))throw new Exception("default ramp");
            foreach(int a in new[]{-50,25,100}){var ramp=GammaBrightness.Curve(baseline,a);for(int c=0;c<3;c++)for(int i=1;i<256;i++)if(ramp[c*256+i]<ramp[c*256+i-1])throw new Exception("nonmonotonic ramp");}
            if(GammaBrightness.Curve(baseline,25)[128]<=baseline[128])throw new Exception("brightness direction");
            foreach(int a in new[]{-50,0,25,50,100}) foreach(int black in new[]{0,15,25,100}) foreach(int contrast in new[]{0,10,50})
            {var ramp=GammaBrightness.Curve(baseline,a,black,contrast);if(ramp[0]!=0||ramp[255]!=65535)throw new Exception("endpoints");for(int i=1;i<256;i++)if(ramp[i]<ramp[i-1])throw new Exception("tone monotonicity");}
            if(GammaBrightness.Curve(baseline,50,25,10)[16]>=GammaBrightness.Curve(baseline,50)[16])throw new Exception("black preservation");
            var previous=new Config{PresetSchema=1,Presets=Config.Defaults().Take(3).ToList()};previous.Presets[1].Amount=32;previous.Presets.Add(new(){Name="custom",Amount=42});previous.Normalize();previous.Normalize();
            if(previous.Presets.Count!=5||previous.Presets[1].Amount!=32||previous.Presets[3].Amount!=75||previous.Presets[4].Name!="custom")throw new Exception("level 3 migration");
            previous.Presets.RemoveAt(3);previous.Normalize();if(previous.Presets.Any(p=>p.BuiltIn=="level3"))throw new Exception("deleted level retained");
            var config=new Config();var fake=new Fake();
            var migrated=new Config{Presets=[new(){Name="default",Amount=0},new(){Name="custom",Amount=45}]};migrated.Normalize();
            if(migrated.Presets[0].Amount!=0||migrated.Presets[4].Amount!=45)throw new Exception("default migration");
            var legacy=new Config{Presets=[new(){Name="default",Amount=-20},new(){Name="+25%",Amount=25},new(){Name="custom",Amount=45}]};
            legacy.Normalize();legacy.Normalize();
            if(legacy.Presets[0].Amount!=0||legacy.Presets[1].Amount!=25||legacy.Presets[2].Amount!=50||legacy.Presets[4].Amount!=45)throw new Exception("legacy migration");
            if(GammaBrightness.Curve(baseline,80)[128]<=baseline[128])throw new Exception("plus 80 curve");
            using(var form=new MainForm(config,fake,true,startup:new FakeStartup())){form.Show();Application.DoEvents();if(form.Active!=0||fake.Amount!=0)throw new Exception("startup default zero");
            if(L.Language!="en"||!form.Text.Contains("Gamma presets"))throw new Exception("default English");
            form.SetLanguage("ko");if(!form.Text.Contains("밝기 프리셋")||config.Language!="ko")throw new Exception("Korean switch");
            var roundtrip=JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(config));if(roundtrip?.Language!="ko"||roundtrip.Presets[2].BlackPreservation!=25||roundtrip.Presets[2].Contrast!=10)throw new Exception("language save");
            if(config.Presets[1].DisplayName!="밝기 1단계")throw new Exception("preset translation");
            form.Size=form.MinimumSize;Application.DoEvents();
            form.VerifyAutomationLayout();
            var visibleTitles=ProcessSelection.WindowTitles(true);if(!visibleTitles.TryGetValue((uint)Environment.ProcessId,out var ownTitles)||!ownTitles.Any(t=>t.Contains("Brightness Toggle")))throw new Exception("window title enumeration");
            using(var koreanShot=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(koreanShot,new Rectangle(Point.Empty,form.Size));koreanShot.Save(Path.Combine(Path.GetDirectoryName(path)!,"brightness-korean.png"));}
            form.SetLanguage("en");Application.DoEvents();form.VerifyAutomationLayout();if(!form.Text.Contains("Gamma presets"))throw new Exception("English switch");
            using(var osd=new Osd())
            {
                osd.Notify(Screen.PrimaryScreen??Screen.AllScreens[0],"+80%",80);Application.DoEvents();
                if(!osd.Visible||!osd.TopMost||osd.ShowInTaskbar)throw new Exception("OSD display");
                using var shot=new Bitmap(osd.Width,osd.Height);osd.DrawToBitmap(shot,new Rectangle(Point.Empty,osd.Size));shot.Save(Path.Combine(Path.GetDirectoryName(path)!,"brightness-osd.png"));osd.Hide();
            }
            form.Size=new Size(1366,900);Application.DoEvents();using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.ChangeExtension(path,".png"));}
            form.Next();if(form.Active!=1||fake.Amount!=25)throw new Exception("cycle level 1");form.Next();if(form.Active!=2||fake.Amount!=50)throw new Exception("cycle level 2");form.Next();if(form.Active!=3||fake.Amount!=75)throw new Exception("cycle level 3");form.Next();if(form.Active!=0||fake.Amount!=0)throw new Exception("cycle default");form.AddPreset();if(config.Presets.Count!=5)throw new Exception("add");form.Apply(4);form.RemovePreset();if(form.Active!=0||config.Presets.Count!=4)throw new Exception("remove active");fake.Fail=true;if(form.Apply(1)||form.Active!=0)throw new Exception("failure state");fake.Fail=false;form.Close();if(form.IsDisposed||form.Visible)throw new Exception("tray hide");int beforeOpenCalls=fake.Calls;int beforeOpenPreset=form.Active;form.OpenSettings();Application.DoEvents();if(!form.Visible||form.WindowState!=FormWindowState.Normal||form.Active!=beforeOpenPreset||fake.Calls!=beforeOpenCalls)throw new Exception("tray open must not change preset");form.Exit();if(fake.Restores==0)throw new Exception("restore on exit");}
            var autoConfig=new Config();autoConfig.Normalize();autoConfig.AutoProcessName="Game.exe";autoConfig.AutoSwitchEnabled=true;autoConfig.AutoPresetId=autoConfig.Presets[2].Id;
            var autoFake=new Fake();using(var autoForm=new MainForm(autoConfig,autoFake,true,startup:new FakeStartup()))
            {
                autoForm.Show();Application.DoEvents();autoForm.EvaluateAuto("GAME");if(autoFake.Amount!=50)throw new Exception("auto enter game");
                int calls=autoFake.Calls;autoForm.EvaluateAuto("game");if(autoFake.Calls!=calls)throw new Exception("auto repeated application");
                autoForm.EvaluateAuto("browser");if(autoFake.Amount!=0)throw new Exception("auto alt-tab restore");
                autoForm.EvaluateAuto("game");autoForm.EvaluateAuto(null);if(autoFake.Amount!=0)throw new Exception("auto missing process restore");
                autoConfig.Presets[2].Amount=63;autoForm.EvaluateAuto("game");if(autoFake.Amount!=63)throw new Exception("auto custom values");
                var saved=JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(autoConfig))!;saved.Normalize();if(!saved.AutoSwitchEnabled||saved.AutoPresetId!=autoConfig.AutoPresetId||!ProcessSelection.Matches(saved.AutoProcessName,"game"))throw new Exception("auto persistence");
                autoFake.Fail=true;autoForm.EvaluateAuto("other");calls=autoFake.Attempts;autoForm.EvaluateAuto("other");if(autoForm.Active!=2||autoFake.Attempts!=calls)throw new Exception("auto failure state");autoFake.Fail=false;autoConfig.AutoSwitchEnabled=false;autoForm.EvaluateAuto("other");if(autoForm.Active!=2)throw new Exception("disabled auto mode");autoForm.Exit();
            }
            var hiddenFake=new Fake();using(var hidden=new MainForm(new Config(),hiddenFake,true,startInTray:true,startup:new FakeStartup()))
            {
                hidden.Show();Application.DoEvents();if(hidden.Visible||hiddenFake.Calls!=1||hiddenFake.Amount!=0)throw new Exception("startup tray initialization");
                hidden.Show();Application.DoEvents();if(!hidden.Visible||hiddenFake.Calls!=1)throw new Exception("reopen startup tray window");
                hidden.Exit();
            }
            File.WriteAllText(path,"PASS: Tray open preserves preset; Startup command, toggle and hidden tray initialization; Process icon fallback; Invisible Unicode window-title search; Active-process auto switching, Alt+Tab/exit restore, case-insensitive process matching, no repeat application, saved rules and custom values, failure backoff; Four-stage presets, tone controls, monotone curves and endpoints, black preservation, migration and persistence; English default, Korean/English switch, language persistence; default 0%, levels +25%/+50%/+75%, old config migration, startup default, four-stage cycle, gamma curve, add/remove, apply failure state, tray hide, exit restore, OSD. No real display changes made.");
        }catch(Exception ex){File.WriteAllText(path,"FAIL: "+ex);Environment.ExitCode=1;}
    }
}
