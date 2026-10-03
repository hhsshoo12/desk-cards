using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using System.Collections.Generic;
using System.Threading.Tasks;
using DeskCards;

internal static partial class Program
{
    private static void SettingsLifecycleTests(string root, Application app)
    {
        Test("settings, edit bar, and expanded window lifecycle", () =>
        {
            var cfg = Config.Load(Path.Combine(root, "windows.json"));
            var mgr = new GroupManager(Path.Combine(root, "window-groups"), cfg);
            try
            {
                mgr.Start();
                var card = mgr.Cards.Single();
                SettingsWindow.Open(mgr, card);
                Pump(100);
                Check(app.Windows.OfType<SettingsWindow>().Count() == 1);
                var settings = app.Windows.OfType<SettingsWindow>().Single();
                settings.Width = settings.MinWidth;
                settings.UpdateLayout();
                Test("settings labels remain readable at minimum window width", () =>
                    Check(Visuals<TextBlock>(settings).Single(t => t.Text == "이름").ActualWidth >= 60));
                Test("card bar settings keep descriptions readable", () =>
                {
                    var go = typeof(SettingsWindow).GetMethod("Go", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var barPage = Enum.Parse(go.GetParameters()[0].ParameterType, "Bar");
                    go.Invoke(settings, new[] { barPage });
                    foreach (double width in new[] { settings.MinWidth, 760.0, 1100.0 })
                    {
                        settings.Width = width;
                        settings.UpdateLayout();
                        Pump(50);
                        settings.UpdateLayout();
                        if (Environment.GetEnvironmentVariable("DESKCARDS_SNAPSHOT_DIR") is { Length: > 0 } dir)
                            Snapshot(settings, Path.Combine(dir, $"bar-{width:0}.png"));
                        var descriptions = Visuals<TextBlock>(settings).Where(t => t.FontSize == 12 && t.Text.Length > 20).ToList();
                        // 버튼이 창 오른쪽 밖으로 잘려 나가지 않는다(선택 버튼이 많으면 다음 줄로 넘어간다).
                        var content = (FrameworkElement)settings.Content;
                        bool inside = Visuals<Button>(settings).Where(b => b.IsVisible)
                            .All(b => b.TranslatePoint(new Point(b.ActualWidth, 0), content).X <= content.ActualWidth - 12);
                        // 짧아서 한 줄에 다 들어가는 설명은 원래 폭이 좁으니 괜찮다. 여러 줄로 꺾였는데 좁으면 문제.
                        bool Readable(TextBlock t) => t.ActualWidth >= 150 || t.ActualHeight <= 20;
                        var narrow = descriptions.Where(t => !Readable(t)).Select(t => $"{t.Text[..10]}={t.ActualWidth:0}");
                        var clipped = Visuals<Button>(settings).Where(b => b.IsVisible && b.TranslatePoint(new Point(b.ActualWidth, 0), content).X > content.ActualWidth - 12).Select(b => b.Content);
                        if (!(descriptions.Count > 0 && descriptions.All(Readable) && inside))
                            throw new Exception($"width {width}: narrow [{string.Join(", ", narrow)}] clipped [{string.Join(", ", clipped)}]");
                    }
                });
                Test("settings page keeps its scroll position when a value change redraws it", () =>
                {
                    var go = typeof(SettingsWindow).GetMethod("Go", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    go.Invoke(settings, new[] { Enum.Parse(go.GetParameters()[0].ParameterType, "Bar") });
                    settings.Width = 760;
                    settings.Height = 460;
                    settings.UpdateLayout();
                    Pump(400);
                    settings.PageScroller.ScrollToEnd();
                    Pump(100);
                    double before = settings.PageScroller.VerticalOffset;
                    mgr.BarDelay = mgr.BarDelay == 500 ? 600 : 500; // 값이 바뀌면 페이지를 다시 그린다
                    Pump(200);
                    double after = settings.PageScroller.VerticalOffset;
                    if (!(before > 0 && Math.Abs(after - before) < 1)) throw new Exception($"offset {before:0} -> {after:0}");
                });
                Test("key combo subpage opens and returns with escape", () =>
                {
                    var go = typeof(SettingsWindow).GetMethod("Go", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var kind = go.GetParameters()[0].ParameterType;
                    go.Invoke(settings, new[] { Enum.Parse(kind, "BarKeys") });
                    settings.Width = 900;
                    settings.UpdateLayout();
                    Pump(50);
                    if (Environment.GetEnvironmentVariable("DESKCARDS_SNAPSHOT_DIR") is { Length: > 0 } dir)
                        Snapshot(settings, Path.Combine(dir, "bar-keys.png"));
                    bool editor = Visuals<KeyComboEditor>(settings).Count() == 1;
                    go.Invoke(settings, new[] { Enum.Parse(kind, "Bar") });
                    Check(editor && !Visuals<KeyComboEditor>(settings).Any());
                });
                mgr.BeginEditMode(card);
                Check(mgr.Editing && mgr.Selected == card && card.IsEditing);
                card.SetGrid(4, 2);
                mgr.EndEditMode();
                ExpandedWindow.Open(card, mgr, editTitle: true);
                Pump(100);
                Check(app.Windows.OfType<ExpandedWindow>().Count() == 1);
                ExpandedWindow.CloseFor(card);
                foreach (var window in app.Windows.OfType<SettingsWindow>().ToArray()) window.Close();
                Pump(100);
                Check(!app.Windows.OfType<ExpandedWindow>().Any() && !app.Windows.OfType<EditBar>().Any());
            }
            finally { mgr.Shutdown(); }
        });
    }
}
