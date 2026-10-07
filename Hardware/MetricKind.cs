// [Part 273] PC Up Check Tools — tools/HwMonitorMini/MetricKind.cs 에서 그대로 옮김(네임스페이스만 바꿈). 바꿀 때는 HwMonitorMini 쪽과 함께 볼 것.
// MetricKind — 위젯 한 칸에 표시할 수 있는 항목 (단일 책임: 항목 식별자).
// [2026-10-04 Part 233] 8칸(2행×4열)을 사용자가 고른다. 설정 파일에는 이 이름(문자열)으로 저장된다 —
//   이름을 바꾸면 기존 설정이 기본값으로 돌아가므로 바꾸지 말고 추가만 한다.
namespace PCUpCheckTools.Hardware
{
    public enum MetricKind
    {
        Empty = 0,        // 비움(두 칸 모두 비면 그 열은 폭에서 빠진다)
        Upload,           // ↑ 업로드 속도
        Download,         // ↓ 다운로드 속도
        CpuUsage,         // CPU 사용률 %
        RamUsage,         // RAM 사용률 %
        CpuTemp,          // CPU 온도 (일반판만)
        GpuTemp,          // VGA 온도 (일반판만)
        SsdTemp,          // SSD 온도 (일반판만)
        MbdTemp,          // 메인보드 온도 (일반판만)
        GpuUsage,         // GPU 사용률 %
        NpuUsage,         // NPU 사용률 %
        CpuClock,         // CPU 속도 GHz
        RamGb,            // 메모리 사용/전체 GB
        DiskActive        // 디스크 활성 시간 %
    }
}
