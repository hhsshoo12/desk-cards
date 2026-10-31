param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$Log)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$Log = [IO.Path]::GetFullPath($Log)
if ($Executable.Contains('"') -or $Log.Contains('"')) { throw 'Invalid path' }
$env:DESKCARDS_TEST_HEADLESS = '1'
$env:DESKCARDS_ISOLATED_DESKTOP = '1'
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
using System.ComponentModel;
public static class DeskCardsIsolatedTests {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct Startup {
    public int Size; public string Reserved; public string Desktop; public string Title;
    public int X,Y,XSize,YSize,XCount,YCount,Fill,Flags; public short Show,ReservedCount;
    public IntPtr ReservedPointer,Input,Output,Error;
  }
  [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr Process,Thread; public uint ProcessId,ThreadId; }
  [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,uint flags,uint access,IntPtr security);
  [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcess(string app,StringBuilder command,IntPtr pa,IntPtr ta,bool inherit,uint flags,IntPtr env,string directory,ref Startup startup,out ProcessInfo process);
  [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
  [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process,out uint code);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
  public static int Run(string exe,string log) {
    string name="DeskCards-tests-"+Guid.NewGuid().ToString("N");
    IntPtr desktop=CreateDesktop(name,IntPtr.Zero,IntPtr.Zero,0,0x01ff,IntPtr.Zero);
    if(desktop==IntPtr.Zero) throw new Win32Exception();
    try {
      var startup=new Startup { Size=Marshal.SizeOf(typeof(Startup)), Desktop="winsta0\\"+name };
      var command=new StringBuilder("cmd.exe /d /s /c \"\""+exe+"\" > \""+log+"\" 2>&1\"");
      ProcessInfo process;
      if(!CreateProcess(null,command,IntPtr.Zero,IntPtr.Zero,false,0x08000000,IntPtr.Zero,System.IO.Path.GetDirectoryName(exe),ref startup,out process)) throw new Win32Exception();
      try { WaitForSingleObject(process.Process,0xffffffff); uint code; if(!GetExitCodeProcess(process.Process,out code))throw new Win32Exception();return (int)code; }
      finally { CloseHandle(process.Thread);CloseHandle(process.Process); }
    } finally { CloseDesktop(desktop); }
  }
}
'@
$result = [DeskCardsIsolatedTests]::Run($Executable, $Log)
Get-Content -LiteralPath $Log
exit $result
