using System.Runtime.InteropServices;

namespace PCUpCheckTools.Services;

// [Part 270] 배터리 잔량 읽기 + 잔량 색 — 트레이 아이콘과 팝오버가 같은 함수를 쓴다.
public static class Battery
{
    /// <summary>
    /// 잔량(0~100). 모르면 null — 0 이나 100 으로 채우지 않는다.
    /// net48 판은 SystemInformation.PowerStatus.BatteryLifePercent(Win32 값 ÷100)를 그대로 그려,
    /// Win32 가 「모름」 으로 주는 255 나 배터리 없는 PC 의 값이 숫자로 보일 수 있었다(TROUBLESHOOTING Part 270).
    /// </summary>
    public static int? ReadPercent()
    {
        if (!GetSystemPowerStatus(out var status)) return null;
        // BatteryFlag 128 = 시스템 배터리 없음 · 255 = 모름. BatteryLifePercent 255 = 모름
        if (status.BatteryFlag is 128 or 255 || status.BatteryLifePercent > 100) return null;
        return status.BatteryLifePercent;
    }

    /// <summary>
    /// 잔량 색(R,G,B). 🔴 net48 판(tools/PureBattery/Program.cs)·웹(battery.html·사전 2종)과 같아야 한다 —
    /// 80%↑ LimeGreen · 30%↑ DeepSkyBlue · 10%↑ Orange · 9%↓ Red.
    /// </summary>
    public static (byte R, byte G, byte B) LevelColor(int batteryPercent)
    {
        if (batteryPercent >= 80) return (0x32, 0xCD, 0x32); // LimeGreen — 최적
        if (batteryPercent >= 30) return (0x00, 0xBF, 0xFF); // DeepSkyBlue — 안정
        if (batteryPercent >= 10) return (0xFF, 0xA5, 0x00); // Orange — 충전 요망
        return (0xFF, 0x00, 0x00);                           // Red — 위험
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
