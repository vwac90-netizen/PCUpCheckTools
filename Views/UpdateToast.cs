using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using static PCUpCheckTools.Views.Ui;

namespace PCUpCheckTools.Views;

// [Part 315] 업데이트 알림 — 화면 오른쪽 아래(팝오버 자리)에 뜨는 작은 창. 포커스를 빼앗지 않는다(ShowActivated=false).
// Avalonia 트레이 아이콘에는 풍선 알림이 없고, Windows 토스트는 앱 등록(AUMID)이 필요해 무설치 zip 앱과 맞지 않는다 → 직접 그린다.
// 상태 4가지: 새 버전 있음 · 설치 중(진행률) · 실패(이유 + 다운로드 페이지) · 업데이트 완료(8초 뒤 닫힘).
public sealed class UpdateToast : Window
{
    private const double ToastWidth = 340;

    private readonly StackPanel stack = new() { Spacing = 10 };
    private DispatcherTimer? autoClose;

    public event Action? InstallRequested;
    public event Action? LaterRequested;
    public event Action? PageRequested;

    public UpdateToast()
    {
        Content = new Border
        {
            Width = ToastWidth,
            Padding = new Thickness(16, 14, 16, 14),
            Background = Brush(Color.FromArgb(0xF2, 0x0F, 0x17, 0x2A)),
            BorderBrush = Brush(Color.FromArgb(0x59, 0x34, 0xD3, 0x99)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = stack,
        };
        Title = "PC Up Check Tools";
        Width = ToastWidth;
        SizeToContent = SizeToContent.Height;
        RequestedThemeVariant = ThemeVariant.Dark;
        Background = Brushes.Transparent;
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeChanged += (_, _) => Place();
        Opened += (_, _) => { RoundCorners(); Place(); };
        Closing += (_, e) => { if (!forceClose) { e.Cancel = true; Hide(); } };
    }

    private bool forceClose;

    public void CloseForExit()
    {
        forceClose = true;
        Close();
    }

    public void ShowAvailable(string version, bool canInstall)
    {
        Fill(S.T($"새 버전 {version}", $"Version {version} is available"),
            canInstall
                ? S.T("PC Up Check Tools 를 업데이트할 수 있습니다. 「지금 업데이트」 를 누르면 받아서 바꾸고 다시 켭니다(설정 · 자동 실행 · 온도는 그대로).",
                      "PC Up Check Tools can be updated. \"Update now\" downloads it, replaces the app and restarts it (settings, startup and temperatures stay as they are).")
                : S.T("PC Up Check Tools 새 버전이 나왔습니다. 다운로드 페이지에서 받아 주세요.", "A new version of PC Up Check Tools is out. Please get it from the release page."),
            Emerald);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (canInstall) row.Children.Add(Action(S.T("지금 업데이트", "Update now"), primary: true, () => InstallRequested?.Invoke()));
        else row.Children.Add(Action(S.T("다운로드 페이지", "Release page"), primary: true, () => PageRequested?.Invoke()));
        row.Children.Add(Action(S.T("나중에", "Later"), primary: false, () => LaterRequested?.Invoke()));
        if (canInstall) row.Children.Add(Action(S.T("바뀐 점", "What's new"), primary: false, () => PageRequested?.Invoke()));
        stack.Children.Add(row);
        Present(autoCloseSeconds: null);
    }

    public void ShowProgress(double? fraction)
    {
        Fill(S.T("업데이트를 받는 중…", "Downloading the update…"),
            fraction is double f ? S.T($"{f:P0} · 끝나면 앱이 잠깐 꺼졌다 다시 켜집니다.", $"{f:P0} · The app will close and reopen when it's done.")
                                 : S.T("끝나면 앱이 잠깐 꺼졌다 다시 켜집니다.", "The app will close and reopen when it's done."),
            Emerald);
        stack.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = fraction ?? 0, IsIndeterminate = fraction is null, Height = 6, MinWidth = 0 });
        Present(autoCloseSeconds: null);
    }

    public void ShowError(string message)
    {
        Fill(S.T("업데이트하지 못했습니다", "Update didn't complete"), message, Orange);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(Action(S.T("다운로드 페이지", "Release page"), primary: true, () => PageRequested?.Invoke()));
        row.Children.Add(Action(S.T("닫기", "Close"), primary: false, () => LaterRequested?.Invoke()));
        stack.Children.Add(row);
        Present(autoCloseSeconds: null);
    }

    public void ShowUpdated(string version)
    {
        Fill(S.T($"{version} 로 업데이트했습니다", $"Updated to {version}"), S.T("설정 · 자동 실행 · 온도는 그대로입니다.", "Settings, startup and temperatures are unchanged."), Emerald);
        Present(autoCloseSeconds: 8);
    }

    private void Fill(string title, string text, Color tone)
    {
        stack.Children.Clear();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock { Text = "\uE896", FontFamily = Icons, FontSize = 16, Foreground = Brush(tone), VerticalAlignment = VerticalAlignment.Center }); // U+E896 다운로드
        head.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Brush(Text1), VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(head);
        stack.Children.Add(Caption(text));
        AutomationProperties.SetName(this, title);
    }

    private static Button Action(string text, bool primary, Action click)
    {
        var b = new Button
        {
            Content = text,
            FontSize = 13,
            FontWeight = primary ? FontWeight.SemiBold : FontWeight.Normal,
            Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(primary ? 1 : 0),
            Background = primary ? Brush(Color.FromArgb(0x1F, 0x34, 0xD3, 0x99)) : Brushes.Transparent,
            BorderBrush = Brush(Color.FromArgb(0x80, 0x34, 0xD3, 0x99)),
            Foreground = Brush(primary ? Hex("#A7F3D0") : Text2),
        };
        b.Click += (_, _) => click();
        return b;
    }

    private void Present(int? autoCloseSeconds)
    {
        autoClose?.Stop();
        autoClose = null;
        if (!IsVisible) Show();
        Place();
        if (autoCloseSeconds is int sec)
        {
            autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(sec) };
            autoClose.Tick += (_, _) => { autoClose?.Stop(); Hide(); };
            autoClose.Start();
        }
    }

    /// <summary>작업 영역(작업 표시줄 제외) 오른쪽 아래 — 팝오버와 같은 여백</summary>
    private void Place()
    {
        var screen = Screens.Primary;
        if (screen is null) return;
        double scale = screen.Scaling;
        var area = screen.WorkingArea;
        int m = (int)Math.Round(12 * scale);
        int w = (int)Math.Round(ToastWidth * scale);
        int h = (int)Math.Round((Bounds.Height > 0 ? Bounds.Height : 140) * scale);
        Position = new PixelPoint(area.X + area.Width - w - m, area.Y + area.Height - h - m);
    }

    private void RoundCorners()
    {
        if (TryGetPlatformHandle()?.Handle is not IntPtr hwnd) return;
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        int noBorder = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(hwnd, 34, ref noBorder, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
