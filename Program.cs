using Avalonia;
using PCUpCheckTools.Sensors;

namespace PCUpCheckTools;

// [Part 275 → 277] 직접 쓴 진입점 — 인자로 갈린다.
//   --register-sensors : UAC 로 승격돼 실행됨 — 온도 센서 작업을 작업 스케줄러에 등록하고 끝난다(Part 277, 켤 때 한 번)
//   --sensors-task     : 작업 스케줄러가 최고 권한으로 실행 — 화면 없이 온도만 파이프로 보낸다(Part 277)
//   그 밖              : 보통 실행
// [Part 291] WinUI Application.Start → Avalonia AppBuilder(AiMeter Part 287 과 같다). 창이 다 숨어도 끝나지 않게(트레이 상주) 명시적 종료만.
//   단일 실행 뮤텍스는 App.OnLaunched 에서 여기로 옮겼다 — 화면 틀을 띄우기 전에 두 번째 실행을 끝낸다.
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == SensorTask.RegisterArgument) return SensorTask.Register();
        if (args.Length >= 1 && args[0] == SensorTask.TaskArgument) return SensorHost.Run();

        // 두 번째 실행은 조용히 끝낸다 — 트레이 아이콘이 두 개 생기지 않게(PureBattery net48 판은 두 개가 떴다)
        using var singleInstance = new Mutex(true, "PCUpCheckTools.SingleInstance", out bool created);
        if (!created) return 0;
        App.SingleInstance = singleInstance;
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }
}
