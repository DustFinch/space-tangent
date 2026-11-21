# SPDX-License-Identifier: GPL-2.0-or-later
param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$ArgumentsBase64,
    [Parameter(Mandatory=$true)][string]$WorkingDirectory,
    [Parameter(Mandatory=$true)][string]$Log,
    [ValidateRange(1,3600)][int]$TimeoutSeconds=600
)
$ErrorActionPreference='Stop'
if (-not $IsWindows) { throw 'Inactive Windows desktop isolation requires Windows.' }
$arguments=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($ArgumentsBase64))
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class SpaceTangentIsolatedGpu {
 [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct Startup {
  public int cb; public string reserved, desktop, title; public int x,y,cx,cy,charsX,charsY,fill,flags;
  public short show,reservedSize; public IntPtr reservedPtr,input,output,error;
 }
 [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr process,thread; public uint pid,tid; }
 [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr devmode,uint flags,uint access,IntPtr security);
 [DllImport("user32.dll",SetLastError=true)] static extern bool CloseDesktop(IntPtr desktop);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcess(string app,StringBuilder command,IntPtr ps,IntPtr ts,bool inherit,uint flags,IntPtr env,string cwd,ref Startup start,out ProcessInfo info);
 [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
 [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process,out uint code);
 [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process,uint code);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetHandleInformation(IntPtr handle,uint mask,uint flags);
 public static int Run(string executable,string args,string cwd,string stdout,int timeoutSeconds) {
  string name="SpaceTangent-GPU-"+Guid.NewGuid().ToString("N");
  IntPtr desktop=CreateDesktop(name,IntPtr.Zero,IntPtr.Zero,0,0x10000000,IntPtr.Zero);
  if(desktop==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
  ProcessInfo info=new ProcessInfo();
  try {
   using(var file=File.Create(stdout)) {
    IntPtr output=file.SafeFileHandle.DangerousGetHandle();
    if(!SetHandleInformation(output,1,1)) throw new Win32Exception(Marshal.GetLastWin32Error());
    var start=new Startup { cb=Marshal.SizeOf(typeof(Startup)),desktop="WinSta0\\"+name,flags=0x101,show=0,output=output,error=output };
    if(!CreateProcess(executable,new StringBuilder("\""+executable+"\" "+args),IntPtr.Zero,IntPtr.Zero,true,0x08000000,IntPtr.Zero,cwd,ref start,out info))
     throw new Win32Exception(Marshal.GetLastWin32Error());
    if(WaitForSingleObject(info.process,checked((uint)timeoutSeconds * 1000))==258) { TerminateProcess(info.process,124); WaitForSingleObject(info.process,5000); return 124; }
    uint code; GetExitCodeProcess(info.process,out code); return (int)code;
   }
  } finally { if(info.thread!=IntPtr.Zero) CloseHandle(info.thread); if(info.process!=IntPtr.Zero) CloseHandle(info.process); CloseDesktop(desktop); }
 }
}
'@
$result=[SpaceTangentIsolatedGpu]::Run($Executable,$arguments,$WorkingDirectory,$Log,$TimeoutSeconds)
Get-Content -LiteralPath $Log -Tail 12
Write-Output "Isolated desktop process exited: $result"
exit $result
