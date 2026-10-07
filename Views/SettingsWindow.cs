using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PCUpCheckTools.Hardware;
using PCUpCheckTools.Services;
using Windows.Graphics;
using WinRT.Interop;
using static PCUpCheckTools.Views.Ui;

namespace PCUpCheckTools.Views;

// [Part 277] 설정 창 — 시안 ②. 팝오버 밖의 보통 창(크기 조절 · 최소화 · 닫기 · 바깥을 눌러도 안 닫힘).
// Part 273~276 은 설정 20여 개가 폭 340 의 라이트 디스미스 팝오버 안에 있었다(오프셋처럼 여러 번 만지는 설정에 맞지 않음).
// 처리기 규칙(AiMeter Part 247): 값이 같으면 무시 · 처리기 안에서 그 컨트롤을 다시 만들지 않는다(다시 그릴 때는 디스패처로 미룬다).
public sealed class SettingsWindow : Window
{
    private readonly SettingsStore settings;
    private readonly HardwareMonitor hardware;
    private readonly DispatcherQueue dispatcher;
    private readonly StackPanel nav;
    private readonly StackPanel content;
    private string section = "general";
    private int selectedSlot;
    private bool temperatureDeclined;
    private bool saveFailed;
    private TextBlock? temperatureStatus;

    /// <summary>언어를 바꿨다 — App 이 팝오버·트레이를 새 언어로</summary>
    public event Action? LanguageChanged;

    /// <summary>트레이 아이콘 표시를 바꿨다 · 온도를 켜거나 껐다 — App 이 트레이를 다시 그린다</summary>
    public event Action? TrayChanged;

