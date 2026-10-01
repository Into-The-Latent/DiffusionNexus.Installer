#Requires -Version 7.2
# Tests for Scripts/Promote-Release.ps1. Run: pwsh -NoProfile -File Scripts/Tests/Promote-Release.Tests.ps1
# Exit code = number of failed cases. CI runs every Scripts/Tests/*.Tests.ps1.
#
# Promote-Release.ps1 runs in-process, so the fake global `gh` below stands in for the CLI (a function
# wins over an executable of the same name): it answers `auth token`, `api repos/...`, `release view`,
# `release download` and `release edit` from $global:Fake, and records every call with the GH_TOKEN it
# ran under. The two checks run for real, in their own pwsh, against the TestKit fixtures: the SDK
# through LocalSDKPath, the catalog releases through -CatalogReleases. Scripts/*.ps1 are copied into
# the fixture's Scripts folder, so the projects of the checkout promotion runs from are the fixture's.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestKit.ps1')

$repo = 'Into-The-Latent/DiffusionNexus.Installer'
$reader = @{ User = 'Little-God1983'; Token = 'tok-reader'; CanPush = $false }
$owner  = @{ User = 'Into-The-Latent'; Token = 'tok-owner'; CanPush = $true }
$sdkPackages = @('DiffusionNexus.Installer.SDK.Catalog', 'DiffusionNexus.Installer.SDK.Models', 'DiffusionNexus.Installer.SDK.Services')

# A failing gh writes its reason to stderr and exits 1.
function Fail-Gh([string]$Reason) { Write-Error $Reason -ErrorAction Continue; $global:LASTEXITCODE = 1 }

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
    if ($call -eq "api repos/$repo/releases/latest --jq .tag_name") {
        if ($f.LatestDown) { return Fail-Gh 'gh: Bad Gateway (HTTP 502)' }
        # releases/latest lagging behind an edit for that many reads.
        if ($f.Edited -and $f.LatestLag -gt 0) { $f.LatestLag--; return $f.LatestBeforeEdit }
        if ($null -eq $f.Latest) { '{"message":"Not Found","status":"404"}'; return Fail-Gh 'gh: Not Found (HTTP 404)' }
        return $f.Latest
    }
    if ($call -like "api repos/$repo --jq*") { return $(if ($byToken.CanPush) { 'true' } else { 'false' }) }
    if ($call -like 'api user *') { return $f.Accounts[0].User }
    if ($args[0] -eq 'release') {
        $release = $f.Releases[$args[2]]
        switch ($args[1]) {
            'view' {
                $f.Views++
                if ($f.Views -eq 2 -and $f.OnSecondView) { & $f.OnSecondView }
                if (-not $release -or $f.ViewDown -or ($f.ViewDownAfterEdit -and $f.Edited)) { return Fail-Gh 'release not found' }
                $assets = @($release.Assets | ForEach-Object { if ($_ -is [string]) { @{ name = $_; state = 'uploaded'; size = 100 } } else { $_ } })
                return (@{ isPrerelease = $release.Prerelease; isDraft = $release.Draft; assets = $assets } | ConvertTo-Json -Depth 4 -Compress)
            }
            'download' {
                if ($f.DownloadFails) { return Fail-Gh 'HTTP 503' }
                if (-not $f.DownloadWritesNothing) {
                    $dir = $args[[array]::IndexOf($args, '--dir') + 1]
                    Set-Content -LiteralPath (Join-Path $dir 'build-info.json') -Value $release.BuildInfo -NoNewline
                }
                return
            }
            'edit' {
                $f.Edited = $true
                $f.LatestBeforeEdit = $f.Latest
                # How far the edit got before gh answered: none, the flag only, all of it, or (Silent) none
                # although gh says it worked.
                if ($f.EditGets -in 'Flag', 'All') { $release.Prerelease = $false }
                if ($f.EditGets -eq 'All') { $f.Latest = $args[2] }
                if ($null -eq $f.EditGets) { $release.Prerelease = $false; $f.Latest = $args[2]; return }
                if ($f.EditGets -eq 'Silent') { return }
                return Fail-Gh 'HTTP 502'
            }
        }
    }
    $global:LASTEXITCODE = 1
}

