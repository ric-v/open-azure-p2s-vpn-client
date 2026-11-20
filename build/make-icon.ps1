param([Parameter(Mandatory)] [string] $Out)
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
    $f = [single]$s
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddLine($f*0.50, $f*0.03, $f*0.93, $f*0.19)
    $path.AddBezier($f*0.93, $f*0.19, $f*0.93, $f*0.63, $f*0.76, $f*0.83, $f*0.50, $f*0.98)
    $path.AddBezier($f*0.50, $f*0.98, $f*0.24, $f*0.83, $f*0.07, $f*0.63, $f*0.07, $f*0.19)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF 0, 0, $f, $f
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.ColorTranslator]::FromHtml('#3B82F6')), ([System.Drawing.ColorTranslator]::FromHtml('#1D4ED8')), 90
    $g.FillPath($grad, $path)
    if ($s -ge 32) {
        # subtle highlight on the left half
        $hl = New-Object System.Drawing.Drawing2D.GraphicsPath
        $hl.AddLine($f*0.50, $f*0.03, $f*0.50, $f*0.98)
        $hl.AddBezier($f*0.50, $f*0.98, $f*0.24, $f*0.83, $f*0.07, $f*0.63, $f*0.07, $f*0.19)
        $hl.CloseFigure()
        $hb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(28, 255, 255, 255))
        $g.FillPath($hb, $hl)
    }
    $w = [System.Drawing.Brushes]::White
    $g.FillEllipse($w, $f*0.375, $f*0.28, $f*0.25, $f*0.25)
    $poly = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($f*0.455), ($f*0.47)),
        (New-Object System.Drawing.PointF ($f*0.545), ($f*0.47)),
        (New-Object System.Drawing.PointF ($f*0.575), ($f*0.74)),
        (New-Object System.Drawing.PointF ($f*0.425), ($f*0.74)))
    $g.FillPolygon($w, $poly)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Classic DIB frame: BITMAPINFOHEADER, 32bpp BGRA bottom-up, then a 1bpp AND mask.
        $w2 = New-Object System.IO.BinaryWriter $ms
        $w2.Write([UInt32]40); $w2.Write([Int32]$s); $w2.Write([Int32]($s * 2)); $w2.Write([UInt16]1); $w2.Write([UInt16]32)
        $w2.Write([UInt32]0); $w2.Write([UInt32]($s * $s * 4)); $w2.Write([Int32]0); $w2.Write([Int32]0); $w2.Write([UInt32]0); $w2.Write([UInt32]0)
        for ($y = $s - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $s; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $w2.Write([byte]$c.B); $w2.Write([byte]$c.G); $w2.Write([byte]$c.R); $w2.Write([byte]$c.A)
            }
        }
        $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
        $w2.Write((New-Object byte[] ($maskRow * $s)))   # all zero: alpha channel decides
        $w2.Flush()
    }
    $frames += ,@($s, $ms.ToArray())
    $bmp.Dispose()
}
# ICO container with PNG frames
$fs = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($fr in $frames) {
    $s = $fr[0]; $data = $fr[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($fr in $frames) { $bw.Write([byte[]]$fr[1]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($Out, $fs.ToArray())
"wrote $Out ($($fs.Length) bytes, $($frames.Count) sizes)"
