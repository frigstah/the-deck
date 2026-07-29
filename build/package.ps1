<#
    Builds everything a release needs: a self-contained app folder, a portable zip, and an
    installer.

    The app is published once and used for both. The only difference between the portable and
    installed layouts is the deck-portable.txt marker, which is added to the zip and never to the
    installer - see AppPaths, where that file is what moves settings next to the executable.

    Usage:
        .\build\package.ps1                      # version from the csproj
        .\build\package.ps1 -Version 1.3.0.42    # what CI does
#>

[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "publish"
$appDir = Join-Path $publish "app"

if (-not $Version) {
    $csproj = Get-Content (Join-Path $root "src\Deck.App\Deck.App.csproj") -Raw
    if ($csproj -notmatch '<Version>([^<]+)</Version>') { throw "No <Version> in Deck.App.csproj" }
    $Version = $Matches[1]
}

# System.Version needs four parts to compare cleanly against a release tag, so a plain 1.3.0
# becomes 1.3.0.0 rather than being left short.
$parts = $Version.Split('.')
while ($parts.Count -lt 4) { $parts += "0" }
$Version = ($parts[0..3]) -join '.'

Write-Host "Deck $Version  ($Configuration / $Runtime)" -ForegroundColor Cyan

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Path $publish | Out-Null

# ---------------------------------------------------------------- publish

Write-Host "publishing..." -ForegroundColor DarkGray
& dotnet publish (Join-Path $root "src\Deck.App\Deck.App.csproj") `
    -c $Configuration -r $Runtime --self-contained true `
    -p:Version=$Version -p:AssemblyVersion=$Version -p:FileVersion=$Version `
    -p:DebugType=none -p:DebugSymbols=false `
    -o $appDir -v quiet --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Copy-Item (Join-Path $root "LICENSE") (Join-Path $appDir "LICENSE") -Force
Copy-Item (Join-Path $root "installer\READ ME FIRST.txt") (Join-Path $appDir "READ ME FIRST.txt") -Force

$size = [math]::Round((Get-ChildItem $appDir -Recurse | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "  app folder: $size MB, $((Get-ChildItem $appDir -Recurse -File).Count) files"

# ---------------------------------------------------------------- portable zip

Write-Host "packing portable zip..." -ForegroundColor DarkGray
$portableDir = Join-Path $publish "portable\Deck-$Version"
New-Item -ItemType Directory -Path $portableDir -Force | Out-Null
Copy-Item "$appDir\*" $portableDir -Recurse -Force

# The marker that moves settings into a data folder beside the executable (I7). Present only here.
@"
This file makes Deck portable.

While it exists next to Deck.exe, settings, servers and logs are kept in the "data" folder
beside it instead of in %APPDATA%\Deck. Delete it to go back to the normal location.
"@ | Set-Content (Join-Path $portableDir "deck-portable.txt") -Encoding UTF8

$portableZip = Join-Path $publish "Deck-$Version-portable-$Runtime.zip"
Compress-Archive -Path $portableDir -DestinationPath $portableZip -CompressionLevel Optimal
Remove-Item (Join-Path $publish "portable") -Recurse -Force

# ---------------------------------------------------------------- update payload

# What the built-in updater downloads. Same files, no marker and no top-level folder, so the
# updater can copy its contents straight over an install of either kind without changing which
# mode that install is in.
Write-Host "packing update payload..." -ForegroundColor DarkGray
$updateZip = Join-Path $publish "Deck-$Version-update-$Runtime.zip"
Compress-Archive -Path "$appDir\*" -DestinationPath $updateZip -CompressionLevel Optimal

# ---------------------------------------------------------------- installer

$setup = $null
if (-not $SkipInstaller) {
    # The per-user location first: that is where winget puts it, and where a GitHub runner does
    # not. Both are checked because the same script runs in both places.
    $iscc = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue)?.Source }

    if ($iscc) {
        Write-Host "building installer..." -ForegroundColor DarkGray
        & $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$appDir" "/DOutputDir=$publish" `
            (Join-Path $root "installer\Deck.iss")
        if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
        $setup = Join-Path $publish "Deck-$Version-setup.exe"
    }
    else {
        Write-Warning "Inno Setup not found - skipping the installer."
        Write-Warning "Install it with:  winget install JRSoftware.InnoSetup"
    }
}

# ---------------------------------------------------------------- digests

# The updater refuses any download whose SHA-256 does not match the one published beside it, so
# these are part of the release rather than a nicety.
$artifacts = @($portableZip, $updateZip) + @($setup | Where-Object { $_ })
$lines = foreach ($a in $artifacts) {
    $hash = (Get-FileHash $a -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $a -Leaf)"
}
$lines | Set-Content (Join-Path $publish "SHA256SUMS.txt") -Encoding ASCII

Write-Host ""
Write-Host "done:" -ForegroundColor Green
foreach ($a in $artifacts) {
    $mb = [math]::Round((Get-Item $a).Length / 1MB, 1)
    Write-Host ("  {0,-46} {1,6} MB" -f (Split-Path $a -Leaf), $mb)
}
Write-Host "  SHA256SUMS.txt"
Write-Host ""
$lines | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
