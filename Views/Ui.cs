using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PCUpCheckTools.Views;

// [Part 277] 팝오버와 설정 창이 함께 쓰는 색·부품 — 시안(https://claude.ai/artifact/WFiDTpShbqxxpfMT9SqoAY)의 토큰.
// 종전엔 PopoverWindow 안에 있었다. 두 창이 같은 모양을 쓰도록 한곳에 둔다.
// [Part 291] WinUI → Avalonia 12. 색·크기·문구는 그대로이고 컨트롤만 바꿨다(AiMeter Part 287 과 같은 대응).
internal static class Ui
{
    public static readonly Color Navy = Hex("#0F172A");
    public static readonly Color Surface = Hex("#1E293B");
    public static readonly Color Raised = Hex("#334155");
    public static readonly Color Text1 = Hex("#F8FAFC");
    public static readonly Color Text2 = Hex("#94A3B8");
    public static readonly Color Line = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
    public static readonly Color Track = Color.FromArgb(0x29, 0x94, 0xA3, 0xB8);
    public static readonly Color Orange = Hex("#FB923C");
    public static readonly Color Emerald = Hex("#34D399");
    public static readonly Color Red = Hex("#F87171");

    /// <summary>Segoe Fluent Icons(Windows 11) · Segoe MDL2 Assets(Windows 10) — WinUI FontIcon 과 같은 글리프</summary>
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static readonly FontFamily MonoFont = new("Consolas"); // 「Mono」 는 참조 패키지의 네임스페이스와 겹친다

    public static SolidColorBrush Brush(Color c) => new(c);

    public static Color Hex(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromArgb(0xFF,
            byte.Parse(hex[..2], NumberStyles.HexNumber),
            byte.Parse(hex[2..4], NumberStyles.HexNumber),
            byte.Parse(hex[4..6], NumberStyles.HexNumber));
    }

    /// <summary>구역 이름 — 자간 80 은 영문 대문자 라벨용, 한글에 주면 어절이 벌어진다(AiMeter Part 253).
    /// [Part 291] WinUI CharacterSpacing 40(1/1000 em) → Avalonia LetterSpacing(픽셀) — 12px 기준 0.48</summary>
    public static TextBlock Label(string text) => new()
    {
        Text = text, FontSize = 12, LetterSpacing = S.IsKorean ? 0 : 12 * 0.040, Foreground = Brush(Text2),
    };

    public static TextBlock Caption(string text) => new KeepTextBlock(text)
    {
        FontSize = 12, Foreground = Brush(Text2), LineHeight = 17,
    };

    public static Control Notice(string text, Color tone) => new Border
    {
        Padding = new Thickness(12, 9, 12, 9),
        CornerRadius = new CornerRadius(8),
        Background = Brush(Color.FromArgb(0x14, tone.R, tone.G, tone.B)),
        BorderBrush = Brush(Color.FromArgb(0x59, tone.R, tone.G, tone.B)),
        BorderThickness = new Thickness(1),
        Child = new KeepTextBlock(text) { FontSize = 12.5, Foreground = Brush(Text1), LineHeight = 19 },
    };

