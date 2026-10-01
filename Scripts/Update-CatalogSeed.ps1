<#
.SYNOPSIS
    Replaces the embedded catalog seed with the latest stable catalog release.

.DESCRIPTION
    The write half of the seed gate, kept out of New-Release.ps1 so a refused release still changes
    nothing. Downloads manifest.json and catalog.zip from the catalog repo's latest stable release
    (or the tag -Version names), verifies the archive against the manifest's sha256, replaces the
    two files under DiffusionNexus.Installer.Electron/Assets/Catalog, and prints the version it
    embedded and the commit to make. This replaces the hand-made "chore(catalog): embed the vN stable
    catalog seed" commit that preceded every release. A Preview manifest is refused: the seed is
    always a stable catalog, because a Preview installer build is promoted without a rebuild.

    Exit codes: 0 the seed was written (unchanged when it already was that release); 2 nothing was
    replaced - the release could not be downloaded, is not a Stable manifest, or its archive does
    not match its manifest.

.PARAMETER RepoRoot
    The installer repo. Defaults to the folder above this script.

.PARAMETER ReleaseBase
    The catalog repo's releases page. Default: DIFFUSIONNEXUS_CATALOG_RELEASES when set, else
    https://github.com/Into-The-Latent/DiffusionNexus.Catalog/releases.

.PARAMETER Version
    Embed stable release vN instead of the latest: a deliberate hold-back. New-Release.ps1 will
    then need -AllowOlderCatalog.

.EXAMPLE
    pwsh Scripts/Update-CatalogSeed.ps1
    git add DiffusionNexus.Installer.Electron/Assets/Catalog
    git commit -m "chore(catalog): embed the v5 stable catalog seed"
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$ReleaseBase,
    [int]$Version
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

trap {
    Write-Host "Catalog seed NOT updated: $($_.Exception.Message) Nothing was replaced." -ForegroundColor Red
    exit 2
}

. (Join-Path $PSScriptRoot 'CatalogRelease.ps1')
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$assets = if ($Version) { "$(Get-CatalogReleaseBase $ReleaseBase)/download/v$Version" } else { "$(Get-CatalogReleaseBase $ReleaseBase)/latest/download" }

# -LiteralPath throughout: a checkout under a folder with [ ] in its name is still the installer repo.
$seedDir = Join-Path $RepoRoot $CatalogSeedFolder
if (-not (Test-Path -LiteralPath $seedDir -PathType Container)) { throw "$seedDir does not exist. Is $RepoRoot the installer repo?" }

$before = @('manifest.json', 'catalog.zip' | ForEach-Object { Get-Sha256 (Join-Path $seedDir $_) })

# Both assets to a temp folder first, verified there, then moved: the seed is never half replaced.
$temp = Join-Path ([IO.Path]::GetTempPath()) ("catalogseed-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $manifestPath = Join-Path $temp 'manifest.json'
    $zipPath = Join-Path $temp 'catalog.zip'
    Save-CatalogAsset "$assets/manifest.json" $manifestPath
    $manifest = Read-CatalogManifest (Get-Content -LiteralPath $manifestPath -Raw) "$assets/manifest.json"
    if ($manifest.Channel -ne 'Stable') { throw "$assets/manifest.json is a $($manifest.Channel) manifest, not a Stable one; the seed is always a stable catalog." }
    if ($Version -and $manifest.Version -ne $Version) { throw "$assets/manifest.json says catalogVersion $($manifest.Version), not $Version." }
    Save-CatalogAsset "$assets/catalog.zip" $zipPath
    $zipHash = Get-Sha256 $zipPath
    if ($zipHash -ne $manifest.Sha256) { throw "the downloaded catalog.zip does not match its manifest: its sha256 is $zipHash, the manifest says $($manifest.Sha256)." }
    Move-Item -LiteralPath $manifestPath -Destination (Join-Path $seedDir 'manifest.json') -Force
    Move-Item -LiteralPath $zipPath -Destination (Join-Path $seedDir 'catalog.zip') -Force
} finally { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }

$after = @('manifest.json', 'catalog.zip' | ForEach-Object { Get-Sha256 (Join-Path $seedDir $_) })
$state = if (($before -join ' ') -eq ($after -join ' ')) { ' - unchanged, it already was' } else { '' }
Write-Host "Embedded catalog v$($manifest.Version) ($($manifest.Short), stable) under $CatalogSeedFolder$state. ($assets)" -ForegroundColor Green
Write-Host "Commit it: git add $CatalogSeedFolder; git commit -m `"chore(catalog): embed the v$($manifest.Version) stable catalog seed`""
exit 0
