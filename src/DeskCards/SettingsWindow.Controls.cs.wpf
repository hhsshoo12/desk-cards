using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskCards;

internal partial class SettingsWindow
{
    /// <summary>나란히 놓인 버튼 중 하나를 고르는 칸. 고른 것은 강조색, 고를 수 없는 것은 흐리게.</summary>
    private UIElement Choice<T>(IEnumerable<(T value, string text)> items, Func<T> get, Action<T> set, Func<T, bool>? allowed = null)
    {
        // 자리가 모자라면 다음 줄로 넘어간다(창이 좁을 때 버튼이 잘리지 않게).
        var panel = new WrapPanel();
        foreach (var (value, text) in items)
        {
            var v = value;
            bool selected = EqualityComparer<T>.Default.Equals(get(), v);
            var b = Button(text, () => set(v), accent: selected);
            b.Margin = new Thickness(0, 0, 4, 4);
            b.MinWidth = 56;
            b.IsEnabled = allowed?.Invoke(v) ?? true;
            panel.Children.Add(b);
        }
        return panel;
    }

    /// <summary>페이지 제목(이동 경로). 누를 수 있으면 흐린 색으로, 사이에 › 를 넣는다.</summary>
    private void Crumb(string text, Action? click = null)
    {
        if (Breadcrumb.Children.Count > 0)
        {
            var chevron = Glyph("", 16);
            chevron.Margin = new Thickness(12, 6, 12, 0);
            chevron.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");
            Breadcrumb.Children.Add(chevron);
        }
        var t = new TextBlock
        {
            Text = text,
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, Malgun Gothic"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 460,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, click != null ? "SubFg" : "Fg");
        if (click != null)
        {
            t.Cursor = Cursors.Hand;
            t.MouseEnter += (_, _) => t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            t.MouseLeave += (_, _) => t.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");
            t.MouseLeftButtonUp += (_, _) => click();
        }
        Breadcrumb.Children.Add(t);
    }

    private void Header(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, _first ? 0 : 24, 0, 8) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        Page.Children.Add(t);
        _first = false;
    }

    private void AddRow(UIElement row)
    {
        Page.Children.Add(row);
        _first = false;
    }

