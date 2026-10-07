using PCUpCheckTools.Sensors;

namespace PCUpCheckTools;

// [Part 275 → 277] 직접 쓴 진입점(csproj DISABLE_XAML_GENERATED_MAIN) — 인자로 갈린다.
//   --register-sensors : UAC 로 승격돼 실행됨 — 온도 센서 작업을 작업 스케줄러에 등록하고 끝난다(Part 277, 켤 때 한 번)
//   --sensors-task     : 작업 스케줄러가 최고 권한으로 실행 — WinUI 없이 온도만 파이프로 보낸다(Part 277)
//   그 밖              : 보통 실행 — 아래는 WinUI 가 자동으로 만들던 Main 과 같다(obj/App.g.i.cs).
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == SensorTask.RegisterArgument) return SensorTask.Register();
        if (args.Length >= 1 && args[0] == SensorTask.TaskArgument) return SensorHost.Run();

        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        global::Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
