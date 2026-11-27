param([Parameter(Mandatory)][string]$Destination, [string]$ArchivePath)

$ErrorActionPreference = 'Stop'
$version = 'v2.4.2373'
$filename = "Suwayomi-Server-$version-windows-x64.zip"
$expected = 'A8F1C7CF45CA3666AFC2E8A06C7F4A968E07CEE7D668E8BE7CF194273A672A15'
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('komasnap-package-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
try {
    $archive = Join-Path $scratch $filename
    if ($ArchivePath) {
        Copy-Item -LiteralPath $ArchivePath -Destination $archive
    } else {
        $url = "https://github.com/Suwayomi/Suwayomi-Server/releases/download/$version/$filename"
        & curl.exe --fail --silent --show-error --location --retry 3 --max-time 600 --output $archive $url
        if ($LASTEXITCODE -ne 0) { throw '无法下载 Suwayomi 官方发行包。' }
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'Suwayomi 发行包 SHA-256 校验失败。' }
    & tar.exe -xf $archive -C $scratch
    if ($LASTEXITCODE -ne 0) { throw '无法解压 Suwayomi 发行包。' }
    $upstream = Join-Path $scratch "Suwayomi-Server-$version-windows-x64"
    $package = Join-Path $scratch 'KomaSnap'
    $service = Join-Path $package 'suwayomi'
    $null = New-Item -ItemType Directory -Path $service -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'release/KomaSnap-Batch.exe') -Destination $package
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'KomaSnap.ico') -Destination $package
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $package 'README.md')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Suwayomi-MPL-2.0.txt') -Destination (Join-Path $service 'LICENSE-MPL-2.0.txt')
    Copy-Item -LiteralPath (Join-Path $upstream 'bin') -Destination (Join-Path $service 'bin') -Recurse
    Copy-Item -LiteralPath (Join-Path $upstream 'jre') -Destination (Join-Path $service 'jre') -Recurse
    $destinationPath = [System.IO.Path]::GetFullPath($Destination)
    if (Test-Path -LiteralPath $destinationPath) { throw '输出文件已存在，请选择其他路径。' }
    Compress-Archive -LiteralPath $package -DestinationPath $destinationPath -CompressionLevel Fastest
    Write-Output "Created $destinationPath"
} finally {
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $resolvedScratch = [System.IO.Path]::GetFullPath($scratch)
    if (-not $resolvedScratch.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not (Split-Path -Leaf $resolvedScratch).StartsWith('komasnap-package-', [System.StringComparison]::Ordinal)) {
        throw '临时目录不在预期范围，未删除。'
    }
    if (Test-Path -LiteralPath $resolvedScratch) { Remove-Item -LiteralPath $resolvedScratch -Recurse -Force }
}

