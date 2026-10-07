// [Part 273] PC Up Check Tools — tools/HwMonitorMini/MetricCatalog.cs 에서 옮김.
// 바꾼 것: ①메뉴 이름을 { ko, en } 쌍으로(S.T) ②LITE 컴파일 상수 대신 「온도를 쓸 수 있는가」 를 인자로 받는다 — 온도는 4단계(관리자 보조 프로세스)에서 켠다.
// 표시 문자열(「CPU: 49 %」 등)·글자 수·색은 원본과 같다 — 라벨이 영문 약어라 언어와 무관하다.
//   칸 순서는 열 우선: [0]=1열 위, [1]=1열 아래, [2]=2열 위, … [7]=4열 아래.
using System.Drawing;

namespace PCUpCheckTools.Hardware
{
    public static class MetricCatalog
    {
        public const int SlotCount = 8;

        // 온도를 쓸 때 기본 배치 = HwMonitorMini 일반판 기본
        public static readonly MetricKind[] DefaultFull =
        {
            MetricKind.Upload, MetricKind.Download,
            MetricKind.CpuUsage, MetricKind.RamUsage,
            MetricKind.CpuTemp, MetricKind.GpuTemp,
            MetricKind.SsdTemp, MetricKind.MbdTemp
        };

        // 온도 없이 기본 배치 = HwMonitorMini Lite 기본
        public static readonly MetricKind[] DefaultLite =
        {
            MetricKind.Upload, MetricKind.Download,
            MetricKind.CpuUsage, MetricKind.RamUsage,
            MetricKind.Empty, MetricKind.Empty,
            MetricKind.Empty, MetricKind.Empty
        };

        public static MetricKind[] Default(bool temperatures) =>
            (MetricKind[])(temperatures ? DefaultFull : DefaultLite).Clone();

        public static bool IsTemperature(MetricKind kind) =>
            kind is MetricKind.CpuTemp or MetricKind.GpuTemp or MetricKind.SsdTemp or MetricKind.MbdTemp;

        // 고를 수 있는 항목 순서 — 온도를 못 쓰면 온도 항목을 뺀다
        public static IEnumerable<MetricKind> Selectable(bool temperatures)
        {
            yield return MetricKind.Upload;
            yield return MetricKind.Download;
            yield return MetricKind.CpuUsage;
            yield return MetricKind.CpuClock;
            yield return MetricKind.RamUsage;
            yield return MetricKind.RamGb;
            yield return MetricKind.GpuUsage;
            yield return MetricKind.NpuUsage;
            yield return MetricKind.DiskActive;
            if (temperatures)
            {
                yield return MetricKind.CpuTemp;
                yield return MetricKind.GpuTemp;
                yield return MetricKind.SsdTemp;
                yield return MetricKind.MbdTemp;
            }
            yield return MetricKind.Empty;
        }

        // 설정 파일 값(문자열 배열) → 8칸. 길이가 다르거나 모르는 이름·지금 못 쓰는 항목이면 기본값으로(튕김 금지)
        public static MetricKind[] Parse(string[]? names, bool temperatures)
        {
            if (names is null || names.Length != SlotCount)
            {
                return Default(temperatures);
            }
            var result = new MetricKind[SlotCount];
            var allowed = new HashSet<MetricKind>(Selectable(temperatures));
            for (int i = 0; i < SlotCount; i++)
            {
                if (!Enum.TryParse(names[i], out MetricKind kind) || !allowed.Contains(kind))
                {
                    return Default(temperatures);
                }
                result[i] = kind;
            }
            return result;
        }

        public static string[] ToNames(MetricKind[] slots)
        {
            return slots.Select(k => k.ToString()).ToArray();
        }

        // 설정 화면의 항목 이름(원본은 트레이 메뉴 한국어 인라인)
        public static string MenuLabel(MetricKind kind)
        {
            return kind switch
            {
                MetricKind.Upload => S.T("↑ 업로드 속도", "↑ Upload speed"),
                MetricKind.Download => S.T("↓ 다운로드 속도", "↓ Download speed"),
                MetricKind.CpuUsage => S.T("CPU 사용률", "CPU usage"),
                MetricKind.CpuClock => S.T("CPU 속도 (GHz)", "CPU speed (GHz)"),
                MetricKind.RamUsage => S.T("RAM 사용률", "RAM usage"),
                MetricKind.RamGb => S.T("메모리 사용량 (GB)", "Memory in use (GB)"),
                MetricKind.GpuUsage => S.T("GPU 사용률", "GPU usage"),
                MetricKind.NpuUsage => S.T("NPU 사용률", "NPU usage"),
                MetricKind.DiskActive => S.T("디스크 활성 시간", "Disk active time"),
                MetricKind.CpuTemp => S.T("CPU 온도", "CPU temperature"),
                MetricKind.GpuTemp => S.T("VGA 온도", "GPU temperature"),
                MetricKind.SsdTemp => S.T("SSD 온도", "SSD temperature"),
                MetricKind.MbdTemp => S.T("메인보드 온도", "Motherboard temperature"),
                _ => S.T("(비움)", "(empty)")
            };
        }

