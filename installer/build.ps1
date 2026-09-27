# 앱과 웹 설치기를 따로 빌드한다. 업로드는 하지 않는다.
[CmdletBinding()]
param([switch]$App, [switch]$Installer)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$appProject = Join-Path $root 'src\DeskCards\DeskCards.csproj'
$setupProject = Join-Path $PSScriptRoot 'DeskCards.Setup\DeskCards.Setup.csproj'
$dist = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist'))
$appOutput = Join-Path $dist 'app'
$setupOutput = Join-Path $dist 'installer'
$buildApp = $App.IsPresent -or -not $Installer.IsPresent
$buildInstaller = $Installer.IsPresent -or -not $App.IsPresent
$appVersion = ([xml](Get-Content -LiteralPath $appProject -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$setupVersion = ([xml](Get-Content -LiteralPath $setupProject -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $appVersion -or -not $setupVersion) { throw 'csproj에서 Version을 찾지 못했어요.' }

function Remove-BuildTemporary([string]$path) {
    $absolute = [IO.Path]::GetFullPath($path)
    if (-not $absolute.StartsWith($dist + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "빌드 임시 경로가 dist 밖입니다: $absolute"
    }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}

if ($buildApp) {
    New-Item -ItemType Directory -Path $appOutput -Force | Out-Null
    $publishDir = Join-Path $dist ('publish-' + [guid]::NewGuid().ToString('N'))
    $zipStage = Join-Path $dist ('zip-' + [guid]::NewGuid().ToString('N'))
    try {
        dotnet publish $appProject -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $publishDir -warnaserror
        if ($LASTEXITCODE -ne 0) { throw '앱 게시 실패' }
        New-Item -ItemType Directory -Path $zipStage | Out-Null
        Copy-Item -LiteralPath (Join-Path $publishDir 'DeskCards.exe') -Destination $zipStage
        [IO.File]::WriteAllText((Join-Path $zipStage 'version.txt'), $appVersion, [Text.UTF8Encoding]::new($false))
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zipPath = Join-Path $appOutput 'DeskCards-win-x64.zip'
        $newZip = Join-Path $dist ('archive-' + [guid]::NewGuid().ToString('N') + '.zip')
        try {
            [IO.Compression.ZipFile]::CreateFromDirectory($zipStage, $newZip)
            Move-Item -LiteralPath $newZip -Destination $zipPath -Force
        } finally { if (Test-Path -LiteralPath $newZip) { Remove-Item -LiteralPath $newZip -Force } }
        $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText(($zipPath + '.sha256'), $hash, [Text.UTF8Encoding]::new($false))
        Write-Host "앱 릴리스 파일: $zipPath"
        Write-Host "앱 해시 파일: $zipPath.sha256"
    } finally { Remove-BuildTemporary $publishDir; Remove-BuildTemporary $zipStage }
}

if ($buildInstaller) {
    dotnet build $setupProject -c Release -warnaserror -p:DebugType=none
    if ($LASTEXITCODE -ne 0) { throw '설치기 빌드 실패 (.NET Framework 4.8 Developer Pack 필요)' }
    New-Item -ItemType Directory -Path $setupOutput -Force | Out-Null
    $setupExe = Join-Path $setupOutput 'DeskCards-Setup.exe'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'DeskCards.Setup\bin\Release\net48\DeskCards-Setup.exe') -Destination $setupExe -Force
    $length = (Get-Item -LiteralPath $setupExe).Length
    if ($length -gt 1MB) { throw "설치기가 1MB를 넘습니다: $length bytes" }
    Write-Host "설치기: $setupExe ($length bytes)"
}

Write-Host '첫 배포는 앱 릴리스를 먼저 올리고, 그다음 설치기 릴리스를 Latest로 올리세요.'
Write-Host '기존 v0.1.0 릴리스는 웹 설치기에서 선택되지 않습니다. 아래 명령은 예시이며 실행하지 않았습니다.'
if ($buildApp) { Write-Host "gh release create app-v$appVersion installer/dist/app/* --title `"Desk Cards $appVersion`" --latest=false" }
if ($buildInstaller) { Write-Host "gh release create installer-v$setupVersion installer/dist/installer/DeskCards-Setup.exe --title `"Desk Cards 설치기 $setupVersion`" --latest" }
