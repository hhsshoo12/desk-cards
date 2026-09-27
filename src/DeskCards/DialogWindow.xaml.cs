using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DeskCards;

/// <summary>Windows 11 ContentDialog 모양의 확인·안내 창. 직접 쓰지 말고 <see cref="Dialogs.Show"/>로 띄운다.</summary>
internal partial class DialogWindow : Window
{
    private MessageBoxResult _result;

    public DialogWindow(string heading, string message, MessageBoxButton buttons, string? primary)
    {
        InitializeComponent();
        Heading.Text = heading;
        Message.Text = message;
        Message.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        if (Message.Visibility != Visibility.Visible) Heading.Margin = new Thickness(0);

        bool cancel = buttons == MessageBoxButton.OKCancel;
        if (!cancel) Buttons.Children.Add(new Border()); // 왼쪽 빈 칸
        _result = cancel ? MessageBoxResult.Cancel : MessageBoxResult.OK;
        var ok = Primary = AddButton(primary ?? "확인", MessageBoxResult.OK, accent: true);
        ok.IsDefault = true;
        if (cancel) AddButton("취소", MessageBoxResult.Cancel, accent: false).IsCancel = true;
        else ok.IsCancel = true;

        SourceInitialized += (_, _) => Hwnd.ApplyFluent(this); // 둥근 모서리 + 시스템 테두리·그림자
        Loaded += (_, _) => ok.Focus();
        // 제목 표시줄이 없으니 창 아무 데나 끌어서 옮긴다.
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not Button) DragMove(); };
    }

    public MessageBoxResult Result => _result;

    /// <summary>파란 버튼(확인·저장 등). 내용에 따라 누를 수 있게 하거나 막을 때 쓴다.</summary>
    public Button Primary { get; }

    /// <summary>안내 글 아래에 내용을 더한다(예: 조합키 입력 칸).</summary>
    public void AddContent(UIElement content) => Body.Children.Add(content);

    private Button AddButton(string text, MessageBoxResult result, bool accent)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)FindResource(accent ? "AccentButton" : "StdButton"),
            Margin = new Thickness(Buttons.Children.Count == 0 ? 0 : 4, 0, Buttons.Children.Count == 0 ? 4 : 0, 0),
        };
        b.Click += (_, _) => { _result = result; Close(); };
        Buttons.Children.Add(b);
        return b;
    }
}