    /// <summary>둥근 면 안에 항목을 얇은 줄로 나눈다</summary>
    public static Border Card(IReadOnlyList<Control> items, Thickness? padding = null)
    {
        var stack = new StackPanel();
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) stack.Children.Add(new Border { Height = 1, Background = Brush(Line), Margin = new Thickness(0, 10, 0, 10) });
            stack.Children.Add(items[i]);
        }
        return new Border
        {
            Child = stack,
            Padding = padding ?? new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(10),
            Background = Brush(Surface),
        };
    }

    /// <summary>「이름(+설명) ··· [스위치]」 한 줄. 처리기는 값이 같으면 무시해야 한다(AiMeter Part 247)</summary>
    public static Control SwitchRow(string text, string? description, bool isOn, Action<bool> changed)
    {
        var grid = new Grid { ColumnSpacing = 12, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        grid.Children.Add(Texts(text, description));
        // 글자 없는 스위치 — 켜짐/꺼짐 글자 자리를 비운다(AiMeter Part 287)
        var toggle = new ToggleSwitch { IsChecked = isOn, OnContent = null, OffContent = null, MinWidth = 0, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, text);
        toggle.IsCheckedChanged += (_, _) => changed(toggle.IsChecked == true);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        return grid;
    }

    /// <summary>「이름(+설명) ··· [콤보]」 한 줄. 로드 때 오는 선택 이벤트는 값이 같아 무시된다(Part 247)</summary>
    public static Control ComboRow(string text, string? description, string[] items, int selected, Action<int> changed, bool[]? enabled = null)
    {
        var grid = new Grid { ColumnSpacing = 12, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        grid.Children.Add(Texts(text, description));
        var combo = new ComboBox { FontSize = 13, MinWidth = 0, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < items.Length; i++)
            combo.Items.Add(new ComboBoxItem { Content = items[i], IsEnabled = enabled is null || enabled[i] });
        combo.SelectedIndex = Math.Max(0, selected);
        AutomationProperties.SetName(combo, text);
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) changed(combo.SelectedIndex); };
        Grid.SetColumn(combo, 1);
        grid.Children.Add(combo);
        return grid;
    }

    public static StackPanel Texts(string text, string? description)
    {
        var texts = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new KeepTextBlock(text) { FontSize = 14, Foreground = Brush(Text1) });
        if (description is not null) texts.Children.Add(Caption(description));
        return texts;
    }

    public static Button IconButton(string glyph, string name)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, FontSize = 16, FontFamily = Icons },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Width = 44,
            Height = 40,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(b, name);
        ToolTip.SetTip(b, name);
        return b;
    }

    /// <summary>[Part 291] 링크 글자 — Avalonia HyperlinkButton 은 밑줄을 긋는다(WinUI 판은 밑줄 없음, 원본 캡처 대조)</summary>
    public static TextBlock NoUnderline(string text) => new() { Text = text, TextDecorations = new TextDecorationCollection() };

    /// <summary>한글 음절 사이에 단어 결합 문자(U+2060) — WinUI 는 한글을 음절마다 끊었다(Part 271). Avalonia 도 같다(AiMeter Part 287)</summary>
    public static string KeepWords(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length * 2);
        for (int i = 0; i < text.Length; i++)
        {
            sb.Append(text[i]);
            if (i + 1 < text.Length && IsHangul(text[i]) && IsHangul(text[i + 1])) sb.Append('⁠');
        }
        return sb.ToString();

        static bool IsHangul(char c) => c is >= '가' and <= '힣';
    }

    /// <summary>System.Drawing 색(Thresholds) → 어두운 바탕용 단계 색. 팝오버·설정은 늘 어두운 바탕이라 「단계」 만 옮긴다(Part 273)</summary>
    public static Color Level(System.Drawing.Color c) =>
        c == Hardware.Thresholds.Warn ? Red
        : c == Hardware.Thresholds.Caution ? Orange
        : c == Hardware.Thresholds.Disabled ? Text2
        : Text1;
}

/// <summary>줄바꿈되는 글자 — 화면에는 어절 유지본, 화면 읽기 이름에는 원문(Part 271).
/// [Part 291] Avalonia TextBlock 의 화면 읽기 이름은 AutomationProperties.Name 이 아니라 Text 다(AiMeter Part 287 실측: 이름에 U+2060 이 섞임)
/// → 이름만 원문을 돌려주는 피어를 붙인다.</summary>
internal sealed class KeepTextBlock : TextBlock
{
    private readonly string original;

    public KeepTextBlock(string text)
    {
        original = text;
        Text = Ui.KeepWords(text);
        TextWrapping = TextWrapping.Wrap;
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this, original);

    private sealed class Peer(TextBlock owner, string name) : TextBlockAutomationPeer(owner)
    {
        protected override string GetNameCore() => name;
    }
}
