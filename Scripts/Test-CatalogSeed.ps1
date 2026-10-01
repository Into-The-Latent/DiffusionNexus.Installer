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
    manifest's, and catalog.zip really has that sha256. In the working tree the whole manifest must
    be the release's too (fields compared, not bytes): the SDK records the seed's channel and pack
    time as where the installed catalog came from, so a Preview stamp or a hand-made generatedAt is
    a wrong seed even over the right archive. The manifest is downloaded from
    <ReleaseBase>/latest/download/manifest.json; public, no token.

    Exit codes - New-Release.ps1 and Promote-Release.ps1 rely on them:
      0  the seed is the latest stable catalog
      3  it is an older stable release: behind the latest, and exactly the release of its own
         number (<ReleaseBase>/download/v<N>/manifest.json). What differs is listed, with the fix:
         pwsh Scripts/Update-CatalogSeed.ps1, then commit. The only answer -AllowOlderCatalog
         overrides: a deliberate hold-back is Update-CatalogSeed.ps1 -Version N.
      4  it is no stable catalog release at all: ahead of the latest, another catalog under the
         latest's number, not the release of its own number, or not a Stable manifest. Listed the
         same way. No flag ships it.
      2  it could not be checked: a release could not be downloaded or the latest is not a Stable
         manifest, the seed is missing or not a manifest, catalog.zip disagrees with its own
         manifest, or the seed folder has uncommitted changes (a release must never carry what no
         commit records). Any unexpected error lands here too, never on 3, so it can never be waved
         through with -AllowOlderCatalog. (1 is left to pwsh, which uses it for a script that does
         not parse.)

.PARAMETER RepoRoot
    The installer repo. Defaults to the folder above this script.

.PARAMETER ReleaseBase
    The catalog repo's releases page. Default: DIFFUSIONNEXUS_CATALOG_RELEASES when set (the
    script tests serve a fixture there), else
    https://github.com/Into-The-Latent/DiffusionNexus.Catalog/releases. The URL read is printed
    with the answer, so a redirected check is never mistaken for the real one.

.PARAMETER Expect
    "<version> <commit> <sha256> <channel> <generatedAt>" - one string, the five separated by spaces
    or commas - to judge instead of the working tree's seed: the catalogSeed a shipped build's
    build-info.json reports, which Promote-Release.ps1 passes. Nothing on disk is read. A channel
    other than Stable is exit 4; generatedAt is compared as an instant. (One string, because
    pwsh -File hands a script literal strings and cannot fill an array parameter.)

.EXAMPLE
    pwsh Scripts/Test-CatalogSeed.ps1

.EXAMPLE
    pwsh Scripts/Test-CatalogSeed.ps1 -Expect "5 51e1684cfa48d22344e82e7037e8e97018be72bd e7d3ee7a... Stable 2026-09-25T14:15:43.8266732+00:00"
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

# Checked, and wrong in a way no flag excuses.
function Stop-NotStable([string]$Reason) {
    Write-Host $Reason -ForegroundColor Red
    Write-Host "No flag ships this seed: -AllowOlderCatalog covers only an older stable release (Update-CatalogSeed.ps1 -Version N)." -ForegroundColor Red
    Write-Host $fix -ForegroundColor Yellow
    exit 4
}

. (Join-Path $PSScriptRoot 'CatalogRelease.ps1')
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$releaseBase = Get-CatalogReleaseBase $ReleaseBase
$manifestUrl = "$releaseBase/latest/download/manifest.json"
$seedText = $null   # the seed manifest's text; only the working tree has one, -Expect gives five values

