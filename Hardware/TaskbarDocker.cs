// [Part 273] PC Up Check Tools — tools/HwMonitorMini/TaskbarDocker.cs 에서 옮김. 배치 계산(앵커 3종 · 오프셋 · 클램프 · 세로 판정 · 보조 모니터)은 원본 그대로.
// 바꾼 것(WinUI 앱에 WinForms·WPF 를 섞지 않으려고 + AiMeter 줄과 겹치지 않게):
//   ①WinForms Screen → Win32 MonitorFromWindow/GetMonitorInfo(플로팅 폴백 작업 영역 · 보조 모니터 이름)
//   ②WPF UI Automation(위젯 버튼) → COM IUIAutomation(AiMeter TaskbarApps 와 같은 방식, 필요한 vtable 슬롯만)
//   ③설정 타입 AppSettings → SettingsStore  ④시계 왼쪽 기준에서 AiMeter 줄(AiMeter.TaskbarStrip)이 있으면 그 왼쪽에
// 모든 Win32 호출은 실패해도 예외를 삼키고 false/폴백을 반환하여 앱이 죽지 않도록 한다(원본 원칙).
using System.Runtime.InteropServices;
using PCUpCheckTools.Services;

namespace PCUpCheckTools.Hardware
{
    public class TaskbarDocker
    {
        private const int GWL_STYLE = -16;
        private const long WS_CHILD = 0x40000000L;
        private const long WS_POPUP = 0x80000000L;
        private const long WS_VISIBLE = 0x10000000L;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private IntPtr dockedChild = IntPtr.Zero;
        private IntPtr parentTaskbar = IntPtr.Zero;
        private bool isDocked;
        private RECT lastChildRect;
        private readonly SettingsStore settings;

        public bool IsDocked => isDocked;

        /// <summary>[Part 277] 붙어 있는 작업 표시줄(앱 버튼 끝을 잴 때 쓴다). 안 붙었으면 Zero</summary>
        public IntPtr ParentTaskbar => isDocked ? parentTaskbar : IntPtr.Zero;

        /// <summary>[Part 277] 줄이 「시계 왼쪽」 기준일 때 오른쪽 끝이 될 화면 x — 알림 영역 왼쪽, AiMeter 줄이 있으면 그 왼쪽. 모르면 null</summary>
        public int? TrayLeftEdge()
        {
            if (!isDocked || !GetWindowRect(parentTaskbar, out RECT taskbarRect)) return null;
            return TrayLeft(taskbarRect);
        }

