using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using PCUpCheckTools.Hardware;
using PCUpCheckTools.Services;
using static PCUpCheckTools.Views.Ui;

namespace PCUpCheckTools.Views;

// [Part 271 → 277] 트레이를 누르면 오른쪽 아래에 뜨는 팝오버 — 시안 ①(https://claude.ai/artifact/WFiDTpShbqxxpfMT9SqoAY).
// - 요약 타일 4(배터리 · CPU · RAM · CPU 온도) + 탭 3(하드웨어 · 배터리 · 내 PC 사양) + 바닥 「사양 스캔」 · 톱니(설정 창).
//   Part 271~276 은 카드를 세로로 쌓아 화면 높이를 거의 다 썼고(200% 에서 1,390px) 설정 20여 개가 이 팝오버 안에 있었다 → 설정은 SettingsWindow 로.
// - 창 처리(테두리 없음 · 라이트 디스미스 · 위치)는 AiMeter PopoverWindow 와 같다.
// - 1초마다 바뀌는 값은 글자만 바꾼다 — 다시 만들면 누르던 버튼이 사라진다(Part 273).
// [Part 291] WinUI 3 → Avalonia 12(AiMeter Part 287 과 같은 대응). 구성·문구·가드는 그대로이고 컨트롤만 바꿨다.
public sealed class PopoverWindow : Window
{
    private const double PopWidth = 360;

    private readonly SettingsStore settings;
    private readonly HardwareMonitor hardware;
    private readonly Action openSettings;
    private readonly StackPanel body;
    private readonly Border bodyBorder;
    private readonly Border footerBorder;
    private readonly Button scanButton;
    private bool isVisible;
    private bool exiting;
    private bool showFirstRunGuide;
    private string tab = "hw";
    private int? percent;
    private bool scanning;
    private bool justCopied;
    private string? scanError;
    private LastScan.Summary? lastScan = LastScan.Load();
    private readonly TextBlock[] tileValues = new TextBlock[4];
    private readonly Dictionary<MetricKind, TextBlock> hardwareValues = new();
    private DateTimeOffset lastHidden = DateTimeOffset.MinValue;
    // [Part 315] 새 버전 안내 — 알림 창을 닫거나 「나중에」 를 골라도 여기에는 남는다
    private string? updateVersion;
    private Action? installUpdate;

