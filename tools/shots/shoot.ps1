# Photographs one window, by the path of the executable that owns it.
#
# -Exe is required, and it is required because of what happened without it: an earlier version
# selected the process by name alone, found the copy of Deck the author had running, photographed
# that, and then killed it. It was off air at the time. Nothing here selects a window it was not
# given the exact path to.
#
# PrintWindow rather than a screen grab, so nothing behind or in front of the window is captured -
# a full-screen shot of somebody's machine picks up whatever else they had open.

param(
    [Parameter(Mandatory = $true)][string]$Out,
    [Parameter(Mandatory = $true)][string]$Exe
)

Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Shot {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@

$exe = (Resolve-Path $Exe).Path

$proc = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exe)) -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 -and $_.Path -eq $exe } |
    Select-Object -First 1

if (-not $proc) { throw "no window belonging to $exe - refusing to photograph anything else" }

$handle = $proc.MainWindowHandle
$rect = New-Object Shot+RECT
[Shot]::GetWindowRect($handle, [ref]$rect) | Out-Null

$bitmap = New-Object System.Drawing.Bitmap(($rect.R - $rect.L), ($rect.B - $rect.T))
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$dc = $graphics.GetHdc()

# 2 is PW_RENDERFULLCONTENT, without which a composited window comes back blank.
[Shot]::PrintWindow($handle, $dc, 2) | Out-Null

$graphics.ReleaseHdc($dc)
$graphics.Dispose()

$bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Output ("captured {0}x{1} to {2}" -f $bitmap.Width, $bitmap.Height, $Out)
$bitmap.Dispose()
