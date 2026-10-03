using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace DeskCards;

/// <summary>
/// 불러온 .dard 하나. 카드 창들과 그 안의 화면(카드·설정)을 묶어서 카드끼리 메시지를 전한다.
/// 카드 설정은 카드가 브라우저 저장소(localStorage 등)에 직접 둔다. 설정 화면은 그 카드와 같은 주소라 같은 저장소를 본다.
/// </summary>
internal sealed class DardRuntime
{
    private readonly GroupManager _mgr;
    private readonly List<DardView> _views = new();
    private readonly Dictionary<string, DardWindow> _windows = new();

    public DardRuntime(DardPackage package, GroupManager mgr, DardStamp stamp)
    {
        Package = package;
        _mgr = mgr;
        Stamp = stamp;
    }

    public DardPackage Package { get; }

    /// <summary>불러올 때의 파일 크기·수정 시각. 같으면 다시 읽지 않는다.</summary>
    public DardStamp Stamp { get; set; }

    public IReadOnlyCollection<DardWindow> Windows => _windows.Values;

    /// <summary>위치·모양을 저장하는 이름. 폴더 이름에는 ':'가 없으니 그룹과 겹치지 않는다.</summary>
    public string KeyFor(string cardId) => $"dard:{Package.Id}/{cardId}";

    public void AddWindow(DardWindow w) => _windows[w.Info.Id] = w;

    public void RemoveWindow(DardWindow w)
    {
        if (_windows.TryGetValue(w.Info.Id, out var current) && current == w) _windows.Remove(w.Info.Id);
    }

    public DardWindow? WindowFor(string cardId) => _windows.GetValueOrDefault(cardId);

    public void Register(DardView view) => _views.Add(view);

    public void Forget(DardView view) => _views.Remove(view);

    /// <summary>같은 .dard의 다른 카드에 "message" 이벤트를 보낸다. 그런 카드가 없으면 false.</summary>
    public bool Deliver(string from, string to, JsonNode? data)
    {
        if (!Package.Cards.Any(c => c.Id == to)) return false;
        foreach (var v in _views.Where(v => v.CardId == to && !v.IsSettings).ToList())
            v.Emit("message", new JsonObject { ["from"] = from, ["data"] = data?.DeepClone() });
        return true;
    }

    /// <summary>카드 옆에 설정 화면을 띄운다.</summary>
    public void OpenSettings(string cardId)
    {
        var info = Package.Cards.FirstOrDefault(c => c.Id == cardId);
        if (info == null || Package.SettingsRatio == null) return;
        DardSettingsWindow.Open(this, info, WindowFor(cardId));
    }
}

/// <summary>파일이 바뀌었는지 가볍게 보는 표시(크기 + 마지막 수정 시각).</summary>
internal readonly record struct DardStamp(long Length, long WriteTicks);