    public PopoverWindow(SettingsStore settings, HardwareMonitor hardware, Action openSettings)
    {
        this.settings = settings;
        this.hardware = hardware;
        this.openSettings = openSettings;

        body = new StackPanel { Spacing = 14 };
        bodyBorder = new Border { Padding = new Thickness(16, 16, 16, 14), Child = body };
        // 창을 보이기 전에 잰 높이는 실제 배치와 다를 수 있다(Part 271) — 내용 크기가 정해질 때마다 다시 맞춘다.
        // [Part 291] 배치 도중에 다시 재지 않게 디스패처로 미룬다
        body.SizeChanged += (_, _) => { if (isVisible) Dispatcher.UIThread.Post(Place); };
        // [Part 291] 화면보다 길면 스크롤 — WinUI 판은 넘치는 아래가 잘렸다. 높이는 스크롤 안 내용으로 잰다(AiMeter Part 287)
        var scroll = new ScrollViewer
        {
            Content = bodyBorder,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        scanButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Height = 40,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            CornerRadius = new CornerRadius(8),
            Background = Brush(Color.FromArgb(0x1F, 0x34, 0xD3, 0x99)),
            BorderBrush = Brush(Color.FromArgb(0x80, 0x34, 0xD3, 0x99)),
            BorderThickness = new Thickness(1),
            Foreground = Brush(Hex("#A7F3D0")),
        };
        scanButton.Click += async (_, _) => await ScanAsync();
        var gear = IconButton("", S.T("설정 창 열기", "Open settings")); // [Part 291] 톱니 글리프 U+E713 — 이식 중 이 글자가 빠져 톱니가 안 보였다(눈에 안 보이는 글자라 diff 로 확인)
        gear.Click += (_, _) => { HideNow(); openSettings(); };

        var footer = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
        };
        footer.Children.Add(scanButton);
        Grid.SetColumn(gear, 1);
        footer.Children.Add(gear);
        footerBorder = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            BorderBrush = Brush(Line),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = footer,
        };

        var root = new Grid
        {
            Width = PopWidth,
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x0F, 0x17, 0x2A)),
            RowDefinitions = { new RowDefinition(), new RowDefinition { Height = GridLength.Auto } },
        };
        root.Children.Add(scroll);
        Grid.SetRow(footerBorder, 1);
        root.Children.Add(footerBorder);

        Content = root;
        Title = "PC Up Check Tools";
        Width = PopWidth;
        RequestedThemeVariant = ThemeVariant.Dark;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur };
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            HideNow();
        };
        Closing += (_, e) =>
        {
            if (exiting) return;
            e.Cancel = true;
            HideNow();
        };
        // 바깥을 누르면 닫힌다(라이트 디스미스)
        Deactivated += (_, _) => HideNow();
        Opened += (_, _) => RoundCorners();

        hardware.Updated += () => { if (isVisible) UpdateLive(); };
        Render();
    }

    /// <summary>[Part 291] 앱을 끝낼 때만 진짜로 닫는다(평소 닫기 요청은 숨김 — AiMeter Part 287)</summary>
    public void CloseForExit()
    {
        exiting = true;
        Close();
    }

    /// <summary>Windows 11 둥근 모서리 — 테두리 없는 창은 기본이 각진 모서리다. DWM 이 덧그리는 얇은 테두리는 없앤다(종전과 같은 값).</summary>
    private void RoundCorners()
    {
        if (TryGetPlatformHandle()?.Handle is not IntPtr hwnd) return;
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        int noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref noBorder, sizeof(int));
    }

    // ── 열기/닫기 ─────────────────────────────────────────────

    public void Toggle()
    {
        if (isVisible) { HideNow(); return; }
        // 팝오버가 열린 채 트레이를 다시 누르면, 먼저 포커스를 잃어 닫히고 곧바로 이 호출이 온다 → 다시 열지 않는다
        if ((DateTimeOffset.UtcNow - lastHidden).TotalMilliseconds < 250) return;
        Render();
        Place();
        Show();
        Activate();
        isVisible = true;
    }

    /// <summary>처음 실행 — 팝오버를 열고 안내 한 줄을 보인다(PureBattery net48 의 실행 전 안내창 대신)</summary>
    public void ShowFirstRunGuide()
    {
        showFirstRunGuide = true;
        Toggle();
    }

    private void HideNow()
    {
        if (!isVisible) return;
        isVisible = false;
        showFirstRunGuide = false;
        lastHidden = DateTimeOffset.UtcNow;
        Hide();
    }

    /// <summary>작업 영역(작업 표시줄 제외) 오른쪽 아래에 내용 높이만큼</summary>
    private void Place()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        double scale = screen.Scaling;
        var area = screen.WorkingArea; // 물리 픽셀
        double margin = 12;
        double maxHeight = area.Height / scale - margin * 2;
        // [Part 291] WinUI 처럼 root.Measure 로 재면 안 된다 — Avalonia 는 저장값을 돌려준다(AiMeter Part 287 함정).
        //   AiMeter 처럼 UpdateLayout() 만 하면 처음 열 때 0 이 나왔다(실측: 높이 71px 창이 작업 표시줄 위에 겹침) — 아직 보이지 않는 창은 배치를 건너뛴다.
        //   본문과 아래 줄을 무효화하고 무한 높이로 직접 잰다(무효화했으므로 저장값이 아니다)
        bodyBorder.InvalidateMeasure();
        footerBorder.InvalidateMeasure();
        bodyBorder.Measure(new Size(PopWidth, double.PositiveInfinity));
        footerBorder.Measure(new Size(PopWidth, double.PositiveInfinity));
        double height = Math.Min(Math.Ceiling(bodyBorder.DesiredSize.Height + footerBorder.DesiredSize.Height), maxHeight);
        Height = height;
        int w = (int)Math.Round(PopWidth * scale);
        int h = (int)Math.Round(height * scale);
        int m = (int)Math.Round(margin * scale);
        var position = new PixelPoint(area.X + area.Width - w - m, area.Y + area.Height - h - m);
        // [Part 291] 보이기 전의 창은 Width·Height 가 아직 적용되지 않아 기본 크기(실측 물리 2134×1170)다. 그대로 오른쪽 아래로 옮기면
        //   창 대부분이 옆 모니터에 걸려 창 배율이 그쪽(1)으로 바뀌고, 보일 때 360 이 물리 360px 로 잡혔다(실측 DesktopScaling 2 → 1).
        //   먼저 Win32 로 최종 위치·물리 크기로 옮겨 창 전체를 이 화면 안에 둔다
        if (TryGetPlatformHandle()?.Handle is IntPtr hwnd)
            SetWindowPos(hwnd, IntPtr.Zero, position.X, position.Y, w, h, 0x0004 /* SWP_NOZORDER */ | 0x0010 /* SWP_NOACTIVATE */);
        Position = position;
    }

    // ── 값 ───────────────────────────────────────────────────

    /// <summary>[Part 315] App 이 새 버전을 찾으면 — 맨 위에 안내 한 줄과 「업데이트」</summary>
    public void SetUpdate(string version, Action install)
    {
        updateVersion = version;
        installUpdate = install;
        if (isVisible) Render();
    }

    /// <summary>배터리 — App 의 1분 타이머</summary>
    public void SetBattery(int? value)
    {
        percent = value;
        if (isVisible) UpdateLive();
    }

    // ── 그리기 ───────────────────────────────────────────────

    /// <summary>전체를 다시 만든다 — 열 때 · 탭을 바꿀 때 · 언어를 바꿨을 때 · 스캔 상태가 바뀔 때</summary>
    public void Render()
    {
        body.Children.Clear();
        hardwareValues.Clear();
        scanButton.Content = scanning ? S.T("모으는 중…", "Collecting…") : S.T("사양 스캔 → 클립보드에 복사", "Scan specs → copy to clipboard");
        scanButton.IsEnabled = !scanning;

        if (showFirstRunGuide)
        {
            body.Children.Add(Notice(S.T(
                "트레이 아이콘을 누르면 이 창이 열립니다. 끝내려면 아이콘을 오른쪽 클릭한 뒤 「끝내기」 를 고르세요. 설정은 오른쪽 아래 톱니에 있습니다.",
                "Click the tray icon to open this window. To quit, right-click it and choose \"Exit\". Settings are behind the gear at the bottom right."), Emerald));
        }

        if (updateVersion is not null && installUpdate is not null)
        {
            var row = new Grid { ColumnSpacing = 10, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            row.Children.Add(new KeepTextBlock(S.T($"새 버전 {updateVersion} 이 나왔습니다.", $"Version {updateVersion} is available.")) { FontSize = 12.5, Foreground = Brush(Text1), VerticalAlignment = VerticalAlignment.Center });
            var go = new Button
            {
                Content = S.T("업데이트", "Update"), FontSize = 12.5, FontWeight = FontWeight.SemiBold, Padding = new Thickness(10, 4, 10, 4),
                CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                Background = Brush(Color.FromArgb(0x1F, 0x34, 0xD3, 0x99)), BorderBrush = Brush(Color.FromArgb(0x80, 0x34, 0xD3, 0x99)), Foreground = Brush(Hex("#A7F3D0")),
            };
            var install = installUpdate;
            go.Click += (_, _) => { HideNow(); install(); };
            Grid.SetColumn(go, 1);
            row.Children.Add(go);
            body.Children.Add(new Border
            {
                Padding = new Thickness(12, 8, 8, 8), CornerRadius = new CornerRadius(8), Child = row,
                Background = Brush(Color.FromArgb(0x14, 0x34, 0xD3, 0x99)), BorderBrush = Brush(Color.FromArgb(0x59, 0x34, 0xD3, 0x99)), BorderThickness = new Thickness(1),
            });
        }

        // 요약 타일 4
        var tiles = new Grid { ColumnSpacing = 8, RowSpacing = 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition() }, RowDefinitions = { new RowDefinition(), new RowDefinition() } };
        string[] names = { S.T("배터리", "Battery"), S.T("CPU 사용률", "CPU usage"), S.T("RAM 사용률", "RAM usage"), S.T("CPU 온도", "CPU temp") };
        for (int i = 0; i < 4; i++)
        {
            var value = new TextBlock { FontSize = 24, FontWeight = FontWeight.SemiBold };
            tileValues[i] = value;
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(new TextBlock { Text = names[i], FontSize = 12, Foreground = Brush(Text2) });
            stack.Children.Add(value);
            var tile = new Border { Background = Brush(Surface), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9), Child = stack };
            Grid.SetColumn(tile, i % 2);
            Grid.SetRow(tile, i / 2);
            tiles.Children.Add(tile);
        }
        body.Children.Add(tiles);

        // 탭 3
        var tabs = new Grid { ColumnSpacing = 4, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition(), new ColumnDefinition() } };
        (string Id, string Name)[] list = { ("hw", S.T("하드웨어", "Hardware")), ("bat", S.T("배터리", "Battery")), ("spec", S.T("내 PC 사양", "My PC specs")) };
        for (int i = 0; i < list.Length; i++)
        {
            var (id, name) = list[i];
            bool on = id == tab;
            var b = new Button
            {
                Content = name,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Height = 34,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(0),
                Background = on ? Brush(Raised) : Brushes.Transparent,
                Foreground = Brush(on ? Text1 : Text2),
            };
            AutomationProperties.SetName(b, name + (on ? S.T(" (선택됨)", " (selected)") : ""));
            b.Click += (_, _) => { if (tab == id) return; tab = id; Render(); };
            Grid.SetColumn(b, i);
            tabs.Children.Add(b);
        }
        body.Children.Add(new Border { Padding = new Thickness(3), CornerRadius = new CornerRadius(9), Background = Brush(Surface), Child = tabs });

        if (tab == "bat") RenderBattery();
        else if (tab == "spec") RenderSpec();
        else RenderHardware();

        UpdateLive();
        if (isVisible) Place();
    }

    private void RenderHardware()
    {
        var kinds = new List<MetricKind> { MetricKind.Upload, MetricKind.Download, MetricKind.CpuClock, MetricKind.RamGb, MetricKind.GpuUsage, MetricKind.NpuUsage, MetricKind.DiskActive };
        if (hardware.Temperatures) kinds.AddRange(new[] { MetricKind.GpuTemp, MetricKind.SsdTemp, MetricKind.MbdTemp });
        var grid = new Grid { RowSpacing = 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        for (int i = 0; i < kinds.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = MetricCatalog.MenuLabel(kinds[i]), FontSize = 13, Foreground = Brush(Text2) };
            var value = new TextBlock { FontSize = 13, FontFamily = MonoFont,FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetRow(name, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(name);
            grid.Children.Add(value);
            hardwareValues[kinds[i]] = value;
        }
        body.Children.Add(grid);
        string? caption = !hardware.Temperatures
            ? S.T("온도는 설정 창 「온도」 에서 켤 수 있습니다(처음 한 번 관리자 권한).", "Turn on temperatures in Settings › Temperatures (admin permission once).")
            : hardware.TemperatureState is TemperatureClient.Status.Running ? null
            : hardware.TemperatureState is TemperatureClient.Status.Starting ? S.T("온도 센서를 시작하는 중…", "Starting the temperature sensors…")
            : S.T("온도를 받지 못하고 있습니다. 설정 창 「온도」 에서 껐다 켜 보세요.", "No temperature data. Try turning temperatures off and on in Settings.");
        if (caption is not null) body.Children.Add(Caption(caption));
    }

    private void RenderBattery()
    {
        if (percent is int p)
        {
            var (r, g, b) = Battery.LevelColor(p);
            var level = Color.FromArgb(0xFF, r, g, b);
            var number = new TextBlock
            {
                FontSize = 44, FontWeight = FontWeight.SemiBold, Foreground = Brush(level),
                Inlines = new InlineCollection { new Run(p.ToString(CultureInfo.InvariantCulture)), new Run("%") { FontSize = 22 } },
            };
            body.Children.Add(number);
            var cells = new Grid { ColumnSpacing = 3, Height = 12 };
            int on = (int)Math.Round(p / 10.0, MidpointRounding.AwayFromZero);
            for (int i = 0; i < 10; i++)
            {
                cells.ColumnDefinitions.Add(new ColumnDefinition());
                var cell = new Border { CornerRadius = new CornerRadius(2), Background = Brush(i < on ? level : Raised) };
                Grid.SetColumn(cell, i);
                cells.Children.Add(cell);
            }
            AutomationProperties.SetName(cells, S.T($"배터리 {p}%", $"Battery {p}%"));
            body.Children.Add(cells);
            body.Children.Add(Caption(S.T("1분마다 갱신 · 색: 80%↑ 초록 · 30%↑ 파랑 · 10%↑ 주황 · 9%↓ 빨강",
                "Updates every minute · 80%+ green · 30%+ blue · 10%+ orange · 9% or less red")));
        }
        else
        {
            // 모르면 숫자를 지어내지 않는다 — 이유도 추정하지 않는다(데스크톱이라 없는지, 읽지 못했는지 구분할 수 없다)
            body.Children.Add(new TextBlock { Text = S.T("모름", "Unknown"), FontSize = 28, FontWeight = FontWeight.SemiBold, Foreground = Brush(Text2) });
            body.Children.Add(Caption(S.T("Windows 가 배터리 잔량을 알려 주지 않았습니다.", "Windows didn't report a battery level.")));
        }
    }

    private void RenderSpec()
    {
        if (scanning)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            // [Part 291] Avalonia 기본 컨트롤에 ProgressRing 이 없다 — 같은 자리에 무한 진행 막대
            row.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 40, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(Caption(S.T("이 PC 사양을 모으는 중입니다. 몇 초 걸립니다.", "Collecting this PC's specs. This takes a few seconds.")));
            body.Children.Add(row);
            return;
        }
        if (scanError is not null) body.Children.Add(Notice(scanError, Orange));
        if (lastScan is null)
        {
            body.Children.Add(Caption(S.T(
                "아직 스캔하지 않았습니다. 아래 버튼을 누르면 이 PC 사양을 모아 클립보드에 복사합니다. 인터넷으로 보내지 않습니다 — pcupcheck.com 에서 「스캔 데이터 붙여넣기」 를 누르세요.",
                "Not scanned yet. The button below collects this PC's specs and copies them to the clipboard. Nothing is sent online — paste them on pcupcheck.com with \"Paste scan data\".")));
            return;
        }
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 8, ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(76) }, new ColumnDefinition() } };
        for (int i = 0; i < lastScan.Rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = lastScan.Rows[i].Name, FontSize = 13, Foreground = Brush(Text2) };
            var value = new KeepTextBlock(lastScan.Rows[i].Value) { FontSize = 13 };
            Grid.SetRow(name, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(name);
            grid.Children.Add(value);
        }
        body.Children.Add(grid);
        body.Children.Add(new Border { Height = 1, Background = Brush(Line) });
        var bottom = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        string when = lastScan.ScannedAt.ToLocalTime().ToString(S.IsKorean ? "M/d HH:mm" : "MMM d HH:mm", S.Culture);
        bottom.Children.Add(new TextBlock
        {
            Text = justCopied ? S.T($"마지막 스캔 {when} · 클립보드에 복사됨", $"Scanned {when} · copied") : S.T($"마지막 스캔 {when}", $"Scanned {when}"),
            FontSize = 12, Foreground = Brush(Text2), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var go = new HyperlinkButton { Content = NoUnderline(S.T("진단하러 가기 ›", "Diagnose ›")), NavigateUri = new Uri("https://pcupcheck.com/scanner"), FontSize = 12, Padding = new Thickness(4, 2, 4, 2) };
        ToolTip.SetTip(go, "pcupcheck.com/scanner");
        Grid.SetColumn(go, 1);
        bottom.Children.Add(go);
        body.Children.Add(bottom);
    }

    /// <summary>1초마다 — 타일·하드웨어 값의 글자와 색만 바꾼다</summary>
    private void UpdateLive()
    {
        var s = hardware.Latest;
        if (tileValues[0] is not null)
        {
            if (percent is int p)
            {
                var (r, g, b) = Battery.LevelColor(p);
                SetTile(0, $"{p}%", Color.FromArgb(0xFF, r, g, b));
            }
            else SetTile(0, S.T("모름", "?"), Text2);
            SetTile(1, s.CpuUsagePercent is double c ? $"{c:0}%" : "--", Level(Thresholds.UsageColor(s.CpuUsagePercent, Thresholds.CpuWarnPercent)));
            SetTile(2, s.RamUsagePercent is double m ? $"{m:0}%" : "--", Level(Thresholds.UsageColor(s.RamUsagePercent, Thresholds.RamWarnPercent)));
            if (!hardware.Temperatures) SetTile(3, S.T("꺼짐", "Off"), Text2);
            else SetTile(3, s.CpuTemperature is double t ? $"{t:0}°C" : "--", Level(Thresholds.TempColor(s.CpuTemperature)));
        }
        foreach (var (kind, block) in hardwareValues)
        {
            var segments = MetricCatalog.Segments(kind, s);
            if (segments.Length == 0) continue;
            var (text, color) = segments[^1];
            block.Text = text.Trim();
            block.Foreground = Brush(Level(color));
        }
    }

    private void SetTile(int i, string text, Color color)
    {
        tileValues[i].Text = text;
        tileValues[i].Foreground = Brush(color);
    }

    // ── 사양 스캔 ─────────────────────────────────────────────

    private async Task ScanAsync()
    {
        if (scanning) return;
        scanning = true;
        scanError = null;
        justCopied = false;
        tab = "spec"; // 결과를 바로 보여 준다
        Render();

        var result = await SpecScanner.RunAsync(); // 백그라운드에서 PowerShell 을 기다리고 UI 스레드로 돌아온다
        scanning = false;
        if (result.Json is string json)
        {
            lastScan = LastScan.FromJson(json);
            LastScan.Save(json);
            if (await CopyAsync(json)) justCopied = true;
            else scanError = S.T("클립보드에 넣지 못했습니다. 다시 눌러 주세요.", "Couldn't write to the clipboard. Please try again.");
        }
        else
        {
            scanError = result.Error;
        }
        Render();
    }

    /// <summary>클립보드에 넣는다. 넣기 실패만 실패로.
    /// [Part 291] WinRT DataPackage + Flush(Part 272) → Avalonia 클립보드. 앱이 상주하므로 넣은 글은 앱이 사는 동안 남는다.</summary>
    private async Task<bool> CopyAsync(string text)
    {
        if (Clipboard is not IClipboard clipboard) return false;
        for (int i = 0; i < 4; i++)
        {
            try
            {
                await clipboard.SetTextAsync(text);
                return true;
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ExternalException)
            {
                await Task.Delay(150); // 다른 프로세스(클립보드 기록 등)가 잠깐 잡고 있으면 다시
            }
        }
        return false;
    }

    // ── Win32 ───────────────────────────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
}
