param([string]$Action = 'list', [int]$ProcessId = 0, [string]$Name = '', [string]$OutputPath = '', [int]$X = 0, [int]$Y = 0, [int]$Width = 1600, [int]$Height = 1000)
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing,System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ShaderDesktop {
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int height,bool repaint);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);
}
'@
[ShaderDesktop]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
if ($Action -eq 'capture') {
 $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
 if ($ProcessId) {
  $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$ProcessId)
  $window = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$condition) | Sort-Object { $_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height } -Descending | Select-Object -First 1
  $rect = $window.Current.BoundingRectangle
  $bounds = New-Object System.Drawing.Rectangle([int]$rect.X,[int]$rect.Y,[int]$rect.Width,[int]$rect.Height)
 }
 $bmp = New-Object System.Drawing.Bitmap($bounds.Width,$bounds.Height)
 $g = [System.Drawing.Graphics]::FromImage($bmp)
 $g.CopyFromScreen($bounds.Left,$bounds.Top,0,0,$bounds.Size)
 $bmp.Save($OutputPath)
 $g.Dispose(); $bmp.Dispose(); exit
}
if ($Action -eq 'click') {
 [ShaderDesktop]::SetCursorPos($X,$Y) | Out-Null
 [ShaderDesktop]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
 [ShaderDesktop]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
 exit
}
$root = [System.Windows.Automation.AutomationElement]::RootElement
if ($ProcessId) {
 $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$ProcessId)
 $root = $root.FindAll([System.Windows.Automation.TreeScope]::Children,$condition) | Sort-Object { $_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height } -Descending | Select-Object -First 1
 if (!$root) { throw 'Window not found' }
 [ShaderDesktop]::ShowWindow([IntPtr]$root.Current.NativeWindowHandle,9) | Out-Null
 [ShaderDesktop]::SetForegroundWindow([IntPtr]$root.Current.NativeWindowHandle) | Out-Null
 if ($Action -eq 'resize') {
  [ShaderDesktop]::MoveWindow([IntPtr]$root.Current.NativeWindowHandle,$X,$Y,$Width,$Height,$true) | Out-Null
  exit
 }
}
$nodes = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
foreach ($node in $nodes) {
 $c = $node.Current
 if ($Action -eq 'invoke' -and ($c.AutomationId -eq $Name -or $c.Name -eq $Name)) {
  $pattern = $node.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
  $pattern.Invoke(); Write-Output "Invoked $Name"; exit
 }
 if ($Action -eq 'list') { '{0}|{1}|{2}|{3}' -f $c.ControlType.ProgrammaticName,$c.AutomationId,$c.Name,$c.BoundingRectangle }
}
