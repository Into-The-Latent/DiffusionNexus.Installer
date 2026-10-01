#Requires -Version 7.2
# Tests for Scripts/Promote-Release.ps1. Run: pwsh -NoProfile -File Scripts/Tests/Promote-Release.Tests.ps1
# Exit code = number of failed cases. CI runs every Scripts/Tests/*.Tests.ps1.
#
# Promote-Release.ps1 runs in-process, so the fake global `gh` below stands in for the CLI (a function
# wins over an executable of the same name): it answers `auth token`, `api repos/...`, `release view`,
# `release download` and `release edit` from $global:Fake, and records every call with the GH_TOKEN it
# ran under. The two checks run for real, in their own pwsh, against the TestKit fixtures: the SDK
# through LocalSDKPath, the catalog releases through -CatalogReleases. Scripts/*.ps1 are copied into
# the fixture's Scripts folder, so the projects that say which SDK packages count are the fixture's.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestKit.ps1')

$repo = 'Into-The-Latent/DiffusionNexus.Installer'
$reader = @{ User = 'Little-God1983'; Token = 'tok-reader'; CanPush = $false }
$owner  = @{ User = 'Into-The-Latent'; Token = 'tok-owner'; CanPush = $true }

function global:gh {
    $call = $args -join ' '
    $f = $global:Fake
    $f.Calls.Add([pscustomobject]@{ Call = $call; Token = $env:GH_TOKEN })
    $global:LASTEXITCODE = 0
    $byToken = $f.Accounts | Where-Object Token -eq $env:GH_TOKEN | Select-Object -First 1
    if ($call -eq 'auth token') { return $f.Accounts[0].Token }
    if ($call -like 'auth token --user *') {
        $account = $f.Accounts | Where-Object User -eq $args[3] | Select-Object -First 1
        if ($account) { return $account.Token }
        $global:LASTEXITCODE = 1; return
    }
    if ($call -eq "api repos/$repo/releases/latest --jq .tag_name") { return $f.Latest }
    if ($call -like "api repos/$repo --jq*") { return $(if ($byToken.CanPush) { 'true' } else { 'false' }) }
    if ($call -like 'api user *') { return $f.Accounts[0].User }
    if ($args[0] -eq 'release') {
        $release = $f.Releases[$args[2]]
        if (-not $release -or $f.ViewDown) { Write-Error 'release not found' -ErrorAction Continue; $global:LASTEXITCODE = 1; return }
        switch ($args[1]) {
            'view' {
                $assets = @($release.Assets | ForEach-Object { @{ name = $_ } })
                return (@{ isPrerelease = $release.Prerelease; isDraft = $release.Draft; assets = $assets } | ConvertTo-Json -Depth 4 -Compress)
            }
            'download' {
                $dir = $args[[array]::IndexOf($args, '--dir') + 1]
                Set-Content -LiteralPath (Join-Path $dir 'build-info.json') -Value $release.BuildInfo -NoNewline
                return
            }
            'edit' {
                if ($f.EditFails) { Write-Error 'HTTP 502' -ErrorAction Continue; $global:LASTEXITCODE = 1; return }
                $release.Prerelease = $false
                $f.Latest = $args[2]
                return
            }
        }
    }
    $global:LASTEXITCODE = 1
}

# A Preview v3.1.0 whose build-info.json reports SDK 2.0.0-preview.1 (tagged, nothing after it) and
# the stable catalog v5 (the latest): everything current, so a case moves one thing.
function New-PromoteFixture {
    $f = Add-CatalogReleases (New-SdkFixture)
    $v5 = Publish-CatalogRelease $f -Version 5
    $f | Add-Member -NotePropertyName Seed -NotePropertyValue (Get-Content -LiteralPath (Join-Path $v5 'manifest.json') -Raw | ConvertFrom-Json -DateKind String)
    $global:Fake = @{
        Accounts = @($reader, $owner)
        Latest   = 'v3.0.10'
        Releases = @{
            'v3.1.0'  = @{ Prerelease = $true; Draft = $false; Assets = @('EasyWorkloadInstaller-ITL-Setup-3.1.0.exe', 'latest.yml', 'build-info.json'); BuildInfo = (New-BuildInfo $f) }
            'v3.0.10' = @{ Prerelease = $false; Draft = $false; Assets = @('latest.yml'); BuildInfo = '' }
        }
        Calls = [System.Collections.Generic.List[object]]::new()
    }
    $f
}

function New-BuildInfo($Fixture, [string]$App = '3.1.0', [string]$Sdk = '2.0.0-preview.1', [int]$SeedVersion = $Fixture.Seed.catalogVersion, [string]$Channel = $Fixture.Seed.channel) {
    $s = $Fixture.Seed
    [ordered]@{
        app = $App; sdk = $Sdk; catalogSchema = 1
        catalogSeed = [ordered]@{ version = $SeedVersion; commit = $s.commit; sha256 = $s.archive.sha256; channel = $Channel; generatedAt = $s.generatedAt }
        builtAt = '2026-10-01T12:00:00Z'
    } | ConvertTo-Json -Depth 4
}

