using System;
using System.Windows.Threading;

namespace DeskCards.Expanded;

/// <summary>바탕화면 카드 창을 앱 스레드에서 다루는 공통 핸들. 창 조작은 모두 WPF 창 스레드로 넘긴다.</summary>
internal abstract class DesktopCardHandle<T> : IDesktopCardHandle where T : DesktopCardWindow
{
    protected T Window { get; private set; } = null!;
    private IntPtr _hwnd;

    protected void Create(Func<T> make)
    {
        WpfThread.Dispatcher.Invoke(() =>
        {
            Window = make();
            Window.Prepare();
            _hwnd = Window.Handle;
        });
    }

    protected void Run(Action action) => WpfThread.Dispatcher.BeginInvoke(action);

    public IntPtr Hwnd => _hwnd;
    public void SetBounds(int x, int y, int width, int height) => Run(() => Window.SetBounds(x, y, width, height));
    public void SetBoundsNow(int x, int y, int width, int height) => WpfThread.Dispatcher.Invoke(() => Window.SetBounds(x, y, width, height));
    public void SetEditing(bool editing) => Run(() => Window.SetEditing(editing));
    public void SetHighlight(bool selected, bool invalid) => Run(() => Window.SetHighlight(selected, invalid));
    public void SetVisible(bool visible) => Run(() => Window.SetVisible(visible));
    public void Restack() => Run(Window.Restack);
    public void SetPalette(Palette palette) => Run(() => Window.SetPalette(palette));
    public void Close() => Run(Window.Close);
}