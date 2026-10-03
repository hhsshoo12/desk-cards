using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeskCards;

internal partial class SettingsWindow
{
    private string CardsSignature() =>
        string.Join("|", _mgr.Cards.Select(c =>
            $"{c.Group.Name}/{c.Group.Items.Count}/{c.CurrentLayout.Cols}x{c.CurrentLayout.Rows}/{c.SizePercent}")) + "#" +
        string.Join("|", _mgr.DardEntries().Select(d => $"{d.Path}/{d.State}/{d.Runtime?.Package.Version}")) + "#" +
        string.Join("|", _mgr.DardIssues().Select(i => i.Key + "=" + i.Detail));

    /// <summary>카드: 편집 시작, 그리고 폴더 카드 › · 위젯 카드 ›.</summary>
    private void BuildCards()
    {
        _cardsSignature = CardsSignature();
        Crumb("카드");
        AddRow(Row("", "카드 편집",
            "화면 위쪽에 편집 막대가 떠요. 카드를 끌어 옮기고, 오른쪽 아래 모서리로 크기를 바꾸고, 골라서 지울 수 있어요.",
            Button("편집 시작", () => _mgr.BeginEditMode(), accent: true)));

        int widgets = _mgr.DardEntries().Count;
        int issues = _mgr.DardIssues().Count;
        AddRow(Row("", "폴더 카드", $"그룹 {_mgr.Cards.Count}개 · 폴더에 넣은 파일을 아이콘으로 보여 주는 카드",
            null, () => Go(PageKind.FolderCards)));
        AddRow(Row(issues > 0 ? "\uE7BA" : "", "위젯 카드",
            (issues > 0 ? $"확인할 것 {issues}개 · " : "") + $"위젯 {widgets}개 · .dard 파일로 추가하는 카드(시계, 메모 등)",
            null, () => Go(PageKind.WidgetCards)));
    }

    // ----- 폴더 카드 -----

    private void BuildFolderCards()
    {
        _cardsSignature = CardsSignature();
        Crumb("카드", () => Go(PageKind.Cards));
        Crumb("폴더 카드");
        AddRow(Row("", "새 그룹", "빈 카드를 하나 만들고 이름을 정해요.",
            Button("만들기", () => _mgr.NewGroup())));

        var cards = _mgr.Cards;
        Header($"그룹 {cards.Count}개");
        foreach (var card in cards)
        {
            var c = card;
            var l = c.CurrentLayout;
            string desc = $"항목 {c.Group.Items.Count}개 · 칸 {l.Cols}×{l.Rows} · 크기 {c.SizePercent}%";
            UIElement? icon = null;
            if (c.Group.Items.Count > 0)
            {
                var img = new Image { Source = c.Group.Items[0].Icon, Width = 24, Height = 24 };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                icon = img;
            }
            AddRow(Row("", c.Group.Name, desc, null, () => ShowCard(c), icon));
        }
    }

    private void BuildCard(CardWindow card)
    {
        _cardName = card.Group.Name;
        Crumb("카드", () => Go(PageKind.Cards));
        Crumb("폴더 카드", () => Go(PageKind.FolderCards));
        Crumb(card.Group.Name);

        AddRow(Row("", "이름", "그룹 폴더 이름도 같이 바뀌어요.", NameBox(card)));
        AddRow(Row("", "크기", "기본 크기 대비 비율이에요. [−] [+]는 5%씩, 숫자를 눌러 직접 입력할 수도 있어요.", Percent(card)));
        AddRow(Row("", "미리보기 칸 (가로)", "카드에 아이콘이 몇 칸 보일지 정해요. 넘치는 항목은 마지막 칸에 묶여요.",
            Stepper(() => card.CurrentLayout.Cols, v => card.SetGrid(v, card.CurrentLayout.Rows))));
        AddRow(Row("", "미리보기 칸 (세로)", null,
            Stepper(() => card.CurrentLayout.Rows, v => card.SetGrid(card.CurrentLayout.Cols, v))));

        Header("관리");
        AddRow(Row("", "위치 옮기기 · 크기 조절", "편집 막대를 띄우고 이 카드를 골라 둬요.",
            Button("편집", () => _mgr.BeginEditMode(card))));
        AddRow(Row("", "폴더 열기", card.Group.Folder,
            Button("열기", () => FileOps.OpenFolder(card.Group.Folder))));
        var delete = Button("삭제", () =>
        {
            if (_mgr.DeleteGroup(card)) Go(PageKind.FolderCards);
        });
        delete.SetResourceReference(ForegroundProperty, "Danger");
        AddRow(Row("", "이 그룹 삭제", "안에 있는 항목은 바탕화면으로 옮겨져요.", delete));
    }

    // ----- 위젯 카드 -----

