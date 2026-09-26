# Desk Cards 설치 파일(DeskCards-Setup-<버전>.exe)을 만든다.
#   1) 앱을 .NET 런타임이 없어도 도는 단일 exe로 게시해 payload/에 넣고
#   2) PyInstaller로 installer.py와 함께 exe 하나로 묶는다.
#
#   powershell -ExecutionPolicy Bypass -File installer\build.ps1
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$root = Split-Path $here -Parent
$proj = Join-Path $root 'src\DeskCards\DeskCards.csproj'
$icon = Join-Path $root 'src\DeskCards\app.ico'
$payload = Join-Path $here 'payload'
$venv = Join-Path $here '.venv'

$version = ([xml](Get-Content $proj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'csproj에서 Version을 찾지 못했어요.' }
Write-Host "== Desk Cards $version"

if (-not (Test-Path $icon)) { python (Join-Path $here 'make_icon.py') }

Write-Host '== 앱 게시 (self-contained 단일 exe)'
if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $payload
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 실패' }
Get-ChildItem $payload -Exclude 'DeskCards.exe' | Remove-Item -Recurse -Force
Set-Content (Join-Path $payload 'version.txt') $version -NoNewline -Encoding utf8

Write-Host '== PyInstaller 준비'
if (-not (Test-Path (Join-Path $venv 'Scripts\python.exe'))) { python -m venv $venv }
$py = Join-Path $venv 'Scripts\python.exe'
& $py -m pip install --quiet --upgrade pip pyinstaller
if ($LASTEXITCODE -ne 0) { throw 'pyinstaller 설치 실패' }

Write-Host '== 설치 파일 만들기'
$name = "DeskCards-Setup-$version"
& $py -m PyInstaller --noconfirm --clean --onefile --windowed `
    --name $name --icon $icon `
    --add-data "$payload;payload" --add-data "$icon;." `
    --distpath (Join-Path $here 'dist') --workpath (Join-Path $here 'build') --specpath (Join-Path $here 'build') `
    (Join-Path $here 'installer.py')
if ($LASTEXITCODE -ne 0) { throw 'PyInstaller 실패' }

$out = Join-Path $here "dist\$name.exe"
Write-Host ("== 완료: {0} ({1:N1} MB)" -f $out, ((Get-Item $out).Length / 1MB))
