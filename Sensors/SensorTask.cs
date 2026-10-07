using System.Diagnostics;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace PCUpCheckTools.Sensors;

// [Part 277] 온도 센서 모드를 작업 스케줄러 작업으로 — 「켤 때 한 번만 UAC」(시안 설정 창 · 온도 구역).
// - 등록(관리자 필요): 같은 exe 를 UAC 로 「--register-sensors」 실행 → 이 클래스의 Register 가 schtasks /Create /XML.
// - 실행(관리자 불필요): 본체가 schtasks /Run — 작업이 최고 권한으로 「--sensors-task」 를 띄운다.
// - XML 은 HwMonitorMini AutoStart.cs(Part 229)를 따른다: 실행 시간 제한 없음(PT0S — schtasks 옵션으로 만들면 72시간 뒤 강제 종료) ·
//   배터리여도 실행 · 이미 돌고 있으면 새로 띄우지 않음. 트리거는 없다(앱이 필요할 때 /Run).
public static class SensorTask
{
    public const string TaskName = @"PCUpCheckTools\Sensors";
    public const string RegisterArgument = "--register-sensors";
    public const string TaskArgument = "--sensors-task";

    private static string ExePath => Environment.ProcessPath ?? "";

    /// <summary>작업이 있고 지금 exe 를 가리키는가(exe 를 옮겼으면 false — 다시 등록해야 한다)</summary>
    public static bool IsRegistered()
    {
        if (Schtasks($"/Query /TN \"{TaskName}\" /XML", out string xml) != 0) return false;
        var m = Regex.Match(xml, @"<Command>(.*?)</Command>", RegexOptions.Singleline);
        if (!m.Success) return false;
        string cmd = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim().Trim('"');
        try
        {
            return string.Equals(Path.GetFullPath(cmd), Path.GetFullPath(ExePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>센서 모드 실행 — 등록돼 있으면 UAC 없이. 성공하면 true.</summary>
    public static bool Run() => Schtasks($"/Run /TN \"{TaskName}\"", out _) == 0;

    /// <summary>관리자 권한으로 등록하도록 UAC 를 띄우고 끝날 때까지 기다린다. 거부면 Declined.</summary>
    public static (bool Ok, bool Declined) RegisterElevated()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = RegisterArgument,
                UseShellExecute = true, // runas 는 ShellExecute 에서만
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p is null) return (false, false);
            if (!p.WaitForExit(30_000)) return (false, false);
            return (p.ExitCode == 0 && IsRegistered(), false);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED = UAC 거부
        {
            return (false, true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, false);
        }
    }

    /// <summary>「--register-sensors」 로 승격된 프로세스 안에서 — 작업을 만든다. 0 = 성공</summary>
    public static int Register()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 2;
        string user = identity.Name;
        string xml =
$@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>PC Up Check Tools — temperature sensors (started by the app when temperatures are on)</Description></RegistrationInfo>
  <Principals><Principal id=""Author""><UserId>{Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context=""Author""><Exec><Command>""{Escape(ExePath)}""</Command><Arguments>{TaskArgument}</Arguments></Exec></Actions>
</Task>";
        string file = Path.Combine(Path.GetTempPath(), $"pcupchecktools-task-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode); // schtasks /XML 은 UTF-16 을 기대
            return Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F", out _) == 0 ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 1;
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? value;

    // schtasks.exe — 창 없음 · 10초 제한. 출력은 콘솔(OEM) 코드 페이지(HwMonitorMini 와 같은 처리)
    private static int Schtasks(string arguments, out string output)
    {
        output = string.Empty;
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            var oem = System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = oem,
                StandardErrorEncoding = oem,
            };
            using var p = Process.Start(psi);
            if (p is null) return -1;
            output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(); } catch (InvalidOperationException) { }
                return -1;
            }
            return p.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            return -1;
        }
    }
}
