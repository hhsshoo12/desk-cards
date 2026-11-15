using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskCards;

/// <summary>
/// .dard 카드 폴더의 manifest.json. 화면(html)과 실행 파일(exe)은 둘 다 선택이지만 최소 하나는 있어야 한다.
/// mode가 "background"면 카드 화면이 숨겨져도 exe가 계속 도는 상시 동작 위젯이다.
/// </summary>
internal sealed record WebCardManifest(string Id, string Name, double Width, double Height, string? Html, string? Exe, bool Background)
{
    public const string FileName = "manifest.json";
    private static readonly Regex IdPattern = new("^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant);

    public static WebCardManifest Load(string directory)
    {
        try
        {
            directory = Path.GetFullPath(directory);
            using var stream = File.OpenRead(Path.Combine(directory, FileName));
            using var json = JsonDocument.Parse(stream);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new WebCardException("manifest.json은 객체여야 해요.");
            string id = ReadText(root, "id");
            if (!IdPattern.IsMatch(id)) throw new WebCardException("id는 영어 소문자·숫자·점·하이픈으로 써 주세요(예: com.example.clock).");
            string name = ReadText(root, "name");
            double width = 300, height = 200;
            if (root.TryGetProperty("size", out var size))
            {
                if (size.ValueKind != JsonValueKind.Array || size.GetArrayLength() != 2 ||
                    !size[0].TryGetDouble(out width) || !size[1].TryGetDouble(out height) ||
                    !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
                    throw new WebCardException("size에는 0보다 큰 가로·세로 크기를 넣어 주세요.");
            }
            string? html = ReadFile(root, "html", directory);
            string? exe = ReadFile(root, "exe", directory);
            bool background = false;
            if (root.TryGetProperty("mode", out var mode))
            {
                string? text = mode.ValueKind == JsonValueKind.String ? mode.GetString() : null;
                if (text is not ("card" or "background")) throw new WebCardException("mode는 \"card\" 또는 \"background\"여야 해요.");
                background = text == "background";
            }
            if (html == null && exe == null) throw new WebCardException("html이나 exe 중 하나는 있어야 해요.");
            return new WebCardManifest(id, name, width, height, html, exe, background);
        }
        catch (WebCardException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            throw new WebCardException("manifest.json을 읽을 수 없어요: " + ex.Message, ex);
        }
    }

    private static string ReadText(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new WebCardException($"{key}을 입력해 주세요.");
        return value.GetString()!;
    }

    /// <summary>카드 폴더 안에 있는 파일 이름(하위 폴더 포함). 없으면 null, 폴더 밖이거나 파일이 없으면 오류.</summary>
    private static string? ReadFile(JsonElement root, string key, string directory)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        string? name = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) throw new WebCardException($"{key}에는 파일 이름을 넣어 주세요.");
        string full = Path.GetFullPath(Path.Combine(directory, name));
        if (!full.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new WebCardException($"{key}는 카드 폴더 안의 파일이어야 해요.");
        if (!File.Exists(full)) throw new WebCardException($"{key} 파일을 찾을 수 없어요: {name}");
        return name;
    }
}

internal sealed class WebCardException : Exception
{
    public WebCardException(string message, Exception? inner = null) : base(message, inner) { }
}