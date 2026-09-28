using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using DeskCards;

internal static partial class Program
{
    private static void DwmCardTests(string root)
    {
        Test("폴더 카드 기준 크기는 여백 없이 아이콘 칸과 안쪽 이름 줄을 합한다", () =>
        {
            foreach (int cols in new[] { 1, 2, 4, 8 })
                foreach (int rows in new[] { 1, 2, 4, 8 })
                {
                    var size = CardWindow.BaseSize(new CardLayout { Cols = cols, Rows = rows }, 72);
                    Check(size == new Size(cols * 72, rows * 72 + CardView.LabelH));
                }
            double dpi = Native.GetDpiForSystem() / 96.0;
            double expected = DesktopGrid.TryGetIconSpacing(out int spacing) ? DesktopGrid.CardCols * spacing / dpi / 2 : 76;
            Check(Math.Abs(CardWindow.MeasureCellSize(dpi) - expected) < 0.001);
        });

        var cfg = Config.Load(Path.Combine(root, "dwm.json"));
        cfg.CellSize = 72;
        cfg.ScaleVersion = 2;
        cfg.FollowWindowsScale = true;
        cfg.DefaultZoom = 1;
        var mgr = new GroupManager(Path.Combine(root, "dwm-groups"), cfg);
        try
        {
            mgr.Start();
            var card = mgr.Cards.Single();
            Pump(100);
            Test("폴더 카드는 비활성 DWM 아크릴 창이며 저장된 칸 크기를 보존한다", () =>
            {
                CheckDwmCard(card, activatable: false);
                Check(cfg.CellSize == 72 && Config.Load(Path.Combine(root, "dwm.json")).CellSize == 72);
                Check(Math.Abs(card.Width - 144) < 0.01 && Math.Abs(card.Height - 172) < 0.01);
                // 유리 틀로 바뀌어도 비클라이언트 여백이 아이콘·이름을 밀어내면 안 된다.
                var content = (FrameworkElement)card.Content;
                Check(Math.Abs(content.ActualWidth - card.Width) < 1 && Math.Abs(content.ActualHeight - card.Height) < 1);
                var frame = card.View.Card;
                var label = card.View.Children.OfType<TextBlock>().Single(t => Grid.GetRow(t) == 1);
                var cells = card.View.Children.OfType<UniformGrid>().Single();
                Check(frame.Background == null && frame.Effect == null && frame.BorderThickness == new Thickness(0));
                Check(Grid.GetRowSpan(frame) == 2 && Math.Abs(frame.ActualHeight - card.View.ActualHeight) < 1);
                Check(label.Effect == null && label.Foreground == Application.Current.Resources["Fg"]);
                double labelTop = label.TranslatePoint(new Point(), card.View).Y;
                double cellsBottom = cells.TranslatePoint(new Point(0, cells.ActualHeight), card.View).Y;
                Check(labelTop >= 144 && labelTop >= cellsBottom && labelTop + label.ActualHeight <= card.View.ActualHeight);
            });
            Test("카드 바는 판·그림자와 판 아래 이름 모양을 유지한다", () =>
            {
                var view = new CardView(card.Group, mgr, onDesktop: false);
                try
                {
                    view.Apply(new CardLayout(), 72);
                    view.Measure(new Size(144, 172));
                    view.Arrange(new Rect(0, 0, 144, 172));
                    var label = view.Children.OfType<TextBlock>().Single(t => Grid.GetRow(t) == 1);
                    Check(view.Card.Background == Application.Current.Resources["CardBg"] && view.Card.BorderThickness == new Thickness(1));
                    Check(view.Card.Effect is DropShadowEffect { BlurRadius: 10, ShadowDepth: 2, Opacity: 0.22 });
                    Check(Grid.GetRowSpan(view.Card) == 1 && Math.Abs(view.Card.ActualHeight - 144) < 1);
                    Check(label.Foreground == Application.Current.Resources["Fg"] && label.Effect == null);
                }
                finally { view.Detach(); }
            });
            Test("편집 선택·화살표 이동·비율 확대 후 테두리와 바탕화면 층을 복원한다", () =>
            {
                card.BeginEdit();
                card.SetSelected(true);
                Check(card.View.Card.BorderThickness == new Thickness(2));
                CheckInnerBorder(card, strong: true);
                var grip = Visuals<Thumb>(card).Single();
                card.UpdateLayout();
                Check(grip.IsVisible && Visuals<Border>(grip).Single().Background is SolidColorBrush { Color.A: 0 });
                Native.GetWindowRect(Hwnd.Of(card), out var before);
                card.Nudge(-1, 1);
                Native.GetWindowRect(Hwnd.Of(card), out var after);
                Check(after.Left == before.Left - 1 && after.Top == before.Top + 1);
                double ratio = card.Width / card.Height;
                card.SetSizePercent(125);
                Check(Math.Abs(card.Width / card.Height - ratio) < 0.001);
                card.SetSizePercent(100);
                card.EndEdit();
                Check(card.View.Card.BorderThickness == new Thickness(0) && !grip.IsVisible);
                Check((Native.GetWindowLongPtr(Hwnd.Of(card), Native.GWL_EXSTYLE).ToInt64() & 8) == 0); // WS_EX_TOPMOST
                CheckInnerBorder(card, strong: false);
            });
            Test("테마 알림이 DWM 다크 모드·아크릴·선택 테두리를 다시 적용한다", () =>
            {
                card.BeginEdit();
                card.SetSelected(true);
                Native.SetDwm(Hwnd.Of(card), Native.DWMWA_USE_IMMERSIVE_DARK_MODE, Theme.IsLight ? 1 : 0);
                Native.SetDwm(Hwnd.Of(card), Native.DWMWA_SYSTEMBACKDROP_TYPE, 1);
                Native.SetDwm(Hwnd.Of(card), Native.DWMWA_BORDER_COLOR, 0);
                Theme.Apply();
                Pump(100);
                CheckDwmCard(card, activatable: false);
                CheckInnerBorder(card, strong: true);
                Check(card.View.Card.BorderThickness == new Thickness(2));
                card.EndEdit();
            });
            Test("드롭 대상 강조는 테마·선택 변경에도 유지되고 드래그 종료 때 해제된다", () =>
            {
                var update = typeof(DeskCard).GetMethod("UpdateBorder", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                try
                {
                    update.Invoke(card, new object[] { true });
                    CheckInnerBorder(card, strong: true);
                    Theme.Apply();
                    Pump(100);
                    CheckInnerBorder(card, strong: true);
                    card.BeginEdit();
                    card.SetSelected(false);
                    CheckInnerBorder(card, strong: true);
                }
                finally
                {
                    update.Invoke(card, new object[] { false });
                    card.EndEdit();
                }
                CheckInnerBorder(card, strong: false);
            });
            if (Environment.GetEnvironmentVariable("DESKCARDS_SNAPSHOT_DIR") is { Length: > 0 } dir)
            {
                PrintCard(card, Path.Combine(dir, "dwm-folder.png"));
                card.BeginEdit();
                card.SetSelected(true);
                Pump(100);
                PrintCard(card, Path.Combine(dir, "dwm-folder-selected.png"));
                card.EndEdit();
            }
        }
        finally { mgr.Shutdown(); }
    }

    private static void CheckDwmCard(DeskCard card, bool activatable)
    {
        var hwnd = Hwnd.Of(card);
        long style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        Check(!card.AllowsTransparency && (style & Native.WS_EX_LAYERED) == 0);
        Check(((style & Native.WS_EX_NOACTIVATE) == 0) == activatable);
        Check((style & Native.WS_EX_TOOLWINDOW) != 0);
        Check(Native.GetWindowLongPtr(hwnd, Native.GWLP_HWNDPARENT) == Native.FindWindow("Progman", null));
        var chrome = WindowChrome.GetWindowChrome(card);
        Check(chrome.CaptionHeight == 0 && chrome.GlassFrameThickness == new Thickness(-1) && chrome.ResizeBorderThickness == new Thickness(0));
        Check(((Grid)card.Content).Background == Application.Current.Resources["PopupBg"]);
        if (Theme.HasBackdrop)
        {
            Check(DwmValue(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE) == 3);
            Check(DwmValue(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE) == (Theme.IsLight ? 0 : 1));
            Check(DwmValue(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE) == 2);
        }
    }

    private static void CheckDardEditing(DardWindow card)
    {
        var view = Visuals<DardView>(card).Single();
        var web = view.Children.OfType<Microsoft.Web.WebView2.Wpf.WebView2>().Single();
        var snapshot = view.Children.OfType<Image>().Single();
        WaitUntil(() => web.CoreWebView2 != null && web.Source?.Scheme == "https");
        // 캡처는 문서가 실제로 표시된 뒤 가능하다.
        var loaded = web.ExecuteScriptAsync("document.readyState");
        WaitUntil(() => loaded.IsCompleted);
        Check(loaded.GetAwaiter().GetResult() is "\"complete\"" or "\"interactive\"");
        double zoom = web.ZoomFactor;
        try
        {
            // 콘솔 본문에는 WPF 동기화 컨텍스트가 없다. 실제 메뉴처럼 Dispatcher에서 캡처를 시작한다.
            var edit = card.Dispatcher.InvokeAsync(() =>
            {
                card.BeginEdit();
                card.SetSelected(true);
            });
            WaitUntil(() => edit.Task.IsCompleted);
            edit.Task.GetAwaiter().GetResult();
            WaitUntil(() => snapshot.Source != null && web.Visibility == Visibility.Hidden);
            Check(snapshot.Visibility == Visibility.Visible);
            Check(Math.Abs(web.ZoomFactor - zoom) < 0.001);
            if (Environment.GetEnvironmentVariable("DESKCARDS_SNAPSHOT_DIR") is { Length: > 0 } dir)
                PrintCard(card, Path.Combine(dir, "dwm-dard-selected.png"));
        }
        finally { card.EndEdit(); }
        Check(snapshot.Visibility == Visibility.Collapsed && snapshot.Source == null && web.Visibility == Visibility.Visible);
        Check(Math.Abs(web.ZoomFactor - zoom) < 0.001);
        Theme.Apply();
        Pump(100);
        CheckDwmCard(card, activatable: true);
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && elapsed.ElapsedMilliseconds < 10000) Pump(50);
        Check(condition());
    }

