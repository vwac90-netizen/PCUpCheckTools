using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace PCUpCheckTools.Services;

// [Part 315] 업데이트 — 사용자: "자동업데이트도 가능하게 해줘. 알림이 뜨고".
// - 확인: GitHub releases/latest 에서 버전 · 이 아키텍처 zip · GitHub 가 계산한 SHA-256(digest)만 읽는다. 이 앱이 보내는 것은 요청 자체(버전이 든 User-Agent)뿐 —
//   사양 · 설정 · 기기 정보는 보내지 않는다. 지금 버전보다 높을 때만 알린다(같거나 낮으면 조용히).
// - 설치: 폴더에 쓸 수 있는지 먼저 본다 → zip 을 받아 크기 · SHA-256 을 대조 → 풀어서 새 exe 의 버전까지 확인 →
//   PowerShell 도우미가 앱이 끝나길 기다려 **같은 폴더**에 덮어쓰고(robocopy) 다시 실행한다. 경로가 같아 자동 실행 값 · 온도 작업(PCUpCheckTools\Sensors)을 다시 등록할 필요가 없다.
//   덮어쓰기 전에 지금 파일을 사본으로 두고, 덮어쓰기에 실패하면 사본으로 되돌린다(섞인 판이 남지 않게). 기록은 %TEMP%\PCUpCheckTools-update\update.log.
// - digest 가 없거나, 이 아키텍처 zip 이 없으면 자동 설치를 하지 않고 릴리스 페이지만 연다(검증할 수 없는 파일은 실행하지 않는다).
public static class UpdateService
{
    private const string Repo = "vwac90-netizen/PCUpCheckTools";
    private const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";

    /// <summary>업데이트 뒤 다시 실행할 때 붙이는 인자 — App 이 「업데이트했습니다」 를 띄운다</summary>
    public const string UpdatedArgument = "--updated";

    /// <summary>덮어쓰지 못해 지금 판으로 되돌렸을 때 — App 이 실패를 알린다</summary>
    public const string UpdateFailedArgument = "--update-failed";

    public sealed record Release(Version Version, string Tag, string PageUrl, string? ZipUrl, long ZipSize, string? Sha256);

    public enum CheckStatus { UpToDate, Available, Failed }

    public sealed record CheckResult(CheckStatus Status, Release? Release);

