using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DeskCards;

/// <summary>그룹 카드 위치를 %APPDATA%\DeskCards\config.json에 저장한다. 그룹 내용 자체는 실제 폴더가 원본이다.</summary>
internal sealed class Config
{
    public Dictionary<string, double[]> Positions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>카드별 미리보기 칸 수와 확대 비율. 그룹 이름 → 배치.</summary>
    public Dictionary<string, CardLayout> Layouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>카드 안 항목 순서. 그룹 이름 → 파일 이름 목록. 없는 그룹은 이름 순.</summary>
    public Dictionary<string, List<string>> Orders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>.dard 카드 승인 기록. 매니페스트 id → 승인한(또는 거절한) 파일 해시. 카드 파일이 스스로 적을 수 없게 여기 둔다.</summary>
    public Dictionary<string, DardApproval> Dards { get; set; } = new(StringComparer.Ordinal);

    /// <summary>.dard 저장소가 지금 있는 곳(DardPackage.StorageLocation). 매니페스트 id → "offline/shared" 등. 바뀌면 불러오기 전에 옮긴다.</summary>
    public Dictionary<string, string> DardStorage { get; set; } = new(StringComparer.Ordinal);

    /// <summary>저장소를 옮기는 중인 .dard(매니페스트 id → 옮겨 갈 위치). 끝나면 지운다. 남아 있으면 지난번에 옮기다 멈춘 것이다.</summary>
    public Dictionary<string, string> DardMoving { get; set; } = new(StringComparer.Ordinal);

    /// <summary>"그대로 두기"를 고른 카드 데이터 경고(DardIssue.Key). 다시 알리지 않는다.</summary>
    public List<string> KeptDardData { get; set; } = new();

    /// <summary>Windows 배율을 바꾸면 카드도 같은 비율로 커지고 작아질지. 기본 켜짐.</summary>
    public bool FollowWindowsScale { get; set; } = true;

    /// <summary>카드를 옮기거나 크기를 바꿀 때 안내선을 보여 주고 줄에 맞출지.</summary>
    public bool ShowGuides { get; set; } = true;

    /// <summary>실험: 카드끼리 간격 없이 딱 붙여 둘 수 있게 할지. 기본 꺼짐(나란히 놓으면 그림자 폭만큼 띄운다).</summary>
    public bool FlushSnap { get; set; }

    /// <summary>실험: 카드끼리 겹친 자리도 저장할지. 기본 꺼짐(겹친 채 놓으면 원래 자리로 돌아간다). 켜면 완전히 붙이기도 켜진다.</summary>
    public bool AllowOverlap { get; set; }

    /// <summary>"다시 보지 않기"를 누른 안내 말풍선 id.</summary>
    public List<string> HiddenTips { get; set; } = new();

    /// <summary>카드의 더보기 칸에 마우스를 잠시 올려 두면 펼칠지. 기본 켜짐.</summary>
    public bool HoverExpand { get; set; } = true;

    /// <summary>더보기 칸에 올려 두고 펼칠 때까지 기다리는 시간(ms, 0~1000, 0.1초 단위). 0은 즉시.</summary>
    public int HoverExpandDelay { get; set; } = HoverDelayDefault;

    public const int HoverDelayMin = 0, HoverDelayMax = 1000, HoverDelayDefault = 400;

    /// <summary>카드 바: 조합키를 누른 채 화면 가장자리에 마우스를 대고 있으면 나오는 카드 줄.</summary>
    public bool BarEnabled { get; set; } = true;

    /// <summary>카드 바에 놓은 카드(비어 있으면 빈 바). 위치·크기는 바 두께 단위라 바 두께·모니터가 바뀌어도 같은 모양이다.</summary>
    public List<BarItem> BarItems { get; set; } = new();

    /// <summary>바탕화면에는 없고 카드 바에만 있는 그룹 이름.</summary>
    public List<string> BarOnlyGroups { get; set; } = new();

    /// <summary>가장자리에 대고 있어야 하는 시간(ms, 0~2000, 0.1초 단위). 이 동안 커서 옆 게이지가 한 바퀴 돈다.</summary>
    public int BarDelay { get; set; } = 500;

    /// <summary>따로 정하지 않은 디스플레이에서 여는 가장자리(마지막으로 고른 곳).</summary>
    public ScreenEdge BarEdge { get; set; } = ScreenEdge.Right;

