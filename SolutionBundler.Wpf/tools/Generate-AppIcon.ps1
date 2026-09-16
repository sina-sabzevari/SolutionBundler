param(
    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

Add-Type -AssemblyName System.Drawing

$size = 256
$bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)

$backgroundBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 22, 119, 210))
$folderBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 124, 196, 255))
$paperBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
$linePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 22, 119, 210), 9)
$linePen.StartCap = $linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

$graphics.FillRoundedRectangle(
    $backgroundBrush,
    [System.Drawing.RectangleF]::new(8, 8, 240, 240),
    [System.Drawing.SizeF]::new(44, 44))

$folderPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
$folderPath.AddPolygon([System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new(42, 72),
    [System.Drawing.PointF]::new(104, 72),
    [System.Drawing.PointF]::new(122, 92),
    [System.Drawing.PointF]::new(214, 92),
    [System.Drawing.PointF]::new(214, 198),
    [System.Drawing.PointF]::new(42, 198)
))
$graphics.FillPath($folderBrush, $folderPath)

$graphics.FillRectangle($paperBrush, 80, 106, 104, 106)
$graphics.DrawLine($linePen, 101, 139, 163, 139)
$graphics.DrawLine($linePen, 101, 164, 163, 164)
$graphics.DrawLine($linePen, 101, 189, 145, 189)

$pngStream = [System.IO.MemoryStream]::new()
$bitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $pngStream.ToArray()

$targetDirectory = Split-Path -Parent $OutputPath
if ($targetDirectory) { [System.IO.Directory]::CreateDirectory($targetDirectory) | Out-Null }
$fileStream = [System.IO.File]::Create($OutputPath)
$writer = [System.IO.BinaryWriter]::new($fileStream)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]1)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([uint16]1)
$writer.Write([uint16]32)
$writer.Write([uint32]$pngBytes.Length)
$writer.Write([uint32]22)
$writer.Write($pngBytes)

$writer.Dispose()
$pngStream.Dispose()
$folderPath.Dispose()
$linePen.Dispose()
$paperBrush.Dispose()
$folderBrush.Dispose()
$backgroundBrush.Dispose()
$graphics.Dispose()
$bitmap.Dispose()
