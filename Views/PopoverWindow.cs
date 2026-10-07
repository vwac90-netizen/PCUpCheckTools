using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PCUpCheckTools.Hardware;
using PCUpCheckTools.Services;
using Windows.Foundation;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;
using WinRT.Interop;
using static PCUpCheckTools.Views.Ui;

namespace PCUpCheckTools.Views;

// [Part 271 → 277] 트레이를 누르면 오른쪽 아래에 뜨는 팝오버 — 시안 ①(https://claude.ai/artifact/WFiDTpShbqxxpfMT9SqoAY).
// - 요약 타일 4(배터리 · CPU · RAM · CPU 온도) + 탭 3(하드웨어 · 배터리 · 내 PC 사양) + 바닥 「사양 스캔」 · 톱니(설정 창).
//   Part 271~276 은 카드를 세로로 쌓아 화면 높이를 거의 다 썼고(200% 에서 1,390px) 설정 20여 개가 이 팝오버 안에 있었다 → 설정은 SettingsWindow 로.
// - 창 처리(테두리 없음 · 라이트 디스미스 · 위치)는 AiMeter PopoverWindow 와 같다.
// - 1초마다 바뀌는 값은 글자만 바꾼다 — 다시 만들면 누르던 버튼이 사라진다(Part 273).
public sealed class PopoverWindow : Window
{
    private const double PopWidth = 360;

    private readonly SettingsStore settings;
    private readonly HardwareMonitor hardware;
    private readonly Action openSettings;
    private readonly Grid root;
    private readonly StackPanel body;
    private readonly Button scanButton;
    private bool isVisible;
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
    private SubclassProc? subclassProc; // 창이 사는 동안 GC 되지 않게 붙잡아 둔다

    public PopoverWindow(SettingsStore settings, HardwareMonitor hardware, Action openSettings)
    {
        this.settings = settings;
        this.hardware = hardware;
        this.openSettings = openSettings;

        body = new StackPanel { Spacing = 14, Padding = new Thickness(16, 16, 16, 14) };
        // 창을 보이기 전에 잰 높이는 실제 배치와 다를 수 있다(Part 271) — 내용 크기가 정해질 때마다 다시 맞춘다
        body.SizeChanged += (_, _) => { if (isVisible) Place(); };

        scanButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 40,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            CornerRadius = new CornerRadius(8),
            Background = Brush(Windows.UI.Color.FromArgb(0x1F, 0x34, 0xD3, 0x99)),
            BorderBrush = Brush(Windows.UI.Color.FromArgb(0x80, 0x34, 0xD3, 0x99)),
            Foreground = Brush(Hex("#A7F3D0")),
        };
        scanButton.Click += async (_, _) => await ScanAsync();
        var gear = IconButton("", S.T("설정 창 열기", "Open settings"));
        gear.Click += (_, _) => { HideNow(); openSettings(); };

        var footer = new Grid
        {
            Padding = new Thickness(12, 10, 12, 10),
            ColumnSpacing = 8,
            BorderBrush = Brush(Line),
            BorderThickness = new Thickness(0, 1, 0, 0),
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
        };
        footer.Children.Add(scanButton);
        Grid.SetColumn(gear, 1);
        footer.Children.Add(gear);

