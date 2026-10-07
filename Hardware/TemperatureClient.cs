using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PCUpCheckTools.Sensors;

namespace PCUpCheckTools.Hardware;

// [Part 275 → 277] 온도 받기(본체 · 일반 권한).
// - [Part 277] 켤 때 작업 스케줄러 작업이 없으면 그때 한 번만 UAC 로 등록(SensorTask.RegisterElevated) → 그 뒤로는 schtasks /Run 으로
//   센서 모드를 띄우고(UAC 없음) 그 쪽 파이프에 접속해 1초마다 오는 온도 줄을 읽는다. Part 275 는 실행마다 UAC 였다.
// - 5초 넘게 줄이 오지 않으면 온도는 모름(null) — 멈춘 값을 지금 값처럼 보이지 않게.
// - 끊기면(센서 모드가 죽음 등) 15초마다 다시 /Run · 접속한다.
public sealed class TemperatureClient : IDisposable
{
    public enum Status { Off, Starting, Running, Declined, Failed }

    private static readonly TimeSpan Stale = TimeSpan.FromSeconds(5);

    private CancellationTokenSource? cts;
    private volatile SensorHost.Reading? latest;
    private long latestTicks;
    private volatile Status state = Status.Off;

    public Status State => state;

    /// <summary>켠다. 처음이면 UAC 가 한 번 뜬다(이 호출이 끝날 때까지 기다린다). 거부·실패면 State 로 알린다.</summary>
    public void Start(bool allowRegister)
    {
        Stop();
        if (!SensorTask.IsRegistered())
        {
            if (!allowRegister)
            {
                state = Status.Failed; // 시작할 때는 묻지 않는다 — 설정에서 다시 켜면 등록
                return;
            }
            var (ok, declined) = SensorTask.RegisterElevated();
            if (!ok)
            {
                state = declined ? Status.Declined : Status.Failed;
                return;
            }
        }

        state = Status.Starting;
        cts = new CancellationTokenSource();
        var token = cts.Token;
        _ = Task.Run(() => Loop(token), token);
    }

    private async Task Loop(CancellationToken token)
    {
        string name = SensorHost.PipeName();
        while (!token.IsCancellationRequested)
        {
            SensorTask.Run(); // 이미 돌고 있으면 작업 설정(IgnoreNew)이 새로 띄우지 않는다
            try
            {
                using var pipe = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(15_000, token);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                while (!token.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(token);
                    if (line is null) break; // 센서 모드가 끝났다
                    var reading = JsonSerializer.Deserialize<SensorHost.Reading>(line);
                    if (reading is null) continue;
                    latest = reading;
                    Interlocked.Exchange(ref latestTicks, DateTime.UtcNow.Ticks);
                    state = Status.Running;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or JsonException or ObjectDisposedException or UnauthorizedAccessException)
            {
                // 접속 못 함·끊김 — 아래에서 잠깐 쉬고 다시
            }
            if (token.IsCancellationRequested) break;
            state = Status.Failed;
            try { await Task.Delay(15_000, token); } catch (OperationCanceledException) { break; }
        }
    }

    public void Stop()
    {
        cts?.Cancel(); // 파이프를 닫으면 센서 모드는 쓰기 실패로 스스로 끝난다
        cts?.Dispose();
        cts = null;
        latest = null;
        state = Status.Off;
    }

    /// <summary>스냅샷에 온도를 채운다. 5초 넘게 새 값이 없으면 채우지 않는다(= 「--」).</summary>
    public void Fill(MetricsSnapshot snapshot)
    {
        var r = latest;
        if (r is null || DateTime.UtcNow.Ticks - Interlocked.Read(ref latestTicks) > Stale.Ticks) return;
        snapshot.CpuTemperature = r.Cpu;
        snapshot.GpuTemperature = r.Gpu;
        snapshot.SsdTemperature = r.Ssd;
        snapshot.MotherboardTemperature = r.Mbd;
    }

    public void Dispose() => Stop();
}
