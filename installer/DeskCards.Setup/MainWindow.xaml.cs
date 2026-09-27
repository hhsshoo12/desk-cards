using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace DeskCards.Setup;

internal partial class MainWindow : Window
{
    private readonly SetupEnvironment _env;
    private readonly SetupEngine _engine;
    private readonly ReleaseService _releases;
    private readonly string _setupExe;
    private readonly bool _uninstall;
    private readonly string? _tempCopy;
    private readonly InstallOptions _options;
    private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
    private AppRelease? _release;
    private int _page;
    private bool _busy, _committing, _complete, _closed, _retry, _failed, _finalized;
    private bool _removeConfig, _launch = true;
    private ProgressBar? _progress;
    private TextBlock? _transfer;

    public MainWindow(SetupEnvironment env, SetupEngine engine, ReleaseService releases, string setupExe, bool uninstall, string? tempCopy = null)
    {
        _env = env; _engine = engine; _releases = releases; _setupExe = setupExe; _uninstall = uninstall; _tempCopy = tempCopy;
        _options = engine.Registration.ReadOptions();
        InitializeComponent();
        Title = uninstall ? "Desk Cards 제거" : "Desk Cards 설치";
        SourceInitialized += (_, _) => WindowEffects.Apply(new WindowInteropHelper(this).Handle, Theme.IsLight);
        Closing += OnClosing;
        Closed += (_, _) => { _closed = true; _cancel.Cancel(); };
        Loaded += async (_, _) => { if (!_uninstall) await CheckVersion(); };
        Render();
    }

    private async Task CheckVersion()
    {
        _failed = false; _busy = true; Render(); Description.Text = "최신 버전을 확인하고 있어요…";
        try { _release = await _releases.FindAsync(_cancel.Token); }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { _busy = false; Close(); return; }
        catch (SetupFailure ex) { _busy = false; ShowFailure(ex); return; }
        finally { _busy = false; }
        if (!_closed) Render();
    }

