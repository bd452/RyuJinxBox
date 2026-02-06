<#
.SYNOPSIS
    Builds and packages Ryujinx for Xbox sideloading.

.DESCRIPTION
    1. Publishes Ryujinx.Xbox as self-contained win-x64
    2. Copies Vulkan ICD manifest
    3. Creates AppX package layout
    4. Optionally creates .appx for sideloading

.PARAMETER Configuration
    Build configuration (Debug or Release). Default: Release

.PARAMETER SkipPackage
    If set, skips AppX packaging (just publishes the binaries)

.EXAMPLE
    .\build-xbox.ps1
    .\build-xbox.ps1 -Configuration Debug -SkipPackage
#>
param(
    [string]$Configuration = "Release",
    [switch]$SkipPackage
)

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
$ProjectPath = "$RepoRoot\src\Ryujinx.Xbox\Ryujinx.Xbox.csproj"
$PublishDir = "$RepoRoot\build\xbox\publish"
$PackageDir = "$RepoRoot\build\xbox\package"

Write-Host "=== Ryujinx Xbox Build ===" -ForegroundColor Green
Write-Host "Configuration: $Configuration"
Write-Host "Repo root: $RepoRoot"
Write-Host ""

# Step 1: Publish
Write-Host "Publishing Ryujinx.Xbox..." -ForegroundColor Cyan
dotnet publish $ProjectPath `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -o $PublishDir `
    /p:DefineConstants=XBOX

if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish failed!"
    exit 1
}

# Step 2: Copy Vulkan ICD manifest
Write-Host "Copying Vulkan ICD manifest..." -ForegroundColor Cyan
Copy-Item "$PSScriptRoot\xbox_vulkan_icd.json" "$PublishDir\" -Force

# Step 3: Create directory structure for ROMs and keys
Write-Host "Creating directory structure..." -ForegroundColor Cyan
$dirs = @("roms", "system", "nand", "sd", "cache\shaders", "cache\pipelines")
foreach ($dir in $dirs) {
    New-Item -ItemType Directory -Path "$PublishDir\$dir" -Force | Out-Null
}

Write-Host ""
Write-Host "=== Build complete ===" -ForegroundColor Green
Write-Host "Output: $PublishDir"
Write-Host ""
Write-Host "Before sideloading to Xbox:" -ForegroundColor Yellow
Write-Host "  1. Place vulkan_dzn.dll (Mesa Dozen Vulkan ICD) in: $PublishDir"
Write-Host "  2. Place prod.keys in: $PublishDir\system\"
Write-Host "  3. Place ROM files in: $PublishDir\roms\"
Write-Host ""

if (-not $SkipPackage) {
    # Step 4: Create AppX package layout
    Write-Host "Creating AppX layout..." -ForegroundColor Cyan

    if (Test-Path $PackageDir) {
        Remove-Item $PackageDir -Recurse -Force
    }

    New-Item -ItemType Directory -Path $PackageDir -Force | Out-Null
    New-Item -ItemType Directory -Path "$PackageDir\Assets" -Force | Out-Null

    # Copy published binaries
    Copy-Item "$PublishDir\*" "$PackageDir\" -Recurse -Force

    # Copy manifest
    Copy-Item "$PSScriptRoot\Package.appxmanifest" "$PackageDir\AppxManifest.xml" -Force

    # Create placeholder assets (required by AppX)
    $assetSizes = @(
        @{ Name = "StoreLogo.png"; Width = 50; Height = 50 },
        @{ Name = "Square150x150Logo.png"; Width = 150; Height = 150 },
        @{ Name = "Square44x44Logo.png"; Width = 44; Height = 44 },
        @{ Name = "Wide310x150Logo.png"; Width = 310; Height = 150 },
        @{ Name = "SplashScreen.png"; Width = 620; Height = 300 }
    )

    Write-Host "  Note: Placeholder assets created. Replace with real logos for production." -ForegroundColor Yellow

    foreach ($asset in $assetSizes) {
        $path = "$PackageDir\Assets\$($asset.Name)"
        if (-not (Test-Path $path)) {
            # Create a minimal 1-pixel PNG as placeholder
            # In production, replace these with real Xbox tile images
            [byte[]]$png = @(137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,0,1,0,0,0,1,8,2,0,0,0,144,119,83,222,0,0,0,12,73,68,65,84,8,215,99,248,207,192,0,0,0,3,0,1,54,174,200,34,0,0,0,0,73,69,78,68,174,66,96,130)
            [System.IO.File]::WriteAllBytes($path, $png)
        }
    }

    Write-Host ""
    Write-Host "AppX layout ready at: $PackageDir" -ForegroundColor Green
    Write-Host ""
    Write-Host "To create .appx and sideload:" -ForegroundColor Yellow
    Write-Host "  makeappx pack /d `"$PackageDir`" /p `"$RepoRoot\build\xbox\Ryujinx.Xbox.appx`""
    Write-Host ""
    Write-Host "To sideload to Xbox:" -ForegroundColor Yellow
    Write-Host "  1. Enable Dev Mode on your Xbox Series S/X"
    Write-Host "  2. Open Xbox Device Portal in your browser"
    Write-Host "  3. Go to My Games & Apps > Install"
    Write-Host "  4. Upload Ryujinx.Xbox.appx"
    Write-Host "  5. Upload ROMs via File Explorer > LocalState > roms"
    Write-Host "  6. Upload prod.keys via File Explorer > LocalState > system"
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
