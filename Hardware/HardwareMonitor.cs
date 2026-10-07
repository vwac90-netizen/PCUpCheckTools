// [Part 273] 하드웨어 수치 지휘 — tools/HwMonitorMini/TrayContext.cs 의 수집·도킹 부분(UpdateMetrics · RedockOrFloat · PlaceFloating ·
//   ApplySlots · SetTaskbar)을 옮겼다. 트레이 메뉴는 팝오버 설정 화면이 대신하므로 여기에는 없다.
// - 1초마다 리더로 스냅샷을 만들고 → 작업 표시줄 줄(켜져 있으면)에 그리고 → Updated 로 팝오버에 알린다.
// - 테마(5틱) · 재도킹(5틱) · 배율 · 세로 작업 표시줄 감시는 원본과 같은 순서·간격.
// - [Part 277] 시계 왼쪽 기준이면 작업 표시줄 앱 버튼 끝을 재서(UIA · 백그라운드) 자리만큼만 — 오른쪽 열부터 빼고, 안 되면 숨김(시안 ③).
// - [Part 275] 온도는 설정에서 켜면 관리자 센서 모드(TemperatureClient → SensorHost)에서 받는다. 꺼져 있으면 HwMonitorMini Lite 와 같은 배치·항목.
using Microsoft.UI.Dispatching;
using PCUpCheckTools.Services;

namespace PCUpCheckTools.Hardware
{
    public sealed class HardwareMonitor : IDisposable
    {
        private const int ThemeCheckTicks = 5;
        private const int RedockRetryTicks = 5;
        private const int FloatingHeight = 40; // 원본 MetricsView.Height(도킹하면 작업 표시줄 높이) — 배율을 곱한다

        private readonly SettingsStore settings;
        private readonly DispatcherQueueTimer timer;
        private readonly ResourceReader resourceReader = new();
        private readonly NetworkSpeedReader networkReader = new();
        private readonly GpuNpuReader gpuNpuReader = new(); // 첫 읽기가 느려 스스로 백그라운드에서 데운다
        private readonly TaskbarDocker docker;
        private readonly MetricsStrip strip = new();
        private readonly TemperatureClient temps = new(); // [Part 275]
        private MetricKind[] slots;
        // [Part 277] 자리 맞추기 — shown = 지금 줄에 그리는 배치(slots 에서 오른쪽 열을 뺀 것일 수 있다)
        private MetricKind[] shown;
        private readonly MetricsRenderer measure = new(96);
        private volatile int appsRight = -1;
        private volatile bool appsBusy;
        private int appsTick;
        private bool stripHidden;
        private bool lightTaskbar;
        private int themeTick;
        private int redockTick;

        /// <summary>[Part 275] 온도를 켰는가(설정). 켜져 있으면 온도 항목을 고를 수 있고 기본 배치가 일반판 배치다</summary>
        public bool Temperatures => settings.TemperatureSensors;

        /// <summary>[Part 275] 센서 모드 상태 — 팝오버 안내용</summary>
        public TemperatureClient.Status TemperatureState => temps.State;

        /// <summary>NPU 가 없는 것이 확실하면 false, 모르면 null(원본 HasNpu)</summary>
        public bool? HasNpu => gpuNpuReader.HasNpu;

        public MetricsSnapshot Latest { get; private set; } = new();
        public MetricKind[] Slots => (MetricKind[])slots.Clone();

        /// <summary>1초마다 새 값 — 팝오버가 열려 있으면 다시 그린다</summary>
        public event Action? Updated;

        public HardwareMonitor(SettingsStore settings, DispatcherQueue dispatcher)
        {
            this.settings = settings;
            docker = new TaskbarDocker(settings);
            slots = MetricCatalog.Parse(settings.Slots, Temperatures);
            shown = slots;
            lightTaskbar = TaskbarTheme.IsLight();
            timer = dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += (_, _) => Tick();
        }

        public void Start()
        {
            // [Part 277] 온도를 켜 둔 채 끝냈으면 작업 스케줄러로 센서 모드를 다시 띄운다 — UAC 없음.
            //   작업이 없으면(exe 를 옮김 등) 시작 때는 묻지 않고 「받지 못함」 으로 둔다 — 설정에서 다시 켜면 등록한다
            if (settings.TemperatureSensors) temps.Start(allowRegister: false);
            if (settings.TaskbarStrip) ShowStrip();
            timer.Start();
            Tick();
        }

        // ── 작업 표시줄 줄 ────────────────────────────────────────

        /// <summary>설정에서 줄을 켜거나 껐다</summary>
        public void SetStripEnabled(bool on)
        {
            if (on) ShowStrip();
            else HideStrip();
        }

        private void ShowStrip()
        {
            strip.Create();
            strip.ApplyTheme(lightTaskbar);
            shown = slots;
            stripHidden = false;
            strip.ApplySlots(shown);
            strip.ApplyDpi(docker.GetTaskbarDpi());
            if (!docker.TryDock(strip.Handle, strip.DesiredWidth)) PlaceFloating();
            strip.Update(Latest);
        }

        private void HideStrip()
        {
            docker.Undock();
            strip.Destroy();
        }

