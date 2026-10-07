using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PCUpCheckTools.Views;

// [Part 277] 팝오버와 설정 창이 함께 쓰는 색·부품 — 시안(https://claude.ai/artifact/WFiDTpShbqxxpfMT9SqoAY)의 토큰.
// 종전엔 PopoverWindow 안에 있었다. 두 창이 같은 모양을 쓰도록 한곳에 둔다.
internal static class Ui
{
    public static readonly Windows.UI.Color Navy = Hex("#0F172A");
    public static readonly Windows.UI.Color Surface = Hex("#1E293B");
    public static readonly Windows.UI.Color Raised = Hex("#334155");
    public static readonly Windows.UI.Color Text1 = Hex("#F8FAFC");
    public static readonly Windows.UI.Color Text2 = Hex("#94A3B8");
    public static readonly Windows.UI.Color Line = Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
    public static readonly Windows.UI.Color Track = Windows.UI.Color.FromArgb(0x29, 0x94, 0xA3, 0xB8);
    public static readonly Windows.UI.Color Orange = Hex("#FB923C");
    public static readonly Windows.UI.Color Emerald = Hex("#34D399");
    public static readonly Windows.UI.Color Red = Hex("#F87171");

    public static SolidColorBrush Brush(Windows.UI.Color c) => new(c);

    public static Windows.UI.Color Hex(string hex)
    {
        hex = hex.TrimStart('#');
        return Windows.UI.Color.FromArgb(0xFF,
            byte.Parse(hex[..2], NumberStyles.HexNumber),
            byte.Parse(hex[2..4], NumberStyles.HexNumber),
            byte.Parse(hex[4..6], NumberStyles.HexNumber));
    }

    /// <summary>구역 이름 — 자간 80 은 영문 대문자 라벨용, 한글에 주면 어절이 벌어진다(AiMeter Part 253)</summary>
    public static TextBlock Label(string text) => new()
    {
        Text = text, FontSize = 12, CharacterSpacing = S.IsKorean ? 0 : 40, Foreground = Brush(Text2),
    };

    public static TextBlock Caption(string text) => Keep(new TextBlock
    {
        FontSize = 12, Foreground = Brush(Text2), TextWrapping = TextWrapping.Wrap, LineHeight = 17,
    }, text);

    public static FrameworkElement Notice(string text, Windows.UI.Color tone) => new Border
    {
        Padding = new Thickness(12, 9, 12, 9),
        CornerRadius = new CornerRadius(8),
        Background = Brush(Windows.UI.Color.FromArgb(0x14, tone.R, tone.G, tone.B)),
        BorderBrush = Brush(Windows.UI.Color.FromArgb(0x59, tone.R, tone.G, tone.B)),
        BorderThickness = new Thickness(1),
        Child = Keep(new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = Brush(Text1), LineHeight = 19 }, text),
    };

    /// <summary>둥근 면 안에 항목을 얇은 줄로 나눈다</summary>
    public static Border Card(IReadOnlyList<FrameworkElement> items, Thickness? padding = null)
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
    public static FrameworkElement SwitchRow(string text, string? description, bool isOn, Action<bool> changed)
    {
        var grid = new Grid { ColumnSpacing = 12, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        grid.Children.Add(Texts(text, description));
        var toggle = new ToggleSwitch { IsOn = isOn, OnContent = "", OffContent = "", MinWidth = 0, Margin = new Thickness(8, 0, -12, 0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, text);
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        return grid;
    }

    /// <summary>「이름(+설명) ··· [콤보]」 한 줄. 로드 때 오는 선택 이벤트는 값이 같아 무시된다(Part 247)</summary>
    public static FrameworkElement ComboRow(string text, string? description, string[] items, int selected, Action<int> changed, bool[]? enabled = null)
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
        texts.Children.Add(Keep(new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Foreground = Brush(Text1) }, text));
        if (description is not null) texts.Children.Add(Caption(description));
        return texts;
    }

    public static Button IconButton(string glyph, string name)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 16 },
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Width = 44,
            Height = 40,
        };
        AutomationProperties.SetName(b, name);
        ToolTipService.SetToolTip(b, name);
        return b;
    }

    /// <summary>줄바꿈되는 글자 — 화면에는 어절 유지본, 화면 읽기 이름에는 원문(Part 271)</summary>
    public static TextBlock Keep(TextBlock block, string text)
    {
        block.Text = KeepWords(text);
        AutomationProperties.SetName(block, text);
        return block;
    }

    /// <summary>한글 음절 사이에 단어 결합 문자(U+2060) — WinUI 는 한글을 음절마다 끊었다(Part 271)</summary>
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
    public static Windows.UI.Color Level(System.Drawing.Color c) =>
        c == Hardware.Thresholds.Warn ? Red
        : c == Hardware.Thresholds.Caution ? Orange
        : c == Hardware.Thresholds.Disabled ? Text2
        : Text1;
}
