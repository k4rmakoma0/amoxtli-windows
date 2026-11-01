$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnetExe = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$env:MSBuildEnableWorkloadResolver = 'false'
$output = Join-Path $projectRoot 'artifacts\qa\'
$report = Join-Path $projectRoot 'artifacts\smoke-results.json'
& $dotnetExe build (Join-Path $projectRoot 'Amoxtli.csproj') -p:Platform=x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true "-p:OutputPath=$output" -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar la app de pruebas.' }
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath (Join-Path $output 'Amoxtli.exe') -ArgumentList @('--smoke-test', ('"' + $report + '"')) -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(60000)) { throw 'La app de pruebas no terminó en 60 segundos.' }
if (-not (Test-Path -LiteralPath $report) -or (Get-Item -LiteralPath $report).LastWriteTimeUtc -lt $started) { throw 'La app no produjo un informe nuevo.' }
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
$result.results | Format-Table -AutoSize
Write-Output "$($result.passed) pruebas correctas; $($result.failed) fallidas. Informe: $report"
if ($result.failed -gt 0) { throw 'Fallaron las pruebas de integración.' }
