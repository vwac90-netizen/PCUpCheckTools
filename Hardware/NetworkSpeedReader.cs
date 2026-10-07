// [Part 273] PC Up Check Tools — tools/HwMonitorMini/NetworkSpeedReader.cs 에서 그대로 옮김(네임스페이스만 바꿈). 바꿀 때는 HwMonitorMini 쪽과 함께 볼 것.
// NetworkSpeedReader — 네트워크 업/다운로드 속도 수집 (단일 책임).
// PerformanceCounter("Network Interface", "Bytes Sent/sec" | "Bytes Received/sec", 인터페이스)로 측정.
// 활성(루프백/터널 제외) 인터페이스를 모두 합산하고, KB/s ↔ MB/s 단위를 자동 변환한다.
// PerformanceCounter는 첫 NextValue() 호출이 항상 0을 반환(워밍업) → 생성 시 1회 프라임.
// [2026-10-04 Part 227] 어댑터 재탐색 — 종전에는 생성자에서 단 1회만 열거해, 부팅 직후 Wi-Fi 미연결이면 앱을 끌 때까지 "--",
//   Wi-Fi 재연결·VPN·USB 랜 추가 뒤에는 사라진/새 어댑터가 반영되지 않았다(OI-14, TrafficMonitor 대조 조사에서 코드로 확정).
//   네트워크 변경 이벤트 · 측정 예외 · 어댑터 0개 상태(30틱마다)에서 다음 Read() 때 카운터를 다시 구성한다.
using System.Diagnostics; // PerformanceCounter / PerformanceCounterCategory
using System.Net.NetworkInformation; // NetworkInterface (활성 NIC 선별) · NetworkChange (변경 이벤트)

namespace PCUpCheckTools.Hardware
{
    public class NetworkSpeedReader : IDisposable
    {
        // 어댑터 0개일 때 재시도 간격(틱 = TrayContext 타이머 1초) — 변경 이벤트를 놓친 경우의 안전망
        private const int RetryTicksWhenEmpty = 30;

        // private 필드: camelCase, 언더스코어 없음 (규칙 csharp-winforms.md §2)
        private readonly List<PerformanceCounter> sentCounters = new();
        private readonly List<PerformanceCounter> receivedCounters = new();
        private bool available;
        private bool disposed;
        // 변경 이벤트는 스레드 풀에서 오므로 카운터를 직접 만지지 않고 플래그만 세운다 → UI 타이머 틱의 Read() 가 재구성
        private volatile bool rescanPending;
        private int ticksSinceRescan;

        public NetworkSpeedReader()
        {
            Rescan();
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        }

        private void OnNetworkChanged(object? sender, EventArgs e)
        {
            rescanPending = true;
        }

        // 기존 카운터를 정리하고 지금 활성인 어댑터로 다시 구성한다
        private void Rescan()
        {
            rescanPending = false;
            ticksSinceRescan = 0;
            DisposeCounters();
            try
            {
                // 실제 활성(Up) 상태이고 루프백/터널이 아닌 인터페이스 이름 집합 구성
                HashSet<string> activeNames = GetActiveInterfaceNames();

                var category = new PerformanceCounterCategory("Network Interface");
                string[] instanceNames = category.GetInstanceNames();

                foreach (string instance in instanceNames)
                {
                    // PerformanceCounter 인스턴스명은 '/', '#' 등이 치환되므로, 활성 NIC 이름과 느슨하게 매칭
                    if (!IsInstanceActive(instance, activeNames))
                    {
                        continue;
                    }

                    var sent = new PerformanceCounter("Network Interface", "Bytes Sent/sec", instance, readOnly: true);
                    var received = new PerformanceCounter("Network Interface", "Bytes Received/sec", instance, readOnly: true);

                    // 워밍업(첫 호출 0 반환) — 생성 즉시 1회 프라임
                    sent.NextValue();
                    received.NextValue();

                    sentCounters.Add(sent);
                    receivedCounters.Add(received);
                }

                available = sentCounters.Count > 0;
            }
            catch (Exception ex)
            {
                available = false;
                Console.WriteLine($"[NetworkSpeedReader] 초기화 실패(네트워크 카운터 사용 불가): {ex.Message}");
            }
        }

