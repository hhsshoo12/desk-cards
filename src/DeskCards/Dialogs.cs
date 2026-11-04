using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DeskCards;

internal enum MessageBoxButton { OK, OKCancel }

internal enum MessageBoxResult { None, OK, Cancel }

/// <summary>
/// 확인·안내 창(Windows 11 대화 상자 모양). 호출한 쪽이 결과를 바로 받도록 창이 닫힐 때까지 메시지를 돌리며 기다린다.
/// 편집 모드의 어두운 막이나 맨 위 창들 뒤로 숨지 않게 맨 위에 띄운다.
/// </summary>
internal static class Dialogs
{
    private const int DialogWidth = 420;

    /// <param name="heading">굵은 제목. 없으면 <paramref name="text"/>를 제목으로 쓴다.</param>
    /// <param name="primary">파란 버튼 글자(기본 "확인"). 예: "삭제".</param>
    /// <remarks>아이콘은 없다. Windows 11 대화 상자처럼 제목으로 알린다.</remarks>
    public static MessageBoxResult Show(string text, MessageBoxButton buttons = MessageBoxButton.OK,
        string? heading = null, string? primary = null)
    {
        string title = heading ?? text, message = heading == null ? "" : text;
        bool cancel = buttons == MessageBoxButton.OKCancel;
        var result = cancel ? MessageBoxResult.Cancel : MessageBoxResult.OK;
        bool done = false;

        var heading_ = new TextBlock { Text = title, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Padding = new Thickness(24, 22, 24, 24), Spacing = 12, Children = { heading_ } };
        if (message.Length > 0) body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });

        var window = new Window { Title = "Desk Cards" };
        var buttonRow = new Grid { ColumnSpacing = 8, Padding = new Thickness(24) };
        buttonRow.ColumnDefinitions.Add(new ColumnDefinition());
        buttonRow.ColumnDefinitions.Add(new ColumnDefinition());
        buttonRow.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];

        Button Add(string label, MessageBoxResult value, int column, bool accent)
        {
            var b = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch };
            if (accent) b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            b.Click += (_, _) => { result = value; window.Close(); };
            Grid.SetColumn(b, column);
            buttonRow.Children.Add(b);
            return b;
        }
        // Windows 11처럼 버튼은 반씩. 버튼이 하나면 오른쪽 절반에 둔다.
        var ok = Add(primary ?? "확인", MessageBoxResult.OK, 1 - (cancel ? 1 : 0) == 0 ? 0 : 1, accent: true);
        if (cancel) { Grid.SetColumn(ok, 0); Add("취소", MessageBoxResult.Cancel, 1, accent: false); }

        var root = new StackPanel { Width = DialogWidth, Children = { body, buttonRow }, RequestedTheme = Theme.Element };
        root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) window.Close();
        };
        window.Content = root;
        Hwnd.ApplyFluent(window, Hwnd.Backdrop.Mica);

        var aw = window.AppWindow;
        if (aw.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(true, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }
        window.ExtendsContentIntoTitleBar = true;
        aw.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        aw.IsShownInSwitchers = false;

        root.Measure(new Windows.Foundation.Size(DialogWidth, double.PositiveInfinity));
        var hwnd = Hwnd.Of(window);
        double scale = Native.GetDpiForWindow(hwnd) / 96.0;
        int w = (int)Math.Round(DialogWidth * scale), h = (int)Math.Round(root.DesiredSize.Height * scale);
        var area = Screens.Primary.WorkArea;
        aw.MoveAndResize(new Windows.Graphics.RectInt32(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h));

        window.Closed += (_, _) => { done = true; PostMessage(IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero); };
        window.Activate();
        Native.ForceForeground(hwnd);
        ok.Focus(FocusState.Programmatic);

        // 창이 닫힐 때까지 메시지를 돌린다. 호출한 쪽 코드는 결과를 받을 때까지 멈춰 있는다.
        while (!done && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int x, y;
    }

    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
