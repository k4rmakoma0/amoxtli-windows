$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$toolsDirectory = Join-Path $projectRoot 'artifacts\tools'
$destination = Join-Path $toolsDirectory 'innosetup'
$download = Join-Path $toolsDirectory 'innosetup-7.1.1-x64.exe'
$expectedHash = '8A5A6711C3766A6E5FB74F280BA0E3551E719CE94538D901FDBD1F8F9920B3F0'
New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
if (-not (Test-Path -LiteralPath $download)) {
    Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe' -OutFile $download
}
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $expectedHash) { throw 'El hash del compilador Inno Setup no coincide.' }
$signature = Get-AuthenticodeSignature -LiteralPath $download
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Pyrsys B\.V\.(?:,|$)') {
    throw 'La firma del compilador Inno Setup no es válida.'
}
$process = Start-Process -FilePath $download -ArgumentList @('/PORTABLE=1','/CURRENTUSER','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="' + $destination + '"')) -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "No se pudo preparar Inno Setup: $($process.ExitCode)" }
$compiler = Join-Path $destination 'ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'No se encontró ISCC.exe.' }
if ($env:GITHUB_OUTPUT) { "compiler-path=$compiler" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8 }
Write-Output "Compilador portable: $compiler"
