using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using PCUpCheckTools.Hardware;
using PCUpCheckTools.Services;
using PCUpCheckTools.Views;

namespace PCUpCheckTools;

// [Part 271 → 277] 진입점. 메인 창 없이 트레이에 상주하고, 트레이를 누르면 팝오버를 연다(AiMeter App 과 같은 틀).
// [Part 277] 시안대로 — 설정은 따로 뜨는 창(SettingsWindow) · 트레이 아이콘은 고른 것을 그린다(UpdateTray).
// [Part 291] WinUI Application(App.xaml) → Avalonia Application(XAML 없이 코드로). 배선은 종전과 같다.
public sealed class App : Application
{
    /// <summary>Program.Main 이 잡은 단일 실행 뮤텍스 — 끝낼 때 놓는다.</summary>
    internal static Mutex? SingleInstance;

    private IClassicDesktopStyleApplicationLifetime? lifetime;
    private TrayIconService? tray;
    private PopoverWindow? popover;
    private SettingsWindow? settingsWindow;
    private DispatcherTimer? timer;
    private HardwareMonitor? hardware;
    private SettingsStore? settings;
    private int? battery;
    private int themeTick;
    private bool lightTaskbar;
    // [Part 315] 업데이트 — 알림 창 · 확인 타이머(시작 1분 뒤, 그 뒤 하루마다) · 찾은 새 버전 · 설치 중
    private UpdateToast? toast;
    private DispatcherTimer? updateTimer;
    private UpdateService.Release? update;
    private bool updating;
    private int lastProgressPercent = -1;

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        // [Part 270] 토글·콤보 강조색을 Windows 강조색 대신 배터리 도구 액센트(emerald)로 고정.
        // [Part 291] Avalonia Fluent 는 Application.Resources 의 SystemAccentColor* 를 보지 않는다(AiMeter Part 287) → 테마 팔레트로.
        //   WinUI 다크 테마는 스위치를 Light2(#34D399)로 칠했고(원본 실측) Avalonia 는 강조색 그대로 칠한다 → 강조색 = #34D399
        Styles.Add(new FluentTheme
        {
            Palettes = { [ThemeVariant.Dark] = new ColorPaletteResources { Accent = Color.Parse("#34D399") } },
        });
        // [Part 291] WinUI 판과 같은 모양으로(원본 픽셀 실측) — 켜진 스위치 손잡이는 검정(Avalonia 기본 흰색),
        //   콤보는 흰 6% 채움 + 옅은 테두리(원본 #2B3647 · #333D4D, Avalonia 기본은 어두운 채움 + 밝은 테두리 #A0A3A7)
        var knob = new SolidColorBrush(Colors.Black);
        Resources["ToggleSwitchKnobFillOn"] = knob;
        Resources["ToggleSwitchKnobFillOnPointerOver"] = knob;
        Resources["ToggleSwitchKnobFillOnPressed"] = knob;
        // [Part 293] 꺼진 스위치도 — Part 291 은 켜진 상태만 대조했다. WinUI 값(AiMeter 0.1.11 실측 #18202F·#91959D·#CACCCF 를 알파로 환산):
        //   바탕 검정 8% · 테두리 흰 55% · 손잡이 흰 77% (Avalonia 기본: 바탕 투명 · 밝은 테두리 · 흰 손잡이)
        var offFill = new SolidColorBrush(Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        var offStroke = new SolidColorBrush(Color.FromArgb(0x8B, 0xFF, 0xFF, 0xFF));
        var offKnob = new SolidColorBrush(Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));
        foreach (var state in new[] { "", "PointerOver", "Pressed" })
        {
            Resources["ToggleSwitchFillOff" + state] = offFill;
            Resources["ToggleSwitchStrokeOff" + state] = offStroke;
            Resources["ToggleSwitchKnobFillOff" + state] = offKnob;
        }
        //   「위치 초기화」 버튼 · 미세조정 숫자 칸도 원본은 같은 #2B3647(Avalonia 기본 #4B5462 · #121923). 바탕을 직접 준 버튼(탭·칸 등)은 영향 없음
        var fill = new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));
        var edge = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        Resources["ComboBoxBackground"] = fill;
        Resources["ComboBoxBorderBrush"] = edge;
        Resources["ButtonBackground"] = fill;
        Resources["TextControlBackground"] = fill;
        Resources["TextControlBorderBrush"] = edge;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        lifetime = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;

        StartupService.SyncPath(); // [Part 283] 폴더를 옮기거나 새 버전을 다른 폴더에 풀었으면 자동 실행이 지금 exe 를 가리키게

        settings = SettingsStore.Load();
        S.Apply(settings.Language); // 화면 문구를 만들기 전에 언어부터
        lightTaskbar = TaskbarTheme.IsLight();

        // [Part 273] 하드웨어 수치 + 작업 표시줄 줄(HwMonitorMini 의 수집·도킹)
        hardware = new HardwareMonitor(settings);
        popover = new PopoverWindow(settings, hardware, OpenSettings);
        tray = new TrayIconService(this);
        tray.LeftClick += () => popover.Toggle();
        tray.ExitRequested += Shutdown;
        hardware.Updated += UpdateTray;
        hardware.Start();

        // PureBattery net48 판과 같은 1분 주기
        timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
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

        // [Part 315] 업데이트 알림
        toast = new UpdateToast();
        toast.InstallRequested += () => _ = SafeInstallAsync();
        toast.PageRequested += () => OpenUrl(update?.PageUrl ?? UpdateService.ReleasesPage);
        toast.LaterRequested += () =>
        {
            if (update is not null && !updating)
            {
                settings.DismissedUpdate = update.Version.ToString(3);
                settings.Save();
            }
            toast.Hide();
        };
        if (Environment.GetCommandLineArgs().Contains(UpdateService.UpdatedArgument)) toast.ShowUpdated(UpdateService.CurrentText);
        else if (Environment.GetCommandLineArgs().Contains(UpdateService.UpdateFailedArgument))
            toast.ShowError(S.T("새 파일로 바꾸지 못해 지금 버전으로 되돌렸습니다. 다운로드 페이지에서 받아 주세요.", "Couldn't replace the app files, so this version was kept. Please download the update from the release page."));
        updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) }; // 시작 직후의 부하를 피해 1분 뒤 첫 확인
        updateTimer.Tick += async (_, _) =>
        {
            updateTimer.Interval = TimeSpan.FromHours(24);
            // 부가 기능의 예외가 트레이 앱 전체를 끝내지 않게 — async void 처리기의 예외는 앱을 죽인다(시험 중 실측)
            try { await CheckUpdateAsync(manual: false); }
            catch (Exception) { }
        };
        updateTimer.Start();

        base.OnFrameworkInitializationCompleted();
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
        if (settings is null || hardware is null) return;
        if (settingsWindow is null)
        {
            settingsWindow = new SettingsWindow(settings, hardware, () => CheckUpdateAsync(manual: true));
            settingsWindow.LanguageChanged += () => { popover?.Render(); UpdateTray(); };
            settingsWindow.TrayChanged += () => { UpdateTray(); popover?.Render(); };
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show();
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

    /// <summary>[Part 315] 새 버전 확인. 자동 확인은 설정이 켜져 있을 때만, 「나중에」 고른 버전은 알림 창 없이 팝오버 안내만.
    /// 「지금 확인」(manual)은 설정과 무관하게 묻고, 새 버전이면 알림 창을 띄운다.</summary>
    private async Task<UpdateService.CheckResult> CheckUpdateAsync(bool manual)
    {
        if (settings is null || (!manual && !settings.CheckUpdates) || updating) return new UpdateService.CheckResult(UpdateService.CheckStatus.Failed, null);
        UpdateService.CheckResult result;
        try { result = await UpdateService.CheckAsync(); }
        catch (Exception) { return new UpdateService.CheckResult(UpdateService.CheckStatus.Failed, null); } // 확인 실패가 앱을 끝내지 않게
        if (result.Status == UpdateService.CheckStatus.Available && result.Release is UpdateService.Release r)
        {
            update = r;
            string version = r.Version.ToString(3);
            popover?.SetUpdate(version, () => _ = SafeInstallAsync());
            if (manual || settings.DismissedUpdate != version) toast?.ShowAvailable(version, UpdateService.CanInstall(r));
        }
        return result;
    }

    /// <summary>[Part 315] 설치 중 예상 못 한 예외 — 앱을 죽이지 않고 알림 창에 실패로 보인다</summary>
    private async Task SafeInstallAsync()
    {
        try { await InstallUpdateAsync(); }
        catch (Exception)
        {
            updating = false;
            toast?.ShowError(S.T("업데이트 중 문제가 생겼습니다. 다운로드 페이지에서 받아 주세요.", "Something went wrong while updating. Please download it from the release page."));
        }
    }

    /// <summary>[Part 315] 받아서 검증하고 도우미를 띄운 뒤 앱을 끝낸다. 도우미가 덮어쓰고 다시 실행한다.</summary>
    private async Task InstallUpdateAsync()
    {
        if (update is null || updating || toast is null) return;
        if (!UpdateService.CanInstall(update)) { OpenUrl(update.PageUrl); return; }
        updating = true;
        lastProgressPercent = -1;
        toast.ShowProgress(null);
        var progress = new Progress<double>(f =>
        {
            int pct = (int)(f * 100);
            if (pct == lastProgressPercent) return; // 받는 동안 수백 번 오므로 1% 단위로만 다시 그린다
            lastProgressPercent = pct;
            toast.ShowProgress(f);
        });
        string? error = await UpdateService.PrepareAsync(update, progress);
        if (error is null)
        {
            Shutdown(); // 도우미가 이 프로세스가 끝나길 기다린다
            return;
        }
        updating = false;
        toast.ShowError(error);
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private void Shutdown()
    {
        timer?.Stop();
        updateTimer?.Stop();
        toast?.CloseForExit();
        hardware?.Dispose(); // 작업 표시줄에서 줄을 떼고 창을 없앤다 · 온도 연결을 끊으면 센서 모드도 끝난다
        tray?.Dispose();
        settingsWindow?.Close();
        popover?.CloseForExit();
        SingleInstance?.ReleaseMutex();
        SingleInstance = null; // Program.Main 의 using 이 Dispose 한다
        lifetime?.Shutdown();
    }
}
