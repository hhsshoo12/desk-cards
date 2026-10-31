using System;
using System.Collections.Generic;
using System.IO;

namespace DeskCards;

/// <summary>브라우저 프로필별 자동 복구 횟수와 중단 상태를 관리한다.</summary>
internal sealed class BrowserRecoveryRegistry
{
    private readonly Dictionary<string, BrowserRecoveryPolicy> _policies = new(StringComparer.OrdinalIgnoreCase);

    public bool IsBlocked(string path) =>
        _policies.TryGetValue(Path.GetFullPath(path), out var policy) && policy.Blocked;

    public void Reset(string path) => _policies.Remove(Path.GetFullPath(path));

    public void RecordExit(string path, long now)
    {
        path = Path.GetFullPath(path);
        if (!_policies.TryGetValue(path, out var policy))
            _policies[path] = policy = new BrowserRecoveryPolicy();
        policy.RecordExit(now);
    }
}
