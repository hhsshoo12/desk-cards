using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DeskFolders;

/// <summary>그룹 카드 위치를 %APPDATA%\DeskFolders\config.json에 저장한다. 그룹 내용 자체는 실제 폴더가 원본이다.</summary>
internal sealed class Config
{
    public Dictionary<string, double[]> Positions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
