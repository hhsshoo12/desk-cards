using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace DeskCards;

internal sealed class CardImportWindow : Window
{
    private readonly TextBox _name = new() { Text = "새 카드" };
    private readonly TextBox _html = new();
    private readonly TextBox _python = new();
    private readonly TextBox _width = new() { Text = "200" };
    private readonly TextBox _height = new() { Text = "200" };
    private readonly ComboBox _group = new();
    private readonly CardStore _store = new(AppPaths.CardsDir);
    public InstalledCard? Result { get; private set; }
    public string Group => _group.SelectedItem as string ?? "";
    public string PythonSource => _python.Text.Trim();

    public CardImportWindow(string[] groups, string group)
    {
        Title = "카드 만들기"; Width = 520; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; ResizeMode = ResizeMode.NoResize;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Malgun Gothic");
        SetResourceReference(BackgroundProperty, "SettingsBg");
        SetResourceReference(ForegroundProperty, "Fg");
        var body = new StackPanel { Margin = new Thickness(24) }; Content = body;
        var modes = new WrapPanel(); body.Children.Add(modes);
        AddButton(modes, "폴더 선택", () => Folder(false));
        AddButton(modes, "zip / .webCard", Archive);
        AddButton(modes, "연결 모드", () => Folder(true));
        body.Children.Add(new TextBlock { Text = "직접 선택", FontSize = 18, Margin = new Thickness(0, 20, 0, 8) });
        Field(body, "이름", _name);
        FileField(body, "HTML 파일", _html, "HTML (*.html;*.htm)|*.html;*.htm");
        FileField(body, "Python 파일 (선택)", _python, "Python (*.py)|*.py");
        Field(body, "처음 가로 크기 (DIP)", _width); Field(body, "처음 세로 크기 (DIP)", _height);
        foreach (string name in groups) _group.Items.Add(name);
        _group.SelectedItem = group; if (_group.SelectedIndex < 0 && _group.Items.Count > 0) _group.SelectedIndex = 0;
        Field(body, "그룹", _group);
        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        AddButton(actions, "직접 선택한 파일로 만들기", Direct); AddButton(actions, "취소", Close); body.Children.Add(actions);
        SourceInitialized += (_, _) => Hwnd.ApplyFluent(this, Theme.HasBackdrop ? Hwnd.Backdrop.Mica : Hwnd.Backdrop.None, roundCorners: true);
    }
    private static void AddButton(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        button.Click += (_, _) => action(); panel.Children.Add(button);
    }
    private static void Field(Panel panel, string label, Control input)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 4) });
        input.MinHeight = 30; panel.Children.Add(input);
    }
    private static void FileField(Panel panel, string label, TextBox input, string filter)
    {
        Field(panel, label, input);
        var button = new Button { Content = "파일 선택", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        button.Click += (_, _) => { var picker = new Microsoft.Win32.OpenFileDialog { Filter = filter }; if (picker.ShowDialog() == true) input.Text = picker.FileName; };
        panel.Children.Add(button);
    }
    private void Complete(Func<InstalledCard> import)
    {
        try { Result = import(); DialogResult = true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CardManifestException or ArgumentException)
        { Dialogs.Show(ex.Message, heading: "카드를 가져오지 못했어요"); }
    }
    private void Folder(bool linked)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "카드 폴더 선택" };
        if (picker.ShowDialog(this) == true) Complete(() => _store.ImportFolder(picker.FolderName, linked));
    }
    private void Archive()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "카드 압축 파일 (*.zip;*.webCard)|*.zip;*.webCard" };
        if (picker.ShowDialog(this) == true) Complete(() => _store.ImportArchive(picker.FileName));
    }
    private void Direct()
    {
        if (!double.TryParse(_width.Text, out double w) || !double.TryParse(_height.Text, out double h) ||
            !double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0 || string.IsNullOrWhiteSpace(_name.Text))
        { Dialogs.Show("이름과 0보다 큰 가로·세로 크기를 입력해 주세요."); return; }
        Complete(() =>
        {
            string staging = Path.Combine(Path.GetTempPath(), "DeskCards-direct-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(new { id = "local." + Guid.NewGuid().ToString("N"), name = _name.Text.Trim(), size = new[] { w, h } }));
                File.Copy(_html.Text.Trim(), Path.Combine(staging, "card.html"));
                if (_python.Text.Trim().Length > 0) File.Copy(_python.Text.Trim(), Path.Combine(staging, "main.py"));
                return _store.ImportFolder(staging) with { Source = Path.GetFullPath(_html.Text.Trim()) };
            }
            finally { Directory.Delete(staging, true); }
        });
    }
}
