using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

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

    public static ImageSource Placeholder { get; } = CreatePlaceholder();
    private static ImageSource CreatePlaceholder()
    {
        var image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            System.Drawing.SystemIcons.Application.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        image.Freeze();
        return image;
    }

    internal static Task<ImageSource?> GetAsync(string path, CancellationToken token, Func<string, ImageSource?>? loader = null)
    {
        var result = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Worker.Value;
        Pending.Add(() =>
        {
            try { result.TrySetResult(token.IsCancellationRequested ? null : (loader ?? Get)(path)); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); result.TrySetResult(null); }
        });
        return result.Task;
    }
}
