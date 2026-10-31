using System;
using System.Collections.Generic;
using System.Windows;

namespace DeskCards;

// 웹 카드 창과 브라우저 복구를 패키지 파서 없이 구성하는 화면 모델.
internal sealed record WebCardInfo(string Id, string Name, double Width, double Height);
internal sealed record CardDocument
{
    public const string CardPage = "card.html", SettingsPage = "settings.html";
    public string Path { get; init; } = "";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Url { get; init; }
    public bool Background { get; init; }
    public IReadOnlyList<WebCardInfo> Cards { get; init; } = Array.Empty<WebCardInfo>();
    public byte[] CardHtml { get; init; } = Array.Empty<byte>();
    public byte[]? SettingsHtml { get; init; }
    public string HostFor(string cardId) => Id + ".card.desk";
    public IReadOnlyList<string> Origins => new[] { "https://" + HostFor("") };
}
