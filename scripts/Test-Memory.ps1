param([string]$DocumentPath, [string]$ReportPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $DocumentPath) { $DocumentPath = Join-Path $projectRoot 'artifacts\memory-tests\04-documento-7_94MiB.md' }
if (-not $ReportPath) { $ReportPath = Join-Path $projectRoot 'artifacts\memory-results.json' }
$DocumentPath = (Resolve-Path -LiteralPath $DocumentPath).Path
$ReportPath = [System.IO.Path]::GetFullPath($ReportPath)
$output = Join-Path $projectRoot 'artifacts\memory-check\'
$intermediate = Join-Path $projectRoot 'obj\memory-check\'
$env:MSBuildEnableWorkloadResolver = 'false'
# Separate intermediates prevent cached MSIX resource indexes from affecting this unpackaged run.
& (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') build (Join-Path $projectRoot 'Amoxtli.csproj') -t:Rebuild -p:Configuration=Debug -p:Platform=x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true "-p:IntermediateOutputPath=$intermediate" "-p:OutputPath=$output" -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar la prueba de memoria.' }
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath (Join-Path $output 'Amoxtli.exe') -WorkingDirectory $output -ArgumentList @('--memory-test', ('"' + $ReportPath + '"'), ('"' + $DocumentPath + '"')) -WindowStyle Hidden -PassThru
$deadline = $started.AddMinutes(3)
while (-not $process.WaitForExit(1000)) {
    if ([DateTime]::UtcNow -gt $deadline) {
        $process.Kill()
        throw 'La prueba de memoria excedió tres minutos.'
    }
}
if ($process.ExitCode -ne 0) { throw "La prueba terminó con código $($process.ExitCode)." }
if (-not (Test-Path -LiteralPath $ReportPath) -or (Get-Item -LiteralPath $ReportPath).LastWriteTimeUtc -lt $started) { throw 'No se generó un informe nuevo.' }
$result = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
$result.samples | Select-Object stage, workingSetMiB, privateMiB, managedMiB, previousRendererAlive, previousFirstBlockAlive, previousLastBlockAlive | Format-Table -AutoSize
if ($result.error) { throw $result.error }
$collected = @($result.samples | Where-Object { $_.stage -like 'collected-*' })
if ($collected.Count -ne 3) { throw 'No terminaron las tres alternancias.' }
if (@($collected | Where-Object { $_.previousRendererAlive -or -not $_.previousNativeContentReleased }).Count -gt 0) { throw 'El documento anterior conserva su renderizador o contenido nativo después de la recolección.' }
if (@($result.samples | Where-Object { $_.sourceViewCharacters -ne 0 }).Count -gt 0) { throw 'La vista de código oculta conserva texto.' }
Write-Output "Tres alternancias completadas y contenido anterior liberado. Informe: $ReportPath"
