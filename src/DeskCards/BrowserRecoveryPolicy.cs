using System.Collections.Generic;

namespace DeskCards;

/// <summary>환경 객체가 교체돼도 유지하는 프로필별 자동 복구 한도. UI 스레드에서만 사용한다.</summary>
internal sealed class BrowserRecoveryPolicy
{
    private readonly Queue<long> _exits = new();
    public bool Blocked { get; private set; }

    public void RecordExit(long now)
    {
        if (Blocked) return;
        while (_exits.TryPeek(out long first) && now - first >= 60_000) _exits.Dequeue();
        _exits.Enqueue(now);
        Blocked = _exits.Count > 3;
    }
}