        // 열 폭 산정용 최대 글자 수(고정폭 글꼴) — 원본 그대로
        public static int MaxChars(MetricKind kind)
        {
            return kind switch
            {
                MetricKind.Upload or MetricKind.Download => 13,     // "↓ 999.9 MB/s"
                MetricKind.CpuTemp or MetricKind.GpuTemp or MetricKind.SsdTemp or MetricKind.MbdTemp => 11, // "CPU: 100 °C"
                MetricKind.CpuClock => 13,                           // "CLK: 9.99 GHz"
                MetricKind.RamGb => 16,                              // "MEM: 99.9/99.9G"
                MetricKind.Empty => 0,
                _ => 10                                              // "CPU: 100 %"
            };
        }

        // 한 칸의 표시 조각(텍스트 + 색). 비움이면 빈 배열. — 원본 그대로
        public static (string Text, Color Color)[] Segments(MetricKind kind, MetricsSnapshot s)
        {
            return kind switch
            {
                MetricKind.Upload => new[] { ("↑ ", Thresholds.UploadArrow), (s.UploadText, NetColor(s.UploadText)) },
                MetricKind.Download => new[] { ("↓ ", Thresholds.DownloadArrow), (s.DownloadText, NetColor(s.DownloadText)) },
                MetricKind.CpuUsage => Usage("CPU: ", s.CpuUsagePercent, Thresholds.CpuWarnPercent),
                MetricKind.RamUsage => Usage("RAM: ", s.RamUsagePercent, Thresholds.RamWarnPercent),
                MetricKind.GpuUsage => Usage("GPU: ", s.GpuUsagePercent, Thresholds.LoadWarnPercent),
                MetricKind.NpuUsage => Usage("NPU: ", s.NpuUsagePercent, Thresholds.LoadWarnPercent),
                MetricKind.DiskActive => Usage("DSK: ", s.DiskActivePercent, Thresholds.LoadWarnPercent),
                MetricKind.CpuClock => new[] { ("CLK: ", Thresholds.Label),
                    (s.CpuClockGhz is null ? "-- GHz" : $"{s.CpuClockGhz.Value:0.00} GHz", s.CpuClockGhz is null ? Thresholds.Disabled : Thresholds.ValueNormal) },
                MetricKind.RamGb => new[] { ("MEM: ", Thresholds.Label),
                    (s.RamUsedGb is null || s.RamTotalGb is null ? "--/--G" : $"{s.RamUsedGb.Value:0.0}/{s.RamTotalGb.Value:0.0}G",
                     Thresholds.UsageColor(s.RamUsagePercent, Thresholds.RamWarnPercent)) },
                MetricKind.CpuTemp => Temp("CPU: ", s.CpuTemperature),
                MetricKind.GpuTemp => Temp("VGA: ", s.GpuTemperature),
                MetricKind.SsdTemp => Temp("SSD: ", s.SsdTemperature),
                MetricKind.MbdTemp => Temp("MBD: ", s.MotherboardTemperature),
                _ => Array.Empty<(string, Color)>()
            };
        }

        private static (string, Color)[] Usage(string label, double? percent, double warn)
        {
            string number = percent is null ? "--" : $"{percent.Value:0}";
            return new[] { (label, Thresholds.Label), ($"{number} %", Thresholds.UsageColor(percent, warn)) };
        }

        private static (string, Color)[] Temp(string label, double? celsius)
        {
            string number = celsius is null ? "--" : $"{celsius.Value:0}";
            return new[] { (label, Thresholds.Label), ($"{number} °C", Thresholds.TempColor(celsius)) };
        }

        private static Color NetColor(string text)
        {
            return text == "--" ? Thresholds.Disabled : Thresholds.ValueNormal;
        }
    }
}
