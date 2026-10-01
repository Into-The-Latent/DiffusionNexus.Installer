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
      2. The release vX and GitHub's latest release. vX must exist, be published (not a draft) and
         carry every asset New-Release.ps1 uploads, fully uploaded: Stable installs read latest.yml
         and the installer from whatever release is latest. Already the latest -> nothing to
         promote. Below the latest -> refused (--latest would move Stable backwards), or nothing to
         promote when vX is an older Stable release anyway. Un-marked but not latest (a promotion
         that failed half way, or a hand edit) -> promoted again, so a re-run repairs it.
      3. Its build-info.json: the SDK version and packages and the catalog seed the build carries.
      4. Test-SdkPin.ps1 -Pin <sdk> -Packages <sdkPackages> against SDK develop and
         Test-CatalogSeed.ps1 -Expect <seed> against the latest stable catalog. Both always run, so
         one refusal lists everything. Behind (exit 3) -> refused, unless -AllowOlderSdk /
         -AllowOlderCatalog. Not checked (exit 2), and a seed that is no stable catalog release at
         all (exit 4) -> refused, no flag.
      5. Step 2 again, because the checks take a while and another promotion may have landed
         meanwhile; then gh release edit vX --prerelease=false --latest, and a read-back that vX is
         now Stable and the latest release.

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
# What New-Release.ps1 uploads. electron-updater on Stable reads latest.yml from GitHub's latest
# release, then the installer and its blockmap; promotion reads build-info.json.
$requiredAssets = @("EasyWorkloadInstaller-ITL-Setup-$Version.exe", "EasyWorkloadInstaller-ITL-Setup-$Version.exe.blockmap", 'latest.yml', 'build-info.json')
. (Join-Path $PSScriptRoot 'ReleaseAccount.ps1')
. (Join-Path $PSScriptRoot 'CatalogRelease.ps1')

# One gh call under the release token: its stdout (joined) and its stderr, for the message when it fails.
function Invoke-ReleaseGh([string[]]$GhArgs) {
    $run = Invoke-Native { Invoke-WithGhToken $releaseToken { gh @GhArgs 2>&1 } }
    [pscustomobject]@{ ExitCode = $run.ExitCode; Out = $run.Out -join "`n"; Err = $run.Err -join "`n" }
}

# What GitHub says now: vX (its flags and assets) and the tag of the latest release, $null when the
# repo has no full release yet (404: nothing to go backwards from). Throws when either cannot be read.
function Get-ReleaseFacts {
    $view = Invoke-ReleaseGh @('release', 'view', $tag, '--repo', $ghRepo, '--json', 'isPrerelease,isDraft,assets')
    if ($view.ExitCode -ne 0) { throw "Could not read release $tag on $ghRepo (gh exit $($view.ExitCode)):`n$($view.Err)" }
    try { $release = $view.Out | ConvertFrom-Json } catch { throw "gh release view $tag answered something that is not JSON:`n$($view.Out)" }
    $latest = Invoke-ReleaseGh @('api', "repos/$ghRepo/releases/latest", '--jq', '.tag_name')
    if ($latest.ExitCode -eq 0) { $latestTag = $latest.Out.Trim() }
    elseif ($latest.Err -match 'HTTP 404') { $latestTag = $null }
    else { throw "Could not read the latest release of $ghRepo (gh exit $($latest.ExitCode)):`n$($latest.Err)" }
    [pscustomobject]@{ Release = $release; LatestTag = $latestTag }
}

