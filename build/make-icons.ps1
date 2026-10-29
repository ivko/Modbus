# Generates the program icons: multi-size .ico files (classic 32-bit DIB entries, PNG for 256 px).
# Run from anywhere: powershell -ExecutionPolicy Bypass -File build\make-icons.ps1
param()
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot

function New-IconImage([int]$Size, [string]$Letter, [string]$Color) {
    $bmp = New-Object System.Drawing.Bitmap $Size, $Size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(0.5, $Size * 0.03)
    $d = [Math]::Max(3, $Size * 0.36)
    $w = $Size - 2 * $pad
    $shape = New-Object System.Drawing.Drawing2D.GraphicsPath
    $shape.AddArc($pad, $pad, $d, $d, 180, 90)
    $shape.AddArc($pad + $w - $d, $pad, $d, $d, 270, 90)
    $shape.AddArc($pad + $w - $d, $pad + $w - $d, $d, $d, 0, 90)
    $shape.AddArc($pad, $pad + $w - $d, $d, $d, 90, 90)
    $shape.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($Color))), $shape)

    $font = New-Object System.Drawing.Font('Segoe UI', [single]($Size * 0.68), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = 'Center'
    $format.LineAlignment = 'Center'
    $box = New-Object System.Drawing.RectangleF 0, ([single]($Size * 0.03)), $Size, $Size
    $g.DrawString($Letter, $font, [System.Drawing.Brushes]::White, $box, $format)
    $g.Dispose()
    return $bmp
}

function Get-EntryBytes([System.Drawing.Bitmap]$Bmp) {
    $size = $Bmp.Width
    $ms = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        $Bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return $ms.ToArray()
    }
    $bw = New-Object System.IO.BinaryWriter $ms
    $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)
    # BITMAPINFOHEADER; the height counts the colour image plus the AND mask.
    $bw.Write([uint32]40); $bw.Write([int32]$size); $bw.Write([int32]($size * 2))
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
    $bw.Write([uint32]($size * $size * 4 + $maskRow * $size))
    $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $Bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $bw.Write((New-Object byte[] ($maskRow * $size)))
    $bw.Flush()
    return $ms.ToArray()
}

function New-Icon([string]$OutFile, [string]$Letter, [string]$Color) {
    $sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
    $entries = foreach ($s in $sizes) {
        $bmp = New-IconImage $s $Letter $Color
        [pscustomobject]@{ Size = $s; Data = (Get-EntryBytes $bmp) }
        $bmp.Dispose()
    }
    $out = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $out
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    foreach ($e in $entries) {
        $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$e.Data.Length); $bw.Write([uint32]$offset)
        $offset += $e.Data.Length
    }
    foreach ($e in $entries) { $bw.Write([byte[]]$e.Data) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($OutFile, $out.ToArray())
    "wrote $OutFile ($($out.Length) bytes)"
}

New-Icon (Join-Path $root 'src\Modbus.App\app.ico') 'M' '#1F5FAF'
New-Icon (Join-Path $root 'src\Modbus.Slave\app.ico') 'S' '#2E7D32'