    private void Render()
    {
        PageContent.Children.Clear(); LogButton.Visibility = Visibility.Collapsed;
        string[] steps = _uninstall ? new[] { "시작", "제거", "완료" } : new[] { "시작", "추가 작업", "설치 준비", "설치", "완료" };
        Steps.Children.Clear();
        for (int i = 0; i < steps.Length; i++)
        {
            var item = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 9, 6, 9), Margin = new Thickness(0, 2, 0, 2) };
            if (i == _page) item.SetResourceReference(BackgroundProperty, "RowBg");
            var label = new TextBlock { Text = (i < _page ? "✓  " : (i + 1) + "  ") + steps[i], FontWeight = i == _page ? FontWeights.SemiBold : FontWeights.Normal };
            label.SetResourceReference(TextBlock.ForegroundProperty, i == _page ? "Accent" : "SubFg"); item.Child = label; Steps.Children.Add(item);
        }
        bool update = _engine.Registration.IsUpdate;
        ModeLabel.Text = _uninstall ? "제거" : update ? "업데이트" : "설치";
        BackButton.Visibility = !_uninstall && _page > 0 && _page < 3 && !_busy ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _busy ? Visibility.Collapsed : Visibility.Visible;
        NextButton.IsEnabled = !_busy;
        CancelButton.IsEnabled = !_committing;
        CancelButton.Content = "취소";
        CancelButton.Visibility = _complete ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = _complete ? "마침" : _uninstall ? "제거" : _page == 2 ? update ? "업데이트" : "설치" : "다음";
        if (_complete)
        {
            Heading.Text = _uninstall ? "제거를 마쳤어요" : "준비가 끝났어요";
            Description.Text = _uninstall ? "Desk Cards를 제거했어요. 그룹 폴더와 그 안의 파일은 그대로 남아 있어요." : "Desk Cards를 사용할 수 있어요.";
            if (!_uninstall) AddCheck("Desk Cards 실행", _launch, value => _launch = value);
            return;
        }
        if (_uninstall)
        {
            Heading.Text = _busy ? "제거하고 있어요" : "Desk Cards를 제거할까요?";
            Description.Text = _busy ? "잠시만 기다려 주세요." : "그룹 폴더(%USERPROFILE%\\DeskCards)와 그 안의 파일은 지우지 않아요.";
            if (!_busy) AddCheck("카드 위치·크기 설정도 지우기", _removeConfig, value => _removeConfig = value);
            return;
        }
        switch (_page)
        {
            case 0:
                Heading.Text = update ? "Desk Cards 업데이트" : "바탕화면을 정리해 보세요";
                Description.Text = "앱과 바로가기를 카드로 묶어 보세요.\n인터넷에서 최신 버전을 내려받아요.";
                AddParagraph("설치 위치\n" + _env.InstallDir + "\n\n필요한 공간: 약 80MB");
                if (update) AddParagraph("그룹과 카드 위치·크기 설정은 그대로 남아요.");
                if (_release != null)
                {
                    AddParagraph(update ? "설치된 버전 " + _engine.Registration.InstalledVersion + " → 최신 버전 " + _release.Version : "최신 버전 " + _release.Version);
                    int? comparison = ReleaseService.CompareInstalled(_engine.Registration.InstalledVersion, _release.Version);
                    if (update && comparison == 0) { Description.Text = "이미 최신 버전이에요"; NextButton.Content = "다시 설치"; }
                    if (update && comparison > 0)
                    { Description.Text = "설치된 버전(" + _engine.Registration.InstalledVersion + ")이 더 새로워요"; NextButton.Visibility = Visibility.Collapsed; CancelButton.Content = "닫기"; }
                }
                else NextButton.IsEnabled = false;
                break;
            case 1:
                Heading.Text = "추가 작업을 선택하세요"; Description.Text = "사용하기 편한 방법으로 설정해 주세요.";
                AddCheck("시작 메뉴 바로가기", _options.StartMenu, value => _options.StartMenu = value);
                AddCheck("바탕화면 바로가기", _options.Desktop, value => _options.Desktop = value);
                AddCheck("Windows 시작 시 실행", _options.AutoStart, value => _options.AutoStart = value);
                break;
            case 2:
                Heading.Text = update ? "업데이트할 준비가 됐어요" : "설치할 준비가 됐어요";
                Description.Text = "선택한 내용을 확인해 주세요.";
                AddParagraph("설치 위치\n" + _env.InstallDir + "\n\n시작 메뉴 바로가기: " + OnOff(_options.StartMenu) +
                    "\n바탕화면 바로가기: " + OnOff(_options.Desktop) + "\nWindows 시작 시 실행: " + OnOff(_options.AutoStart));
                break;
            case 3:
                Heading.Text = update ? "업데이트하고 있어요" : "설치하고 있어요";
                Description.Text = "다운로드를 준비하고 있어요.";
                _progress = new ProgressBar { Minimum = 0, Maximum = 100, Margin = new Thickness(0, 20, 0, 12) };
                _transfer = new TextBlock { TextWrapping = TextWrapping.Wrap }; _transfer.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");
                PageContent.Children.Add(_progress); PageContent.Children.Add(_transfer); break;
        }
    }

    private static string OnOff(bool value) => value ? "켬" : "끔";
    private void AddParagraph(string text) { var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 22, Margin = new Thickness(0, 0, 0, 16) }; PageContent.Children.Add(block); }
    private void AddCheck(string text, bool initial, Action<bool> change)
    {
        var check = new CheckBox { Content = text, IsChecked = initial };
        check.Checked += (_, _) => change(true); check.Unchecked += (_, _) => change(false); PageContent.Children.Add(check);
    }
    private void Back(object sender, RoutedEventArgs e) { _page--; Render(); }
    private async void Next(object sender, RoutedEventArgs e)
    {
        if (_retry) { _retry = false; await CheckVersion(); return; }
        if (_complete)
        {
            try
            {
                if (_uninstall) FinishRemoval();
                else if (_launch) Process.Start(new ProcessStartInfo(_env.AppExe) { UseShellExecute = true, WorkingDirectory = _env.InstallDir });
                Close();
            }
            catch (Exception ex) { new SetupLog(_env).Write("마침 실패", ex); ShowFailure(new SetupFailure("마무리하지 못했어요. 로그를 확인해 주세요.", false, ex)); }
            return;
        }
        if (!_uninstall && _page < 2) { _page++; Render(); return; }
        await Run();
    }

    private async Task Run()
    {
        _busy = true; _committing = _uninstall; _page = _uninstall ? 1 : 3; Render();
        Action<string> status = text => Dispatcher.Invoke(() => { if (!_closed) Description.Text = text + "…"; });
        try
        {
            if (_uninstall) await Task.Run(() => _engine.Uninstall(_removeConfig, status));
            else
            {
                var progress = new Progress<TransferProgress>(p =>
                {
                    if (_closed || _transfer == null || _progress == null) return;
                    _transfer.Text = string.Format("{0:F1} MB / {1} MB · {2:F1} MB/s", p.Received / 1048576.0, p.Total.HasValue ? (p.Total.Value / 1048576.0).ToString("F1") : "?", p.BytesPerSecond / 1048576.0);
                    _progress.Value = p.Total > 0 ? 100.0 * p.Received / p.Total.Value : 0;
                });
                await Task.Run(() => _engine.InstallAsync(_release!, _options, _setupExe, progress, status,
                    () => Dispatcher.Invoke(() => { _committing = true; CancelButton.IsEnabled = false; }), _cancel.Token));
            }
            _complete = true; _page = _uninstall ? 2 : 4;
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { _busy = false; _committing = false; Close(); return; }
        catch (Exception ex) { _busy = false; _committing = false; ShowFailure(ex as SetupFailure ?? new SetupFailure("작업을 완료하지 못했어요.", false, ex)); return; }
        finally { _busy = false; _committing = false; }
        Render();
    }

    private void ShowFailure(SetupFailure failure)
    {
        _failed = true; _retry = failure.Retry && _page == 0 && !_uninstall;
        Heading.Text = "진행할 수 없어요"; Description.Text = failure.Message;
        PageContent.Children.Clear(); LogButton.Visibility = Visibility.Visible;
        NextButton.Visibility = _retry ? Visibility.Visible : Visibility.Collapsed; NextButton.IsEnabled = true; NextButton.Content = "다시 시도";
        BackButton.Visibility = Visibility.Collapsed; CancelButton.Visibility = Visibility.Visible; CancelButton.IsEnabled = true; CancelButton.Content = "닫기";
    }

    private void Cancel(object sender, RoutedEventArgs e) { Close(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_committing) { e.Cancel = true; return; }
        if (_busy) { e.Cancel = true; _cancel.Cancel(); CancelButton.IsEnabled = false; Description.Text = "취소하고 있어요…"; return; }
        if (_complete && _uninstall && !_failed)
        {
            try { FinishRemoval(); }
            catch (Exception ex) { new SetupLog(_env).Write("종료 정리 실패", ex); }
        }
    }
    private void FinishRemoval()
    {
        if (_finalized) return;
        _engine.FinishUninstall();
        if (_tempCopy != null) Cleanup.Schedule(_tempCopy);
        _finalized = true;
    }
    private void OpenLog(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_env.LogPath) { UseShellExecute = true }); }
        catch (Exception ex) { Description.Text = "로그 파일을 열지 못했어요: " + _env.LogPath; new SetupLog(_env).Write("로그 열기 실패", ex); }
    }
}
