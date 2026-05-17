param(
    [Parameter(Mandatory = $true)]
    [string] $Root,

    [string] $Version = "3.11.0.2"
)

$ErrorActionPreference = "Stop"

$rootPath = (Resolve-Path -LiteralPath $Root).Path
$toolRoot = Join-Path $rootPath ".tools\nsis"
$localMakeNsis = Join-Path $toolRoot "NSIS\Bin\makensis.exe"

if (Test-Path -LiteralPath $localMakeNsis) {
    Write-Output $localMakeNsis
    exit 0
}

$pathMakeNsis = Get-Command "makensis.exe" -ErrorAction SilentlyContinue
if ($pathMakeNsis) {
    Write-Output $pathMakeNsis.Source
    exit 0
}

New-Item -ItemType Directory -Force -Path $toolRoot | Out-Null

$packageName = "nsis-package.$Version.nupkg"
$packagePath = Join-Path $toolRoot $packageName
$packageUrl = "https://api.nuget.org/v3-flatcontainer/nsis-package/$Version/$packageName"

if (!(Test-Path -LiteralPath $packagePath)) {
    Invoke-WebRequest -Uri $packageUrl -OutFile $packagePath
}

$extractRoot = Join-Path $toolRoot "package"
if (Test-Path -LiteralPath $extractRoot) {
    Remove-Item -LiteralPath $extractRoot -Recurse -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $extractRoot)

$extractedMakeNsis = Join-Path $extractRoot "NSIS\Bin\makensis.exe"
if (!(Test-Path -LiteralPath $extractedMakeNsis)) {
    throw "makensis.exe was not found in NSIS-Package $Version."
}

if (Test-Path -LiteralPath (Join-Path $toolRoot "NSIS")) {
    Remove-Item -LiteralPath (Join-Path $toolRoot "NSIS") -Recurse -Force
}

Move-Item -LiteralPath (Join-Path $extractRoot "NSIS") -Destination (Join-Path $toolRoot "NSIS")
Remove-Item -LiteralPath $extractRoot -Recurse -Force

if (!(Test-Path -LiteralPath $localMakeNsis)) {
    throw "NSIS was extracted but makensis.exe was not found."
}

Write-Output $localMakeNsis