# ------------------------------------------------------------------------ the seed under judgement
if ($Expect) {
    $values = @($Expect -split '[\s,]+' | Where-Object { $_ })   # not into $Expect: the parameter's [string] would join them again
    if ($values.Count -ne 5) { Stop-Unchecked "-Expect takes five values (version commit sha256 channel generatedAt), got $($values.Count): $($values -join ' ')" }
    # The same validator as a manifest's: one definition of a seed for both paths.
    try { $seed = New-CatalogSeed $values[0] $values[1] $values[2] $values[3] $values[4] '-Expect' }
    catch { Stop-Unchecked $_.Exception.Message }
    $seedName = 'The seed given'
    $seedLabel = 'The seed given is a {0} seed.'
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
    # Only stdout lines are files. git may write to stderr and still exit 0 (a CRLF warning during the
    # index refresh, a deprecation notice); that text is only for the message when git fails.
    $output = @(& git -C $RepoRoot status --porcelain --untracked-files=all -- $CatalogSeedFolder 2>&1)
    $status = @($output | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) { Stop-Unchecked "git status in $RepoRoot failed:`n$(($output | ForEach-Object { "$_" }) -join "`n")" }
    if ($status.Count -gt 0) {
        Stop-Unchecked "the seed under $CatalogSeedFolder has uncommitted changes:`n$(($status | ForEach-Object { "  $_" }) -join "`n")`nCommit them (or restore the files) and run the check again."
    }
    $seedName = 'The embedded seed'
    $fix = "-> pwsh Scripts/Update-CatalogSeed.ps1, then commit $CatalogSeedFolder."
    $seedText = Get-Content -LiteralPath $manifestPath -Raw
    try { $seed = Read-CatalogManifest $seedText "the embedded seed manifest $manifestPath" }
    catch { Stop-Unchecked $_.Exception.Message }
    $zipHash = Get-Sha256 $zipPath
    if ($zipHash -ne $seed.Sha256) {
        Stop-Unchecked "$zipPath does not match its manifest: its sha256 is $zipHash, the manifest says $($seed.Sha256). Run pwsh Scripts/Update-CatalogSeed.ps1 to replace both files, then commit."
    }
    $seedLabel = "$manifestPath is a {0} manifest."
}
if ($seed.Channel -ne 'Stable') {
    Stop-NotStable "$($seedLabel -f $seed.Channel) The seed is always a stable catalog: the SDK records the seed's channel as where the installed catalog came from, and promotion carries this binary to Stable users unchanged."
}

# A release's manifest, downloaded: its seed and its text. Anything short of that is "not checked".
function Get-ReleaseManifest([string]$Url, [string]$What) {
    $temp = Join-Path ([IO.Path]::GetTempPath()) ("catalogseed-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        try { Read-CatalogReleaseManifest $Url (Join-Path $temp 'manifest.json') $What } catch { Stop-Unchecked $_.Exception.Message }
    } finally { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}

# What the seed has that the release does not. Empty = the seed is that release: the five values,
# and in the working tree the whole manifest, parsed (so CRLF, a BOM or indentation do not count).
# The pack time is listed only when the first three agree: otherwise it differs anyway.
function Get-Differences($Release) {
    $lines = @(
        if ($seed.Version -ne $Release.Version) {
            $direction = if ($seed.Version -lt $Release.Version) { 'the seed is behind' } else { 'the seed is ahead: content the stable channel does not serve' }
            "catalogVersion $($seed.Version) vs $($Release.Version) ($direction)"
        }
        if ($seed.Commit -ne $Release.Commit) { "commit $($seed.Short) vs $($Release.Short)" }
        if ($seed.Sha256 -ne $Release.Sha256) { "archive sha256 $($seed.Sha256.Substring(0, 12))... vs $($Release.Sha256.Substring(0, 12))..." }
    )
    if ($lines.Count -eq 0 -and $seed.GeneratedAt -ne $Release.GeneratedAt) {
        $lines = @("generatedAt $($seed.GeneratedAt.ToString('o')) vs $($Release.GeneratedAt.ToString('o')): not the release's pack time (hand-made or re-packed)")
    }
    if ($lines.Count -eq 0 -and $null -ne $seedText) {
        $parsed = { param($text) $text | ConvertFrom-Json | ConvertTo-Json -Depth 32 -Compress }
        if ((& $parsed $seedText) -ne (& $parsed $Release.Text)) {
            $lines = @("the manifest itself is not the release's (channel, generatedAt or a section hash): hand-made or re-packed")
        }
    }
    $lines
}

# ------------------------------------------------------------------------ the latest stable release
$remote = Get-ReleaseManifest $manifestUrl 'the latest stable manifest'
if ($remote.Channel -ne 'Stable') { Stop-Unchecked "$manifestUrl is a $($remote.Channel) manifest, not a Stable one." }

# ---------------------------------------------------------------------------------- the verdict
$differences = @(Get-Differences $remote)
if ($differences.Count -eq 0) {
    Write-Host "Catalog seed v$($seed.Version) ($($seed.Short)) is the latest stable catalog. ($manifestUrl)" -ForegroundColor Green
    exit 0
}
Write-Host "$seedName is not the latest stable catalog (v$($remote.Version), $($remote.Short), $manifestUrl):" -ForegroundColor Yellow
$differences | ForEach-Object { Write-Host "  $_" }
if ($seed.Version -gt $remote.Version) { Stop-NotStable "A seed ahead of the latest stable release is no stable release." }
if ($seed.Version -eq $remote.Version) { Stop-NotStable "Another catalog under the number of the latest stable release is no stable release." }

# Behind. The override is for a deliberate hold-back, so the seed must be exactly the stable
# release of its own number, as Update-CatalogSeed.ps1 -Version N embeds it.
$tagUrl = "$releaseBase/download/v$($seed.Version)/manifest.json"
$tag = Get-ReleaseManifest $tagUrl "the stable release v$($seed.Version)"
$tagDifferences = @(Get-Differences $tag)
if ($tag.Channel -ne 'Stable') { $tagDifferences += "$tagUrl is a $($tag.Channel) manifest" }
if ($tagDifferences.Count -gt 0) {
    Write-Host "It is not the stable release v$($seed.Version) either ($tagUrl):" -ForegroundColor Red
    $tagDifferences | ForEach-Object { Write-Host "  $_" }
    Stop-NotStable "The seed is no stable catalog release."
}
Write-Host "It is the older stable release v$($seed.Version) ($tagUrl)." -ForegroundColor Yellow
Write-Host $fix -ForegroundColor Yellow
exit 3
