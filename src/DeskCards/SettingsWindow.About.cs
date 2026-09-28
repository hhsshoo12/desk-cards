using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeskCards;

internal partial class SettingsWindow
{
    private void BuildAbout()
    {
        Crumb("정보");
        var icon = new Image { Source = LoadIcon(32), Width = 24, Height = 24 };
        AddRow(UpdateRow(icon));

        Header("개발 정보");
        AddRow(Row("", "개발자", "hhsshoo12", Button("프로필", () => OpenUrl(DeveloperUrl))));
        AddRow(Row("", "함께 만든 AI", "Claude (Anthropic) · Codex (OpenAI)", null));
        AddRow(Row("", "저작권", "Copyright © 2026 hhsshoo12. All rights reserved.", null));
        AddRow(Row("", "사용한 라이브러리", ".NET 10 · WPF · Windows Forms · WebView2", null, () => Go(PageKind.Libraries)));

        Header("링크");
        AddRow(Row("", "GitHub", RepoUrl, Button("열기", () => OpenUrl(RepoUrl))));
        AddRow(Row("", "설정 폴더", AppPaths.ConfigDir, Button("열기", () => FileOps.OpenFolder(AppPaths.ConfigDir))));
    }

    /// <summary>앱과 설치기가 쓰는 라이브러리·구성 요소와 그 라이선스.</summary>
    private void BuildLibraries()
    {
        Crumb("정보", () => Go(PageKind.About));
        Crumb("사용한 라이브러리");

        Header("앱에 포함");
        AddRow(Row("", ".NET 10 런타임", "MIT 라이선스 · .NET Foundation, Microsoft",
            Button("라이선스", () => OpenUrl("https://github.com/dotnet/runtime/blob/main/LICENSE.TXT"))));
        AddRow(Row("", "WPF (Windows Presentation Foundation)", "MIT 라이선스 · .NET Foundation, Microsoft",
            Button("라이선스", () => OpenUrl("https://github.com/dotnet/wpf/blob/main/LICENSE.TXT"))));
        AddRow(Row("", "Windows Forms", "MIT 라이선스 · .NET Foundation, Microsoft (트레이 아이콘)",
            Button("라이선스", () => OpenUrl("https://github.com/dotnet/winforms/blob/main/LICENSE.TXT"))));
        AddRow(Row("", "Microsoft Edge WebView2 SDK", "BSD 3조항 라이선스 · Microsoft (.dard 카드 화면)",
            Button("라이선스", () => OpenUrl("https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3719.77/License"))));
        AddRow(Row("", "C#/WinRT · Windows SDK 형식", "MIT 라이선스 · Microsoft (WebView2 합성 화면이 사용)",
            Button("라이선스", () => OpenUrl("https://github.com/microsoft/CsWinRT/blob/master/LICENSE"))));
        AddRow(Row("", ".NET 런타임에 포함된 제3자 구성 요소", "런타임이 함께 싣고 있는 오픈 소스 목록이에요.",
            Button("목록", () => OpenUrl("https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT"))));

        Header("Windows 구성 요소 (앱에 포함하지 않음)");
        AddRow(Row("", "Segoe Fluent Icons", "아이콘 글꼴 · Windows 11 기본 글꼴", null));
        AddRow(Row("", "Microsoft Edge WebView2 런타임", ".dard 카드 화면 · Windows 11 기본 구성 요소", null));
        AddRow(Row("", ".NET Framework 4.8", "설치기가 사용 · Windows 기본 구성 요소", null));
    }

    /// <summary>
    /// 앱 이름 줄: 버전(새 버전이 있으면 "현재 → 최신")과 오른쪽 버튼.
    /// 버튼은 최신(흰색, 누를 수 없음) → 업데이트(파란색) → 받는 동안 글씨 자리에 게이지 → 준비 완료 → 앱 재시작(파란색).
    /// </summary>
    private Border UpdateRow(UIElement icon)
    {
        var u = Updater.Instance;
        var text = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };

