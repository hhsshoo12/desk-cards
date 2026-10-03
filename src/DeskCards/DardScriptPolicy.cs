using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using AngleSharp.Html.Parser;

namespace DeskCards;

/// <summary>패키지에 들어 있던 스크립트 본문만 허용한다. HTML을 실행하거나 외부 리소스를 읽지 않는다.</summary>
internal static class DardScriptPolicy
{
    private static readonly ConditionalWeakTable<byte[], Policy> Cache = new();
    private sealed record Policy(string Hashes);

    public static string Hashes(byte[] html) => Cache.GetValue(html, bytes =>
    {
        // HTML 파서의 줄바꿈/문자 참조 처리를 브라우저와 맞춘다. 정규식으로 태그를 추출하지 않는다.
        using var doc = new HtmlParser(new HtmlParserOptions { IsScripting = true }).ParseDocument(Encoding.UTF8.GetString(bytes));
        var hashes = doc.QuerySelectorAll("script").Where(s => !s.HasAttribute("src"))
            .Select(s => "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(s.TextContent))) + "'")
            .Distinct().ToArray();
        return new Policy(hashes.Length == 0 ? "'none'" : string.Join(" ", hashes));
    }).Hashes;
}
