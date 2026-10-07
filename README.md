# PC Up Check Tools

Windows 트레이에 상주하면서 **배터리 · 하드웨어 수치 · 내 PC 사양**을 한곳에서 보여 주는 무료 앱입니다.
[pcupcheck.com](https://pcupcheck.com) 의 데스크톱 도구 셋(PureBattery · PCUpCheck 미니 · 사양 스캐너)을 하나로 합쳤습니다.

## 이런 걸 할 수 있어요

- **트레이 아이콘 하나** — 배터리 잔량 · CPU 사용률 · CPU 온도 중 고른 숫자를 색으로 보여 줍니다(앱 아이콘만 보이게 할 수도 있어요). 배터리가 없는 PC 의 기본값은 CPU 사용률입니다.
- **팝오버** — 아이콘을 누르면 요약 타일 4개(배터리 · CPU · RAM · CPU 온도)와 탭 3개가 열립니다.
  - **하드웨어**: 업로드 · 다운로드 속도, CPU 속도, 메모리, GPU · NPU 사용률, 디스크 활성, (켜면) VGA · SSD · 메인보드 온도 — 1초마다
  - **배터리**: 잔량과 10칸 막대(80%↑ 초록 · 30%↑ 파랑 · 10%↑ 주황 · 9%↓ 빨강)
  - **내 PC 사양**: 마지막 사양 스캔 결과(CPU · RAM · 그래픽 · 저장 장치 · 화면 · 시스템 · 연식)
- **사양 스캔** — 버튼 하나로 이 PC 사양을 모아 클립보드에 복사합니다. [pcupcheck.com/scanner](https://pcupcheck.com/scanner) 에서 「스캔 데이터 붙여넣기」 를 누르면 AI 업그레이드 진단으로 이어집니다. 결과 형식은 사이트의 사양 스캐너(`PCUpCheck_Setup.exe`)와 같습니다.
- **작업 표시줄 줄** — 시계 옆에 2줄 × 4칸으로 수치를 띄웁니다(칸마다 항목 선택). 클릭은 아래 작업 표시줄로 그대로 통과합니다. 작업 표시줄 앱 버튼이 늘어 자리가 모자라면 오른쪽 열부터 줄이고, 그래도 안 되면 숨겨서 앱 버튼을 덮지 않습니다.
- **설정 창** — 언어(자동 · 한국어 · English), 트레이 아이콘에 표시할 것, Windows 시작 때 실행, 온도, 줄 위치 · 미세조정 · 표시 항목.

모르는 값은 `0` 이 아니라 **「모름」 · 「–」** 로 보여 줍니다.

## 필요한 것

- Windows 10 · 11 (x64)
- 설치할 런타임 없음 — 실행에 필요한 것이 zip 안에 다 들어 있습니다(풀면 약 220MB)

## 시작하기

1. [Releases](https://github.com/vwac90-netizen/PCUpCheckTools/releases/latest) 에서 `PCUpCheckTools-<버전>-win-x64.zip` 을 받습니다.
2. 원하는 폴더에 풀고 `PCUpCheckTools.exe` 를 실행합니다. 처음 실행하면 팝오버가 열리며 사용법을 한 줄로 알려 줍니다.
3. 트레이 아이콘이 안 보이면 작업 표시줄 오른쪽 **^**(숨겨진 아이콘)를 펼치세요.

### ⚠️ 처음 실행할 때 Windows 가 막을 수 있어요

개인이 비영리로 만든 앱이라 아직 **코드 서명이 없습니다.**

- **스마트 앱 제어**가 켜진 PC 에서는 Windows 가 실행을 막습니다. 끄는 것은 권하지 않습니다(Windows 버전에 따라 다시 켜려면 재설치가 필요할 수 있어요). 걱정되면 아래 「직접 빌드」로 만들어 쓰셔도 됩니다.
- 그 밖의 PC 에서는 「Windows의 PC 보호」 파란 창이 뜰 수 있어요 → **「추가 정보」 → 「실행」**.
- 받은 파일이 맞는지는 릴리스 노트의 **SHA-256** 과 비교해 확인할 수 있습니다.
  ```powershell
  Get-FileHash .\PCUpCheckTools-0.1.0-win-x64.zip -Algorithm SHA256
  ```

## 관리자 권한과 데이터

- **인터넷으로 아무것도 보내지 않습니다.** (「진단하러 가기」 를 누르면 브라우저로 pcupcheck.com 을 열 뿐입니다.)
- **사양 스캔** 결과는 클립보드와 `%APPDATA%\PCUpCheckTools\last-scan.json` 에만 남습니다. 스캔은 Windows PowerShell 로 `Win32_Processor` 등 WMI 정보와 `Get-PhysicalDisk` 를 읽습니다(`Scan/collect-specs.ps1`).
- **설정**은 `%APPDATA%\PCUpCheckTools\settings.json`.
- **온도**(기본 꺼짐)는 CPU · SSD · 메인보드 센서를 읽으려면 관리자 권한이 필요합니다. 설정에서 처음 켤 때 **한 번만** UAC 를 묻고 작업 스케줄러에 `PCUpCheckTools\Sensors` 작업(최고 권한 · 트리거 없음)을 등록합니다. 그 뒤로는 앱이 이 작업으로 센서 모드를 띄우므로 다시 묻지 않습니다. 관리자 권한으로 하는 일은 온도 센서 읽기([LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor))뿐이며, 값은 이 사용자만 접속할 수 있는 파이프로 앱에 전달됩니다.

## 지금의 한계

- 코드 서명이 없습니다(위 안내).
- 작업 표시줄 줄의 「자리 맞추기」 는 위치가 **시계 왼쪽**일 때만 동작합니다.
- 배터리가 없는 PC 인지, 배터리 정보를 못 읽은 것인지는 구분할 수 없어 둘 다 「모름」 으로 보입니다.
- 그래픽 메모리(VRAM)는 Windows 가 정확한 값을 주지 않는 경우가 많아 보여 주지 않습니다.

## 지우기

1. 트레이 아이콘 오른쪽 클릭 → **끝내기**
2. 설정에서 「Windows 시작 때 자동 실행」 을 껐다면 그대로, 아니면 끄고 나서 폴더를 지웁니다.
3. 온도를 켰었다면 작업 스케줄러에서 `PCUpCheckTools\Sensors` 를 지웁니다(관리자 PowerShell: `schtasks /Delete /TN "PCUpCheckTools\Sensors" /F`).
4. `%APPDATA%\PCUpCheckTools` 폴더를 지웁니다.

## 직접 빌드

.NET 10 SDK 와 Windows 10 SDK(19041) 가 필요합니다.

```powershell
dotnet publish -c Release -r win-x64 -o out
.\out\PCUpCheckTools.exe
```

## 사용한 것 · 감사

- [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) (WinUI 3) — MIT
- [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) — MIT
- [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) — MPL-2.0 (온도 센서)
- System.Diagnostics.PerformanceCounter — MIT

## 라이선스

[MIT](LICENSE)

---

## English summary

**PC Up Check Tools** is a free Windows tray app that combines three desktop tools from [pcupcheck.com](https://pcupcheck.com): a battery meter, a taskbar hardware monitor, and the spec scanner.

- **Tray icon** shows a number of your choice (battery, CPU usage or CPU temperature) or just the app icon.
- **Popover**: four summary tiles plus *Hardware*, *Battery* and *My PC specs* tabs.
- **Spec scan** copies your PC's specs to the clipboard in the same format as the site's scanner — paste them at [pcupcheck.com/scanner](https://pcupcheck.com/scanner).
- **Taskbar strip**: 2 × 4 numbers next to the clock; clicks pass through; it drops columns (or hides) instead of covering taskbar buttons.
- **Nothing is sent online.** Settings and the last scan stay in `%APPDATA%\PCUpCheckTools`.
- **Temperatures** (off by default) need admin rights: the first time you turn them on, Windows asks once (UAC) and a scheduled task `PCUpCheckTools\Sensors` is registered so it won't ask again. Admin rights are used only to read temperature sensors (LibreHardwareMonitor).
- Not code-signed yet: Smart App Control blocks it; elsewhere use *More info → Run anyway*. Verify the SHA-256 in the release notes.
- Unknown values show as "unknown" / "–", never as 0.

Requirements: Windows 10/11 x64, no runtime install. License: MIT.
