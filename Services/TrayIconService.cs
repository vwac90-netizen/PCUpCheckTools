using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Avalonia.Controls;

namespace PCUpCheckTools.Services;

// [Part 270 → 277] 트레이 아이콘 하나. 왼쪽 클릭 = 팝오버, 우클릭 = 끝내기.
// - [Part 277] 무엇을 보일지 고른다(시안 ④): 숫자(배터리 · CPU 사용률 · CPU 온도) 또는 앱 아이콘. 고르는 규칙은 App 이 정하고 여기는 그리기만.
// - 숫자 색: 호출부가 넘긴다(배터리 80/30/10 4색 · CPU·온도는 Thresholds 단계). 모르면 회색 「–」 — 숫자를 지어내지 않는다.
// - 시스템 작은 아이콘 크기(배율 반영)로 그린다 — 고배율에서 흐려지지 않게(Part 270).
// [Part 291] H.NotifyIcon(WinUI) → Avalonia TrayIcon. 그리기(GDI+)는 그대로 두고 PNG 로 넘긴다 — 숫자 모양을 바꾸지 않으려고.
public sealed class TrayIconService : IDisposable
{
    private readonly TrayIcon icon;
    private readonly TrayIcons icons;
    private readonly NativeMenuItem exitItem;
    private string lastKey = "";

    public event Action? LeftClick;
    public event Action? ExitRequested;

    public TrayIconService(Avalonia.Application app)
    {
        exitItem = new NativeMenuItem(S.T("끝내기", "Exit")) { Command = new Command(() => ExitRequested?.Invoke()) };
        icon = new TrayIcon
        {
            ToolTipText = "PC Up Check Tools",
            Menu = new NativeMenu { exitItem },
        };
        icon.Clicked += (_, _) => LeftClick?.Invoke();
        Show(null, Color.Gray, appIcon: true, "PC Up Check Tools");
        icons = new TrayIcons { icon };
        TrayIcon.SetIcons(app, icons);
        icon.IsVisible = true;
    }

    /// <summary>그린다. 글자·색·종류가 같으면 그림은 그대로 두고 툴팁만 바꾼다.</summary>
    /// <param name="text">숫자(예: "89"). null 이면 「–」(모름)</param>
    public void Show(string? text, Color color, bool appIcon, string tooltip)
    {
        exitItem.Header = S.T("끝내기", "Exit");
        icon.ToolTipText = tooltip.Length > 127 ? tooltip[..126] + "…" : tooltip; // 트레이 툴팁은 127자 제한
        string key = appIcon ? "app" : $"{text}|{color.ToArgb()}";
        if (key == lastKey) return;
        lastKey = key;
        Draw(appIcon ? null : text ?? "–", appIcon ? Color.Empty : text is null ? Color.FromArgb(0x94, 0xA3, 0xB8) : color);
    }

    private void Draw(string? text, Color color)
    {
        int size = Math.Max(16, GetSystemMetricsForDpi(49 /* SM_CXSMICON */, GetDpiForSystem()));
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (text is null)
            {
                DrawChip(g, size);
            }
            else
            {
                // 투명 바탕 위 ClearType 은 가장자리에 색 번짐 → 회색조 안티앨리어싱
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                float fontPx = size * (text.Length >= 3 ? 0.58f : 0.78f); // PureBattery: 2자리 9pt · 3자리 7pt @16px
                using var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(color);
                using var format = new StringFormat(StringFormat.GenericTypographic)
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
                };
                g.DrawString(text, font, brush, new RectangleF(0, 0, size, size), format);
            }
        }

        // [Part 291] 종전엔 GetHicon 핸들을 직접 만들고 해제했다 — 이제는 PNG 로 넘기고 아이콘 핸들은 Avalonia 가 관리한다
        using var png = new MemoryStream();
        bmp.Save(png, ImageFormat.Png);
        png.Position = 0;
        icon.Icon = new WindowIcon(png);
    }

    /// <summary>앱 아이콘 — 칩 모양(웹 파비콘·HwMonitorMini app.ico 와 같은 memory 칩 모티프), 하늘색</summary>
    private static void DrawChip(Graphics g, int size)
    {
        float s = size / 24f;
        using var pen = new Pen(Color.FromArgb(0x38, 0xBD, 0xF8), Math.Max(1.4f, 1.8f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawRectangle(pen, 6 * s, 6 * s, 12 * s, 12 * s);
        g.DrawRectangle(pen, 9.5f * s, 9.5f * s, 5 * s, 5 * s);
        foreach (float p in new[] { 9f, 15f })
        {
            g.DrawLine(pen, p * s, 2 * s, p * s, 5 * s);
            g.DrawLine(pen, p * s, 19 * s, p * s, 22 * s);
            g.DrawLine(pen, 2 * s, p * s, 5 * s, p * s);
            g.DrawLine(pen, 19 * s, p * s, 22 * s, p * s);
        }
    }

    public void Dispose()
    {
        icon.IsVisible = false;
        icon.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    private sealed class Command(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
