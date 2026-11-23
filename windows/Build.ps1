param([switch]$Verify, [string]$VerifyName = 'ReaderVerification.exe', [string]$ReleaseName = 'KomaSnap.exe')

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$winmd = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/8.1/References/CommonConfiguration/Neutral/Annotated/Windows.winmd'
$sources = 'Reader.cs','ReaderTray.cs','ReaderAppearance.cs','ReaderSpine.cs','ReaderState.cs','ReaderBook.cs','PdfBook.cs','ArchiveBook.cs','PageTurnEffect.cs','PageSlideEffect.cs','ContinuousReader.cs','Bookshelf.cs' | ForEach-Object { Join-Path $PSScriptRoot $_ }
$references = 'System.Runtime.dll','System.Runtime.WindowsRuntime.dll','System.Xml.Linq.dll',
    'System.IO.Compression.dll','System.Drawing.dll','System.Windows.Forms.dll',
    'WPF/PresentationFramework.dll','WPF/PresentationCore.dll','WPF/WindowsBase.dll','System.Xaml.dll' |
    ForEach-Object { "/reference:$(Join-Path $framework $_)" }
$references += "/reference:$winmd"
$resource = Join-Path $PSScriptRoot 'Reader.xaml'
$tokens = Join-Path $PSScriptRoot 'ThemeTokens.xaml'
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
& (Join-Path $framework 'csc.exe') /nologo /codepage:65001 /optimize+ /platform:anycpu @options @references "/win32manifest:$manifest" "/win32icon:$icon" "/resource:$resource,Reader.xaml" "/resource:$tokens,ThemeTokens.xaml" "/out:$target" @sources
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE。" }
if (!$Verify) { Copy-Item -LiteralPath $icon -Destination (Join-Path $releaseFolder 'KomaSnap.ico') -Force }
