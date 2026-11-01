# 릴리스 파일 세 개를 만든다. 업로드는 하지 않는다.
#   DeskCards-win-x64.zip(+.sha256): 앱 자체 업데이트용. DeskCards.exe, uninstall.exe, version.txt
#   DeskCards-Setup.exe: 위 zip을 품은 설치기(인터넷 없이 설치)
# 버전은 저장소 루트의 Directory.Build.props 한 곳이다(앱·설치기·제거기 공통).
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$appProject = Join-Path $root 'src\DeskCards\DeskCards.csproj'
$setupProject = Join-Path $PSScriptRoot 'DeskCards.Setup\DeskCards.Setup.csproj'
$dist = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist'))
$output = Join-Path $dist 'release'
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Directory.Build.props에서 Version을 찾지 못했어요.' }

function Remove-BuildTemporary([string]$path) {
    $absolute = [IO.Path]::GetFullPath($path)
    if (-not $absolute.StartsWith($dist + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "빌드 임시 경로가 dist 밖입니다: $absolute"
    }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}

# .NET Framework 4.8 개발자 팩이 없어도 Microsoft의 NuGet 참조 어셈블리로 빌드한다.
function Build-Setup([string[]]$properties) {
    dotnet build $setupProject -c Release -warnaserror -p:DebugType=none -p:AutomaticallyUseReferenceAssemblyPackages=true @properties | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '설치기 빌드 실패' }
}

$publishDir = Join-Path $dist ('publish-' + [guid]::NewGuid().ToString('N'))
$uninstallerDir = Join-Path $dist ('uninstaller-' + [guid]::NewGuid().ToString('N'))
$zipStage = Join-Path $dist ('zip-' + [guid]::NewGuid().ToString('N'))
try {
    # 1. 앱
    dotnet publish $appProject -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $publishDir -warnaserror
    if ($LASTEXITCODE -ne 0) { throw '앱 게시 실패' }

    # 2. 제거기: 같은 설치기 코드를 앱 없이, 늘 제거 모드로 빌드한다. 설치기 빌드와 obj·bin을 나눈다.
    Build-Setup @('-p:Uninstaller=true', "-p:OutputPath=$uninstallerDir\", '-p:IntermediateOutputPath=obj\Uninstaller\')

    # 3. 앱 zip. 이미 배포된 앱이 업데이트를 찾으려면 파일 이름과 zip 맨 위의 DeskCards.exe·version.txt를 바꾸면 안 된다.
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    New-Item -ItemType Directory -Path $zipStage | Out-Null
    Copy-Item -LiteralPath (Join-Path $publishDir 'DeskCards.exe') -Destination $zipStage
    Copy-Item -LiteralPath (Join-Path $uninstallerDir 'DeskCards-Setup.exe') -Destination (Join-Path $zipStage 'uninstall.exe')
    [IO.File]::WriteAllText((Join-Path $zipStage 'version.txt'), $version, [Text.UTF8Encoding]::new($false))
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipPath = Join-Path $output 'DeskCards-win-x64.zip'
    $newZip = Join-Path $dist ('archive-' + [guid]::NewGuid().ToString('N') + '.zip')
    try {
        [IO.Compression.ZipFile]::CreateFromDirectory($zipStage, $newZip)
        Move-Item -LiteralPath $newZip -Destination $zipPath -Force
    } finally { if (Test-Path -LiteralPath $newZip) { Remove-Item -LiteralPath $newZip -Force } }
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($zipPath + '.sha256'), $hash, [Text.UTF8Encoding]::new($false))

    # 4. 설치기: 앱 zip을 품는다.
    Build-Setup @("-p:AppPackage=$zipPath")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'DeskCards.Setup\bin\Release\net48\DeskCards-Setup.exe') -Destination (Join-Path $output 'DeskCards-Setup.exe') -Force
} finally { Remove-BuildTemporary $publishDir; Remove-BuildTemporary $uninstallerDir; Remove-BuildTemporary $zipStage }

Get-ChildItem -LiteralPath $output | ForEach-Object { Write-Host ("{0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) }
Write-Host '아래 명령은 예시이며 실행하지 않았습니다. 릴리스는 하나이고 Latest로 올립니다(README의 설치기 링크가 Latest를 가리킴).'
Write-Host "gh release create app-v$version installer/dist/release/* --title `"Desk Cards $version`" --latest"
