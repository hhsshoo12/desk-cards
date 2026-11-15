using System.Threading;
using System.Windows.Threading;

namespace DeskCards.Expanded;

/// <summary>펼친 창·카드 바·게이지가 같이 쓰는 WPF 창 스레드(STA).</summary>
internal static class WpfThread
{
    private static Dispatcher? _dispatcher;
    private static readonly object Gate = new();

    public static Dispatcher Dispatcher
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
}
