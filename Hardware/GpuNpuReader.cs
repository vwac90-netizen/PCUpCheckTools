// [Part 273] PC Up Check Tools — tools/HwMonitorMini/GpuNpuReader.cs 에서 그대로 옮김(네임스페이스만 바꿈). 바꿀 때는 HwMonitorMini 쪽과 함께 볼 것.
// GpuNpuReader — GPU·NPU 사용률 수집 (단일 책임).
// [2026-10-04 Part 233] 작업 관리자 「성능」 탭과 같은 방식:
//   - 값: PerformanceCounter 범주 "GPU Engine" 의 "Utilization Percentage" — 인스턴스 이름이
//     pid_…_luid_0x…_0x…_phys_N_eng_N_engtype_<종류> 이다. 같은 장치(LUID)·같은 엔진 종류끼리 더하고,
//     그 장치의 사용률 = 엔진 종류별 합 중 최댓값(작업 관리자 GPU % 와 같은 정의).
//   - 장치 구분: 모양으로 추정하지 않는다. LUID 로 D3DKMTOpenAdapterFromLuid → QueryAdapterInfo
//     (15 = 어댑터 종류 플래그, 65 = 이름). ComputeOnly = NPU · Software(Microsoft Basic Render Driver) = 제외 · 나머지 = GPU.
//     실측(이 PC): Arc 140V = 렌더+디스플레이, Intel(R) AI Boost = ComputeOnly, Basic Render Driver = Software.
//   - 「GPU Engine」 첫 읽기는 0.6~4.8초 걸린다(이후 1~3ms) → 백그라운드에서 데운 뒤부터 값을 낸다(그전엔 null = "--").
//   - 관리자 권한이 필요 없다(Lite 판에서도 동작).
using System.Diagnostics;               // PerformanceCounterCategory
using System.Runtime.InteropServices;   // D3DKMT P/Invoke
using System.Text.RegularExpressions;

namespace PCUpCheckTools.Hardware
{
    public class GpuNpuReader : IDisposable
    {
        private const string Category = "GPU Engine";
        private const string CounterName = "utilization percentage"; // ReadCategory 의 키는 소문자

        private enum AdapterKind { Gpu, Npu, Ignore }

        // private 필드: camelCase, 언더스코어 없음 (규칙 §2)
        // [2026-10-05 Part 235] 스레드 전제 — previous·kinds 는 일부만 lock 으로 보호한다. 지금 안전한 이유:
        //   ① Read 는 ready == true 이후에만 진행하고, 데우기(Task.Run)는 ready 를 세우기 전에 끝난다
        //   ② Read 호출처는 HardwareMonitor 의 1초 타이머(DispatcherQueueTimer) 하나 = UI 스레드 단독 (Part 273 — 원본은 TrayContext 의 WinForms 타이머)
        //   다른 스레드에서 Read 를 부르게 되면 Read 전체(previous.Keys 순회·KindOf 의 kinds 캐시 포함)를 lock 안으로 넓혀라.
        private PerformanceCounterCategory? category;
        private volatile bool ready;       // 첫 읽기(데우기) 완료
        private bool disposed;
        private readonly Dictionary<string, CounterSample> previous = new();      // 인스턴스별 직전 원시 표본
        private readonly Dictionary<string, AdapterKind> kinds = new();           // LUID 문자열 → 종류(한 번 조회 후 캐시)

        // NPU 가 있는 PC 인가(메뉴에서 NPU 항목을 회색으로 할지). 데우기가 끝나기 전엔 null.
        public bool? HasNpu { get; private set; }

        public GpuNpuReader()
        {
            // 첫 읽기가 느리므로 UI 스레드를 막지 않게 백그라운드에서 데운다
            Task.Run(() =>
            {
                try
                {
                    var cat = new PerformanceCounterCategory(Category);
                    InstanceDataCollection data = cat.ReadCategory()[CounterName];
                    lock (previous)
                    {
                        Capture(data);
                        HasNpu = previous.Keys.Select(LuidOf).OfType<string>().Distinct()
                            .Any(l => KindOf(l) == AdapterKind.Npu);
                        category = cat;
                    }
                    ready = true;
                }
                catch (Exception ex)
                {
                    HasNpu = false;
                    Console.WriteLine($"[GpuNpuReader] GPU Engine 카운터 사용 불가: {ex.Message}");
                }
            });
        }

