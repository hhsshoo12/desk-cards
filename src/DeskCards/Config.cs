using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DeskCards;

/// <summary>그룹 카드 위치를 %APPDATA%\DeskCards\config.json에 저장한다. 그룹 내용 자체는 실제 폴더가 원본이다.</summary>
internal sealed class Config
{
    public Dictionary<string, double[]> Positions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>카드별 미리보기 칸 수와 확대 비율. 그룹 이름 → 배치.</summary>
    public Dictionary<string, CardLayout> Layouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Windows 배율을 바꾸면 카드도 같은 비율로 커지고 작아질지. 기본 켜짐.</summary>
    public bool FollowWindowsScale { get; set; } = true;

    /// <summary>카드를 옮기거나 크기를 바꿀 때 안내선을 보여 주고 줄에 맞출지.</summary>
    public bool ShowGuides { get; set; } = true;

    /// <summary>미리보기 칸 하나의 기준 크기(DIP). 처음 실행 때 바탕화면 아이콘 간격으로 정하고 고정한다.</summary>
    public double CellSize { get; set; }

    /// <summary>따라가기를 끈 순간의 Windows 배율. 꺼져 있는 동안은 이 배율일 때의 실제 크기를 유지한다.</summary>
    public double FixedScale { get; set; }

    /// <summary>새 카드의 확대 비율.</summary>
    public double DefaultZoom { get; set; }

    /// <summary>배율 계산 방식 버전. 2부터 Zoom에 Windows 배율이 포함된다.</summary>
    public int ScaleVersion { get; set; }

    private static string FilePath => Path.Combine(
        AppPaths.ConfigDir, "config.json");

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath));
                if (cfg != null)
                {
                    cfg.Positions = new Dictionary<string, double[]>(cfg.Positions, StringComparer.OrdinalIgnoreCase);
                    cfg.Layouts = new Dictionary<string, CardLayout>(cfg.Layouts ?? new(), StringComparer.OrdinalIgnoreCase);
                    return cfg;
                }
            }
        }
        catch
        {
            // 손상된 설정은 무시하고 새로 시작한다.
        }
        return new Config();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 저장 실패는 치명적이지 않다.
        }
    }
}

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
        Zoom = Math.Clamp(Zoom, MinZoom, MaxZoom),
    };
}