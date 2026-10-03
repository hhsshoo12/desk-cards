using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace DeskCards;

/// <summary>.dard를 열거나 검사하다가 거부할 때. 메시지는 사용자에게 그대로 보여 준다.</summary>
internal sealed class DardException : Exception
{
    public DardException(string message) : base(message) { }
}

/// <summary>매니페스트의 cards 한 장: 카드 ID, 이름, 가로:세로 비율.</summary>
internal sealed record DardCardInfo(string Id, string Name, double RatioW, double RatioH);

/// <summary>
/// 권한 하나. Key는 승인 기록에 남는 값(예: internet, notify, system:cpu)이고 Label은 화면에 보여 줄 이름이다.
/// Works가 false면 이 버전의 앱에는 아직 그 기능이 없다.
/// </summary>
internal sealed record DardPermission(string Key, string Label, bool Works);

/// <summary>
/// .dard 카드 패키지(확장자만 바꾼 zip). manifest.json, card.html, (있으면) settings.html만 들어 있다.
/// 디스크에 풀지 않고 매번 메모리에서 연다. 크기는 풀었을 때 기준 32MB까지.
/// </summary>
internal sealed class DardPackage
{
    public const long MaxUnpacked = 32L * 1024 * 1024;
    public const string Manifest = "manifest.json", CardPage = "card.html", SettingsPage = "settings.html";

    /// <summary>카드 화면(CSS 픽셀)의 기준 넓이. 비율만 받고 넓이는 이 값으로 맞춘다(1:1이면 200×200).</summary>
    public const double CardArea = 200 * 200;

    /// <summary>설정 화면(CSS 픽셀 = 창의 DIP)의 넓이. 3:4면 약 312×416.</summary>
    public const double SettingsArea = 360 * 360;

