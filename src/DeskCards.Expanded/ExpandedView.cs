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
    private static Dispatcher Dispatcher => WpfThread.Dispatcher;
    private TileWindow? _window;

    /// <summary>WPF 스레드와 라이브러리를 미리 올려 두어 첫 펼침이 늦지 않게 한다.</summary>
    public static void Warmup() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        // 진짜 펼친 창을 화면 밖에서 한 번 그려 본다(WPF 렌더러·글꼴·아크릴 첫 준비가 첫 펼침에 비치지 않게).
        try
        {
            var w = new TileWindow(new WarmupHost(), new ExpandedOptions(new Palette("#FFFFFFFF", "#FFC5C5C5", "#18FFFFFF", "#B82C2C2C", false)),
                "Desk Cards", "", new[] { new TileData("warmup.txt", "warmup", 0, 0, null) });
            w.ShowActivated = false;
            w.Show();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            timer.Tick += (_, _) => { timer.Stop(); w.SafeClose(); };
            timer.Start();
        }
        catch { }
    });

    private sealed class WarmupHost : IExpandedHost
    {
        public (int X, int Y) Place(int widthPx, int heightPx) => (-32000, -32000);
        public void Launch(string path) { }
        public void ItemMenu(string path) { }
        public void AddToGroup(IReadOnlyList<string> paths) { }
        public void SetOrder(IReadOnlyList<string> paths) { }
        public bool Rename(string name) => false;
        public void OpenSettings() { }
        public void Closed() { }
    }

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
