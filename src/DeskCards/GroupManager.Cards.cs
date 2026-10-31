using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DeskCards;

internal sealed partial class GroupManager
{
    private readonly HashSet<string> _cardLoadErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BrowserRecoveryPolicy> _backendRecovery = new(StringComparer.OrdinalIgnoreCase);

    public void ReimportWebCard(WebCardWindow card)
    {
        var entry = _cfg.Cards.FirstOrDefault(c => c.Directory == card.Runtime.Package.Path);
        if (entry == null) return;
        if (entry.Linked) { RestartImportedCard(entry.Directory, true); return; }
        if (Dialogs.Show("내부 카드 폴더와 저장된 데이터를 원본으로 교체해요.", MessageBoxButton.OKCancel,
            heading: "카드를 다시 가져올까요?", primary: "다시 가져오기") != MessageBoxResult.OK) return;
        string staging = Path.Combine(AppPaths.CardsDir, ".reimport-" + Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(staging, "previous");
        Directory.CreateDirectory(staging);
        bool moved = false;
        try
        {
            var store = new CardStore(Path.Combine(staging, "new"));
            InstalledCard replacement;
            if (Directory.Exists(entry.Source)) replacement = store.ImportFolder(entry.Source);
            else if (Path.GetExtension(entry.Source).Equals(".html", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(entry.Source).Equals(".htm", StringComparison.OrdinalIgnoreCase))
            {
                string direct = Path.Combine(staging, "direct"); Directory.CreateDirectory(direct);
                File.Copy(Path.Combine(entry.Directory, "manifest.json"), Path.Combine(direct, "manifest.json"));
                File.Copy(entry.Source, Path.Combine(direct, "card.html"));
                if (!string.IsNullOrEmpty(entry.PythonSource)) File.Copy(entry.PythonSource, Path.Combine(direct, "main.py"));
                replacement = store.ImportFolder(direct);
            }
            else replacement = store.ImportArchive(entry.Source);
            if (replacement.Manifest.Id != entry.Id) throw new IOException("원본 카드 ID가 바뀌었어요. 새 카드로 가져와 주세요.");
            UnloadWebCard(entry.Directory);
            Directory.Move(entry.Directory, backup); moved = true;
            Directory.Move(replacement.Directory, entry.Directory); moved = false;
            RestartImportedCard(entry.Directory, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CardManifestException)
        {
            if (moved && !Directory.Exists(entry.Directory)) { Directory.Move(backup, entry.Directory); moved = false; }
            ReconcileImportedCards();
            Dialogs.Show(ex.Message, heading: "다시 가져오지 못했어요");
        }
        finally { if (!moved) Directory.Delete(staging, true); }
    }

    public bool DeleteWebCard(WebCardWindow card)
    {
        var entry = _cfg.Cards.FirstOrDefault(c => c.Directory == card.Runtime.Package.Path);
        if (entry == null) return false;
        if (Dialogs.Show(entry.Linked ? "연결을 해제해요. 원본 폴더는 남겨 둬요." : "내부 카드 폴더와 카드가 저장한 데이터도 삭제해요.",
            MessageBoxButton.OKCancel, heading: $"'{card.CardName}' 카드를 삭제할까요?", primary: "삭제") != MessageBoxResult.OK) return false;
        string? staged = null;
        bool saved = false;
        UnloadWebCard(entry.Directory);
        try
        {
            if (!entry.Linked && Directory.Exists(entry.Directory))
            {
                string expected = Path.GetFullPath(Path.Combine(AppPaths.CardsDir, entry.Id));
                if (!string.Equals(expected, Path.GetFullPath(entry.Directory), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("카드 저장소 밖의 폴더는 삭제할 수 없어요.");
                staged = Path.Combine(AppPaths.CardsDir, ".deleted-" + Guid.NewGuid().ToString("N"));
                Directory.Move(entry.Directory, staged);
            }
            _cfg.Cards.Remove(entry);
            if (!_cfg.TrySave()) throw new IOException("카드 삭제 설정을 저장하지 못했어요.");
            saved = true;
            if (staged != null) Directory.Delete(staged, true);
            RaiseChanged();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!saved)
            {
                if (!_cfg.Cards.Contains(entry)) _cfg.Cards.Add(entry);
                if (staged != null && Directory.Exists(staged)) Directory.Move(staged, entry.Directory);
                ReconcileImportedCards();
            }
            Dialogs.Show(ex.Message, heading: saved ? "남은 카드 파일을 정리하지 못했어요" : "카드를 삭제하지 못했어요");
            return saved;
        }
    }

    private void RestartImportedCard(string path, bool manual)
    {
        if (_shuttingDown) return;
        if (manual) _backendRecovery.Remove(path);
        UnloadWebCard(path);
        _cardLoadErrors.Remove(path);
        ReconcileImportedCards();
    }

    public void ImportCardFolder(string group)
    {
        if (_shuttingDown) return;
        if (!_cfg.CardTrustNoticeShown)
        {
            Dialogs.Show("이 카드는 PC에 제한 없이 접근해요. 직접 만들었거나 믿는 카드만 쓰세요.", heading: "카드 가져오기");
            _cfg.CardTrustNoticeShown = true;
            if (!_cfg.TrySave()) return;
        }
        var picker = new CardImportWindow(Cards.Select(c => c.Group.Name).ToArray(), group);
        if (picker.ShowDialog() != true || picker.Result == null) return;
        try
        {
            var imported = picker.Result;
            if (_cfg.Cards.Any(c => c.Id == imported.Manifest.Id)) throw new IOException("같은 ID의 카드가 이미 있어요.");
            var registration = new CardRegistration { Id = imported.Manifest.Id, Directory = imported.Directory, Source = imported.Source, PythonSource = picker.PythonSource, Group = picker.Group, Linked = imported.Linked };
            _cfg.Cards.Add(registration);
            if (!_cfg.TrySave())
            {
                _cfg.Cards.Remove(registration);
                throw new IOException("카드 폴더는 복사했지만 설정을 저장하지 못했어요: " + imported.Directory);
            }
            Reconcile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CardManifestException)
        { Dialogs.Show(ex.Message, heading: "카드를 가져오지 못했어요"); }
    }

    private void ReconcileImportedCards()
    {
        _cfg.Cards ??= new();
        foreach (var item in _cfg.Cards)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Directory) || _webCards.ContainsKey(item.Directory) || _cardLoadErrors.Contains(item.Directory)) continue;
            IDisposable? backend = null;
            try
            {
                var manifest = CardManifest.Load(item.Directory);
                if (string.IsNullOrEmpty(item.Address))
                {
                    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(manifest.Id));
                    int value = (hash[0] << 16) | (hash[1] << 8) | hash[2];
                    do
                    {
                        if (value < 2) value = 2;
                        item.Address = $"127.{(value >> 16) & 255}.{(value >> 8) & 255}.{value & 255}";
                        value = (value + 1) & 0xffffff;
                    } while (_cfg.Cards.Any(other => other != item && other.Address == item.Address));
                }
                bool python = File.Exists(Path.Combine(item.Directory, "main.py"));
                for (int attempt = 0; attempt < 32; attempt++)
                {
                    try
                    {
                        if (python)
                        {
                            var probe = new TcpListener(IPAddress.Parse(item.Address), item.Port);
                            probe.Start(); probe.Stop();
                            backend = new CardPythonProcess(item.Directory, item.Address, item.Port);
                        }
                        else backend = new CardStaticServer(item.Directory, IPAddress.Parse(item.Address), item.Port);
                        break;
                    }
                    catch (SocketException) { item.Port = item.Port >= 65535 ? 47831 : item.Port + 1; }
                }
                if (backend == null) throw new IOException("카드 서버의 포트를 열지 못했어요.");
                if (!_cfg.TrySave()) { backend.Dispose(); throw new IOException("카드 주소를 저장하지 못했어요."); }
                string settings = Path.Combine(item.Directory, "settings.html");
                var document = new CardDocument
                {
                    Id = manifest.Id, Name = manifest.Name, Path = item.Directory,
                    Url = $"http://{item.Address}:{item.Port}/",
                    Background = manifest.Background,
                    Cards = new[] { new WebCardInfo(manifest.Id, manifest.Name, manifest.Width, manifest.Height) },
                    CardHtml = Array.Empty<byte>(),
                    SettingsHtml = File.Exists(settings) ? File.ReadAllBytes(settings) : null,
                };
                var runtime = new CardRuntime(document, this, default)
                {
                    Backend = backend,
                    Ready = backend is CardPythonProcess process ? process.Ready : System.Threading.Tasks.Task.CompletedTask,
                    Restart = () => _debounce.Dispatcher.BeginInvoke(() => RestartImportedCard(item.Directory, true)),
                };
                _webCards.Add(item.Directory, runtime);
                if (item.Linked)
                {
                    var watcher = new FileSystemWatcher(item.Directory) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName };
                    var refresh = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                    refresh.Tick += (_, _) =>
                    {
                        refresh.Stop();
                        if (!_shuttingDown && _webCards.TryGetValue(item.Directory, out var active) && active == runtime)
                            foreach (var window in runtime.Windows) window.ReloadPage();
                    };
                    FileSystemEventHandler changed = (_, _) => _debounce.Dispatcher.BeginInvoke(() =>
                    { if (_shuttingDown) return; refresh.Stop(); refresh.Start(); });
                    watcher.Changed += changed; watcher.Created += changed; watcher.Deleted += changed;
                    watcher.Renamed += (_, e) => changed(watcher, e);
                    watcher.EnableRaisingEvents = true;
                    runtime.SourceWatcher = watcher;
                }
                if (backend is CardPythonProcess running)
                    running.Failed += message => _debounce.Dispatcher.BeginInvoke(() =>
                    {
                        if (_shuttingDown || !_webCards.TryGetValue(item.Directory, out var current) || current != runtime) return;
                        foreach (var window in runtime.Windows) window.ShowError(message);
                        if (!_backendRecovery.TryGetValue(item.Directory, out var policy))
                            _backendRecovery[item.Directory] = policy = new BrowserRecoveryPolicy();
                        policy.RecordExit(Environment.TickCount64);
                        if (policy.Blocked) return;
                        var retry = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                        retry.Tick += (_, _) =>
                        {
                            retry.Stop();
                            if (!_shuttingDown && _webCards.TryGetValue(item.Directory, out var active) && active == runtime)
                                RestartImportedCard(item.Directory, false);
                        };
                        retry.Start();
                    });
                foreach (var info in document.Cards) CreateWebCardWindow(runtime, info);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CardManifestException or System.ComponentModel.Win32Exception or FormatException)
            {
                backend?.Dispose();
                _cardLoadErrors.Add(item.Directory);
                Dialogs.Show(ex.Message, heading: "카드를 열지 못했어요");
            }
        }
    }
}
