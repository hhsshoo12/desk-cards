using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeskCards;

internal static partial class DardStorage
{
    public static bool IsLocation(string? location) => location is
        "legacy/shared" or "offline/shared" or "offline/card" or "online/shared" or "online/card" or
        "offline/v2/shared" or "offline/v2/card" or "online/v2/shared" or "online/v2/card";

    public static (bool Internet, bool PerCard) ParseLocation(string location)
    {
        if (!IsLocation(location)) throw new IOException("알 수 없는 카드 저장소 위치예요.");
        return (location.StartsWith("online", StringComparison.Ordinal), location.EndsWith("/card", StringComparison.Ordinal));
    }

    /// <summary>설정이 없을 때 기존 저장소 목록, 마지막 이전 기록, 구버전 프로필에서 출발점을 찾는다.</summary>
    public static string? FindLocation(DardPackage pkg)
    {
        var pending = ReadMove(pkg.Id);
        if (pending != null) return pending.Acknowledged ? pending.To : pending.From;
        foreach (bool internet in new[] { pkg.Internet, !pkg.Internet })
        {
            var origins = Recorded(internet).Where(p => p.Value == pkg.Id).Select(p => p.Key).ToList();
            if (origins.Count == 0) continue;
            string env = internet ? "online" : "offline";
            if (origins.Contains("https://" + pkg.Host)) return env + "/v2/shared";
            if (origins.Any(o => o.EndsWith(".c--v2.card.desk", StringComparison.Ordinal))) return env + "/v2/card";
            return env + (origins.Contains(pkg.LegacyOriginFor("", false)) ? "/shared" : "/card");
        }
        return Directory.Exists(Path.Combine(AppPaths.WebDataDir, "EBWebView", "Default")) ? "legacy/shared" : null;
    }

