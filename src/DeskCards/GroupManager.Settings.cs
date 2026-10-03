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
    /// <summary>카드를 옮기거나 크기를 바꿀 때 안내선을 보여 주고 줄에 맞출지.</summary>
    public bool ShowGuides
    {
        get => _cfg.ShowGuides;
        set
        {
            if (_cfg.ShowGuides == value) return;
            _cfg.ShowGuides = SmartGuides.Enabled = value;
            _cfg.Save();
            RaiseChanged();
        }
    }

    /// <summary>실험: 카드끼리 간격 없이 딱 붙여 둘 수 있게 할지.</summary>
    public bool FlushSnap
    {
        get => _cfg.FlushSnap || _cfg.AllowOverlap;
        set
        {
            if (_cfg.AllowOverlap) return; // 겹치기를 허용하는 동안은 늘 켜짐
            Store(_cfg.FlushSnap != value, () => _cfg.FlushSnap = SmartGuides.Flush = value);
            RefreshPlacement();
        }
    }

    /// <summary>실험: 카드끼리 겹친 자리도 저장할지. 켜면 완전히 붙이기도 함께 켜진다.</summary>
    public bool AllowOverlap
    {
        get => _cfg.AllowOverlap;
        set
        {
            Store(_cfg.AllowOverlap != value, () =>
            {
                _cfg.AllowOverlap = SmartGuides.AllowOverlap = value;
                if (value) _cfg.FlushSnap = true;
                SmartGuides.AllowOverlap = _cfg.AllowOverlap;
                SmartGuides.Flush = _cfg.FlushSnap || _cfg.AllowOverlap;
            });
            RefreshPlacement();
        }
    }

    /// <summary>안내 말풍선에서 "다시 보지 않기"를 눌렀는지.</summary>
    public bool TipHidden(string id) => _cfg.HiddenTips.Contains(id);

    public int HiddenTipCount => _cfg.HiddenTips.Count;

    public void HideTip(string id) => Store(!_cfg.HiddenTips.Contains(id), () => _cfg.HiddenTips.Add(id));

    /// <summary>숨긴 안내를 모두 다시 보이게 한다.</summary>
    public void ShowAllTips() => Store(_cfg.HiddenTips.Count > 0, () => _cfg.HiddenTips.Clear());

    // ----- 카드 바 -----

    public bool BarEnabled { get => _cfg.BarEnabled; set => Store(_cfg.BarEnabled != value, () => _cfg.BarEnabled = value); }

    public int BarDelay
    {
        get => _cfg.BarDelay;
        set
        {
            value = Math.Clamp((int)Math.Round(value / 100.0) * 100, 0, Config.BarDelayMax);
            Store(_cfg.BarDelay != value, () => _cfg.BarDelay = value);
        }
    }

    /// <summary>이 디스플레이에서 여는 가장자리. null이면 이 디스플레이에서는 열지 않는다.</summary>
    public ScreenEdge? BarEdgeFor(string display) =>
        _cfg.BarEdges.TryGetValue(display, out var edge) ? edge : _cfg.BarEdge;

    /// <summary>디스플레이별 가장자리를 정한다. 고른 가장자리는 처음 보는 디스플레이의 기본값도 된다.</summary>
    public void SetBarEdge(string display, ScreenEdge? edge)
    {
        if (_cfg.BarEdges.TryGetValue(display, out var old) && old == edge) return;
        _cfg.BarEdges[display] = edge;
        if (edge is { } e) _cfg.BarEdge = e;
        _cfg.Save();
        RaiseChanged();
    }
    public IReadOnlyList<int> BarKeys
    {
        get => _cfg.BarKeys;
        set
        {
            var keys = KeyCombo.Clean(value);
            Store(!keys.SequenceEqual(_cfg.BarKeys), () => _cfg.BarKeys = keys);
        }
    }

    public int BarSize
    {
        get => _cfg.BarSize;
        set
        {
            value = Math.Clamp(value, Config.BarSizeMin, Config.BarSizeMax);
            Store(_cfg.BarSize != value, () => _cfg.BarSize = value);
        }
    }

    public int BarZone
    {
        get => _cfg.BarZone;
        set
        {
            value = Math.Clamp(value, 0, Config.BarZoneMax);
            Store(_cfg.BarZone != value, () => _cfg.BarZone = value);
        }
    }

    /// <summary>바뀌었을 때만 적용하고 저장한 뒤 알린다.</summary>
    private void Store(bool changed, Action apply)
    {
        if (!changed) return;
        apply();
        _cfg.Save();
        RaiseChanged();
    }

    /// <summary>카드의 더보기 칸에 마우스를 잠시 올려 두면 펼칠지.</summary>
    public bool HoverExpand
    {
        get => _cfg.HoverExpand;
        set
        {
            if (_cfg.HoverExpand == value) return;
            _cfg.HoverExpand = value;
            _cfg.Save();
            RaiseChanged();
        }
    }

    /// <summary>더보기 칸에 올려 두고 펼칠 때까지 기다리는 시간(ms).</summary>
    public int HoverExpandDelay
    {
        get => _cfg.HoverExpandDelay;
        set
        {
            value = Config.NormalizeHoverDelay(value);
            if (_cfg.HoverExpandDelay == value) return;
            _cfg.HoverExpandDelay = value;
            _cfg.Save();
            RaiseChanged();
        }
    }

    /// <summary>
    /// 켜져 있으면 Windows 배율이 바뀔 때 카드도 같은 비율로 따라 커지고 작아진다.
    /// 켜고 끄는 순간에는 지금 보이는 크기를 그대로 두고, 크기 조절은 어느 쪽이든 할 수 있다.
    /// </summary>
    public bool FollowWindowsScale
    {
        get => _cfg.FollowWindowsScale;
        set
        {
            if (_cfg.FollowWindowsScale == value) return;
            if (value)
            {
                // 고정해 두었던 크기를 지금 배율 기준 확대 비율로 옮겨 담는다.
                foreach (var card in AllCards)
                    card.RebaseZoom(_cfg.FixedScale / card.DpiScale);
                _cfg.DefaultZoom *= _cfg.FixedScale / Native.PrimaryScale();
            }
            else
            {
                _cfg.FixedScale = Native.PrimaryScale();
                foreach (var card in AllCards)
                    card.RebaseZoom(card.DpiScale / _cfg.FixedScale);
            }
            _cfg.FollowWindowsScale = value;
            _cfg.Save();
            RefitAll();
        }
    }

    /// <summary>
    /// 카드 확대 비율에 추가로 곱할 값. 따라가기가 켜져 있으면 1(WPF가 배율대로 키워 준다),
    /// 꺼져 있으면 배율이 바뀐 만큼 되돌려 실제 크기를 유지한다.
    /// </summary>
    public double ZoomFactor(double dpiScale) => FollowWindowsScale ? 1 : _cfg.FixedScale / dpiScale;

    /// <summary>미리보기 칸 하나의 기준 크기(DIP).</summary>
    public double CellSize => _cfg.CellSize;

    /// <summary>예전 설정(Zoom에 배율이 따로 곱해지던 방식)을 지금 방식으로 바꾸고, 빠진 값을 채운다.</summary>
    private void MigrateScale()
    {
        double s = Native.PrimaryScale();
        bool dirty = false;
        if (_cfg.CellSize <= 0)
        {
            _cfg.CellSize = CardWindow.MeasureCellSize(Native.GetDpiForSystem() / 96.0);
            dirty = true;
        }
        if (_cfg.ScaleVersion < 2)
        {
            // 예전: 켜짐이면 크기 = 배율 × Zoom. 이제는 배율을 Zoom에 담아 둔다.
            if (_cfg.FollowWindowsScale)
                foreach (var l in _cfg.Layouts.Values) l.Zoom = Math.Round(l.Zoom * s, 3);
            _cfg.DefaultZoom = _cfg.FollowWindowsScale ? s : 1;
            _cfg.FixedScale = s;
            _cfg.ScaleVersion = 2;
            dirty = true;
        }
        if (_cfg.FixedScale <= 0) { _cfg.FixedScale = s; dirty = true; }
        if (_cfg.DefaultZoom <= 0) { _cfg.DefaultZoom = _cfg.FollowWindowsScale ? s : 1; dirty = true; }
        if (dirty) _cfg.Save();
    }

    /// <summary>카드 모양(칸 수·확대 비율). 저장된 게 없으면 2×2, 기본 확대 비율.</summary>
    public CardLayout GetLayout(string name) =>
        _cfg.Layouts.TryGetValue(name, out var l) ? l.Normalized() : new CardLayout { Zoom = _cfg.DefaultZoom }.Normalized();

    public void SaveLayout(DeskCard card, CardLayout layout)
    {
        var l = layout.Normalized();
        l.Zoom = Math.Round(l.Zoom, 3);
        _cfg.Layouts[card.Key] = l;
        _cfg.Save();
        RaiseChanged();
    }
}
