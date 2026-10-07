using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PCUpCheckTools.Hardware;
using PCUpCheckTools.Services;
using PCUpCheckTools.Views;

namespace PCUpCheckTools;

// [Part 271 → 277] 진입점. 메인 창 없이 트레이에 상주하고, 트레이를 누르면 팝오버를 연다(AiMeter App 과 같은 틀).
// [Part 277] 시안대로 — 설정은 따로 뜨는 창(SettingsWindow) · 트레이 아이콘은 고른 것을 그린다(UpdateTray).
public partial class App : Application
{
    private Mutex? singleInstance;
    private TrayIconService? tray;
    private PopoverWindow? popover;
    private SettingsWindow? settingsWindow;
    private DispatcherQueueTimer? timer;
    private HardwareMonitor? hardware;
    private SettingsStore? settings;
    private DispatcherQueue? dispatcher;
    private int? battery;
    private int themeTick;
    private bool lightTaskbar;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 두 번째 실행은 조용히 끝낸다 — 트레이 아이콘이 두 개 생기지 않게(PureBattery net48 판은 두 개가 떴다)
        singleInstance = new Mutex(true, "PCUpCheckTools.SingleInstance", out bool created);
        if (!created)
        {
            Exit();
            return;
        }

        dispatcher = DispatcherQueue.GetForCurrentThread();
        settings = SettingsStore.Load();
        S.Apply(settings.Language); // 화면 문구를 만들기 전에 언어부터
        lightTaskbar = TaskbarTheme.IsLight();

        // [Part 273] 하드웨어 수치 + 작업 표시줄 줄(HwMonitorMini 의 수집·도킹)
        hardware = new HardwareMonitor(settings, dispatcher);
        popover = new PopoverWindow(settings, hardware, OpenSettings);
        tray = new TrayIconService();
        tray.LeftClick += () => popover.Toggle();
        tray.ExitRequested += Shutdown;
        hardware.Updated += UpdateTray;
        hardware.Start();

        // PureBattery net48 판과 같은 1분 주기
        timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMinutes(1);
        timer.Tick += (_, _) => UpdateBattery();
        timer.Start();
        UpdateBattery();

        if (settings.IsFirstRun)
        {
            settings.Save(); // 다음 실행부터는 안내하지 않는다
            popover.ShowFirstRunGuide();
        }
        // --show: 시작하자마자 팝오버 · --settings: 설정 창(점검용 — AiMeter 와 같은 인자)
        else if (Environment.GetCommandLineArgs().Contains("--show"))
        {
            popover.Toggle();
        }
        if (Environment.GetCommandLineArgs().Contains("--settings")) OpenSettings();
    }

    private void UpdateBattery()
    {
        battery = Battery.ReadPercent();
        popover?.SetBattery(battery);
        UpdateTray();
    }

    /// <summary>[Part 277] 설정 창 — 하나만. 열려 있으면 앞으로</summary>
    private void OpenSettings()
    {
        if (settings is null || hardware is null || dispatcher is null) return;
        if (settingsWindow is null)
        {
            settingsWindow = new SettingsWindow(settings, hardware, dispatcher);
            settingsWindow.LanguageChanged += () => { popover?.Render(); UpdateTray(); };
            settingsWindow.TrayChanged += () => { UpdateTray(); popover?.Render(); };
            settingsWindow.Closed += (_, _) => settingsWindow = null;
        }
        settingsWindow.Activate();
    }

    /// <summary>[Part 277] 트레이 아이콘 = 고른 것(시안 ④). 자동 = 배터리가 있으면 배터리, 없으면 CPU 사용률</summary>
    private void UpdateTray()
    {
        if (tray is null || settings is null || hardware is null) return;
        if (++themeTick >= 5) { themeTick = 0; lightTaskbar = TaskbarTheme.IsLight(); } // 숫자 기본색 — 라이트 작업 표시줄에서 흰 글자가 안 보이지 않게
        var s = hardware.Latest;
        var normal = lightTaskbar ? System.Drawing.Color.FromArgb(0x0F, 0x17, 0x2A) : System.Drawing.Color.FromArgb(0xF8, 0xFA, 0xFC);
        System.Drawing.Color Step(System.Drawing.Color c) =>
            c == Thresholds.Warn ? System.Drawing.Color.FromArgb(0xF8, 0x55, 0x55) : c == Thresholds.Caution ? System.Drawing.Color.FromArgb(0xFB, 0x92, 0x3C) : normal;

        string mode = settings.TrayShow == SettingsStore.TrayAuto ? (battery is null ? SettingsStore.TrayCpu : SettingsStore.TrayBattery) : settings.TrayShow;
        string? text = null;
        var color = normal;
        bool app = false;
        switch (mode)
        {
            case SettingsStore.TrayBattery:
                if (battery is int b) { text = b.ToString(); var (r, g, bl) = Battery.LevelColor(b); color = System.Drawing.Color.FromArgb(r, g, bl); }
                break;
            case SettingsStore.TrayCpu:
                if (s.CpuUsagePercent is double c) { text = $"{Math.Round(c):0}"; color = Step(Thresholds.UsageColor(c, Thresholds.CpuWarnPercent)); }
                break;
            case SettingsStore.TrayCpuTemp:
                if (hardware.Temperatures && s.CpuTemperature is double t) { text = $"{Math.Round(t):0}"; color = Step(Thresholds.TempColor(t)); }
                break;
            default:
                app = true;
                break;
        }

        // 툴팁 — 아는 값만(모르는 것은 빼거나 「모름」)
        var line1 = new List<string>();
        line1.Add(battery is int bb ? S.T($"배터리 {bb}%", $"Battery {bb}%") : S.T("배터리 모름", "Battery ?"));
        if (s.CpuUsagePercent is double cu) line1.Add($"CPU {cu:0}%");
        if (s.RamUsagePercent is double ru) line1.Add($"RAM {ru:0}%");
        string tip = "PC Up Check Tools\n" + string.Join(" · ", line1);
        if (hardware.Temperatures && s.CpuTemperature is double ct) tip += S.T($"\nCPU 온도 {ct:0}°C", $"\nCPU temp {ct:0}°C");
        tray.Show(text, color, app, tip);
    }

    private void Shutdown()
    {
        timer?.Stop();
        hardware?.Dispose(); // 작업 표시줄에서 줄을 떼고 창을 없앤다 · 온도 연결을 끊으면 센서 모드도 끝난다
        tray?.Dispose();
        settingsWindow?.Close();
        popover?.Close();
        singleInstance?.ReleaseMutex();
        singleInstance?.Dispose();
        singleInstance = null;
        Exit();
    }
}
