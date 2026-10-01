<#
.SYNOPSIS
    Promotes a Preview release of the Easy Workload Installer to Stable, after checking that its SDK
    and its bundled catalog are still current.

.DESCRIPTION
    A Preview build is a GitHub pre-release. Promoting it un-marks it and makes it GitHub's latest
    release, which is what installs on Stable read: no rebuild, so Stable gets exactly the binaries
    the testers ran. But the build may have waited in Preview while SDK fixes or a catalog release
    landed, and a hand-typed `gh release edit` checks none of that. This script does, from what the
    build says about itself (its build-info.json asset), against what is current NOW:

      1. A signed-in gh account that can write to the installer repo (Scripts/ReleaseAccount.ps1);
         none -> refused before anything is read. Only this script's gh calls use its token.
      2. The release vX: it must exist, be published (not a draft) and be a pre-release. Already
         Stable -> nothing to promote. A version below the current Stable release is refused:
         --latest would move GitHub's latest release backwards.
      3. Its build-info.json asset (New-Release.ps1 Step 1c). A release without one was made before
         the asset existed: cut a new Preview.
      4. Test-SdkPin.ps1 -Pin <sdk> against SDK develop and Test-CatalogSeed.ps1 -Expect <seed>
         against the latest stable catalog. Both always run, so one refusal lists everything.
         Behind (exit 3) -> refused, unless -AllowOlderSdk / -AllowOlderCatalog. Not checked
         (exit 2), and a seed that is no stable catalog release at all (exit 4) -> refused, no flag.
      5. gh release edit vX --prerelease=false --latest.

.PARAMETER Version
    The release to promote, e.g. 3.0.11 (its tag is v3.0.11).

.PARAMETER AllowOlderSdk
    Promote even though SDK develop has commits the build's SDK version does not contain - a
    deliberate hold-back. The commits are still listed. It does not override a check that could not
    run.

.PARAMETER AllowOlderCatalog
    Promote even though the build bundles an older stable catalog release than the latest - a
    deliberate hold-back. It does not override a seed that is no stable release at all, nor a check
    that could not run.

.PARAMETER CatalogReleases
    The catalog releases page the seed is judged against. Default: the real one,
    https://github.com/Into-The-Latent/DiffusionNexus.Catalog/releases. For the script tests, which
    serve a fixture. Only this parameter counts: DIFFUSIONNEXUS_CATALOG_RELEASES is ignored, so a
    value left in the environment cannot steer a promotion. Any other value is announced.

.EXAMPLE
    .\Scripts\Promote-Release.ps1 -Version 3.0.11
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [switch]$AllowOlderSdk,
    [switch]$AllowOlderCatalog,
    [string]$CatalogReleases
)

$ErrorActionPreference = 'Stop'
# The gates and gh answer with exit codes this script reads; a profile that turns native exit codes
# into errors would throw on the first "no".
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = Split-Path $PSScriptRoot -Parent
$ghRepo   = 'Into-The-Latent/DiffusionNexus.Installer'
$tag      = "v$Version"
. (Join-Path $PSScriptRoot 'ReleaseAccount.ps1')
. (Join-Path $PSScriptRoot 'CatalogRelease.ps1')

# One gh call under the release token: its stdout, and its stderr for the message when it fails.
function Invoke-ReleaseGh([string[]]$GhArgs) {
    $lines = @(Invoke-WithGhToken $releaseToken { gh @GhArgs 2>&1 })
    $exit = $LASTEXITCODE
    [pscustomobject]@{
        ExitCode = $exit
        Out      = @($lines | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }) -join "`n"
        Err      = @($lines | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }) -join "`n"
    }
}

function Get-ReleaseState {
    $view = Invoke-ReleaseGh @('release', 'view', $tag, '--repo', $ghRepo, '--json', 'isPrerelease,isDraft,assets')
    if ($view.ExitCode -ne 0) { return $null }
    try { $view.Out | ConvertFrom-Json } catch { $null }
}

# ------------------------------------------------------------------------------- 1. the account
Write-Host "Step 1: a signed-in gh account can edit releases on $ghRepo" -ForegroundColor Cyan
$releaseToken = Resolve-ReleaseToken $ghRepo
if (-not $releaseToken) { throw "$(Get-NoReleaseAccountMessage $ghRepo) Nothing was read or changed." }

# ------------------------------------------------------------------------------- 2. the release
Write-Host "Step 2: release $tag must be a published Preview release" -ForegroundColor Cyan
$view = Invoke-ReleaseGh @('release', 'view', $tag, '--repo', $ghRepo, '--json', 'isPrerelease,isDraft,assets')
if ($view.ExitCode -ne 0) { throw "Could not read release $tag on $ghRepo (gh exit $($view.ExitCode)):`n$($view.Err)`nNothing was changed." }
try { $release = $view.Out | ConvertFrom-Json } catch { throw "gh release view $tag answered something that is not JSON:`n$($view.Out)`nNothing was changed." }
if ($release.isDraft) { throw "$tag is a draft, not a published Preview release: testers never ran it. Nothing was changed." }
if (-not $release.isPrerelease) {
    Write-Host "$tag is already a Stable release: nothing to promote." -ForegroundColor Green
    return
}
# --latest makes this GitHub's latest release, the one Stable installs read. Below the current one,
# that would move Stable backwards.
$latest = Invoke-ReleaseGh @('api', "repos/$ghRepo/releases/latest", '--jq', '.tag_name')
if ($latest.ExitCode -ne 0) { throw "Could not read the latest Stable release of $ghRepo (gh exit $($latest.ExitCode)):`n$($latest.Err)`nNothing was changed." }
$latestTag = $latest.Out.Trim()
$latestVersion = $null
if ($latestTag -match '^v(\d+\.\d+\.\d+)$' -and [version]::TryParse($Matches[1], [ref]$latestVersion) -and $latestVersion -gt [version]$Version) {
    throw "$latestTag is already Stable and newer than $tag. Promoting $tag would make GitHub's latest release go backwards. Nothing was changed."
}

