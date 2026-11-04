using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace DeskCards;

internal static partial class ShellIcons
{
    // 셸 확장이 오래 걸려도 UI와 스레드 풀을 붙잡지 않는다. 취소된 이전 목록의 요청은 건너뛴다.
    private static readonly BlockingCollection<Action> Pending = new();
    private static readonly Lazy<Thread> Worker = new(() =>
    {
        var thread = new Thread(() => { foreach (var action in Pending.GetConsumingEnumerable()) action(); })
            { IsBackground = true, Name = "DeskCards shell icons" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    });

    /// <summary>아이콘이 오기 전에 보여 주는 빈 아이콘(투명). 이름과 자리는 그대로 보이고 아이콘만 나중에 채워진다.</summary>
    public static IconBitmap Placeholder { get; } = new(1, 1, new byte[4]);

    internal static Task<IconBitmap?> GetAsync(string path, CancellationToken token, Func<string, IconBitmap?>? loader = null)
    {
        var result = new TaskCompletionSource<IconBitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Worker.Value;
        Pending.Add(() =>
        {
            try { result.TrySetResult(token.IsCancellationRequested ? null : (loader ?? Get)(path)); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); result.TrySetResult(null); }
        });
        return result.Task;
    }
}
