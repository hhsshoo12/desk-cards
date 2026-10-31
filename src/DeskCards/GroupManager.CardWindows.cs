using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskCards;

// 1단계에서는 기존 패키지를 띄우지 않는다. 창 복구는 새 카드 실행 경로에 연결할 때까지 보존한다.
internal sealed partial class GroupManager
{
    private readonly Dictionary<string, CardRuntime> _webCards = new(StringComparer.OrdinalIgnoreCase);

    private void OnBrowserLost(string profile) => _debounce.Dispatcher.BeginInvoke(() => RecoverBrowserCards(profile));

    private void RecoverBrowserCards(string profile)
    {
        if (_shuttingDown) return;
        bool blocked = BrowserEnvironment.BrowserRecoveryBlocked(profile);
        foreach (var runtime in _webCards.Values.Where(r => string.Equals(
            BrowserEnvironment.ProfileDir(), profile, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            CardSettingsWindow.CloseFor(runtime);
            foreach (var card in runtime.Windows.ToList())
            {
                if (blocked) card.ShowError(BrowserEnvironment.BrowserRecoveryError);
                else RecreateWebCardWindow(card);
            }
        }
    }

    internal void ReloadWebCardWindow(WebCardWindow card)
    {
        if (_shuttingDown || card.Runtime.WindowFor(card.Info.Id) != card) return;
        if (_cfg.Cards.Any(c => c.Directory == card.Runtime.Package.Path))
        {
            BrowserEnvironment.ResetBrowserRecovery();
            RestartImportedCard(card.Runtime.Package.Path, true);
            return;
        }
        string profile = BrowserEnvironment.ProfileDir();
        bool blocked = BrowserEnvironment.BrowserRecoveryBlocked(profile);
        BrowserEnvironment.ResetBrowserRecovery();
        if (blocked) RecoverBrowserCards(profile);
        else card.ReloadPage();
    }

    private void CreateWebCardWindow(CardRuntime runtime, WebCardInfo info)
    {
        var window = new WebCardWindow(runtime, info, this);
        runtime.AddWindow(window);
        PlaceAndShow(window, WebCardWindow.BaseSizeFor(info, CellSize));
    }

    private void RecreateWebCardWindow(WebCardWindow window)
    {
        var runtime = window.Runtime;
        if (Selected == window) Select(null);
        window.ClosingByManager = true;
        window.Close();
        CreateWebCardWindow(runtime, window.Info);
    }

    private void UnloadWebCard(string path)
    {
        if (!_webCards.Remove(path, out var runtime)) return;
        runtime.SourceWatcher?.Dispose();
        runtime.Backend?.Dispose();
        CardSettingsWindow.CloseFor(runtime);
        foreach (var window in runtime.Windows.ToList())
        {
            if (Selected == window) Select(null);
            window.ClosingByManager = true;
            window.Close();
        }
    }
}
