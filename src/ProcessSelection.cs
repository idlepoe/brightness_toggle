using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace BrightnessToggle;

internal static class ProcessSelection
{
    // These bitmaps are owned by the caller. Protected or stopped processes use
    // a system icon; obtaining an icon never requires administrator privileges.
    internal static Bitmap LoadIcon(string name)
    {
        Process[] processes=[];
        try
        {
            if(Normalize(name).Length>0)processes=Process.GetProcessesByName(Normalize(name));
            foreach(var process in processes)try
            {
                string? path=process.MainModule?.FileName;
                if(path==null)continue;
                using var icon=Icon.ExtractAssociatedIcon(path);
                if(icon!=null)return icon.ToBitmap();
            }catch{}
        }
        catch{}
        finally{foreach(var process in processes)process.Dispose();}
        return SystemIcons.Application.ToBitmap();
    }
    internal static string CleanTitle(string value)=>new(value.Normalize(NormalizationForm.FormKC).Where(c=>char.GetUnicodeCategory(c)!=UnicodeCategory.Format&&!char.IsControl(c)).ToArray());
    internal static string FriendlyName(string name,string title)=>string.Equals(Normalize(name),"PioneerGame",StringComparison.OrdinalIgnoreCase)?"ARC Raiders":CleanTitle(title);
    internal static bool SearchMatches(string name,string title,string query)
    {
        static string Key(string value)=>new(CleanTitle(value).Where(c=>!char.IsWhiteSpace(c)).ToArray());
        string haystack=Key(name+" "+Normalize(name)+".exe "+title+" "+FriendlyName(name,title));
        return query.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).All(term=>haystack.Contains(Key(term),StringComparison.OrdinalIgnoreCase));
    }
    internal static Dictionary<uint,List<string>> WindowTitles(bool includeHidden=false)
    {
        var titles=new Dictionary<uint,List<string>>();
        Native.EnumWindows((window,_)=>
        {
            if(!includeHidden&&!Native.IsWindowVisible(window))return true;
            int length=Native.GetWindowTextLength(window);if(length<=0)return true;
            var text=new StringBuilder(Math.Min(length+1,4096));Native.GetWindowText(window,text,text.Capacity);
            string title=CleanTitle(text.ToString());if(title.Length==0)return true;
            Native.GetWindowThreadProcessId(window,out uint id);
            if(!titles.TryGetValue(id,out var list))titles[id]=list=[];
            if(!list.Contains(title))list.Add(title);return true;
        },IntPtr.Zero);
        return titles;
    }
    internal static string Normalize(string? name)
    {
        string value=(name??"").Trim();
        return value.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)?value[..^4]:value;
    }
    internal static bool Matches(string? target,string? foreground)=>
        Normalize(target).Length>0 && string.Equals(Normalize(target),Normalize(foreground),StringComparison.OrdinalIgnoreCase);

    internal static string? ForegroundName()
    {
        var window=Native.GetForegroundWindow();
        if(window==IntPtr.Zero)return null;
        Native.GetWindowThreadProcessId(window,out uint id);
        try {using var process=Process.GetProcessById((int)id);return process.ProcessName;}
        catch {return null;}
    }

    internal static string? Choose(IWin32Window owner)
    {
        using var dialog=new Form{Text=L.T("프로세스 선택"),Size=new Size(680,530),MinimumSize=new Size(560,420),StartPosition=FormStartPosition.CenterParent,ShowInTaskbar=false,Font=new Font("맑은 고딕",10)};
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(16),ColumnCount=1,RowCount=4};
        root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.Percent,100));root.RowStyles.Add(new(SizeType.AutoSize));
        root.Controls.Add(new Label{Text=L.T("실행 중인 프로세스 검색 (이름 또는 창 제목)"),AutoSize=true});
        var search=new TextBox{Dock=DockStyle.Fill};root.Controls.Add(search);
        var list=new ListBox{Dock=DockStyle.Fill,IntegralHeight=false,DrawMode=DrawMode.OwnerDrawFixed};root.Controls.Add(list);
        var icons=new Dictionary<string,Bitmap>(StringComparer.OrdinalIgnoreCase);
        void SizeRows(){list.ItemHeight=Math.Max(dialog.Font.Height+10,(int)Math.Ceiling(28*dialog.DeviceDpi/96.0));}
        dialog.FontChanged+=(_,_)=>SizeRows();dialog.DpiChanged+=(_,_)=>SizeRows();SizeRows();
        list.DrawItem+=(_,e)=>
        {
            e.DrawBackground();if(e.Index<0||e.Index>=list.Items.Count)return;
            var entry=(Entry)list.Items[e.Index];
            if(!icons.TryGetValue(entry.Name,out var icon))icons[entry.Name]=icon=LoadIcon(entry.Name);
            int size=Math.Min(e.Bounds.Height-6,(int)Math.Ceiling(20*dialog.DeviceDpi/96.0));
            e.Graphics.DrawImage(icon,new Rectangle(e.Bounds.Left+4,e.Bounds.Top+(e.Bounds.Height-size)/2,size,size));
            var text=new Rectangle(e.Bounds.Left+size+12,e.Bounds.Top,Math.Max(1,e.Bounds.Width-size-16),e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics,entry.ToString(),e.Font,text,e.ForeColor,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        };
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true};
        var refresh=new Button{Text=L.T("새로 고침"),AutoSize=true};
        var select=new Button{Text=L.T("선택"),AutoSize=true,Enabled=false};
        var cancel=new Button{Text=L.T("취소"),AutoSize=true,DialogResult=DialogResult.Cancel};
        var fromWindow=new Button{Text=L.T("창에서 선택 (5초)"),AutoSize=true};
        actions.Controls.AddRange([refresh,select,fromWindow,cancel]);root.Controls.Add(actions);dialog.Controls.Add(root);dialog.AcceptButton=select;dialog.CancelButton=cancel;
        string? selectedName=null;
        using var captureTimer=new System.Windows.Forms.Timer{Interval=5000};
        fromWindow.Click+=(_,_)=>{dialog.WindowState=FormWindowState.Minimized;captureTimer.Start();};
        captureTimer.Tick+=(_,_)=>
        {
            captureTimer.Stop();string? captured=ForegroundName();
            using var self=Process.GetCurrentProcess();
            if(!string.IsNullOrEmpty(captured)&&!Matches(self.ProcessName,captured)){selectedName=captured;dialog.DialogResult=DialogResult.OK;dialog.Close();}
            else {dialog.WindowState=FormWindowState.Normal;dialog.Activate();MessageBox.Show(dialog,L.T("대상 창을 선택하지 못했습니다. 다시 시도하세요."),"Brightness Toggle",MessageBoxButtons.OK,MessageBoxIcon.Information);}
        };
        var entries=new List<Entry>();
        void Filter(){list.Items.Clear();foreach(var entry in entries.Where(e=>SearchMatches(e.Name,e.Title,search.Text)))list.Items.Add(entry);if(list.Items.Count>0)list.SelectedIndex=0;select.Enabled=list.SelectedIndex>=0;}
        void Refresh()
        {
            entries.Clear();
            var titles=WindowTitles();
            foreach(var process in Process.GetProcesses())
            {
                using(process)try
                {
                    if(process.Id==Environment.ProcessId)continue;
                    string name=process.ProcessName,title="";
                    try{title=process.MainWindowTitle;}catch{}
                    if(titles.TryGetValue((uint)process.Id,out var windows))
                        entries.AddRange(windows.Select(t=>new Entry(name,t)));
                    else entries.Add(new(name,CleanTitle(title)));
                }catch{}
            }
            entries=entries.Distinct().OrderByDescending(e=>e.Title.Length>0).ThenBy(e=>e.Name,StringComparer.OrdinalIgnoreCase).ThenBy(e=>e.Title).ToList();Filter();
        }
        search.TextChanged+=(_,_)=>Filter();refresh.Click+=(_,_)=>Refresh();list.SelectedIndexChanged+=(_,_)=>select.Enabled=list.SelectedIndex>=0;
        void Select(){if(list.SelectedItem is Entry entry){selectedName=entry.Name;dialog.DialogResult=DialogResult.OK;dialog.Close();}}
        select.Click+=(_,_)=>Select();list.DoubleClick+=(_,_)=>Select();Refresh();
        try{return dialog.ShowDialog(owner)==DialogResult.OK?selectedName:null;}
        finally{foreach(var icon in icons.Values)icon.Dispose();}
    }
    private sealed record Entry(string Name,string Title)
    {
        public override string ToString()=>Title.Length>0?Title+"  ("+Name+".exe)":FriendlyName(Name,Title) is {Length:>0} friendly?friendly+"  ("+Name+".exe)":Name+".exe";
    }
}
