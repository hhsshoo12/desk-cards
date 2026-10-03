using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DeskCards;

internal static partial class DardStorage
{
    // ----- 저장소 목록: 데이터가 있을 수 있는 origin → 매니페스트 id -----
    // 앱 설정 파일과 따로, 브라우저 데이터 폴더 안에 둔다. 설정 파일이 날아가거나 되돌려져도 "어디에 무슨 데이터가 있나"는 남는다.

    private static readonly object _indexLock = new();

    private static string IndexPath(bool internet) => Path.Combine(AppPaths.WebDataDir, internet ? "online" : "offline", "desk-origins.json");

    /// <summary>환경 하나의 저장소 목록(origin → 매니페스트 id).</summary>
    public static Dictionary<string, string> Recorded(bool internet)
    {
        lock (_indexLock)
        {
            try
            {
                return ReadIndex(internet);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private static Dictionary<string, string> ReadIndex(bool internet)
    {
        string path = IndexPath(internet);
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return index;
        var read = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
            ?? throw new JsonException("저장소 목록이 비어 있어요.");
        foreach (var (origin, id) in read)
            if (!string.IsNullOrEmpty(id)) index[origin] = id;
        return index;
    }

    private static void UpdateIndex(bool internet, Action<Dictionary<string, string>> change)
    {
        lock (_indexLock)
        {
            // 손상된 목록을 빈 목록으로 덮어쓰면 다른 카드의 데이터 소유권을 잃는다.
            var index = ReadIndex(internet);
            change(index);
            WriteAtomic(IndexPath(internet), index);
        }
    }

}
