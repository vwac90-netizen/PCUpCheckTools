using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace PCUpCheckTools.Services;

// [Part 272] 사양 스캔 — 앱에 넣은 Scan/collect-specs.ps1(apps/web/get_specs.ps1 수집부와 같은 필드)을 PowerShell 로 돌려 JSON 을 받는다.
// 실행 방식은 스캐너 exe(ScannerLauncher.cs)와 같다: 임시 .ps1 + powershell.exe -NoProfile -ExecutionPolicy Bypass -File, 창 없음.
// 다른 점: .env 키를 읽어 app-settings.js 에 쓰던 부분이 없다 · 클립보드는 앱이 넣는다(이 클래스는 JSON 만 돌려준다).
// 결과는 인터넷으로 보내지 않는다 — 사용자가 붙여넣기 전까지 이 PC 의 클립보드에만 있다.
public static class SpecScanner
{
    private const string ResourceName = "PCUpCheckTools.Scan.collect-specs.ps1";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public sealed record Result(string? Json, string? Summary, string? Error);

    public static async Task<Result> RunAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), "PCUpCheckTools");
        string script = Path.Combine(dir, "collect-specs.ps1");
        try
        {
            Directory.CreateDirectory(dir);
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException("collect-specs.ps1 resource missing"))
            using (var reader = new StreamReader(stream))
            {
                // Windows PowerShell 5.1 은 BOM 없는 파일을 시스템 코드 페이지로 읽는다 → BOM 있는 UTF-8
                await File.WriteAllTextAsync(script, await reader.ReadToEndAsync(), new UTF8Encoding(true));
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("powershell.exe did not start");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(Timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new Result(null, null, S.T("60초 안에 끝나지 않아 멈췄습니다.", "It didn't finish within 60 seconds and was stopped."));
            }

            string json = (await stdout).Trim();
            if (json.Length == 0)
            {
                string err = (await stderr).Trim();
                return new Result(null, null, S.T("PowerShell 이 결과를 돌려주지 않았습니다.", "PowerShell returned no result.")
                    + (err.Length > 0 ? " " + FirstLine(err) : ""));
            }
            return new Result(json, Summarize(json), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new Result(null, null, S.T("스캔을 실행하지 못했습니다. ", "Couldn't run the scan. ") + ex.Message);
        }
        catch (JsonException)
        {
            return new Result(null, null, S.T("결과가 올바른 JSON 이 아닙니다.", "The result was not valid JSON."));
        }
        finally
        {
            try { if (File.Exists(script)) File.Delete(script); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>복사했다는 확인용 한 줄 — 「CPU · RAM nGB · GPU」. 값이 없으면 그 칸을 뺀다(지어내지 않는다).</summary>
    private static string Summarize(string json)
    {
        using var doc = JsonDocument.Parse(json); // 올바른 JSON 인지도 여기서 확인된다
        var root = doc.RootElement;
        var parts = new List<string>();
        if (Get(root, "CPU", "Model") is { } cpu) parts.Add(cpu);
        if (root.TryGetProperty("RAM", out var ram) && ram.TryGetProperty("TotalGB", out var gb) && gb.ValueKind == JsonValueKind.Number) parts.Add($"RAM {gb.GetRawText()}GB");
        if (Get(root, "GPU", "Model") is { } gpu) parts.Add(gpu);
        return string.Join(" · ", parts);
    }

    private static string? Get(JsonElement root, string section, string key) =>
        root.TryGetProperty(section, out var s) && s.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();
}
