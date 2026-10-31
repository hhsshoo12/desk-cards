using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeskCards;

/// <summary>
/// 웹 카드 한 장. 웹 화면(자식 창)을 담아야 해서 투명 창이 아닌 Windows 11 둥근 창을 쓴다.
/// 모서리·그림자·아크릴 배경은 Windows가 그리고, 카드 이름 줄은 두지 않는다.
/// 카드는 비율만 정하고 크기는 앱이 정한다. 크기를 바꾸면 페이지를 그림처럼 늘리지 않고 확대 비율로 다시 그려 선명하게 둔다.
/// </summary>
internal sealed class WebCardWindow : DeskCard
{
    private readonly Border _frame;
    private readonly CardWebView _view;
    private CardVisibilityMonitor? _visibility;

    public WebCardWindow(CardRuntime runtime, WebCardInfo info, GroupManager mgr) : base(mgr)
    {
        Runtime = runtime;
        Info = info;

        _view = new CardWebView(runtime, info.Id, settings: false, new Size(info.Width, info.Height))
        {
            MenuRequested = () => Menus.ForWebCard(this, Mgr).ShowAtCursor(),
        };
        _frame = new Border { Child = _view };
        Layout.Children.Add(_frame);
        if (runtime.Package.Url != null)
            Loaded += (_, _) => _visibility ??= new CardVisibilityMonitor(this, runtime.Package.Background, suspend =>
            {
                _view.SetSuspended(suspend);
                if (runtime.Backend is CardPythonProcess python) python.Suspend(suspend);
            });

        // 편집 중(웹 화면이 그림으로 바뀐 동안)의 우클릭도 카드 메뉴.
        PreviewMouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            Menus.ForWebCard(this, Mgr).ShowAtCursor();
        };
    }

    public CardRuntime Runtime { get; }
    public WebCardInfo Info { get; }
    public override string Key => Runtime.KeyFor(Info.Id);
    public override string CardName => Info.Name;
    protected override Border Frame => _frame;
    protected override bool Activatable => true;
    protected override bool FreeResize => true;

    /// <summary>확대 1일 때 카드 크기(DIP): 넓이는 기본 폴더 카드(2×2 칸)와 같고 비율은 카드가 정한다.</summary>
    public static Size BaseSizeFor(WebCardInfo info, double cell) =>
        new Size(info.Width, info.Height);

    protected override Size BaseSize() => BaseSizeFor(Info, Mgr.CellSize);

    protected override void ApplyScale(double t)
    {
        // 웹 화면은 변환으로 키우면 흐려지므로, 창 크기를 따라가게 두고 페이지 확대 비율만 바꾼다.
        Layout.LayoutTransform = Transform.Identity;
        _view.SetZoom(1);
    }

    /// <summary>편집 중에는 창이 누르기·끌기를 받아야 하므로 웹 화면을 그림으로 바꿔 둔다.</summary>
    protected override void OnEditChanged(bool editing) => _view.Freeze(editing);

    /// <summary>페이지를 다시 불러온다(우클릭 메뉴).</summary>
    public void Reload() => Mgr.ReloadWebCardWindow(this);

    internal void ReloadPage() => _view.Reload();

    internal void ShowError(string text) => _view.ShowError(text);

    protected override void OnClosed(EventArgs e)
    {
        _visibility?.Dispose();
        _view.Close();
        Runtime.RemoveWindow(this);
        base.OnClosed(e);
    }
}
