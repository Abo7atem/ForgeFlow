param(
    [switch]$SkipObfuscation
)

$ErrorActionPreference = 'Stop'

function Get-LatestPackPath {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [Parameter(Mandatory = $true)][string]$FrameworkFolder
    )

    if (-not (Test-Path -LiteralPath $RootPath)) {
        throw "Pack root not found: $RootPath"
    }

    $candidates = Get-ChildItem -LiteralPath $RootPath -Directory |
        Sort-Object { [version]$_.Name } -Descending

    foreach ($candidate in $candidates) {
        $frameworkPath = Join-Path $candidate.FullName $FrameworkFolder
        if (Test-Path -LiteralPath $frameworkPath) {
            return $frameworkPath
        }
    }

    throw "No compatible pack path found under: $RootPath for $FrameworkFolder"
}

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectDir = Join-Path $repoRoot 'ForgeFlow'
$projectPath = Join-Path $projectDir 'ForgeFlow.csproj'
$versionPath = Join-Path $repoRoot 'version.txt'
$releaseDir = Join-Path $projectDir 'bin\Release\net8.0-windows'
$protectedDir = Join-Path $releaseDir 'Protected'
$generatedConfigPath = Join-Path $projectDir 'obfuscar.generated.xml'
$obfuscarExe = Join-Path $env:USERPROFILE '.nuget\packages\obfuscar\2.2.50\tools\Obfuscar.Console.exe'

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Project file not found: $projectPath"
}

$version = (Get-Content -LiteralPath $versionPath -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'version.txt is empty.'
}

Write-Host "Building ForgeFlow $version..." -ForegroundColor Cyan
& dotnet build $projectPath --configuration Release "/p:Version=$version" "/p:AssemblyVersion=$version.0" "/p:FileVersion=$version.0"
if ($LASTEXITCODE -ne 0) {
    throw 'Release build failed.'
}

if (Test-Path -LiteralPath $protectedDir) {
    Remove-Item -LiteralPath $protectedDir -Recurse -Force
}
New-Item -ItemType Directory -Path $protectedDir -Force | Out-Null

if (-not $SkipObfuscation) {
    if (-not (Test-Path -LiteralPath $obfuscarExe)) {
        throw "Obfuscar.Console.exe not found: $obfuscarExe"
    }

    $windowsDesktopRef = Get-LatestPackPath -RootPath 'C:\Program Files\dotnet\packs\Microsoft.WindowsDesktop.App.Ref' -FrameworkFolder 'ref\net8.0'
    $netCoreRef = Get-LatestPackPath -RootPath 'C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref' -FrameworkFolder 'ref\net8.0'

    $obfuscarXml = @"
<?xml version="1.0" encoding="utf-8"?>
<Obfuscator>
  <Var name="InPath" value="$releaseDir\" />
  <Var name="OutPath" value="$protectedDir\" />
  <Var name="KeepPublicApi" value="true" />
  <Var name="HidePrivateApi" value="true" />
  <Var name="RenameFields" value="true" />
  <Var name="RenameMethods" value="true" />
  <Var name="RenameProperties" value="true" />
  <Var name="RenameEvents" value="true" />
  <Var name="UseUnicodeNames" value="true" />
  <Var name="HideStrings" value="true" />
  <AssemblySearchPath path="$releaseDir" />
  <AssemblySearchPath path="$windowsDesktopRef" />
  <AssemblySearchPath path="$netCoreRef" />
  <Module file="`$(InPath)ForgeFlow.dll" />
</Obfuscator>
"@

    [System.IO.File]::WriteAllText($generatedConfigPath, $obfuscarXml, [System.Text.UTF8Encoding]::new($false))

    Write-Host 'Running Obfuscar...' -ForegroundColor Cyan
    & $obfuscarExe $generatedConfigPath
    if ($LASTEXITCODE -ne 0) {
        throw 'Obfuscation failed.'
    }
}
else {
    Copy-Item -LiteralPath (Join-Path $releaseDir 'ForgeFlow.dll') -Destination (Join-Path $protectedDir 'ForgeFlow.dll') -Force
}

$runtimeFiles = @(
    'ForgeFlow.exe',
    'ForgeFlow.deps.json',
    'ForgeFlow.runtimeconfig.json',
    'version.txt'
)

foreach ($file in $runtimeFiles) {
    $source = Join-Path $releaseDir $file
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $protectedDir $file) -Force
    }
}

$toolsSource = Join-Path $releaseDir 'Tools'
if (Test-Path -LiteralPath $toolsSource) {
    Copy-Item -LiteralPath $toolsSource -Destination (Join-Path $protectedDir 'Tools') -Recurse -Force
}

$runtimeSource = Join-Path $releaseDir 'runtimes'
if (Test-Path -LiteralPath $runtimeSource) {
    Copy-Item -LiteralPath $runtimeSource -Destination (Join-Path $protectedDir 'runtimes') -Recurse -Force
}

$mappingPath = Join-Path $protectedDir 'Mapping.txt'
if (Test-Path -LiteralPath $mappingPath) {
    Remove-Item -LiteralPath $mappingPath -Force
}

Write-Host ''
Write-Host 'ForgeFlow protected release completed.' -ForegroundColor Green
Write-Host "Output: $protectedDir" -ForegroundColor Green
