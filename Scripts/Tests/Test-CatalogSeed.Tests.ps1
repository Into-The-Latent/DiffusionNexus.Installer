#Requires -Version 7.2
# Tests for Scripts/Test-CatalogSeed.ps1. Run: pwsh -NoProfile -File Scripts/Tests/Test-CatalogSeed.Tests.ps1
# Exit code = number of failed cases. CI runs every Scripts/Tests/*.Tests.ps1.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestKit.ps1')

Test-Case 'equal: the seed is the latest stable release, reached through the releases/latest redirect' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 5) | Out-Null
    Assert-Result (Invoke-CatalogSeedCheck $f) 0 -Contains 'Catalog seed v5 (0000000) is the latest stable catalog.'
}

Test-Case 'behind: names both versions, the fix command, and where the release came from' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 4) | Out-Null
    Publish-CatalogRelease $f -Version 5 | Out-Null
    Assert-Result (Invoke-CatalogSeedCheck $f) 3 `
        -Contains 'is not the latest stable catalog (v5, 0000000', 'catalogVersion 4 vs 5 (the seed is behind)',
                  'pwsh Scripts/Update-CatalogSeed.ps1', "$($f.ReleaseUrl)/latest/download/manifest.json"
}

Test-Case 'ahead: a seed newer than stable is content the stable channel does not serve' {
    $f = New-CatalogFixture
    Publish-CatalogRelease $f -Version 5 | Out-Null
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 6 -NotLatest) | Out-Null
    Assert-Result (Invoke-CatalogSeedCheck $f) 3 -Contains 'catalogVersion 6 vs 5 (the seed is ahead: content the stable channel does not serve)'
}

Test-Case 'same version, other commit: a seed taken from a preview packed under the same number differs' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 5 -Commit ('a' * 40)) | Out-Null
    Publish-CatalogRelease $f -Version 5 -Commit ('b' * 40) | Out-Null
    Assert-Result (Invoke-CatalogSeedCheck $f) 3 -Contains 'commit aaaaaaa vs bbbbbbb' -Lacks 'catalogVersion'
}

Test-Case 'same version and commit, other bytes: a re-packed release differs by its archive hash' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 5 -Content 'first pack') | Out-Null
    Publish-CatalogRelease $f -Version 5 -Content 'second pack' | Out-Null
    Assert-Result (Invoke-CatalogSeedCheck $f) 3 -Contains 'archive sha256' -Lacks 'catalogVersion', 'commit 0000'
}

Test-Case 'a zip that disagrees with its own manifest is not checked, whatever the release says' {
    $f = New-CatalogFixture
    $seed = Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 5) -Uncommitted
    Set-Content -LiteralPath (Join-Path $seed 'catalog.zip') -Value 'not the archive'
    Invoke-FixtureGit $f.Installer @('add', '--all'); Invoke-FixtureGit $f.Installer @('commit', '--quiet', '-m', 'corrupt seed')
    Assert-Result (Invoke-CatalogSeedCheck $f) 2 -Contains 'NOT checked', 'does not match its manifest', 'pwsh Scripts/Update-CatalogSeed.ps1'
}

Test-Case 'an uncommitted seed is not checked: a release must never carry what no commit records' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 5) -Uncommitted | Out-Null
    Assert-Result (Invoke-CatalogSeedCheck $f) 2 -Contains 'NOT checked', 'uncommitted changes', 'manifest.json'
}

Test-Case 'an untracked file next to the seed is uncommitted too' {
    $f = New-CatalogFixture
    $seed = Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 5)
    Set-Content -LiteralPath (Join-Path $seed 'manifest.json.bak') -Value 'old'
    Assert-Result (Invoke-CatalogSeedCheck $f) 2 -Contains 'uncommitted changes', 'manifest.json.bak'
}

Test-Case 'a seed with no files is not checked' {
    $f = New-CatalogFixture
    Publish-CatalogRelease $f -Version 5 | Out-Null
    Assert-Result (Invoke-CatalogSeedCheck $f) 2 -Contains 'NOT checked', 'manifest.json does not exist'
}

Test-Case 'download fails: a server that has no release, and one that is not there at all, are both "not checked"' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 5) | Out-Null
    Remove-Item -LiteralPath (Join-Path $f.Releases 'latest')
    Assert-Result (Invoke-CatalogSeedCheck $f) 2 -Contains 'NOT checked', 'could not download', "$($f.ReleaseUrl)/latest/download/manifest.json"
    Assert-Result (Invoke-CatalogSeedCheck $f -ReleaseBase 'http://127.0.0.1:1/releases') 2 -Contains 'could not download http://127.0.0.1:1/releases/latest/download/manifest.json'
}

Test-Case 'a release whose manifest is not a manifest, or not a Stable one, is "not checked"' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 5) | Out-Null
    Set-Content -LiteralPath (Join-Path $f.Releases 'download' 'v5' 'manifest.json') -Value '<html>rate limited</html>'
    Assert-Result (Invoke-CatalogSeedCheck $f) 2 -Contains 'NOT checked', 'is not JSON'
    $g = New-CatalogFixture
    Set-CatalogSeed $g -From (Publish-CatalogRelease $g -Version 5) | Out-Null
    Publish-CatalogRelease $g -Version 6 -Channel 'Preview' | Out-Null
    Assert-Result (Invoke-CatalogSeedCheck $g) 2 -Contains 'is a Preview manifest, not a Stable one'
}

Test-Case 'the release location comes from -ReleaseBase, else DIFFUSIONNEXUS_CATALOG_RELEASES, and is printed' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 5) | Out-Null
    $saved = $env:DIFFUSIONNEXUS_CATALOG_RELEASES
    $env:DIFFUSIONNEXUS_CATALOG_RELEASES = "$($f.ReleaseUrl)/"
    try {
        $check = Join-Path $PSScriptRoot '..' 'Test-CatalogSeed.ps1'
        $output = & pwsh -NoProfile -File $check -RepoRoot $f.Installer 2>&1
        Assert-Result ([pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = ($output | ForEach-Object { "$_" }) -join "`n" }) 0 -Contains "$($f.ReleaseUrl)/latest/download/manifest.json"
    } finally { $env:DIFFUSIONNEXUS_CATALOG_RELEASES = $saved }
}

