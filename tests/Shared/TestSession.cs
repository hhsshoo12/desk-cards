using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DeskCards.Testing;

/// <summary>두 회귀 실행기가 공유하는 안내 창. 테스트 UI가 대기 중이어도 별도 STA에서 계속 그린다.</summary>
internal sealed class TestSession : IDisposable
{
    private readonly Thread? _thread;
    private readonly ManualResetEventSlim _ready = new();
    private StatusWindow? _window;
    private Exception? _error;
    private int _completed, _failed;
    private bool _disposed;

    private TestSession(string suite)
    {
        // 화면을 쓰지 않는 진단 실행에만 명시적으로 지정한다. 필터 실행도 기본값은 안내 창 표시다.
        if (Environment.GetEnvironmentVariable("DESKCARDS_TEST_HEADLESS") == "1") return;
        _thread = new Thread(() =>
        {
            try
            {
                // 시작은 항상 최소화하고, 안내 창이 닫히면 같은 STA에서 그 최소화를 되돌린다.
                var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Windows Shell을 찾지 못했어요.");
                dynamic shell = Activator.CreateInstance(type)!;
                try { RunWithDesktop(() => shell.MinimizeAll(), () => ShowStatus(suite), () => shell.UndoMinimizeALL()); }
                finally { Marshal.FinalReleaseComObject(shell); }
            }
            catch (Exception ex)
            {
                _error = ex;
                try { _window?.Finish(); } catch { }
                _ready.Set();
            }
        }) { IsBackground = true, Name = "DeskCards test status" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_error != null) { _thread.Join(); _ready.Dispose(); throw new InvalidOperationException("테스트 안내 창을 띄우지 못했어요.", _error); }
    }

    public static TestSession Start(string suite) => new TestSession(suite);

    internal static void RunWithDesktop(Action minimize, Action run, Action restore)
    {
        minimize();
        try { run(); }
        finally { restore(); }
    }

    private void ShowStatus(string suite)
    {
        try
        {
            Thread.Sleep(350); // 셸 애니메이션 뒤 안내 창을 띄운다.
            _window = new StatusWindow(suite);
            EventHandler? rendered = null;
            rendered = (_, _) => { _window.ContentRendered -= rendered; _ready.Set(); };
            _window.ContentRendered += rendered;
            _window.Show();
            Dispatcher.Run();
        }
        catch
        {
            // 안내 창 생성 실패도 원래 앱 창을 복원하기 전에 정리한다.
            try { _window?.Finish(); } catch { }
            throw;
        }
    }