# ------------------------------------------------------------------------- 3. what the build is
Write-Host "Step 3: build-info.json of $tag" -ForegroundColor Cyan
if (-not @($release.assets | Where-Object name -eq 'build-info.json')) {
    throw "$tag has no build-info.json asset: it was released before New-Release.ps1 wrote one, so nothing says which SDK and catalog it carries. Cut a new Preview with New-Release.ps1 -Prerelease. Nothing was changed."
}
$download = Join-Path ([IO.Path]::GetTempPath()) ("promote-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $download | Out-Null
try {
    $got = Invoke-ReleaseGh @('release', 'download', $tag, '--repo', $ghRepo, '--pattern', 'build-info.json', '--dir', $download)
    $buildInfoPath = Join-Path $download 'build-info.json'
    if ($got.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $buildInfoPath -PathType Leaf)) {
        throw "Could not download build-info.json of $tag (gh exit $($got.ExitCode)):`n$($got.Err)`nNothing was changed."
    }
    $buildInfoText = Get-Content -LiteralPath $buildInfoPath -Raw
} finally { Remove-Item -LiteralPath $download -Recurse -Force -ErrorAction SilentlyContinue }
try {
    $buildInfo = $buildInfoText | ConvertFrom-Json
    $seed = Read-BuildInfoSeed $buildInfoText "build-info.json of $tag"
} catch { throw "build-info.json of $tag cannot be read ($($_.Exception.Message)). Cut a new Preview. Nothing was changed." }
if ("$($buildInfo.app)" -ne $Version) { throw "build-info.json of $tag says it describes version '$($buildInfo.app)', not $Version. Cut a new Preview. Nothing was changed." }
if ("$($buildInfo.sdk)" -notmatch '^\d+\.\d+\.\d+') { throw "build-info.json of $tag names no SDK version (got '$($buildInfo.sdk)'). Cut a new Preview. Nothing was changed." }
Write-Host "  app $($buildInfo.app), SDK $($buildInfo.sdk), catalog seed v$($seed.Version) ($($seed.Short), $($seed.Channel))" -ForegroundColor Green

# --------------------------------------------------------------- 4. still current, as of now
$refusals = [System.Collections.Generic.List[string]]::new()

Write-Host "Step 4a: SDK $($buildInfo.sdk) includes everything on SDK develop" -ForegroundColor Cyan
pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-SdkPin.ps1') -RepoRoot $repoRoot -Pin $buildInfo.sdk
switch ($LASTEXITCODE) {
    0 { }
    3 {
        if ($AllowOlderSdk) { Write-Warning "Promoting WITHOUT the SDK commits listed above (-AllowOlderSdk)." }
        else { $refusals.Add("SDK $($buildInfo.sdk) is behind SDK develop (listed above). Cut a new Preview with a bumped SDK, or re-run with -AllowOlderSdk to promote without those commits on purpose.") }
    }
    default { $refusals.Add("The SDK version could not be checked (see above). No flag overrides that.") }
}

Write-Host "Step 4b: the bundled catalog seed is the latest stable catalog" -ForegroundColor Cyan
if (-not $CatalogReleases) { $CatalogReleases = $DefaultCatalogReleases }
if ($CatalogReleases.TrimEnd('/') -ne $DefaultCatalogReleases) {
    Write-Warning "Step 4b reads $CatalogReleases, not the real catalog releases (-CatalogReleases)."
}
$expect = "$($seed.Version) $($seed.Commit) $($seed.Sha256) $($seed.Channel) $($seed.GeneratedAt.ToString('o'))"
pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-CatalogSeed.ps1') -RepoRoot $repoRoot -ReleaseBase $CatalogReleases -Expect $expect
switch ($LASTEXITCODE) {
    0 { }
    3 {
        if ($AllowOlderCatalog) { Write-Warning "Promoting WITHOUT the latest stable catalog seed (-AllowOlderCatalog)." }
        else { $refusals.Add("The bundled catalog seed is an older stable catalog than the latest (listed above). Cut a new Preview after Update-CatalogSeed.ps1, or re-run with -AllowOlderCatalog to promote this seed on purpose.") }
    }
    4 { $refusals.Add("The bundled catalog seed is no stable catalog release (listed above). Cut a new Preview after Update-CatalogSeed.ps1. No flag overrides that.") }
    default { $refusals.Add("The bundled catalog seed could not be checked (see above). No flag overrides that.") }
}

if ($refusals.Count -gt 0) {
    throw "$tag was NOT promoted:`n  $($refusals -join "`n  ")`nNothing was changed: $tag is still a pre-release."
}

# ------------------------------------------------------------------------------- 5. promote
Write-Host "Step 5: promoting $tag to Stable" -ForegroundColor Cyan
$edit = Invoke-ReleaseGh @('release', 'edit', $tag, '--repo', $ghRepo, '--prerelease=false', '--latest')
if ($edit.ExitCode -ne 0) {
    # Say what the release is now, not what it probably is.
    $after = Get-ReleaseState
    $state = if ($null -eq $after) { "Its state could not be read back: check it on GitHub before retrying." }
             elseif ($after.isPrerelease) { "$tag is still a pre-release." }
             else { "$tag is no longer a pre-release all the same: check on GitHub that it is the latest release." }
    throw "gh release edit $tag failed (gh exit $($edit.ExitCode)):`n$($edit.Err)`n$state"
}
Write-Host "Promoted $tag to Stable: installs on Stable are offered it on their next update check." -ForegroundColor Green
