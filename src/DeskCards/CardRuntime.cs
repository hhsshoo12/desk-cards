using System.Collections.Generic;
using System.Linq;

namespace DeskCards;

/// <summary>
/// 불러온 웹 카드 하나. 카드 창들과 그 안의 화면(카드·설정)을 묶어서 카드끼리 메시지를 전한다.
/// 카드 설정은 카드가 브라우저 저장소(localStorage 등)에 직접 둔다. 설정 화면은 그 카드와 같은 주소라 같은 저장소를 본다.
/// </summary>
internal sealed class CardRuntime
{
    private readonly GroupManager _mgr;
    private readonly Dictionary<string, WebCardWindow> _windows = new();

    public CardRuntime(CardDocument package, GroupManager mgr, CardStamp stamp)
    {
        Package = package;
        _mgr = mgr;
        Stamp = stamp;
    }

    public CardDocument Package { get; }
    public System.IDisposable? Backend { get; init; }
    public System.Threading.Tasks.Task Ready { get; init; } = System.Threading.Tasks.Task.CompletedTask;
    public System.Action? Restart { get; init; }
    public System.IO.FileSystemWatcher? SourceWatcher { get; set; }

    /// <summary>불러올 때의 파일 크기·수정 시각. 같으면 다시 읽지 않는다.</summary>
    public CardStamp Stamp { get; set; }

    public IReadOnlyCollection<WebCardWindow> Windows => _windows.Values;

    /// <summary>위치·모양을 저장하는 이름. 폴더 이름에는 ':'가 없으니 그룹과 겹치지 않는다.</summary>
    public string KeyFor(string cardId) => $"card:{Package.Id}/{cardId}";

    public void AddWindow(WebCardWindow w) => _windows[w.Info.Id] = w;

    public void RemoveWindow(WebCardWindow w)
    {
        if (_windows.TryGetValue(w.Info.Id, out var current) && current == w) _windows.Remove(w.Info.Id);
    }

    public WebCardWindow? WindowFor(string cardId) => _windows.GetValueOrDefault(cardId);

    /// <summary>카드 옆에 설정 화면을 띄운다.</summary>
    public void OpenSettings(string cardId)
    {
        var info = Package.Cards.FirstOrDefault(c => c.Id == cardId);
        if (info == null || Package.SettingsHtml == null) return;
        CardSettingsWindow.Open(this, info, WindowFor(cardId));
    }
}

/// <summary>파일이 바뀌었는지 가볍게 보는 표시(크기 + 마지막 수정 시각).</summary>
internal readonly record struct CardStamp(long Length, long WriteTicks);