function Invoke-Promote($Fixture, [string]$Version = '3.1.0', [hashtable]$Flags = @{}, [string]$LeakedReleases) {
    $scripts = Join-Path $Fixture.Installer 'Scripts'
    New-Item -ItemType Directory -Force -Path $scripts | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '..') -Filter '*.ps1' -File | Copy-Item -Destination $scripts
    $savedSdkPath = $env:LocalSDKPath
    $savedReleases = $env:DIFFUSIONNEXUS_CATALOG_RELEASES
    $env:LocalSDKPath = $Fixture.Sdk
    $env:DIFFUSIONNEXUS_CATALOG_RELEASES = $LeakedReleases
    try {
        $params = @{ Version = $Version; CatalogReleases = $Fixture.ReleaseUrl } + $Flags
        $script = Join-Path $scripts 'Promote-Release.ps1'
        $output = & { try { & $script @params; 'COMPLETED' } catch { "REFUSED: $($_.Exception.Message)" } } *>&1
        $text = ($output | ForEach-Object { "$_" }) -join "`n"
        [pscustomobject]@{
            Text     = $text
            Refused  = $text.Contains('REFUSED: ')
            Edits    = @($global:Fake.Calls | Where-Object Call -like 'release edit *')
            Reads    = @($global:Fake.Calls | Where-Object { $_.Call -like 'release view *' -or $_.Call -like 'release download *' })
        }
    } finally {
        $env:LocalSDKPath = $savedSdkPath
        $env:DIFFUSIONNEXUS_CATALOG_RELEASES = $savedReleases
    }
}

function Assert-Refused($Result, [string]$Pattern) {
    if (-not $Result.Refused) { throw "expected a refusal. Output:`n$($Result.Text)" }
    Assert-Like $Result.Text $Pattern 'output'
    Assert-Equal $Result.Edits.Count 0 'release edits'
}

Test-Case 'everything current: promoted with the writer token, exact edit, GH_TOKEN left as it was' {
    $f = New-PromoteFixture
    $env:GH_TOKEN = 'tok-from-profile'
    try { $r = Invoke-Promote $f; $after = $env:GH_TOKEN } finally { Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue }
    if ($r.Refused) { throw "expected a promotion. Output:`n$($r.Text)" }
    Assert-Like $r.Text '*SDK pin 2.0.0-preview.1 includes everything*Catalog seed v5*is the latest stable catalog*Promoted v3.1.0 to Stable*' 'output'
    Assert-Equal $r.Edits.Count 1 'release edits'
    Assert-Equal $r.Edits[0].Call "release edit v3.1.0 --repo $repo --prerelease=false --latest" 'the edit'
    Assert-Equal $r.Edits[0].Token 'tok-owner' 'the token the edit ran under'
    Assert-Equal $after 'tok-from-profile' 'GH_TOKEN after the run'
}

Test-Case 'no signed-in account can write: refused before the release is read' {
    $f = New-PromoteFixture
    $global:Fake.Accounts = @($reader)
    $r = Invoke-Promote $f
    Assert-Refused $r "*Sign Into-The-Latent in once*Nothing was read or changed*"
    Assert-Equal $r.Reads.Count 0 'release reads'
}

Test-Case 'a release that does not exist or cannot be read is refused, with gh''s reason' {
    $f = New-PromoteFixture
    Assert-Refused (Invoke-Promote $f -Version '3.9.9') '*Could not read release v3.9.9*release not found*Nothing was changed*'
    $global:Fake.ViewDown = $true
    Assert-Refused (Invoke-Promote $f) '*Could not read release v3.1.0*'
}

Test-Case 'already Stable: nothing to promote, no edit, not an error' {
    $f = New-PromoteFixture
    $global:Fake.Releases['v3.1.0'].Prerelease = $false
    $r = Invoke-Promote $f
    if ($r.Refused) { throw "expected no refusal. Output:`n$($r.Text)" }
    Assert-Like $r.Text '*v3.1.0 is already a Stable release: nothing to promote*' 'output'
    Assert-Equal $r.Edits.Count 0 'release edits'
}

Test-Case 'a draft is refused: testers never ran it' {
    $f = New-PromoteFixture
    $global:Fake.Releases['v3.1.0'].Draft = $true
    Assert-Refused (Invoke-Promote $f) '*v3.1.0 is a draft*'
}

Test-Case 'a Preview below the current Stable release is refused: latest would go backwards' {
    $f = New-PromoteFixture
    $global:Fake.Latest = 'v3.2.0'
    Assert-Refused (Invoke-Promote $f) '*v3.2.0 is already Stable and newer than v3.1.0*'
}

Test-Case 'no build-info.json asset (a release from before the asset): refused with "cut a new Preview"' {
    $f = New-PromoteFixture
    $global:Fake.Releases['v3.1.0'].Assets = @('latest.yml')
    $r = Invoke-Promote $f
    Assert-Refused $r '*has no build-info.json asset*Cut a new Preview*'
    Assert-Equal @($r.Reads | Where-Object Call -like 'release download *').Count 0 'downloads'
}

