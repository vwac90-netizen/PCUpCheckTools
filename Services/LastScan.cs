using System.Globalization;
using System.Text.Json;

namespace PCUpCheckTools.Services;

// [Part 277] 마지막 사양 스캔 — 팝오버 「내 PC 사양」 탭이 보여 준다(사용자: "스캔한 사양도 표기되면 좋은데...").
// - %APPDATA%\PCUpCheckTools\last-scan.json 에 스캔 JSON 그대로 + 시각. 이 PC 에만 남고 어디로도 보내지 않는다.
// - 화면 요약은 스캔 JSON 에 있는 값만 쓴다 — 없거나 모르는 값(Unknown·0·빈 칸)은 그 줄을 뺀다(지어내지 않는다).
public static class LastScan
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCUpCheckTools", "last-scan.json");

    public sealed record Row(string Name, string Value);

    public sealed record Summary(DateTimeOffset ScannedAt, IReadOnlyList<Row> Rows);

    public static void Save(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using var doc = JsonDocument.Parse(json);
            var wrapper = new Dictionary<string, object> { ["ScannedAt"] = DateTimeOffset.Now, ["Data"] = doc.RootElement };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(wrapper));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 저장 실패 — 이번 실행 동안만 화면에 남는다(호출부가 요약을 들고 있다)
        }
    }

    public static Summary? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var at = doc.RootElement.GetProperty("ScannedAt").GetDateTimeOffset();
            return new Summary(at, Rows(doc.RootElement.GetProperty("Data")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public static Summary FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new Summary(DateTimeOffset.Now, Rows(doc.RootElement));
    }

    private static List<Row> Rows(JsonElement d)
    {
        var rows = new List<Row>();
        void Add(string ko, string en, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add(new Row(S.T(ko, en), value));
        }

        // CPU — 「Intel Core Ultra 7 258V · 8코어」
        string? cpu = Clean(Str(d, "CPU", "Model"));
        int cores = Int(d, "CPU", "Cores");
        Add("CPU", "CPU", cpu is null ? null : cores > 0 ? S.T($"{cpu} · {cores}코어", $"{cpu} · {cores} cores") : cpu);

        // RAM — 「32GB LPDDR5 · 8533MHz」
        int gb = Int(d, "RAM", "TotalGB");
        int mhz = Int(d, "RAM", "Speed");
        string? type = Str(d, "RAM", "Type");
        if (gb > 0) Add("RAM", "RAM", $"{gb}GB{(type is null ? "" : " " + type)}{(mhz > 0 ? $" · {mhz}MHz" : "")}");

        // 그래픽 — 모델명만(VRAM_GB 는 믿을 수 없어 보이지 않는다: CLAUDE.md §3.1)
        Add("그래픽", "Graphics", Clean(Str(d, "GPU", "Model")));

        // 저장 장치 — 여러 개면 「·」 로
        if (d.TryGetProperty("Disk", out var disk))
        {
            var items = disk.ValueKind == JsonValueKind.Array ? disk.EnumerateArray().ToList() : new List<JsonElement> { disk };
            var parts = items.Select(x =>
            {
                string? model = Str(x, "Model");
                int size = IntOf(x, "SizeGB");
                string? bus = Str(x, "BusType");
                var bits = new List<string>();
                if (model is not null) bits.Add(model);
                if (size > 0) bits.Add($"{size}GB");
                if (bus is not null) bits.Add(bus);
                return string.Join(" ", bits);
            }).Where(s => s.Length > 0);
            Add("저장 장치", "Storage", string.Join(" · ", parts));
        }

        int w = Int(d, "Display", "Width"), h = Int(d, "Display", "Height"), hz = Int(d, "Display", "RefreshRate");
        if (w > 0 && h > 0) Add("화면", "Display", $"{w}×{h}{(hz > 0 ? $" · {hz}Hz" : "")}");

        string? maker = Str(d, "System", "Manufacturer"), model2 = Str(d, "System", "Model"), os = Str(d, "Software", "OS");
        Add("시스템", "System", string.Join(" · ", new[] { string.Join(" ", new[] { maker, model2 }.Where(x => x is not null)), os ?? "" }.Where(x => x.Length > 0)));

        // 연식 — BIOS 날짜를 알 때만(Part 274 이전 스캔은 날짜를 못 읽어 0 이었다)
        string? hwDate = Str(d, "Maintenance", "HardwareDate");
        if (hwDate is not null && d.TryGetProperty("Maintenance", out var m) && m.TryGetProperty("SystemAgeYears", out var age) && age.ValueKind == JsonValueKind.Number)
            Add("연식", "Age", S.T($"약 {age.GetDouble().ToString("0.#", CultureInfo.InvariantCulture)}년 (BIOS {hwDate})", $"about {age.GetDouble().ToString("0.#", CultureInfo.InvariantCulture)} years (BIOS {hwDate})"));
        return rows;
    }

    private static string? Str(JsonElement d, string section, string key) =>
        d.TryGetProperty(section, out var s) ? Str(s, key) : null;

    private static string? Str(JsonElement s, string key)
    {
        if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty(key, out var v)) return null;
        string? text = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) || text is "Unknown" ? null : text.Trim();
    }

    private static int Int(JsonElement d, string section, string key) =>
        d.TryGetProperty(section, out var s) ? IntOf(s, key) : 0;

    private static int IntOf(JsonElement s, string key) =>
        s.ValueKind == JsonValueKind.Object && s.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double x) ? (int)Math.Round(x) : 0;

    /// <summary>「Intel(R) Core(TM)」 의 상표 기호만 뺀다 — 모델명 자체는 그대로</summary>
    private static string? Clean(string? s) => s?.Replace("(R)", "").Replace("(TM)", "").Replace("  ", " ").Trim();
}