    private void BuildWidgetCards()
    {
        _cardsSignature = CardsSignature();
        Crumb("카드", () => Go(PageKind.Cards));
        Crumb("위젯 카드");
        AddRow(Row("", "위젯 추가",
            ".dard 파일을 골라 그룹 폴더에 넣어요. 처음 넣는 위젯은 추가할지 한 번 물어봐요. 그룹 폴더에 직접 넣어도 돼요.",
            Button("파일 선택", AddWidgets, accent: true)));

        AddDardIssues();

        var list = _mgr.DardEntries();
        Header($"위젯 {list.Count}개");
        if (list.Count == 0)
        {
            AddRow(Row("", "아직 위젯이 없어요", "위의 [파일 선택]으로 .dard 파일을 추가해 보세요.", null));
            return;
        }
        foreach (var entry in list)
        {
            string path = entry.Path;
            string desc = entry.State switch
            {
                DardState.On => $"카드 {entry.Runtime!.Package.Cards.Count}장 · 버전 {entry.Runtime.Package.Version}",
                DardState.Off => "꺼 둠",
                DardState.Asking => "추가할지 묻는 중",
                _ => "열 수 없음",
            };
            AddRow(Row(entry.State == DardState.On ? "" : entry.State == DardState.Off ? "" : "",
                entry.Name, desc, null, () => ShowWidget(path)));
        }
    }

    /// <summary>카드 파일과 저장된 데이터가 맞지 않는 곳. 지우기는 되돌릴 수 없으니 한 번 더 묻는다.</summary>
    private void AddDardIssues()
    {
        var issues = _mgr.DardIssues();
        if (issues.Count == 0) return;
        Header($"확인할 것 {issues.Count}개");
        foreach (var issue in issues)
        {
            var i = issue;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            if (i.Kind == DardIssueKind.Duplicate)
            {
                buttons.Children.Add(Button("파일 위치 열기", () => FileOps.Reveal(i.Files[^1])));
            }
            else
            {
                var clear = Button("데이터 지우기", () =>
                {
                    var r = Dialogs.Show("지운 데이터는 되돌릴 수 없어요.", MessageBoxButton.OKCancel, heading: "저장된 데이터를 지울까요?", primary: "지우기");
                    if (r == MessageBoxResult.OK) _mgr.ResolveDardIssue(i, clear: true);
                });
                clear.SetResourceReference(ForegroundProperty, "Danger");
                buttons.Children.Add(clear);
                var keep = Button("그대로 두기", () => _mgr.ResolveDardIssue(i, clear: false));
                keep.Margin = new Thickness(8, 0, 0, 0);
                buttons.Children.Add(keep);
            }
            var row = Row("\uE7BA", i.Title, i.Detail, buttons);
            row.SetResourceReference(Border.BorderBrushProperty, "Danger");
            AddRow(row);
        }
    }

