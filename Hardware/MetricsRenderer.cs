// [Part 273] PC Up Check Tools — tools/HwMonitorMini/MetricsRenderer.cs 에서 옮김. 바꾼 것: 네임스페이스 · 글자 렌더링(ClearType → 회색조, 픽셀 알파 창) · 초기 배치 호출(MetricCatalog.Default 가 메서드). 바꿀 때는 HwMonitorMini 쪽과 함께 볼 것.
// MetricsRenderer — MetricsSnapshot 을 2행×최대 4열 작업표시줄 레이아웃으로 그린다 (단일 책임: 그리기).
// 기본 배치(SSOT .claude/agents/hwmonitor-tray-architect.md §5.0):
//   ↑ 8.07 KB/s   CPU: 49 %   CPU: 53 °C   SSD: 41 °C
//   ↓ 2.77 KB/s   RAM: 87 %   VGA: -- °C   MBD: -- °C
// [2026-10-04 Part 233] 8칸에 무엇을 둘지 사용자가 고른다(MetricKind · MetricCatalog). 종전엔 위 배치가 코드에 고정이었다.
//   열 폭 = 그 열 두 칸 항목의 최대 글자 수. 두 칸 모두 비면 그 열은 폭에서 빠진다(Lite 기본 2열이 이 경우).
//   항목별 텍스트·색은 MetricCatalog 한 곳에서 정한다. 라벨은 고정색, 값은 Thresholds 임계 컬러. 센서 부재는 회색 "--".
// 고정폭(Consolas) 폰트 + ClearTypeGridFit 로 숫자 변동 시 흔들림 최소화·선명도 확보.
// IDisposable: 내부 Font 를 보유하므로 앱 종료 시 Dispose (규칙 §3).
using System.Drawing.Text; // TextRenderingHint

using System.Drawing; // [Part 273] WinUI 프로젝트의 암시적 using 에는 System.Drawing 이 없다

namespace PCUpCheckTools.Hardware
{
    public class MetricsRenderer : IDisposable
    {
        // private 필드: camelCase, 언더스코어 없음 (규칙 §2)
        // [Part 218] DPI 가 바뀌면 폰트·폭을 다시 만든다 → readonly 해제
        private Font font;
        private readonly StringFormat stringFormat;
        private int columnGap;     // 열 간 간격(px)
        private int edgePad;       // 좌우 여백(px)
        private int desiredWidth;  // 콘텐츠가 요구하는 전체 폭(px)
        private int currentDpi;
        private float charWidth;   // 고정폭 한 글자 폭(px)
        private bool disposed;

        // [Part 233] 8칸 배치(열 우선: [0]=1열 위, [1]=1열 아래, …)
        private MetricKind[] slots = MetricCatalog.Default(false); // [Part 273] 기본값이 온도 사용 여부를 받는다 — 호출부가 곧 SetSlots 로 실제 배치를 넣는다

        // 기준 글자 크기(포인트). 실제 픽셀 크기는 DPI 로 환산한다.
        private const float BaseFontPoints = 8.0f;

        public int DesiredWidth => desiredWidth;
        public int CurrentDpi => currentDpi;

        public MetricsRenderer(int dpi = 96)
        {
            // 세그먼트를 빈틈없이 이어붙이기 위해 타이포그래픽 포맷 사용(여백 최소화)
            stringFormat = StringFormat.GenericTypographic;
            stringFormat.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;

            font = CreateFont(dpi);
            Measure(dpi);
        }

