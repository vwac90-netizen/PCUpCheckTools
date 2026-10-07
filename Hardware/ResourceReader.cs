// [Part 273] PC Up Check Tools — tools/HwMonitorMini/ResourceReader.cs 에서 그대로 옮김(네임스페이스만 바꿈). 바꿀 때는 HwMonitorMini 쪽과 함께 볼 것.
// ResourceReader — CPU/RAM 사용률 수집 (단일 책임).
// CPU%: PerformanceCounter("Processor", "% Processor Time", "_Total") — 첫 NextValue()는 0(워밍업) → 생성 시 프라임.
// RAM%: GlobalMemoryStatusEx P/Invoke의 dwMemoryLoad(0~100, 실제 물리 메모리 사용률) 사용.
// [2026-10-04 Part 233] 작업 관리자 「성능」 탭 항목 추가 — 메모리 사용/전체 GB(같은 GlobalMemoryStatusEx) ·
//   CPU 속도 = Processor Information(_Total) "Processor Frequency"(MHz, 기준) × "% Processor Performance"/100 ·
//   디스크 활성 = 100 − PhysicalDisk(_Total) "% Idle Time". 모두 관리자 권한 불필요. 카운터가 없으면 해당 값 null(= "--").
using System.Diagnostics; // PerformanceCounter
using System.Runtime.InteropServices; // GlobalMemoryStatusEx P/Invoke

namespace PCUpCheckTools.Hardware
{
    public class ResourceReader : IDisposable
    {
        // private 필드: camelCase, 언더스코어 없음 (규칙 csharp-winforms.md §2)
        private PerformanceCounter? cpuCounter;
        private PerformanceCounter? cpuPerfCounter;   // [Part 233] % Processor Performance(터보면 100 초과)
        private PerformanceCounter? cpuFreqCounter;   // [Part 233] Processor Frequency(MHz)
        private PerformanceCounter? diskIdleCounter;  // [Part 233] PhysicalDisk(_Total) % Idle Time
        private bool disposed;

        public ResourceReader()
        {
            try
            {
                cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
                // 워밍업: 첫 호출은 항상 0 → 생성 즉시 1회 프라임 (기획서 §3)
                cpuCounter.NextValue();
            }
            catch (Exception ex)
            {
                cpuCounter = null;
                Console.WriteLine($"[ResourceReader] CPU 카운터 초기화 실패: {ex.Message}");
            }
            cpuPerfCounter = TryCounter("Processor Information", "% Processor Performance", "_Total");
            cpuFreqCounter = TryCounter("Processor Information", "Processor Frequency", "_Total");
            diskIdleCounter = TryCounter("PhysicalDisk", "% Idle Time", "_Total");
        }

        // [Part 233] 카운터 생성 + 워밍업(첫 값 0). 없으면 null — 해당 항목만 "--"
        private static PerformanceCounter? TryCounter(string category, string counter, string instance)
        {
            try
            {
                var c = new PerformanceCounter(category, counter, instance, readOnly: true);
                c.NextValue();
                return c;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ResourceReader] {category}/{counter} 사용 불가: {ex.Message}");
                return null;
            }
        }

        public void Read(MetricsSnapshot snapshot)
        {
            // ── CPU 사용률 ──
            if (cpuCounter is not null)
            {
                try
                {
                    snapshot.CpuUsagePercent = cpuCounter.NextValue();
                }
                catch (Exception ex)
                {
                    // 이번 틱 실패 → null(=--) 폴백, 튕김 금지
                    Console.WriteLine($"[ResourceReader] CPU 측정 예외: {ex.Message}");
                }
            }

            // ── RAM 사용률 ──
            try
            {
                var memStatus = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(memStatus))
                {
                    // dwMemoryLoad: 사용 중인 물리 메모리의 대략적 백분율(0~100)
                    snapshot.RamUsagePercent = memStatus.dwMemoryLoad;
                    // [Part 233] 사용/전체 GB(1GB = 1024³ 바이트, 작업 관리자와 같은 단위)
                    const double gb = 1024.0 * 1024.0 * 1024.0;
                    snapshot.RamTotalGb = memStatus.ullTotalPhys / gb;
                    snapshot.RamUsedGb = (memStatus.ullTotalPhys - memStatus.ullAvailPhys) / gb;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ResourceReader] RAM 측정 예외: {ex.Message}");
            }

            // ── [Part 233] CPU 속도(GHz) ──
            try
            {
                if (cpuPerfCounter is not null && cpuFreqCounter is not null)
                {
                    double mhz = cpuFreqCounter.NextValue();
                    double perf = cpuPerfCounter.NextValue();
                    if (mhz > 0 && perf > 0)
                    {
                        snapshot.CpuClockGhz = mhz * perf / 100.0 / 1000.0;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ResourceReader] CPU 속도 측정 예외: {ex.Message}");
            }

            // ── [Part 233] 디스크 활성(%) ──
            try
            {
                if (diskIdleCounter is not null)
                {
                    double idle = diskIdleCounter.NextValue();
                    snapshot.DiskActivePercent = Math.Clamp(100.0 - idle, 0, 100);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ResourceReader] 디스크 측정 예외: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            cpuCounter?.Dispose();
            cpuPerfCounter?.Dispose();
            cpuFreqCounter?.Dispose();
            diskIdleCounter?.Dispose();
            cpuCounter = null;
            disposed = true;
            GC.SuppressFinalize(this);
        }

        // ── GlobalMemoryStatusEx P/Invoke (kernel32) ──
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        // 구조체는 클래스로 선언(레퍼런스 전달) — dwLength를 sizeof로 초기화
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private sealed class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }
    }
}
