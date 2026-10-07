using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PCUpCheckTools.Hardware;

namespace PCUpCheckTools.Sensors;

// [Part 275 → 277] 관리자 센서 모드 — 작업 스케줄러 작업(SensorTask)이 최고 권한으로 「--sensors-task」 를 띄운다.
// - 온도 읽기는 HwMonitorMini HardwareReader(LibreHardwareMonitor) 그대로 — CPU·SSD·메인보드 온도는 관리자 권한이 있어야 읽힌다.
// - [Part 277] 이 쪽이 파이프를 만든다(이름 = 사용자 SID 로 고정, 접속은 그 사용자만). 본체가 접속하면 1초마다 온도 한 줄(JSON)을 쓴다.
//   Part 275 는 본체가 파이프를 만들고 UAC 로 이 모드를 띄웠다 — 실행마다 UAC 가 떠서 작업 스케줄러 방식으로 바꿨다.
// - 「모름」(null)도 그대로 보낸다 — 0 으로 채우지 않는다.
// - 창이 없다. 30초 안에 본체가 접속하지 않거나, 접속이 끊기면(본체 종료·온도 끔) 스스로 끝난다 — 관리자 프로세스를 남기지 않는다.
internal static class SensorHost
{
    public sealed record Reading(double? Cpu, double? Gpu, double? Ssd, double? Mbd);

    /// <summary>사용자마다 하나 — 다른 사용자의 센서 모드와 섞이지 않게 SID 를 붙인다</summary>
    public static string PipeName()
    {
        using var id = WindowsIdentity.GetCurrent();
        return "PCUpCheckTools.Sensors." + (id.User?.Value ?? "unknown");
    }

    public static int Run()
    {
        NamedPipeServerStream pipe;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var security = new PipeSecurity();
            // 접속은 이 사용자(본체는 일반 권한)와 관리자만 — 같은 SID 라도 본체 토큰은 관리자 그룹이 꺼져 있어 사용자 SID 로 허용한다
            security.AddAccessRule(new PipeAccessRule(id.User!, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            pipe = NamedPipeServerStreamAcl.Create(PipeName(), PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 3; // 이미 다른 센서 모드가 돌고 있다(FirstPipeInstance) — 하나면 충분
        }

        using (pipe)
        using (var reader = new HardwareReader())
        {
            reader.Open();
            try
            {
                using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    pipe.WaitForConnectionAsync(wait.Token).GetAwaiter().GetResult();
                }
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                while (pipe.IsConnected)
                {
                    var snapshot = new MetricsSnapshot();
                    reader.Read(snapshot);
                    writer.WriteLine(JsonSerializer.Serialize(new Reading(snapshot.CpuTemperature, snapshot.GpuTemperature, snapshot.SsdTemperature, snapshot.MotherboardTemperature)));
                    Thread.Sleep(1000);
                }
                return 0;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                return 0; // 접속이 없었거나 끊겼다 — 조용히 끝낸다
            }
        }
    }
}
