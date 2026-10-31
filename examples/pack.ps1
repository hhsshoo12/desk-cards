# 카드 폴더 전체를 zip으로 묶는다: pwsh -File examples/pack.ps1 clock
param([Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Name)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot $Name
$dist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$output = Join-Path $dist "$Name.zip"
$temporary = Join-Path $dist ([Guid]::NewGuid().ToString('N') + '.zip')
try {
    [IO.Compression.ZipFile]::CreateFromDirectory($source, $temporary)
    Move-Item -LiteralPath $temporary -Destination $output -Force
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
Write-Host "만들었어요: $output"
