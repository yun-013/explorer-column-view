# ColumnView idle CPU meter that does NOT steal focus.
#
#   powershell -ExecutionPolicy Bypass -File measure-idle.ps1
#
# Start this FIRST, then click the ColumnView window and leave it alone.
# Task Manager cannot see this bug: opening it takes the foreground away
# from ColumnView, and the CPU burn only happens while ColumnView is in front.
param([int]$Seconds = 60, [int]$Interval = 5)

Add-Type @"
using System;using System.Runtime.InteropServices;
public class M {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
}
"@ -ErrorAction SilentlyContinue

$out = Join-Path $PSScriptRoot 'measure-idle.log'
$p = Get-Process ColumnView -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { "ColumnView is not running." | Tee-Object $out; exit 1 }

"=== $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  pid=$($p.Id) ===" | Tee-Object $out
"Click the ColumnView window now, then leave the machine alone." | Tee-Object $out -Append

$reps = [math]::Max(1, [int]($Seconds / $Interval))
for ($i = 1; $i -le $reps; $i++) {
  $p.Refresh(); $t0 = $p.TotalProcessorTime
  Start-Sleep -Seconds $Interval
  $p.Refresh()
  $cpu = ($p.TotalProcessorTime - $t0).TotalMilliseconds / ($Interval * 10)
  $fgp = 0; [void][M]::GetWindowThreadProcessId([M]::GetForegroundWindow(), [ref]$fgp)
  $fgName = (Get-Process -Id $fgp -ErrorAction SilentlyContinue).ProcessName
  ("[{0,2}] cpu={1,5:N1}%   foreground={2}" -f $i, $cpu, $fgName) | Tee-Object $out -Append
}
"" | Tee-Object $out -Append
"Only the rows with foreground=ColumnView are meaningful." | Tee-Object $out -Append
