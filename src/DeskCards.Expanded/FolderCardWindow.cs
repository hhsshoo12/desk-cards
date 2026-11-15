using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace DeskCards.Expanded;

internal sealed class FolderCardHandle : DesktopCardHandle<FolderCardWindow>, IFolderCardHandle
{
    public FolderCardHandle(FolderCardOptions options, IFolderCardHost host) => Create(() => new FolderCardWindow(options, host));

    public void SetFolder(FolderCardData data) => Run(() => Window.SetFolder(data));
}

/// <summary>
/// 폴더 카드 한 장(바탕화면). 창 규칙은 웹 카드와 같은 <see cref="DesktopCardWindow"/>이고, 안에 아이콘 칸(BarCardView)을 담는다.
/// 크기는 비율을 지키며 바꾸고(오른쪽 아래 모서리), 파일·폴더를 끌어다 놓으면 앱에 알린다.
/// </summary>
internal sealed class FolderCardWindow : DesktopCardWindow
{
    private readonly IFolderCardHost _host;
    private readonly BarCardView _view;
    private FolderCardData _data;
    private CardLabel? _label;
    private int _baseW;

    public FolderCardWindow(FolderCardOptions options, IFolderCardHost host)
        : base(host, options.Group, "Desk Cards Card", options.Palette, options.X, options.Y, options.Width, options.Height, options.Visible)
    {
        _host = host;
        _data = options.Data;
        _view = new BarCardView(host, ToBar(_data), desktop: true);
        Surface.Children.Add(_view);
        _view.Apply(ToBar(_data), _data.HoverExpand, _data.HoverDelayMs);

        _baseW = Math.Max(1, options.Width);
        IsVisibleChanged += (_, _) => SyncLabel();
        AllowDrop = true;
        DragEnter += OnDrag;
        DragOver += OnDrag;
        DragLeave += (_, _) => _view.SetDrop(false);
        Drop += OnDrop;
    }

    protected override bool LockAspect => true;

    protected override void OnBoundsChanged() => FollowLabel();
    protected override void OnMoved() => FollowLabel();
    protected override void OnClosedCore() { _label?.Dispose(); _label = null; }

    /// <summary>카드 아래 이름 줄(판 없이 글자만, 클릭 통과). 카드가 소유자라 같은 스레드·같은 층에서 따라온다.</summary>
    private void SyncLabel()
    {
        if (Dead) return;
        if (!IsVisible) { _label?.Hide(); return; }
        _label ??= new CardLabel(Handle);
        _label.Text = _data.Name;
        if (!_label.IsShown) _label.Show();
        FollowLabel();
    }

    private void FollowLabel()
    {
        if (_label == null || !IsVisible || Dead) return;
        var c = CurrentContent();
        double t = _data.LabelScale * c.Width / _baseW; // 끄는 도중에도 카드 크기를 따라 글자가 커진다
        double dpi = Scale;
        _label.Place(c.Left, c.Bottom, c.Width, DesktopCards.LabelPixels(t, dpi), t, dpi);
    }

    private static BarCardData ToBar(FolderCardData d) =>
        new(d.Group, d.Name, d.Folder, d.Cols, d.Rows, 1, 0, 0, 0, d.Items);

    internal void SetFolder(FolderCardData data)
    {
        if (Dead) return;
        _data = data;
        _baseW = Math.Max(1, W);
        _view.Apply(ToBar(data), data.HoverExpand, data.HoverDelayMs);
        if (_label != null) _label.Text = data.Name;
        FollowLabel();
    }

    private void OnDrag(object sender, DragEventArgs e)
    {
        // 이 카드의 폴더에서 끌어낸 항목을 이 카드 위로 지나가도 넣을 대상이 아니다(이미 들어 있다).
        bool ok = e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Any(p => !InThisFolder(p));
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        _view.SetDrop(ok);
        e.Handled = true;
    }

    private bool InThisFolder(string path)
    {
        try { return string.Equals(System.IO.Path.GetDirectoryName(path), System.IO.Path.GetFullPath(_data.Folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        _view.SetDrop(false);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Any(p => !InThisFolder(p))) _host.AddToGroup(paths.ToList());
        e.Effects = DragDropEffects.None; // 옮기기는 앱이 직접 하므로 원본 쪽에서 지우지 않게
        e.Handled = true;
    }
}