    /// <summary>지금 실행 중인 버전(csproj Version — InformationalVersion 의 「+커밋」 앞)</summary>
    public static Version Current { get; } = ParseVersion(
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]) ?? new Version(0, 0, 0);

    public static string CurrentText => Current.ToString(3);

    // 정적 필드는 적힌 순서대로 초기화된다 — Current 보다 앞에 두면 User-Agent 를 만들 때 Current 가 null 이라 형식 초기화가 실패했다(시험 중 실측: 앱 전체 종료)
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub API 는 User-Agent 가 없으면 거부한다. 앱 이름과 버전만 — 사용자 · 기기 정보는 넣지 않는다
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PCUpCheckTools", CurrentText));
        return http;
    }

    private static string FeedUrl()
    {
#if DEBUG
        // 디버그 빌드에서만 — 가짜 릴리스로 검증 실패 경로를 시험한다(배포판에는 이 분기가 없다)
        if (Environment.GetEnvironmentVariable("PCUPCHECKTOOLS_UPDATE_FEED") is { Length: > 0 } feed) return feed;
#endif
        return LatestApi;
    }

    public static async Task<CheckResult> CheckAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, FeedUrl());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new CheckResult(CheckStatus.Failed, null);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (ParseVersion(tag.TrimStart('v', 'V')) is not Version version) return new CheckResult(CheckStatus.Failed, null);
            if (version <= Current) return new CheckResult(CheckStatus.UpToDate, null);

            string page = root.TryGetProperty("html_url", out var h) && h.GetString() is string u ? u : ReleasesPage;
            string want = $"PCUpCheckTools-{version.ToString(3)}-win-{(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64")}.zip";
            string? zipUrl = null, sha = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    if (a.GetProperty("name").GetString() != want) continue;
                    zipUrl = a.GetProperty("browser_download_url").GetString();
                    size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long n) ? n : 0;
                    if (a.TryGetProperty("digest", out var d) && d.GetString() is string digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        sha = digest[7..].ToLowerInvariant();
                }
            }
            return new CheckResult(CheckStatus.Available, new Release(version, tag, page, zipUrl, size, sha));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new CheckResult(CheckStatus.Failed, null);
        }
    }

    /// <summary>자동 설치가 가능한 릴리스인가 — 이 아키텍처 zip 과 SHA-256 이 둘 다 있어야 한다</summary>
    public static bool CanInstall(Release r) => r.ZipUrl is not null && r.Sha256 is { Length: 64 };

    /// <summary>받고 · 검증하고 · 도우미를 띄운다. 성공하면 null(호출부가 곧바로 앱을 끝내야 한다), 실패하면 사용자에게 보일 문구.</summary>
    public static async Task<string?> PrepareAsync(Release r, IProgress<double>? progress)
    {
        if (!CanInstall(r)) return S.T("이 버전은 자동으로 설치할 수 없습니다. 다운로드 페이지에서 받아 주세요.", "This version can't be installed automatically. Please download it from the release page.");
        string target = AppContext.BaseDirectory.TrimEnd('\\', '/');
        string? exe = Environment.ProcessPath;
        if (exe is null) return S.T("앱 위치를 알 수 없습니다.", "Couldn't find the app's location.");
        if (!CanWrite(target))
            return S.T("이 폴더에는 쓸 수 없어 자동으로 바꿀 수 없습니다. 다운로드 페이지에서 받아 주세요.", "Can't write to the app folder, so it can't update itself. Please download it from the release page.");

        string work = Path.Combine(Path.GetTempPath(), "PCUpCheckTools-update");
        string zip = Path.Combine(work, $"PCUpCheckTools-{r.Version.ToString(3)}.zip");
        string extract = Path.Combine(work, "new");
        try
        {
            if (Directory.Exists(extract)) Directory.Delete(extract, recursive: true);
            Directory.CreateDirectory(work);

            // 받기 — 진행률은 크기를 알 때만
            using (var response = await Http.GetAsync(r.ZipUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? r.ZipSize;
                await using var src = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var dst = File.Create(zip);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            // 검증 — 크기 · SHA-256(GitHub 가 계산한 digest)
            var info = new FileInfo(zip);
            if (r.ZipSize > 0 && info.Length != r.ZipSize) return Mismatch(zip);
            string actual;
            await using (var fs = File.OpenRead(zip)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs).ConfigureAwait(false)).ToLowerInvariant();
            if (actual != r.Sha256) return Mismatch(zip);

            // 풀기 — 릴리스 zip 은 「PCUpCheckTools-x.y.z\」 폴더 하나(Part 292). 폴더 없이 묶인 것도 받는다
            ZipFile.ExtractToDirectory(zip, extract);
            File.Delete(zip); // 푼 뒤에는 필요 없다(약 47MB)
            string? source = Directory.GetFiles(extract, "PCUpCheckTools.exe", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName).OrderBy(d => d!.Length).FirstOrDefault();
            if (source is null) return S.T("받은 파일에 앱이 없습니다.", "The download doesn't contain the app.");
            string? newVersion = FileVersionInfo.GetVersionInfo(Path.Combine(source, "PCUpCheckTools.exe")).ProductVersion?.Split('+')[0];
            if (ParseVersion(newVersion) != r.Version)
                return S.T("받은 앱의 버전이 릴리스와 다릅니다. 설치하지 않았습니다.", "The downloaded app's version doesn't match the release. Nothing was installed.");

            // 도우미 — 앱이 끝나길 기다려 덮어쓰고 다시 실행
            string script = Path.Combine(work, "apply.ps1");
            File.WriteAllText(script, ApplyScript, new System.Text.UTF8Encoding(true));
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                         "-Source", source, "-Target", target, "-Exe", exe, "-AppPid", Environment.ProcessId.ToString(), "-Work", work })
                psi.ArgumentList.Add(a);
            Process.Start(psi);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            return S.T("업데이트를 받지 못했습니다. 잠시 뒤 다시 해 보거나 다운로드 페이지에서 받아 주세요.", "Couldn't download the update. Try again later or download it from the release page.");
        }
    }

    private static string Mismatch(string zip)
    {
        try { File.Delete(zip); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return S.T("받은 파일이 릴리스와 다릅니다(SHA-256 불일치). 설치하지 않았습니다.", "The download doesn't match the release (SHA-256 mismatch). Nothing was installed.");
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, $".update-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Version? ParseVersion(string? text) =>
        Version.TryParse(text, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;

    // 앱이 끝나면(온도 센서 모드도 같은 exe 라 함께 기다린다) 지금 파일을 사본으로 두고 → 새 파일로 덮어쓰고 → 실패하면 사본으로 되돌린 뒤 → 다시 실행.
    // robocopy 종료 코드 0~7 = 성공, 8 이상 = 실패. /R:30 /W:1 — 잠긴 파일은 1초 간격으로 30번까지 다시 시도.
    private const string ApplyScript = """
param([string]$Source, [string]$Target, [string]$Exe, [int]$AppPid, [string]$Work)
$log = Join-Path $Work 'update.log'
function Write-Log($m) { Add-Content -LiteralPath $log -Value ("{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $m) }
Write-Log "start $Source -> $Target"
$result = '--update-failed'
try { Wait-Process -Id $AppPid -Timeout 60 -ErrorAction SilentlyContinue } catch {}
$name = [IO.Path]::GetFileNameWithoutExtension($Exe)
for ($i = 0; $i -lt 30; $i++) {
    $left = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
    if ($left.Count -eq 0) { break }
    Start-Sleep -Seconds 1
}
$backup = Join-Path $Work 'previous'
if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue }
robocopy $Target $backup /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
$backupCode = $LASTEXITCODE
if ($backupCode -ge 8) {
    Write-Log "backup failed ($backupCode) - not updating"
} else {
    robocopy $Source $Target /E /R:30 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    $code = $LASTEXITCODE
    if ($code -ge 8) {
        Write-Log "copy failed ($code) - restoring previous files"
        robocopy $backup $Target /E /R:30 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        Write-Log "restore exit $LASTEXITCODE"
    } else {
        Write-Log "copied ($code)"
        $result = '--updated'
        Remove-Item -LiteralPath $Source -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Start-Process -FilePath $Exe -ArgumentList $result
Write-Log "restarted"
""";
}
