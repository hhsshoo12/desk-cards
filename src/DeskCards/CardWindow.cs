using System;
using System.Windows;
using System.Windows.Controls;

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
        AllowDrop = true;
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
        Group.Changed += RefreshLabel;

        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => UpdateBorder(false);
        Drop += OnDrop;
        // 아이콘 칸 밖(이름 줄·가장자리)을 우클릭해도 카드 메뉴.
        MouseRightButtonUp += (_, e) => { e.Handled = true; Menus.ForCard(this, Mgr).ShowAtCursor(); };
    }

    public GroupModel Group { get; }
    public CardView View { get; }
    public override string Key => Group.Name;
    public override string CardName => Group.Name;
    protected override string? LabelText => Group.Name;
    protected override Border Frame => View.Card;

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
        // 기준 단위에서 칸 크기는 항상 같으므로 아이콘도 같다(기본 72 → 40).
        View.Apply(_layout, Mgr.CellSize);
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
        bool ok = FileOps.CanAccept(e.Data, Group.Folder);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        UpdateBorder(ok);
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        UpdateBorder(false);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            FileOps.AddToGroup(paths, Group.Folder, Mgr.Root);
        // 이동은 우리가 직접 했으므로 원본 쪽에서 삭제하지 않도록 None을 돌려준다.
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        Group.Changed -= RefreshLabel;
        View.Detach();
        base.OnClosed(e);
    }
}
