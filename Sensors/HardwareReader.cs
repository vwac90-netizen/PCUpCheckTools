// [Part 275] PC Up Check Tools — tools/HwMonitorMini/HardwareReader.cs 에서 그대로 옮김(네임스페이스만 바꿈). 센서 모드(관리자, SensorHost)에서만 쓴다. 바꿀 때는 HwMonitorMini 쪽과 함께 볼 것.
// HardwareReader — LibreHardwareMonitor 래퍼 (단일 책임: 온도 센서 수집).
// 핵심 원칙(기획서 §3): 앱 1회 Open() / 종료 시 Close(). 매 틱 재오픈 금지(핸들 누수·비용).
// CPU/Storage/GPU/Motherboard 만 활성화하여 불필요한 하드웨어 업데이트 비용 제거.
// 일부 센서(특히 SSD/메인보드)는 관리자 권한이 있어야 노출됨 → 권한 없으면 null 폴백(graceful degrade, 튕김 금지).
using LibreHardwareMonitor.Hardware; // NuGet LibreHardwareMonitorLib (MPL-2.0)

namespace PCUpCheckTools.Hardware
{
    public class HardwareReader : IDisposable
    {
        // private 필드: camelCase, 언더스코어 없음 (규칙 csharp-winforms.md §2)
        private readonly Computer computer;
        private readonly UpdateVisitor visitor;
        private bool isOpen;
        private bool disposed;

        public HardwareReader()
        {
            // 필요한 하드웨어만 활성 (저부하 — 기획서 §3 CPU 0.5% 미만 목표)
            computer = new Computer
            {
                IsCpuEnabled = true,
                IsStorageEnabled = true,   // SSD/HDD 온도
                IsGpuEnabled = true,       // VGA 온도
                IsMotherboardEnabled = true // MBD 온도
                // IsMemoryEnabled / IsNetworkEnabled 등은 비활성 → 불필요한 Update 비용 제거
            };
            visitor = new UpdateVisitor();
        }

        // 앱 시작 시 1회 호출. 권한/드라이버 문제로 실패해도 예외를 삼키고 isOpen=false 유지(폴백 동작)
        public void Open()
        {
            try
            {
                computer.Open();
                isOpen = true;
            }
            catch (Exception ex)
            {
                isOpen = false;
                // 관리자 권한 없음·커널 드라이버 로드 실패 등 → 온도는 전부 "--"로 graceful degrade
                Console.WriteLine($"[HardwareReader] Open 실패(권한/드라이버 확인 필요): {ex.Message}");
            }
        }

        // 현재 온도 스냅샷을 snapshot 에 채운다. 센서 부재/예외 시 해당 필드는 null 유지(=-- 표기).
        // HISTORY Part 93: ①SubHardware 재귀 수집(메인보드 온도는 SuperIO 하위 하드웨어에 있음 — 기존 미순회 버그)
        //   ②온도 유효성 게이트(IsValidTemp)로 0·비정상값을 무효 처리 → null→"--" 통일(사용자 요청)
        //   ③CPU 대표 센서는 명칭 우선순위(Package/Tctl/Tdie) → 없으면 유효 코어 최댓값으로 선택(첫 값 채택 폐기).
        public void Read(MetricsSnapshot snapshot)
        {
            if (!isOpen)
            {
                return; // Open 실패 시 온도 전부 null(=--) 폴백
            }

            try
            {
                // IVisitor 패턴으로 활성 하드웨어 + 하위 센서 갱신 (기획서 §3)
                computer.Accept(visitor);

                // CPU 온도는 대표 센서 선택 우선순위가 있어 후보를 모아 마지막에 결정한다.
                double? cpuPreferred = null; // Package/Tctl/Tdie 우선
                double? cpuFallback = null;  // 그 외 유효 코어 온도 중 최댓값

                foreach (IHardware hardware in computer.Hardware)
                {
                    CollectFrom(hardware, snapshot, ref cpuPreferred, ref cpuFallback);
                }

                // 대표 CPU 온도 확정(우선 센서 → 없으면 코어 최댓값). 둘 다 없으면 null 유지(--)
                snapshot.CpuTemperature ??= cpuPreferred ?? cpuFallback;
            }
            catch (Exception ex)
            {
                // 수집 중 예외(외장 GPU 절전 진입 등) → 이번 틱은 기존 null 유지하고 계속 진행(튕김 금지)
                Console.WriteLine($"[HardwareReader] Read 예외(이번 틱 온도 일부 누락): {ex.Message}");
            }
        }