        /// <summary>도킹 실패·세로 작업 표시줄 — 작업 영역 오른쪽 아래 + 오프셋. 자식 스타일이 남지 않게 새 창으로(원본 PlaceFloating)</summary>
        private void PlaceFloating()
        {
            docker.Undock();
            strip.Create();
            strip.ApplyTheme(lightTaskbar);
            int height = (int)Math.Round(FloatingHeight * docker.GetTaskbarDpi() / 96.0);
            var (x, y) = docker.GetFloatingBottomRight(strip.DesiredWidth, height);
            strip.PlaceFloating(x, y, height);
        }

        /// <summary>새 창을 만들어 선택된 작업 표시줄에 붙인다. 실패하면 플로팅(원본 RedockOrFloat — 핸들만 되살리면 안 그려짐, Part 231)</summary>
        private void RedockOrFloat()
        {
            try
            {
                docker.Undock();
                ShowStrip();
            }
            catch
            {
                // 다음 틱 재시도(튕김 금지)
            }
        }

        /// <summary>위치(앵커·오프셋)를 바꿨다 — 다음 배치를 바로 다시 계산</summary>
        public void ApplyPosition()
        {
            if (!settings.TaskbarStrip || !strip.IsAlive) return; // [Part 275] 시작 중(줄을 아직 안 만듦)에는 할 일이 없다
            if (docker.IsDocked)
            {
                docker.InvalidatePosition();
                if (docker.Reposition(strip.DesiredWidth)) strip.Redraw();
            }
            else
            {
                PlaceFloating();
            }
        }

        /// <summary>표시할 작업 표시줄을 바꿨다 — 떼고 새 작업 표시줄 배율로 다시 붙인다(원본 SetTaskbar)</summary>
        public void ApplyTaskbar()
        {
            if (!settings.TaskbarStrip) return;
            docker.InvalidatePosition();
            RedockOrFloat();
        }

        /// <summary>8칸을 바꿨다 — 저장 → 폭 다시 재기 → 다시 배치(원본 ApplySlots)</summary>
        public void ApplySlots(MetricKind[] next)
        {
            slots = (MetricKind[])next.Clone();
            settings.Slots = MetricCatalog.ToNames(slots);
            shown = slots; // 다음 틱에 자리를 다시 맞춘다
            strip.ApplySlots(shown);
            if (!settings.TaskbarStrip) return;
            try
            {
                if (docker.IsDocked)
                {
                    docker.InvalidatePosition();
                    docker.Reposition(strip.DesiredWidth);
                    strip.Redraw();
                }
                else
                {
                    PlaceFloating();
                }
            }
            catch
            {
                // 다음 틱 재시도
            }
        }

        // ── 1초 틱 — 원본 TrayContext.UpdateMetrics ──────────────────

        private void Tick()
        {
            var snapshot = new MetricsSnapshot();
            try { resourceReader.Read(snapshot); } catch { /* 이번 틱 리소스 누락 → "--" */ }
            try { networkReader.Read(snapshot); } catch { /* 이번 틱 네트워크 누락 → "--" */ }
            try { gpuNpuReader.Read(snapshot); } catch { /* 이번 틱 GPU·NPU 누락 → "--" */ }
            if (settings.TemperatureSensors) temps.Fill(snapshot); // [Part 275] 5초 넘게 값이 없으면 채우지 않는다(= "--")
            Latest = snapshot;

            if (settings.TaskbarStrip)
            {
                try
                {
                    UpdateStrip(snapshot);
                }
                catch
                {
                    // UI 갱신 예외도 앱을 죽이지 않음
                }
            }
            Updated?.Invoke();
        }

        private void UpdateStrip(MetricsSnapshot snapshot)
        {
            // 작업 표시줄 테마 변화(설정 앱에서 라이트/다크를 바꾸면 5초 안에 반영)
            if (++themeTick >= ThemeCheckTicks)
            {
                themeTick = 0;
                bool light = TaskbarTheme.IsLight();
                if (light != lightTaskbar)
                {
                    lightTaskbar = light;
                    strip.ApplyTheme(light);
                }
            }

            // 붙어 있던 작업 표시줄이 파괴됐다(탐색기 재시작·보조 모니터 분리) — 새 창으로 다시 붙인다
            if (docker.IsDocked && (!docker.IsParentAlive() || !strip.IsAlive))
            {
                RedockOrFloat();
            }
            // 도킹이 안 된 상태(시작 때 작업 표시줄 없음·탐색기 재시작 중)면 5틱마다 다시 시도
            else if (!docker.IsDocked && ++redockTick >= RedockRetryTicks)
            {
                redockTick = 0;
                if (!docker.IsTaskbarVertical() && docker.ResolveTaskbar() != IntPtr.Zero)
                {
                    RedockOrFloat();
                }
            }

            // 배율·작업 표시줄 방향 변화
            bool resized = strip.ApplyDpi(docker.GetTaskbarDpi());
            if (docker.IsDocked && docker.IsTaskbarVertical())
            {
                PlaceFloating(); // 가로 → 세로로 옮겨졌다: 떼고 화면 모서리에
            }
            else if (resized && !docker.IsDocked)
            {
                PlaceFloating();
            }
            else if (resized)
            {
                docker.InvalidatePosition();
            }

            FitToRoom(); // [Part 277]
            if (stripHidden) return; // 자리가 없어 숨겼다 — 배치·그리기 없음(SWP_SHOWWINDOW 로 다시 보이지 않게)

            if (docker.IsDocked && docker.Reposition(strip.DesiredWidth))
            {
                strip.Redraw(); // 크기가 바뀌었다 — 새 크기로 다시 그림
            }
            strip.Update(snapshot); // 값이 바뀌었을 때만 그린다
        }

