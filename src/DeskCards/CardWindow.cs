using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskCards;

/// <summary>
/// 바탕화면에 붙어 있는 폴더(그룹) 카드 하나. Windows 11이 모서리·그림자·아크릴을 그려 주는 보통 창이고, 눌러도 활성화되지 않는다.
/// 평소에는 카드 안의 아이콘 클릭·끌어 꺼내기·파일 끌어 넣기만 되고, 옮기기·크기 조절은 편집 모드에서만 된다.
///
/// 이 클래스는 창의 뼈대와 공개 동작만 가진다. 나머지는 부분 파일로 나눴다.
///   CardWindow.Chrome.cs   바탕화면 층·z 순서·창 메시지
///   CardWindow.Geometry.cs 크기·배율·위치·이름 줄
///   CardWindow.Editing.cs  편집 상태·강조 테두리
///   CardWindow.Drop.cs     파일 끌어 넣기
/// 편집 모드의 끌기·크기 조절은 CardEditor가 한다.
/// </summary>
internal sealed partial class CardWindow : Window
{
    public const double LabelH = CardView.LabelH;

    private readonly CardEditor _editor;
    private readonly Grid _root;
    private bool _closed;

    public CardWindow(GroupModel group, GroupManager mgr)
    {
        Mgr = mgr;
        Group = group;
        Title = "Desk Cards Card";
        ConfigureChrome();

        View = new CardView(group, mgr, onDesktop: true)
        {
            Expand = hover =>
            {
                if (hover && ExpandedWindow.IsOpenFor(this)) return;
                ExpandedWindow.Open(this, Mgr, hover: hover);
            },
            CardMenu = () => Menus.ForCard(this, Mgr),
            Editing = () => IsEditing,
        };
        _root = new Grid { Background = Theme.Brush("PopupBg") };
        _root.Children.Add(View);
        Content = _root;
        _editor = new CardEditor(this, _root);
        EnableDrop(_root);
        // 아이콘 칸 밖(이름 줄·가장자리)을 우클릭해도 카드 메뉴.
        _root.RightTapped += (_, e) => { e.Handled = true; Menus.ForCard(this, Mgr).ShowAtCursor(); };

        var hwnd = Handle;
        Hwnd.MakeNoActivateTool(hwnd);
        Hwnd.ApplyFluent(this, Hwnd.Backdrop.Acrylic);
        AttachToDesktop(hwnd);
        InstallMessageHook(hwnd);
        Theme.Changed += OnThemeChanged;
        Group.Changed += RefreshLabel;
        AppWindow.Changed += (_, e) => { if (e.DidVisibilityChange) SyncLabel(); };
        Closed += (_, _) => OnClosed();

        UpdateBorder();
        ApplySize();
    }

    public GroupManager Mgr { get; }
    public GroupModel Group { get; }
    public CardView View { get; }

    /// <summary>위치·모양을 저장하는 이름(config.json의 PositionsPx·Layouts 키).</summary>
    public string Key => Group.Name;

    /// <summary>편집 막대 등에 보여 주는 카드 이름.</summary>
    public string CardName => Group.Name;

    /// <summary>앱이 카드를 닫는다. 쓰는 쪽이 ClosingByManager를 먼저 켜 둔다.</summary>
    public bool ClosingByManager { get; set; }

    /// <summary>카드 창이 닫혔을 때(앱이 직접 닫는 경우 포함). 쓰는 쪽은 ClosingByManager로 구분한다.</summary>
    public event EventHandler? CardClosed;

    public bool IsVisible => Native.IsWindowVisible(Handle);

    /// <summary>활성화하지 않고 바탕화면 층에 보인다.</summary>
    public void Show()
    {
        if (_closed) return;
        AppWindow.Show(false);
        SendToBottom();
    }

    private IntPtr Handle => Hwnd.Of(this);

    private void OnClosed()
    {
        _closed = true;
        Theme.Changed -= OnThemeChanged;
        Group.Changed -= RefreshLabel;
        _label?.Dispose();
        _hook?.Dispose();
        _hook = null;
        View.Detach();
        CardClosed?.Invoke(this, EventArgs.Empty);
    }

    private void OnThemeChanged() => Ui.Post(() =>
    {
        if (_closed || Handle == IntPtr.Zero) return;
        Hwnd.ApplyFluent(this, Hwnd.Backdrop.Acrylic);
        UpdateBorder();
    }, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);

    /// <summary>제목 줄 없이 Windows가 그리는 1px 테두리·둥근 모서리·그림자만 남긴다.</summary>
    private void ConfigureChrome()
    {
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(true, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }
        AppWindow.IsShownInSwitchers = false;
    }
}
