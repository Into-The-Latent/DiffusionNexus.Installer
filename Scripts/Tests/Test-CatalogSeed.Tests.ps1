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
    Assert-Result (Invoke-CatalogSeedCheck $f) 4 -Contains 'generatedAt 2027-01-01T00:00:00', '-AllowOlderCatalog covers only'
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
    # The five values build-info.json reports: version, commit, archive sha256, channel, generatedAt.
    $f = New-CatalogFixture
    $packed = '2026-09-25T14:15:43.8266732+00:00'   # what Write-CatalogManifest stamps
    $old = Publish-CatalogRelease $f -Version 4 -Content 'older'
    $oldSha = (Get-FileHash -LiteralPath (Join-Path $old 'catalog.zip') -Algorithm SHA256).Hash
    $pack = Publish-CatalogRelease $f -Version 5
    $sha = (Get-FileHash -LiteralPath (Join-Path $pack 'catalog.zip') -Algorithm SHA256).Hash   # upper-case, as Get-FileHash prints it
    $c4 = ('0' * 7 + '4') * 5; $c5 = ('0' * 7 + '5') * 5
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', "5 $c5 $sha Stable $packed")) 0 -Contains 'Catalog seed v5 (0000000) is the latest stable catalog.'
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', "5 $c5 $sha Stable 2026-09-25T16:15:43.8266732+02:00")) 0   # the same instant
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', "4,$c4,$oldSha,Stable,$packed")) 3 -Contains 'The seed given is not the latest stable catalog', 'catalogVersion 4 vs 5', 'It is the older stable release v4' -Lacks 'Update-CatalogSeed'
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', "4,$c4,$sha,Stable,$packed")) 4 -Contains 'It is not the stable release v4 either'
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', "6 $(('0' * 7 + '6') * 5) $sha Stable $packed")) 4 -Contains 'the seed is ahead'
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', "5 $c5 $sha Preview $packed")) 4 -Contains 'is a Preview seed', 'The seed is always a stable catalog'
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', "5 $c5 $sha Stable 2027-01-01T00:00:00+00:00")) 4 -Contains 'generatedAt 2027-01-01T00:00:00'
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', "5 00000005 $sha Stable $packed")) 2 -Contains 'NOT checked', '-Expect', 'commit'
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', "5 $c5 $sha Stable yesterday")) 2 -Contains 'NOT checked', 'generatedAt'
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', "5 $c5 $sha")) 2 -Contains 'NOT checked', '-Expect takes five values'
}

Test-Case 'the fixture release server moves to another port when its port is taken before it binds' {
    # Another process can take the probed port between the probe and the bind, on a busy runner.
    $holder = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $holder.Start()
    try {
        $taken = $holder.LocalEndpoint.Port
        $root = Join-Path ([IO.Path]::GetTempPath()) ("relsrv-" + [guid]::NewGuid().ToString('N'))
        $script:FixtureRoots.Add($root)
        New-Item -ItemType Directory -Path (Join-Path $root 'releases') -Force | Out-Null
        # A free port it is given is the port it takes: -Port is the first attempt. Probed afresh on each
        # try, because another process may take it first (and the server then rightly moves on); a server
        # that ignored -Port would miss on every try.
        $exact = $false
        for ($try = 1; $try -le 5 -and -not $exact; $try++) {
            $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
            $probe.Start(); $free = $probe.LocalEndpoint.Port; $probe.Stop()
            $exact = (Start-ReleaseServer $root -Port $free).Url -eq "http://127.0.0.1:$free/releases"
        }
        if (-not $exact) { throw 'a free port passed as -Port was not the port taken, in 5 tries' }
        $server = Start-ReleaseServer $root -Port $taken
        Assert-Like $server.Url 'http://127.0.0.1:*/releases' 'url'
        if ($server.Url -like "*:$taken/*") { throw "the server claims the taken port $taken" }
        $status = try { (Invoke-WebRequest -Uri "$($server.Url)/download/v1/manifest.json" -SkipHttpErrorCheck -TimeoutSec 10).StatusCode } catch { $_.Exception.Message }
        Assert-Equal $status 404 'a missing asset from the moved server'
    } finally { $holder.Stop() }
}

Test-Case 'Step 1c reads build-info''s catalogSeed and names every field that differs, sha256 included' {
    . (Join-Path $PSScriptRoot '..' 'CatalogRelease.ps1')
    $c = 'a' * 40; $sha = 'b' * 64; $other = 'c' * 64
    $info = "{ ""app"": ""3.0.99"", ""catalogSeed"": { ""version"": 5, ""commit"": ""$c"", ""sha256"": ""$sha"", ""channel"": ""Stable"", ""generatedAt"": ""2026-09-25T14:15:43.8266732+00:00"" } }"
    $packaged = Read-BuildInfoSeed $info 'build-info.json'
    Assert-Equal $packaged.Version 5 'version'
    Assert-Equal $packaged.GeneratedAt.UtcDateTime.ToString('o') '2026-09-25T14:15:43.8266732Z' 'generatedAt, read as text whatever the culture'
    Assert-Equal (@(Compare-CatalogSeed $packaged (New-CatalogSeed 5 $c $sha Stable '2026-09-25T16:15:43.8266732+02:00' 'x')).Count) 0 'differences from the same seed'
    $lines = @(Compare-CatalogSeed $packaged (New-CatalogSeed 5 $c $other Preview '2027-01-01T00:00:00+00:00' 'x')) -join "`n"
    Assert-Like $lines "*archive sha256 $sha vs $other*channel Stable vs Preview*generatedAt 2026-09-25T14:15:43.8266732+00:00 vs 2027-01-01T00:00:00.0000000+00:00*" 'differences'
    $missing = { Read-BuildInfoSeed '{ "app": "3.0.99" }' 'build-info.json' }
    try { & $missing; throw 'expected a refusal' } catch { Assert-Like $_.Exception.Message '*build-info.json reports no catalogSeed*' 'no seed' }
}

Test-Case 'Step 1c keeps the packaged app''s refusal reason from stderr, and only stdout as the answer' {
    . (Join-Path $PSScriptRoot '..' 'CatalogRelease.ps1')
    $dir = Join-Path ([IO.Path]::GetTempPath()) ("buildinfo " + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $script:FixtureRoots.Add($dir)
    New-Item -ItemType Directory -Path $dir | Out-Null
    $refuses = Join-Path $dir 'refuses.cmd'
    Set-Content -LiteralPath $refuses -Value '@echo off', 'echo The embedded catalog.zip has sha256 x; its manifest says y. 1>&2', 'exit /b 1'
    try { Get-BuildInfoText $refuses; throw 'expected a refusal' }
    catch { Assert-Like $_.Exception.Message '*did not answer --build-info (exit 1)*its manifest says y.*' 'the refusal' }
    $answers = Join-Path $dir 'answers.cmd'
    Set-Content -LiteralPath $answers -Value '@echo off', 'echo a warning 1>&2', 'echo {"app": "3.0.99"}', 'exit /b 0'
    Assert-Equal (Get-BuildInfoText $answers) '{"app": "3.0.99"}' 'the answer, without what went to stderr'
}

Complete-Tests
