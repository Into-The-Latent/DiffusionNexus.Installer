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

    To promote a Preview build to everyone, un-mark it - no rebuild, same binaries:
        gh release edit v3.0.9 --repo Into-The-Latent/DiffusionNexus.Installer --prerelease=false --latest

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
    [switch]$Prerelease
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$project  = Join-Path $repoRoot 'DiffusionNexus.Installer.Electron'
$publish  = Join-Path $project  'bin\Release\net10.0\win-x64\publish'
$ghRepo   = 'Into-The-Latent/DiffusionNexus.Installer'

# Keep this in step with ElectronVersion in the .csproj; electron-builder is invoked
# directly below and does not read that property.
$electronVersion = '42.4.1'

Write-Host "Setting version to $Version" -ForegroundColor Cyan
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$props = Get-Content $propsPath -Raw
$props = $props -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$Version</Version>"
Set-Content $propsPath $props -NoNewline

# A release must be built from the PUBLISHED SDK packages, never from a local checkout.
# Directory.Build.targets auto-enables UseLocalSDK whenever E:\Repos\DiffusionNexus.Installer.SDK
# exists - and it exists on every dev machine here - so without this pin a release silently embeds
# whatever branch the SDK repo happens to have checked out, and cannot be reproduced from a clean
# clone. Pinned explicitly rather than left to the environment, because a User-scope
# UseLocalSDK=true survives shells and would otherwise win.
if (-not $env:GITHUB_PACKAGES_TOKEN) {
    throw "GITHUB_PACKAGES_TOKEN is not set. The release build restores the SDK from GitHub Packages (see nuget.config) and would fail the restore."
}

# Publish copies a file only when the source is NEWER than the copy already in the publish folder.
# A package DLL keeps its older packed timestamp, so SDK DLLs left over from an earlier local-SDK
# publish look newer and survive into the installer. v3.0.8 shipped that way. Start from an empty folder.
if (Test-Path $publish) {
    Write-Host "Clearing the previous publish output" -ForegroundColor Cyan
    Remove-Item $publish -Recurse -Force
}

Write-Host "Step 1/3: dotnet publish (SDK from NuGet, not the local checkout)" -ForegroundColor Cyan
dotnet publish (Join-Path $project 'DiffusionNexus.Installer.Electron.csproj') -c Release --nologo -p:UseLocalSDK=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Check the packaged SDK DLLs directly, because a clean publish is not proof of what was packaged.
# Each one must carry the version the csproj pins. A local project build reports a bare "2.0.0+<sha>".
$csproj = [xml](Get-Content (Join-Path $project 'DiffusionNexus.Installer.Electron.csproj') -Raw)
$sdkRefs = @($csproj.Project.ItemGroup.PackageReference | Where-Object { $_.Include -like 'DiffusionNexus.Installer.SDK.*' })
if ($sdkRefs.Count -eq 0) { throw "No SDK PackageReferences found in the Electron csproj." }
foreach ($ref in $sdkRefs) {
    $dll = Join-Path $publish "bin\$($ref.Include).dll"
    if (-not (Test-Path $dll)) { throw "Packaged SDK assembly missing: $dll" }
    $actual = (Get-Item $dll).VersionInfo.ProductVersion
    if ($actual -notlike "$($ref.Version)+*" -and $actual -ne $ref.Version) {
        throw "$($ref.Include) in the publish output is '$actual', expected package $($ref.Version). The local SDK leaked into the release."
    }
}
Write-Host "  SDK assemblies match the pinned packages" -ForegroundColor Green

# The publish above is the first moment the packaged npm tree exists, so this is where the
# notices can be checked against what actually ships. Drift means a dependency changed and the
# committed notices were not regenerated: fix that and commit before releasing.
Write-Host "Step 1b: third-party notices match the packaged app" -ForegroundColor Cyan
pwsh (Join-Path $repoRoot 'Scripts\Generate-ThirdPartyNotices.ps1') -Check -RefreshNpm
if ($LASTEXITCODE -ne 0) { throw "THIRD-PARTY-NOTICES.txt is stale. Run Scripts/Generate-ThirdPartyNotices.ps1, commit, and release again." }

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
$builderConfig = Get-Content (Join-Path $project 'Properties\electron-builder.json') -Raw |
    ConvertFrom-Json -AsHashtable
$builderConfig.electronVersion = $electronVersion
$builderConfig.appId           = 'diffusion-nexus-installer'
$builderConfig.buildVersion    = $Version
$builderConfig.copyright       = "Copyright $([char]0x00A9) Into The Latent"
$builderConfig.extraResources  = 'bin/**/*'
# app.asar is built from the 'app' subfolder only, keeping the .NET output under bin/ outside it.
$builderConfig.directories     = @{ app = 'app'; output = $publish }

$generatedConfig = Join-Path $publish 'electron-builder.publish.json'
$builderConfig | ConvertTo-Json -Depth 10 | Set-Content $generatedConfig -Encoding utf8

Push-Location $publish
try {
    npx electron-builder --config=./electron-builder.publish.json --publish never
    if ($LASTEXITCODE -ne 0) { throw "electron-builder failed" }
} finally { Pop-Location }

# Fail loudly rather than shipping an installer that cannot ever update itself.
$appUpdate = Join-Path $publish 'win-unpacked\resources\app-update.yml'
if (-not (Test-Path $appUpdate)) {
    throw "app-update.yml was not generated - the packaged app would not be able to update. Aborting."
}
Write-Host "  app-update.yml present" -ForegroundColor Green

if ($SkipUpload) { Write-Host "SkipUpload set - done." -ForegroundColor Yellow; return }

$channelName = if ($Prerelease) { 'Preview (GitHub pre-release)' } else { 'Stable (full release)' }
Write-Host "Step 3/3: publishing v$Version to $ghRepo on $channelName" -ForegroundColor Cyan
$setup = Join-Path $publish "EasyWorkloadInstaller-ITL-Setup-$Version.exe"
foreach ($f in @($setup, "$setup.blockmap", (Join-Path $publish 'latest.yml'))) {
    if (-not (Test-Path $f)) { throw "Expected artifact missing: $f" }
}
# latest.yml for both channels: electron-updater reads it for any tag without a suffix, even
# with allowPrerelease set, so a Preview build needs no separately named channel file and a
# promoted one is already complete.
$ghArgs = @('release', 'create', "v$Version", $setup, "$setup.blockmap", (Join-Path $publish 'latest.yml'),
            '--repo', $ghRepo, '--title', $Version, '--notes', $Notes)
if ($Prerelease) { $ghArgs += '--prerelease' }
gh @ghArgs
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }

Write-Host "Released v$Version on $channelName" -ForegroundColor Green
if ($Prerelease) {
    Write-Host "Promote it to Stable with: gh release edit v$Version --repo $ghRepo --prerelease=false --latest" -ForegroundColor Yellow
}
