param([string]$Path)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnetExe = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$env:MSBuildEnableWorkloadResolver = 'false'
$output = Join-Path $projectRoot 'artifacts\dev\'
& $dotnetExe build (Join-Path $projectRoot 'Amoxtli.csproj') -p:Platform=x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true "-p:OutputPath=$output" -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar Amoxtli.' }
$executable = Join-Path $output 'Amoxtli.exe'
if ($Path) { Start-Process -FilePath $executable -ArgumentList ('"' + (Resolve-Path -LiteralPath $Path).Path + '"') }
else { Start-Process -FilePath $executable }
