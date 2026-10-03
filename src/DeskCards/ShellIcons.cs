using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskCards;

/// <summary>셸에서 파일/바로가기의 고해상도 아이콘을 가져온다.</summary>
internal static partial class ShellIcons
{
    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;
    private const int IconPx = 96;

    private static readonly Dictionary<string, (long Stamp, ImageSource? Image)> Cache = new(StringComparer.OrdinalIgnoreCase);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path, IntPtr pbc, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    private static ImageSource? Get(string path)
    {
        long stamp = SafeStamp(path);
        if (Cache.TryGetValue(path, out var cached) && cached.Stamp == stamp) return cached.Image;
        var img = Load(path);
        if (Cache.Count >= 1024) Cache.Clear();
        Cache[path] = (stamp, img);
        return img;
    }

    private static long SafeStamp(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks; } catch { return 0; }
    }

    private static ImageSource? Load(string path)
    {
        IShellItemImageFactory? factory = null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
            if (factory.GetImage(new SIZE { cx = IconPx, cy = IconPx }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbm) != 0)
                return null;
            return FromHBitmap(hbm);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbm != IntPtr.Zero) Native.DeleteObject(hbm);
            if (factory != null) Marshal.ReleaseComObject(factory);
        }
    }

    private static BitmapSource? FromHBitmap(IntPtr hbm)
    {
        var bmp = new Native.BITMAP();
        if (Native.GetObject(hbm, Marshal.SizeOf<Native.BITMAP>(), ref bmp) == 0) return null;
        int w = bmp.bmWidth, h = bmp.bmHeight;
        var bi = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var buf = new byte[w * h * 4];
        IntPtr hdc = Native.GetDC(IntPtr.Zero);
        try
        {
            if (Native.GetDIBits(hdc, hbm, 0, (uint)h, buf, ref bi, 0) == 0) return null;
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, hdc);
        }

        // 알파 채널이 없는 옛날 아이콘은 전부 0으로 오므로 불투명 처리한다.
        bool anyAlpha = false;
        for (int i = 3; i < buf.Length; i += 4)
            if (buf[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha)
            for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;

        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buf, w * 4);
        src.Freeze();
        return src;
    }
}
