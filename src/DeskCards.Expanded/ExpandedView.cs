using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Threading;

namespace DeskCards.Expanded;

/// <summary>
/// 펼친 창 하나. WPF 창은 앱 UI 스레드와 따로 만든 STA 스레드에서 돌고, 이 클래스의 메서드는 어느 스레드에서 불러도 된다.
/// </summary>
public sealed class ExpandedView
{
    private static Dispatcher? _dispatcher;
    private static readonly object Gate = new();
    private TileWindow? _window;

    private static Dispatcher Dispatcher
    {
        get
        {
            lock (Gate)
            {
                if (_dispatcher != null) return _dispatcher;
                using var ready = new ManualResetEventSlim();
                var thread = new Thread(() =>
                {
                    var d = Dispatcher.CurrentDispatcher;
                    d.UnhandledException += (_, e) => e.Handled = true; // 창 하나의 오류로 앱 전체가 죽지 않게
                    _dispatcher = d;
                    ready.Set();
                    Dispatcher.Run();
                }) { IsBackground = true, Name = "DeskCards.Expanded" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                ready.Wait();
                return _dispatcher!;
            }
        }
    }

    /// <summary>WPF 스레드와 라이브러리를 미리 올려 두어 첫 펼침이 늦지 않게 한다.</summary>
    public static void Warmup() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        try { new TileWindow.Probe(); } catch { }
    });

    public static ExpandedView Open(IExpandedHost host, ExpandedOptions options, string title, string folder, IReadOnlyList<TileData> items)
    {
        var view = new ExpandedView();
        Dispatcher.BeginInvoke(() =>
        {
            var w = new TileWindow(host, options, title, folder, items);
            view._window = w;
            w.Show();
            w.Activate();
        });
        return view;
    }

    public void SetContent(string title, string folder, IReadOnlyList<TileData> items) =>
        Dispatcher.BeginInvoke(() => _window?.SetContent(title, folder, items));

    /// <summary>ItemMenu로 연 메뉴가 닫혔다.</summary>
    public void MenuClosed() => Dispatcher.BeginInvoke(() => _window?.MenuClosed());

    public void Close() => Dispatcher.BeginInvoke(() => _window?.SafeClose());
}
