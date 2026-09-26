using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DeskFolders;

/// <summary>그룹 카드 위치를 %APPDATA%\DeskFolders\config.json에 저장한다. 그룹 내용 자체는 실제 폴더가 원본이다.</summary>
internal sealed class Config
{
    public Dictionary<string, double[]> Positions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>카드별 미리보기 칸 수와 확대 비율. 그룹 이름 → 배치.</summary>
    public Dictionary<string, CardLayout> Layouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>카드 크기에 Windows 배율(125% 등)을 곱할지. 기본 켜짐.</summary>
    public bool FollowWindowsScale { get; set; } = true;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskFolders", "config.json");

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