        // 지금 GPU·NPU 사용률을 snapshot 에 채운다. 데우기 전·실패 시 null(= "--").
        public void Read(MetricsSnapshot snapshot)
        {
            if (!ready || disposed || category is null)
            {
                return;
            }
            try
            {
                InstanceDataCollection data = category.ReadCategory()[CounterName];
                // 장치(LUID) → 엔진 종류 → 합
                var sums = new Dictionary<string, Dictionary<string, double>>();
                lock (previous)
                {
                    foreach (InstanceData inst in data.Values)
                    {
                        string name = inst.InstanceName;
                        if (!previous.TryGetValue(name, out CounterSample before))
                        {
                            continue; // 새로 생긴 인스턴스(새 프로세스) — 다음 틱부터 계산
                        }
                        float value = CounterSample.Calculate(before, inst.Sample);
                        string? luid = LuidOf(name);
                        string engine = EngineOf(name);
                        if (luid is null || float.IsNaN(value) || value <= 0)
                        {
                            continue;
                        }
                        if (!sums.TryGetValue(luid, out var byEngine))
                        {
                            byEngine = new Dictionary<string, double>();
                            sums[luid] = byEngine;
                        }
                        byEngine[engine] = byEngine.GetValueOrDefault(engine) + value;
                    }
                    Capture(data);
                }

                double? gpu = null, npu = null;
                foreach (string luid in previous.Keys.Select(LuidOf).OfType<string>().Distinct())
                {
                    AdapterKind kind = KindOf(luid);
                    if (kind == AdapterKind.Ignore)
                    {
                        continue;
                    }
                    double usage = sums.TryGetValue(luid, out var byEngine) && byEngine.Count > 0 ? byEngine.Values.Max() : 0;
                    usage = Math.Min(100, usage);
                    if (kind == AdapterKind.Gpu)
                    {
                        gpu = Math.Max(gpu ?? 0, usage); // GPU 가 여럿(내장 + 외장)이면 가장 바쁜 쪽
                    }
                    else
                    {
                        npu = Math.Max(npu ?? 0, usage);
                    }
                }
                snapshot.GpuUsagePercent = gpu;
                snapshot.NpuUsagePercent = npu;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GpuNpuReader] 측정 예외: {ex.Message}"); // 이번 틱 "--", 튕김 금지
            }
        }

        // 이번 원시 표본을 다음 계산의 기준으로 저장(사라진 프로세스 인스턴스는 버린다)
        private void Capture(InstanceDataCollection data)
        {
            previous.Clear();
            foreach (InstanceData inst in data.Values)
            {
                previous[inst.InstanceName] = inst.Sample;
            }
        }

        private static string? LuidOf(string instance)
        {
            Match m = Regex.Match(instance, @"luid_0x([0-9a-fA-F]{8})_0x([0-9a-fA-F]{8})");
            return m.Success ? (m.Groups[1].Value + "_" + m.Groups[2].Value).ToLowerInvariant() : null;
        }

        private static string EngineOf(string instance)
        {
            int i = instance.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
            return i < 0 ? string.Empty : instance.Substring(i + 8);
        }

        // LUID → 장치 종류(D3DKMT 로 1회 조회 후 캐시). 조회 실패면 GPU 로 본다(종전 방식에 가까운 안전한 쪽).
        private AdapterKind KindOf(string luid)
        {
            if (kinds.TryGetValue(luid, out AdapterKind cached))
            {
                return cached;
            }
            AdapterKind kind = AdapterKind.Gpu;
            try
            {
                string[] parts = luid.Split('_');
                var open = new OpenAdapterFromLuid
                {
                    LuidHigh = unchecked((int)Convert.ToUInt32(parts[0], 16)),
                    LuidLow = Convert.ToUInt32(parts[1], 16)
                };
                if (D3DKMTOpenAdapterFromLuid(ref open) == 0)
                {
                    IntPtr buffer = Marshal.AllocHGlobal(4);
                    try
                    {
                        var query = new QueryAdapterInfo { hAdapter = open.hAdapter, Type = KmtqaiTypeAdapterType, pPrivateDriverData = buffer, PrivateDriverDataSize = 4 };
                        if (D3DKMTQueryAdapterInfo(ref query) == 0)
                        {
                            uint flags = (uint)Marshal.ReadInt32(buffer);
                            if ((flags & AdapterTypeSoftwareDevice) != 0)
                            {
                                kind = AdapterKind.Ignore;
                            }
                            else if ((flags & AdapterTypeComputeOnly) != 0)
                            {
                                kind = AdapterKind.Npu;
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                        uint handle = open.hAdapter;
                        D3DKMTCloseAdapter(ref handle);
                    }
                }
            }
            catch
            {
                // 조회 실패 → GPU 로 본다
            }
            kinds[luid] = kind;
            return kind;
        }

        public void Dispose()
        {
            disposed = true;
            GC.SuppressFinalize(this);
        }

        // ── D3DKMT (gdi32) — 커널 그래픽 어댑터 정보 ──
        private const int KmtqaiTypeAdapterType = 15;            // KMTQAITYPE_ADAPTERTYPE → D3DKMT_ADAPTERTYPE 비트 플래그
        private const uint AdapterTypeSoftwareDevice = 1u << 2;  // SoftwareDevice
        private const uint AdapterTypeComputeOnly = 1u << 11;    // ComputeOnly (NPU 등 MCDM 장치)

        [DllImport("gdi32.dll")]
        private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapterFromLuid open);

        [DllImport("gdi32.dll")]
        private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo query);

        [DllImport("gdi32.dll")]
        private static extern int D3DKMTCloseAdapter(ref uint hAdapter);

        [StructLayout(LayoutKind.Sequential)]
        private struct OpenAdapterFromLuid
        {
            public uint LuidLow;
            public int LuidHigh;
            public uint hAdapter;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct QueryAdapterInfo
        {
            public uint hAdapter;
            public int Type;
            public IntPtr pPrivateDriverData;
            public uint PrivateDriverDataSize;
        }
    }
}
