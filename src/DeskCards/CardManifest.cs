using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskCards;

/// <summary>카드 폴더의 manifest.json. 크기는 카드의 최초 DIP 크기다.</summary>
internal sealed record CardManifest(string Id, string Name, double Width, double Height, bool Background)
{
    private static readonly Regex IdPattern = new("^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant);

    public static CardManifest Load(string directory)
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(directory, "manifest.json"));
            using var json = JsonDocument.Parse(stream);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new CardManifestException("manifest.json은 객체여야 해요.");
            string id = ReadText(root, "id");
            if (!IdPattern.IsMatch(id) || !FileOps.IsValidGroupName(id))
                throw new CardManifestException("id는 영어 소문자·숫자·점·하이픈으로 써 주세요(예: com.example.clock).");
            string name = ReadText(root, "name");
            if (!root.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Array || size.GetArrayLength() != 2 ||
                !size[0].TryGetDouble(out double width) || !size[1].TryGetDouble(out double height) ||
                !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
                throw new CardManifestException("size에는 0보다 큰 가로·세로 크기를 넣어 주세요.");
            bool background = false;
            if (root.TryGetProperty("background", out var value))
            {
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new CardManifestException("background는 true 또는 false여야 해요.");
                background = value.GetBoolean();
            }
            return new CardManifest(id, name, width, height, background);
        }
        catch (CardManifestException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            throw new CardManifestException("manifest.json을 읽을 수 없어요: " + ex.Message, ex);
        }
    }

    private static string ReadText(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new CardManifestException($"{key}을 입력해 주세요.");
        return value.GetString()!;
    }
}

internal sealed class CardManifestException : Exception
{
    public CardManifestException(string message, Exception? inner = null) : base(message, inner) { }
}
