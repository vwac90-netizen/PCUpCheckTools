using Microsoft.Win32;

namespace PCUpCheckTools.Services;

// [Part 270] AiMeter StartupService 와 같은 파일. 시작 프로그램 등록 — HKCU\...\Run 의 「PCUpCheckTools」 값(관리자 권한 불필요). PureBattery(net48) 의 「시작프로그램 폴더에 복사」 안내를 대신한다.
// 상태는 설정 파일이 아니라 레지스트리에서 직접 읽는다 — 사용자가 작업 관리자 「시작 앱」 에서 꺼도 화면이 사실대로 보이게.
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PCUpCheckTools";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string v && string.Equals(v, Command, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// [Part 283] 앱 폴더를 옮기거나 새 버전을 다른 폴더에 풀면 Run 값이 옛 exe 를 가리킨 채 남았다 — 다음 로그인에 아무것도 안 뜨거나 옛 판이 뜨고,
    /// 설정 창에는 「꺼짐」 으로 보여 사용자는 고장을 몰랐다. 켜져 있는(값이 있는) 경우에만, 같은 파일 이름의 다른 경로면 지금 exe 로 고쳐 쓴다.
    /// 값이 없으면(꺼짐) 켜지 않고, 다른 프로그램 이름이면 건드리지 않는다. 실패는 조용히 — 다음 실행에 다시 맞춘다.
    /// </summary>
    public static void SyncPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is not string value) return;
            if (string.Equals(value, Command, StringComparison.OrdinalIgnoreCase)) return;
            string registered = value.Trim().Trim('"');
            if (!string.Equals(Path.GetFileName(registered), Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase)) return;
            key.SetValue(ValueName, Command);
        }
        catch
        {
        }
    }

    /// <returns>레지스트리에 쓰거나 지우는 데 성공하면 true</returns>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return IsEnabled() == enabled;
        }
        catch
        {
            return false;
        }
    }
}