        private int TrayLeft(RECT taskbarRect)
        {
            int left = taskbarRect.Right;
            IntPtr trayNotify = FindWindowEx(parentTaskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (trayNotify != IntPtr.Zero && GetWindowRect(trayNotify, out RECT trayRect)) left = trayRect.Left;
            // [Part 273] AiMeter 줄이 있으면 그 왼쪽
            IntPtr aiMeter = FindWindowEx(parentTaskbar, IntPtr.Zero, "AiMeter.TaskbarStrip", null);
            if (aiMeter != IntPtr.Zero && IsWindowVisible(aiMeter) && GetWindowRect(aiMeter, out RECT aiRect) && aiRect.Left < left) left = aiRect.Left;
            return left;
        }

        // 위젯 회피(UIA) 캐시 — 10틱마다 갱신. -1 = 위젯 버튼 없음/모름 (원본 Part 231)
        private const int WidgetsRefreshTicks = 10;
        private volatile int widgetsRightScreen = -1; // [Part 273] 백그라운드에서 채운다
        private volatile bool widgetsBusy;
        private volatile bool widgetsChanged;
        private int widgetsTick = WidgetsRefreshTicks;
        private IntPtr widgetsTaskbar = IntPtr.Zero;

        public TaskbarDocker(SettingsStore settings)
        {
            this.settings = settings;
        }

        /// <summary>작업 표시줄 목록 — [0] = 주(Shell_TrayWnd), [1..] = 보조(Shell_SecondaryTrayWnd, X 좌표순)</summary>
        public static List<IntPtr> GetTaskbars()
        {
            var list = new List<IntPtr>();
            try
            {
                IntPtr primary = FindWindow("Shell_TrayWnd", null);
                if (primary != IntPtr.Zero)
                {
                    list.Add(primary);
                }
                var secondary = new List<(IntPtr Handle, int Left)>();
                IntPtr h = IntPtr.Zero;
                while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                {
                    if (GetWindowRect(h, out RECT r))
                    {
                        secondary.Add((h, r.Left));
                    }
                }
                foreach (var item in secondary.OrderBy(x => x.Left))
                {
                    list.Add(item.Handle);
                }
            }
            catch
            {
                // 열거 실패 → 지금까지 찾은 것만
            }
            return list;
        }

        /// <summary>설정 화면용 이름 — 「주 모니터」 / 「보조 모니터 1 (오른쪽 · 1920×1080)」 (원본 TrayContext.BuildTaskbarMenu 와 같은 표기)</summary>
        public static List<string> TaskbarNames()
        {
            var names = new List<string>();
            List<IntPtr> bars = GetTaskbars();
            int primaryX = bars.Count > 0 && MonitorBounds(bars[0]) is { } p ? p.Left : 0;
            for (int i = 0; i < bars.Count; i++)
            {
                if (i == 0)
                {
                    names.Add(S.T("주 모니터", "Main display"));
                    continue;
                }
                if (MonitorBounds(bars[i]) is { } b)
                {
                    string side = b.Left < primaryX ? S.T("왼쪽", "left") : S.T("오른쪽", "right");
                    names.Add(S.T($"보조 모니터 {i} ({side} · {b.Right - b.Left}×{b.Bottom - b.Top})", $"Display {i + 1} ({side} · {b.Right - b.Left}×{b.Bottom - b.Top})"));
                }
                else
                {
                    names.Add(S.T($"보조 모니터 {i}", $"Display {i + 1}"));
                }
            }
            return names;
        }

        /// <summary>사용자가 고른 작업 표시줄. 그 모니터가 없어졌으면 주 작업 표시줄로.</summary>
        public IntPtr ResolveTaskbar()
        {
            List<IntPtr> bars = GetTaskbars();
            if (bars.Count == 0)
            {
                return IntPtr.Zero;
            }
            int index = settings.TaskbarIndex;
            return index >= 0 && index < bars.Count ? bars[index] : bars[0];
        }

        /// <summary>붙어 있던 작업 표시줄이 아직 살아 있는가(탐색기 재시작·보조 모니터 분리 시 파괴된다)</summary>
        public bool IsParentAlive()
        {
            return parentTaskbar != IntPtr.Zero && IsWindow(parentTaskbar);
        }

        public void InvalidatePosition()
        {
            lastChildRect = default;
        }

        /// <summary>작업 표시줄의 배율(DPI). 찾지 못하면 96.</summary>
        public int GetTaskbarDpi()
        {
            try
            {
                IntPtr taskbar = ResolveTaskbar();
                if (taskbar == IntPtr.Zero)
                {
                    return 96;
                }
                uint dpi = GetDpiForWindow(taskbar);
                return dpi == 0 ? 96 : (int)dpi;
            }
            catch
            {
                return 96;
            }
        }

        /// <summary>작업 표시줄이 세로(화면 왼쪽·오른쪽)인가 — 세로면 도킹하지 않고 띄운다(원본 Part 218)</summary>
        public bool IsTaskbarVertical()
        {
            try
            {
                IntPtr taskbar = ResolveTaskbar();
                if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out RECT r))
                {
                    return false;
                }
                return (r.Bottom - r.Top) > (r.Right - r.Left);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>창을 작업 표시줄의 자식으로 붙인다. 실패하면 false(호출부가 플로팅).</summary>
        public bool TryDock(IntPtr childHandle, int desiredWidth)
        {
            try
            {
                IntPtr taskbar = ResolveTaskbar();
                if (taskbar == IntPtr.Zero || IsTaskbarVertical())
                {
                    return false;
                }
                IntPtr previousParent = SetParent(childHandle, taskbar);
                if (previousParent == IntPtr.Zero && Marshal.GetLastWin32Error() != 0)
                {
                    return false;
                }
                long style = GetWindowLongPtr(childHandle, GWL_STYLE).ToInt64();
                style = (style & ~WS_POPUP) | WS_CHILD | WS_VISIBLE;
                SetWindowLongPtr(childHandle, GWL_STYLE, new IntPtr(style));

                dockedChild = childHandle;
                parentTaskbar = taskbar;
                isDocked = true;
                lastChildRect = default;
                Reposition(desiredWidth);
                return true;
            }
            catch
            {
                isDocked = false;
                return false;
            }
        }

        /// <summary>작업 표시줄 크기·배율·알림 영역 변화에 맞춰 다시 배치. 위치가 같으면 아무것도 안 한다(원본 그대로).</summary>
        /// <returns>창 크기가 바뀌었으면 true — 호출부가 다시 그린다</returns>
        public bool Reposition(int desiredWidth)
        {
            if (!isDocked || dockedChild == IntPtr.Zero || parentTaskbar == IntPtr.Zero)
            {
                return false;
            }
            if (widgetsChanged)
            {
                widgetsChanged = false;
                lastChildRect = default;
            }
            try
            {
                if (!GetWindowRect(parentTaskbar, out RECT taskbarRect))
                {
                    return false;
                }
                int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
                int taskbarWidth = taskbarRect.Right - taskbarRect.Left;
                int width = Math.Min(desiredWidth, Math.Max(0, taskbarWidth - 8));

                int trayLeftScreen = taskbarRect.Right;
                int trayRightScreen = taskbarRect.Right;
                IntPtr trayNotify = FindWindowEx(parentTaskbar, IntPtr.Zero, "TrayNotifyWnd", null);
                if (trayNotify != IntPtr.Zero && GetWindowRect(trayNotify, out RECT trayRect))
                {
                    trayLeftScreen = trayRect.Left;
                    trayRightScreen = trayRect.Right;
                }
                // [Part 273] AiMeter 의 작업 표시줄 줄도 「알림 영역 왼쪽」 에 붙는다 — 같은 자리에 겹쳐 둘 다 읽을 수 없었다(실화면).
                //   AiMeter 줄이 있으면 그 왼쪽을 기준으로 삼는다(시계 왼쪽 앵커에서만 의미가 있다) — [Part 277] TrayLeft() 로 묶음
                trayLeftScreen = Math.Min(trayLeftScreen, TrayLeft(taskbarRect));

                const int margin = 8;
                int xScreen;
                switch (settings.Anchor)
                {
                    case SettingsStore.AnchorTrayRight:
                        xScreen = trayRightScreen + margin;
                        break;
                    case SettingsStore.AnchorTaskbarLeft:
                        xScreen = taskbarRect.Left + margin;
                        // Win11 가운데 정렬이면 왼쪽 끝에 위젯(날씨) 버튼이 있다 → 그 오른쪽으로(원본 Part 231)
                        int widgetsRight = GetWidgetsRight(taskbarRect);
                        if (widgetsRight > 0)
                        {
                            xScreen = Math.Max(xScreen, widgetsRight + margin);
                        }
                        break;
                    default:
                        xScreen = trayLeftScreen - width - margin;
                        break;
                }
                xScreen += settings.OffsetX;

                int xInParent = Math.Clamp(xScreen - taskbarRect.Left, 0, Math.Max(0, taskbarWidth - width));
                int height = taskbarHeight;
                int yInParent = Math.Clamp(settings.OffsetY, -height, taskbarHeight);

                var newRect = new RECT { Left = xInParent, Top = yInParent, Right = xInParent + width, Bottom = yInParent + height };
                if (newRect.Equals(lastChildRect))
                {
                    return false;
                }
                bool resized = newRect.Right - newRect.Left != lastChildRect.Right - lastChildRect.Left
                               || newRect.Bottom - newRect.Top != lastChildRect.Bottom - lastChildRect.Top;
                lastChildRect = newRect;
                SetWindowPos(dockedChild, IntPtr.Zero, xInParent, yInParent, width, height, SWP_NOACTIVATE | SWP_NOZORDER | SWP_SHOWWINDOW);
                return resized;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>도킹했을 때 창 높이(= 작업 표시줄 높이). 모르면 null.</summary>
        public int? DockedHeight()
        {
            return isDocked && GetWindowRect(parentTaskbar, out RECT r) ? r.Bottom - r.Top : null;
        }

        // Win11 위젯 버튼의 오른쪽 끝(화면 좌표). 주 작업 표시줄 + 버튼이 왼쪽 25% 안(= 가운데 정렬)일 때만. 못 찾으면 -1 (원본 Part 231)
        private int GetWidgetsRight(RECT taskbarRect)
        {
            IntPtr primary = FindWindow("Shell_TrayWnd", null);
            if (parentTaskbar != primary)
            {
                return -1;
            }
            if (widgetsTaskbar == parentTaskbar && ++widgetsTick < WidgetsRefreshTicks)
            {
                return widgetsRightScreen;
            }
            widgetsTick = 0;
            widgetsTaskbar = parentTaskbar;
            // [Part 273] UIA 조회를 UI 스레드에서 하지 않는다 — 실측: 「작업 표시줄 왼쪽 끝」 에서 앱이 잠깐씩 응답 없음(Responding=False),
            //   검사 도구의 UIA 호출도 시간 초과. AiMeter Part 255 처럼 백그라운드에서 재고, 결과는 다음 틱 배치에 쓴다(그동안은 직전 값).
            if (!widgetsBusy)
            {
                widgetsBusy = true;
                IntPtr bar = parentTaskbar;
                int left = taskbarRect.Left, width = taskbarRect.Right - taskbarRect.Left;
                Task.Run(() =>
                {
                    int edge = -1;
                    if (Uia.BoundsByAutomationId(bar, "WidgetsButton") is double[] r && r[2] > 0 && r[0] < left + width / 4)
                    {
                        edge = (int)Math.Ceiling(r[0] + r[2]);
                    }
                    if (edge != widgetsRightScreen) widgetsChanged = true; // 바뀌었으면 다음 틱(UI 스레드)에 다시 배치
                    widgetsRightScreen = edge;
                    widgetsBusy = false;
                });
            }
            return widgetsRightScreen;
        }

        public void Undock()
        {
            if (!isDocked || dockedChild == IntPtr.Zero)
            {
                return;
            }
            try
            {
                if (IsWindow(dockedChild))
                {
                    SetParent(dockedChild, IntPtr.Zero);
                }
            }
            catch
            {
                // 정리 단계 예외 무시
            }
            isDocked = false;
            dockedChild = IntPtr.Zero;
            parentTaskbar = IntPtr.Zero;
        }

        /// <summary>플로팅 폴백 위치 — 대상 작업 표시줄이 있는 모니터의 작업 영역 오른쪽 아래 + 사용자 오프셋(원본과 같은 계산)</summary>
        public (int X, int Y) GetFloatingBottomRight(int width, int height)
        {
            RECT wa = WorkArea(ResolveTaskbar()) ?? new RECT { Left = 0, Top = 0, Right = 1280, Bottom = 720 };
            int x = wa.Right - width - 8 + settings.OffsetX;
            int y = wa.Bottom - height - 8 + settings.OffsetY;
            return (Math.Max(wa.Left, x), Math.Max(wa.Top, y));
        }

        private static RECT? MonitorBounds(IntPtr hwnd) => Monitor(hwnd, work: false);

        private static RECT? WorkArea(IntPtr hwnd) => Monitor(hwnd, work: true);

        private static RECT? Monitor(IntPtr hwnd, bool work)
        {
            try
            {
                IntPtr mon = hwnd != IntPtr.Zero ? MonitorFromWindow(hwnd, 2 /* NEAREST */) : MonitorFromPoint(new POINT(), 1 /* PRIMARY */);
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                return GetMonitorInfo(mon, ref info) ? (work ? info.rcWork : info.rcMonitor) : null;
            }
            catch
            {
                return null;
            }
        }

        // ── Win32 ──
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public bool Equals(RECT other)
            {
                return Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;
            }
        }
    }
}