        var fill = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        var rest = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        var track = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center };
        track.ColumnDefinitions.Add(fill);
        track.ColumnDefinitions.Add(rest);
        var trackBg = new Border { CornerRadius = new CornerRadius(2) };
        trackBg.SetResourceReference(Border.BackgroundProperty, "ControlBorder");
        Grid.SetColumnSpan(trackBg, 2);
        var bar = new Border { CornerRadius = new CornerRadius(2) };
        bar.SetResourceReference(Border.BackgroundProperty, "Accent");
        track.Children.Add(trackBg);
        track.Children.Add(bar);
        var percent = new TextBlock { FontSize = 12, MinWidth = 36, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        percent.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");
        var gauge = new Grid { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 1) };
        gauge.ColumnDefinitions.Add(new ColumnDefinition());
        gauge.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(percent, 1);
        gauge.Children.Add(track);
        gauge.Children.Add(percent);

        var detail = new Grid();
        detail.Children.Add(text);
        detail.Children.Add(gauge);

        // 확인이 금방 끝나도 누른 게 보이도록 로딩 동그라미를 잠깐은 보여 준다.
        // (시각을 비교하면 타이머가 몇 ms 일찍 울릴 때 동그라미가 꺼지지 않으므로 켜짐/꺼짐으로 둔다.)
        bool spinHold = false;
        var spinTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        spinTimer.Tick += (_, _) => { spinTimer.Stop(); spinHold = false; _updateRefresh?.Invoke(); };
        var spinner = Spinner();

        var button = new Button();
        button.Click += (_, _) =>
        {
            switch (u?.State)
            {
                case UpdateState.Available: _ = u.DownloadAsync(); break;
                case UpdateState.Ready: u.Restart(); break;
                case UpdateState.Idle or UpdateState.UpToDate or UpdateState.Failed:
                    spinHold = true;
                    spinTimer.Start();
                    _ = u.CheckAsync();
                    break;
            }
        };

        void Refresh()
        {
            var state = u?.State ?? UpdateState.UpToDate;
            var current = u?.Current ?? Updater.RunningVersion;
            string version = u?.Latest is { } latest && latest > current
                ? $"현재 버전 {current.ToString(3)} → 최신 버전 {latest.ToString(3)}"
                : $"버전 {current.ToString(3)}";
            bool downloading = state == UpdateState.Downloading;
            gauge.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
            text.Visibility = downloading ? Visibility.Collapsed : Visibility.Visible;
            double p = Math.Clamp(u?.Progress ?? 0, 0, 1);
            fill.Width = new GridLength(p, GridUnitType.Star);
            rest.Width = new GridLength(1 - p, GridUnitType.Star);
            percent.Text = $"{Math.Round(p * 100)}%";

            // 버전 0.2.3  최신  260927-2033 (최신만 굵게)
            text.Inlines.Clear();
            switch (state)
            {
                case UpdateState.Preparing: text.Inlines.Add(new System.Windows.Documents.Run("업데이트 준비 중…")); break;
                case UpdateState.Ready: text.Inlines.Add(new System.Windows.Documents.Run("업데이트 준비 완료")); break;
                default:
                    text.Inlines.Add(new System.Windows.Documents.Run(version));
                    if (state == UpdateState.UpToDate)
                    {
                        text.Inlines.Add(new System.Windows.Documents.Run("  "));
                        text.Inlines.Add(new System.Windows.Documents.Run("최신") { FontWeight = FontWeights.Bold });
                        if (u?.LastChecked is { } at)
                            text.Inlines.Add(new System.Windows.Documents.Run("  " + at.ToString("yyMMdd-HHmm")));
                    }
                    else if (state == UpdateState.Failed)
                        text.Inlines.Add(new System.Windows.Documents.Run(" · " + u?.Error));
                    break;
            }
            text.SetResourceReference(TextBlock.ForegroundProperty, state == UpdateState.Ready ? "Accent" : "SubFg");

            bool spinning = state == UpdateState.Checking || spinHold;
            (string label, bool accent, bool enabled) = state switch
            {
                _ when spinning => ("", false, false),
                UpdateState.Available when u?.CanUpdate != true => ("개발 빌드", false, false),
                UpdateState.Available => ("업데이트", true, true),
                UpdateState.Downloading => ("받는 중", false, false),
                UpdateState.Preparing => ("준비 중", false, false),
                UpdateState.Ready => ("앱 재시작", true, true),
                _ => ("버전 확인", false, u != null),
            };
            button.Content = spinning ? spinner : label;
            button.Style = (Style)FindResource(accent ? "AccentButton" : "StdButton");
            button.IsEnabled = enabled;
            if (enabled) button.ClearValue(ForegroundProperty);
            else button.SetResourceReference(ForegroundProperty, "SubFg");
        }

        _updateRefresh = Refresh;
        Refresh();
        return Row("", "Desk Cards", null, button, null, icon, detail);
    }

    /// <summary>Windows 11의 로딩 동그라미(ProgressRing)처럼 도는 호.</summary>
    private static FrameworkElement Spinner()
    {
        var ring = new System.Windows.Shapes.Ellipse
        {
            Width = 16,
            Height = 16,
            StrokeThickness = 2,
            StrokeDashCap = PenLineCap.Round,
            StrokeDashArray = new DoubleCollection { 7, 100 }, // 둘레의 1/3쯤만 그린다
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        ring.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Accent");
        var rotate = new RotateTransform();
        ring.RenderTransform = rotate;
        rotate.BeginAnimation(RotateTransform.AngleProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9))
            { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        return ring;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 기본 브라우저가 없으면 무시 */ }
    }
}