# What promoting vX means, given those facts. Prints nothing, so each step words it for itself:
#   Promote  $true, or $false when there is nothing to do
#   Reason   why there is nothing to do ("vX is ..."), when Promote is $false
#   Unmarked vX is no pre-release any more but not GitHub's latest release (a half-done promotion, a
#            hand edit): promoting finishes it, and a refusal must not call it a pre-release
#   Latest   the latest tag, for messages
# Throws when the promotion must not happen.
function Get-PromotionVerdict($Facts) {
    $release = $Facts.Release
    $latestTag = $Facts.LatestTag
    $verdict = [pscustomobject]@{ Promote = $false; Reason = $null; Unmarked = $false; Latest = $latestTag }
    if ($release.isDraft) { throw "$tag is a draft, not a published Preview release: testers never ran it." }
    if ($latestTag -eq $tag) {
        $verdict.Reason = "$tag is already GitHub's latest Stable release"
        return $verdict
    }
    if ($null -ne $latestTag) {
        # A guard against moving Stable backwards compares, or refuses: a tag it cannot read is no "go".
        $latestVersion = $null
        if (-not ($latestTag -match '^v(\d+\.\d+\.\d+)$' -and [version]::TryParse($Matches[1], [ref]$latestVersion))) {
            throw "GitHub's latest release of $ghRepo is '$latestTag', which is no vX.Y.Z tag, so $tag cannot be checked against it: promoting could move Stable backwards. Make a vX.Y.Z release the latest by hand first."
        }
        if ($latestVersion -gt [version]$Version) {
            if (-not $release.isPrerelease) {
                $verdict.Reason = "$tag is an older Stable release; $latestTag is the latest"
                return $verdict
            }
            throw "$latestTag is already Stable and newer than $tag. Promoting $tag would make GitHub's latest release go backwards."
        }
    }
    $verdict.Unmarked = -not $release.isPrerelease
    $missing = @(foreach ($name in $requiredAssets) {
        $asset = @($release.assets | Where-Object name -eq $name) | Select-Object -First 1
        if (-not $asset) { "$name (missing)" }
        elseif ($asset.state -ne 'uploaded' -or [long]$asset.size -le 0) { "$name (upload not finished: state '$($asset.state)', $($asset.size) bytes)" }
    })
    if ($missing.Count -gt 0) {
        throw "$tag does not carry every release asset:`n  $($missing -join "`n  ")`nAs the latest release it would send every Stable install to a file that is not there. Cut a new Preview with New-Release.ps1 -Prerelease (a release made before build-info.json existed has none either)."
    }
    $verdict.Promote = $true
    $verdict
}

# After a successful edit releases/latest may trail it for a moment: read until GitHub shows vX as
# Stable and latest, a few times a second apart, before calling the edit not done. $null = unreadable.
function Read-AfterEdit([bool]$EditSucceeded) {
    $tries = if ($EditSucceeded) { 5 } else { 1 }
    for ($try = 1; $try -le $tries; $try++) {
        if ($try -gt 1) { Start-Sleep -Seconds 1 }
        try { $facts = Get-ReleaseFacts } catch { $facts = $null; continue }
        if (-not $facts.Release.isPrerelease -and $facts.LatestTag -eq $tag) { break }
    }
    $facts
}

# ------------------------------------------------------------------------------- 1. the account
Write-Host "Step 1: a signed-in gh account can edit releases on $ghRepo" -ForegroundColor Cyan
$releaseToken = Resolve-ReleaseToken $ghRepo
if (-not $releaseToken) { throw "$(Get-NoReleaseAccountMessage $ghRepo) Nothing was read or changed." }

# ------------------------------------------------------------------------------- 2. the release
Write-Host "Step 2: release $tag can become GitHub's latest release" -ForegroundColor Cyan
try { $verdict = Get-PromotionVerdict (Get-ReleaseFacts) } catch { throw "$($_.Exception.Message)`nNothing was changed." }
if (-not $verdict.Promote) {
    Write-Host "$($verdict.Reason): nothing to promote." -ForegroundColor Green
    return
}
if ($verdict.Unmarked) {
    Write-Warning "$tag is no longer a pre-release but is not GitHub's latest release ($(if ($verdict.Latest) { "that is $($verdict.Latest)" } else { 'there is none' })), so Stable installs are not offered it. Promoting it again makes it the latest."
}
# What a refusal leaves behind, as Step 2 found it.
$unchanged = if ($verdict.Unmarked) { "$tag stays un-marked but not GitHub's latest release: Stable installs are not offered it. Mark it a pre-release again, or promote a newer build." }
             else { "$tag is still a pre-release." }

