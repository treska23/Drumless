param(
    [Parameter(Mandatory = $true)]
    [string] $ProcessName,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class WindowCaptureNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr handle, out Rect rect);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr handle, IntPtr deviceContext, uint flags);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr handle);
}
"@

$process = Get-Process -Name $ProcessName -ErrorAction Stop |
    Where-Object { $_.MainWindowHandle -ne 0 } |
    Select-Object -First 1

if (-not $process) {
    throw "No se encontró una ventana visible para $ProcessName."
}

$rect = New-Object WindowCaptureNative+Rect
if (-not [WindowCaptureNative]::GetWindowRect($process.MainWindowHandle, [ref] $rect)) {
    throw "No se pudo obtener el tamaño de la ventana."
}

$dpi = [WindowCaptureNative]::GetDpiForWindow($process.MainWindowHandle)
$scale = if ($dpi -gt 0) { $dpi / 96.0 } else { 1.0 }
$width = [int](($rect.Right - $rect.Left) * $scale)
$height = [int](($rect.Bottom - $rect.Top) * $scale)
$bitmap = New-Object System.Drawing.Bitmap($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$context = $graphics.GetHdc()

try {
    if (-not [WindowCaptureNative]::PrintWindow($process.MainWindowHandle, $context, 2)) {
        throw "Windows no pudo capturar la ventana."
    }
}
finally {
    $graphics.ReleaseHdc($context)
    $graphics.Dispose()
}

$directory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

Get-Item -LiteralPath $OutputPath | Select-Object FullName, Length
