param(
    [Parameter(Mandatory)][string]$BuildStatePath,
    [Parameter(Mandatory)][string]$SignedPayloadDirectory,
    [Parameter(Mandatory)][string]$CompilerPath
)

$ErrorActionPreference = 'Stop'
$state = Get-Content -LiteralPath $BuildStatePath -Raw | ConvertFrom-Json
if ($state.UninstallerName -ne [IO.Path]::GetFileName($state.UninstallerName) -or $state.UninstallerName -notmatch '^uninst-.+\.e(32|64)$') {
    throw 'El nombre del desinstalador no es válido.'
}
if ((Get-FileHash -LiteralPath $CompilerPath -Algorithm SHA256).Hash -ne $state.CompilerHash) {
    throw 'Usa el mismo compilador Inno Setup que preparó el desinstalador.'
}
$files = @(
    @{ Relative = 'app\Amoxtli.exe'; Destination = (Join-Path $state.PublishDirectory 'Amoxtli.exe') },
    @{ Relative = 'app\Amoxtli.dll'; Destination = (Join-Path $state.PublishDirectory 'Amoxtli.dll') },
    @{ Relative = 'uninstaller\Amoxtli-uninstaller.exe'; Destination = (Join-Path $state.SignedUninstallerDirectory $state.UninstallerName) }
)
& (Join-Path $PSScriptRoot 'Assert-ReleaseSignature.ps1') -Path @($files | ForEach-Object { Join-Path $SignedPayloadDirectory $_.Relative })
foreach ($file in $files) {
    Copy-Item -LiteralPath (Join-Path $SignedPayloadDirectory $file.Relative) -Destination $file.Destination
}
& (Join-Path $PSScriptRoot 'Build-Installer.ps1') -Version $state.Version -CompilerPath $CompilerPath `
    -RequireSignedPayload -PublishDirectory $state.PublishDirectory `
    -SignedUninstallerDirectory $state.SignedUninstallerDirectory -ReleaseDirectory $state.ReleaseDirectory
if ($env:GITHUB_OUTPUT) {
    "installer-path=$(Join-Path $state.ReleaseDirectory "Amoxtli-$($state.Version)-windows-x64-setup.exe")" |
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
}
