param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)

$ErrorActionPreference = 'Stop'
$installer = Join-Path $Directory "Amoxtli-$Version-windows-x64-setup.exe"
& (Join-Path $PSScriptRoot 'Assert-ReleaseSignature.ps1') -Path $installer
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
Write-Output "Instalador firmado y checksum final: $installer"