Test-Case 'a build-info.json that is not this version''s, or not readable, is refused' {
    $f = New-PromoteFixture
    $global:Fake.Releases['v3.1.0'].BuildInfo = New-BuildInfo $f -App '3.0.10'
    Assert-Refused (Invoke-Promote $f) "*says it describes version '3.0.10', not 3.1.0*"
    $global:Fake.Releases['v3.1.0'].BuildInfo = '{ "app": "3.1.0", "sdk": "2.0.0", '
    Assert-Refused (Invoke-Promote $f) '*build-info.json of v3.1.0 cannot be read*Cut a new Preview*'
}

Test-Case 'SDK behind develop: refused with the commit, "cut a new Preview" and -AllowOlderSdk' {
    $f = New-PromoteFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: landed while in Preview'
    Assert-Refused (Invoke-Promote $f) '*fix: landed while in Preview*v3.1.0 was NOT promoted*Cut a new Preview*-AllowOlderSdk*still a pre-release*'
}

Test-Case 'catalog behind the latest stable: refused with the difference and -AllowOlderCatalog' {
    $f = New-PromoteFixture
    Publish-CatalogRelease $f -Version 6 | Out-Null
    Assert-Refused (Invoke-Promote $f) '*catalogVersion 5 vs 6*v3.1.0 was NOT promoted*-AllowOlderCatalog*'
}

Test-Case 'both behind: one refusal lists both' {
    $f = New-PromoteFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: landed while in Preview'
    Publish-CatalogRelease $f -Version 6 | Out-Null
    Assert-Refused (Invoke-Promote $f -Flags @{ AllowOlderCatalog = $true }) '*NOT promoted*SDK 2.0.0-preview.1 is behind*'
    $r = Invoke-Promote $f
    Assert-Refused $r '*NOT promoted*-AllowOlderSdk*-AllowOlderCatalog*'
}

Test-Case '-AllowOlderSdk and -AllowOlderCatalog promote with a warning' {
    $f = New-PromoteFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: landed while in Preview'
    Publish-CatalogRelease $f -Version 6 | Out-Null
    $r = Invoke-Promote $f -Flags @{ AllowOlderSdk = $true; AllowOlderCatalog = $true }
    if ($r.Refused) { throw "expected a promotion. Output:`n$($r.Text)" }
    Assert-Like $r.Text '*Promoting WITHOUT the SDK commits*Promoting WITHOUT the latest stable catalog seed*Promoted v3.1.0*' 'output'
    Assert-Equal $r.Edits.Count 1 'release edits'
}

Test-Case 'an SDK version that cannot be checked is refused even with -AllowOlderSdk' {
    $f = New-PromoteFixture
    $global:Fake.Releases['v3.1.0'].BuildInfo = New-BuildInfo $f -Sdk '2.0.0-preview.7'
    Assert-Refused (Invoke-Promote $f -Flags @{ AllowOlderSdk = $true; AllowOlderCatalog = $true }) '*has no tag v2.0.0-preview.7*could not be checked*No flag overrides that*'
}

Test-Case 'a seed that is no stable release (exit 4) or cannot be checked (exit 2) is refused even with -AllowOlderCatalog' {
    $f = New-PromoteFixture
    $global:Fake.Releases['v3.1.0'].BuildInfo = New-BuildInfo $f -Channel 'Preview'
    Assert-Refused (Invoke-Promote $f -Flags @{ AllowOlderCatalog = $true }) '*a Preview seed*no stable catalog release*No flag overrides that*'
    $global:Fake.Releases['v3.1.0'].BuildInfo = New-BuildInfo $f
    Remove-Item -LiteralPath (Join-Path $f.Releases 'download' 'v5' 'manifest.json')
    Assert-Refused (Invoke-Promote $f -Flags @{ AllowOlderCatalog = $true }) '*Catalog seed NOT checked*could not be checked*No flag overrides that*'
}

Test-Case 'a leftover DIFFUSIONNEXUS_CATALOG_RELEASES does not steer the seed check: only -CatalogReleases does' {
    $f = New-PromoteFixture
    $leak = New-CatalogFixture
    Copy-Item -LiteralPath (Join-Path $f.Releases 'download') -Destination $leak.Releases -Recurse
    Set-Content -LiteralPath (Join-Path $leak.Releases 'latest') -Value 'v5' -NoNewline
    Publish-CatalogRelease $f -Version 6 | Out-Null
    Assert-Refused (Invoke-Promote $f -LeakedReleases $leak.ReleaseUrl) "*catalogVersion 5 vs 6*$($f.ReleaseUrl)*NOT promoted*"
}

Test-Case 'a failed edit says the release is still a pre-release' {
    $f = New-PromoteFixture
    $global:Fake.EditFails = $true
    $r = Invoke-Promote $f
    if (-not $r.Refused) { throw "expected a failure. Output:`n$($r.Text)" }
    Assert-Like $r.Text '*gh release edit v3.1.0 failed*HTTP 502*v3.1.0 is still a pre-release*' 'output'
}

Remove-Item function:global:gh
Complete-Tests
