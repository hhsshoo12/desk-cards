using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace DeskCards;

/// <summary>웹 카드(.dard): 가져오기, 바탕화면에 놓기·내리기·지우기.</summary>
internal sealed class WebCardsPage : SettingsPage
{
    private bool _rebuilding;

    public WebCardsPage(SettingsPageArgs a) : base(a)
    {
        Start();
        // exe가 켜지고 꺼지는 것을 목록에 바로 비춘다(한 번에 여러 번 와도 한 번만 다시 그린다).
        Loaded += (_, _) => { if (WebCardManager.Instance is { } w) w.Changed += OnCardsChanged; };
        Unloaded += (_, _) => { if (WebCardManager.Instance is { } w) w.Changed -= OnCardsChanged; };
    }

    private void OnCardsChanged()
    {
        if (_rebuilding) return;
        _rebuilding = true;
        Ui.Post(() => { _rebuilding = false; Rebuild(); });
    }

    protected override void Build()
    {
        Crumbs(("카드", SettingsPageKind.Cards), ("웹 카드", null));
        var web = WebCardManager.Instance;
        if (web == null) return;

        Row("\uE8E5", ".dard 파일 가져오기", "웹 카드 파일을 골라 가져와요. 같은 카드가 있으면 새 내용으로 바꿔요.", AccentBtn("가져오기", () => _ = PickAsync(web, folder: false)));
        Row("\uE8B7", "폴더 가져오기", "manifest.json이 들어 있는 카드 폴더를 그대로 가져와요.", Btn("폴더", () => _ = PickAsync(web, folder: true)));

        var cards = web.Store.List();
        Header("가져온 카드");
        if (cards.Count == 0)
        {
            Row("\uE946", "가져온 카드가 없어요", "위에서 .dard 파일이나 카드 폴더를 가져와 보세요.");
            return;
        }
        foreach (var card in cards)
        {
            var m = card.Manifest;
            string id = m.Id;
            string kind = (m.Html != null ? "화면" : "") + (m.Html != null && m.Exe != null ? " + " : "") + (m.Exe != null ? "프로그램" : "") + (m.Background ? " · 상시 동작" : "");
            var toggle = Switch(web.IsPlaced(id), on =>
            {
                if (on) web.Place(id); else web.Unplace(id);
                Ui.Post(Rebuild);
            });
            ToolTipService.SetToolTip(toggle, "바탕화면에 두기");
            var remove = Btn("지우기", () =>
            {
                if (Dialogs.Show($"'{m.Name}' 카드를 지울까요? 카드가 쓰던 데이터 폴더는 그대로 남아요.", MessageBoxButton.OKCancel, "카드 지우기", "지우기") != MessageBoxResult.OK) return;
                web.Uninstall(id);
                Ui.Post(Rebuild);
            });
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            string state = "";
            if (web.ExeStatus(id) is { } running)
            {
                state = running ? " · 실행 중" : " · 멈춤";
                panel.Children.Add(Btn(running ? "멈추기" : "시작", () => { if (running) web.StopExe(id); else web.StartExe(id); }));
                if (running) panel.Children.Add(Btn("다시 시작", () => web.RestartExe(id)));
            }
            panel.Children.Add(toggle);
            panel.Children.Add(remove);
            Row("\uE8B7", m.Name, $"{id} · {kind}{state}", panel);
        }
    }

    private async System.Threading.Tasks.Task PickAsync(WebCardManager web, bool folder)
    {
        try
        {
            IntPtr hwnd = Hwnd.Of(Host);
            string? path;
            if (folder)
            {
                var picker = new FolderPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.FileTypeFilter.Add("*");
                path = (await picker.PickSingleFolderAsync())?.Path;
            }
            else
            {
                var picker = new FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.FileTypeFilter.Add(".dard");
                picker.FileTypeFilter.Add(".zip");
                path = (await picker.PickSingleFileAsync())?.Path;
            }
            if (path == null) return;
            var card = web.Import(path);
            if (!web.IsPlaced(card.Manifest.Id)) web.Place(card.Manifest.Id);
            Rebuild();
        }
        catch (WebCardException ex) { Dialogs.Show(ex.Message, heading: "카드를 가져오지 못했어요"); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        { Dialogs.Show(ex.Message, heading: "카드를 가져오지 못했어요"); }
    }
}