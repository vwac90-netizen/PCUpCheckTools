// [Part 273] 작업 표시줄에 붙는 하드웨어 수치 창 — tools/HwMonitorMini/MetricsView.cs(WinForms Form + 크로마키)를 대신한다.
// - 그리기는 원본 MetricsRenderer 그대로. 창은 AiMeter TaskbarStrip 과 같은 순수 Win32 픽셀 알파 창(UpdateLayeredWindow) —
//   크로마키는 글자 가장자리가 키 색과 섞여 테마마다 키를 바꿔야 했다(원본 Part 228). 픽셀 알파는 바탕이 그냥 투명이다.
// - 클릭 통과(WS_EX_TRANSPARENT) · Alt+Tab 비표시(TOOLWINDOW) · 포커스 비탈취(NOACTIVATE) — 원본과 같다(작업 표시줄 조작을 막지 않는다).
// - 창 핸들은 Create 로 새로 만든다. 탐색기 재시작 등으로 부모와 함께 파괴되면 호출부가 새로 만든다
//   (원본 Part 231 실측: 핸들만 되살리면 붙고 보이는데도 아무것도 그려지지 않았다).
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PCUpCheckTools.Hardware
{
    public sealed class MetricsStrip : IDisposable
    {
        private const string ClassName = "PCUpCheckTools.MetricsStrip";
        private static WndProc? procKeepAlive; // 대리자가 GC 되면 창 프로시저 호출이 죽는다

        private readonly MetricsRenderer renderer = new(96);
        private MetricsSnapshot snapshot = new();
        private string lastSignature = string.Empty;
        private Size lastSize;

        public IntPtr Handle { get; private set; }
        public int DesiredWidth => renderer.DesiredWidth;
        public bool IsAlive => Handle != IntPtr.Zero && IsWindow(Handle);

        /// <summary>새 창을 만든다(최상위 팝업 · 아직 부모 없음). 이미 있으면 버리고 새로.</summary>
        public void Create()
        {
            Destroy();
            EnsureClass();
            Handle = CreateWindowEx(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
                ClassName, "PC Up Check Tools", unchecked((uint)WS_POPUP), 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            lastSignature = string.Empty;
            lastSize = Size.Empty;
        }

        public void Destroy()
        {
            if (IsAlive) DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }

        /// <summary>[Part 277] 자리가 모자라 숨기거나 다시 보인다 — 창은 그대로 두고(도킹 유지) 보이기만 바꾼다</summary>
        public void SetHidden(bool hidden)
        {
            if (!IsAlive) return;
            ShowWindow(Handle, hidden ? 0 /* SW_HIDE */ : 4 /* SW_SHOWNOACTIVATE */);
            if (!hidden) Redraw();
        }

        /// <summary>배율이 바뀌면 글자·폭을 다시 잰다. 바뀌었으면 true.</summary>
        public bool ApplyDpi(int dpi)
        {
            if (!renderer.SetDpi(dpi)) return false;
            lastSignature = string.Empty;
            return true;
        }

        public void ApplySlots(MetricKind[] slots)
        {
            renderer.SetSlots(slots);
            lastSignature = string.Empty;
        }

        /// <summary>라이트/다크 팔레트 — 원본처럼 Thresholds 의 전역 값을 바꾼다.</summary>
        public void ApplyTheme(bool light)
        {
            Thresholds.UseLightPalette = light;
            lastSignature = string.Empty;
        }

        /// <summary>도킹 실패·세로 작업 표시줄 — 화면 모서리에 맨 위로 띄운다(원본 PlaceFloating).</summary>
        public void PlaceFloating(int x, int y, int height)
        {
            if (!IsAlive) return;
            SetWindowPos(Handle, new IntPtr(-1) /* HWND_TOPMOST */, x, y, DesiredWidth, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            lastSignature = string.Empty;
            Paint(new Size(DesiredWidth, height));
        }

        /// <summary>새 값을 그린다. 그릴 글자·색이 같고 크기도 같으면 건너뛴다(원본 UpdateMetrics 의 시그니처 비교).</summary>
        public void Update(MetricsSnapshot next)
        {
            snapshot = next;
            if (!IsAlive || !GetWindowRect(Handle, out RECT r)) return;
            var size = new Size(r.Right - r.Left, r.Bottom - r.Top);
            if (size.Width <= 0 || size.Height <= 0) return;
            string signature = renderer.Signature(next);
            if (signature == lastSignature && size == lastSize) return;
            lastSignature = signature;
            Paint(size);
        }

        /// <summary>크기가 바뀐 뒤(재배치) 같은 값으로 다시 그린다.</summary>
        public void Redraw()
        {
            lastSignature = string.Empty;
            Update(snapshot);
        }

        private void Paint(Size size)
        {
            lastSize = size;
            using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                try
                {
                    renderer.Draw(g, new Rectangle(0, 0, size.Width, size.Height), snapshot);
                }
                catch
                {
                    // 그리기 실패는 다음 틱에 다시(튕김 금지 — 원본 OnPaint 와 같은 규칙)
                }
            }
            Present(bmp);
        }

        /// <summary>GDI+ 비트맵을 미리 곱한 알파(PArgb) DIB 로 옮겨 레이어드 창에 올린다(AiMeter TaskbarStrip.Present 와 같다).</summary>
        private void Present(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr mem = CreateCompatibleDC(screen);
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            IntPtr dib = CreateDIBSection(screen, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
            IntPtr old = SelectObject(mem, dib);
            try
            {
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    var row = new byte[w * 4];
                    for (int y = 0; y < h; y++)
                    {
                        Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                        Marshal.Copy(row, 0, bits + y * w * 4, row.Length);
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
                var size = new SIZE { cx = w, cy = h };
                var src = new POINT();
                var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
                UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
            }
            finally
            {
                SelectObject(mem, old);
                DeleteObject(dib);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        private static void EnsureClass()
        {
            if (procKeepAlive is not null) return;
            procKeepAlive = (h, msg, wParam, lParam) =>
                msg == 0x0021 /* WM_MOUSEACTIVATE */ ? new IntPtr(3) /* MA_NOACTIVATE */ : DefWindowProc(h, msg, wParam, lParam);
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procKeepAlive),
                hInstance = GetModuleHandle(null),
                lpszClassName = ClassName,
            };
            RegisterClassEx(ref wc);
        }

        public void Dispose()
        {
            Destroy();
            renderer.Dispose();
        }

        // ── Win32 ──
        private const long WS_POPUP = 0x80000000L;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, uint style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, IntPtr dstPoint, ref SIZE size, IntPtr srcDc,
            ref POINT srcPoint, int key, ref BLENDFUNCTION blend, int flags);
    }
}
