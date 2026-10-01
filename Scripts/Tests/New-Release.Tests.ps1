#Requires -Version 7.2
# Tests for the Step 0 gates of Scripts/New-Release.ps1. Run: pwsh -NoProfile -File Scripts/Tests/New-Release.Tests.ps1
# Exit code = number of failed cases. CI runs every Scripts/Tests/*.Tests.ps1.
#
# Only the gates are under test. Scripts/*.ps1 are copied into the fixture repo's own Scripts folder
# and run from there, so the script under test takes the fixture for its checkout exactly as a
# release takes the real one (there is no parameter for another root: a release is cut from the
# checkout the script lives in). The SDK pin check finds a fixture SDK through LocalSDKPath, the
# seed check reads the fixture releases through -CatalogReleases (never the environment: a leftover
# DIFFUSIONNEXUS_CATALOG_RELEASES must not steer a release), and -SkipUpload keeps gh out of it. A run that gets past the gates fails at dotnet publish, because the
# fixture has no project. That failure is the proof it got there: the version was written and
# "dotnet publish failed" is on the output. (So these tests need dotnet on PATH; CI runs them after
# setup-dotnet.) Paths reach the child pwsh through the environment, never pasted into the -Command
# text: a fixture path holds an apostrophe.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestKit.ps1')

# The SDK fixture plus the catalog half: a stable v5 release served over local HTTP and that same
# release committed as the seed, so a run gets past Step 0d unless a case moves one of them.
function New-ReleaseFixture {
    $f = Add-CatalogReleases (New-SdkFixture)
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 5) | Out-Null
    $f
}

# -LeakedReleases sets DIFFUSIONNEXUS_CATALOG_RELEASES for the run, as a value left in a developer's
# environment would; it is cleared otherwise.
function Invoke-NewRelease($Fixture, [string[]]$ExtraArgs = @(), [switch]$NativeErrors, [string]$LeakedReleases) {
    $scripts = Join-Path $Fixture.Installer 'Scripts'
    New-Item -ItemType Directory -Force -Path $scripts | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '..') -Filter '*.ps1' -File | Copy-Item -Destination $scripts
    $script = Join-Path $scripts 'New-Release.ps1'
    $props = Join-Path $Fixture.Installer 'Directory.Build.props'
    Set-Content -LiteralPath $props -Value '<Project><PropertyGroup><Version>0.0.1</Version></PropertyGroup></Project>'
    $savedSdkPath = $env:LocalSDKPath
    $savedReleases = $env:DIFFUSIONNEXUS_CATALOG_RELEASES
    $savedToken = $env:GITHUB_PACKAGES_TOKEN
    $env:LocalSDKPath = $Fixture.Sdk
    $env:DIFFUSIONNEXUS_CATALOG_RELEASES = $LeakedReleases
    $env:NEWRELEASE_TEST_RELEASES = $Fixture.ReleaseUrl
    $env:GITHUB_PACKAGES_TOKEN = 'fixture-token'
    $env:NEWRELEASE_TEST_SCRIPT = $script
    $env:NEWRELEASE_TEST_ALLOWOLDER = if ('-AllowOlderSdk' -in $ExtraArgs) { '1' } else { '' }
    $env:NEWRELEASE_TEST_ALLOWOLDERCATALOG = if ('-AllowOlderCatalog' -in $ExtraArgs) { '1' } else { '' }
    try {
        # A profile may set $PSNativeCommandUseErrorActionPreference; the gate must work either way.
        $preference = if ($NativeErrors) { '$true' } else { '$false' }
        $command = '$PSNativeCommandUseErrorActionPreference = ' + $preference + '; $extra = @{ AllowOlderSdk = [bool]$env:NEWRELEASE_TEST_ALLOWOLDER; AllowOlderCatalog = [bool]$env:NEWRELEASE_TEST_ALLOWOLDERCATALOG; CatalogReleases = $env:NEWRELEASE_TEST_RELEASES }; & $env:NEWRELEASE_TEST_SCRIPT -Version 9.9.9 -SkipUpload @extra'
        $output = & pwsh -NoProfile -Command $command 2>&1
        $exit = $LASTEXITCODE
        [pscustomobject]@{
            ExitCode       = $exit
            Text           = ($output | ForEach-Object { "$_" }) -join "`n"
            VersionWritten = (Get-Content -LiteralPath $props -Raw).Contains('<Version>9.9.9</Version>')
        }
    } finally {
        $env:LocalSDKPath = $savedSdkPath
        $env:DIFFUSIONNEXUS_CATALOG_RELEASES = $savedReleases
        $env:GITHUB_PACKAGES_TOKEN = $savedToken
        $env:NEWRELEASE_TEST_SCRIPT = $null
        $env:NEWRELEASE_TEST_ALLOWOLDER = $null
        $env:NEWRELEASE_TEST_ALLOWOLDERCATALOG = $null
        $env:NEWRELEASE_TEST_RELEASES = $null
    }
}

Test-Case 'a pin behind SDK develop refuses before the version write and names -AllowOlderSdk' {
    $f = New-ReleaseFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: unreleased change'
    $r = Invoke-NewRelease $f
    if ($r.ExitCode -eq 0) { throw "expected a refusal, got exit 0. Output:`n$($r.Text)" }
    Assert-Like $r.Text '*fix: unreleased change*re-run with -AllowOlderSdk*Nothing was built or changed*' 'output'
    Assert-Equal $r.VersionWritten $false 'version written'
}

