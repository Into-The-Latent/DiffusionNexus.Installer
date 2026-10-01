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

Test-Case 'behind: an older stable release names both versions, the fix command, and where the release came from' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 4) | Out-Null
    Publish-CatalogRelease $f -Version 5 | Out-Null
    Assert-Result (Invoke-CatalogSeedCheck $f) 3 `
        -Contains 'is not the latest stable catalog (v5, 0000000', 'catalogVersion 4 vs 5 (the seed is behind)',
                  "It is the older stable release v4 ($($f.ReleaseUrl)/download/v4/manifest.json)",
                  'pwsh Scripts/Update-CatalogSeed.ps1', "$($f.ReleaseUrl)/latest/download/manifest.json"
}

Test-Case 'behind, but not the stable release of its own number: no flag may ship it (exit 4)' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 4 -Commit ('a' * 40)) | Out-Null
    Publish-CatalogRelease $f -Version 4 -Commit ('b' * 40) -NotLatest | Out-Null
    Publish-CatalogRelease $f -Version 5 | Out-Null
    Assert-Result (Invoke-CatalogSeedCheck $f) 4 `
        -Contains 'catalogVersion 4 vs 5 (the seed is behind)', 'It is not the stable release v4 either', 'commit aaaaaaa vs bbbbbbb',
                  '-AllowOlderCatalog covers only an older stable release', 'pwsh Scripts/Update-CatalogSeed.ps1'
}

Test-Case 'behind, and the release of its number cannot be read: not checked' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 4) | Out-Null
    Remove-Item -LiteralPath (Join-Path $f.Releases 'download' 'v4') -Recurse -Force
    Publish-CatalogRelease $f -Version 5 | Out-Null
    Assert-Result (Invoke-CatalogSeedCheck $f) 2 -Contains 'NOT checked', "could not download $($f.ReleaseUrl)/download/v4/manifest.json"
}

Test-Case 'ahead: a seed newer than stable is content the stable channel does not serve, and no flag ships it' {
    $f = New-CatalogFixture
    Publish-CatalogRelease $f -Version 5 | Out-Null
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 6 -NotLatest) | Out-Null
    Assert-Result (Invoke-CatalogSeedCheck $f) 4 `
        -Contains 'catalogVersion 6 vs 5 (the seed is ahead: content the stable channel does not serve)', '-AllowOlderCatalog covers only an older stable release'
}

Test-Case 'same version, other commit: a seed taken from a preview packed under the same number is no stable release' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 5 -Commit ('a' * 40)) | Out-Null
    Publish-CatalogRelease $f -Version 5 -Commit ('b' * 40) | Out-Null
    Assert-Result (Invoke-CatalogSeedCheck $f) 4 -Contains 'commit aaaaaaa vs bbbbbbb' -Lacks 'catalogVersion'
}

Test-Case 'same version and commit, other bytes: a re-packed release differs by its archive hash' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 5 -Content 'first pack') | Out-Null
    Publish-CatalogRelease $f -Version 5 -Content 'second pack' | Out-Null
    Assert-Result (Invoke-CatalogSeedCheck $f) 4 -Contains 'archive sha256' -Lacks 'catalogVersion', 'commit 0000'
}

Test-Case 'a Preview seed manifest over the stable bytes is no stable seed: the SDK records its channel (exit 4)' {
    $f = New-CatalogFixture
    $seed = Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 5) -Uncommitted
    $manifest = Get-Content -LiteralPath (Join-Path $seed 'manifest.json') -Raw | ConvertFrom-Json
    $manifest.channel = 'Preview'
    Set-Content -LiteralPath (Join-Path $seed 'manifest.json') -Value ($manifest | ConvertTo-Json -Depth 5) -NoNewline
    Invoke-FixtureGit $f.Installer @('add', '--all'); Invoke-FixtureGit $f.Installer @('commit', '--quiet', '-m', 'preview seed')
    Assert-Result (Invoke-CatalogSeedCheck $f) 4 -Contains 'is a Preview manifest', 'The seed is always a stable catalog', '-AllowOlderCatalog covers only'
}

Test-Case 'the same version, commit and archive under another pack time is not the release (exit 4)' {
    $f = New-CatalogFixture
    $seed = Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 5) -Uncommitted
    $path = Join-Path $seed 'manifest.json'
    Set-Content -LiteralPath $path -Value ((Get-Content -LiteralPath $path -Raw) -replace '2026-09-25T14:15:43', '2027-01-01T00:00:00') -NoNewline
    Invoke-FixtureGit $f.Installer @('add', '--all'); Invoke-FixtureGit $f.Installer @('commit', '--quiet', '-m', 'hand-made seed')
    Assert-Result (Invoke-CatalogSeedCheck $f) 4 -Contains 'the manifest itself is not the release', '-AllowOlderCatalog covers only'
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

Test-Case 'what git writes to stderr while it exits 0 is not an uncommitted change' {
    # A CRLF warning during the index refresh, a deprecation notice, or a trace: none of them is a file.
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 5) | Out-Null
    $saved = $env:GIT_TRACE
    $env:GIT_TRACE = '1'
    try { $r = Invoke-CatalogSeedCheck $f } finally { $env:GIT_TRACE = $saved }
    Assert-Result $r 0 -Contains 'is the latest stable catalog'
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
    $old = Publish-CatalogRelease $f -Version 4 -Content 'older'
    $oldSha = (Get-FileHash -LiteralPath (Join-Path $old 'catalog.zip') -Algorithm SHA256).Hash
    $pack = Publish-CatalogRelease $f -Version 5
    $sha = (Get-FileHash -LiteralPath (Join-Path $pack 'catalog.zip') -Algorithm SHA256).Hash   # upper-case, as Get-FileHash prints it
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', "5 $(('0' * 7 + '5') * 5) $sha")) 0 -Contains 'Catalog seed v5 (0000000) is the latest stable catalog.'
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', "4,$(('0' * 7 + '4') * 5),$oldSha")) 3 -Contains 'The seed given is not the latest stable catalog', 'catalogVersion 4 vs 5', 'It is the older stable release v4' -Lacks 'Update-CatalogSeed'
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', "4,$(('0' * 7 + '4') * 5),$sha")) 4 -Contains 'It is not the stable release v4 either'
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', "6 $(('0' * 7 + '6') * 5) $sha")) 4 -Contains 'the seed is ahead'
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', "5 00000005 $sha")) 2 -Contains 'NOT checked', '-Expect commit'
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', "5 $sha")) 2 -Contains 'NOT checked', '-Expect takes three values'
}

Complete-Tests