    public SettingsWindow(SettingsStore settings, HardwareMonitor hardware, DispatcherQueue dispatcher)
    {
        this.settings = settings;
        this.hardware = hardware;
        this.dispatcher = dispatcher;

        nav = new StackPanel { Spacing = 2, Padding = new Thickness(8, 12, 8, 12), Width = 200 };
        content = new StackPanel { Spacing = 14, Padding = new Thickness(24, 20, 24, 24), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var navBorder = new Border { Child = nav, BorderBrush = Brush(Line), BorderThickness = new Thickness(0, 0, 1, 0) };

        var root = new Grid
        {
            RequestedTheme = ElementTheme.Dark,
            Background = Brush(Navy),
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() },
        };
        root.Children.Add(navBorder);
        Grid.SetColumn(scroll, 1);
        root.Children.Add(scroll);
        Content = root;

        Title = S.T("PC Up Check Tools 설정", "PC Up Check Tools settings");
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = false;
        AppWindow.TitleBar.BackgroundColor = Navy;
        AppWindow.TitleBar.InactiveBackgroundColor = Navy;
        AppWindow.TitleBar.ButtonBackgroundColor = Navy;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Navy;
        AppWindow.TitleBar.ForegroundColor = Text1;
        AppWindow.TitleBar.ButtonForegroundColor = Text1;
        if (AppWindow.Presenter is OverlappedPresenter p) { p.PreferredMinimumWidth = 640; p.PreferredMinimumHeight = 420; }

        double scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(780 * scale), (int)(620 * scale)));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(area.X + (area.Width - (int)(780 * scale)) / 2, area.Y + (area.Height - (int)(620 * scale)) / 2));

        hardware.Updated += UpdateStatus;
        Closed += (_, _) => hardware.Updated -= UpdateStatus;
        Render();
    }

    // ── 그리기 ───────────────────────────────────────────────

    public void Render()
    {
        Title = S.T("PC Up Check Tools 설정", "PC Up Check Tools settings");
        nav.Children.Clear();
        (string Id, string Name)[] list =
        {
            ("general", S.T("일반", "General")),
            ("temp", S.T("온도", "Temperatures")),
            ("strip", S.T("작업 표시줄 줄", "Taskbar strip")),
            ("slots", S.T("표시 항목", "Strip items")),
            ("about", S.T("정보", "About")),
        };
        foreach (var (id, name) in list)
        {
            bool on = id == section;
            var b = new Button
            {
                Content = name,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Height = 40,
                FontSize = 14,
                FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Background = on ? Brush(Surface) : new SolidColorBrush(Colors.Transparent),
                Foreground = Brush(on ? Text1 : Hex("#CBD5E1")),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, name + (on ? S.T(" (선택됨)", " (selected)") : ""));
            b.Click += (_, _) => { if (section == id) return; section = id; Render(); };
            nav.Children.Add(b);
        }

        content.Children.Clear();
        temperatureStatus = null;
        content.Children.Add(new TextBlock { Text = list.First(x => x.Id == section).Name, FontSize = 22, FontWeight = FontWeights.SemiBold });
        switch (section)
        {
            case "temp": RenderTemperature(); break;
            case "strip": RenderStrip(); break;
            case "slots": RenderSlots(); break;
            case "about": RenderAbout(); break;
            default: RenderGeneral(); break;
        }
        if (saveFailed) content.Children.Add(Notice(S.T("설정을 저장하지 못했습니다. 다음 실행 때는 기본값으로 돌아갑니다.", "Couldn't save the setting. It will reset next launch."), Orange));
    }

    private void Save() => saveFailed = !settings.Save();

    /// <summary>처리기가 끝난 뒤 다시 그린다(Part 247)</summary>
    private void Rerender() => dispatcher.TryEnqueue(Render);

    private void RenderGeneral()
    {
        string[] langs = { SettingsStore.LangAuto, SettingsStore.LangKo, SettingsStore.LangEn };
        string[] trays = { SettingsStore.TrayAuto, SettingsStore.TrayBattery, SettingsStore.TrayCpu, SettingsStore.TrayCpuTemp, SettingsStore.TrayApp };
        var startupFailed = Notice(S.T("시작 프로그램 등록을 바꾸지 못했습니다.", "Couldn't change the startup setting."), Orange);
        startupFailed.Visibility = Visibility.Collapsed;
        content.Children.Add(Card(new List<FrameworkElement>
        {
            // 이름은 두 언어를 함께 — 지금 언어를 못 읽어도 찾을 수 있게(AiMeter Part 266)
            ComboRow(S.T("언어 · Language", "Language · 언어"), null,
                new[] { S.T("자동 (Windows 따름)", "Auto (follow Windows)"), "한국어", "English" },
                Array.IndexOf(langs, settings.Language), i =>
                {
                    if (langs[i] == settings.Language) return;
                    settings.Language = langs[i];
                    Save();
                    S.Apply(settings.Language);
                    dispatcher.TryEnqueue(() => { Render(); LanguageChanged?.Invoke(); });
                }),
            ComboRow(S.T("트레이 아이콘에 표시", "Tray icon shows"),
                S.T("자동 = 배터리가 있으면 배터리, 없으면 CPU 사용률. CPU 온도는 「온도」 를 켜야 숫자가 나옵니다.", "Auto = battery if there is one, otherwise CPU usage. CPU temperature needs Temperatures on."),
                new[] { S.T("자동", "Auto"), S.T("배터리 잔량", "Battery level"), S.T("CPU 사용률", "CPU usage"), S.T("CPU 온도", "CPU temperature"), S.T("앱 아이콘만", "App icon only") },
                Array.IndexOf(trays, settings.TrayShow), i =>
                {
                    if (trays[i] == settings.TrayShow) return;
                    settings.TrayShow = trays[i];
                    Save();
                    TrayChanged?.Invoke();
                }),
            SwitchRow(S.T("Windows 시작 때 자동 실행", "Start with Windows"), null, StartupService.IsEnabled(), on =>
            {
                if (on == StartupService.IsEnabled()) return;
                startupFailed.Visibility = StartupService.Set(on) ? Visibility.Collapsed : Visibility.Visible;
            }),
        }));
        content.Children.Add(startupFailed);
    }

    private void RenderTemperature()
    {
        temperatureStatus = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        content.Children.Add(Card(new List<FrameworkElement>
        {
            SwitchRow(S.T("CPU · VGA · SSD · 메인보드 온도", "CPU, GPU, SSD and motherboard temperatures"),
                S.T("처음 켤 때 한 번 관리자 권한(UAC)을 묻고 작업 스케줄러에 등록합니다. 그 뒤로는 묻지 않습니다. 관리자 권한으로는 온도 센서만 읽습니다.",
                    "Asks for admin permission (UAC) once, the first time, and registers a scheduled task. After that it won't ask. Admin rights are used only to read temperature sensors."),
                settings.TemperatureSensors, on =>
                {
                    if (on == settings.TemperatureSensors) return;
                    temperatureDeclined = !hardware.SetTemperatures(on); // 처음이면 여기서 UAC 창
                    TrayChanged?.Invoke();
                    Rerender(); // 스위치·상태가 바뀌었다
                }),
            temperatureStatus,
        }));
        if (temperatureDeclined)
            content.Children.Add(Notice(S.T("관리자 권한을 허용하지 않았거나 등록하지 못해 온도를 켜지 않았습니다.", "Temperatures stay off: admin permission was not granted or registration failed."), Orange));
        UpdateStatus();
    }

    /// <summary>온도 상태 한 줄 — 1초마다(설정 창이 열려 있을 때)</summary>
    private void UpdateStatus()
    {
        if (temperatureStatus is null) return;
        var (text, color) = !settings.TemperatureSensors
            ? (S.T("꺼짐", "Off"), Text2)
            : hardware.TemperatureState switch
            {
                TemperatureClient.Status.Running => (S.T("● 온도를 받는 중", "● Receiving temperatures"), Emerald),
                TemperatureClient.Status.Starting => (S.T("● 시작하는 중…", "● Starting…"), Text2),
                _ => (S.T("● 받지 못하고 있습니다 — 껐다 켜면 다시 등록합니다", "● No data — turn it off and on to register again"), Orange),
            };
        temperatureStatus.Text = text;
        temperatureStatus.Foreground = Brush(color);
    }

    private void RenderStrip()
    {
        string[] anchors = { SettingsStore.AnchorTrayLeft, SettingsStore.AnchorTrayRight, SettingsStore.AnchorTaskbarLeft };
        string[] overflows = { SettingsStore.OverflowShrink, SettingsStore.OverflowHide };
        var taskbars = TaskbarDocker.TaskbarNames();
        var items = new List<FrameworkElement>
        {
            SwitchRow(S.T("작업 표시줄에 하드웨어 수치 표시", "Show hardware numbers on the taskbar"), S.T("클릭은 아래 작업 표시줄로 그대로 통과합니다.", "Clicks pass through to the taskbar."),
                settings.TaskbarStrip, on =>
                {
                    if (on == settings.TaskbarStrip) return;
                    settings.TaskbarStrip = on;
                    Save();
                    hardware.SetStripEnabled(on);
                }),
            ComboRow(S.T("위치", "Position"), null,
                new[] { S.T("시계 왼쪽", "Left of the clock"), S.T("시계 오른쪽", "Right of the clock"), S.T("작업 표시줄 왼쪽 끝", "Taskbar left edge") },
                Array.IndexOf(anchors, settings.Anchor), i =>
                {
                    if (anchors[i] == settings.Anchor) return;
                    settings.Anchor = anchors[i];
                    Save();
                    hardware.ApplyPosition();
                }),
            OffsetRow(),
            ComboRow(S.T("자리가 모자랄 때", "When there isn't room"), S.T("시계 왼쪽일 때 작업 표시줄 앱 버튼을 덮지 않습니다.", "Left of the clock: never covers taskbar app buttons."),
                new[] { S.T("칸을 줄여서 표시", "Show fewer columns"), S.T("숨기기", "Hide") },
                Array.IndexOf(overflows, settings.StripOverflow), i =>
                {
                    if (overflows[i] == settings.StripOverflow) return;
                    settings.StripOverflow = overflows[i];
                    Save();
                }),
        };
        if (taskbars.Count > 1)
        {
            // 작업 표시줄이 하나뿐이면 고를 것이 없다 — 칸을 만들지 않는다
            items.Add(ComboRow(S.T("표시할 작업 표시줄", "Which taskbar"), null, taskbars.ToArray(),
                Math.Clamp(settings.TaskbarIndex, 0, taskbars.Count - 1), i =>
                {
                    if (i == settings.TaskbarIndex) return;
                    settings.TaskbarIndex = i;
                    Save();
                    hardware.ApplyTaskbar();
                }));
        }
        var reset = new Button { Content = S.T("위치 초기화", "Reset position") };
        reset.Click += (_, _) =>
        {
            settings.Anchor = SettingsStore.AnchorTrayLeft;
            settings.OffsetX = 0;
            settings.OffsetY = 0;
            Save();
            hardware.ApplyPosition();
            Rerender();
        };
        items.Add(reset);
        content.Children.Add(Card(items));
    }

    /// <summary>X/Y 미세조정(px) — 원본 HwMonitorMini OffsetDialog 의 두 숫자 칸</summary>
    private FrameworkElement OffsetRow()
    {
        var grid = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto } } };
        grid.Children.Add(Texts(S.T("미세조정 X · Y (px)", "Offset X · Y (px)"), S.T("+X 오른쪽 · +Y 아래", "+X right · +Y down")));
        NumberBox Box(int value, string name, Action<int> changed)
        {
            var box = new NumberBox { Value = value, Minimum = -2000, Maximum = 2000, SmallChange = 1, Width = 104, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, name);
            box.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) changed((int)Math.Round(e.NewValue)); };
            return box;
        }
        var x = Box(settings.OffsetX, S.T("가로 미세조정 (+오른쪽)", "Horizontal offset (+right)"), v =>
        {
            if (v == settings.OffsetX) return;
            settings.OffsetX = v;
            Save();
            hardware.ApplyPosition();
        });
        var y = Box(settings.OffsetY, S.T("세로 미세조정 (+아래)", "Vertical offset (+down)"), v =>
        {
            if (v == settings.OffsetY) return;
            settings.OffsetY = v;
            Save();
            hardware.ApplyPosition();
        });
        Grid.SetColumn(x, 1);
        Grid.SetColumn(y, 2);
        grid.Children.Add(x);
        grid.Children.Add(y);
        return grid;
    }

    /// <summary>2줄 × 4열 미리보기 — 칸을 누르면 아래 콤보가 그 칸의 항목을 고른다(시안 ② 표시 항목)</summary>
    private void RenderSlots()
    {
        content.Children.Add(Caption(S.T("칸을 누르면 그 칸에 보일 항목을 고릅니다. 위 줄과 아래 줄, 열 4개입니다.", "Click a cell to choose what it shows: two rows, four columns.")));
        var slots = hardware.Slots;
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8, Padding = new Thickness(14), CornerRadius = new CornerRadius(10), Background = Brush(Hex("#020617")) };
        for (int c = 0; c < 4; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < MetricCatalog.SlotCount; i++)
        {
            int slot = i;
            bool on = slot == selectedSlot;
            string position = S.T($"{slot / 2 + 1}열 {(slot % 2 == 0 ? "위" : "아래")}", $"Column {slot / 2 + 1} {(slot % 2 == 0 ? "top" : "bottom")}");
            var b = new Button
            {
                Content = new TextBlock { Text = MetricCatalog.MenuLabel(slots[slot]), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Height = 40,
                CornerRadius = new CornerRadius(6),
                Background = Brush(Navy),
                BorderBrush = Brush(on ? Emerald : Raised),
                BorderThickness = new Thickness(on ? 2 : 1),
                Foreground = Brush(slots[slot] == MetricKind.Empty ? Text2 : Text1),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, $"{position}: {MetricCatalog.MenuLabel(slots[slot])}" + (on ? S.T(" (선택됨)", " (selected)") : ""));
            b.Click += (_, _) => { if (selectedSlot == slot) return; selectedSlot = slot; Render(); };
            Grid.SetColumn(b, slot / 2);
            Grid.SetRow(b, slot % 2);
            grid.Children.Add(b);
        }
        content.Children.Add(grid);

        var kinds = MetricCatalog.Selectable(hardware.Temperatures).ToArray();
        bool npuMissing = hardware.HasNpu == false;
        string selected = S.T($"{selectedSlot / 2 + 1}열 {(selectedSlot % 2 == 0 ? "위" : "아래")}", $"Column {selectedSlot / 2 + 1} {(selectedSlot % 2 == 0 ? "top" : "bottom")}");
        var names = kinds.Select(k => MetricCatalog.MenuLabel(k) + (k == MetricKind.NpuUsage && npuMissing ? S.T(" — NPU 없음", " — no NPU") : "")).ToArray();
        var enabled = kinds.Select(k => !(k == MetricKind.NpuUsage && npuMissing)).ToArray();
        var reset = new Button { Content = S.T("기본 배치로", "Default layout") };
        reset.Click += (_, _) =>
        {
            hardware.ApplySlots(MetricCatalog.Default(hardware.Temperatures));
            Save();
            Rerender();
        };
        content.Children.Add(Card(new List<FrameworkElement>
        {
            ComboRow(selected, hardware.Temperatures ? null : S.T("온도 항목은 「온도」 를 켜면 고를 수 있습니다.", "Temperature items appear when Temperatures is on."),
                names, Array.IndexOf(kinds, slots[selectedSlot]), i =>
                {
                    var next = hardware.Slots;
                    if (next[selectedSlot] == kinds[i]) return;
                    next[selectedSlot] = kinds[i];
                    hardware.ApplySlots(next);
                    Save();
                    Rerender(); // 미리보기 칸 글자
                }, enabled),
            reset,
        }));
    }

    private void RenderAbout()
    {
        string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "?";
        var link = new HyperlinkButton { Content = "pcupcheck.com", NavigateUri = new Uri("https://pcupcheck.com"), Padding = new Thickness(0) };
        content.Children.Add(Card(new List<FrameworkElement>
        {
            new TextBlock { Text = $"PC Up Check Tools {version}", FontSize = 15, FontWeight = FontWeights.SemiBold },
            Caption(S.T("배터리 · 하드웨어 수치 · 사양 스캔. 사양은 인터넷으로 보내지 않고 이 PC 의 클립보드와 설정 폴더(%APPDATA%\\PCUpCheckTools)에만 남습니다.",
                "Battery, hardware numbers and spec scan. Specs are never sent online — they stay in this PC's clipboard and settings folder (%APPDATA%\\PCUpCheckTools).")),
            link,
        }));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
