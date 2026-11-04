using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeskCards;

/// <summary>
/// 우클릭·트레이 메뉴. WinUI의 MenuFlyout(시스템 메뉴 모양)을 쓴다.
/// 바탕화면 카드는 활성화되지 않는 창이라 그 안에서는 메뉴가 안 닫히므로, 보이지 않는 작은 창을 활성화해 그 위에 띄운다.
/// </summary>
internal sealed class FluentMenu
{
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    private static MenuHost? _open;

    /// <summary>떠 있는 메뉴가 있는지.</summary>
    public static bool IsOpen => _open != null;

    private readonly List<MenuFlyoutItemBase> _items = new();

    /// <param name="glyph">Segoe Fluent Icons 글자. image가 있으면 그림을 대신 쓴다.</param>
    public FluentMenu Item(string glyph, string text, Action act, bool enabled = true, WriteableBitmap? image = null, bool danger = false)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        if (image != null) item.Icon = new ImageIcon { Source = image };
        else if (glyph.Length > 0) item.Icon = new FontIcon { Glyph = glyph, FontFamily = new FontFamily(IconFont) };
        if (danger) item.Foreground = Theme.Brush("Danger");
        item.Click += (_, _) => Ui.Post(act);
        _items.Add(item);
        return this;
    }

    public FluentMenu Separator()
    {
        if (_items.Count > 0 && _items[^1] is not MenuFlyoutSeparator) _items.Add(new MenuFlyoutSeparator());
        return this;
    }

    /// <summary>마우스 커서 자리에 띄운다.</summary>
    public void ShowAtCursor()
    {
        if (_items.Count == 0) return;
        Native.GetCursorPos(out var cursor);
        ShowAt(cursor.X, cursor.Y);
    }

    /// <summary>화면 좌표(물리 픽셀)에 띄운다.</summary>
    public void ShowAt(int x, int y)
    {
        while (_items.Count > 0 && _items[^1] is MenuFlyoutSeparator) _items.RemoveAt(_items.Count - 1);
        if (_items.Count == 0) return;
        _open?.Close();
        var flyout = new MenuFlyout { ShouldConstrainToRootBounds = false };
        foreach (var i in _items) flyout.Items.Add(i);
        _open = new MenuHost(flyout, x, y, () => _open = null);
    }

    /// <summary>메뉴를 띄우는 동안만 있는 투명한 1×1 창.</summary>
    private sealed class MenuHost
    {
        private readonly Window _window;
        private readonly Action _onClosed;
        private bool _closed;

        public MenuHost(MenuFlyout flyout, int x, int y, Action onClosed)
        {
            _onClosed = onClosed;
            var root = new Grid { Width = 1, Height = 1, Background = null, RequestedTheme = Theme.Element };
            _window = new Window { Content = root, Title = "Desk Cards Menu" };
            var aw = _window.AppWindow;
            if (aw.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
            {
                p.SetBorderAndTitleBar(false, false);
                p.IsResizable = false;
            }
            aw.IsShownInSwitchers = false;
            aw.MoveAndResize(new Windows.Graphics.RectInt32(x, y, 1, 1));
            var hwnd = Hwnd.Of(_window);
            Hwnd.MakeTool(hwnd, Native.WS_EX_LAYERED);
            // 완전히 투명하게(보이는 건 메뉴뿐).
            SetLayeredWindowAttributes(hwnd, 0, 0, 2);
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 1, 1, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            _window.Activate();
            Native.ForceForeground(hwnd);
            _window.Closed += (_, _) => Finish();
            _window.Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) Close(); };
            flyout.Closed += (_, _) => Close();
            root.Loaded += (_, _) =>
            {
                double scale = root.XamlRoot.RasterizationScale;
                flyout.ShowAt(root, new FlyoutShowOptions { Position = new Windows.Foundation.Point(0, 0), Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
            };
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            Finish();
            try { _window.Close(); } catch { }
        }

        private void Finish()
        {
            _closed = true;
            _onClosed();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    }
}