        // 현재 업/다운 속도를 snapshot 에 채운다. 합산 후 단위 자동 변환.
        public void Read(MetricsSnapshot snapshot)
        {
            if (disposed)
            {
                return;
            }
            ticksSinceRescan++;
            if (rescanPending || (!available && ticksSinceRescan >= RetryTicksWhenEmpty))
            {
                Rescan(); // 새 카운터의 첫 값은 워밍업(0)이라 이번 틱은 0 KB/s 가 표시될 수 있다
            }

            if (!available)
            {
                return; // 카운터 없음 → "--" 폴백 유지
            }

            try
            {
                double totalSent = 0;
                double totalReceived = 0;

                foreach (PerformanceCounter c in sentCounters)
                {
                    totalSent += c.NextValue();
                }
                foreach (PerformanceCounter c in receivedCounters)
                {
                    totalReceived += c.NextValue();
                }

                snapshot.UploadBytesPerSec = totalSent;
                snapshot.DownloadBytesPerSec = totalReceived;
                snapshot.UploadText = FormatSpeed(totalSent);
                snapshot.DownloadText = FormatSpeed(totalReceived);
            }
            catch (Exception ex)
            {
                // 측정 중 예외(NIC 분리 등) → 이번 틱은 "--" 유지하고 계속(튕김 금지)
                Console.WriteLine($"[NetworkSpeedReader] 측정 예외: {ex.Message}");
                rescanPending = true; // [Part 227] 사라진 어댑터 카운터를 계속 붙잡지 않도록 다음 틱에 재구성
            }
        }

        // 바이트/초 → KB/s 또는 MB/s 자동 변환 (1MB = 1024KB 기준)
        private static string FormatSpeed(double bytesPerSec)
        {
            double kb = bytesPerSec / 1024.0;
            if (kb >= 1024.0)
            {
                return $"{kb / 1024.0:0.0} MB/s";
            }
            return $"{kb:0.0} KB/s";
        }

        // 물리/Up 상태이고 루프백·터널이 아닌 인터페이스 이름(Description/Name) 수집
        private static HashSet<string> GetActiveInterfaceNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    {
                        continue;
                    }
                    names.Add(ni.Name);
                    names.Add(ni.Description);
                }
            }
            catch
            {
                // 활성 NIC 조회 실패 시 빈 집합 → IsInstanceActive에서 폴백 허용
            }
            return names;
        }

        // PerformanceCounter 인스턴스명이 활성 NIC 목록과 매칭되는지 판정.
        // 카운터 인스턴스명은 특수문자 치환·이름 차이가 있어 정확 일치가 어려우므로, 명백한 가상/필터 인스턴스만 제외하는 보수적 매칭.
        private static bool IsInstanceActive(string instance, HashSet<string> activeNames)
        {
            // 의미 없는 합계/가상 인스턴스 제외
            if (instance.Contains("isatap", StringComparison.OrdinalIgnoreCase) ||
                instance.Contains("Loopback", StringComparison.OrdinalIgnoreCase) ||
                instance.Contains("Teredo", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 활성 NIC 이름을 알 수 없으면(조회 실패) 가상 제외만 하고 통과(보수적 폴백)
            if (activeNames.Count == 0)
            {
                return true;
            }

            // 카운터 인스턴스명은 '[', ']', '(', ')', '#', '/' 가 '_' 로 치환됨 → 동일 규칙으로 정규화 후 부분 매칭
            string normalizedInstance = Normalize(instance);
            foreach (string name in activeNames)
            {
                string normalizedName = Normalize(name);
                if (normalizedInstance.Contains(normalizedName) || normalizedName.Contains(normalizedInstance))
                {
                    return true;
                }
            }
            return false;
        }

        private static string Normalize(string value)
        {
            return value
                .Replace('(', '_').Replace(')', '_')
                .Replace('[', '_').Replace(']', '_')
                .Replace('#', '_').Replace('/', '_')
                .Replace(" ", string.Empty)
                .ToLowerInvariant();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
            DisposeCounters();
            disposed = true;
            GC.SuppressFinalize(this);
        }

        // [Part 227] 재구성(Rescan)과 Dispose 가 함께 쓰는 카운터 정리
        private void DisposeCounters()
        {
            foreach (PerformanceCounter c in sentCounters)
            {
                c.Dispose();
            }
            foreach (PerformanceCounter c in receivedCounters)
            {
                c.Dispose();
            }
            sentCounters.Clear();
            receivedCounters.Clear();
            available = false;
        }
    }
}
