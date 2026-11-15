using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DeskCards.Expanded;

namespace DeskCards;

/// <summary>
/// 바탕화면에 둔 웹 카드(.dard)를 띄우고 관리한다. 창과 화면은 DLL(WPF)이 맡고,
/// 여기서는 자리 저장, 카드의 exe 실행, 화면과 exe 사이의 메시지 중계를 한다.
/// exe는 mode가 background면 카드를 숨겨도 계속 돌고, 아니면 카드가 보이는 동안만 돈다.
/// </summary>
internal sealed class WebCardManager
{
    public static WebCardManager? Instance { get; private set; }

    private readonly Config _cfg;
    private readonly GroupManager _mgr;
    private readonly Dictionary<string, Running> _running = new(StringComparer.OrdinalIgnoreCase);
    private bool _editing, _shuttingDown;

    public WebCardManager(Config cfg, GroupManager mgr, string? storeRoot = null)
    {
        _cfg = cfg;
        _mgr = mgr;
        Store = new WebCardStore(storeRoot ?? Path.Combine(AppPaths.LocalDataDir, "cards"));
    }

    public WebCardStore Store { get; }

    /// <summary>카드가 가져와졌거나 놓이거나 없어졌을 때(설정 창 갱신용).</summary>
    public event Action? Changed;

    public static WebCardManager Create(Config cfg, GroupManager mgr) => Instance = new WebCardManager(cfg, mgr);

    public void Start()
    {
        foreach (var id in _cfg.WebCards.Keys.ToList())
        {
            if (Find(id) is { } card) Open(card);
            else { _cfg.WebCards.Remove(id); _cfg.Save(); }
        }
    }

    public void Shutdown()
    {
        _shuttingDown = true;
        foreach (var r in _running.Values.ToList()) r.Dispose();
        _running.Clear();
        if (Instance == this) Instance = null;
    }

    /// <summary>놓인 웹 카드들의 화면 자리(물리 픽셀). 다른 카드를 옮길 때 안내선 기준이 된다.</summary>
    public List<Native.RECT> Rects(string? except = null) => _cfg.WebCards.Where(kv => _running.ContainsKey(kv.Key) && kv.Value.Shown && !kv.Key.Equals(except, StringComparison.OrdinalIgnoreCase))
        .Select(kv => new Native.RECT { Left = kv.Value.X, Top = kv.Value.Y, Right = kv.Value.X + kv.Value.Width, Bottom = kv.Value.Y + kv.Value.Height }).ToList();