    /// <summary>디스플레이별 여는 가장자리. 디스플레이 이름(Screen.DeviceName) → 가장자리, null이면 그 디스플레이에서는 열지 않음.</summary>
    public Dictionary<string, ScreenEdge?> BarEdges { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>바를 여는 조합키(가상 키 코드, 예: Alt + Shift). 설정에서 직접 눌러서 정한다.</summary>
    public List<int> BarKeys { get; set; } = KeyCombo.Default.ToList();

    /// <summary>바 두께. 모니터 작업 영역의 %(10~33, 3분의 1까지).</summary>
    public int BarSize { get; set; } = BarSizeMax;

    /// <summary>인식 영역: 가장자리에서 이만큼 안쪽까지 대고 있어도 된다(0.1% 단위, 0 = 딱 붙었을 때만 ~ 25 = 2.5%).</summary>
    public int BarZone { get; set; }

    public const int BarDelayMax = 2000, BarSizeMin = 10, BarSizeMax = 33, BarZoneMax = 25;

    /// <summary>미리보기 칸 하나의 기준 크기(DIP). 처음 실행 때 바탕화면 아이콘 간격으로 정하고 고정한다.</summary>
    public double CellSize { get; set; }

    /// <summary>따라가기를 끈 순간의 Windows 배율. 꺼져 있는 동안은 이 배율일 때의 실제 크기를 유지한다.</summary>
    public double FixedScale { get; set; }

    /// <summary>새 카드의 확대 비율.</summary>
    public double DefaultZoom { get; set; }

    /// <summary>배율 계산 방식 버전. 2부터 Zoom에 Windows 배율이 포함된다.</summary>
    public int ScaleVersion { get; set; }

    /// <summary>새 버전을 알아서 받아 두었다가 다음에 켤 때 바꿀지. 기본 켜짐.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>받아 두고 다음 실행 때 바꿔 끼울 버전(예: "0.2.2"). 없으면 null.</summary>
    public string? PendingUpdate { get; set; }

    public static int NormalizeHoverDelay(int ms) =>
        Math.Clamp((int)Math.Round(ms / 100.0) * 100, HoverDelayMin, HoverDelayMax);

    private static string DefaultPath => Path.Combine(AppPaths.ConfigDir, "config.json");

    private string _filePath = DefaultPath;

    public static Config Load(string? filePath = null)
    {
        filePath ??= DefaultPath;
        foreach (string candidate in new[] { filePath, filePath + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(candidate));
                if (cfg != null)
                {
                    cfg._filePath = filePath;
                    var positions = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (name, p) in cfg.Positions ?? new())
                        if (p is { Length: 2 } && double.IsFinite(p[0]) && double.IsFinite(p[1])) positions[name] = p;
                    cfg.Positions = positions;
                    var layouts = new Dictionary<string, CardLayout>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (name, layout) in cfg.Layouts ?? new())
                        if (layout != null) layouts[name] = layout.Normalized();
                    cfg.Layouts = layouts;
                    var orders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (name, order) in cfg.Orders ?? new())
                        if (order != null) orders[name] = order.Where(n => !string.IsNullOrEmpty(n)).ToList();
                    cfg.Orders = orders;
                    if (!double.IsFinite(cfg.CellSize) || cfg.CellSize < 16 || cfg.CellSize > 512) cfg.CellSize = 0;
                    if (!double.IsFinite(cfg.FixedScale) || cfg.FixedScale <= 0 || cfg.FixedScale > 8) cfg.FixedScale = 0;
                    if (!double.IsFinite(cfg.DefaultZoom) || cfg.DefaultZoom <= 0) cfg.DefaultZoom = 0;
                    else cfg.DefaultZoom = Math.Clamp(cfg.DefaultZoom, CardLayout.MinZoom, CardLayout.MaxZoom);
                    cfg.HiddenTips = cfg.HiddenTips?.Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList() ?? new();
                    cfg.BarItems = (cfg.BarItems ?? new())
                        .Where(i => i != null && !string.IsNullOrEmpty(i.Group) && double.IsFinite(i.X) && double.IsFinite(i.Y) && double.IsFinite(i.W) && i.W > 0)
                        .GroupBy(i => i.Group, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
                    cfg.BarOnlyGroups = cfg.BarOnlyGroups?.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new();
                    cfg.HoverExpandDelay = NormalizeHoverDelay(cfg.HoverExpandDelay);
                    cfg.BarDelay = Math.Clamp((int)Math.Round(cfg.BarDelay / 100.0) * 100, 0, BarDelayMax);
                    cfg.BarSize = Math.Clamp(cfg.BarSize, BarSizeMin, BarSizeMax);
                    cfg.BarZone = Math.Clamp(cfg.BarZone, 0, BarZoneMax);
                    if (!Enum.IsDefined(cfg.BarEdge)) cfg.BarEdge = ScreenEdge.Right;
                    var edges = new Dictionary<string, ScreenEdge?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (display, edge) in cfg.BarEdges ?? new())
                        if (!string.IsNullOrEmpty(display) && (edge == null || Enum.IsDefined(edge.Value))) edges[display] = edge;
                    cfg.BarEdges = edges;
                    cfg.BarKeys = KeyCombo.Clean(cfg.BarKeys);
                    var dards = new Dictionary<string, DardApproval>(StringComparer.Ordinal);
                    foreach (var (id, approval) in cfg.Dards ?? new())
                        if (!string.IsNullOrEmpty(id) && approval is { Hash.Length: > 0 }) dards[id] = approval.Normalized();
                    cfg.Dards = dards;
                    var dardStorage = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (id, location) in cfg.DardStorage ?? new())
                        if (!string.IsNullOrEmpty(id) && DeskCards.DardStorage.IsLocation(location)) dardStorage[id] = location;
                    cfg.DardStorage = dardStorage;
                    cfg.DardMoving = new Dictionary<string, string>((cfg.DardMoving ?? new()).Where(p => !string.IsNullOrEmpty(p.Key) && dardStorage.ContainsKey(p.Key)), StringComparer.Ordinal);
                    cfg.KeptDardData = cfg.KeptDardData?.Where(k => !string.IsNullOrEmpty(k)).Distinct().ToList() ?? new();
                    return cfg;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // 주 파일이 손상되었으면 마지막으로 온전히 저장된 사본을 읽는다.
            }
        }
        return new Config { _filePath = filePath };
    }

    public void Save() => TrySave();

    /// <summary>데이터를 지우기 전에 저장 성공을 확인해야 하는 작업에서 쓴다.</summary>
    public bool TrySave()
    {
        string temporary = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true });
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_filePath))
            {
                for (int attempt = 0; ; attempt++)
                {
                    try { File.Replace(temporary, _filePath, _filePath + ".bak"); break; }
                    catch (IOException) when (attempt < 3) { System.Threading.Thread.Sleep(50); }
                }
            }
            else File.Move(temporary, _filePath);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("설정 저장 실패: {0}: {1}", _filePath, ex);
            try { File.AppendAllText(_filePath + ".log", $"{DateTimeOffset.Now:o} 설정 저장 실패: {ex}{System.Environment.NewLine}"); } catch { }
            return false;
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>카드 바에 놓은 카드 하나. X·Y(왼쪽 위)와 W(너비)는 바 두께를 1로 친 값이다.</summary>
internal sealed class BarItem
{
    public string Group { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 0.8;

    public BarItem Clone() => new() { Group = Group, X = X, Y = Y, W = W };
}

/// <summary>.dard 한 종류(매니페스트 id)의 승인. 해시가 바뀌면 다시 묻는다(권한이 같거나 줄었으면 그대로 승인).</summary>
internal sealed class DardApproval
{
    public string Hash { get; set; } = "";
    public bool Allowed { get; set; }
    /// <summary>승인한 권한 키(DardPermission.Key).</summary>
    public List<string> Permissions { get; set; } = new();

    public DardApproval Normalized() => new()
    {
        Hash = Hash,
        Allowed = Allowed,
        Permissions = Permissions?.Where(p => !string.IsNullOrEmpty(p)).ToList() ?? new(),
    };
}

/// <summary>화면 가장자리. 값은 Windows 앱바(ABE_*)와 같다.</summary>
internal enum ScreenEdge { Left = 0, Top = 1, Right = 2, Bottom = 3 }

/// <summary>
/// 카드 모양. 미리보기 칸 수(가로×세로)가 카드 비율을 정하고, Zoom은 그 모양 그대로 키우거나 줄이는 배율이다.
/// </summary>
internal sealed class CardLayout
{
    public const int MinCells = 1, MaxCells = 8;
    public const double MinZoom = 0.5, MaxZoom = 4;

    public int Cols { get; set; } = 2;
    public int Rows { get; set; } = 2;
    public double Zoom { get; set; } = 1;

    public CardLayout Normalized() => new()
    {
        Cols = Math.Clamp(Cols, MinCells, MaxCells),
        Rows = Math.Clamp(Rows, MinCells, MaxCells),
        Zoom = double.IsFinite(Zoom) ? Math.Clamp(Zoom, MinZoom, MaxZoom) : 1,
    };
}
