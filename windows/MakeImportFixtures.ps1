$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression
$folder = Join-Path $PSScriptRoot 'import-fixtures'
$null = New-Item -ItemType Directory -Force -Path $folder

function New-Png($width, $height, $color) {
    $bmp = [System.Drawing.Bitmap]::new($width, $height)
    $canvas = [System.Drawing.Graphics]::FromImage($bmp)
    $buffer = [System.IO.MemoryStream]::new()
    try {
        $canvas.Clear($color)
        $bmp.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$buffer.ToArray()
    }
    finally { $buffer.Dispose(); $canvas.Dispose(); $bmp.Dispose() }
}

function Write-Archive($name, [System.Collections.IDictionary]$entries) {
    $file = [System.IO.File]::Create((Join-Path $folder $name))
    $zip = [System.IO.Compression.ZipArchive]::new($file, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($key in $entries.Keys) {
            $stream = $zip.CreateEntry($key).Open()
            try {
                $bytes = $entries[$key]
                if ($bytes -is [string]) { $bytes = [System.Text.Encoding]::UTF8.GetBytes($bytes) }
                $stream.Write($bytes, 0, $bytes.Length)
            }
            finally { $stream.Dispose() }
        }
    }
    finally { $zip.Dispose() }
}

$red = New-Png 300 500 ([System.Drawing.Color]::Firebrick)
$green = New-Png 320 480 ([System.Drawing.Color]::ForestGreen)
$blue = New-Png 500 300 ([System.Drawing.Color]::RoyalBlue)
$comic = [ordered]@{'漫画/10.png'=$blue; '__MACOSX/extra.png'=$blue; '漫画/2.png'=$green; '漫画/1.png'=$red}
Write-Archive '漫画样本.cbz' $comic
Write-Archive '漫画样本.zip' $comic
Write-Archive '无图片.zip' @{ 'notes.txt' = 'no pictures' }

$container = '<container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="OPS/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>'
$comicPackage = @'
<package xmlns="http://www.idpf.org/2007/opf" version="3.0">
  <manifest><item id="a" href="pages/a.xhtml" media-type="application/xhtml+xml"/><item id="b" href="images/2.png" media-type="image/png"/><item id="c" href="pages/c.xhtml" media-type="application/xhtml+xml"/></manifest>
  <spine page-progression-direction="rtl"><itemref idref="c"/><itemref idref="a"/><itemref idref="b"/></spine>
</package>
'@
$comicEpub = [ordered]@{
    'mimetype'='application/epub+zip'
    'META-INF/container.xml'=$container
    'OPS/book.opf'=$comicPackage
    'OPS/pages/a.xhtml'='<html xmlns="http://www.w3.org/1999/xhtml"><body><img src="../images/1.png"/></body></html>'
    'OPS/pages/c.xhtml'='<html xmlns="http://www.w3.org/1999/xhtml"><body><svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"><image xlink:href="../images/%E5%B0%81%E9%9D%A2.png"/></svg></body></html>'
    'OPS/images/1.png'=$red
    'OPS/images/2.png'=$green
    'OPS/images/封面.png'=$blue
}
Write-Archive '图片样本.epub' $comicEpub

$textPackage = @'
<package xmlns="http://www.idpf.org/2007/opf" version="3.0">
  <manifest><item id="text" href="pages/long.xhtml" media-type="application/xhtml+xml"/><item id="cover" href="images/cover.png" media-type="image/png"/></manifest>
  <spine><itemref idref="text"/><itemref idref="cover"/></spine>
</package>
'@
$paragraphs = (1..28 | ForEach-Object {
    "<p>第 $_ 段。远处的灯亮了，穿过窄窄的巷子，可以看见河面泛起微光。她合上书，沿着石阶慢慢走下去。&nbsp;The evening was quiet.</p>"
}) -join ''
$textEpub = [ordered]@{
    'mimetype'='application/epub+zip'
    'META-INF/container.xml'=$container
    'OPS/book.opf'=$textPackage
    'OPS/pages/long.xhtml'=('<html xmlns="http://www.w3.org/1999/xhtml"><head><title>样本</title></head><body><h1>第一章 · 河边的灯</h1>' + $paragraphs + '</body></html>')
    'OPS/images/cover.png'=$red
}
Write-Archive '文字样本.epub' $textEpub