    public void Complete(bool passed)
    {
        _completed++;
        if (!passed) _failed++;
        var window = _window;
        if (window == null || window.Dispatcher.HasShutdownStarted) return;
        int completed = _completed, failed = _failed;
        window.Dispatcher.BeginInvoke(new Action(() => window.Update(completed, failed)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var window = _window;
        if (window != null && !window.Dispatcher.HasShutdownStarted)
            window.Dispatcher.Invoke(() => { window.Finish(); window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); });
        _thread?.Join();
        _ready.Dispose();
        if (_error != null) throw new InvalidOperationException("테스트 안내 창에서 오류가 발생했어요.", _error);
    }

    private sealed class StatusWindow : Window
    {
        private readonly TextBlock _counts, _elapsed;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly DispatcherTimer _timer;
        private bool _finishing;
        private IntPtr _hwnd;

        public StatusWindow(string suite)
        {
            bool dark;
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
            Brush bg = Brush(dark ? "#292929" : "#FAFAFA"), fg = Brush(dark ? "#FFFFFF" : "#1A1A1A");
            Brush secondary = Brush(dark ? "#C5C5C5" : "#606060"), accent = Brush(dark ? "#60CDFF" : "#0067C0");
            Title = "Desk Cards · 테스트 진행 중";
            Width = 520; SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false; ShowActivated = false; Topmost = true;
            Background = bg; Foreground = fg;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
            UseLayoutRounding = true;
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(0) });

            var content = new StackPanel();
            var body = new StackPanel { Margin = new Thickness(32, 28, 32, 28) };
            var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 22) };
            var tiles = new Grid { Width = 20, Height = 20, Margin = new Thickness(0, 0, 10, 0) };
            string[] colors = { "#4CC2FF", "#FFB900", "#6CCB5F", "#FF6F61" };
            for (int i = 0; i < 4; i++) tiles.Children.Add(new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = Brush(colors[i]),
                HorizontalAlignment = i % 2 == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                VerticalAlignment = i < 2 ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            });
            brand.Children.Add(tiles);
            brand.Children.Add(new TextBlock { Text = "Desk Cards", FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            body.Children.Add(brand);
            body.Children.Add(new TextBlock { Text = "현재 테스트가 진행중입니다", FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = "잠시만 기다려 주세요. 완료되면 이 창이 자동으로 닫힙니다.", FontSize = 14, Foreground = secondary,
                Margin = new Thickness(0, 12, 0, 24), TextWrapping = TextWrapping.Wrap, LineHeight = 21 });
            var track = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Brush(dark ? "#484848" : "#E5E5E5"), ClipToBounds = true };
            var motion = new TranslateTransform(-120, 0);
            var segment = new Border { Width = 120, CornerRadius = new CornerRadius(1.5), Background = accent, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = motion };
            track.Child = segment; body.Children.Add(track); content.Children.Add(body);
            Loaded += (_, _) =>
            {
                Center();
                if (SystemParameters.ClientAreaAnimation)
                    motion.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-120, 456, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever });
                else motion.X = 168;
            };
            var footer = new Grid { Margin = new Thickness(32, 18, 32, 18) };
            footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var details = new StackPanel();
            details.Children.Add(new TextBlock { Text = suite, FontSize = 12, Foreground = secondary });
            _counts = new TextBlock { Text = "검사를 준비하고 있습니다", FontSize = 12, Foreground = secondary, Margin = new Thickness(0, 4, 0, 0) };
            details.Children.Add(_counts); footer.Children.Add(details);
            _elapsed = new TextBlock { Text = "0:00", FontSize = 12, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(_elapsed, 1); footer.Children.Add(_elapsed);
            content.Children.Add(new Border { Background = Brush(dark ? "#202020" : "#F3F3F3"), BorderBrush = Brush(dark ? "#383838" : "#E5E5E5"), BorderThickness = new Thickness(0, 1, 0, 0), Child = footer });
            Content = content;
            SourceInitialized += (_, _) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                int round = 2, mode = dark ? 1 : 0;
                DwmSetWindowAttribute(_hwnd, 33, ref round, 4);
                DwmSetWindowAttribute(_hwnd, 20, ref mode, 4);
                // 안내 창은 포커스를 가져가지 않는다. 일반 사용자 창 최소화 판정에서도 제외한다.
                var source = HwndSource.FromHwnd(_hwnd);
                source?.AddHook(NoActivate);
                int style = GetWindowLong(_hwnd, -20);
                SetWindowLong(_hwnd, -20, style | 0x08000000 | 0x00000080);
            };
            Closing += (_, e) => e.Cancel = !_finishing;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            bool captured = false;
            _timer.Tick += (_, _) =>
            {
                _elapsed.Text = $"{(int)_clock.Elapsed.TotalMinutes}:{_clock.Elapsed.Seconds:00}";
                // 테스트 중 편집 모드의 바탕화면 보기에도 안내는 계속 보인다.
                if (IsIconic(_hwnd)) ShowWindow(_hwnd, 4);
                SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
                if (!captured && Environment.GetEnvironmentVariable("DESKCARDS_TEST_STATUS_SNAPSHOT") is { Length: > 0 } path)
                {
                    Capture(path); captured = true;
                }
            };
            _timer.Start();
        }

        public void Update(int completed, int failed) => _counts.Text = failed == 0
            ? $"완료된 검사 {completed}개" : $"완료된 검사 {completed}개 · 실패 {failed}개";
        public void Finish() { _timer.Stop(); _finishing = true; Close(); }

        private static Brush Brush(string color) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; brush.Freeze(); return brush; }
        private static IntPtr NoActivate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x21) { handled = true; return new IntPtr(3); } // WM_MOUSEACTIVATE / MA_NOACTIVATE
            return IntPtr.Zero;
        }
        private void Center()
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            GetMonitorInfo(MonitorFromWindow(_hwnd, 2), ref info);
            GetWindowRect(_hwnd, out var rect);
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            SetWindowPos(_hwnd, new IntPtr(-1), info.Monitor.Left + (info.Monitor.Right - info.Monitor.Left - width) / 2,
                info.Monitor.Top + (info.Monitor.Bottom - info.Monitor.Top - height) / 2, 0, 0, 0x0011);
        }
        private void Capture(string path)
        {
            // 창 단위 PrintWindow만 쓴다. 사용자 화면 전체는 캡처하지 않는다.
            GetWindowRect(_hwnd, out var rect);
            using var bitmap = new System.Drawing.Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                IntPtr dc = graphics.GetHdc();
                try { if (!PrintWindow(_hwnd, dc, 2)) throw new IOException("테스트 안내 창을 캡처하지 못했어요."); }
                finally { graphics.ReleaseHdc(dc); }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("Test status snapshot: " + path);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
