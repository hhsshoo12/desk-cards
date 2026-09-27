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

    /// <summary>Windows 배율을 바꾸면 카드도 같은 비율로 커지고 작아질지. 기본 켜짐.</summary>
    public bool FollowWindowsScale { get; set; } = true;

    /// <summary>카드를 옮기거나 크기를 바꿀 때 안내선을 보여 주고 줄에 맞출지.</summary>
    public bool ShowGuides { get; set; } = true;

    /// <summary>카드의 더보기 칸에 마우스를 잠시 올려 두면 펼칠지. 기본 켜짐.</summary>
    public bool HoverExpand { get; set; } = true;

    /// <summary>더보기 칸에 올려 두고 펼칠 때까지 기다리는 시간(ms, 0~1000, 0.1초 단위). 0은 즉시.</summary>
    public int HoverExpandDelay { get; set; } = HoverDelayDefault;

    public const int HoverDelayMin = 0, HoverDelayMax = 1000, HoverDelayDefault = 400;

    /// <summary>카드 바: 조합키를 누른 채 화면 가장자리에 마우스를 대고 있으면 나오는 카드 줄.</summary>
    public bool BarEnabled { get; set; } = true;

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

    public void Save()
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
            if (File.Exists(_filePath)) File.Replace(temporary, _filePath, _filePath + ".bak");
            else File.Move(temporary, _filePath);
        }
        catch
        {
            // 저장 실패는 치명적이지 않다.
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
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
