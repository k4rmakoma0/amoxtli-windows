param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [string]$CompilerPath,
    [switch]$PrepareSigning,
    [switch]$RequireSignedPayload,
    [string]$PublishDirectory,
    [string]$SignedUninstallerDirectory,
    [string]$ReleaseDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnetExe = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if ($PrepareSigning -and $RequireSignedPayload) { throw 'Elige preparar la firma o empaquetar archivos firmados.' }
if ($PublishDirectory -and -not $RequireSignedPayload) { throw 'Solo se puede reutilizar una publicación al empaquetar archivos firmados.' }

if (-not $Version) {
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'Package.appxmanifest') -Raw
    $packageVersion = [version]$manifest.Package.Identity.Version
    $Version = '{0}.{1}.{2}' -f $packageVersion.Major, $packageVersion.Minor, $packageVersion.Build
}

if (-not $CompilerPath) {
    $candidates = @(
        (Join-Path $projectRoot 'artifacts\tools\innosetup\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    )
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }
    $CompilerPath = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw 'Instala Inno Setup 6.3 o posterior, o indica -CompilerPath con la ruta de ISCC.exe.'
}
$CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path

# A fresh directory prevents obsolete runtime files leaking into a new installer.
$buildDirectory = Join-Path $projectRoot ('artifacts\installer\build-' + [guid]::NewGuid().ToString('N'))
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $buildDirectory 'publish' }
if (-not $ReleaseDirectory) { $ReleaseDirectory = Join-Path $projectRoot 'artifacts\releases' }
if ($PrepareSigning) {
    $SignedUninstallerDirectory = Join-Path $buildDirectory 'uninstaller'
    $ReleaseDirectory = Join-Path $buildDirectory 'installer-to-sign'
    New-Item -ItemType Directory -Path $SignedUninstallerDirectory -Force | Out-Null
}
New-Item -ItemType Directory -Path $publishDirectory, $releaseDirectory -Force | Out-Null

