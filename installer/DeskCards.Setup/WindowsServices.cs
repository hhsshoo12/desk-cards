using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;

namespace DeskCards.Setup;

internal interface IAppProcesses
{
    void RequestQuit();
    bool WaitForExit(string exe, int milliseconds);
    void Kill(string exe);
}

internal sealed class WindowsAppProcesses : IAppProcesses
{
    public void RequestQuit()
    {
        if (!Mutex.TryOpenExisting("DeskCards.SingleInstance", out var mutex)) return;
        using (mutex)
            if (EventWaitHandle.TryOpenExisting("DeskCards.Quit", out var request)) using (request) request.Set();
    }

    private static List<Process> Matching(string exe)
    {
        var result = new List<Process>();
        try
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            {
                bool keep = false;
                try
                {
                    if (process.HasExited) continue;
                    if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                    { result.Add(process); keep = true; }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception ex) { throw new IOException("앱 프로세스 경로를 확인하지 못했습니다.", ex); }
                finally { if (!keep) process.Dispose(); }
            }
            return result;
        }
        catch { foreach (var process in result) process.Dispose(); throw; }
    }

    public bool WaitForExit(string exe, int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            var processes = Matching(exe);
            if (processes.Count == 0) return true;
            foreach (var process in processes) process.Dispose();
            Thread.Sleep(100);
        } while (watch.ElapsedMilliseconds < milliseconds);
        var remaining = Matching(exe);
        foreach (var process in remaining) process.Dispose();
        return remaining.Count == 0;
    }

    public void Kill(string exe)
    {
        var processes = Matching(exe);
        try { foreach (var process in processes) { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } } }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}

internal static class AppStopper
{
    public static void Stop(IAppProcesses processes, string exe)
    {
        processes.RequestQuit();
        if (processes.WaitForExit(exe, 5000)) return;
        processes.Kill(exe);
        if (!processes.WaitForExit(exe, 5000)) throw new SetupFailure("Desk Cards를 끄지 못했어요");
    }
}

internal interface IShortcuts { void Create(string path, string exe, string workingDirectory); }

internal sealed class ShellShortcuts : IShortcuts
{
    internal static Tuple<string, string?> Inspect(string path)
    {
        object com = new ShellLink();
        IntPtr buffer = Marshal.AllocCoTaskMem(32768 * 2);
        try
        {
            ((IPersistFile)com).Load(path, 0);
            ((IShellLinkW)com).GetPath(buffer, 32768, IntPtr.Zero, 0);
            string target = Marshal.PtrToStringUni(buffer)!;
            var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
            ((IPropertyStore)com).GetValue(ref key, out var value);
            try { return Tuple.Create(target, value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null); }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.FreeCoTaskMem(buffer); Marshal.FinalReleaseComObject(com); }
    }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);

    public void Create(string path, string exe, string workingDirectory)
    {
        // COM ShellLink runs on an STA even when installation runs in the background.
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { CreateSta(path, exe, workingDirectory); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error != null) throw new IOException("바로가기를 만들지 못했습니다.", error);
    }

    private static void CreateSta(string path, string exe, string directory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        object com = new ShellLink();
        try
        {
            var link = (IShellLinkW)com;
            link.SetPath(exe); link.SetWorkingDirectory(directory); link.SetIconLocation(exe, 0); link.SetDescription("Desk Cards");
            var store = (IPropertyStore)com;
            var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
            var value = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(SetupEnvironment.AppId) };
            try { store.SetValue(ref key, ref value); store.Commit(); }
            finally { Marshal.FreeCoTaskMem(value.Pointer); }
            ((IPersistFile)com).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(com); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink { }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(IntPtr file, int max, IntPtr findData, int flags);
        void GetIDList(out IntPtr id); void SetIDList(IntPtr id);
        void GetDescription(IntPtr name, int max); void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory(IntPtr dir, int max); void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments(IntPtr args, int max); void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey); void SetHotkey(short hotkey);
        void GetShowCmd(out int cmd); void SetShowCmd(int cmd);
        void GetIconLocation(IntPtr icon, int max, out int index); void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr hwnd, int flags); void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count); void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value); void Commit();
    }
}

internal static class Cleanup
{
    public static string EncodedCommand(string path)
    {
        string script = "$cleanupFile = '" + path.Replace("'", "''") + "'; " +
            "for ($attempt = 0; $attempt -lt 30; $attempt++) { Start-Sleep -Seconds 2; " +
            "try { Remove-Item -LiteralPath $cleanupFile -Force -ErrorAction Stop; break } catch {} }";
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }
    public static void Schedule(string path) => Process.Start(new ProcessStartInfo
    {
        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
        Arguments = "-NoProfile -NonInteractive -EncodedCommand " + EncodedCommand(path),
        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
    })?.Dispose();
}