    private static void CheckInnerBorder(CardWindow card, bool strong)
    {
        // DWMWA_BORDER_COLOR는 설정용 속성이라 이 OS에서 조회하면 E_INVALIDARG다.
        // 네이티브 선은 PrintWindow로 확인하고, 안쪽 선의 색과 두께는 직접 검사한다.
        Check(card.View.Card.BorderBrush == Application.Current.Resources["Accent"]);
        Check(card.View.Card.BorderThickness == new Thickness(strong ? 2 : 0));
    }

    private static int DwmValue(IntPtr hwnd, int attr)
    {
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(hwnd, attr, out int value, sizeof(int)));
        return value;
    }

    // 일반 화면 캡처에 빠지는 바탕화면 층도 물리 픽셀 크기로 그린다. 테스트 매니페스트는 PerMonitorV2다.
    private static void PrintCard(DeskCard card, string path)
    {
        card.UpdateLayout();
        Pump(200); // 편집 상태가 바뀐 뒤 합성된 새 프레임을 기다린다.
        var hwnd = Hwnd.Of(card);
        Native.GetWindowRect(hwnd, out var rect);
        using var bitmap = new System.Drawing.Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            try { Check(PrintWindow(hwnd, dc, 2)); }
            finally { graphics.ReleaseHdc(dc); }
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