Test-Case '-AllowOlderSdk gets past a behind pin even when the session turns native exit codes into errors' {
    $f = New-ReleaseFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: unreleased change'
    $r = Invoke-NewRelease $f -ExtraArgs @('-AllowOlderSdk') -NativeErrors
    Assert-Like $r.Text '*Releasing WITHOUT the SDK commits*dotnet publish failed*' 'output'
    Assert-Equal $r.VersionWritten $true 'version written'
}

Test-Case 'a pin that could not be checked refuses even with -AllowOlderSdk, whatever the native error setting' {
    $f = New-ReleaseFixture
    Set-InstallerPins $f '2.0.0-preview.1' -AppVersion '2.0.0-preview.2'
    $r = Invoke-NewRelease $f -ExtraArgs @('-AllowOlderSdk') -NativeErrors
    if ($r.ExitCode -eq 0) { throw "expected a refusal, got exit 0. Output:`n$($r.Text)" }
    Assert-Like $r.Text '*do not all pin the same version*could not be checked*Nothing was built or changed*' 'output'
    Assert-Equal $r.VersionWritten $false 'version written'
}

Test-Case 'a seed that is not the latest stable catalog refuses before the version write and names -AllowOlderCatalog' {
    $f = New-ReleaseFixture
    Publish-CatalogRelease $f -Version 6 | Out-Null
    $r = Invoke-NewRelease $f
    if ($r.ExitCode -eq 0) { throw "expected a refusal, got exit 0. Output:`n$($r.Text)" }
    Assert-Like $r.Text '*Step 0d*catalogVersion 5 vs 6*re-run with -AllowOlderCatalog*Nothing was built or changed*' 'output'
    Assert-Equal $r.VersionWritten $false 'version written'
}

Test-Case '-AllowOlderCatalog gets past a stale seed; -AllowOlderSdk alone does not' {
    $f = New-ReleaseFixture
    Publish-CatalogRelease $f -Version 6 | Out-Null
    $r = Invoke-NewRelease $f -ExtraArgs @('-AllowOlderSdk')
    Assert-Like $r.Text '*re-run with -AllowOlderCatalog*' 'output with -AllowOlderSdk only'
    Assert-Equal $r.VersionWritten $false 'version written with -AllowOlderSdk only'
    $r = Invoke-NewRelease $f -ExtraArgs @('-AllowOlderCatalog') -NativeErrors
    Assert-Like $r.Text '*Releasing WITHOUT the latest stable catalog seed*dotnet publish failed*' 'output'
    Assert-Equal $r.VersionWritten $true 'version written'
}

Test-Case 'a seed ahead of stable refuses even with -AllowOlderCatalog: the flag covers only an older stable release' {
    $f = New-ReleaseFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 6 -NotLatest) | Out-Null
    $r = Invoke-NewRelease $f -ExtraArgs @('-AllowOlderCatalog') -NativeErrors
    if ($r.ExitCode -eq 0) { throw "expected a refusal, got exit 0. Output:`n$($r.Text)" }
    Assert-Like $r.Text '*the seed is ahead*not a stable catalog release -AllowOlderCatalog may ship*Nothing was built or changed*' 'output'
    Assert-Equal $r.VersionWritten $false 'version written'
}

Test-Case 'a leftover DIFFUSIONNEXUS_CATALOG_RELEASES does not steer Step 0d: only -CatalogReleases does' {
    # The environment points at a server on which the seed is the latest; the releases the release
    # reads have moved on to v6. Step 0d must judge against those.
    $f = New-ReleaseFixture
    $leak = New-CatalogFixture
    Copy-Item -LiteralPath (Join-Path $f.Releases 'download') -Destination $leak.Releases -Recurse
    Set-Content -LiteralPath (Join-Path $leak.Releases 'latest') -Value 'v5' -NoNewline
    Publish-CatalogRelease $f -Version 6 | Out-Null
    $r = Invoke-NewRelease $f -LeakedReleases $leak.ReleaseUrl
    if ($r.ExitCode -eq 0) { throw "expected a refusal, got exit 0. Output:`n$($r.Text)" }
    Assert-Like $r.Text "*catalogVersion 5 vs 6*$($f.ReleaseUrl)*Nothing was built or changed*" 'output'
    Assert-Equal $r.VersionWritten $false 'version written'
}

Test-Case 'a seed that could not be checked refuses even with -AllowOlderCatalog' {
    $f = New-ReleaseFixture
    Set-Content -LiteralPath (Join-Path $f.Installer 'DiffusionNexus.Installer.Electron' 'Assets' 'Catalog' 'manifest.json.bak') -Value 'old'
    $r = Invoke-NewRelease $f -ExtraArgs @('-AllowOlderCatalog') -NativeErrors
    if ($r.ExitCode -eq 0) { throw "expected a refusal, got exit 0. Output:`n$($r.Text)" }
    Assert-Like $r.Text '*uncommitted changes*could not be checked*Nothing was built or changed*' 'output'
    Assert-Equal $r.VersionWritten $false 'version written'
}

Complete-Tests
