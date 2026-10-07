using System.Text.Json;

namespace PCUpCheckTools.Services;

// [Part 271] 사용자 설정. %APPDATA%\PCUpCheckTools\settings.json (AiMeter SettingsStore 와 같은 저장 방식).
public sealed class SettingsStore
{
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCUpCheckTools");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    /// <summary>auto(Windows 표시 언어) · ko · en</summary>
    public string Language { get; set; } = LangAuto;

    public const string LangAuto = "auto";
    public const string LangKo = "ko";
    public const string LangEn = "en";

    // ── [Part 273] 작업 표시줄 하드웨어 줄 — HwMonitorMini AppSettings(hwmonitor.settings.json)와 같은 항목. 그 파일을 가져오지는 않는다(계획) ──

    /// <summary>작업 표시줄에 하드웨어 수치 줄을 붙일까. HwMonitorMini 는 늘 붙었다 → 기본 켬.</summary>
    public bool TaskbarStrip { get; set; } = true;

    /// <summary>붙일 위치 — 시계 왼쪽(기본) · 시계 오른쪽 · 작업 표시줄 왼쪽 끝</summary>
    public string Anchor { get; set; } = AnchorTrayLeft;

    public const string AnchorTrayLeft = "trayLeft";
    public const string AnchorTrayRight = "trayRight";
    public const string AnchorTaskbarLeft = "taskbarLeft";

    /// <summary>가로·세로 미세조정(px, +오른쪽/아래)</summary>
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }

    /// <summary>0 = 주 모니터, 1.. = 보조 모니터(X 좌표순)</summary>
    public int TaskbarIndex { get; set; }

    /// <summary>8칸 항목(MetricKind 이름, 열 우선). 없거나 잘못되면 기본 배치</summary>
    public string[]? Slots { get; set; }

    /// <summary>[Part 275] 온도(CPU·VGA·SSD·메인보드) — 관리자 센서 모드를 띄운다(UAC). 기본 꺼짐</summary>
    public bool TemperatureSensors { get; set; }

    /// <summary>[Part 277] 트레이 아이콘에 표시 — auto(배터리가 있으면 배터리, 없으면 CPU 사용률) · battery · cpu · cputemp · app</summary>
    public string TrayShow { get; set; } = TrayAuto;

    public const string TrayAuto = "auto";
    public const string TrayBattery = "battery";
    public const string TrayCpu = "cpu";
    public const string TrayCpuTemp = "cputemp";
    public const string TrayApp = "app";

    /// <summary>[Part 277] 작업 표시줄 줄이 앱 버튼까지 닿을 때 — shrink(오른쪽 열부터 빼고 한 열도 안 되면 숨김) · hide(다 안 들어가면 숨김)</summary>
    public string StripOverflow { get; set; } = OverflowShrink;

    public const string OverflowShrink = "shrink";
    public const string OverflowHide = "hide";

    /// <summary>설정 파일이 없었다 = 처음 실행. PureBattery(net48)의 실행 전 안내창 대신 팝오버를 한 번 열어 안내한다.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFirstRun { get; private set; }

    public static SettingsStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<SettingsStore>(File.ReadAllText(FilePath));
                if (loaded is not null)
                {
                    if (loaded.Language is not (LangAuto or LangKo or LangEn)) loaded.Language = LangAuto;
                    if (loaded.Anchor is not (AnchorTrayLeft or AnchorTrayRight or AnchorTaskbarLeft)) loaded.Anchor = AnchorTrayLeft;
                    if (loaded.TrayShow is not (TrayAuto or TrayBattery or TrayCpu or TrayCpuTemp or TrayApp)) loaded.TrayShow = TrayAuto;
                    if (loaded.StripOverflow is not (OverflowShrink or OverflowHide)) loaded.StripOverflow = OverflowShrink;
                    return loaded;
                }
            }
            else
            {
                return new SettingsStore { IsFirstRun = true };
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 읽지 못하면 기본값으로 시작한다 — 파일은 다음 저장 때 덮어쓴다
        }
        return new SettingsStore();
    }

    /// <returns>저장에 실패하면 false — 화면이 알릴 수 있게</returns>
    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
