// [Part 273] PC Up Check Tools — tools/HwMonitorMini/TaskbarTheme.cs 에서 그대로 옮김(네임스페이스만 바꿈). 바꿀 때는 HwMonitorMini 쪽과 함께 볼 것.
// TaskbarTheme — 작업표시줄이 라이트인지 판정 (단일 책임: 테마 조회).
// [2026-10-04 Part 228] Windows 설정 「기본 Windows 모드」(작업표시줄·시작 메뉴 색)는
//   HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\SystemUsesLightTheme (DWORD, 1 = 라이트) 에 있다.
//   「앱 모드」(AppsUseLightTheme)가 아니라 이 값이 작업표시줄 바탕을 정한다. 값이 없거나 읽기 실패면 다크로 본다(종전 동작).
using Microsoft.Win32; // Registry

namespace PCUpCheckTools.Hardware
{
    public static class TaskbarTheme
    {
        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        public static bool IsLight()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                object? value = key?.GetValue("SystemUsesLightTheme");
                return value is int i && i == 1;
            }
            catch
            {
                return false; // 조회 실패 → 다크(튕김 금지)
            }
        }
    }
}
