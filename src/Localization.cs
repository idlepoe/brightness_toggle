namespace BrightnessToggle;

internal static class L
{
    internal static string Language { get; set; } = "en";
    private static readonly Dictionary<string,string> English = new()
    {
        ["이미 실행 중입니다. 트레이의 밝기 전환 아이콘을 확인하세요."]="Brightness Toggle is already running. Look for the sun icon in the system tray.",
        ["실행 실패: "]="Unable to start: ",
        ["새 프리셋"]="New preset", ["프리셋"]="Preset",
        ["디스플레이에 접근할 수 없습니다."]="Cannot access this display.",
        ["이 디스플레이의 감마 보정을 읽을 수 없습니다."]="Cannot read gamma correction for this display.",
        ["현재 화면 상태를 읽을 수 없습니다."]="Cannot read the current display state.",
        ["밝기 적용 실패: 디스플레이·HDR·그래픽 드라이버 설정을 확인하세요."]="Cannot apply gamma. Check the display, HDR mode, and graphics driver.",
        ["드라이버가 밝기 적용을 확인하지 못했습니다. HDR·게임 화면 모드를 확인하세요."]="The driver did not confirm the gamma change. Check HDR and the game's display mode.",
        [" 이전 상태 복원도 실패했습니다."]=" Restoring the previous state also failed.",
        ["원래 밝기 복원 실패: "]="Could not restore original gamma: ",
        ["밝기 보정: 0% · 원래 화면"]="Gamma correction: 0% · Original",
        ["현재: default · 보정 0%"]="Current: default · Correction 0%",
        ["조정 시 바로 미리보기"]="Live preview while adjusting",
        ["X를 누르면 시스템 트레이로 숨겨집니다. 완전 종료: 트레이 우클릭 → 종료"]="X hides this window to the tray. To quit: right-click the sun icon → Exit.",
        ["원래 화면"]="Original",
        ["Brightness Toggle 1.0.3 · 밝기 프리셋"]="Brightness Toggle 1.0.3 · Gamma presets",
        ["한 번 눌러 밝기 전환"]="One-key brightness toggle",
        ["추가"]="Add", ["삭제"]="Delete", ["프리셋 이름"]="Preset name",
        ["선택한 프리셋 적용"]="Apply selected preset", ["대상 디스플레이"]="Target display",
        ["전환 단축키 (한 번 누르면 다음 프리셋)"]="Cycle hotkey (press once for next preset)",
        ["다음 프리셋"]="Next preset", ["설정 열기"]="Open settings",
        ["종료 (원래 밝기 복원)"]="Exit (restore original gamma)",
        ["디스플레이를 찾을 수 없습니다."]="No display was found.",
        ["밝기 아이콘을 읽을 수 없습니다."]="Cannot load the brightness icon.",
        ["밝기: default (0%)"]="Gamma: default (0%)",
        ["적용 요청 완료 · "]="Applied · ", ["밝기 적용 실패"]="Gamma adjustment failed",
        ["설정 저장 실패: "]="Could not save settings: ",
        [": 다음 프리셋"]=": Next preset",
        ["단축키 사용 중입니다. 다른 F키를 선택하세요."]="That hotkey is in use. Choose another F key.",
        ["밝기 프로그램이 트레이에서 실행 중입니다"]="Brightness Toggle is running in the tray",
        ["작업 표시줄 오른쪽의 해 아이콘을 확인하세요. 숨겨져 있으면 ∧를 누르세요. 설정/종료: 아이콘 우클릭"]="Look for the sun icon near the clock (or under hidden icons). Right-click it for settings or Exit.",
        ["\n복원을 다시 시도한 뒤 종료하세요."]="\nTry restoring again before exiting.",
        ["{0}   (보정 {1:+0;-0;0}%)"]="{0}   (Correction {1:+0;-0;0}%)",
        ["밝기 보정: {0:+0;-0;0}%"]="Gamma correction: {0:+0;-0;0}%",
        ["현재: {0} · 보정 {1:+0;-0;0}%"]="Current: {0} · Correction {1:+0;-0;0}%",
        ["밝기: {0} ({1:+0;-0;0}%)"]="Gamma: {0} ({1:+0;-0;0}%)"
    };
    internal static string T(string korean)=>Language=="ko"?korean:English.GetValueOrDefault(korean,korean);
    internal static string F(string template,params object[] args)=>string.Format(T(template),args);
    internal static string Canonical(string value)=>English.FirstOrDefault(p=>p.Value==value).Key??value;
    internal static void Apply(Control control)
    {
        // Never translate user-authored preset names or combo-box choices.
        if(control is not TextBoxBase && control is not ListBox && control is not ComboBox)
            control.Text=T(Canonical(control.Text));
        foreach(Control child in control.Controls)Apply(child);
    }
    internal static void ApplyMenu(ToolStripItemCollection items)
    {
        foreach(ToolStripItem item in items)
        {
            item.Text=T(Canonical(item.Text ?? string.Empty));
            if(item is ToolStripDropDownItem parent)ApplyMenu(parent.DropDownItems);
        }
    }
}