        // [Part 218] 고정폭 폰트(Consolas)를 **픽셀 단위**로 만든다(8pt × dpi/72).
        //   이전엔 포인트 단위라 그리는 창의 DPI 에 따라 커지는데, 폭은 96 DPI 1×1 비트맵에서 1회만 재서
        //   고배율(200%)에서 글자와 폭이 어긋났다. 픽셀 단위면 측정과 그리기가 같은 크기로 고정된다.
        private static Font CreateFont(int dpi)
        {
            float px = BaseFontPoints * Math.Max(96, dpi) / 72f;
            return new Font("Consolas", px, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        // 배율이 바뀌었을 때 호출 — 폰트·열 폭·전체 폭을 다시 계산한다. 같은 DPI 면 아무것도 안 한다.
        public bool SetDpi(int dpi)
        {
            if (dpi <= 0 || dpi == currentDpi)
            {
                return false;
            }
            Font old = font;
            font = CreateFont(dpi);
            old.Dispose();
            Measure(dpi);
            return true;
        }

        // [Part 233] 배치를 바꾼다 — 폭을 다시 계산(호출부가 창 폭·위치를 갱신)
        public void SetSlots(MetricKind[] newSlots)
        {
            slots = (MetricKind[])newSlots.Clone();
            Measure(currentDpi);
        }

        private void Measure(int dpi)
        {
            currentDpi = dpi;
            // 고정폭 한 글자 폭 측정 → 열 폭 산정(측정 비트맵도 같은 DPI 로 맞춘다)
            using (var tmp = new Bitmap(1, 1))
            {
                tmp.SetResolution(Math.Max(96, dpi), Math.Max(96, dpi));
                using var g = Graphics.FromImage(tmp);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; // [Part 273] 픽셀 알파 창이라 ClearType 을 쓸 수 없다 — 잴 때와 그릴 때 같은 방식(AiMeter Part 254)
                charWidth = g.MeasureString("0", font, PointF.Empty, stringFormat).Width;
            }

            columnGap = (int)Math.Ceiling(charWidth * 2);
            edgePad = (int)Math.Ceiling(charWidth);

            int width = edgePad;
            int shown = 0;
            foreach (int chars in ColumnChars())
            {
                if (chars == 0)
                {
                    continue; // 빈 열
                }
                width += (shown > 0 ? columnGap : 0) + (int)Math.Ceiling(charWidth * chars);
                shown++;
            }
            desiredWidth = width + edgePad;
        }

        // 열마다 필요한 최대 글자 수(두 칸 중 큰 쪽). 0 이면 빈 열.
        private IEnumerable<int> ColumnChars()
        {
            for (int c = 0; c < MetricCatalog.SlotCount / 2; c++)
            {
                yield return Math.Max(MetricCatalog.MaxChars(slots[c * 2]), MetricCatalog.MaxChars(slots[c * 2 + 1]));
            }
        }

        // bounds 영역(보통 윈도우 클라이언트 전체)에 스냅샷을 그린다.
        public void Draw(Graphics g, Rectangle bounds, MetricsSnapshot s)
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; // [Part 273] 픽셀 알파 창이라 ClearType 을 쓸 수 없다 — 잴 때와 그릴 때 같은 방식(AiMeter Part 254)

            int rowHeight = Math.Max(1, bounds.Height / 2);
            float fontHeight = font.GetHeight(g);
            float row0Y = bounds.Top + Math.Max(0, (rowHeight - fontHeight) / 2f);
            float row1Y = bounds.Top + rowHeight + Math.Max(0, (rowHeight - fontHeight) / 2f);

            float x = bounds.Left + edgePad;
            int c = 0;
            bool first = true;
            foreach (int chars in ColumnChars())
            {
                if (chars > 0)
                {
                    if (!first)
                    {
                        x += columnGap;
                    }
                    DrawSegments(g, x, row0Y, MetricCatalog.Segments(slots[c * 2], s));
                    DrawSegments(g, x, row1Y, MetricCatalog.Segments(slots[c * 2 + 1], s));
                    x += (float)Math.Ceiling(charWidth * chars);
                    first = false;
                }
                c++;
            }
        }

        // [Part 233] 값 변동 감지용 시그니처 — 실제로 그릴 텍스트와 색(임계 경계 통과도 변동으로 감지)
        public string Signature(MetricsSnapshot s)
        {
            var parts = new List<string>(MetricCatalog.SlotCount * 2);
            foreach (MetricKind kind in slots)
            {
                foreach ((string text, Color color) in MetricCatalog.Segments(kind, s))
                {
                    parts.Add(text + "/" + color.ToArgb());
                }
                parts.Add("|");
            }
            return string.Join(";", parts);
        }

        // 세그먼트들을 startX 부터 좌→우로 이어서 그린다(고정폭이라 정렬 안정적). Brush 는 using 으로 즉시 해제(규칙 §3).
        private void DrawSegments(Graphics g, float startX, float y, (string Text, Color Color)[] segments)
        {
            float x = startX;
            foreach ((string text, Color color) in segments)
            {
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }
                using (var brush = new SolidBrush(color))
                {
                    g.DrawString(text, font, brush, x, y, stringFormat);
                }
                x += g.MeasureString(text, font, PointF.Empty, stringFormat).Width;
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            font.Dispose();
            stringFormat.Dispose();
            disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
