using System;
using System.IO;

namespace DeskCards.Expanded;

/// <summary>카드 창 이벤트를 늘 파일에 남긴다(%LOCALAPPDATA%\DeskCards\card-events.log, 1MB를 넘으면 새로 시작). 깜박임 같은 일을 나중에 기록으로 찾으려고 둔다.</summary>
internal static class CardLog
{
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskCards", "card-events.log");

    public static void Write(string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > 1_000_000) File.Delete(Path);
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
