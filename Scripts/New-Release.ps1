<#
.SYNOPSIS
    Builds, packages and publishes a release of the DiffusionNexus Installer.

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
    Version to release, e.g. 3.0.5. Written to Directory.Build.props.

.PARAMETER Notes
    Release notes body.

.PARAMETER SkipUpload
    Build and package only; do not create the GitHub release.

.EXAMPLE
    .\Scripts\New-Release.ps1 -Version 3.0.5 -Notes "Fixes the shortcut launch."
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$Notes = "",
    [switch]$SkipUpload
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

Write-Host "Step 1/3: dotnet publish (SDK from NuGet, not the local checkout)" -ForegroundColor Cyan
dotnet publish (Join-Path $project 'DiffusionNexus.Installer.Electron.csproj') -c Release --nologo -p:UseLocalSDK=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

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

Write-Host "Step 3/3: publishing v$Version to $ghRepo" -ForegroundColor Cyan
$setup = Join-Path $publish "DiffusionNexus-Setup-$Version.exe"
foreach ($f in @($setup, "$setup.blockmap", (Join-Path $publish 'latest.yml'))) {
    if (-not (Test-Path $f)) { throw "Expected artifact missing: $f" }
}
gh release create "v$Version" $setup "$setup.blockmap" (Join-Path $publish 'latest.yml') `
    --repo $ghRepo --title $Version --notes $Notes
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }

Write-Host "Released v$Version" -ForegroundColor Green
