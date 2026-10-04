using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeskCards;

/// <summary>
/// .dard 카드 한 장. 웹 화면(자식 창)을 담아야 해서 투명 창이 아닌 Windows 11 둥근 창을 쓴다.
/// 모서리·그림자·아크릴 배경은 Windows가 그리고, 카드 이름 줄은 두지 않는다.
/// 카드는 비율만 정하고 크기는 앱이 정한다. 크기를 바꾸면 페이지를 그림처럼 늘리지 않고 확대 비율로 다시 그려 선명하게 둔다.
/// </summary>
internal sealed class DardWindow : DeskCard
{
    private readonly Border _frame;
    private readonly DardView _view;

    public DardWindow(DardRuntime runtime, DardCardInfo info, GroupManager mgr) : base(mgr)
    {
        Runtime = runtime;
        Info = info;

        _view = new DardView(runtime, info.Id, settings: false, DardPackage.SizeFor(info.RatioW, info.RatioH, DardPackage.CardArea))
        {
            MenuRequested = () => Menus.ForDard(this, Mgr).ShowAtCursor(),
        };
        _frame = new Border { Child = _view };
        Layout.Children.Add(_frame);

        // 편집 중(웹 화면이 그림으로 바뀐 동안)의 우클릭도 카드 메뉴.
        PreviewMouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            Menus.ForDard(this, Mgr).ShowAtCursor();
        };
    }

    public DardRuntime Runtime { get; }
    public DardCardInfo Info { get; }
    public override string Key => Runtime.KeyFor(Info.Id);
    public override string CardName => Info.Name;
    protected override Border Frame => _frame;
    protected override bool Activatable => true;

    /// <summary>확대 1일 때 카드 크기(DIP): 넓이는 기본 폴더 카드(2×2 칸)와 같고 비율은 카드가 정한다.</summary>
    public static Size BaseSizeFor(DardCardInfo info, double cell) =>
        DardPackage.SizeFor(info.RatioW, info.RatioH, 4 * cell * cell);

    protected override Size BaseSize() => BaseSizeFor(Info, Mgr.CellSize);

    protected override void ApplyScale(double t)
    {
        // 웹 화면은 변환으로 키우면 흐려지므로, 창 크기를 따라가게 두고 페이지 확대 비율만 바꾼다.
        Layout.LayoutTransform = Transform.Identity;
        _view.SetZoom(BaseSize().Width * t / _view.Viewport.Width);
    }

    /// <summary>편집 중에는 창이 누르기·끌기를 받아야 하므로 웹 화면을 그림으로 바꿔 둔다.</summary>
    protected override void OnEditChanged(bool editing) => _view.Freeze(editing);

    /// <summary>페이지를 다시 불러온다(우클릭 메뉴).</summary>
    public void Reload() => Mgr.ReloadDardWindow(this);

    internal void ReloadPage() => _view.Reload();

    internal void ShowError(string text) => _view.ShowError(text);

    protected override void OnClosed(EventArgs e)
    {
        _view.Close();
        Runtime.RemoveWindow(this);
        base.OnClosed(e);
    }
}
