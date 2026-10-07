// [Part 273] PC Up Check Tools — tools/HwMonitorMini/Thresholds.cs 에서 그대로 옮김(네임스페이스만 바꿈). 바꿀 때는 HwMonitorMini 쪽과 함께 볼 것.
// Thresholds — 임계치/색상 설정 상수 (단일 책임: UI 컬러 코딩 정책 분리).
// 기획서 §5: 정상=연한 하늘색/화이트, 경고(CPU≥85% · RAM≥90% · 온도≥80°C)=네온 레드/오렌지.
// 값/임계치를 렌더러에서 분리해 한 곳에서만 조정하도록 한다(웹 디자인 토큰과 톤 일치).
using System.Drawing; // [Part 273] WinUI 프로젝트의 암시적 using 에는 System.Drawing 이 없다

namespace PCUpCheckTools.Hardware
{
    public static class Thresholds
    {
        // ── 경고 임계값 (설정 상수) ──
        public const double CpuWarnPercent = 85.0;   // CPU 사용률 경고
        public const double RamWarnPercent = 90.0;   // RAM 사용률 경고
        public const double TempWarnCelsius = 80.0;  // 온도 경고(CPU/SSD/VGA/MBD 공통)
        public const double TempCautionCelsius = 70.0; // 온도 주의(경고 직전 오렌지)
        public const double LoadWarnPercent = 90.0;  // [Part 233] GPU·NPU·디스크 활성 경고

        // ── 색상 토큰 (pcupcheck 다크 아이덴티티 계승) ──
        // [2026-10-04 Part 228] 다크/라이트 2벌 팔레트. 종전엔 다크 작업표시줄 전용 한 벌뿐이라
        //   라이트 작업표시줄에서 흰 글자가 밝은 바탕에 묻혔다. TrayContext 가 작업표시줄 테마(TaskbarTheme)를 읽어
        //   UseLightPalette 를 바꾸고, 아래 이름들은 현재 팔레트 색을 돌려준다(호출부 무변경).
        public static bool UseLightPalette { get; set; }

        // 정상 값: 다크 = 밝은 화이트(#f8fafc) / 라이트 = 짙은 남색(#0f172a)
        public static Color ValueNormal => UseLightPalette ? Color.FromArgb(0x0F, 0x17, 0x2A) : Color.FromArgb(0xF8, 0xFA, 0xFC);
        // 강조/엑센트(트레이 아이콘): 스카이블루(#38bdf8) — 트레이 아이콘은 테마와 무관하게 유지
        public static readonly Color Accent = Color.FromArgb(0x38, 0xBD, 0xF8);
        // 라벨 접두사(CPU:/RAM:/SSD:/VGA:/MBD:): 다크 #94a3b8 / 라이트 #475569
        public static Color Label => UseLightPalette ? Color.FromArgb(0x47, 0x55, 0x69) : Color.FromArgb(0x94, 0xA3, 0xB8);
        // 비활성/센서 부재("--"): 흐린 그레이 — 다크 #6b7280 / 라이트 #64748b
        public static Color Disabled => UseLightPalette ? Color.FromArgb(0x64, 0x74, 0x8B) : Color.FromArgb(0x6B, 0x72, 0x80);
        // 주의(임계 직전): 오렌지 — 다크 #fb923c / 라이트 #c2410c
        public static Color Caution => UseLightPalette ? Color.FromArgb(0xC2, 0x41, 0x0C) : Color.FromArgb(0xFB, 0x92, 0x3C);
        // 경고(임계 초과): 레드 — 다크 #f85555 / 라이트 #b91c1c (라이트 바탕 #F3F3F3 대비 4.5 이상)
        public static Color Warn => UseLightPalette ? Color.FromArgb(0xB9, 0x1C, 0x1C) : Color.FromArgb(0xF8, 0x55, 0x55);
        // 네트워크 화살표 고정색 — 업로드(오렌지)/다운로드(그린)
        public static Color UploadArrow => UseLightPalette ? Color.FromArgb(0xC2, 0x41, 0x0C) : Color.FromArgb(0xFB, 0x92, 0x3C);
        public static Color DownloadArrow => UseLightPalette ? Color.FromArgb(0x15, 0x80, 0x3D) : Color.FromArgb(0x4A, 0xDE, 0x80);

        // 사용률(%) 값의 임계 색상 — null(부재)은 Disabled
        public static Color UsageColor(double? percent, double warnPercent)
        {
            if (percent is null)
            {
                return Disabled;
            }
            return percent.Value >= warnPercent ? Warn : ValueNormal;
        }

        // 온도(°C) 값의 임계 색상 — 경고/주의/정상/부재 4단계
        public static Color TempColor(double? celsius)
        {
            if (celsius is null)
            {
                return Disabled;
            }
            if (celsius.Value >= TempWarnCelsius)
            {
                return Warn;
            }
            if (celsius.Value >= TempCautionCelsius)
            {
                return Caution;
            }
            return ValueNormal;
        }
    }
}