    /// <summary>
    /// 설정 한 줄: [아이콘] 제목 / 설명 ........ [컨트롤]. click이 있으면 줄 전체를 누를 수 있고 오른쪽에 › 가 붙는다.
    /// </summary>
    private static Border Row(string glyph, string title, string? desc, UIElement? control, Action? click = null,
        UIElement? icon = null, UIElement? detail = null)
    {
        var grid = new Grid { MinHeight = 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var head = icon ?? Glyph(glyph, 18);
        if (head is FrameworkElement fe)
        {
            fe.HorizontalAlignment = HorizontalAlignment.Left;
            fe.VerticalAlignment = VerticalAlignment.Center;
        }
        grid.Children.Add(head);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var t = new TextBlock { Text = title, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        texts.Children.Add(t);
        if (!string.IsNullOrEmpty(desc))
        {
            var d = new TextBlock { Text = desc, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
            d.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");
            texts.Children.Add(d);
        }
        if (detail != null) texts.Children.Add(detail); // 설명 자리에 글씨 대신 넣을 것(게이지 등)
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        UIElement? right = control;
        if (click != null)
        {
            var chevron = Glyph("", 12);
            chevron.Margin = new Thickness(8, 0, 4, 0);
            right = chevron;
        }
        if (right != null)
        {
            if (right is FrameworkElement r) r.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
            if (control != null && click == null)
            {
                // 첫 줄이 남는 높이를 받아야 최소 높이(44)보다 내용이 작을 때 가운데 온다(둘 다 Auto면 위로 쏠린다).
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                bool? stacked = null;
                grid.SizeChanged += (_, _) =>
                {
                    // 오른쪽 컨트롤을 놓고도 글씨 자리가 넉넉히(240) 남지 않으면 컨트롤을 글씨 아래로 내린다.
                    // 버튼이 여러 개인 선택 칸처럼 넓은 컨트롤은 창이 넓어도 아래로 간다.
                    control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    bool narrow = grid.ActualWidth - 40 - control.DesiredSize.Width < 240;
                    if (stacked == narrow) return;
                    stacked = narrow;
                    Grid.SetColumnSpan(texts, narrow ? 2 : 1);
                    Grid.SetRow(control, narrow ? 1 : 0);
                    Grid.SetColumn(control, narrow ? 1 : 2);
                    Grid.SetColumnSpan(control, narrow ? 2 : 1);
                    if (control is FrameworkElement field)
                    {
                        field.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                        field.Margin = new Thickness(0, narrow ? 10 : 0, 0, 0);
                    }
                };
            }
        }

        var row = RowBox(grid, new Thickness(16, 11, 16, 11));
        if (click != null)
        {
            row.Cursor = Cursors.Hand;
            HoverFade(row, () => "RowBg");
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        }
        return row;
    }

    /// <summary>설정 행과 설명 칸에 쓰는 공통 테두리. 안쪽 여백은 내용마다 정한다.</summary>
    private static Border RowBox(UIElement content, Thickness padding)
    {
        var box = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = padding,
            Margin = new Thickness(0, 0, 0, 4),
            Child = content,
        };
        box.SetResourceReference(Border.BackgroundProperty, "RowBg");
        box.SetResourceReference(Border.BorderBrushProperty, "RowBorder");
        return box;
    }

    private Button Button(string text, Action act, bool accent = false)
    {
        var b = new Button { Content = text, Style = (Style)FindResource(accent ? "AccentButton" : "StdButton") };
        b.Click += (_, _) => act();
        return b;
    }

    /// <summary>
    /// 켬/끔 스위치. 동그라미는 끔 ↔ 켬 사이를 167ms 동안 미끄러진다.
    /// quiet면 누른 뒤 페이지를 다시 그리지 않아서 미끄러지는 게 보인다. 값에 따라 페이지 모양이 바뀌면 false로 둔다.
    /// </summary>
    private CheckBox Switch(bool value, Action<bool> set, bool quiet = true)
    {
        const double Travel = 20; // 트랙 40 - 여백 4×2 - 동그라미 12 (마우스를 올려 14로 커져도 여백이 3이라 같다)
        var s = new CheckBox { IsChecked = value, Style = (Style)FindResource("Switch") };
        var shift = new TranslateTransform(value ? Travel : 0, 0);
        s.Loaded += (_, _) =>
        {
            if (s.Template.FindName("Knob", s) is FrameworkElement knob) knob.RenderTransform = shift;
        };
        void Slide() => shift.BeginAnimation(TranslateTransform.XProperty, Motion.In(null, s.IsChecked == true ? Travel : 0, Motion.Fast));
        s.Checked += (_, _) => Slide();
        s.Unchecked += (_, _) => Slide();
        s.Click += (_, _) =>
        {
            _quiet = quiet;
            try { set(s.IsChecked == true); }
            finally { _quiet = false; }
        };
        return s;
    }

    /// <summary>
    /// 배경을 key 색으로 83ms 동안 바꾼다(null = 투명). 테마 색은 직접 움직일 수 없어 잠깐 새 붓으로 칠하고,
    /// 다 바뀌면 다시 테마 리소스를 따르게 한다.
    /// </summary>
    private static void FadeBackground(Border b, string? key, double ms = Motion.Faster)
    {
        var from = (b.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
        var to = key != null && b.TryFindResource(key) is SolidColorBrush target ? target.Color : Color.FromArgb(0, from.R, from.G, from.B);
        if (from.A == 0) from = Color.FromArgb(0, to.R, to.G, to.B); // 투명에서 시작할 때 엉뚱한 색을 거치지 않게
        var brush = new SolidColorBrush(from);
        b.Background = brush;
        var anim = new System.Windows.Media.Animation.ColorAnimation(to, TimeSpan.FromMilliseconds(Motion.Ms(ms)));
        anim.Completed += (_, _) =>
        {
            if (b.Background != brush) return;
            if (key != null) b.SetResourceReference(Border.BackgroundProperty, key);
            else b.Background = Brushes.Transparent;
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    /// <summary>마우스를 올리면 hover 색으로, 떼면 rest()가 돌려주는 색으로 부드럽게 바꾼다.</summary>
    private static void HoverFade(Border b, Func<string?> rest, string hover = "HoverBg")
    {
        b.MouseEnter += (_, _) => FadeBackground(b, hover);
        b.MouseLeave += (_, _) => FadeBackground(b, rest());
    }

    /// <summary>설명 글씨(흐린 12pt). 나중에 글을 바꿔야 하는 줄에서 Row의 detail로 넘긴다.</summary>
    private static TextBlock Desc(string text)
    {
        var d = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
        d.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");
        return d;
    }

    private static TextBlock Glyph(string glyph, double size)
    {
        var t = new TextBlock { Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        return t;
    }

    /// <summary>둥근 입력 칸. 입력 중에는 아래쪽에 강조색 줄이 생긴다.</summary>
    private static Border Field(TextBox box, double width)
    {
        box.FontSize = 14;
        box.Background = Brushes.Transparent;
        box.BorderThickness = new Thickness(0);
        box.Padding = new Thickness(8, 4, 8, 5);
        box.VerticalContentAlignment = VerticalAlignment.Center;
        box.SetResourceReference(TextBox.ForegroundProperty, "Fg");
        box.SetResourceReference(TextBox.CaretBrushProperty, "Fg");
        var line = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(0, 0, 4, 4), Visibility = Visibility.Hidden };
        line.SetResourceReference(Border.BackgroundProperty, "Accent");
        box.GotKeyboardFocus += (_, _) => line.Visibility = Visibility.Visible;
        box.LostKeyboardFocus += (_, _) => line.Visibility = Visibility.Hidden;
        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(line);
        var field = new Border { Width = width, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Child = grid };
        field.SetResourceReference(Border.BackgroundProperty, "ControlBg");
        field.SetResourceReference(Border.BorderBrushProperty, "ControlBorder");
        return field;
    }

    /// <summary>[−] 값 [+] 로 min~max(기본 칸 수 1~8)를 고른다. format이 있으면 값을 그 모양으로 보여 준다.</summary>
    private UIElement Stepper(Func<int> get, Action<int> set,
        int min = CardLayout.MinCells, int max = CardLayout.MaxCells, Func<int, string>? format = null)
    {
        format ??= v => v.ToString();
        var num = new TextBlock { Text = format(get()), FontSize = 14, MinWidth = 32, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _refreshControls.Add(() => num.Text = format(get()));
        num.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        void Step(int delta)
        {
            int next = Math.Clamp(get() + delta, min, max);
            if (next == get()) return;
            set(next);
            num.Text = format(get());
        }
        // 마우스를 올리고 휠을 굴리면 한 칸(120)마다 한 단계씩 바뀐다. 터치패드의 작은 값은 모아서 센다.
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Tag = SmoothScroll.WheelOwner };
        int wheel = 0;
        panel.MouseWheel += (_, e) =>
        {
            e.Handled = true;
            wheel += e.Delta;
            while (Math.Abs(wheel) >= 120)
            {
                Step(wheel > 0 ? +1 : -1);
                wheel -= Math.Sign(wheel) * 120;
            }
        };
        panel.MouseLeave += (_, _) => wheel = 0;
        panel.Children.Add(StepButton("", () => Step(-1)));
        panel.Children.Add(num);
        panel.Children.Add(StepButton("", () => Step(+1)));
        return panel;
    }

    private Button StepButton(string glyph, Action act)
    {
        var b = new Button { Style = (Style)FindResource("IconButton"), Content = Glyph(glyph, 12) };
        b.Click += (_, _) => act();
        return b;
    }

    // ----- 기타 -----

    private static BitmapSource? LoadIcon(int size)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/app.ico"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - size)).First();
        }
        catch
        {
            return null;
        }
    }
}
