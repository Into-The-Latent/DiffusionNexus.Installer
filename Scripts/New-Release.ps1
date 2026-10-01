<#
.SYNOPSIS
    Builds, packages and publishes a release of the Easy Workload Installer by Into the Latent.

.DESCRIPTION
    Packaging happens in TWO steps, and the second one is not optional.

    `dotnet publish` runs electron-builder via ElectronNET's MSBuild targets, using
    Properties/electron-builder.local.json - a copy of the real config with the `publish`
    block removed. That block has to be absent there because electron-builder AUTO-PUBLISHES
    whenever a provider is configured, and would fail a plain local build with
    "GitHub Personal Access Token is not set".

    But electron-builder ALSO only emits `resources/app-update.yml` when a provider IS
    configured, and that file is how the installed app knows where its updates live. Build
    with the local config alone and you get an installer that runs perfectly and then dies
    with "ENOENT: app-update.yml" the moment anyone checks for updates.

    So step 2 re-runs electron-builder over the same output with the REAL config plus
    `--publish never`: provider present (so app-update.yml and latest.yml are generated),
    upload suppressed (so no token is needed). Assets are then uploaded with `gh`, which
    authenticates as the signed-in user rather than requiring a token in the build.

.PARAMETER Version
    Version to release, e.g. 3.0.5. Written to Directory.Build.props. Always a plain X.Y.Z,
    for a Preview build too - see -Prerelease for why a suffix is refused.

.PARAMETER Notes
    Release notes body.

.PARAMETER SkipUpload
    Build and package only; do not create the GitHub release.