    private sealed record TransferOrigin(string From, string To, string Archive);
    private sealed class MoveRecord
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public List<TransferOrigin> Origins { get; set; } = new();
        public bool Exported { get; set; }
        public bool Imported { get; set; }
        public bool Acknowledged { get; set; }
        public int Skipped { get; set; }
    }

    private static string MoveDir(string id) => Path.Combine(AppPaths.WebDataDir, "moves",
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id))));

    private static MoveRecord? ReadMove(string id)
    {
        string path = Path.Combine(MoveDir(id), "move.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<MoveRecord>(File.ReadAllText(path))
            ?? throw new IOException("저장소 이전 기록이 손상됐어요.") : null;
    }

    private static void WriteAtomic(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>설정의 새 위치가 디스크에 저장된 뒤에만 이전 기록을 치운다.</summary>
    public static void AcknowledgeMove(string id)
    {
        var move = ReadMove(id);
        if (move == null || !move.Imported) return;
        string dir = MoveDir(id);
        // 작은 완료 기록은 남긴다. config.json이 오래된 백업으로 복구돼도 완료된 대상을 덮어쓰지 않는다.
        move.Acknowledged = true;
        WriteAtomic(Path.Combine(dir, "move.json"), move);
        foreach (string archive in Directory.EnumerateFiles(dir, "*.zip")) File.Delete(archive);
        File.Delete(Path.Combine(dir, "move.json.tmp"));
    }

    /// <summary>마지막 카드 파일을 지울 때 브라우저 데이터와 재시도용 사본을 함께 지운다.</summary>
    public static async Task DeleteAsync(string id)
    {
        await _moving.WaitAsync();
        try
        {
            var move = ReadMove(id);
            foreach (bool internet in new[] { false, true })
            {
                var origins = Recorded(internet).Where(p => p.Value == id).Select(p => p.Key).ToList();
                if (origins.Count == 0) continue;
                var keeper = await KeeperFor(internet);
                foreach (string origin in origins)
                    await keeper.Cdp("Storage.clearDataForOrigin", new { origin, storageTypes = "all" });
                UpdateIndex(internet, index => origins.ForEach(o => index.Remove(o)));
            }
            if (move?.From == "legacy/shared")
            {
                var legacy = await KeeperAt(AppPaths.WebDataDir, false);
                foreach (string origin in move.Origins.Select(p => p.From).Distinct())
                    await legacy.Cdp("Storage.clearDataForOrigin", new { origin, storageTypes = "all" });
            }
            string dir = MoveDir(id);
            if (Directory.Exists(dir))
            {
                // 재귀 삭제하지 않고 이 카드의 해시 디렉터리 안에 만든 파일만 지운다.
                File.Delete(Path.Combine(dir, "move.json"));
                foreach (string file in Directory.EnumerateFiles(dir)) File.Delete(file);
                Directory.Delete(dir);
            }
        }
        finally { _moving.Release(); }
    }

    /// <summary>
    /// 모든 원본을 먼저 디스크에 내보낸다. 가져오기 완료 기록을 디스크에 남긴 뒤 원본을 지우므로,
    /// 설정 저장 전에 앱이 종료되어도 재시도는 완료된 대상을 다시 덮어쓰지 않는다.
    /// 실패하면 원본과 이전 기록을 남기고, 호출자는 카드를 띄우지 않은 채 재시도할 수 있다.
    /// </summary>
    public static async Task<int> MoveAsync(DardPackage pkg, string fromLocation)
    {
        var (fromNet, fromPerCard) = ParseLocation(fromLocation);
        bool legacyProfile = fromLocation == "legacy/shared";
        bool sameEnv = !legacyProfile && fromNet == pkg.Internet;
        bool oldOrigins = !fromLocation.Contains("/v2/", StringComparison.Ordinal);
        var pairs = pkg.Cards.Select(c => new TransferOrigin(
            oldOrigins ? pkg.LegacyOriginFor(c.Id, fromPerCard) : pkg.OriginFor(c.Id, fromPerCard),
            pkg.OriginFor(c.Id, pkg.StoragePerCard), ""))
            .DistinctBy(p => p.To).Where(p => !sameEnv || p.From != p.To)
            .Select((p, i) => p with { Archive = i + ".zip" }).ToList();
        await _moving.WaitAsync();
        try
        {
            string dir = MoveDir(pkg.Id), path = Path.Combine(dir, "move.json");
            var move = ReadMove(pkg.Id);
            if (move is { Imported: true } && move.To == fromLocation && move.To != pkg.StorageLocation)
                move = null; // 이전 완료 후 다음 업데이트가 다시 저장소를 바꾸는 경우
            if (move != null && (move.From != fromLocation || move.To != pkg.StorageLocation || !move.Origins.SequenceEqual(pairs)))
                throw new IOException("이전 중인 카드의 구성이 바뀌었어요. 이전에 사용하던 카드 파일로 먼저 데이터 옮기기를 마쳐 주세요.");
            if (move == null)
            {
                move = new MoveRecord { From = fromLocation, To = pkg.StorageLocation, Origins = pairs };
                WriteAtomic(path, move);
            }
            var from = legacyProfile ? await KeeperAt(AppPaths.WebDataDir, false) : await KeeperFor(fromNet);
            var to = await KeeperFor(pkg.Internet);
            await PrepareAsync(pkg);
            await ExportMoveAsync(from, move, legacyProfile, fromNet, pkg.Id, dir, path);
            await ImportMoveAsync(to, move, dir, path);
            await ClearMoveSourceAsync(from, move, sameEnv, legacyProfile, fromNet);
            return move.Skipped;
        }
        finally { _moving.Release(); }
    }

    private static async Task ExportMoveAsync(Keeper from, MoveRecord move, bool legacyProfile, bool fromNet, string id, string dir, string path)
    {
        if (!move.Exported)
        {
            move.Skipped = 0;
            var recorded = legacyProfile ? new Dictionary<string, string>() : Recorded(fromNet);
            foreach (var p in move.Origins)
            {
                if (recorded.TryGetValue(p.From, out string? owner) && owner != id)
                    throw new IOException("예전 저장소 주소를 다른 카드도 사용하고 있어 자동으로 옮길 수 없어요. 원본 데이터는 그대로 남겨 뒀어요.");
                move.Skipped += await from.RunAsync(p.From, MovePage.Export, Path.Combine(dir, p.Archive));
                using var saved = new FileStream(Path.Combine(dir, p.Archive), FileMode.Open, FileAccess.Write, FileShare.None);
                saved.Flush(flushToDisk: true);
            }
            move.Exported = true;
            WriteAtomic(path, move);
        }
    }

    private static async Task ImportMoveAsync(Keeper to, MoveRecord move, string dir, string path)
    {
        if (!move.Imported)
        {
            foreach (var p in move.Origins)
            {
                await to.Cdp("Storage.clearDataForOrigin", new { origin = p.To, storageTypes = "all" });
                await to.RunAsync(p.To, MovePage.Import, Path.Combine(dir, p.Archive));
            }
            move.Imported = true;
            WriteAtomic(path, move);
        }
    }

    private static async Task ClearMoveSourceAsync(Keeper from, MoveRecord move, bool sameEnv, bool legacyProfile, bool fromNet)
    {
        var keep = sameEnv ? move.Origins.Select(p => p.To).ToHashSet() : new HashSet<string>();
        var cleared = move.Origins.Select(p => p.From).Distinct().Where(o => !keep.Contains(o)).ToList();
        foreach (string origin in cleared)
            await from.Cdp("Storage.clearDataForOrigin", new { origin, storageTypes = "all" });
        if (!legacyProfile) UpdateIndex(fromNet, index => cleared.ForEach(o => index.Remove(o)));
    }
}
