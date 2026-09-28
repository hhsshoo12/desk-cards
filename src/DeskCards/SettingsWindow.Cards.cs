using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeskCards;

internal partial class SettingsWindow
{
    private string CardsSignature() => string.Join("|", _mgr.Cards.Select(c =>
        $"{c.Group.Name}/{c.Group.Items.Count}/{c.CurrentLayout.Cols}x{c.CurrentLayout.Rows}/{c.SizePercent}"));

    private void BuildCards()
    {
        _cardsSignature = CardsSignature();
        Crumb("카드");
        AddRow(Row("", "카드 편집",
            "화면 위쪽에 편집 막대가 떠요. 카드를 끌어 옮기고, 오른쪽 아래 모서리로 크기를 바꾸고, 골라서 지울 수 있어요.",
            Button("편집 시작", () => _mgr.BeginEditMode(), accent: true)));
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
            if (_mgr.DeleteGroup(card)) Go(PageKind.Cards);
        });
        delete.SetResourceReference(ForegroundProperty, "Danger");
        AddRow(Row("", "이 그룹 삭제", "안에 있는 항목은 바탕화면으로 옮겨져요.", delete));
    }

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
    private UIElement Percent(CardWindow card)
    {
        var box = new TextBox { Text = card.SizePercent.ToString(), MaxLength = 3, TextAlignment = TextAlignment.Right };
        _refreshControls.Add(() => { if (!box.IsKeyboardFocusWithin) box.Text = card.SizePercent.ToString(); });
        void Apply(int percent) => box.Text = card.SetSizePercent(Math.Clamp(percent, 10, 999)).ToString();
        void Commit()
        {
            if (_closed || !_mgr.Cards.Contains(card)) return;
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
