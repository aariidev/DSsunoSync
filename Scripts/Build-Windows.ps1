[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [switch]$SkipInstaller
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root "ArideevSunoPresence.csproj"
$Publish = Join-Path $Root "publish"
$Dist = Join-Path $Root "dist"
$projectXml = [xml](Get-Content -LiteralPath $Project -Raw)
$appVersion = $projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($appVersion)) {
    throw "No se pudo leer la versión del proyecto SunoSync."
}
$extensionManifest = Get-Content -LiteralPath (Join-Path $Root "BrowserExtension\manifest.json") -Raw | ConvertFrom-Json
if ($extensionManifest.version -ne $appVersion) {
    throw "La versión de la extensión ($($extensionManifest.version)) no coincide con la versión de la aplicación ($appVersion)."
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET 10 SDK no está instalado. Instala el SDK de .NET 10 y vuelve a ejecutar este script."
}
$version = (& dotnet --version).Trim()
if ([version]($version -replace '-.*$','') -lt [version]"10.0.100") {
    throw "Se necesita .NET SDK 10.0.100 o posterior. Detectado: $version"
}

if (Test-Path $Publish) { Remove-Item $Publish -Recurse -Force }
New-Item -ItemType Directory -Path $Publish -Force | Out-Null
& dotnet publish $Project -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o $Publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host "EXE autónomo de SunoSync creado: $Publish\ArideevSunoPresence.exe" -ForegroundColor Green
Write-Host "La extensión está integrada en el EXE y se extrae en AppData al iniciar." -ForegroundColor Green

if (-not $SkipInstaller) {
    $programFilesX86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
    $isccCandidates = @(
        $(if ($programFilesX86) { Join-Path $programFilesX86 "Inno Setup 6\ISCC.exe" }),
        $(if ($env:ProgramFiles) { Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe" })
    ) | Where-Object { $_ }
    $iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) {
        Write-Warning "No se encontró Inno Setup 6. El EXE autónomo sí está listo; instala Inno Setup 6 para crear el instalador."
    } else {
        New-Item -ItemType Directory -Path $Dist -Force | Out-Null
        & $iscc (Join-Path $Root "Installer\ArideevSunoPresence.iss") "/DMyAppVersion=$appVersion"
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup falló con código $LASTEXITCODE" }
        Write-Host "Instalador creado en: $Dist" -ForegroundColor Green
    }
}