$previousWorkloadResolver = $env:MSBuildEnableWorkloadResolver
Push-Location $projectRoot
try {
    $env:MSBuildEnableWorkloadResolver = 'false'
    if (-not $RequireSignedPayload) {
      & $dotnetExe publish Amoxtli.csproj -c Release -r win-x64 --self-contained true `
        -p:Platform=x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true `
        -p:AppxPackage=false -p:GenerateAppxPackageOnBuild=false -p:PublishAppxPackage=false `
        -p:BuildAppxUploadPackageForUap=false -p:BuildAppxSideloadPackageForUap=false `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false `
        "-p:Version=$Version" -p:IncludeSourceRevisionInInformationalVersion=false -p:Product=Amoxtli "-p:Company=Alan Anaya" `
        "-p:IntermediateOutputPath=$buildDirectory\obj\" "-p:OutputPath=$buildDirectory\bin\" `
        "-p:PublishDir=$publishDirectory\" -v:minimal
      if ($LASTEXITCODE -ne 0) { throw 'No se pudo publicar la app autocontenida.' }
    }

    foreach ($requiredFile in @('Amoxtli.exe', 'Amoxtli.dll', 'Amoxtli.pri', 'Microsoft.UI.Xaml.dll', 'coreclr.dll', 'Assets\Welcome.md', 'THIRD-PARTY-NOTICES.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $requiredFile) -PathType Leaf)) {
            throw "Falta un archivo requerido en la publicación: $requiredFile"
        }
    }
    # The build gets its own obj and bin folders: a Store (MSIX) build leaves a packaged resource index
    # in obj\x64\Release that, reused here, ships without WinUI's resources and crashes the app at startup.
    $resourceIndex = [System.Text.Encoding]::Latin1.GetString([System.IO.File]::ReadAllBytes((Join-Path $publishDirectory 'Amoxtli.pri')))
    if (-not $resourceIndex.Contains('Microsoft.UI.Xaml') -or -not $resourceIndex.Contains('generic.xbf')) {
        throw 'Amoxtli.pri no incluye los recursos de WinUI; la app no arrancaría sin paquete.'
    }

    $compilerArguments = @('/Q', "/DAppVersion=$Version", "/DPublishDirectory=$PublishDirectory", "/DReleaseDirectory=$ReleaseDirectory")
    if ($PrepareSigning -or $RequireSignedPayload) {
        if (-not $SignedUninstallerDirectory) { throw 'Falta SignedUninstallerDirectory.' }
        $compilerArguments += @('/DExternalSigning', "/DSignedUninstallerDirectory=$SignedUninstallerDirectory")
    }
    if ($RequireSignedPayload) {
        $uninstallers = @(Get-ChildItem -LiteralPath $SignedUninstallerDirectory -File | Where-Object { $_.Name -match '^uninst-.+\.e(32|64)$' })
        if ($uninstallers.Count -ne 1) { throw 'Debe haber exactamente un desinstalador firmado.' }
        & (Join-Path $PSScriptRoot 'Assert-ReleaseSignature.ps1') -Path @(
            (Join-Path $PublishDirectory 'Amoxtli.exe'),
            (Join-Path $PublishDirectory 'Amoxtli.dll'),
            $uninstallers[0].FullName
        )
    }
    # With SignedUninstaller enabled and no signing tool, ISCC writes its
    # .e32/.e64 cache file and stops until an external signature is supplied.
    & $CompilerPath @compilerArguments (Join-Path $projectRoot 'installer\Amoxtli.iss')
    if ($PrepareSigning) {
        $compilerExit = $LASTEXITCODE
        $uninstallers = @(Get-ChildItem -LiteralPath $SignedUninstallerDirectory -File | Where-Object { $_.Name -match '^uninst-.+\.e(32|64)$' })
        if ($compilerExit -ne 2 -or $uninstallers.Count -ne 1 -or $uninstallers[0].Length -eq 0) {
            throw 'Inno Setup no produjo el desinstalador esperado para la firma.'
        }
        $inputDirectory = Join-Path $buildDirectory 'signing-input'
        New-Item -ItemType Directory -Path (Join-Path $inputDirectory 'app'), (Join-Path $inputDirectory 'uninstaller') -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $PublishDirectory 'Amoxtli.exe'), (Join-Path $PublishDirectory 'Amoxtli.dll') -Destination (Join-Path $inputDirectory 'app')
        Copy-Item -LiteralPath $uninstallers[0].FullName -Destination (Join-Path $inputDirectory 'uninstaller\Amoxtli-uninstaller.exe')
        $stateFile = Join-Path $buildDirectory 'build-state.json'
        @{
            Version = $Version
            PublishDirectory = $PublishDirectory
            SignedUninstallerDirectory = $SignedUninstallerDirectory
            ReleaseDirectory = $ReleaseDirectory
            CompilerHash = (Get-FileHash -LiteralPath $CompilerPath -Algorithm SHA256).Hash
            UninstallerName = $uninstallers[0].Name
        } | ConvertTo-Json | Set-Content -LiteralPath $stateFile -Encoding utf8
        if ($env:GITHUB_OUTPUT) {
            "build-state=$stateFile" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
            "payload-directory=$inputDirectory" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
        }
        $global:LASTEXITCODE = 0
        Write-Output "Archivos sin firmar para SignPath: $inputDirectory"
        Write-Output "Estado de compilación: $stateFile"
        return
    }
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar el instalador.' }

    $installer = Join-Path $releaseDirectory "Amoxtli-$Version-windows-x64-setup.exe"
    $hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
    Write-Output "Instalador: $installer"
    Write-Output "SHA-256: $installer.sha256"
    Write-Output "Archivos de la app: $publishDirectory"
}
finally {
    $env:MSBuildEnableWorkloadResolver = $previousWorkloadResolver
    Pop-Location
}
