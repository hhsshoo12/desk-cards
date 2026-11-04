using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace DeskCards;

/// <summary>
/// 바탕화면에 붙어 있는 폴더(그룹) 카드 하나(2×2 미리보기).
/// 옮기기·크기 조절·편집 모드는 DeskCard가 하고, 여기서는 아이콘 칸과 파일 끌어 넣기를 한다.
/// </summary>
internal sealed class CardWindow : DeskCard
{
    public CardWindow(GroupModel group, GroupManager mgr) : base(mgr)
    {
        Group = group;
        View = new CardView(group, mgr, onDesktop: true)
        {
            Expand = hover =>
            {
                if (hover && ExpandedWindow.IsOpenFor(this)) return;
                ExpandedWindow.Open(this, Mgr, hover: hover);
            },
            CardMenu = () => Menus.ForCard(this, Mgr),
            Editing = () => Editing,
        };
        Layout.Children.Add(View);
        UpdateBorder();
        Group.Changed += RefreshLabel;

        var root = (UIElement)Content;
        root.AllowDrop = true;
        root.DragEnter += OnDragOver;
        root.DragOver += OnDragOver;
        root.DragLeave += (_, _) => UpdateBorder(false);
        root.Drop += OnDrop;
        // 아이콘 칸 밖(이름 줄·가장자리)을 우클릭해도 카드 메뉴.
        root.RightTapped += (_, e) => { e.Handled = true; Menus.ForCard(this, Mgr).ShowAtCursor(); };
        Init();
    }

    public GroupModel Group { get; }
    public CardView View { get; }
    public override string Key => Group.Name;
    public override string CardName => Group.Name;
    protected override string? LabelText => Group.Name;
    protected override Border? Frame => View?.Card;

    /// <summary>
    /// 미리보기 칸 하나의 기준 크기(DIP)를 바탕화면 아이콘 간격으로 잰다. 기본 2×2 카드의 가로가 아이콘 2칸이 된다.
    /// 처음 한 번만 재서 설정에 고정한다(배율을 바꾼 직후에는 아이콘 간격이 늦게 바뀌어 값이 흔들린다).
    /// </summary>
    public static double MeasureCellSize(double dpiScale)
    {
        double w = 152;
        if (DesktopGrid.TryGetIconSpacing(out int cx)) w = DesktopGrid.CardCols * cx / dpiScale;
        return w / 2;
    }

    /// <summary>확대 비율을 적용하기 전의 카드 창 크기. 칸 수가 비율을 정한다. 이름은 창 밖 아래에 따로 띄운다.</summary>
    public static Size BaseSize(CardLayout layout, double cell) =>
        new(layout.Cols * cell, layout.Rows * cell);

    protected override Size BaseSize() => BaseSize(_layout, Mgr.CellSize);

    protected override void ApplyScale(double t)
    {
        base.ApplyScale(t);
        // 칸 크기에 확대 비율을 곱해 아이콘·여백까지 함께 키운다(기본 72 → 40).
        View.Apply(_layout, Mgr.CellSize * t);
    }

    /// <summary>미리보기 칸 수를 바꾼다(카드 비율이 따라 바뀐다).</summary>
    public void SetGrid(int cols, int rows)
    {
        _layout.Cols = Math.Clamp(cols, CardLayout.MinCells, CardLayout.MaxCells);
        _layout.Rows = Math.Clamp(rows, CardLayout.MinCells, CardLayout.MaxCells);
        CommitLayout();
    }

    // ----- 드롭 -----

    private void OnDragOver(object sender, DragEventArgs e)
    {
        // 끌어온 것이 파일·폴더인지만 먼저 보고(목록을 읽는 건 비동기), 이 폴더에 넣을 수 있는지는 놓을 때 거른다.
        bool ok = e.DataView.Contains(StandardDataFormats.StorageItems);
        e.AcceptedOperation = ok ? DataPackageOperation.Move : DataPackageOperation.None;
        UpdateBorder(ok);
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        UpdateBorder(false);
        var deferral = e.GetDeferral();
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var paths = new System.Collections.Generic.List<string>();
                foreach (var item in items)
                    if (!string.IsNullOrEmpty(item.Path)) paths.Add(item.Path);
                FileOps.AddToGroup(paths, Group.Folder, Mgr.Root);
            }
            // 이동은 우리가 직접 했으므로 원본 쪽에서 삭제하지 않도록 None을 돌려준다.
            e.AcceptedOperation = DataPackageOperation.None;
        }
        finally { deferral.Complete(); }
        e.Handled = true;
    }

    protected override void OnClosed()
    {
        Group.Changed -= RefreshLabel;
        View.Detach();
        base.OnClosed();
    }
}