        // ── [Part 277] 작업 표시줄 앱 버튼과 겹치지 않게 ─────────────────

        /// <summary>시계 왼쪽 기준 + 도킹 중일 때만. 앱 버튼 끝을 모르면 종전처럼 전부 그린다.</summary>
        private void FitToRoom()
        {
            MetricKind[] target = slots;
            bool hide = false;
            IntPtr bar = docker.ParentTaskbar;
            if (bar != IntPtr.Zero && settings.Anchor == SettingsStore.AnchorTrayLeft)
            {
                if (appsTick++ % 5 == 0 && !appsBusy) // 5초마다 — 창을 열고 닫으면 앱 버튼 수가 바뀐다
                {
                    appsBusy = true;
                    Task.Run(() =>
                    {
                        appsRight = Uia.MaxRightByClassName(bar, "Taskbar.TaskListButtonAutomationPeer") ?? -1; // UI 스레드에서 하지 않는다(Part 273)
                        appsBusy = false;
                    });
                }
                int apps = appsRight;
                if (apps >= 0 && docker.TrayLeftEdge() is int trayLeft)
                {
                    int margin = (int)Math.Round(8 * docker.GetTaskbarDpi() / 96.0);
                    int room = trayLeft - margin - (apps + margin) + settings.OffsetX; // 줄 왼쪽 끝 = trayLeft - 폭 - margin + OffsetX ≥ 앱 버튼 끝 + margin
                    measure.SetDpi(docker.GetTaskbarDpi());
                    var candidates = new List<MetricKind[]> { slots };
                    if (settings.StripOverflow == SettingsStore.OverflowShrink)
                    {
                        var cur = slots;
                        while (true)
                        {
                            int col = Enumerable.Range(0, MetricCatalog.SlotCount / 2).LastOrDefault(c => cur[c * 2] != MetricKind.Empty || cur[c * 2 + 1] != MetricKind.Empty, -1);
                            if (col <= 0) break; // 남은 열이 하나 이하
                            cur = (MetricKind[])cur.Clone();
                            cur[col * 2] = MetricKind.Empty;
                            cur[col * 2 + 1] = MetricKind.Empty;
                            candidates.Add(cur);
                        }
                    }
                    target = candidates.FirstOrDefault(k => { measure.SetSlots(k); return measure.DesiredWidth <= room; }) ?? slots;
                    measure.SetSlots(target);
                    hide = measure.DesiredWidth > room; // 한 열도(숨기기 설정이면 전부가) 안 들어간다
                }
            }
            if (!target.SequenceEqual(shown))
            {
                shown = target;
                strip.ApplySlots(shown);
                docker.InvalidatePosition();
            }
            if (hide != stripHidden)
            {
                stripHidden = hide;
                strip.SetHidden(hide);
                docker.InvalidatePosition();
            }
        }

        // ── [Part 275] 온도 켜고 끄기 ──────────────────────────────

        /// <summary>온도를 켜거나 끈다. 켤 때 UAC 를 거부·실패하면 false(설정은 꺼진 채).</summary>
        public bool SetTemperatures(bool on)
        {
            if (on)
            {
                temps.Start(allowRegister: true); // [Part 277] 작업이 없으면 여기서 한 번 UAC
                if (temps.State is TemperatureClient.Status.Declined or TemperatureClient.Status.Failed) return false;
            }
            else
            {
                temps.Stop();
            }
            SetTemperatureSetting(on);
            return true;
        }

        /// <summary>설정값을 바꾸고 8칸을 맞춘다.
        /// 8칸을 고른 적이 없으면(Slots 없음) 기본 배치만 바꾼다(켬 = 일반판 · 끔 = Lite). 고른 적이 있으면 끌 때 온도 칸만 비우고 나머지는 둔다.</summary>
        private void SetTemperatureSetting(bool on)
        {
            settings.TemperatureSensors = on;
            if (settings.Slots is null)
            {
                slots = MetricCatalog.Default(on);
                strip.ApplySlots(slots);
                ApplyPosition(); // 폭이 바뀌었다
            }
            else if (!on)
            {
                ApplySlots(slots.Select(k => MetricCatalog.IsTemperature(k) ? MetricKind.Empty : k).ToArray());
            }
            settings.Save();
        }

        public void Dispose()
        {
            timer.Stop();
            temps.Dispose();
            HideStrip();
            strip.Dispose();
            measure.Dispose();
            resourceReader.Dispose();
            networkReader.Dispose();
            gpuNpuReader.Dispose();
        }
    }
}