.PARAMETER Prerelease
    Publish to the Preview channel: the GitHub release is created as a pre-release. Installs
    following Preview (the channel setting, or DIFFUSIONNEXUS_CATALOG_CHANNEL=preview) are
    offered it; installs on Stable are not, because they read GitHub's releases/latest, which
    never names a pre-release.

    The build is identical either way, and the version stays a plain X.Y.Z. That is deliberate:
    a suffixed version (3.1.0-beta.1) flips electron-updater into matching releases by that
    suffix and makes the installed app accept pre-releases whatever its channel setting says.

    To promote a Preview build to everyone, un-mark it - no rebuild, same binaries. Today that is
    the gh command below; Promote-Release.ps1 (issue #30) will check the SDK pin and the catalog
    seed against what is current first. Because promotion never rebuilds, a Preview build embeds
    the stable catalog seed too (Step 0d), never a preview one.
        gh release edit v3.0.9 --repo Into-The-Latent/DiffusionNexus.Installer --prerelease=false --latest

.PARAMETER AllowOlderSdk
    Release even though Scripts/Test-SdkPin.ps1 found commits on SDK develop that the pinned SDK
    version does not contain - a deliberate hold-back, such as a Stable hotfix while newer SDK work
    is meant for Preview only. The commits left out are still listed. It does not override a check
    that could not run (no SDK checkout, a failed fetch, pins that disagree).

.PARAMETER AllowOlderCatalog
    Release even though Scripts/Test-CatalogSeed.ps1 found the embedded catalog seed to be an older
    stable release rather than the latest - a deliberate hold-back of the seed, after
    Update-CatalogSeed.ps1 -Version N. What differs is still listed, and the release notes name the
    seed that ships. It does not override a seed that is no stable release at all (ahead of stable,
    another catalog under stable's number, a Preview manifest; exit 4), nor a check that could not
    run (no download, an uncommitted or corrupt seed; exit 2).

.PARAMETER CatalogReleases
    The catalog releases page Step 0d judges the seed against. Default: the real one,
    https://github.com/Into-The-Latent/DiffusionNexus.Catalog/releases. For the script tests,
    which serve a fixture. Only this parameter counts: DIFFUSIONNEXUS_CATALOG_RELEASES, which the
    standalone seed scripts honour, is ignored here, so a value left in the environment cannot
    steer a release. Any other value is announced before the check.

.EXAMPLE
    .\Scripts\New-Release.ps1 -Version 3.0.5 -Notes "Fixes the shortcut launch."

.EXAMPLE
    .\Scripts\New-Release.ps1 -Version 3.0.9 -Notes "For testers: new folders page." -Prerelease
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$Notes = "",
    [switch]$SkipUpload,
    [switch]$Prerelease,
    [switch]$AllowOlderSdk,
    [switch]$AllowOlderCatalog,
    [string]$CatalogReleases
)

$ErrorActionPreference = 'Stop'
# The gates below answer with exit codes (3 = behind, 2 = not checked) that this script reads. A
# profile that turns native exit codes into errors would throw on the first "no" before the
# -AllowOlderSdk decision is even reached; dotnet and gh are checked through $LASTEXITCODE anyway.
$PSNativeCommandUseErrorActionPreference = $false
# A release is cut from the checkout this script lives in, and from nowhere else: the notices check
# reads this checkout's files, and a parameter for another root would let Step 1 modify a tree that
# Step 0 never checked. (Scripts/Tests/New-Release.Tests.ps1 copies the scripts into its fixture.)
$repoRoot = Split-Path $PSScriptRoot -Parent
$project  = Join-Path $repoRoot 'DiffusionNexus.Installer.Electron'
$publish  = Join-Path $project  'bin\Release\net10.0\win-x64\publish'
$ghRepo   = 'Into-The-Latent/DiffusionNexus.Installer'

# Keep this in step with ElectronVersion in the .csproj; electron-builder is invoked
# directly below and does not read that property.
$electronVersion = '42.4.1'

# ---------------------------------------------------------------------------------------- Step 0
# Everything here runs BEFORE this script changes anything on disk. A refusal leaves the working
# tree exactly as it was: no version write, no cleared publish folder.

# Step 0a. A release must be built from the PUBLISHED SDK packages, never from a local checkout.
# Directory.Build.targets auto-enables UseLocalSDK whenever E:\Repos\DiffusionNexus.Installer.SDK
# exists - and it exists on every dev machine here - so without this pin a release silently embeds
# whatever branch the SDK repo happens to have checked out, and cannot be reproduced from a clean
# clone. Pinned explicitly rather than left to the environment, because a User-scope
# UseLocalSDK=true survives shells and would otherwise win. The token is what that restore needs.
Write-Host "Step 0a: the packages token is present" -ForegroundColor Cyan
if (-not $env:GITHUB_PACKAGES_TOKEN) {
    throw "GITHUB_PACKAGES_TOKEN is not set. The release build restores the SDK from GitHub Packages (see nuget.config) and would fail the restore. Nothing was built or changed."
}

# Step 0b. The upload at the very end needs write access to the repo, and the everyday gh account
# may only have read access - v3.0.10's first upload failed that way, after the whole build. Find a
# signed-in account that can publish now; only the upload gets its token, the active account stays.
. (Join-Path $PSScriptRoot 'ReleaseAccount.ps1')
if (-not $SkipUpload) {
    Write-Host "Step 0b: a signed-in gh account can publish to $ghRepo" -ForegroundColor Cyan
    $releaseToken = Resolve-ReleaseToken $ghRepo
    if (-not $releaseToken) { throw "$(Get-NoReleaseAccountMessage $ghRepo) Nothing was built or changed." }
}

# Step 0c. A release must not leave out SDK work that is already on the SDK's develop branch:
# v3.0.9 shipped SDK 2.0.0-preview.8 the day after preview.9 (the Manager-aware Update-ComfyUI.bat)
# was published, because nothing compared the pin with the SDK.
Write-Host "Step 0c: the SDK pin includes everything on SDK develop" -ForegroundColor Cyan
pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-SdkPin.ps1') -RepoRoot $repoRoot
switch ($LASTEXITCODE) {
    0 { }
    3 {
        if (-not $AllowOlderSdk) {
            throw "The SDK pin is behind SDK develop (listed above). Bump it, or re-run with -AllowOlderSdk to release without those commits on purpose. Nothing was built or changed."
        }
        Write-Warning "Releasing WITHOUT the SDK commits listed above (-AllowOlderSdk)."
    }
    default { throw "The SDK pin could not be checked (see above). Nothing was built or changed." }
}

# Step 0d. The embedded catalog seed must be the latest stable catalog: a seed ahead of it ships
# content the stable channel does not serve, one behind it ships stale content to every fresh
# machine, and until now only a manual commit before each release kept it current. A Preview build
# embeds stable too: promotion never rebuilds. The fix is Scripts/Update-CatalogSeed.ps1 and a
# commit; this script never writes the seed.
Write-Host "Step 0d: the embedded catalog seed is the latest stable catalog" -ForegroundColor Cyan
# The releases page is passed explicitly, so the check never falls back to
# DIFFUSIONNEXUS_CATALOG_RELEASES: a value left in the environment must not steer a release.
. (Join-Path $PSScriptRoot 'CatalogRelease.ps1')
if (-not $CatalogReleases) { $CatalogReleases = $DefaultCatalogReleases }
if ($CatalogReleases.TrimEnd('/') -ne $DefaultCatalogReleases) {
    Write-Warning "Step 0d reads $CatalogReleases, not the real catalog releases (-CatalogReleases)."
}
pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-CatalogSeed.ps1') -RepoRoot $repoRoot -ReleaseBase $CatalogReleases
switch ($LASTEXITCODE) {
    0 { }
    3 {
        if (-not $AllowOlderCatalog) {
            throw "The embedded catalog seed is not the latest stable catalog (listed above). Run pwsh Scripts/Update-CatalogSeed.ps1 and commit, or re-run with -AllowOlderCatalog to ship this seed on purpose. Nothing was built or changed."
        }
        Write-Warning "Releasing WITHOUT the latest stable catalog seed (-AllowOlderCatalog)."
    }
    4 { throw "The embedded catalog seed is not a stable catalog release -AllowOlderCatalog may ship (listed above). Run pwsh Scripts/Update-CatalogSeed.ps1 and commit. Nothing was built or changed." }
    default { throw "The embedded catalog seed could not be checked (see above). Nothing was built or changed." }
}
# What Step 0d judged, for Step 1c: the packaged app must report exactly this seed.
$seed = Read-CatalogManifest (Get-Content -LiteralPath (Join-Path $repoRoot $CatalogSeedFolder 'manifest.json') -Raw) 'the embedded seed manifest'

Write-Host "Setting version to $Version" -ForegroundColor Cyan
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$props = Get-Content -LiteralPath $propsPath -Raw
$props = $props -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$Version</Version>"
Set-Content -LiteralPath $propsPath -Value $props -NoNewline

# Publish copies a file only when the source is NEWER than the copy already in the publish folder.
# A package DLL keeps its older packed timestamp, so SDK DLLs left over from an earlier local-SDK
# publish look newer and survive into the installer. v3.0.8 shipped that way. Start from an empty folder.
if (Test-Path -LiteralPath $publish) {
    Write-Host "Clearing the previous publish output" -ForegroundColor Cyan
    Remove-Item -LiteralPath $publish -Recurse -Force
}

Write-Host "Step 1/3: dotnet publish (SDK from NuGet, not the local checkout)" -ForegroundColor Cyan
dotnet publish (Join-Path $project 'DiffusionNexus.Installer.Electron.csproj') -c Release --nologo -p:UseLocalSDK=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Check the packaged SDK DLLs directly, because a clean publish is not proof of what was packaged.
# Each one must be byte-identical to the DLL inside the NuGet package the csproj pins, which the
# restore above has just put in the global packages folder. The version string is NOT enough: a
# local project build of the SDK repo stamps "<Version>+<sha>" from ITS Directory.Build.props, and
# the moment the pin and that props value agree (2.0.0 vs 2.0.0-preview.N today; identical on the
# next stable pin) a leaked local DLL and the package DLL look the same by version.
$globalPackages = ((dotnet nuget locals global-packages -l) -replace '^global-packages:\s*', '').Trim()
if (-not (Test-Path -LiteralPath $globalPackages)) { throw "Could not resolve the NuGet global packages folder (got '$globalPackages')." }
$csproj = [xml](Get-Content -LiteralPath (Join-Path $project 'DiffusionNexus.Installer.Electron.csproj') -Raw)
$sdkRefs = @($csproj.Project.ItemGroup.PackageReference | Where-Object { $_.Include -like 'DiffusionNexus.Installer.SDK.*' })
if ($sdkRefs.Count -eq 0) { throw "No SDK PackageReferences found in the Electron csproj." }
# One version: Step 0c already refused pins that disagree, and that refusal cannot be overridden.
$sdkPin = $sdkRefs[0].Version
foreach ($ref in $sdkRefs) {
    $dll = Join-Path $publish "bin\$($ref.Include).dll"
    if (-not (Test-Path -LiteralPath $dll)) { throw "Packaged SDK assembly missing: $dll" }
    $packaged = Join-Path $globalPackages "$($ref.Include.ToLowerInvariant())\$($ref.Version)\lib\net10.0\$($ref.Include).dll"
    if (-not (Test-Path -LiteralPath $packaged)) { throw "Restored package assembly missing: $packaged. The restore did not come from the pinned package $($ref.Include) $($ref.Version)." }
    $actualHash   = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
    $expectedHash = (Get-FileHash -LiteralPath $packaged -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) {
        $actualVersion   = (Get-Item -LiteralPath $dll).VersionInfo.ProductVersion
        $expectedVersion = (Get-Item -LiteralPath $packaged).VersionInfo.ProductVersion
        throw "$($ref.Include) in the publish output ('$actualVersion') is not the DLL from package $($ref.Version) ('$expectedVersion'). The local SDK leaked into the release."
    }
}
Write-Host "  SDK assemblies are byte-identical to the pinned packages" -ForegroundColor Green

# The publish above is the first moment the packaged npm tree exists, so this is where the
# notices can be checked against what actually ships. Drift means a dependency changed and the
# committed notices were not regenerated: fix that and commit before releasing.
Write-Host "Step 1b: third-party notices match the packaged app" -ForegroundColor Cyan
pwsh (Join-Path $PSScriptRoot 'Generate-ThirdPartyNotices.ps1') -Check -RefreshNpm
if ($LASTEXITCODE -ne 0) { throw "THIRD-PARTY-NOTICES.txt is stale. Run pwsh Scripts/Generate-ThirdPartyNotices.ps1 -RefreshNpm (this publish output is what it rescans), commit, and release again." }

# Step 1c: build-info.json. The PACKAGED app is asked what it contains, so the asset is what the
# binary says, not what this script assumed - and the answer has to agree with what Step 0
# checked, or this is a build that does not contain what was checked. Promote-Release.ps1 and the
# catalog repo's schema gate read this asset; nothing reads the release notes back.
Write-Host "Step 1c: build-info.json from the packaged app" -ForegroundColor Cyan
$entryPoint = Join-Path $publish 'bin\DiffusionNexus.Installer.Electron.exe'
if (-not (Test-Path -LiteralPath $entryPoint)) { throw "Packaged entry point missing: $entryPoint" }
$buildInfoText = (& $entryPoint --build-info | ForEach-Object { "$_" }) -join "`n"
if ($LASTEXITCODE -ne 0) { throw "The packaged app did not answer --build-info (exit $LASTEXITCODE):`n$buildInfoText" }
try { $buildInfo = $buildInfoText | ConvertFrom-Json }
catch { throw "The packaged app's --build-info answer is not JSON:`n$buildInfoText" }
if ($buildInfo.app -ne $Version) { throw "The packaged app says it is version '$($buildInfo.app)', not $Version." }
if ($buildInfo.sdk -ne $sdkPin) { throw "The packaged app says it was built with SDK $($buildInfo.sdk); the projects pin $sdkPin. This build does not contain what was checked." }
$packagedSeed = $buildInfo.catalogSeed
if (-not $packagedSeed) { throw "The packaged app reports no catalogSeed. This build is not what the release script expects." }
if ("$($packagedSeed.version)" -ne "$($seed.Version)" -or "$($packagedSeed.commit)".ToLowerInvariant() -ne $seed.Commit -or "$($packagedSeed.sha256)".ToLowerInvariant() -ne $seed.Sha256) {
    throw "The packaged app says it bundles catalog v$($packagedSeed.version) ($($packagedSeed.commit)); Step 0d checked v$($seed.Version) ($($seed.Commit)). This build does not contain what was checked."
}
# builtAt is null only when the app cannot date its own assembly file (a single-file publish). The
# shipped app is never that, and promotion reads this asset as fact, so a null here is a broken build.
if (-not $buildInfo.builtAt) { throw "The packaged app reports no builtAt: it could not find its own assembly file to date. This build is not what the release script expects." }
$buildInfoPath = Join-Path $publish 'build-info.json'
Set-Content -LiteralPath $buildInfoPath -Value $buildInfoText -Encoding utf8 -NoNewline
Write-Host "  app $($buildInfo.app), SDK $($buildInfo.sdk), catalog schema $($buildInfo.catalogSchema), catalog seed v$($seed.Version) ($($seed.Short))" -ForegroundColor Green

Write-Host "Step 2/3: repackaging with the publish config (emits app-update.yml)" -ForegroundColor Cyan

# The settings below are written INTO a generated config rather than passed as electron-builder's
# `-c.some.path value` overrides, because those overrides do not survive PowerShell.
#
# ElectronNET's MSBuild target passes them that way and it works - but MSBuild's Exec runs the
# command through cmd.exe. Run the identical argument list from PowerShell and yargs fails to bind
# any of the pairs, reporting every VALUE as an unknown positional argument; switch them to `=`
# form and it is worse but quieter, as `-c.directories.app=app` binds to `-c` (the alias for
# --config) and silently replaces the config path, so the build dies looking for a file called
# `.directories.app=app`. A generated config has no such ambiguity and behaves the same in
# every shell.
$builderConfig = Get-Content -LiteralPath (Join-Path $project 'Properties\electron-builder.json') -Raw |
    ConvertFrom-Json -AsHashtable
$builderConfig.electronVersion = $electronVersion
$builderConfig.appId           = 'diffusion-nexus-installer'
$builderConfig.buildVersion    = $Version
$builderConfig.copyright       = "Copyright $([char]0x00A9) Into The Latent"
$builderConfig.extraResources  = 'bin/**/*'
# app.asar is built from the 'app' subfolder only, keeping the .NET output under bin/ outside it.
$builderConfig.directories     = @{ app = 'app'; output = $publish }

$generatedConfig = Join-Path $publish 'electron-builder.publish.json'
$builderConfig | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $generatedConfig -Encoding utf8

Push-Location -LiteralPath $publish
try {
    npx electron-builder --config=./electron-builder.publish.json --publish never
    if ($LASTEXITCODE -ne 0) { throw "electron-builder failed" }
} finally { Pop-Location }

# Fail loudly rather than shipping an installer that cannot ever update itself.
$appUpdate = Join-Path $publish 'win-unpacked\resources\app-update.yml'
if (-not (Test-Path -LiteralPath $appUpdate)) {
    throw "app-update.yml was not generated - the packaged app would not be able to update. Aborting."
}
Write-Host "  app-update.yml present" -ForegroundColor Green

if ($SkipUpload) { Write-Host "SkipUpload set - done." -ForegroundColor Yellow; return }

$channelName = if ($Prerelease) { 'Preview (GitHub pre-release)' } else { 'Stable (full release)' }
Write-Host "Step 3/3: publishing v$Version to $ghRepo on $channelName" -ForegroundColor Cyan
$setup = Join-Path $publish "EasyWorkloadInstaller-ITL-Setup-$Version.exe"
foreach ($f in @($setup, "$setup.blockmap", (Join-Path $publish 'latest.yml'), $buildInfoPath)) {
    if (-not (Test-Path -LiteralPath $f)) { throw "Expected artifact missing: $f" }
}
# The notes end with what the build contains, generated from the same data as build-info.json.
# Human lines only; nothing reads them back.
$buildLines = "Built with Installer SDK $($buildInfo.sdk)`nBundled catalog v$($seed.Version) ($($seed.Channel.ToLowerInvariant()))"
$notesWithBuild = if ($Notes.Trim()) { "$($Notes.TrimEnd())`n`n$buildLines" } else { $buildLines }
# latest.yml for both channels: electron-updater reads it for any tag without a suffix, even
# with allowPrerelease set, so a Preview build needs no separately named channel file and a
# promoted one is already complete.
$ghArgs = @('release', 'create', "v$Version", $setup, "$setup.blockmap", (Join-Path $publish 'latest.yml'), $buildInfoPath,
            '--repo', $ghRepo, '--title', $Version, '--notes', $notesWithBuild)
if ($Prerelease) { $ghArgs += '--prerelease' }
# Under the token Step 0b resolved: the active gh account may be read-only here.
Invoke-WithGhToken $releaseToken { gh @ghArgs }
if ($LASTEXITCODE -ne 0) { throw "gh release create failed (see gh's output above). Check whether v$Version exists on $ghRepo before retrying: gh may have created it and then failed on an asset." }

Write-Host "Released v$Version on $channelName" -ForegroundColor Green
if ($Prerelease) {
    Write-Host "Promote it to Stable, once the pin and the seed are still current: gh release edit v$Version --repo $ghRepo --prerelease=false --latest (Promote-Release.ps1 will do these checks; issue #30)" -ForegroundColor Yellow
}
