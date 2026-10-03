using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DeskCards;

internal sealed partial class GroupManager
{
    // ----- 편집 모드 -----
    // 모든 카드를 한꺼번에 옮기고 크기를 바꿀 수 있게 하고, 화면 위쪽에 편집 막대를 띄운다.
    // 막대의 이름 바꾸기·칸 수·삭제 등은 '고른 카드'(마지막으로 누른 카드)에 적용된다.

    public bool Editing { get; private set; }
    public DeskCard? Selected { get; private set; }

    public void BeginEditMode(DeskCard? select = null)
    {
        if (!Editing)
        {
            Editing = true;
            // 설정 창은 잠시 숨겼다가 편집이 끝나면 되살린다. 다른 앱 창은 바탕화면 보기로 치우고 되살리지 않는다.
            SettingsWindow.HideForEdit();
            ExpandedWindow.CloseCurrent();
            bool toggled = DesktopShell.ShowDesktop();
            foreach (var c in AllCards) c.BeginEdit();
            RefreshPlacement();
            // 바탕화면 보기가 창들을 치우는 동안 기다렸다가 막과 막대를 띄운다(먼저 띄우면 같이 치워진다).
            var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(toggled ? 350 : 1) };
            delay.Tick += (_, _) =>
            {
                delay.Stop();
                if (!Editing) return;
                EditDim.ShowAll(this);
                EditBar.Open(this);
                RaiseEditLayer();
            };
            delay.Start();
        }
        Select(select ?? Selected);
    }

    public void EndEditMode()
    {
        if (!Editing) return;
        Editing = false;
        Selected = null;
        EditDim.CloseAll();
        BalloonTip.CloseCurrent();
        foreach (var c in AllCards) c.EndEdit();
        EditBar.CloseBar();
        EditChanged?.Invoke();
    }

    public void Select(DeskCard? card)
    {
        Selected = card;
        foreach (var c in AllCards) c.SetSelected(c == card);
        RaiseEditLayer();
        EditChanged?.Invoke();
    }

    /// <summary>
    /// 편집 중에 모든 카드를 어두운 막 위로, 막대는 그 위로 다시 올린다.
    /// 새 카드를 띄우면(바탕화면 층에 붙이는 과정에서) 다른 카드들이 막 뒤로 밀려나기 때문에 그때마다 부른다.
    /// </summary>
    private void RaiseEditLayer()
    {
        if (!Editing) return;
        foreach (var c in AllCards) c.RaiseForEdit();
        EditBar.BringToTop();
    }
}
