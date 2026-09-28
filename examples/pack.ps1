# 예시 카드 폴더를 .dard로 묶는다: pwsh -File examples/pack.ps1 clock  →  examples/dist/clock.dard
param([Parameter(Mandatory)][string]$Name)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot $Name
$dist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$out = Join-Path $dist "$Name.dard"
if (Test-Path $out) { Remove-Item $out }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($out, 'Create')
try {
    foreach ($file in 'manifest.json', 'card.html', 'settings.html') {
        $path = Join-Path $source $file
        if (Test-Path $path) { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $path, $file) }
    }
} finally { $zip.Dispose() }
Write-Host "만들었어요: $out"
