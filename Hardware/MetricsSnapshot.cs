// [Part 273] PC Up Check Tools — tools/HwMonitorMini/MetricsSnapshot.cs 에서 그대로 옮김(네임스페이스만 바꿈). 바꿀 때는 HwMonitorMini 쪽과 함께 볼 것.
// MetricsSnapshot — 1회 갱신(1틱) 결과를 담는 불변 성격의 DTO.
// 수집·렌더링과 분리(단일 책임): 리더들이 값을 채워 넣고, 출력부(Program)는 이 객체만 읽는다.
// 수집 불가/센서 부재 항목은 nullable(double?)로 두고, 표기 시 "--" 로 폴백한다.
namespace PCUpCheckTools.Hardware
{
    public class MetricsSnapshot
    {
        // ── A) 네트워크 (초당 바이트를 사람이 읽기 좋은 단위로 변환한 문자열) ──
        // 업로드/다운로드 속도(KB/s·MB/s 자동 변환된 표시 문자열). 수집 불가 시 "--".
        public string UploadText { get; set; } = "--";
        public string DownloadText { get; set; } = "--";

        // 원본 수치(바이트/초) — 추후 임계치 판정·정렬 등에 쓰기 위해 보존
        public double UploadBytesPerSec { get; set; }
        public double DownloadBytesPerSec { get; set; }

        // ── B) 리소스 ──
        // CPU 사용률(%) / RAM 사용률(%). 수집 불가 시 null → 표기 "--".
        public double? CpuUsagePercent { get; set; }
        public double? RamUsagePercent { get; set; }

        // ── C) 온도 (°C) ──
        public double? CpuTemperature { get; set; }
        public double? SsdTemperature { get; set; }

        // ── D) 확장 온도 (°C) — 센서 부재 시 null(숨김/-- 폴백) ──
        public double? GpuTemperature { get; set; }   // VGA
        public double? MotherboardTemperature { get; set; } // MBD

        // ── E) [2026-10-04 Part 233] 작업 관리자 「성능」 탭 항목 — 수집 불가 시 null(= "--") ──
        public double? GpuUsagePercent { get; set; }   // GPU 사용률(여럿이면 가장 바쁜 GPU)
        public double? NpuUsagePercent { get; set; }   // NPU 사용률(ComputeOnly 장치)
        public double? CpuClockGhz { get; set; }       // 현재 CPU 속도(GHz) = 기준 주파수 × % Processor Performance
        public double? RamUsedGb { get; set; }         // 사용 중 물리 메모리(GB)
        public double? RamTotalGb { get; set; }        // 전체 물리 메모리(GB)
        public double? DiskActivePercent { get; set; } // 디스크 활성 시간(%) = 100 − PhysicalDisk(_Total) % Idle Time

        // 갱신 시각(검증 로그용)
        public DateTime Timestamp { get; set; } = DateTime.Now;

        // nullable 온도/사용률을 표기용 문자열로 변환 (값 없으면 "--", 있으면 소수 1자리)
        // 사용자 노출 문자열은 이 앱 한정 한국어/인라인 허용 (규칙 csharp-winforms.md §5)
        public static string FormatValue(double? value, string suffix)
        {
            if (value is null)
            {
                return "--";
            }
            return $"{value.Value:0.0}{suffix}";
        }
    }
}
