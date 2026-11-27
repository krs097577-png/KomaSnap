param([switch]$Verify, [switch]$SuwayomiVerify, [switch]$BatchVerify, [string]$VerifyName = 'ReaderVerification.exe', [string]$ReleaseName = 'KomaSnap.exe')

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$winmd = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/8.1/References/CommonConfiguration/Neutral/Annotated/Windows.winmd'
$sources = 'Reader.cs','ReaderTray.cs','ReaderAppearance.cs','ReaderColorEffect.cs','ReaderSpine.cs','ReaderState.cs','ReaderBook.cs','PdfBook.cs','ArchiveBook.cs','PageTurnEffect.cs','PageSlideEffect.cs','ContinuousReader.cs','Bookshelf.cs','OnlineManga.cs','OnlineBook.cs','OnlineMangaWindow.cs','OnlineRule.cs','OnlineRuleStandard.cs','OnlineHtml.cs','OnlineCoverCache.cs' | ForEach-Object { Join-Path $PSScriptRoot $_ }
$sources += Join-Path $PSScriptRoot 'Suwayomi.cs'
$sources += Join-Path $PSScriptRoot 'OnlineWorkspace.cs'
$references = 'System.Runtime.dll','System.Runtime.WindowsRuntime.dll','System.Xml.Linq.dll',
    'System.IO.Compression.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Net.Http.dll','System.Web.Extensions.dll','netstandard.dll',
    'WPF/PresentationFramework.dll','WPF/PresentationCore.dll','WPF/WindowsBase.dll','System.Xaml.dll' |
    ForEach-Object { "/reference:$(Join-Path $framework $_)" }
$references += "/reference:$winmd"
$parserPackages = @(
    @{Name='HtmlAgilityPack'; Version='1.13.1'; Hash='FC3F549E1C86EFF1EE6E7AD872A7C0F5AC21CEE093D0F606F8270B3E937AFCCE'},
    @{Name='Fizzler'; Version='1.3.0'; Hash='B46BF2A7499594248317FFD700DE6189F5FC68A5F3BCBEABC128114A7FADDF49'},
    @{Name='Fizzler.Systems.HtmlAgilityPack'; Version='1.2.1'; Hash='A2FF0A7379C1711C64D08F963D1D75405943D7AD3B724DF533EDFB4A320495B7'}
)
$parserCache = Join-Path $PSScriptRoot 'parser-packages'
$null = New-Item -ItemType Directory -Path $parserCache -Force
$parserResources = @("/resource:$(Join-Path $PSScriptRoot 'HtmlAgilityPack-LICENSE.txt'),OnlineParser.HtmlAgilityPack.License")
foreach ($package in $parserPackages) {
    $name = $package.Name.ToLowerInvariant()
    $version = $package.Version
    $archive = Join-Path $parserCache "$name.$version.nupkg"
    if (!(Test-Path -LiteralPath $archive)) {
        $partial = $archive + '.' + [guid]::NewGuid().ToString('N') + '.download'
        try {
            & curl.exe --fail --silent --show-error --location --max-time 90 --output $partial "https://api.nuget.org/v3-flatcontainer/$name/$version/$name.$version.nupkg"
            if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $partial).Hash -ne $package.Hash) {
                throw "无法获取经过校验的 HTML 解析组件 $name。"
            }
            Move-Item -LiteralPath $partial -Destination $archive
        } finally {
            if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial }
        }
    }
    if ((Get-FileHash -LiteralPath $archive).Hash -ne $package.Hash) { throw "解析组件校验失败 $name。" }
    $destination = Join-Path $parserCache "$name.$version"
    $null = New-Item -ItemType Directory -Path $destination -Force
    $entry = "lib/netstandard2.0/$($package.Name).dll"
    $extract = @($entry)
    if ($package.Name -ne 'HtmlAgilityPack') { $extract += 'COPYING.txt' }
    & tar.exe -xf $archive -C $destination @extract
    if ($LASTEXITCODE -ne 0) { throw "无法解包 HTML 解析组件 $name。" }
    $library = Join-Path $destination $entry
    $references += "/reference:$library"
    $parserResources += "/resource:$library,OnlineParser.$($package.Name)"
    if ($extract.Count -eq 2) {
        $parserResources += "/resource:$(Join-Path $destination 'COPYING.txt'),OnlineParser.$($package.Name).License"
    }
}
$resource = Join-Path $PSScriptRoot 'Reader.xaml'
$tokens = Join-Path $PSScriptRoot 'ThemeTokens.xaml'
$online = Join-Path $PSScriptRoot 'OnlineMangaWindow.xaml'
$mangadex = Join-Path $PSScriptRoot 'MangaDexRule.json'
$manifest = Join-Path $PSScriptRoot 'reader.manifest'
$icon = Join-Path $PSScriptRoot 'KomaSnap.ico'
if (!(Test-Path -LiteralPath $icon)) { throw '缺少 KomaSnap.ico，请用 MakeIcon.ps1 从设计图生成图标。' }
$releaseFolder = Join-Path (Split-Path $PSScriptRoot) 'outputs'
$null = New-Item -ItemType Directory -Path $releaseFolder -Force
$target = Join-Path $releaseFolder $ReleaseName
$options = @('/target:winexe', '/main:Reader')
if ($Verify) {
    $target = Join-Path $PSScriptRoot $VerifyName
    $options = @('/target:exe', '/main:Verify')
    $sources += Join-Path $PSScriptRoot 'Verify.cs'
}
if ($SuwayomiVerify) {
    $target = Join-Path $PSScriptRoot $VerifyName
    $options = @('/target:exe', '/main:SuwayomiVerify')
    $sources += Join-Path $PSScriptRoot 'SuwayomiVerify.cs'
}
if ($BatchVerify) {
    $target = Join-Path $PSScriptRoot $VerifyName
    $options = @('/target:exe', '/main:BatchVerify')
    $sources += Join-Path $PSScriptRoot 'BatchVerify.cs'
}
$shader = Join-Path $PSScriptRoot ('.ReaderColor-' + [guid]::NewGuid().ToString('N') + '.ps')
try {
    & (Join-Path $PSScriptRoot 'CompileReaderShader.ps1') (Join-Path $PSScriptRoot 'ReaderColor.hlsl') $shader
    & (Join-Path $framework 'csc.exe') /nologo /codepage:65001 /optimize+ /platform:anycpu @options @references @parserResources "/win32manifest:$manifest" "/win32icon:$icon" "/resource:$resource,Reader.xaml" "/resource:$tokens,ThemeTokens.xaml" "/resource:$online,OnlineMangaWindow.xaml" "/resource:$mangadex,MangaDexRule.json" "/resource:$shader,ReaderColor.ps" "/out:$target" @sources
    if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE。" }
} finally {
    if (Test-Path -LiteralPath $shader) { Remove-Item -LiteralPath $shader }
}
if (!$Verify -and !$SuwayomiVerify -and !$BatchVerify) { Copy-Item -LiteralPath $icon -Destination (Join-Path $releaseFolder 'KomaSnap.ico') -Force }