    private static readonly Regex IdPattern = new(@"^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex CardIdPattern = new(@"^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly string[] SystemFields = { "cpu", "gpu", "memory", "battery", "storage", "uptime", "network" };
    private static readonly string[] ControlFields = { "volume", "brightness" };

    private DardPackage() { }

    public string Path { get; private init; } = "";
    /// <summary>파일 전체의 SHA-256(소문자 16진수). 승인은 이 값에 묶인다.</summary>
    public string Hash { get; private init; } = "";
    public string Id { get; private init; } = "";
    public string Name { get; private init; } = "";
    public string Version { get; private init; } = "";
    public IReadOnlyList<DardCardInfo> Cards { get; private init; } = Array.Empty<DardCardInfo>();
    public byte[] CardHtml { get; private init; } = Array.Empty<byte>();
    public byte[]? SettingsHtml { get; private init; }
    /// <summary>설정 화면 비율. settings.html이 없으면 null.</summary>
    public (double W, double H)? SettingsRatio { get; private init; }
    public IReadOnlyList<DardPermission> Permissions { get; private init; } = Array.Empty<DardPermission>();

    /// <summary>인터넷과 통신해도 되는지(내부망·이 PC는 언제나 막힘). 아니면 브라우저의 모든 연결이 막다른 길로 간다.</summary>
    public bool Internet => Permissions.Any(p => p.Key == "internet");

    /// <summary>카드마다 브라우저 저장소를 따로 쓰는지(매니페스트 "storage": "card"). 아니면 모든 카드가 하나를 같이 쓴다("shared", 기본).</summary>
    public bool StoragePerCard { get; private init; }

    public const long DefaultQuota = 128L * 1024 * 1024, LargeQuota = 2L * 1024 * 1024 * 1024;

    /// <summary>주소(origin)마다 쓸 수 있는 IndexedDB·OPFS·Cache 용량. localStorage는 브라우저가 따로 약 5MB로 묶는다.</summary>
    public long Quota => Permissions.Any(p => p.Key == "storage.large") ? LargeQuota : DefaultQuota;

    /// <summary>
    /// 저장소가 있는 곳. 브라우저 데이터 폴더(online/offline)와 나누는 방식이 같으면 같은 저장소를 본다.
    /// 업데이트로 이 값이 바뀌면 불러오기 전에 저장소를 옮긴다.
    /// </summary>
    public string StorageLocation => (Internet ? "online" : "offline") + "/v2/" + (StoragePerCard ? "card" : "shared");

    /// <summary>.dard의 가상 호스트. 저장소를 같이 쓰면 모든 카드와 설정 화면이 이 주소에서 뜬다.</summary>
    public string Host => StorageHost(Id, "s--v2");

    private static string StorageHost(string key, string scope)
    {
        // SHA-256을 DNS용 base32로 표현해 긴 패키지·카드 id도 IndexedDB의 파일 경로를 늘리지 않는다.
        // 가운데 '--' 레이블은 구형 매니페스트 id 문법으로 만들 수 없다.
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var encoded = new StringBuilder(52);
        int bits = 0, value = 0;
        foreach (byte b in SHA256.HashData(Encoding.UTF8.GetBytes(key)))
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5) { bits -= 5; encoded.Append(alphabet[(value >> bits) & 31]); }
        }
        if (bits > 0) encoded.Append(alphabet[(value << (5 - bits)) & 31]);
        return $"{encoded}.{scope}.card.desk";
    }

    /// <summary>카드와 설정 화면의 호스트. 카드별 저장이면 패키지 id와 카드 id를 함께 해시한다.</summary>
    public string HostFor(string cardId) => HostFor(cardId, StoragePerCard);

    private string HostFor(string cardId, bool perCard) => perCard ? StorageHost(Id + "\0" + cardId, "c--v2") : Host;

    /// <summary>이 .dard가 쓰는 모든 origin(저장소 단위).</summary>
    public IReadOnlyList<string> Origins =>
        StoragePerCard ? Cards.Select(c => "https://" + HostFor(c.Id)).ToList() : new[] { "https://" + Host };

    /// <summary>카드의 옛 저장소 위치(perCard 방식이었는지에 따라)에서 이 카드가 쓰던 origin.</summary>
    public string OriginFor(string cardId, bool perCard) => "https://" + HostFor(cardId, perCard);

    public string LegacyOriginFor(string cardId, bool perCard) => "https://" + (perCard ? cardId + "." : "") + Id + ".card.desk";

    /// <summary>이 .dard의 주소인지(다른 카드의 주소 포함).</summary>
    public bool OwnsHost(string host) =>
        string.Equals(host, Host, StringComparison.OrdinalIgnoreCase) ||
        Cards.Any(c => string.Equals(host, HostFor(c.Id, true), StringComparison.OrdinalIgnoreCase));

    public static DardPackage Load(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxUnpacked) throw new DardException("파일이 너무 커요(32MB까지).");
        return Parse(File.ReadAllBytes(path), path);
    }

    public static DardPackage Parse(byte[] bytes, string path)
    {
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var files = Unzip(bytes);
        if (!files.TryGetValue(Manifest, out var manifestBytes)) throw new DardException("manifest.json이 없어요.");
        if (!files.TryGetValue(CardPage, out var cardHtml)) throw new DardException("card.html이 없어요.");
        files.TryGetValue(SettingsPage, out var settingsHtml);

        JsonElement m;
        try
        {
            using var doc = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            m = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new DardException("manifest.json을 읽을 수 없어요: " + ex.Message);
        }
        if (m.ValueKind != JsonValueKind.Object) throw new DardException("manifest.json이 객체가 아니에요.");

        if (!m.TryGetProperty("dard", out var ver) || ver.ValueKind != JsonValueKind.Number || ver.GetDouble() != 1)
            throw new DardException("지원하지 않는 형식이에요(\"dard\": 1만 열 수 있어요).");
        string id = Text(m, "id", 100);
        if (!IdPattern.IsMatch(id) || id.Split('.').Any(label => label.Length > 63))
            throw new DardException("id는 영어 소문자·숫자·점·하이픈으로 써 주세요(예: com.example.clock).");
        string name = Text(m, "name", 40);
        string version = Text(m, "version", 20);

        var cards = new List<DardCardInfo>();
        if (!m.TryGetProperty("cards", out var cardsEl) || cardsEl.ValueKind != JsonValueKind.Array || cardsEl.GetArrayLength() == 0)
            throw new DardException("cards에 카드를 한 장 이상 적어 주세요.");
        if (cardsEl.GetArrayLength() > 64) throw new DardException("카드가 너무 많아요(64장까지).");
        foreach (var c in cardsEl.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object) throw new DardException("cards의 항목은 객체여야 해요.");
            string cid = Text(c, "id", 32);
            if (!CardIdPattern.IsMatch(cid)) throw new DardException($"카드 id '{cid}'는 영어 소문자·숫자·하이픈으로 써 주세요.");
            if (cards.Any(x => x.Id == cid)) throw new DardException($"카드 id '{cid}'가 두 번 나와요.");
            string cname = c.TryGetProperty("name", out _) ? Text(c, "name", 40) : name;
            var (rw, rh) = Ratio(c, $"카드 '{cid}'");
            cards.Add(new DardCardInfo(cid, cname, rw, rh));
        }

        (double, double)? settingsRatio = null;
        if (m.TryGetProperty("settings", out var settingsEl))
        {
            if (settingsHtml == null) throw new DardException("settings가 있으면 settings.html도 넣어 주세요.");
            if (settingsEl.ValueKind != JsonValueKind.Object) throw new DardException("settings는 객체여야 해요.");
            settingsRatio = Ratio(settingsEl, "설정 화면");
        }
        else if (settingsHtml != null) settingsRatio = (3, 4);

        bool perCard = false;
        if (m.TryGetProperty("storage", out var storageEl))
        {
            perCard = (storageEl.ValueKind == JsonValueKind.String ? storageEl.GetString() : null) switch
            {
                "shared" => false,
                "card" => true,
                _ => throw new DardException("storage는 \"shared\"나 \"card\"로 적어 주세요."),
            };
        }

        return new DardPackage
        {
            Path = path,
            Hash = hash,
            Id = id,
            Name = name,
            Version = version,
            Cards = cards,
            CardHtml = cardHtml,
            SettingsHtml = settingsHtml,
            SettingsRatio = settingsRatio,
            Permissions = ReadPermissions(m),
            StoragePerCard = perCard,
        };
    }

    /// <summary>비율(가로:세로)과 넓이로 크기를 정한다.</summary>
    public static Size SizeFor(double ratioW, double ratioH, double area)
    {
        double k = ratioW / ratioH;
        return new Size(Math.Sqrt(area * k), Math.Sqrt(area / k));
    }

    /// <summary>
    /// zip에서 허용한 파일만 꺼낸다. 헤더에 적힌 크기는 거짓일 수 있으니(zip 폭탄) 실제로 풀면서 읽은 바이트를 센다.
    /// </summary>
    private static Dictionary<string, byte[]> Unzip(byte[] bytes)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                string n = entry.FullName;
                if (n is not (Manifest or CardPage or SettingsPage))
                    throw new DardException($"들어 있으면 안 되는 항목이 있어요: {n}");
                if (files.ContainsKey(n)) throw new DardException($"{n}이 두 번 들어 있어요.");
                if (total + entry.Length > MaxUnpacked) throw new DardException("풀었을 때 32MB를 넘어요.");
                using var s = entry.Open();
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = s.Read(chunk, 0, chunk.Length)) > 0)
                {
                    total += read;
                    if (total > MaxUnpacked) throw new DardException("풀었을 때 32MB를 넘어요.");
                    buffer.Write(chunk, 0, read);
                }
                files[n] = buffer.ToArray();
            }
        }
        catch (InvalidDataException)
        {
            throw new DardException("zip 파일이 아니거나 손상됐어요.");
        }
        return files;
    }

    private static string Text(JsonElement o, string key, int max)
    {
        if (!o.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()))
            throw new DardException($"{key}를 적어 주세요.");
        string s = v.GetString()!.Trim();
        if (s.Length > max) throw new DardException($"{key}가 너무 길어요({max}자까지).");
        return s;
    }

    private static (double, double) Ratio(JsonElement o, string what)
    {
        if (!o.TryGetProperty("ratio", out var r) || r.ValueKind != JsonValueKind.Array || r.GetArrayLength() != 2 ||
            r[0].ValueKind != JsonValueKind.Number || r[1].ValueKind != JsonValueKind.Number)
            throw new DardException($"{what}의 ratio를 [가로, 세로]로 적어 주세요(예: [2, 1]).");
        double w = r[0].GetDouble(), h = r[1].GetDouble();
        if (!(w > 0) || !(h > 0) || !double.IsFinite(w) || !double.IsFinite(h) || w / h > 8 || h / w > 8)
            throw new DardException($"{what}의 ratio는 양수이고, 한쪽이 다른 쪽의 8배를 넘지 않아야 해요.");
        return (w, h);
    }

    /// <summary>매니페스트의 권한 목록. 모르는 권한이 있으면 거부한다.</summary>
    private static IReadOnlyList<DardPermission> ReadPermissions(JsonElement m)
    {
        var list = new List<DardPermission>();
        if (!m.TryGetProperty("permissions", out var p)) return list;
        if (p.ValueKind != JsonValueKind.Object) throw new DardException("permissions는 객체여야 해요.");
        foreach (var prop in p.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "system":
                    var fields = Strings(prop.Value, prop.Name, SystemFields).Distinct().OrderBy(f => Array.IndexOf(SystemFields, f)).ToList();
                    string all = string.Join(", ", fields);
                    list.AddRange(fields.Select(f => new DardPermission("system:" + f, $"시스템 상태 읽기 ({all})", false)));
                    break;
                case "notify":
                    if (Flag(prop)) list.Add(new DardPermission("notify", "알림 보내기", false));
                    break;
                case "system.control":
                    var controls = Strings(prop.Value, prop.Name, ControlFields).Distinct().OrderBy(f => Array.IndexOf(ControlFields, f)).ToList();
                    string both = string.Join(", ", controls);
                    list.AddRange(controls.Select(f => new DardPermission("system.control:" + f, $"소리·화면 밝기 조절 ({both})", false)));
                    break;
                case "internet":
                    if (Flag(prop)) list.Add(new DardPermission("internet", "인터넷과 통신 (내부망 제외)", true));
                    break;
                case "storage.large":
                    if (Flag(prop)) list.Add(new DardPermission("storage.large", "큰 저장 공간 (2GB까지)", true));
                    break;
                default:
                    throw new DardException($"모르는 권한이에요: {prop.Name}");
            }
        }
        return list;
    }

    private static bool Flag(JsonProperty prop)
    {
        if (prop.Value.ValueKind != JsonValueKind.True && prop.Value.ValueKind != JsonValueKind.False)
            throw new DardException($"{prop.Name}은 true나 false로 적어 주세요.");
        return prop.Value.GetBoolean();
    }

    /// <summary>화면에 보여 줄 권한 이름(같은 묶음은 한 줄로).</summary>
    public IEnumerable<DardPermission> PermissionLines => Permissions.DistinctBy(p => p.Label);

    private static List<string> Strings(JsonElement v, string key, string[]? allowed)
    {
        if (v.ValueKind != JsonValueKind.Array) throw new DardException($"{key}는 목록으로 적어 주세요.");
        var list = new List<string>();
        foreach (var e in v.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String) throw new DardException($"{key}의 항목은 글자여야 해요.");
            string s = e.GetString()!;
            if (allowed != null && !allowed.Contains(s)) throw new DardException($"{key}에 모르는 항목이 있어요: {s}");
            list.Add(s);
        }
        if (list.Count == 0) throw new DardException($"{key}가 비어 있어요.");
        return list;
    }
}