$allAssets = @('EasyWorkloadInstaller-ITL-Setup-3.1.0.exe', 'EasyWorkloadInstaller-ITL-Setup-3.1.0.exe.blockmap', 'latest.yml', 'build-info.json')

# A Preview v3.1.0 with every asset, whose build-info.json reports SDK 2.0.0-preview.1 (tagged,
# nothing after it) in three packages and the stable catalog v5 (the latest); v3.0.10 is the latest
# Stable release. Everything current, so a case moves one thing.
function New-PromoteFixture {
    $f = Add-CatalogReleases (New-SdkFixture)
    $v5 = Publish-CatalogRelease $f -Version 5
    $f | Add-Member -NotePropertyName Seed -NotePropertyValue (Get-Content -LiteralPath (Join-Path $v5 'manifest.json') -Raw | ConvertFrom-Json -DateKind String)
    $global:Fake = @{
        Accounts = @($reader, $owner)
        Latest   = 'v3.0.10'
        Releases = @{
            'v3.1.0'  = @{ Prerelease = $true; Draft = $false; Assets = $allAssets; BuildInfo = (New-BuildInfo $f) }
            'v3.0.10' = @{ Prerelease = $false; Draft = $false; Assets = @('latest.yml'); BuildInfo = '' }
        }
        Calls = [System.Collections.Generic.List[object]]::new()
        Views = 0
    }
    $f
}

function New-BuildInfo($Fixture, [string]$App = '3.1.0', [string]$Sdk = '2.0.0-preview.1', [string]$Channel = $Fixture.Seed.channel, $Packages = $sdkPackages) {
    $s = $Fixture.Seed
    $info = [ordered]@{
        app = $App; sdk = $Sdk; catalogSchema = 1
        catalogSeed = [ordered]@{ version = $s.catalogVersion; commit = $s.commit; sha256 = $s.archive.sha256; channel = $Channel; generatedAt = $s.generatedAt }
        builtAt = '2026-10-01T12:00:00Z'
    }
    if ($null -ne $Packages) { $info.sdkPackages = @($Packages) }
    $info | ConvertTo-Json -Depth 4
}

