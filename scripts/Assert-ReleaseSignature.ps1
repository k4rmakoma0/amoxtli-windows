param([Parameter(Mandatory)][string[]]$Path)

$ErrorActionPreference = 'Stop'
foreach ($file in $Path) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Falta el archivo firmado: $file" }
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    if ($signature.Status -ne 'Valid') { throw "Firma no válida en $file : $($signature.Status)" }
    if ($signature.SignerCertificate.Subject -notmatch '(?i)(?:^|,\s*)CN=SignPath Foundation(?:,|$)') {
        throw "El archivo no está firmado por SignPath Foundation: $file"
    }
    if (-not $signature.TimeStamperCertificate) { throw "La firma no tiene sello de tiempo: $file" }
    Write-Output "Firma verificada: $file"
}
