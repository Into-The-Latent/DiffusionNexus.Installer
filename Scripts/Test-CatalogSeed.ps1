<#
.SYNOPSIS
    Fails when the embedded catalog seed is not the latest stable catalog release.

.DESCRIPTION
    The installer embeds a catalog (DiffusionNexus.Installer.Electron/Assets/Catalog: catalog.zip
    and manifest.json) so a fresh machine has a workload list before it reaches the network. Until
    now a manual commit before each release kept it current, and nothing checked it. A seed ahead
    of the stable release ships content the stable channel does not serve; one behind it ships
    stale content to every fresh install. Under SDK 2.0.0 the two could ping-pong with the update
    check (docs/manual-smoke.md, section 1.4-1.6). This script refuses both, and New-Release.ps1
    runs it before it builds anything. A Preview build must pass it too: promotion never rebuilds,
    so a promoted binary must not carry a preview seed to Stable users.

    Equal means: the seed's catalogVersion, commit and archive.sha256 are the latest stable
    manifest's, and catalog.zip really has that sha256. The manifest is downloaded from
    <ReleaseBase>/latest/download/manifest.json; public, no token.

    Exit codes - New-Release.ps1 and Promote-Release.ps1 rely on them:
      0  the seed is the latest stable catalog
      3  it differs. What differs is listed, with the fix: pwsh Scripts/Update-CatalogSeed.ps1,
         then commit.
      2  it could not be checked: the release could not be downloaded or is not a Stable manifest,
         the seed is missing or not a manifest, catalog.zip disagrees with its own manifest, or the
         seed folder has uncommitted changes (a release must never carry what no commit records).
         Any unexpected error lands here too, never on 3, so it can never be waved through with
         -AllowOlderCatalog. (1 is left to pwsh, which uses it for a script that does not parse.)

.PARAMETER RepoRoot
    The installer repo. Defaults to the folder above this script.

.PARAMETER ReleaseBase
    The catalog repo's releases page. Default: DIFFUSIONNEXUS_CATALOG_RELEASES when set (the
    script tests serve a fixture there), else
    https://github.com/Into-The-Latent/DiffusionNexus.Catalog/releases. The URL read is printed
    with the answer, so a redirected check is never mistaken for the real one.

.PARAMETER Expect
    "<version> <commit> <sha256>" - one string, the three separated by spaces or commas - to judge
    instead of the working tree's seed. Promote-Release.ps1 passes what a shipped build's
    build-info.json says. Nothing on disk is read. (One string, because pwsh -File hands a script
    literal strings and cannot fill an array parameter.)

.EXAMPLE
    pwsh Scripts/Test-CatalogSeed.ps1

.EXAMPLE
    pwsh Scripts/Test-CatalogSeed.ps1 -Expect "5 51e1684cfa48d22344e82e7037e8e97018be72bd e7d3ee7a..."
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$ReleaseBase,
    [string]$Expect
)

$ErrorActionPreference = 'Stop'
# git answers with exit codes that are data here; a session that turns native exit codes into errors
# must not apply.
$PSNativeCommandUseErrorActionPreference = $false

