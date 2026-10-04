using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace DeskCards.Setup;

/// <summary>설치할 앱 zip(DeskCards.exe, uninstall.exe, version.txt). 설치기에 들어 있고, 제거기 전용 빌드에는 없다.</summary>
internal interface IAppPackage
{
    /// <summary>들어 있는 앱의 버전. 설치기 버전과 같다.</summary>
    Version Version { get; }
    /// <summary>zip을 path에 새 파일로 쓴다.</summary>
    void CopyTo(string path, CancellationToken token);
}

internal sealed class EmbeddedPackage : IAppPackage
{
    public const string ResourceName = SetupEnvironment.ZipName;
    private static readonly Assembly Self = typeof(EmbeddedPackage).Assembly;

    public Version Version { get; }

    private EmbeddedPackage(Version version) { Version = version; }

    /// <summary>설치기에 들어 있는 앱. 없으면(제거기 전용·개발 빌드) null.</summary>
    public static EmbeddedPackage? Load()
    {
        if (Self.GetManifestResourceInfo(ResourceName) == null) return null;
        var v = Self.GetName().Version!;
        return new EmbeddedPackage(new Version(v.Major, v.Minor, v.Build));
    }

    public void CopyTo(string path, CancellationToken token)
    {
        using var input = Self.GetManifestResourceStream(ResourceName) ?? throw new SetupFailure("설치기에 앱이 들어 있지 않아요", false);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920);
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            token.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
        }
    }

    /// <summary>설치된 버전과 비교. 설치된 쪽이 더 새로우면 양수, 읽을 수 없으면 null.</summary>
    public static int? CompareInstalled(string? installed, Version available) =>
        Version.TryParse(installed, out var current) ? (int?)current.CompareTo(available) : null;
}
