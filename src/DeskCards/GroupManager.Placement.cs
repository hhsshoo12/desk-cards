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
    /// <summary>편집 중이면 모든 카드의 자리를 지금 설정으로 다시 검사해 안 되는 자리를 빨간 테두리로 표시한다.</summary>
    public void RefreshPlacement()
    {
        foreach (var c in AllCards) c.CheckPlacement();
    }

    /// <summary>다른 카드들의 화면 위치(픽셀). 옮길 때 안내선 기준으로 쓴다.</summary>
    public IReadOnlyList<Native.RECT> CardRects(DeskCard except) =>
        AllCards.Where(c => c != except)
            .Select(c => c.Footprint)
            .Where(r => r.Right > r.Left)
            .ToList();

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _debounce.Dispatcher.BeginInvoke(() =>
    {
        if (!_shuttingDown) RefitAll();
    });

    /// <summary>모든 카드를 화면 안으로 다시 맞추고 위치를 저장한다.</summary>
    private void RefitAll()
    {
        foreach (var card in AllCards) card.Refit();
        RaiseChanged();
    }

    /// <summary>저장된 자리(없으면 빈 자리)에 카드를 띄운다. 편집 중이면 바로 편집 상태로.</summary>
    /// <param name="baseSize">빈 자리를 찾을 때 쓸 카드 기준 크기. 없으면 기본 폴더 카드.</param>
    private void PlaceAndShow(DeskCard card, Size? baseSize)
    {
        if (_cfg.PositionsPx.TryGetValue(card.Key, out var pos) && pos.Length == 2 && IsOnScreen(pos[0], pos[1]))
        {
            card.RestorePosition(new Point(pos[0], pos[1]));
        }
        else
        {
            var p = NextFreeSlot(baseSize);
            card.RestorePosition(p);
            _cfg.PositionsPx[card.Key] = new[] { p.X, p.Y };
            _cfg.Save();
        }
        card.Closed += OnCardClosed;
        card.Show();
        // 해상도나 작업 표시줄이 바뀌었을 수 있으니 화면 안으로 맞춘다.
        card.Refit();
        if (Editing)
        {
            card.BeginEdit();
            RaiseEditLayer();
        }
    }

    public void SavePosition(DeskCard card)
    {
        var p = card.PhysicalPosition;
        _cfg.PositionsPx[card.Key] = new[] { p.X, p.Y };
        _cfg.Save();
    }

    private static bool IsOnScreen(double x, double y)
    {
        return System.Windows.Forms.Screen.AllScreens.Any(s => new Rect(s.WorkingArea.X, s.WorkingArea.Y,
            s.WorkingArea.Width, s.WorkingArea.Height).Contains(new Point(x + 40, y + 40)));
    }

    /// <summary>
    /// 새 카드 자리. 주 모니터 작업 영역을 반 카드 간격 격자로 나눠 오른쪽 위부터 아래로,
    /// 다음 열은 왼쪽으로 가며 비어 있는 첫 자리를 찾는다. 새 카드는 기본 크기다.
    /// </summary>
    /// <param name="baseSize">새 카드의 기준 크기(DIP). 없으면 기본 폴더 카드(2×2).</param>
    private Point NextFreeSlot(Size? baseSize = null)
    {
        var wa = SystemParameters.WorkArea;
        var taken = AllCards.Where(c => Hwnd.Of(c) != IntPtr.Zero).Select(c => c.Footprint)
            .Select(r => new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)).ToList();

        double s = Native.PrimaryScale();
        double k = _cfg.DefaultZoom * ZoomFactor(s);
        var size = baseSize ?? CardWindow.BaseSize(new CardLayout(), CellSize);
        if (baseSize == null) size.Height += DeskCard.LabelH; // 카드 아래 이름 줄
        int wPx = (int)Math.Round(size.Width * k * s), hPx = (int)Math.Round(size.Height * k * s);
        var waPx = DesktopGrid.WorkAreaAt((int)(wa.Left * s) + 1, (int)(wa.Top * s) + 1);
        var (nx, ny) = DesktopGrid.Counts(waPx, wPx, hPx);
        for (int i = nx; i >= 0; i -= 2)
        {
            for (int j = 0; j <= ny; j += 2)
            {
                var (px, py) = DesktopGrid.CellAt(waPx, wPx, hPx, i, j);
                var r = new Rect(px, py, wPx, hPx);
                var probe = new Rect(r.X + 4, r.Y + 4, r.Width - 8, r.Height - 8);
                if (!taken.Any(t => t.IntersectsWith(probe))) return r.TopLeft;
            }
        }
        return new Point((wa.Left + 40) * s, (wa.Top + 40) * s); // 빈 자리가 없으면 겹쳐서라도 보이게
    }
}