        // 한 하드웨어(및 그 SubHardware 재귀)에서 온도 센서를 수집해 스냅샷에 매핑한다.
        // SubHardware 재귀가 핵심: 메인보드(Motherboard) 최상위엔 보통 직속 온도 센서가 없고,
        // 하위 SuperIO 칩(Nuvoton/ITE 등)에 시스템/메인보드 온도가 달려 있다(기존 버그: 최상위만 순회).
        private static void CollectFrom(IHardware hardware, MetricsSnapshot snapshot, ref double? cpuPreferred, ref double? cpuFallback)
        {
            foreach (ISensor sensor in hardware.Sensors)
            {
                // 온도 센서만 필터 (SensorType==Temperature)
                if (sensor.SensorType != SensorType.Temperature)
                {
                    continue;
                }
                if (sensor.Value is null)
                {
                    continue;
                }

                double value = sensor.Value.Value;
                // 0·비정상값(드라이버 미로드 시 0 등)은 무효 처리 → 해당 항목은 null 유지하여 "--"로 통일
                if (!IsValidTemp(value))
                {
                    continue;
                }

                switch (hardware.HardwareType)
                {
                    case HardwareType.Cpu:
                        // "CPU Package" / "Core (Tctl/Tdie)" 를 대표값으로 우선 채택, 그 외 코어는 최댓값 후보
                        string name = sensor.Name ?? string.Empty;
                        if (name.Contains("Package") || name.Contains("Tctl") || name.Contains("Tdie"))
                        {
                            cpuPreferred ??= value;
                        }
                        else if (cpuFallback is null || value > cpuFallback.Value)
                        {
                            cpuFallback = value;
                        }
                        break;

                    case HardwareType.Storage:
                        // SSD/NVMe 온도 (윈도우 기본 API 불가 → LHM 필수). 첫 유효값 채택.
                        snapshot.SsdTemperature ??= value;
                        break;

                    case HardwareType.GpuNvidia:
                    case HardwareType.GpuAmd:
                    case HardwareType.GpuIntel:
                        snapshot.GpuTemperature ??= value;
                        break;

                    case HardwareType.Motherboard:
                    case HardwareType.SuperIO:
                        // 메인보드 온도는 보통 SuperIO 칩 하위 센서로 노출됨(첫 유효값 채택)
                        snapshot.MotherboardTemperature ??= value;
                        break;
                }
            }

            // ★ SubHardware 재귀 — SuperIO(메인보드) 등 하위 하드웨어의 센서까지 수집(기존 미순회 버그 수정)
            foreach (IHardware subHardware in hardware.SubHardware)
            {
                CollectFrom(subHardware, snapshot, ref cpuPreferred, ref cpuFallback);
            }
        }

        // 온도 유효성 판정 — 구동 중 부품이 0°C 이하이거나 150°C 이상일 수 없으므로 무효(센서 미동작/드라이버 미로드)로 본다.
        // 무효값은 수집하지 않아 해당 항목이 null 로 남고 렌더러에서 "--" 로 통일 표기된다(사용자 요청).
        private static bool IsValidTemp(double value)
        {
            return value > 0.0 && value < 150.0;
        }

        public void Close()
        {
            if (isOpen)
            {
                try
                {
                    computer.Close();
                }
                catch
                {
                    // 종료 시 예외는 무시 (정리 단계)
                }
                isOpen = false;
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            Close();
            disposed = true;
            GC.SuppressFinalize(this);
        }

        // LibreHardwareMonitor 의 IVisitor 구현 — 하드웨어와 하위 하드웨어(SuperIO 등)를 순회하며 Update 호출.
        // 별도 파일이 아닌 nested 로 두어 HardwareReader 전용임을 명시(파일당 한 클래스 규칙은 공개 타입 기준).
        private sealed class UpdateVisitor : IVisitor
        {
            public void VisitComputer(IComputer computer)
            {
                computer.Traverse(this);
            }

            public void VisitHardware(IHardware hardware)
            {
                hardware.Update(); // 센서 값 실제 갱신
                foreach (IHardware subHardware in hardware.SubHardware)
                {
                    subHardware.Accept(this);
                }
            }

            public void VisitSensor(ISensor sensor) { }

            public void VisitParameter(IParameter parameter) { }
        }
    }
}
