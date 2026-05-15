param(
    [Parameter(Mandatory = $true)]
    [string]$Root,

    [Parameter(Mandatory = $true)]
    [string]$RuntimeIdentifier,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$DownloadUrl = $env:WINDIVERT_URL
)

$ErrorActionPreference = "Stop"

$rootPath = [IO.Path]::GetFullPath($Root)
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$rid = $RuntimeIdentifier.ToLowerInvariant()
$downloadUrls = if ([string]::IsNullOrWhiteSpace($DownloadUrl)) {
    @(
        "https://reqrypt.org/download/WinDivert-2.2.2-A.zip",
        "https://github.com/basil00/Divert/releases/download/v2.2.2/WinDivert-2.2.2-A.zip"
    )
}
else {
    @($DownloadUrl)
}

switch ($rid) {
    "win-x64" {
        $arch = "x64"
        $driverFile = "WinDivert64.sys"
    }
    "win-x86" {
        $arch = "x86"
        $driverFile = "WinDivert32.sys"
    }
    default {
        throw "WinDivert automatic packaging currently supports win-x64 and win-x86 only. RuntimeIdentifier=$RuntimeIdentifier"
    }
}

function Test-WinDivertDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory
    )

    $dllPath = Join-Path $Directory "WinDivert.dll"
    $driverPath = Join-Path $Directory $driverFile
    return (Test-Path -LiteralPath $dllPath -PathType Leaf) -and
        (Test-Path -LiteralPath $driverPath -PathType Leaf)
}

function Get-FirstWinDivertDirectory {
    $candidateDirectories = @(
        (Join-Path $rootPath $arch),
        (Join-Path $rootPath "drivers\$arch"),
        (Join-Path $rootPath "AppProxyHelper\drivers\$arch"),
        $rootPath,
        (Join-Path $rootPath "drivers"),
        (Join-Path $rootPath "AppProxyHelper\drivers")
    )

    foreach ($directory in $candidateDirectories) {
        if ((Test-Path -LiteralPath $directory -PathType Container) -and
            (Test-WinDivertDirectory -Directory $directory)) {
            return $directory
        }
    }

    return $null
}

function Ensure-DownloadedWinDivert {
    $cacheRoot = Join-Path $rootPath ".appdata\windivert"
    $extractRoot = Join-Path $cacheRoot "WinDivert-2.2.2-A"
    $archDirectory = Join-Path $extractRoot $arch

    if (Test-WinDivertDirectory -Directory $archDirectory) {
        return $archDirectory
    }

    New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null
    $zipPath = Join-Path $cacheRoot "WinDivert-2.2.2-A.zip"

    if (Test-Path -LiteralPath $zipPath -PathType Leaf) {
        Write-Host "Using cached WinDivert archive: $zipPath"
        if (Expand-WinDivertArchive -ZipPath $zipPath -Destination $cacheRoot -ArchDirectory $archDirectory) {
            return $archDirectory
        }

        Write-Host "Cached WinDivert archive is invalid; downloading again."
    }

    Download-WinDivertArchive -Urls $downloadUrls -OutputPath $zipPath

    if (-not (Expand-WinDivertArchive -ZipPath $zipPath -Destination $cacheRoot -ArchDirectory $archDirectory)) {
        throw "Downloaded WinDivert package did not contain $arch\WinDivert.dll and $arch\$driverFile."
    }

    return $archDirectory
}

function Download-WinDivertArchive {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Urls,

        [Parameter(Mandatory = $true)]
        [string]$OutputPath
    )

    $maxAttempts = 3
    $lastError = $null
    foreach ($url in $Urls) {
        for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
            try {
                Write-Host "Downloading WinDivert from $url (attempt $attempt/$maxAttempts)"
                Invoke-WebRequest -Uri $url -OutFile $OutputPath -UseBasicParsing -TimeoutSec 120
                return
            }
            catch {
                $lastError = $_
                if ($attempt -lt $maxAttempts) {
                    Write-Host "WinDivert download failed: $($_.Exception.Message)"
                    Start-Sleep -Seconds (2 * $attempt)
                }
            }
        }

        Write-Host "WinDivert download source failed: $url"
    }

    if ($null -ne $lastError) {
        throw $lastError
    }

    throw "No WinDivert download URLs were configured."
}

function Expand-WinDivertArchive {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ZipPath,

        [Parameter(Mandatory = $true)]
        [string]$Destination,

        [Parameter(Mandatory = $true)]
        [string]$ArchDirectory
    )

    try {
        Write-Host "Extracting WinDivert package"
        Expand-Archive -LiteralPath $ZipPath -DestinationPath $Destination -Force
    }
    catch {
        return $false
    }

    return Test-WinDivertDirectory -Directory $ArchDirectory
}

function Copy-WinDivertFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    try {
        Copy-Item -LiteralPath $Source -Destination $Destination -Force
        return
    }
    catch {
        if (Test-Path -LiteralPath $Destination -PathType Leaf) {
            $sourceItem = Get-Item -LiteralPath $Source
            $destinationItem = Get-Item -LiteralPath $Destination
            if ($sourceItem.Length -eq $destinationItem.Length) {
                Write-Host "Using existing locked WinDivert file: $Destination"
                return
            }
        }

        throw
    }
}

$sourceDirectory = Get-FirstWinDivertDirectory
if ($null -eq $sourceDirectory) {
    $sourceDirectory = Ensure-DownloadedWinDivert
}

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

$dllSource = Join-Path $sourceDirectory "WinDivert.dll"
$driverSource = Join-Path $sourceDirectory $driverFile
$dllTarget = Join-Path $outputPath "WinDivert.dll"
$driverTarget = Join-Path $outputPath $driverFile

Copy-WinDivertFile -Source $dllSource -Destination $dllTarget
Copy-WinDivertFile -Source $driverSource -Destination $driverTarget

Write-Host "WinDivert copied to output root:"
Write-Host "  $dllTarget"
Write-Host "  $driverTarget"
