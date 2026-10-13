# Renders the app icon from icon.svg next to this script. Every size, from the
# 16 px tray to the 256 px exe icon, comes from that one drawing, so the tray,
# the taskbar, the windows and the installer all show the same thing. Edge,
# which ships with Windows, does the drawing. Writes, under src\Clipline\Assets:
#   Clipline.ico, Clipline.png      the exe, the tray and the windows
#   Clipline-titlebar.png           title bars, 20 px at up to 300 %
#   Clipline-about.png              the About card in settings, 40 px at up to 300 %
#   Package\*.png                   the tiles and taskbar icons of the MSIX
# Run from anywhere:  pwsh build/icon/make-icon.ps1

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

# Size, and whether it goes into the .ico.
$sizes = @(
    @(16, $true), @(20, $true), @(24, $true), @(32, $true), @(36, $true), @(40, $true), @(48, $true),
    @(64, $true), @(256, $true),
    @(44, $false), @(50, $false), @(150, $false), @(60, $false), @(120, $false))

# Lay every size out in one row and take a single screenshot of it.
$work = Join-Path ([System.IO.Path]::GetTempPath()) "clipline-icon-$([guid]::NewGuid())"
New-Item -ItemType Directory $work | Out-Null
$src = 'file:///' + (Join-Path $PSScriptRoot 'icon.svg').Replace('\', '/')
$x = 0; $slots = @(); $imgs = ''
foreach ($entry in $sizes) {
    $size, $ico = $entry
    $imgs += "<img src='$src' style='position:absolute;left:${x}px;top:0;width:${size}px;height:${size}px'>"
    $slots += @{ Size = $size; X = $x; Ico = $ico }
    $x += $size + 8
}
$sheet = Join-Path $work 'sheet.html'
Set-Content $sheet "<html><body style='margin:0;background:transparent'>$imgs</body></html>"
$url = 'file:///' + $sheet.Replace('\', '/')
$png = Join-Path $work 'sheet.png'
$edgeProfile = Join-Path $work 'profile'
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
Start-Process $edge -WindowStyle Hidden -ArgumentList @(
    '--headless=new', '--disable-gpu', '--hide-scrollbars', '--allow-file-access-from-files', '--no-first-run', '--force-device-scale-factor=1',
    '--default-background-color=00000000', '--virtual-time-budget=2000', "--user-data-dir=$edgeProfile",
    "--window-size=$x,256", "--screenshot=$png", $url) | Out-Null
# Edge returns before the screenshot lands, and leaves helpers running.
for ($i = 0; $i -lt 150 -and -not (Test-Path $png); $i++) { Start-Sleep -Milliseconds 200 }
Start-Sleep -Milliseconds 500
Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -like "*$edgeProfile*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
if (-not (Test-Path $png)) { throw 'Edge did not render the icon.' }

function ToPng([System.Drawing.Bitmap]$bmp) {
    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    , $stream.ToArray()
}

$sheetBitmap = [System.Drawing.Bitmap]::FromFile($png)
$images = @{}
foreach ($slot in $slots) {
    $rect = New-Object System.Drawing.Rectangle $slot.X, 0, $slot.Size, $slot.Size
    $bmp = $sheetBitmap.Clone($rect, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $slot.Png = ToPng $bmp
    $images["$($slot.Size)"] = $slot.Png
    $bmp.Dispose()
}

# The wide tile: the 150 px icon in the middle of a clear 310 x 150.
$wide = New-Object System.Drawing.Bitmap 310, 150, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($wide)
$g.Clear([System.Drawing.Color]::Transparent)
$tile = [System.Drawing.Bitmap]::FromStream((New-Object System.IO.MemoryStream (, $images['150'])))
$g.DrawImageUnscaled($tile, 80, 0)
$g.Dispose(); $tile.Dispose()
$widePng = ToPng $wide
$wide.Dispose()
$sheetBitmap.Dispose()
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue

# ICO container with PNG payloads, which Windows has read since Vista.
$icoSlots = @($slots | Where-Object { $_.Ico })
$count = $icoSlots.Count
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$count)
$offset = 6 + 16 * $count
foreach ($slot in $icoSlots) {
    $dim = if ($slot.Size -ge 256) { 0 } else { $slot.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$slot.Png.Length); $w.Write([uint32]$offset)
    $offset += $slot.Png.Length
}
foreach ($slot in $icoSlots) { $w.Write($slot.Png) }
$w.Flush()

$dir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\src\Clipline\Assets'))
$package = Join-Path $dir 'Package'
New-Item -ItemType Directory -Force $package | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $dir 'Clipline.ico'), $out.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $dir 'Clipline.png'), $images['256'])
# Drawn large so they stay sharp when Windows scales the display up.
[System.IO.File]::WriteAllBytes((Join-Path $dir 'Clipline-titlebar.png'), $images['60'])
[System.IO.File]::WriteAllBytes((Join-Path $dir 'Clipline-about.png'), $images['120'])

# The names the MSIX manifest asks for. The unplated target sizes are what
# the taskbar, Start and the Alt+Tab switcher show.
$tiles = [ordered]@{
    'Square44x44Logo.png'   = $images['44']
    'Square150x150Logo.png' = $images['150']
    'StoreLogo.png'         = $images['50']
    'Wide310x150Logo.png'   = $widePng
}
foreach ($size in 16, 24, 32, 48, 256) {
    $tiles["Square44x44Logo.targetsize-${size}_altform-unplated.png"] = $images["$size"]
}
foreach ($name in $tiles.Keys) { [System.IO.File]::WriteAllBytes((Join-Path $package $name), $tiles[$name]) }
"Wrote $dir"