function Invoke-Promote($Fixture, [string]$Version = '3.1.0', [hashtable]$Flags = @{}, [string]$LeakedReleases) {
    $scripts = Join-Path $Fixture.Installer 'Scripts'
    New-Item -ItemType Directory -Force -Path $scripts | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '..') -Filter '*.ps1' -File | Copy-Item -Destination $scripts
    $savedSdkPath = $env:LocalSDKPath
    $savedReleases = $env:DIFFUSIONNEXUS_CATALOG_RELEASES
    $env:LocalSDKPath = $Fixture.Sdk
    $env:DIFFUSIONNEXUS_CATALOG_RELEASES = $LeakedReleases
    $global:Fake.Calls.Clear(); $global:Fake.Views = 0; $global:Fake.Edited = $false
    try {
        $params = @{ Version = $Version; CatalogReleases = $Fixture.ReleaseUrl } + $Flags
        $script = Join-Path $scripts 'Promote-Release.ps1'
        $output = & { try { & $script @params; 'COMPLETED' } catch { "REFUSED: $($_.Exception.Message)" } } *>&1
        $text = ($output | ForEach-Object { "$_" }) -join "`n"
        [pscustomobject]@{
            Text    = $text
            Refused = $text.Contains('REFUSED: ')
            Edits   = @($global:Fake.Calls | Where-Object Call -like 'release edit *')
            Reads   = @($global:Fake.Calls | Where-Object { $_.Call -like 'release view *' -or $_.Call -like 'release download *' })
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

function Assert-Promoted($Result, [string]$Pattern = '*Promoted v3.1.0 to Stable*') {
    if ($Result.Refused) { throw "expected a promotion. Output:`n$($Result.Text)" }
    Assert-Like $Result.Text $Pattern 'output'
    Assert-Equal $Result.Edits.Count 1 'release edits'
}

function Assert-NothingToDo($Result, [string]$Pattern) {
    if ($Result.Refused) { throw "expected no refusal. Output:`n$($Result.Text)" }
    Assert-Like $Result.Text $Pattern 'output'
    Assert-Equal $Result.Edits.Count 0 'release edits'
}

# --------------------------------------------------------------------------------- promoted
Test-Case 'everything current: promoted with the writer token, exact edit, GH_TOKEN left as it was' {
    $f = New-PromoteFixture
    $env:GH_TOKEN = 'tok-from-profile'
    try { $r = Invoke-Promote $f; $after = $env:GH_TOKEN } finally { Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue }
    Assert-Promoted $r '*SDK pin 2.0.0-preview.1 includes everything*Catalog seed v5*is the latest stable catalog*Promoted v3.1.0 to Stable*'
    Assert-Equal $r.Edits[0].Call "release edit v3.1.0 --repo $repo --prerelease=false --latest" 'the edit'
    Assert-Equal $r.Edits[0].Token 'tok-owner' 'the token the edit ran under'
    Assert-Equal $after 'tok-from-profile' 'GH_TOKEN after the run'
}

Test-Case 'a repo with no full release yet (releases/latest is 404) is promoted: nothing to go backwards from' {
    $f = New-PromoteFixture
    $global:Fake.Latest = $null
    Assert-Promoted (Invoke-Promote $f)
}

Test-Case 'un-marked but not latest (a half-done promotion, a hand edit): the gates run and it is made latest' {
    $f = New-PromoteFixture
    $global:Fake.Releases['v3.1.0'].Prerelease = $false
    $r = Invoke-Promote $f
    Assert-Promoted $r '*is no longer a pre-release but is not GitHub''s latest release (that is v3.0.10)*includes everything*Promoted v3.1.0*'
    Assert-Equal ([regex]::Matches($r.Text, 'is no longer a pre-release but').Count) 1 'times the state is announced'
}

Test-Case 'un-marked but not latest, and a gate refuses: the refusal says it stays un-marked, not "still a pre-release"' {
    $f = New-PromoteFixture
    $global:Fake.Releases['v3.1.0'].Prerelease = $false
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: landed while in Preview'
    $r = Invoke-Promote $f
    Assert-Refused $r '*NOT promoted*Nothing was changed: v3.1.0 stays un-marked but not GitHub''s latest release*'
    if ($r.Text.Contains('still a pre-release')) { throw "the refusal calls an un-marked release a pre-release. Output:`n$($r.Text)" }
}

Test-Case 'releases/latest lagging behind a successful edit is waited for, not reported as a failure' {
    $f = New-PromoteFixture
    $global:Fake.LatestLag = 2
    Assert-Promoted (Invoke-Promote $f)
}

Test-Case 'vX promoted by someone else while the checks ran: nothing left to do, said as such, no edit' {
    $f = New-PromoteFixture
    $global:Fake.OnSecondView = { $global:Fake.Latest = 'v3.1.0'; $global:Fake.Releases['v3.1.0'].Prerelease = $false }
    $r = Invoke-Promote $f
    Assert-NothingToDo $r '*includes everything*While the checks ran, v3.1.0 became GitHub''s latest Stable release: nothing left to promote*'
}

Test-Case '-AllowOlderSdk and -AllowOlderCatalog promote with a warning' {
    $f = New-PromoteFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: landed while in Preview'
    Publish-CatalogRelease $f -Version 6 | Out-Null
    Assert-Promoted (Invoke-Promote $f -Flags @{ AllowOlderSdk = $true; AllowOlderCatalog = $true }) '*Promoting WITHOUT the SDK commits*Promoting WITHOUT the latest stable catalog seed*Promoted v3.1.0*'
}

# ------------------------------------------------------------------------ nothing to promote
Test-Case 'already GitHub''s latest Stable release: nothing to promote, no edit, not an error' {
    $f = New-PromoteFixture
    $global:Fake.Releases['v3.1.0'].Prerelease = $false
    $global:Fake.Latest = 'v3.1.0'
    Assert-NothingToDo (Invoke-Promote $f) '*v3.1.0 is already GitHub''s latest Stable release: nothing to promote*'
}

Test-Case 'an older Stable release below the latest: nothing to promote, not an error' {
    $f = New-PromoteFixture
    $global:Fake.Releases['v3.1.0'].Prerelease = $false
    $global:Fake.Latest = 'v3.2.0'
    Assert-NothingToDo (Invoke-Promote $f) '*v3.1.0 is an older Stable release; v3.2.0 is the latest: nothing to promote*'
}

# ------------------------------------------------------------------------- refused: GitHub
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

Test-Case 'a latest release that cannot be read (not a 404) is refused, with gh''s reason' {
    $f = New-PromoteFixture
    $global:Fake.LatestDown = $true
    Assert-Refused (Invoke-Promote $f) '*Could not read the latest release*HTTP 502*Nothing was changed*'
}

Test-Case 'a latest tag that is no vX.Y.Z is refused: the backwards guard compares or says no' {
    $f = New-PromoteFixture
    foreach ($latest in 'v3.2.0-hotfix', '3.2.0', '') {
        $global:Fake.Latest = $latest
        Assert-Refused (Invoke-Promote $f) "*latest release of $repo is '$latest', which is no vX.Y.Z tag*"
    }
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

Test-Case 'a missing or half-uploaded asset is refused before anything is downloaded: Stable would read it from latest' {
    $f = New-PromoteFixture
    $global:Fake.Releases['v3.1.0'].Assets = @('EasyWorkloadInstaller-ITL-Setup-3.1.0.exe', 'EasyWorkloadInstaller-ITL-Setup-3.1.0.exe.blockmap', 'build-info.json')
    $r = Invoke-Promote $f
    Assert-Refused $r '*does not carry every release asset*latest.yml (missing)*Cut a new Preview*'
    Assert-Equal @($r.Reads | Where-Object Call -like 'release download *').Count 0 'downloads'
    $global:Fake.Releases['v3.1.0'].Assets = @(
        @{ name = 'EasyWorkloadInstaller-ITL-Setup-3.1.0.exe'; state = 'starter'; size = 0 }
        'EasyWorkloadInstaller-ITL-Setup-3.1.0.exe.blockmap'
        @{ name = 'latest.yml'; state = 'uploaded'; size = 0 }
        'build-info.json')
    Assert-Refused (Invoke-Promote $f) "*Setup-3.1.0.exe (upload not finished: state 'starter', 0 bytes)*latest.yml (upload not finished: state 'uploaded', 0 bytes)*"
    $global:Fake.Releases['v3.1.0'].Assets = @('EasyWorkloadInstaller-ITL-Setup-3.1.0.exe', 'EasyWorkloadInstaller-ITL-Setup-3.1.0.exe.blockmap', 'latest.yml')
    Assert-Refused (Invoke-Promote $f) '*build-info.json (missing)*made before build-info.json existed*'
}

Test-Case 'a promotion that lands while the checks run is seen: no edit from the old snapshot' {
    $f = New-PromoteFixture
    $global:Fake.OnSecondView = { $global:Fake.Latest = 'v3.2.0'; $global:Fake.Releases['v3.2.0'] = @{ Prerelease = $false; Draft = $false; Assets = @(); BuildInfo = '' } }
    Assert-Refused (Invoke-Promote $f) '*includes everything*While the checks ran, GitHub changed: v3.2.0 is already Stable and newer than v3.1.0*Nothing was changed*'
}

# --------------------------------------------------------------------- refused: build-info
Test-Case 'build-info.json that cannot be downloaded, or is not there after the download, is refused' {
    $f = New-PromoteFixture
    $global:Fake.DownloadFails = $true
    Assert-Refused (Invoke-Promote $f) '*Could not download build-info.json of v3.1.0 (gh exit 1)*HTTP 503*'
    $global:Fake.DownloadFails = $false; $global:Fake.DownloadWritesNothing = $true
    Assert-Refused (Invoke-Promote $f) '*Could not download build-info.json of v3.1.0 (gh exit 0)*'
}

Test-Case 'a build-info.json that is not this version''s, not readable, or lists no sdkPackages is refused' {
    $f = New-PromoteFixture
    $global:Fake.Releases['v3.1.0'].BuildInfo = New-BuildInfo $f -App '3.0.10'
    Assert-Refused (Invoke-Promote $f) "*says it describes version '3.0.10', not 3.1.0*"
    $global:Fake.Releases['v3.1.0'].BuildInfo = '{ "app": "3.1.0", "sdk": "2.0.0", '
    Assert-Refused (Invoke-Promote $f) '*build-info.json of v3.1.0 cannot be read*Cut a new Preview*'
    $global:Fake.Releases['v3.1.0'].BuildInfo = New-BuildInfo $f -Packages $null
    Assert-Refused (Invoke-Promote $f) '*build-info.json of v3.1.0 cannot be read (build-info.json of v3.1.0 lists no sdkPackages.)*'
}

# ------------------------------------------------------------------------ refused: the gates
Test-Case 'SDK behind develop: refused with the commit, "cut a new Preview" and -AllowOlderSdk' {
    $f = New-PromoteFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: landed while in Preview'
    Assert-Refused (Invoke-Promote $f) '*fix: landed while in Preview*v3.1.0 was NOT promoted*Cut a new Preview*-AllowOlderSdk*still a pre-release*'
}

Test-Case 'the build''s sdkPackages decide which SDK commits count, not the checkout promotion runs from' {
    # The checkout stopped referencing Catalog; the build ships it, and a Catalog fix landed meanwhile.
    $f = New-PromoteFixture
    Write-FixtureProject $f 'Installer.Core' @('DiffusionNexus.Installer.SDK.Models') '2.0.0-preview.1'
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Catalog/Catalog.cs' 'fix: catalog fix while in Preview'
    Assert-Refused (Invoke-Promote $f) '*fix: catalog fix while in Preview*SDK 2.0.0-preview.1 is behind*'
    # ...and a package the build does not ship does not count, whatever the checkout references.
    $global:Fake.Releases['v3.1.0'].BuildInfo = New-BuildInfo $f -Packages @('DiffusionNexus.Installer.SDK.Models')
    Assert-Promoted (Invoke-Promote $f)
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
    Assert-Refused (Invoke-Promote $f) '*NOT promoted*-AllowOlderSdk*-AllowOlderCatalog*'
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

# ------------------------------------------------------------------------------- the edit
Test-Case 'an edit gh calls failed that GitHub shows as done is a promotion, with gh''s error as a warning' {
    $f = New-PromoteFixture
    $global:Fake.EditGets = 'All'
    Assert-Promoted (Invoke-Promote $f) '*gh release edit v3.1.0 failed (gh exit 1)*HTTP 502*GitHub shows v3.1.0 as Stable and the latest release all the same*Promoted v3.1.0 to Stable*'
}

Test-Case 'a failed edit reports what GitHub shows afterwards, read back, never a guess' {
    $f = New-PromoteFixture
    $cases = [ordered]@{
        'None'    = '*gh release edit v3.1.0 failed*HTTP 502*v3.1.0 is still a pre-release*'
        'Flag'    = '*gh release edit v3.1.0 failed*v3.1.0 is no longer a pre-release, but GitHub''s latest release is v3.0.10*Re-run this script*'
    }
    foreach ($gets in $cases.Keys) {
        $global:Fake.Releases['v3.1.0'].Prerelease = $true; $global:Fake.Latest = 'v3.0.10'
        $global:Fake.EditGets = $gets
        $r = Invoke-Promote $f
        if (-not $r.Refused) { throw "$gets : expected a failure. Output:`n$($r.Text)" }
        Assert-Like $r.Text $cases[$gets] "output when the edit got $gets"
    }
    $global:Fake.Releases['v3.1.0'].Prerelease = $true; $global:Fake.Latest = 'v3.0.10'
    $global:Fake.EditGets = 'None'; $global:Fake.ViewDownAfterEdit = $true
    Assert-Like (Invoke-Promote $f).Text '*gh release edit v3.1.0 failed*could not be read back*re-run this script*' 'output when nothing can be read back'
}

Test-Case 'an edit gh reports as done but GitHub does not show is not called a promotion' {
    $f = New-PromoteFixture
    $global:Fake.EditGets = 'Silent'
    $r = Invoke-Promote $f
    if (-not $r.Refused) { throw "expected a failure. Output:`n$($r.Text)" }
    Assert-Like $r.Text '*gh release edit v3.1.0 reported success, but: v3.1.0 is still a pre-release*' 'output'
}

Remove-Item function:global:gh
Complete-Tests
