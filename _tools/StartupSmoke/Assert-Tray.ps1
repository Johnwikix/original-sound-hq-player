param(
    [Parameter(Mandatory)][int]$PlayerProcessId,
    [switch]$ExpectedAbsent
)
$ErrorActionPreference = 'Stop'

# 在交互式 Windows 会话运行。查询 Explorer 的真实注册状态，不以日志或控件字段代替。
if (-not ('StartupTrayProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
public static class StartupTrayProbe {
    [StructLayout(LayoutKind.Sequential)]
    public struct Identifier {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public Guid Guid;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref Identifier id, out Rect rect);
    public static int Query(string executable, out Rect rect) {
        // H.NotifyIcon 2.4.1: 默认 GUID = SHA256(Environment.ProcessPath + "_") 的前 16 字节。
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(executable + "_"));
        var id = new Identifier {
            Size = (uint)Marshal.SizeOf<Identifier>(),
            Guid = new Guid(hash.AsSpan(0, 16))
        };
        return Shell_NotifyIconGetRect(ref id, out rect);
    }
}
'@
}
$player = Get-Process -Id $PlayerProcessId
if ($player.ProcessName -ne 'OriginalSound HIFI Player') { throw 'Not a player process.' }
$rect = [StartupTrayProbe+Rect]::new()
$result = [StartupTrayProbe]::Query($player.MainModule.FileName, [ref]$rect)
$present = $result -eq 0
if ($present -eq [bool]$ExpectedAbsent) {
    throw "Unexpected tray registration: present=$present, HRESULT=$result, PID=$PlayerProcessId"
}
[pscustomobject]@{
    ProcessId = $PlayerProcessId
    TrayRegistered = $present
    Left = $rect.Left
    Top = $rect.Top
    Right = $rect.Right
    Bottom = $rect.Bottom
}
