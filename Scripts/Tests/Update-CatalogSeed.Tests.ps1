#Requires -Version 7.2
# Tests for Scripts/Update-CatalogSeed.ps1. Run: pwsh -NoProfile -File Scripts/Tests/Update-CatalogSeed.Tests.ps1
# Exit code = number of failed cases. CI runs every Scripts/Tests/*.Tests.ps1.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestKit.ps1')

function Invoke-SeedUpdate($Fixture, [string[]]$ExtraArgs = @()) {
    $script = Join-Path $PSScriptRoot '..' 'Update-CatalogSeed.ps1'
    $output = & pwsh -NoProfile -File $script -RepoRoot $Fixture.Installer -ReleaseBase $Fixture.ReleaseUrl @ExtraArgs 2>&1
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = ($output | ForEach-Object { "$_" }) -join "`n" }
}

function Get-SeedHashes($Fixture) {
    $seed = Join-Path $Fixture.Installer 'DiffusionNexus.Installer.Electron' 'Assets' 'Catalog'
    @('manifest.json', 'catalog.zip' | ForEach-Object {
        $file = Join-Path $seed $_
        if (Test-Path -LiteralPath $file) { (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash } else { 'absent' }
    })
}

Test-Case 'latest: replaces both files with the release assets byte for byte, names the version, and the gate then passes' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 4) | Out-Null
    $pack = Publish-CatalogRelease $f -Version 5
    Assert-Result (Invoke-SeedUpdate $f) 0 -Contains 'Embedded catalog v5 (0000000, stable) under DiffusionNexus.Installer.Electron/Assets/Catalog',
        'git commit -m "chore(catalog): embed the v5 stable catalog seed"' -Lacks 'unchanged'
    $expected = @('manifest.json', 'catalog.zip' | ForEach-Object { (Get-FileHash -LiteralPath (Join-Path $pack $_) -Algorithm SHA256).Hash })
    Assert-Equal ((Get-SeedHashes $f) -join ' ') ($expected -join ' ') 'seed files after the update'
    Invoke-FixtureGit $f.Installer @('add', '--all'); Invoke-FixtureGit $f.Installer @('commit', '--quiet', '-m', 'seed')
    Assert-Result (Invoke-CatalogSeedCheck $f) 0
}

Test-Case '-Version fetches that stable tag instead of latest: a deliberate hold-back' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 3) | Out-Null
    $pack4 = Publish-CatalogRelease $f -Version 4
    Publish-CatalogRelease $f -Version 5 | Out-Null
    Assert-Result (Invoke-SeedUpdate $f -ExtraArgs @('-Version', '4')) 0 -Contains 'Embedded catalog v4 (0000000, stable)', "$($f.ReleaseUrl)/download/v4"
    Assert-Equal (Get-SeedHashes $f)[1] (Get-FileHash -LiteralPath (Join-Path $pack4 'catalog.zip') -Algorithm SHA256).Hash 'catalog.zip'
}

Test-Case 'a seed that is already current is written all the same and reported unchanged' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 5) | Out-Null
    $before = Get-SeedHashes $f
    Assert-Result (Invoke-SeedUpdate $f) 0 -Contains 'Embedded catalog v5', 'unchanged'
    Assert-Equal ((Get-SeedHashes $f) -join ' ') ($before -join ' ') 'seed files'
}

Test-Case 'an archive that does not match its manifest replaces nothing' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 4) | Out-Null
    $before = Get-SeedHashes $f
    $pack = Publish-CatalogRelease $f -Version 5
    Set-Content -LiteralPath (Join-Path $pack 'catalog.zip') -Value 'truncated download'
    Assert-Result (Invoke-SeedUpdate $f) 2 -Contains 'NOT updated', 'does not match its manifest', 'Nothing was replaced'
    Assert-Equal ((Get-SeedHashes $f) -join ' ') ($before -join ' ') 'seed files'
}

Test-Case 'a download that fails replaces nothing, and names the URL' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 4) | Out-Null
    $before = Get-SeedHashes $f
    Assert-Result (Invoke-SeedUpdate $f -ExtraArgs @('-Version', '9')) 2 -Contains 'NOT updated', "could not download $($f.ReleaseUrl)/download/v9/manifest.json"
    Assert-Equal ((Get-SeedHashes $f) -join ' ') ($before -join ' ') 'seed files'
}

Test-Case 'a Preview manifest is refused: the seed is always a stable catalog' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 5) | Out-Null
    $before = Get-SeedHashes $f
    Publish-CatalogRelease $f -Version 6 -Channel 'Preview' | Out-Null
    Assert-Result (Invoke-SeedUpdate $f) 2 -Contains 'NOT updated', 'is a Preview manifest'
    Assert-Equal ((Get-SeedHashes $f) -join ' ') ($before -join ' ') 'seed files'
}

Test-Case 'a tag whose manifest says another version replaces nothing' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 4) | Out-Null
    $before = Get-SeedHashes $f
    $pack = Publish-CatalogRelease $f -Version 5
    Write-CatalogManifest $pack -Version 7 -Commit ('c' * 40) -Channel 'Stable' -Sha256 (Get-FileHash -LiteralPath (Join-Path $pack 'catalog.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Result (Invoke-SeedUpdate $f -ExtraArgs @('-Version', '5')) 2 -Contains 'NOT updated', 'says catalogVersion 7, not 5'
    Assert-Equal ((Get-SeedHashes $f) -join ' ') ($before -join ' ') 'seed files'
}

Test-Case 'a folder that is not the installer repo is refused before anything is downloaded' {
    $f = New-CatalogFixture
    Publish-CatalogRelease $f -Version 5 | Out-Null
    Assert-Result (Invoke-SeedUpdate $f) 2 -Contains 'NOT updated', 'does not exist. Is', 'the installer repo'
}

Complete-Tests
