param(
    [Parameter(Mandatory)][string]$Jdk,
    [Parameter(Mandatory)][string]$BuildTools,
    [Parameter(Mandatory)][string]$AndroidJar,
    [Parameter(Mandatory)][string]$Keystore,
    [string]$Alias = 'komasnap'
)

$ErrorActionPreference = 'Stop'
$jdkPath = (Resolve-Path -LiteralPath $Jdk).Path
$bt = (Resolve-Path -LiteralPath $BuildTools).Path
$platform = (Resolve-Path -LiteralPath $AndroidJar).Path
$key = (Resolve-Path -LiteralPath $Keystore).Path
if (!$env:KOMASNAP_SIGN_STORE_PASSWORD) { throw '请设置 KOMASNAP_SIGN_STORE_PASSWORD。' }
$keyPassword = if ($env:KOMASNAP_SIGN_KEY_PASSWORD) { 'env:KOMASNAP_SIGN_KEY_PASSWORD' } else { 'env:KOMASNAP_SIGN_STORE_PASSWORD' }
$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $root 'app'
$build = Join-Path $root ('build/' + [Guid]::NewGuid().ToString('N'))
$gen = Join-Path $build 'gen'
$classes = Join-Path $build 'classes'
$null = New-Item -ItemType Directory -Path $gen, "$classes/com/komasnap/reader" -Force
$aapt = Join-Path $bt 'aapt2.exe'
$java = Join-Path $jdkPath 'bin/java.exe'
$javac = Join-Path $jdkPath 'bin/javac.exe'
$jar = Join-Path $jdkPath 'bin/jar.exe'
$zipalign = Join-Path $bt 'zipalign.exe'
$signer = Join-Path $bt 'lib/apksigner.jar'
$dexer = Join-Path $bt 'lib/d8.jar'

function Run([string]$program, [string[]]$arguments) {
    & $program @arguments
    if ($LASTEXITCODE -ne 0) { throw "$(Split-Path -Leaf $program) 退出码 $LASTEXITCODE。" }
}

Run $aapt @('compile', '--dir', "$app/res", '-o', "$build/res.zip")
Run $aapt @('link', '-o', "$build/unsigned.apk", '-I', $platform, '--manifest', "$app/AndroidManifest.xml",
    '--java', $gen, '--min-sdk-version', '24', '--target-sdk-version', '34',
    '--version-code', '182', '--version-name', '1.8.2', "$build/res.zip")
$sources = @(Get-ChildItem "$app/src", $gen -Recurse -Filter '*.java' | ForEach-Object FullName)
Run $javac (@('--release', '8', '-encoding', 'UTF-8', '-cp', $platform, '-d', $classes) + $sources)
$classList = @(Get-ChildItem $classes -Recurse -Filter '*.class' | ForEach-Object FullName)
[IO.File]::WriteAllLines("$build/classes.txt", [string[]]$classList, [Text.UTF8Encoding]::new($false))
Run $java @('-cp', $dexer, 'com.android.tools.r8.D8', '--min-api', '24', '--lib', $platform,
    '--output', $build, "@$build/classes.txt")
Run $jar @('uf', "$build/unsigned.apk", '-C', $build, 'classes.dex')
Run $zipalign @('-f', '-p', '4', "$build/unsigned.apk", "$build/aligned.apk")
$apk = "$build/KomaSnap-Android-1.8.2.apk"
Run $java @('-jar', $signer, 'sign', '--ks', $key, '--ks-key-alias', $Alias,
    '--ks-pass', 'env:KOMASNAP_SIGN_STORE_PASSWORD', '--key-pass', $keyPassword, '--out', $apk, "$build/aligned.apk")
Run $java @('-jar', $signer, 'verify', '--print-certs', $apk)
Write-Output $apk