    private void AddWidgets()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "추가할 위젯 고르기",
            Filter = "Desk Cards 위젯 (*.dard)|*.dard",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames)
        {
            if (_mgr.ImportDard(file) is { } error)
                Dialogs.Show(error, heading: $"'{Path.GetFileName(file)}'을 추가하지 못했어요");
        }
    }

    /// <summary>위젯 하나: 켜고 끄기, 카드별 크기·설정, 관리, 권한, 파일 정보.</summary>
    private void BuildWidget(string path)
    {
        var entry = _mgr.DardEntries().First(d => SamePath(d.Path, path));
        Crumb("카드", () => Go(PageKind.Cards));
        Crumb("위젯 카드", () => Go(PageKind.WidgetCards));
        Crumb(entry.Name);

        if (entry.Runtime is { } runtime)
        {
            var pkg = runtime.Package;
            AddRow(Row("", "사용", "끄면 이 위젯의 카드가 모두 사라지고, 다시 켤 때까지 띄우지 않아요.",
                Switch(true, v => _mgr.SetDardEnabled(path, v), quiet: false)));

            var windows = pkg.Cards.Select(info => runtime.WindowFor(info.Id)).OfType<DardWindow>().ToList();
            Header($"카드 {windows.Count}장");
            foreach (var w in windows)
            {
                var card = w;
                AddRow(Row("", card.Info.Name, "크기 · 기본 크기 대비 비율이에요.", Percent(card)));
                if (pkg.SettingsRatio != null)
                    AddRow(Row("", $"{card.Info.Name} 설정", "이 카드가 제공하는 설정 창을 열어요.",
                        Button("열기", () => runtime.OpenSettings(card.Info.Id))));
            }

            Header("관리");
            if (windows.Count > 0)
                AddRow(Row("", "위치 옮기기 · 크기 조절", "편집 막대를 띄우고 이 위젯의 카드를 골라 둬요.",
                    Button("편집", () => _mgr.BeginEditMode(windows[0]))));
            AddRow(Row("", "다시 불러오기", "카드 화면을 처음부터 다시 읽어요. 모양이 이상할 때 써 보세요.",
                Button("다시 불러오기", () => { foreach (var card in windows) card.Reload(); })));
            AddRow(Row("", "파일 위치 열기", path, Button("열기", () => FileOps.Reveal(path))));
            AddWidgetDelete(path);

            Header("권한");
            if (pkg.Permissions.Count == 0)
                AddRow(Row("", "요구하는 권한 없음", "이 위젯은 화면만 그려요.", null));
            foreach (var permission in pkg.PermissionLines)
                AddRow(Row("", permission.Label, permission.Works ? null : "이 버전의 Desk Cards에는 아직 이 기능이 없어서 동작하지 않아요.", null));

            Header("정보");
            AddRow(Row("", "버전", pkg.Version, null));
            AddRow(Row("", "ID", pkg.Id, null));
            return;
        }

        switch (entry.State)
        {
            case DardState.Off:
                AddRow(Row("", "사용", "꺼 둔 위젯이에요. 켜면 추가할지 다시 물어봐요.",
                    Switch(false, v => _mgr.SetDardEnabled(path, v), quiet: false)));
                break;
            case DardState.Asking:
                AddRow(Row("", "추가할지 묻는 중", "열려 있는 확인 창에서 답해 주세요.", null));
                break;
            default:
                AddRow(Row("", "열 수 없는 위젯",
                    "파일이 깨졌거나, 같은 위젯이 이미 떠 있거나, 아직 쓰는 중이에요. 고친 뒤 다시 시도해 주세요.",
                    Button("다시 시도", () => _mgr.RetryDard(path))));
                break;
        }
        Header("관리");
        AddRow(Row("", "파일 위치 열기", path, Button("열기", () => FileOps.Reveal(path))));
        AddWidgetDelete(path);
    }

    private void AddWidgetDelete(string path)
    {
        var delete = Button("삭제", () =>
        {
            if (_mgr.DeleteDardFile(path)) Go(PageKind.WidgetCards);
        });
        delete.SetResourceReference(ForegroundProperty, "Danger");
        AddRow(Row("", "이 위젯 삭제", ".dard 파일을 휴지통으로 옮기고 카드를 모두 치워요.", delete));
    }

    // ----- 공통 -----

    private Border NameBox(CardWindow card)
    {
        var box = new TextBox { Text = card.Group.Name, MaxLength = 80 };
        bool committing = false;
        void Commit()
        {
            if (committing || _closed || !_mgr.Cards.Contains(card)) return;
            string name = box.Text.Trim();
            if (name.Length == 0 || name == card.Group.Name) { box.Text = card.Group.Name; return; }
            committing = true;
            try { if (!_mgr.RenameGroup(card, name)) box.Text = card.Group.Name; }
            finally { committing = false; }
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); Keyboard.ClearFocus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { box.Text = card.Group.Name; Keyboard.ClearFocus(); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => Commit();
        return Field(box, 220);
    }

    /// <summary>카드 크기(%): [−] 숫자 % [+]. 5%씩 움직이고 숫자를 직접 입력할 수도 있다.</summary>
    private UIElement Percent(DeskCard card)
    {
        var box = new TextBox { Text = card.SizePercent.ToString(), MaxLength = 3, TextAlignment = TextAlignment.Right };
        _refreshControls.Add(() => { if (!box.IsKeyboardFocusWithin) box.Text = card.SizePercent.ToString(); });
        void Apply(int percent) => box.Text = card.SetSizePercent(Math.Clamp(percent, 10, 999)).ToString();
        void Commit()
        {
            if (_closed || !_mgr.AllCards.Contains(card)) return;
            if (int.TryParse(box.Text.Trim().TrimEnd('%'), out int v)) Apply(v);
            else box.Text = card.SizePercent.ToString();
        }
        box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        box.GotKeyboardFocus += (_, _) => box.SelectAll();
        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (box.IsKeyboardFocusWithin) return;
            e.Handled = true;
            box.Focus();
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Commit();
            box.SelectAll();
        };
        box.LostKeyboardFocus += (_, _) => Commit();

        var unit = new TextBlock { Text = "%", FontSize = 14, Margin = new Thickness(6, 0, 6, 1), VerticalAlignment = VerticalAlignment.Center };
        unit.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(StepButton("", () =>
        {
            int cur = card.SizePercent;
            Apply(cur % 5 == 0 ? cur - 5 : cur / 5 * 5); // 103 → 100, 100 → 95
        }));
        panel.Children.Add(Field(box, 64));
        panel.Children.Add(unit);
        panel.Children.Add(StepButton("", () => Apply((card.SizePercent / 5 + 1) * 5)));
        return panel;
    }
}
