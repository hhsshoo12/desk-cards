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

# 카드의 main.py를 돌릴 Python(embeddable). 받은 파일은 installer\cache에 두고, 고정한 SHA-256과 다르면 쓰지 않는다.
# 버전을 바꿀 때는 python.org에서 받은 새 zip의 SHA-256으로 아래 두 값을 함께 고친다.
$pythonVersion = '3.14.10'
$pythonSha256 = '90905e64a840557d8c1bc4f5b74db3d7ee85e3ffb217611cac1c7813acea4159'
$cache = Join-Path $PSScriptRoot 'cache'
$pythonZip = Join-Path $cache "python-$pythonVersion-embed-amd64.zip"
if (-not (Test-Path -LiteralPath $pythonZip)) {
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri "https://www.python.org/ftp/python/$pythonVersion/python-$pythonVersion-embed-amd64.zip" -OutFile $pythonZip -UseBasicParsing
}
if ((Get-FileHash -LiteralPath $pythonZip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pythonSha256) {
    Remove-Item -LiteralPath $pythonZip -Force
    throw "Python $pythonVersion zip의 SHA-256이 고정한 값과 달라요. 받은 파일을 지웠어요."
}

# 카드의 requirements.txt를 설치할 pip(순수 Python wheel). SHA-256은 PyPI가 알려 주는 값이다.
$pipVersion = '26.2.2'
$pipSha256 = 'fd7f4dfa8190e014013b9f08f3e973f4e4c26ffd179a204895533a7254813a70'
$pipWheel = Join-Path $cache "pip-$pipVersion-py3-none-any.whl"
if (-not (Test-Path -LiteralPath $pipWheel)) {
    $ProgressPreference = 'SilentlyContinue'
    $file = (Invoke-RestMethod -Uri "https://pypi.org/pypi/pip/$pipVersion/json").urls | Where-Object { $_.filename -eq "pip-$pipVersion-py3-none-any.whl" } | Select-Object -First 1
    if (-not $file) { throw "PyPI에서 pip $pipVersion wheel을 찾지 못했어요." }
    Invoke-WebRequest -Uri $file.url -OutFile $pipWheel -UseBasicParsing
}
if ((Get-FileHash -LiteralPath $pipWheel -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pipSha256) {
    Remove-Item -LiteralPath $pipWheel -Force
    throw "pip $pipVersion wheel의 SHA-256이 고정한 값과 달라요. 받은 파일을 지웠어요."
}

$publishDir = Join-Path $dist ('publish-' + [guid]::NewGuid().ToString('N'))
$uninstallerDir = Join-Path $dist ('uninstaller-' + [guid]::NewGuid().ToString('N'))
$zipStage = Join-Path $dist ('zip-' + [guid]::NewGuid().ToString('N'))
try {
    # 1. 앱
    dotnet publish $appProject -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:PythonEmbedZip=$pythonZip -p:PipWheel=$pipWheel -o $publishDir -warnaserror
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