        root = new Grid
        {
            Width = PopWidth,
            RequestedTheme = ElementTheme.Dark,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xEE, 0x0F, 0x17, 0x2A)),
            RowDefinitions = { new RowDefinition(), new RowDefinition { Height = GridLength.Auto } },
        };
        root.Children.Add(body);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);

        var esc = new KeyboardAccelerator { Key = VirtualKey.Escape };
        esc.Invoked += (_, e) => { e.Handled = true; HideNow(); };
        root.KeyboardAccelerators.Add(esc);
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        Content = root;
        Title = "PC Up Check Tools";
        SystemBackdrop = new DesktopAcrylicBackdrop();

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Closing += (_, e) => { e.Cancel = true; HideNow(); };

        var hwnd = WindowNative.GetWindowHandle(this);
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        int noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref noBorder, sizeof(int));
        // AiMeter Part 246: 크기 조절을 끈 창에도 대화상자 프레임이 되살아나 흰 띠가 남는다 → WM_NCCALCSIZE 에서 창 전체를 내용 영역으로
        subclassProc = (h, msg, wParam, lParam, id, data) =>
            msg == 0x0083 /* WM_NCCALCSIZE */ && wParam != IntPtr.Zero ? IntPtr.Zero : DefSubclassProc(h, msg, wParam, lParam);
        SetWindowSubclass(hwnd, subclassProc, 1, IntPtr.Zero);
        Closed += (_, _) => RemoveWindowSubclass(hwnd, subclassProc, 1);

        // 바깥을 누르면 닫힌다(라이트 디스미스)
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) HideNow();
        };

        hardware.Updated += () => { if (isVisible) UpdateLive(); };
        Render();
    }

    // ── 열기/닫기 ─────────────────────────────────────────────

    public void Toggle()
    {
        if (isVisible) { HideNow(); return; }
        // 팝오버가 열린 채 트레이를 다시 누르면, 먼저 포커스를 잃어 닫히고 곧바로 이 호출이 온다 → 다시 열지 않는다
        if ((DateTimeOffset.UtcNow - lastHidden).TotalMilliseconds < 250) return;
        Render();
        Place();
        AppWindow.Show();
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
        AppWindow.Hide();
    }

    /// <summary>작업 영역(작업 표시줄 제외) 오른쪽 아래에 내용 높이만큼</summary>
    private void Place()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double margin = 12;
        double maxHeight = area.Height / scale - margin * 2;
        root.Measure(new Size(PopWidth, double.PositiveInfinity));
        double height = Math.Min(Math.Ceiling(root.DesiredSize.Height), maxHeight);
        int w = (int)Math.Round(PopWidth * scale);
        int h = (int)Math.Round(height * scale);
        int m = (int)Math.Round(margin * scale);
        AppWindow.MoveAndResize(new RectInt32(area.X + area.Width - w - m, area.Y + area.Height - h - m, w, h));
    }

    // ── 값 ───────────────────────────────────────────────────

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

        // 요약 타일 4
        var tiles = new Grid { ColumnSpacing = 8, RowSpacing = 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition() }, RowDefinitions = { new RowDefinition(), new RowDefinition() } };
        string[] names = { S.T("배터리", "Battery"), S.T("CPU 사용률", "CPU usage"), S.T("RAM 사용률", "RAM usage"), S.T("CPU 온도", "CPU temp") };
        for (int i = 0; i < 4; i++)
        {
            var value = new TextBlock { FontSize = 24, FontWeight = FontWeights.SemiBold };
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
        var tabs = new Grid { ColumnSpacing = 4, Padding = new Thickness(3), CornerRadius = new CornerRadius(9), Background = Brush(Surface),
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition(), new ColumnDefinition() } };
        (string Id, string Name)[] list = { ("hw", S.T("하드웨어", "Hardware")), ("bat", S.T("배터리", "Battery")), ("spec", S.T("내 PC 사양", "My PC specs")) };
        for (int i = 0; i < list.Length; i++)
        {
            var (id, name) = list[i];
            bool on = id == tab;
            var b = new Button
            {
                Content = name,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 34,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(0),
                Background = on ? Brush(Raised) : new SolidColorBrush(Colors.Transparent),
                Foreground = Brush(on ? Text1 : Text2),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, name + (on ? S.T(" (선택됨)", " (selected)") : ""));
            b.Click += (_, _) => { if (tab == id) return; tab = id; Render(); };
            Grid.SetColumn(b, i);
            tabs.Children.Add(b);
        }
        body.Children.Add(tabs);

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
            var value = new TextBlock { FontSize = 13, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
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
            var level = Windows.UI.Color.FromArgb(0xFF, r, g, b);
            var number = new TextBlock { FontSize = 44, FontWeight = FontWeights.SemiBold, Foreground = Brush(level) };
            number.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = p.ToString(CultureInfo.InvariantCulture) });
            number.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "%", FontSize = 22 });
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
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cells, S.T($"배터리 {p}%", $"Battery {p}%"));
            body.Children.Add(cells);
            body.Children.Add(Caption(S.T("1분마다 갱신 · 색: 80%↑ 초록 · 30%↑ 파랑 · 10%↑ 주황 · 9%↓ 빨강",
                "Updates every minute · 80%+ green · 30%+ blue · 10%+ orange · 9% or less red")));
        }
        else
        {
            // 모르면 숫자를 지어내지 않는다 — 이유도 추정하지 않는다(데스크톱이라 없는지, 읽지 못했는지 구분할 수 없다)
            body.Children.Add(new TextBlock { Text = S.T("모름", "Unknown"), FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Brush(Text2) });
            body.Children.Add(Caption(S.T("Windows 가 배터리 잔량을 알려 주지 않았습니다.", "Windows didn't report a battery level.")));
        }
    }

    private void RenderSpec()
    {
        if (scanning)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
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
            var value = Keep(new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap }, lastScan.Rows[i].Value);
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
        var go = new HyperlinkButton { Content = S.T("진단하러 가기 ›", "Diagnose ›"), NavigateUri = new Uri("https://pcupcheck.com/scanner"), FontSize = 12, Padding = new Thickness(4, 2, 4, 2) };
        ToolTipService.SetToolTip(go, "pcupcheck.com/scanner");
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
                SetTile(0, $"{p}%", Windows.UI.Color.FromArgb(0xFF, r, g, b));
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

    private void SetTile(int i, string text, Windows.UI.Color color)
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

    /// <summary>클립보드에 넣는다. 넣기 실패만 실패로 — Flush 는 잠겨 있으면 몇 번 다시 시도(Part 272)</summary>
    private static async Task<bool> CopyAsync(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        try
        {
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return false;
        }
        for (int i = 0; i < 4; i++)
        {
            try
            {
                Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
                break;
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
                await Task.Delay(150);
            }
        }
        return true;
    }

    // ── Win32 ───────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, IntPtr data);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
