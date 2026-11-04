using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DeskCards;

/// <summary>편집 상태(편집 중·고름·안 되는 자리·드롭 대상)와 그에 따른 강조 테두리.</summary>
internal sealed partial class CardWindow
{
    private static readonly Windows.UI.Color InvalidColor = ColorHelper.FromArgb(255, 0xE8, 0x11, 0x23);
    private static readonly Brush InvalidBrush = new SolidColorBrush(InvalidColor);

    private bool _editing, _selected, _invalid, _dropTarget;

    public bool IsEditing => _editing;

    /// <summary>편집 모드로. 켜고 끄는 건 GroupManager.BeginEditMode/EndEditMode가 모든 카드에 한꺼번에 한다.</summary>
    public void BeginEdit()
    {
        _editing = true;
        _editor.Enabled = true;
        UpdateBorder();
        RaiseForEdit();
    }

    public void EndEdit()
    {
        if (!_editing) return;
        _editing = _selected = _invalid = false;
        _editor.Enabled = false;
        UpdateBorder();
        SendToBottom(); // HWND_BOTTOM은 맨 위(topmost) 상태도 함께 푼다.
        Mgr.SavePosition(this);
    }

    /// <summary>편집 막대의 작업 대상으로 골랐는지. 고른 카드는 테두리가 굵어진다.</summary>
    public void SetSelected(bool on)
    {
        _selected = on && _editing;
        UpdateBorder();
    }

    /// <summary>지금 자리가 안 되는 자리일 때 빨간 테두리(편집 중에만).</summary>
    internal void SetInvalid(bool on)
    {
        on &= _editing;
        if (_invalid == on) return;
        _invalid = on;
        UpdateBorder();
    }

    /// <summary>편집 중이면 지금 자리를 지금 설정으로 검사해 표시한다.</summary>
    public void CheckPlacement() => SetInvalid(_editing && _editor.Placement() != SmartGuides.Placement.Ok);

    private void SetDropTarget(bool on)
    {
        _dropTarget = on;
        UpdateBorder();
    }

    /// <summary>
    /// 드롭 대상이거나 편집 중이면 강조 테두리. 편집 중에 고른 카드는 더 굵게.
    /// Windows가 그리는 1px 창 테두리를 강조색으로 바꾸고, 고른 카드는 안쪽 테두리를 더한다.
    /// 지금 설정에서 안 되는 자리(겹침·간격보다 가까움)는 빨간색으로.
    /// </summary>
    private void UpdateBorder()
    {
        bool strong = _dropTarget || _selected || _invalid;
        var hwnd = Handle;
        if (hwnd != IntPtr.Zero)
        {
            var c = _invalid ? InvalidColor : Theme.Color("Accent");
            Native.SetDwm(hwnd, Native.DWMWA_BORDER_COLOR,
                strong || _editing ? c.R | c.G << 8 | c.B << 16 : Native.DWMWA_COLOR_DEFAULT);
        }
        var frame = View.Card;
        frame.BorderBrush = _invalid ? InvalidBrush : Theme.Brush("Accent");
        frame.BorderThickness = new Thickness(strong ? 2 : 0);
    }
}
