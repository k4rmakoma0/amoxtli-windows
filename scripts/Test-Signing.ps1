param(
    [Parameter(Mandatory)][string]$UnsignedInstaller,
    [Parameter(Mandatory)][string]$CompilerPath
)

$ErrorActionPreference = 'Stop'
$assertSignature = Join-Path $PSScriptRoot 'Assert-ReleaseSignature.ps1'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testDirectory = Join-Path $projectRoot ('artifacts\signing-tests\' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null

function Assert-Rejected([scriptblock]$Action, [string]$ExpectedMessage) {
    $rejected = $false
    try { & $Action }
    catch {
        if ($_.Exception.Message -notlike $ExpectedMessage) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Se aceptó un archivo que debía rechazarse.' }
}

Assert-Rejected { & $assertSignature -Path $UnsignedInstaller } '*Firma no válida*'
Write-Output 'PASS: se rechazan instaladores sin firma.'

# The signed compiler provides a real, trusted, timestamped third-party PE
# sample. It must not be accepted as a SignPath Foundation release.
$compilerSignature = Get-AuthenticodeSignature -LiteralPath $CompilerPath
if ($compilerSignature.Status -ne 'Valid') { throw 'El compilador de prueba debe tener una firma pública válida.' }
Assert-Rejected { & $assertSignature -Path $CompilerPath } '*no está firmado por SignPath Foundation*'
Write-Output 'PASS: se rechaza un editor ajeno aunque su firma sea válida.'

# Inno caches the signed uninstaller as .e32/.e64; verify Windows can inspect
# PE signatures under that extension without modifying the original compiler.
$cacheSample = Join-Path $testDirectory 'signature-test.e32'
Copy-Item -LiteralPath $CompilerPath -Destination $cacheSample
if ((Get-AuthenticodeSignature -LiteralPath $cacheSample).Status -ne 'Valid') { throw 'No se puede verificar una firma PE con extensión .e32.' }
Write-Output 'PASS: se pueden verificar las firmas de la caché de Inno Setup.'

$fakeRelease = Join-Path $testDirectory 'Amoxtli-1.0.0-windows-x64-setup.exe'
Copy-Item -LiteralPath $UnsignedInstaller -Destination $fakeRelease
Assert-Rejected { & (Join-Path $PSScriptRoot 'Finalize-SignedRelease.ps1') -Directory $testDirectory -Version '1.0.0' } '*Firma no válida*'
if (Test-Path -LiteralPath "$fakeRelease.sha256") { throw 'Se generó un checksum de release firmada para un instalador sin firma.' }
Write-Output 'PASS: una firma inválida impide generar el checksum de la release firmada.'