# ------------------------------------------------------------------------- 3. what the build is
Write-Host "Step 3: build-info.json of $tag" -ForegroundColor Cyan
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
    $sdkPackages = Read-BuildInfoSdkPackages $buildInfoText "build-info.json of $tag"
} catch { throw "build-info.json of $tag cannot be read ($($_.Exception.Message)). Cut a new Preview. Nothing was changed." }
if ("$($buildInfo.app)" -ne $Version) { throw "build-info.json of $tag says it describes version '$($buildInfo.app)', not $Version. Cut a new Preview. Nothing was changed." }
if ("$($buildInfo.sdk)" -notmatch '^\d+\.\d+\.\d+') { throw "build-info.json of $tag names no SDK version (got '$($buildInfo.sdk)'). Cut a new Preview. Nothing was changed." }
Write-Host "  app $($buildInfo.app), SDK $($buildInfo.sdk) ($($sdkPackages.Count) packages), catalog seed v$($seed.Version) ($($seed.Short), $($seed.Channel))" -ForegroundColor Green

# --------------------------------------------------------------- 4. still current, as of now
$refusals = [System.Collections.Generic.List[string]]::new()

# The packages the build ships decide which SDK commits count, not the ones this checkout pins:
# promotion may run from any branch.
Write-Host "Step 4a: SDK $($buildInfo.sdk) includes everything on SDK develop" -ForegroundColor Cyan
pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-SdkPin.ps1') -RepoRoot $repoRoot -Pin $buildInfo.sdk -Packages ($sdkPackages -join ',')
switch ($LASTEXITCODE) {
    0 { }
    3 {
        if ($AllowOlderSdk) { Write-Warning "Promoting WITHOUT the SDK commits listed above (-AllowOlderSdk)." }
        else { $refusals.Add("SDK $($buildInfo.sdk) is behind SDK develop (listed above). Cut a new Preview with a bumped SDK, or re-run with -AllowOlderSdk to promote without those commits on purpose.") }
    }
    default { $refusals.Add("The SDK version could not be checked (see above). No flag overrides that.") }
}

Write-Host "Step 4b: the bundled catalog seed is the latest stable catalog" -ForegroundColor Cyan
$CatalogReleases = Resolve-GateCatalogReleases $CatalogReleases 'Step 4b'
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
    throw "$tag was NOT promoted:`n  $($refusals -join "`n  ")`nNothing was changed: $unchanged"
}

# ------------------------------------------------------------------------------- 5. promote
# The checks took a while: judge what GitHub says now, not what Step 2 read.
Write-Host "Step 5: promoting $tag to Stable" -ForegroundColor Cyan
try { $verdict = Get-PromotionVerdict (Get-ReleaseFacts) } catch { throw "While the checks ran, GitHub changed: $($_.Exception.Message)`nNothing was changed." }
if (-not $verdict.Promote) {
    Write-Host "While the checks ran, $($verdict.Reason -replace '^(\S+) is already', '$1 became'): nothing left to promote." -ForegroundColor Green
    return
}
$edit = Invoke-ReleaseGh @('release', 'edit', $tag, '--repo', $ghRepo, '--prerelease=false', '--latest')
# Read back either way, and say what the release is now, not what it probably is. What GitHub shows
# decides the outcome, not gh's exit code.
$after = Read-AfterEdit ($edit.ExitCode -eq 0)
$state = if ($null -eq $after) { "Its state could not be read back: check it on GitHub, then re-run this script, which finishes a half-done promotion." }
         elseif ($after.Release.isPrerelease) { "$tag is still a pre-release." }
         elseif ($after.LatestTag -ne $tag) { "$tag is no longer a pre-release, but GitHub's latest release is $(if ($after.LatestTag) { $after.LatestTag } else { 'none' }), so Stable installs are not offered it. Re-run this script: it makes $tag the latest." }
         else { $null }
if ($edit.ExitCode -ne 0) {
    if ($state) { throw "gh release edit $tag failed (gh exit $($edit.ExitCode)):`n$($edit.Err)`n$state" }
    Write-Warning "gh release edit $tag failed (gh exit $($edit.ExitCode)): $($edit.Err) GitHub shows $tag as Stable and the latest release all the same."
} elseif ($state) { throw "gh release edit $tag reported success, but: $state" }
Write-Host "Promoted $tag to Stable: installs on Stable are offered it on their next update check." -ForegroundColor Green