trap {
    Write-Host "Catalog seed NOT checked: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}

function Stop-Unchecked([string]$Reason) {
    Write-Host "Catalog seed NOT checked: $Reason" -ForegroundColor Red
    exit 2
}

. (Join-Path $PSScriptRoot 'CatalogRelease.ps1')
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$manifestUrl = "$(Get-CatalogReleaseBase $ReleaseBase)/latest/download/manifest.json"

# ------------------------------------------------------------------------ the seed under judgement
if ($Expect) {
    $values = @($Expect -split '[\s,]+' | Where-Object { $_ })   # not into $Expect: the parameter's [string] would join them again
    if ($values.Count -ne 3) { Stop-Unchecked "-Expect takes three values (version commit sha256), got $($values.Count): $($values -join ' ')" }
    if ($values[0] -notmatch '^\d+$') { Stop-Unchecked "-Expect version '$($values[0])' is not a number." }
    if ($values[1] -notmatch '^[0-9a-fA-F]{40}$') { Stop-Unchecked "-Expect commit '$($values[1])' is not a 40-digit commit." }
    if ($values[2] -notmatch '^[0-9a-fA-F]{64}$') { Stop-Unchecked "-Expect sha256 '$($values[2])' is not a sha256." }
    $seed = [pscustomobject]@{
        Version = [int]$values[0]; Commit = $values[1].ToLowerInvariant(); Sha256 = $values[2].ToLowerInvariant()
        Short = $values[1].Substring(0, 7).ToLowerInvariant()
    }
    $seedName = 'The seed given'
    $fix = 'The build that carries this seed does not bundle the latest stable catalog.'
} else {
    # -LiteralPath throughout: a checkout under a folder with [ ] in its name must not read as "no seed".
    $seedDir = Join-Path $RepoRoot $CatalogSeedFolder
    $manifestPath = Join-Path $seedDir 'manifest.json'
    $zipPath = Join-Path $seedDir 'catalog.zip'
    foreach ($file in $manifestPath, $zipPath) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { Stop-Unchecked "$file does not exist." }
    }
    # Uncommitted seed files must never ship: a release records a commit, and the seed in that commit
    # would not be the seed built. An untracked file in the folder counts too.
    $status = @(& git -C $RepoRoot status --porcelain --untracked-files=all -- $CatalogSeedFolder 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) { Stop-Unchecked "git status in $RepoRoot failed:`n$($status -join "`n")" }
    if ($status.Count -gt 0) {
        Stop-Unchecked "the seed under $CatalogSeedFolder has uncommitted changes:`n$(($status | ForEach-Object { "  $_" }) -join "`n")`nCommit them (or restore the files) and run the check again."
    }
    try { $seed = Read-CatalogManifest (Get-Content -LiteralPath $manifestPath -Raw) "the embedded seed manifest $manifestPath" }
    catch { Stop-Unchecked $_.Exception.Message }
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($zipHash -ne $seed.Sha256) {
        Stop-Unchecked "$zipPath does not match its manifest: its sha256 is $zipHash, the manifest says $($seed.Sha256). Run pwsh Scripts/Update-CatalogSeed.ps1 to replace both files, then commit."
    }
    $seedName = 'The embedded seed'
    $fix = "-> pwsh Scripts/Update-CatalogSeed.ps1, then commit $CatalogSeedFolder."
}

# ------------------------------------------------------------------------ the latest stable release
$temp = Join-Path ([IO.Path]::GetTempPath()) ("catalogseed-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $remotePath = Join-Path $temp 'manifest.json'
    try { Save-CatalogAsset $manifestUrl $remotePath } catch { Stop-Unchecked $_.Exception.Message }
    try { $remote = Read-CatalogManifest (Get-Content -LiteralPath $remotePath -Raw) "the latest stable manifest ($manifestUrl)" }
    catch { Stop-Unchecked $_.Exception.Message }
} finally { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
if ($remote.Channel -ne 'Stable') { Stop-Unchecked "$manifestUrl is a $($remote.Channel) manifest, not a Stable one." }

# ---------------------------------------------------------------------------------- the verdict
$differences = @(
    if ($seed.Version -ne $remote.Version) {
        $direction = if ($seed.Version -lt $remote.Version) { 'the seed is behind' } else { 'the seed is ahead: content the stable channel does not serve' }
        "catalogVersion $($seed.Version) vs $($remote.Version) ($direction)"
    }
    if ($seed.Commit -ne $remote.Commit) { "commit $($seed.Short) vs $($remote.Short)" }
    if ($seed.Sha256 -ne $remote.Sha256) { "archive sha256 $($seed.Sha256.Substring(0, 12))... vs $($remote.Sha256.Substring(0, 12))..." }
)
if ($differences.Count -eq 0) {
    Write-Host "Catalog seed v$($seed.Version) ($($seed.Short)) is the latest stable catalog. ($manifestUrl)" -ForegroundColor Green
    exit 0
}
Write-Host "$seedName is not the latest stable catalog (v$($remote.Version), $($remote.Short), $manifestUrl):" -ForegroundColor Yellow
$differences | ForEach-Object { Write-Host "  $_" }
Write-Host $fix -ForegroundColor Yellow
exit 3