    public InstalledWebCard? Find(string id) => Store.List().FirstOrDefault(c => c.Manifest.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public bool IsPlaced(string id) => _cfg.WebCards.ContainsKey(id);

    public bool IsShown(string id) => _cfg.WebCards.TryGetValue(id, out var p) && p.Shown;

    /// <summary>.dard 파일이나 폴더를 가져온다. 이미 놓여 있으면 새 내용으로 다시 띄운다.</summary>
    public InstalledWebCard Import(string source)
    {
        var card = Store.Import(source);
        if (_running.ContainsKey(card.Manifest.Id)) { Close(card.Manifest.Id); Open(card); }
        Changed?.Invoke();
        return card;
    }

    /// <summary>바탕화면에 놓는다.</summary>
    public void Place(string id)
    {
        if (Find(id) is not { } card || _running.ContainsKey(id)) return;
        double k = Native.PrimaryScale();
        int w = (int)Math.Round(card.Manifest.Width * k), h = (int)Math.Round(card.Manifest.Height * k);
        var wa = Screens.Primary.WorkArea;
        int n = _cfg.WebCards.Count;
        _cfg.WebCards[id] = new WebCardPlacement { X = wa.Right - w - 40 - 32 * n, Y = wa.Top + 40 + 32 * n, Width = w, Height = h, Shown = true };
        _cfg.Save();
        Open(card);
        Changed?.Invoke();
    }

    /// <summary>바탕화면에서 내린다(가져온 카드는 그대로 둔다).</summary>
    public void Unplace(string id)
    {
        Close(id);
        if (_cfg.WebCards.Remove(id)) _cfg.Save();
        Changed?.Invoke();
    }

    /// <summary>가져온 카드를 지운다.</summary>
    public void Uninstall(string id)
    {
        Unplace(id);
        try { Store.Remove(id); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Changed?.Invoke();
    }

    public void SetShown(string id, bool shown)
    {
        if (!_cfg.WebCards.TryGetValue(id, out var p) || !_running.TryGetValue(id, out var r)) return;
        p.Shown = shown;
        _cfg.Save();
        r.Handle?.SetVisible(shown);
        r.SyncProcess();
        Changed?.Invoke();
    }

    public void Reload(string id)
    {
        if (_running.TryGetValue(id, out var r)) r.Handle?.Reload();
    }

    /// <summary>바탕화면 편집 모드에 들어가고 나온다(카드가 띠로 옮기고 크기를 바꿀 수 있게 한다).</summary>
    public void SetEditing(bool editing)
    {
        _editing = editing;
        foreach (var r in _running.Values) r.Handle?.SetEditing(editing);
    }

    /// <summary>어두운 막 뒤로 밀린 카드를 다시 막 위로 올린다.</summary>
    public void RaiseForEdit()
    {
        if (!_editing) return;
        foreach (var r in _running.Values) r.Handle?.SetEditing(true);
        // 카드 올리기는 창 스레드에서 비동기로 끝나므로, 그 뒤에 편집 막대를 다시 맨 위로 올린다.
        WebCards.After(() => Ui.Post(EditBar.BringToTop));
    }

    private void Open(InstalledWebCard card)
    {
        string id = card.Manifest.Id;
        if (_shuttingDown || _running.ContainsKey(id) || !_cfg.WebCards.TryGetValue(id, out var p)) return;
        var running = new Running(this, card, p);
        _running[id] = running;
        running.Start(_editing);
    }

    private void Close(string id)
    {
        if (_running.Remove(id, out var r)) r.Dispose();
    }

    private void ShowMenu(string id) => Ui.Post(() => Menus.ForWebCard(this, _mgr, id).ShowAtCursor());

    internal static string DataDir(string id) => Path.Combine(AppPaths.LocalDataDir, "carddata", id);

    private sealed class Running : IWebCardHost, IDisposable
    {
        private readonly WebCardManager _owner;
        private readonly InstalledWebCard _card;
        private readonly WebCardPlacement _placement;
        private WebCardProcess? _process;
        private bool _disposed;

        public Running(WebCardManager owner, InstalledWebCard card, WebCardPlacement placement)
        {
            _owner = owner;
            _card = card;
            _placement = placement;
        }

        public IWebCardHandle? Handle { get; private set; }

        private string Id => _card.Manifest.Id;

        public void Start(bool editing)
        {
            var m = _card.Manifest;
            if (m.Html != null)
            {
                Handle = WebCards.Open(new WebCardOptions(Id, m.Name, _card.Directory, m.Html,
                    Path.Combine(AppPaths.LocalDataDir, "webdata"),
                    _placement.X, _placement.Y, _placement.Width, _placement.Height, _placement.Shown, ExpandedWindow.CurrentPalette()), this);
                if (editing) Handle.SetEditing(true);
            }
            SyncProcess();
        }

        /// <summary>exe는 background 카드면 늘, 아니면 카드가 보이는 동안만 돈다.</summary>
        public void SyncProcess()
        {
            var m = _card.Manifest;
            if (m.Exe == null || _disposed) return;
            bool want = m.Background || _placement.Shown;
            if (want && (_process == null || _process.HasExited)) StartProcess(m.Exe);
            else if (!want && _process != null) StopProcess();
        }

        private void StartProcess(string exe)
        {
            _process?.Dispose();
            var process = new WebCardProcess(Path.Combine(_card.Directory, exe), _card.Directory, Id, DataDir(Id));
            process.Message += OnExeLine;
            try { process.Start(); _process = process; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                process.Dispose();
                _process = null;
            }
        }

        private void StopProcess()
        {
            var p = _process;
            _process = null;
            p?.Dispose();
        }

        private void OnExeLine(string line)
        {
            var handle = Handle;
            if (_disposed || handle == null) return;
            string json;
            try { using var doc = JsonDocument.Parse(line); json = line; }
            catch (JsonException) { json = JsonSerializer.Serialize(line); }
            handle.ToPage(json);
        }

        // ----- IWebCardHost (WPF 창 스레드에서 불린다) -----

        public void Bounds(int x, int y, int width, int height) => Ui.Post(() =>
        {
            _placement.X = x; _placement.Y = y; _placement.Width = width; _placement.Height = height;
            _owner._cfg.Save();
            _owner._mgr.RaiseEditLayerNow();
        });

        public void Menu() => _owner.ShowMenu(Id);

        public void FromPage(string json) { var p = _process; p?.Send(json); }

        public PixelRect Snap(string id, PixelRect rect) => ExpandedWindow.OnUi(() =>
        {
            var wa = DesktopGrid.WorkAreaAt(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
            var others = _owner._mgr.CardRects(null).Concat(_owner.Rects(id)).ToList();
            var (x, y, lines) = SmartGuides.Snap(rect.Left, rect.Top, rect.Width, rect.Height, others, wa, Native.PrimaryScale());
            if (_owner._cfg.ShowGuides) SmartGuides.Show(wa, lines); else SmartGuides.Hide();
            return new PixelRect(x, y, x + rect.Width, y + rect.Height);
        }, rect);

        public void GuidesOff() => Ui.Post(SmartGuides.Hide);

        public void Activated() => Ui.Post(() => _owner._mgr.RestoreDesktopOrder());

        public void Closed() { }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Handle?.Close();
            Handle = null;
            StopProcess();
        }
    }
}