Test-Case 'a seed manifest rewritten with CRLF and a BOM still equals the release: fields are compared, not bytes' {
    $f = New-CatalogFixture
    $seed = Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 5) -Uncommitted
    $path = Join-Path $seed 'manifest.json'
    [IO.File]::WriteAllText($path, ((Get-Content -LiteralPath $path -Raw) -replace "`r?`n", "`r`n"), [Text.UTF8Encoding]::new($true))
    Invoke-FixtureGit $f.Installer @('add', '--all'); Invoke-FixtureGit $f.Installer @('commit', '--quiet', '-m', 'crlf seed')
    Assert-Result (Invoke-CatalogSeedCheck $f) 0
}

Test-Case '-Expect judges the values given and reads nothing on disk: no seed, no git repo needed' {
    $f = New-CatalogFixture
    $pack = Publish-CatalogRelease $f -Version 5
    $sha = (Get-FileHash -LiteralPath (Join-Path $pack 'catalog.zip') -Algorithm SHA256).Hash   # upper-case, as Get-FileHash prints it
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', "5 $(('0' * 7 + '5') * 5) $sha")) 0 -Contains 'Catalog seed v5 (0000000) is the latest stable catalog.'
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', "4,$(('0' * 7 + '4') * 5),$sha")) 3 -Contains 'The seed given is not the latest stable catalog', 'catalogVersion 4 vs 5' -Lacks 'Update-CatalogSeed'
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', "5 00000005 $sha")) 2 -Contains 'NOT checked', '-Expect commit'
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', "5 $sha")) 2 -Contains 'NOT checked', '-Expect takes three values'
}

Complete-Tests
