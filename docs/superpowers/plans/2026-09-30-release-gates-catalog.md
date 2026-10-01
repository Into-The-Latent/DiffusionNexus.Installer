# Release Gates, Catalog Half (#38) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `New-Release.ps1` refuses, before it changes anything, when the embedded catalog seed is not the latest stable catalog release; `Update-CatalogSeed.ps1` replaces the hand-made seed commit; the packaged app reports the seed it carries in `build-info.json` and the release notes name it.

**Architecture:** Two new scripts under `Scripts/` (`Test-CatalogSeed.ps1`, the gate; `Update-CatalogSeed.ps1`, the write half) share one dot-sourced helper (`CatalogRelease.ps1`: where the releases are, one download function, one manifest reader) that `New-Release.ps1` uses too for Step 1c. The tests serve a fixture "GitHub Releases" folder over a local `HttpListener`, so the download path under test is the real one (`Invoke-WebRequest`, redirects, 404s). `BuildInfo` gains `catalogSeed`, read from the embedded `manifest.json` with the SDK's own `CatalogManifest.Parse`.

**Tech Stack:** pwsh 7.2+ (`Microsoft.PowerShell.ThreadJob`, `Compress-Archive`, `System.Net.HttpListener`), git, xunit + FluentAssertions, GitHub Actions windows-latest.

**Spec:** `docs/superpowers/specs/2026-09-30-release-and-update-channels-design.md` sections 4.3, 4.4, 4.5 (`catalogSeed`), 4.6 and 9. Issue: Into-The-Latent/DiffusionNexus.Installer #38. Depends on #37 (draft PR #40, branch `feature/release-gates-sdk`): this plan's branch stacks on it.

## Global Constraints

- Exit codes of the gate: **0** equal, **3** differs (overridable with `-AllowOlderCatalog`), **2** not checked (never overridable). Never 1; a `trap` maps unexpected errors to 2. `Update-CatalogSeed.ps1`: 0 done, 2 nothing replaced.
- The seed must **equal** the latest stable release: same `catalogVersion`, `commit` and `archive.sha256`; the zip's real sha256 must equal its own manifest's. Not "not older".
- Exit 2 for: the release cannot be downloaded or is not a Stable manifest, the seed cannot be read, the zip disagrees with its manifest, `git status --porcelain -- DiffusionNexus.Installer.Electron/Assets/Catalog` is not empty.
- `-Expect <version> <commit> <sha256>` judges the three values given and reads nothing on disk.
- The manifest comes from `<releases>/latest/download/manifest.json` with no token. `<releases>` is `https://github.com/Into-The-Latent/DiffusionNexus.Catalog/releases`, or `-ReleaseBase`, or `$env:DIFFUSIONNEXUS_CATALOG_RELEASES` (the tests point it at their fixture; the URL used is always printed).
- Step 0d runs after 0c and before the version write. Step 1c requires `catalogSeed` in the packaged app's answer to equal what 0d judged. `-AllowOlderCatalog` overrides exit 3 only.
- Release notes end with `Built with Installer SDK X` and `Bundled catalog vN (stable)`; nothing reads them back.
- Every file access by `-LiteralPath`; fixture paths carry a space, an apostrophe and `[x]`. No path is ever pasted into `-Command` text.
- pwsh 7.2+, no Pester. Branch `feature/release-gates-catalog` off `feature/release-gates-sdk`; one PR, base `feature/release-gates-sdk`.
- Line endings: index LF, worktree CRLF. Before each commit `git diff --cached --numstat` must equal `git diff --cached -w --numstat`.
- Pushing needs the Into-The-Latent token: `git -c credential.helper= -c "credential.helper=!f() { echo username=Into-The-Latent; echo password=$(gh auth token --user Into-The-Latent); }; f" push ...`. Never switch the active `gh` account.

## Review Focus

1. **`releases/latest/download/...` is an HTTP redirect on GitHub**: the download must follow it, or every check is exit 2. Pinned in Task 1: the fixture server answers `latest/download/*` with a 302, and the match case goes through it.
2. **The seed manifest in the worktree has CRLF line endings** (autocrlf) or a BOM: the gate compares fields, never bytes, so that must not read as "differs". Pinned in Task 1 (`a seed manifest rewritten with CRLF still equals the release`).
3. **`-Expect` from a `build-info.json` with an upper-case sha256** or a short commit: the comparison is case-insensitive on hex, and a malformed value is exit 2, not 3. Pinned in Task 1 (`-Expect` cases).
4. **A stray `DIFFUSIONNEXUS_CATALOG_RELEASES` pointing nowhere** on the release machine: exit 2 naming the URL, never 3, so `-AllowOlderCatalog` cannot wave it through. Pinned in Task 1 (`download fails`) and Task 4 (`could not be checked` refuses with the flag).
5. **A leftover untracked file in the seed folder** (`manifest.json.bak`): uncommitted, exit 2. Pinned in Task 1 (`an untracked file next to the seed is uncommitted too`).

---

### Task 1: `CatalogRelease.ps1`, `Test-CatalogSeed.ps1`, the release server in the kit, test first

**Files:**
- Modify: `Scripts/Tests/TestKit.ps1` (release server, catalog packs, seed fixture, `Invoke-CatalogSeedCheck`)
- Create: `Scripts/Tests/Test-CatalogSeed.Tests.ps1`
- Create: `Scripts/CatalogRelease.ps1`
- Create: `Scripts/Test-CatalogSeed.ps1`

**Interfaces:**
- Produces (kit): `New-CatalogFixture` → `{Root, Installer, Releases, ReleaseUrl}`; `Add-CatalogReleases $f` (adds `Releases`/`ReleaseUrl` to any fixture and starts its server); `Publish-CatalogRelease $f -Version N [-Commit] [-Channel Stable] [-Content] [-NotLatest]` → the pack folder; `Set-CatalogSeed $f -From <pack folder> [-Uncommitted]` → the seed folder (commits it unless `-Uncommitted`); `Invoke-CatalogSeedCheck $f [-ExtraArgs]` → `{ExitCode, Text}`; `Complete-Tests` stops the servers.
- Produces (helper, dot-sourced): `$CatalogSeedFolder = 'DiffusionNexus.Installer.Electron/Assets/Catalog'`, `Get-CatalogReleaseBase [given]` → URL without trailing slash, `Save-CatalogAsset <url> <path>` (throws naming the URL), `Read-CatalogManifest <text> <what>` → `{Version:int, Commit, Sha256 (lower), Channel, Short}` (throws naming what is wrong).
- Produces: `pwsh -NoProfile -File Scripts/Test-CatalogSeed.ps1 [-RepoRoot] [-ReleaseBase] [-Expect v,commit,sha]` → exit 0/3/2. Task 4 switches on it; #30 uses `-Expect`.

- [ ] **Step 1: Branch**

```powershell
git switch feature/release-gates-sdk; git pull; git switch -c feature/release-gates-catalog
```

- [ ] **Step 2: Kit additions** (append to `Scripts/Tests/TestKit.ps1`, before `Test-Case`)

```powershell
# ------------------------------------------------------------------ the catalog repo's releases
# What the seed scripts see of Into-The-Latent/DiffusionNexus.Catalog: a folder served over local
# HTTP, so the path under test is the real one (Invoke-WebRequest, a redirect, a 404), never a
# file copy standing in for a download.
#   <Releases>\download\vN\{manifest.json,catalog.zip}   = the assets of release vN
#   <Releases>\latest                                    = a text file naming the tag releases/latest
#                                                          redirects to, as GitHub does (302)
$script:ReleaseServers = [System.Collections.Generic.List[object]]::new()

function Start-ReleaseServer([string]$Root) {
    $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop()
    $listener = [System.Net.HttpListener]::new()
    $listener.Prefixes.Add("http://127.0.0.1:$port/")
    $listener.Start()
    $job = Start-ThreadJob -ArgumentList $listener, $Root {
        param($listener, $root)
        while ($listener.IsListening) {
            try { $context = $listener.GetContext() } catch { break }
            $response = $context.Response
            $path = $context.Request.Url.AbsolutePath
            if ($path -match '^/releases/latest/download/(.+)$' -and (Test-Path -LiteralPath (Join-Path $root 'releases' 'latest') -PathType Leaf)) {
                $tag = (Get-Content -LiteralPath (Join-Path $root 'releases' 'latest') -Raw).Trim()
                $response.StatusCode = 302
                $response.RedirectLocation = "/releases/download/$tag/$($Matches[1])"
            } else {
                $file = Join-Path $root ($path.TrimStart('/') -replace '/', [IO.Path]::DirectorySeparatorChar)
                if (Test-Path -LiteralPath $file -PathType Leaf) {
                    $bytes = [IO.File]::ReadAllBytes($file)
                    $response.ContentLength64 = $bytes.Length
                    $response.OutputStream.Write($bytes, 0, $bytes.Length)
                } else { $response.StatusCode = 404 }
            }
            $response.Close()
        }
    }
    $server = [pscustomobject]@{ Url = "http://127.0.0.1:$port/releases"; Listener = $listener; Job = $job }
    $script:ReleaseServers.Add($server)
    $server
}

function Add-CatalogReleases($Fixture) {
    $releases = Join-Path $Fixture.Root 'releases'
    New-Item -ItemType Directory -Force -Path $releases | Out-Null
    $server = Start-ReleaseServer $Fixture.Root
    $Fixture | Add-Member -NotePropertyName Releases -NotePropertyValue $releases -Force
    $Fixture | Add-Member -NotePropertyName ReleaseUrl -NotePropertyValue $server.Url -Force
    $Fixture
}

# A fixture with no SDK repos: the seed scripts read the installer checkout and the releases only.
function New-CatalogFixture {
    $root = Join-Path ([IO.Path]::GetTempPath()) ("sdkpin o'test [x]-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $script:FixtureRoots.Add($root)
    $fixture = [pscustomobject]@{ Root = $root; Installer = Join-Path $root 'installer' }
    New-Item -ItemType Directory -Path $fixture.Installer | Out-Null
    Add-CatalogReleases $fixture
}

# One packed release, as the catalog CI writes it: catalog.zip holding the content given, and the
# manifest with the zip's sha256. -Commit defaults to a sha derived from the version, so two
# versions never share one; two packs of the same version with different -Content share version
# and commit and differ in bytes, like a re-pack.
function Publish-CatalogRelease($Fixture, [int]$Version, [string]$Commit, [string]$Channel = 'Stable', [string]$Content, [switch]$NotLatest) {
    $dir = Join-Path $Fixture.Releases 'download' "v$Version"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $stage = Join-Path $dir 'stage'
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    if (-not $Content) { $Content = "catalog v$Version" }
    Set-Content -LiteralPath (Join-Path $stage 'catalog.json') -Value "{ `"schemaVersion`": 1, `"content`": `"$Content`" }"
    $zip = Join-Path $dir 'catalog.zip'
    Compress-Archive -LiteralPath (Join-Path $stage 'catalog.json') -DestinationPath $zip -Force
    Remove-Item -LiteralPath $stage -Recurse -Force
    if (-not $Commit) { $Commit = ('{0:x8}' -f $Version) * 5 }
    Write-CatalogManifest $dir -Version $Version -Commit $Commit -Channel $Channel -Sha256 (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $NotLatest) { Set-Content -LiteralPath (Join-Path $Fixture.Releases 'latest') -Value "v$Version" -NoNewline }
    $dir
}

function Write-CatalogManifest([string]$Dir, [int]$Version, [string]$Commit, [string]$Channel, [string]$Sha256) {
    $manifest = [ordered]@{
        schemaVersion = 1; catalogVersion = $Version; channel = $Channel; commit = $Commit
        generatedAt = '2026-09-25T14:15:43.8266732+00:00'
        archive = [ordered]@{ name = 'catalog.zip'; sha256 = $Sha256; bytes = (Get-Item -LiteralPath (Join-Path $Dir 'catalog.zip')).Length }
        workloads = @(); workflows = @()
    }
    Set-Content -LiteralPath (Join-Path $Dir 'manifest.json') -Value ($manifest | ConvertTo-Json -Depth 5) -NoNewline
}

# The installer checkout of the fixture carrying the seed the way the real one does: a git repo with
# both files committed under DiffusionNexus.Installer.Electron/Assets/Catalog (the gate refuses an
# uncommitted seed, so -Uncommitted is the case, not the default).
function Set-CatalogSeed($Fixture, [string]$From, [switch]$Uncommitted) {
    $seed = Join-Path $Fixture.Installer 'DiffusionNexus.Installer.Electron' 'Assets' 'Catalog'
    New-Item -ItemType Directory -Force -Path $seed | Out-Null
    Copy-Item -LiteralPath (Join-Path $From 'manifest.json'), (Join-Path $From 'catalog.zip') -Destination $seed -Force
    if (-not (Test-Path -LiteralPath (Join-Path $Fixture.Installer '.git'))) {
        Invoke-FixtureGit $Fixture.Installer @('init', '--quiet', '--initial-branch=main')
    }
    if (-not $Uncommitted) {
        Invoke-FixtureGit $Fixture.Installer @('add', '--', 'DiffusionNexus.Installer.Electron/Assets/Catalog')
        Invoke-FixtureGit $Fixture.Installer @('commit', '--quiet', '-m', 'chore(catalog): embed the seed')
    }
    $seed
}

# Runs Test-CatalogSeed.ps1 in a child pwsh, exactly as New-Release.ps1 does.
function Invoke-CatalogSeedCheck($Fixture, [string[]]$ExtraArgs = @()) {
    $check = Join-Path $PSScriptRoot '..' 'Test-CatalogSeed.ps1'
    $output = & pwsh -NoProfile -File $check -RepoRoot $Fixture.Installer -ReleaseBase $Fixture.ReleaseUrl @ExtraArgs 2>&1
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = ($output | ForEach-Object { "$_" }) -join "`n" }
}
```

And in `Complete-Tests`, before the fixture roots are removed:

```powershell
    foreach ($server in $script:ReleaseServers) {
        $server.Listener.Stop(); $server.Listener.Close()
        Remove-Job -Job $server.Job -Force -ErrorAction SilentlyContinue
    }
```

- [ ] **Step 3: Write the failing tests** `Scripts/Tests/Test-CatalogSeed.Tests.ps1`

```powershell
#Requires -Version 7.2
# Tests for Scripts/Test-CatalogSeed.ps1. Run: pwsh -NoProfile -File Scripts/Tests/Test-CatalogSeed.Tests.ps1
# Exit code = number of failed cases. CI runs every Scripts/Tests/*.Tests.ps1.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestKit.ps1')

Test-Case 'equal: the seed is the latest stable release, reached through the releases/latest redirect' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 5) | Out-Null
    Assert-Result (Invoke-CatalogSeedCheck $f) 0 -Contains 'Catalog seed v5 (00000005) is the latest stable catalog.'
}

Test-Case 'behind: names both versions, the fix command, and where the release came from' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 4) | Out-Null
    Publish-CatalogRelease $f -Version 5 | Out-Null
    Assert-Result (Invoke-CatalogSeedCheck $f) 3 `
        -Contains 'is not the latest stable catalog (v5, 00000005', 'catalogVersion 4 vs 5 (the seed is behind)',
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
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-ReleaseBase', 'http://127.0.0.1:1/releases')) 2 -Contains 'could not download http://127.0.0.1:1/releases/latest/download/manifest.json'
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

Test-Case 'a seed manifest rewritten with CRLF still equals the release: fields are compared, not bytes' {
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
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', '5', ('0' * 7 + '5') * 5, $sha)) 0 -Contains 'Catalog seed v5 (00000005) is the latest stable catalog.'
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', '4', ('0' * 7 + '4') * 5, $sha)) 3 -Contains 'The seed given is not the latest stable catalog', 'catalogVersion 4 vs 5' -Lacks 'Update-CatalogSeed'
    Assert-Result (Invoke-CatalogSeedCheck $f -ExtraArgs @('-Expect', '5', '00000005', $sha)) 2 -Contains 'NOT checked', '-Expect commit'
}

Complete-Tests
```

- [ ] **Step 4: Run, expect every case to fail** (the script does not exist)

Run: `pwsh -NoProfile -File Scripts/Tests/Test-CatalogSeed.Tests.ps1`
Expected: `0 passed, 14 failed`, each with "exit code 1" or the missing-file error.

- [ ] **Step 5: `Scripts/CatalogRelease.ps1`**

```powershell
# Shared by Test-CatalogSeed.ps1, Update-CatalogSeed.ps1 and New-Release.ps1 - dot-source it.
# Where the catalog is published, how one asset is fetched, and what a manifest has to say.

# Where the embedded seed lives, relative to the installer repo: the two files the Electron project
# embeds as catalog.zip and manifest.json.
$CatalogSeedFolder = 'DiffusionNexus.Installer.Electron/Assets/Catalog'

# The catalog repo's releases. releases/latest is GitHub's own "newest full release" redirect, which
# never names a pre-release, so <base>/latest/download/manifest.json is the stable channel's manifest.
$DefaultCatalogReleases = 'https://github.com/Into-The-Latent/DiffusionNexus.Catalog/releases'

function Get-CatalogReleaseBase([string]$Given) {
    $base = if ($Given) { $Given } elseif ($env:DIFFUSIONNEXUS_CATALOG_RELEASES) { $env:DIFFUSIONNEXUS_CATALOG_RELEASES } else { $DefaultCatalogReleases }
    $base.TrimEnd('/')
}

# One release asset to a file. Public, no token. Redirects are followed (releases/latest is one).
# Throws naming the URL on any failure: no server, a 404, a timeout.
function Save-CatalogAsset([string]$Url, [string]$Path) {
    try { Invoke-WebRequest -Uri $Url -OutFile $Path -TimeoutSec 60 | Out-Null }
    catch { throw "could not download $Url ($($_.Exception.Message))" }
}

# The fields the seed gate compares, from a manifest's text: catalogVersion, the catalog commit,
# the archive's sha256 (lower-cased; hex is case-insensitive) and the channel. Throws naming $What
# when the text is not that.
function Read-CatalogManifest([string]$Text, [string]$What) {
    try { $json = $Text | ConvertFrom-Json }
    catch { throw "$What is not JSON ($($_.Exception.Message))" }
    $version = "$($json.catalogVersion)"; $commit = "$($json.commit)"; $sha = "$($json.archive.sha256)"
    if ($version -notmatch '^\d+$') { throw "$What has no catalogVersion (got '$version')." }
    if ($commit -notmatch '^[0-9a-fA-F]{40}$') { throw "$What has no 40-digit commit (got '$commit')." }
    if ($sha -notmatch '^[0-9a-fA-F]{64}$') { throw "$What has no archive.sha256 (got '$sha')." }
    [pscustomobject]@{
        Version = [int]$version; Commit = $commit.ToLowerInvariant(); Sha256 = $sha.ToLowerInvariant()
        Channel = "$($json.channel)"; Short = $commit.Substring(0, 7).ToLowerInvariant()
    }
}
```

- [ ] **Step 6: `Scripts/Test-CatalogSeed.ps1`**

```powershell
<#
.SYNOPSIS
    Fails when the embedded catalog seed is not the latest stable catalog release.

.DESCRIPTION
    The installer embeds a catalog (DiffusionNexus.Installer.Electron/Assets/Catalog: catalog.zip
    and manifest.json) so a fresh machine has a workload list before it reaches the network. Until
    now a manual commit before each release kept it current, and nothing checked it. A seed ahead
    of the stable release ships content the stable channel does not serve; one behind it ships
    stale content to every fresh install. Under SDK 2.0.0 the two could ping-pong with the update
    check (docs/manual-smoke.md, section 1.4-1.6). This script refuses both, and New-Release.ps1
    runs it before it builds anything. A Preview build must pass it too: promotion never rebuilds,
    so a promoted binary must not carry a preview seed to Stable users.

    Equal means: the seed's catalogVersion, commit and archive.sha256 are the latest stable
    manifest's, and catalog.zip really has that sha256. The manifest is downloaded from
    <ReleaseBase>/latest/download/manifest.json; public, no token.

    Exit codes - New-Release.ps1 and Promote-Release.ps1 rely on them:
      0  the seed is the latest stable catalog
      3  it differs. What differs is listed, with the fix: pwsh Scripts/Update-CatalogSeed.ps1,
         then commit.
      2  it could not be checked: the release could not be downloaded or is not a Stable manifest,
         the seed is missing or not a manifest, catalog.zip disagrees with its own manifest, or the
         seed folder has uncommitted changes (a release must never carry what no commit records).
         Any unexpected error lands here too, never on 3, so it can never be waved through with
         -AllowOlderCatalog. (1 is left to pwsh, which uses it for a script that does not parse.)

.PARAMETER RepoRoot
    The installer repo. Defaults to the folder above this script.

.PARAMETER ReleaseBase
    The catalog repo's releases page. Default: DIFFUSIONNEXUS_CATALOG_RELEASES when set (the
    script tests serve a fixture there), else
    https://github.com/Into-The-Latent/DiffusionNexus.Catalog/releases. The URL read is printed
    with the answer, so a redirected check is never mistaken for the real one.

.PARAMETER Expect
    Three values - version, commit, sha256 - to judge instead of the working tree's seed.
    Promote-Release.ps1 passes what a shipped build's build-info.json says. Nothing on disk is read.

.EXAMPLE
    pwsh Scripts/Test-CatalogSeed.ps1

.EXAMPLE
    pwsh Scripts/Test-CatalogSeed.ps1 -Expect 5, 51e1684cfa48d22344e82e7037e8e97018be72bd, e7d3ee7a...
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$ReleaseBase,
    [ValidateCount(3, 3)][string[]]$Expect
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

trap {
    Write-Host "Catalog seed NOT checked: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}

function Stop-Unchecked([string]$Reason) {
    Write-Host "Catalog seed NOT checked: $Reason" -ForegroundColor Red
    exit 2
}

. (Join-Path $PSScriptRoot 'CatalogRelease.ps1')
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$manifestUrl = "$(Get-CatalogReleaseBase $ReleaseBase)/latest/download/manifest.json"

# ------------------------------------------------------------------------ the seed under judgement
if ($Expect) {
    if ($Expect[0] -notmatch '^\d+$') { Stop-Unchecked "-Expect version '$($Expect[0])' is not a number." }
    if ($Expect[1] -notmatch '^[0-9a-fA-F]{40}$') { Stop-Unchecked "-Expect commit '$($Expect[1])' is not a 40-digit commit." }
    if ($Expect[2] -notmatch '^[0-9a-fA-F]{64}$') { Stop-Unchecked "-Expect sha256 '$($Expect[2])' is not a sha256." }
    $seed = [pscustomobject]@{ Version = [int]$Expect[0]; Commit = $Expect[1].ToLowerInvariant(); Sha256 = $Expect[2].ToLowerInvariant(); Short = $Expect[1].Substring(0, 7).ToLowerInvariant() }
    $seedName = 'The seed given'
    $fix = 'The build that carries this seed does not bundle the latest stable catalog.'
} else {
    # -LiteralPath throughout: a checkout under a folder with [ ] in its name must not read as "no seed".
    $seedDir = Join-Path $RepoRoot $CatalogSeedFolder
    $manifestPath = Join-Path $seedDir 'manifest.json'
    $zipPath = Join-Path $seedDir 'catalog.zip'
    foreach ($file in $manifestPath, $zipPath) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { Stop-Unchecked "$file does not exist." }
    }
    # Uncommitted seed files must never ship: a release records a commit, and the seed in it would
    # not be the seed built. An untracked file in the folder counts too.
    $status = @(& git -C $RepoRoot status --porcelain -- $CatalogSeedFolder 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) { Stop-Unchecked "git status in $RepoRoot failed:`n$($status -join "`n")" }
    if ($status.Count -gt 0) {
        Stop-Unchecked "the seed under $CatalogSeedFolder has uncommitted changes:`n$(($status | ForEach-Object { "  $_" }) -join "`n")`nCommit them (or restore the files) and run the check again."
    }
    $seed = Read-CatalogManifest (Get-Content -LiteralPath $manifestPath -Raw) "the embedded seed manifest $manifestPath"
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($zipHash -ne $seed.Sha256) {
        Stop-Unchecked "$zipPath does not match its manifest: its sha256 is $zipHash, the manifest says $($seed.Sha256). Run pwsh Scripts/Update-CatalogSeed.ps1 to replace both files, then commit."
    }
    $seedName = 'The embedded seed'
    $fix = "-> pwsh Scripts/Update-CatalogSeed.ps1, then commit $CatalogSeedFolder."
}

# ------------------------------------------------------------------------ the latest stable release
$temp = Join-Path ([IO.Path]::GetTempPath()) ("catalogseed-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $remotePath = Join-Path $temp 'manifest.json'
    try { Save-CatalogAsset $manifestUrl $remotePath } catch { Stop-Unchecked $_.Exception.Message }
    try { $remote = Read-CatalogManifest (Get-Content -LiteralPath $remotePath -Raw) "the latest stable manifest ($manifestUrl)" }
    catch { Stop-Unchecked $_.Exception.Message }
} finally { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
if ($remote.Channel -ne 'Stable') { Stop-Unchecked "$manifestUrl is a $($remote.Channel) manifest, not a Stable one." }

# ---------------------------------------------------------------------------------- the verdict
$differences = @(
    if ($seed.Version -ne $remote.Version) {
        $direction = if ($seed.Version -lt $remote.Version) { 'the seed is behind' } else { 'the seed is ahead: content the stable channel does not serve' }
        "catalogVersion $($seed.Version) vs $($remote.Version) ($direction)"
    }
    if ($seed.Commit -ne $remote.Commit) { "commit $($seed.Short) vs $($remote.Short)" }
    if ($seed.Sha256 -ne $remote.Sha256) { "archive sha256 $($seed.Sha256.Substring(0, 12))... vs $($remote.Sha256.Substring(0, 12))..." }
)
if ($differences.Count -eq 0) {
    Write-Host "Catalog seed v$($seed.Version) ($($seed.Short)) is the latest stable catalog. ($manifestUrl)" -ForegroundColor Green
    exit 0
}
Write-Host "$seedName is not the latest stable catalog (v$($remote.Version), $($remote.Short), $manifestUrl):" -ForegroundColor Yellow
$differences | ForEach-Object { Write-Host "  $_" }
Write-Host $fix -ForegroundColor Yellow
exit 3
```

- [ ] **Step 7: Run, expect all green**

Run: `pwsh -NoProfile -File Scripts/Tests/Test-CatalogSeed.Tests.ps1`
Expected: `14 passed, 0 failed`. Also `pwsh -NoProfile -File Scripts/Tests/Test-SdkPin.Tests.ps1` still `31 passed`.

- [ ] **Step 8: Commit**

```
git add Scripts/CatalogRelease.ps1 Scripts/Test-CatalogSeed.ps1 Scripts/Tests/TestKit.ps1 Scripts/Tests/Test-CatalogSeed.Tests.ps1
git commit -m "feat(release): Test-CatalogSeed.ps1 - the embedded seed must equal the latest stable catalog"
```

---

### Task 2: `Update-CatalogSeed.ps1`, test first

**Files:**
- Create: `Scripts/Tests/Update-CatalogSeed.Tests.ps1`
- Create: `Scripts/Update-CatalogSeed.ps1`

**Interfaces:**
- Consumes: `CatalogRelease.ps1` (Task 1), the kit's `New-CatalogFixture`, `Publish-CatalogRelease`, `Set-CatalogSeed`, `Invoke-CatalogSeedCheck`.
- Produces: `pwsh -NoProfile -File Scripts/Update-CatalogSeed.ps1 [-RepoRoot] [-ReleaseBase] [-Version N]` → exit 0 with `Embedded catalog vN (<short>, stable) under DiffusionNexus.Installer.Electron/Assets/Catalog` and the commit command, or 2 with nothing replaced.

- [ ] **Step 1: Write the failing tests**

```powershell
#Requires -Version 7.2
# Tests for Scripts/Update-CatalogSeed.ps1. Run: pwsh -NoProfile -File Scripts/Tests/Update-CatalogSeed.Tests.ps1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestKit.ps1')

function Invoke-SeedUpdate($Fixture, [string[]]$ExtraArgs = @()) {
    $script = Join-Path $PSScriptRoot '..' 'Update-CatalogSeed.ps1'
    $output = & pwsh -NoProfile -File $script -RepoRoot $Fixture.Installer -ReleaseBase $Fixture.ReleaseUrl @ExtraArgs 2>&1
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = ($output | ForEach-Object { "$_" }) -join "`n" }
}

function Get-SeedHashes($Fixture) {
    $seed = Join-Path $Fixture.Installer 'DiffusionNexus.Installer.Electron' 'Assets' 'Catalog'
    @('manifest.json', 'catalog.zip') | ForEach-Object {
        $file = Join-Path $seed $_
        if (Test-Path -LiteralPath $file) { (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash } else { 'absent' }
    }
}

Test-Case 'latest: replaces both files with the release assets byte for byte, names the version, and the gate then passes' {
    $f = New-CatalogFixture
    Set-CatalogSeed $f -From (Publish-CatalogRelease $f -Version 4) | Out-Null
    $pack = Publish-CatalogRelease $f -Version 5
    Assert-Result (Invoke-SeedUpdate $f) 0 -Contains 'Embedded catalog v5 (00000005, stable) under DiffusionNexus.Installer.Electron/Assets/Catalog',
        'git commit -m "chore(catalog): embed the v5 stable catalog seed"' -Lacks 'unchanged'
    $expected = @('manifest.json', 'catalog.zip') | ForEach-Object { (Get-FileHash -LiteralPath (Join-Path $pack $_) -Algorithm SHA256).Hash }
    Assert-Equal ((Get-SeedHashes $f) -join ' ') ($expected -join ' ') 'seed files after the update'
    Invoke-FixtureGit $f.Installer @('add', '--all'); Invoke-FixtureGit $f.Installer @('commit', '--quiet', '-m', 'seed')
    Assert-Result (Invoke-CatalogSeedCheck $f) 0
}

Test-Case '-Version fetches that stable tag instead of latest: a deliberate hold-back' {
    $f = New-CatalogFixture
    $pack4 = Publish-CatalogRelease $f -Version 4
    Publish-CatalogRelease $f -Version 5 | Out-Null
    Assert-Result (Invoke-SeedUpdate $f -ExtraArgs @('-Version', '4')) 0 -Contains 'Embedded catalog v4 (00000004, stable)'
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

Test-Case 'a folder that is not the installer repo is refused before anything is downloaded' {
    $f = New-CatalogFixture
    Publish-CatalogRelease $f -Version 5 | Out-Null
    Assert-Result (Invoke-SeedUpdate $f) 2 -Contains 'NOT updated', 'does not exist. Is', 'the installer repo'
}

Complete-Tests
```

- [ ] **Step 2: Run, expect 7 failures** (`pwsh -NoProfile -File Scripts/Tests/Update-CatalogSeed.Tests.ps1`)

- [ ] **Step 3: `Scripts/Update-CatalogSeed.ps1`**

```powershell
<#
.SYNOPSIS
    Replaces the embedded catalog seed with the latest stable catalog release.

.DESCRIPTION
    The write half of the seed gate, kept out of New-Release.ps1 so a refused release still changes
    nothing. Downloads manifest.json and catalog.zip from the catalog repo's latest stable release
    (or the tag -Version names), verifies the archive against the manifest's sha256, replaces the
    two files under DiffusionNexus.Installer.Electron/Assets/Catalog, and prints the version it
    embedded and the commit to make. This replaces the hand-made "chore(catalog): embed the vN stable
    catalog seed" commit that preceded every release. A Preview manifest is refused: the seed is
    always a stable catalog, because a Preview installer build is promoted without a rebuild.

    Exit codes: 0 the seed was written (unchanged when it already was that release); 2 nothing was
    replaced - the release could not be downloaded, is not a Stable manifest, or its archive does
    not match its manifest.

.PARAMETER RepoRoot
    The installer repo. Defaults to the folder above this script.

.PARAMETER ReleaseBase
    The catalog repo's releases page. Default: DIFFUSIONNEXUS_CATALOG_RELEASES when set, else
    https://github.com/Into-The-Latent/DiffusionNexus.Catalog/releases.

.PARAMETER Version
    Embed stable release vN instead of the latest: a deliberate hold-back. New-Release.ps1 will
    then need -AllowOlderCatalog.

.EXAMPLE
    pwsh Scripts/Update-CatalogSeed.ps1
    git add DiffusionNexus.Installer.Electron/Assets/Catalog
    git commit -m "chore(catalog): embed the v5 stable catalog seed"
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$ReleaseBase,
    [int]$Version
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

trap {
    Write-Host "Catalog seed NOT updated: $($_.Exception.Message) Nothing was replaced." -ForegroundColor Red
    exit 2
}

. (Join-Path $PSScriptRoot 'CatalogRelease.ps1')
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$assets = if ($Version) { "$(Get-CatalogReleaseBase $ReleaseBase)/download/v$Version" } else { "$(Get-CatalogReleaseBase $ReleaseBase)/latest/download" }

$seedDir = Join-Path $RepoRoot $CatalogSeedFolder
if (-not (Test-Path -LiteralPath $seedDir -PathType Container)) { throw "$seedDir does not exist. Is $RepoRoot the installer repo?" }

function Get-Sha256([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }
}
$before = @('manifest.json', 'catalog.zip' | ForEach-Object { Get-Sha256 (Join-Path $seedDir $_) })

# Both assets to a temp folder first, verified there, then moved: the seed is never half replaced.
$temp = Join-Path ([IO.Path]::GetTempPath()) ("catalogseed-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $manifestPath = Join-Path $temp 'manifest.json'
    $zipPath = Join-Path $temp 'catalog.zip'
    Save-CatalogAsset "$assets/manifest.json" $manifestPath
    $manifest = Read-CatalogManifest (Get-Content -LiteralPath $manifestPath -Raw) "$assets/manifest.json"
    if ($manifest.Channel -ne 'Stable') { throw "$assets/manifest.json is a $($manifest.Channel) manifest, not a Stable one; the seed is always a stable catalog." }
    if ($Version -and $manifest.Version -ne $Version) { throw "$assets/manifest.json says catalogVersion $($manifest.Version), not $Version." }
    Save-CatalogAsset "$assets/catalog.zip" $zipPath
    $zipHash = Get-Sha256 $zipPath
    if ($zipHash -ne $manifest.Sha256) { throw "the downloaded catalog.zip does not match its manifest: its sha256 is $zipHash, the manifest says $($manifest.Sha256)." }
    Move-Item -LiteralPath $manifestPath -Destination (Join-Path $seedDir 'manifest.json') -Force
    Move-Item -LiteralPath $zipPath -Destination (Join-Path $seedDir 'catalog.zip') -Force
} finally { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }

$after = @('manifest.json', 'catalog.zip' | ForEach-Object { Get-Sha256 (Join-Path $seedDir $_) })
$state = if (($before -join ' ') -eq ($after -join ' ')) { ' - unchanged, it already was' } else { '' }
Write-Host "Embedded catalog v$($manifest.Version) ($($manifest.Short), stable) under $CatalogSeedFolder$state. ($assets)" -ForegroundColor Green
Write-Host "Commit it: git add $CatalogSeedFolder; git commit -m `"chore(catalog): embed the v$($manifest.Version) stable catalog seed`""
exit 0
```

- [ ] **Step 4: Run, expect 7 passed.** Then the whole `Scripts/Tests` folder still green.

- [ ] **Step 5: Commit**

```
git add Scripts/Update-CatalogSeed.ps1 Scripts/Tests/Update-CatalogSeed.Tests.ps1
git commit -m "feat(release): Update-CatalogSeed.ps1 replaces the hand-made seed commit"
```

---

### Task 3: `catalogSeed` in `--build-info`, test first

**Files:**
- Modify: `DiffusionNexus.Installer.Tests/Services/BuildInfoTests.cs`
- Modify: `DiffusionNexus.Installer.Electron/Services/BuildInfo.cs`

**Interfaces:**
- Produces: `BuildInfoCatalogSeed(int Version, string Commit, string Sha256)` as `catalogSeed` (`version`, `commit`, `sha256`) in the JSON, between `catalogSchema` and `builtAt`; `BuildInfo.Create(string? assemblyLocation, Func<Stream?> embeddedManifest)`. A missing or incomplete embedded manifest throws (the process exits non-zero, and Step 1c refuses the build).

- [ ] **Step 1: Write the failing tests** (add to `BuildInfoTests`)

```csharp
    [Fact]
    public void The_document_names_the_embedded_catalog_seed()
    {
        // Step 1c compares this with the seed Step 0d judged; Promote-Release passes it to
        // Test-CatalogSeed -Expect. It is the embedded manifest.json, read the way the app reads it.
        using var stream = typeof(BuildInfo).Assembly.GetManifestResourceStream("manifest.json")!;
        using var doc = JsonDocument.Parse(stream);
        var embedded = doc.RootElement;

        var seed = BuildInfo.Create().CatalogSeed;

        seed.Version.Should().Be(embedded.GetProperty("catalogVersion").GetInt32());
        seed.Commit.Should().Be(embedded.GetProperty("commit").GetString()).And.MatchRegex("^[0-9a-f]{40}$");
        seed.Sha256.Should().Be(embedded.GetProperty("archive").GetProperty("sha256").GetString()).And.MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Without_a_complete_embedded_manifest_there_is_no_answer()
    {
        // A build with no seed, or a seed manifest without commit or archive, must not print a
        // document that promotion would trust: the exception ends the process with a non-zero exit.
        var location = typeof(BuildInfo).Assembly.Location;
        var noSeed = () => BuildInfo.Create(location, () => null);
        var noCommit = () => BuildInfo.Create(location, () => new MemoryStream("{\"catalogVersion\":5,\"archive\":{\"name\":\"catalog.zip\",\"sha256\":\"ab\",\"bytes\":1}}"u8.ToArray()));
        var noArchive = () => BuildInfo.Create(location, () => new MemoryStream("{\"catalogVersion\":5,\"commit\":\"abc\"}"u8.ToArray()));

        noSeed.Should().Throw<InvalidOperationException>().WithMessage("*manifest.json*missing*");
        noCommit.Should().Throw<InvalidOperationException>().WithMessage("*commit*");
        noArchive.Should().Throw<InvalidOperationException>().WithMessage("*archive*");
    }
```

And in `The_json_is_one_object_with_the_release_scripts_property_names`, after the `catalogSchema` line:

```csharp
        var seed = root.GetProperty("catalogSeed");
        seed.GetProperty("version").GetInt32().Should().Be(BuildInfo.Create().CatalogSeed.Version);
        seed.GetProperty("commit").GetString().Should().Be(BuildInfo.Create().CatalogSeed.Commit);
        seed.GetProperty("sha256").GetString().Should().Be(BuildInfo.Create().CatalogSeed.Sha256);
```

- [ ] **Step 2: Run, expect compile failure** (`CatalogSeed` does not exist)

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~BuildInfoTests" --nologo`

- [ ] **Step 3: Implement** (`BuildInfo.cs`)

```csharp
/// <summary>The catalog the build embeds as its seed: the three values Test-CatalogSeed.ps1 judges.</summary>
public sealed record BuildInfoCatalogSeed(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("commit")] string Commit,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record BuildInfoDocument(
    [property: JsonPropertyName("app")] string App,
    [property: JsonPropertyName("sdk")] string Sdk,
    [property: JsonPropertyName("catalogSchema")] int CatalogSchema,
    [property: JsonPropertyName("catalogSeed")] BuildInfoCatalogSeed CatalogSeed,
    [property: JsonPropertyName("builtAt")] DateTimeOffset? BuiltAt);
```

`Create(string? assemblyLocation)` delegates to `Create(assemblyLocation, () => typeof(BuildInfo).Assembly.GetManifestResourceStream("manifest.json"))`; the new overload parses the stream with `CatalogManifest.Parse` (the app's own reader of that resource) and throws `InvalidOperationException` when the stream is null ("The embedded catalog manifest 'manifest.json' is missing: this build carries no catalog seed and must not be released."), when `Commit` is null/empty, or when `Archive?.Sha256` is.

- [ ] **Step 4: Run, expect green**; then the whole suite: `dotnet test DiffusionNexus.Installer.slnx --nologo`.

- [ ] **Step 5: Commit**

```
git add DiffusionNexus.Installer.Electron/Services/BuildInfo.cs DiffusionNexus.Installer.Tests/Services/BuildInfoTests.cs
git commit -m "feat(release): --build-info names the embedded catalog seed"
```

---

### Task 4: Step 0d, Step 1c, the notes line, `-AllowOlderCatalog`, docs

**Files:**
- Modify: `Scripts/Tests/New-Release.Tests.ps1`
- Modify: `Scripts/New-Release.ps1`
- Modify: `README.md`, `docs/manual-smoke.md` (section 1.6 note), the spec (4.3: `-ReleaseBase`/env; 9: the local release server)

**Interfaces:**
- Consumes: Task 1's exit codes and `CatalogRelease.ps1`; Task 3's `catalogSeed`.

- [ ] **Step 1: Failing tests.** `Invoke-NewRelease` gains `$env:DIFFUSIONNEXUS_CATALOG_RELEASES = $Fixture.ReleaseUrl` (restored in `finally`) and `AllowOlderCatalog` in the splat; a `New-ReleaseFixture` helper = `New-SdkFixture`, `Add-CatalogReleases`, `Publish-CatalogRelease -Version 5`, `Set-CatalogSeed`. The three existing cases use it. New cases:

```powershell
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

Test-Case 'a seed that could not be checked refuses even with -AllowOlderCatalog' {
    $f = New-ReleaseFixture
    Set-Content -LiteralPath (Join-Path $f.Installer 'DiffusionNexus.Installer.Electron' 'Assets' 'Catalog' 'manifest.json.bak') -Value 'old'
    $r = Invoke-NewRelease $f -ExtraArgs @('-AllowOlderCatalog') -NativeErrors
    if ($r.ExitCode -eq 0) { throw "expected a refusal, got exit 0. Output:`n$($r.Text)" }
    Assert-Like $r.Text '*uncommitted changes*could not be checked*Nothing was built or changed*' 'output'
    Assert-Equal $r.VersionWritten $false 'version written'
}
```

- [ ] **Step 2: Run, expect the 3 new cases to fail** (no Step 0d: the run reaches dotnet publish).

- [ ] **Step 3: `New-Release.ps1`**: `[switch]$AllowOlderCatalog` with its `.PARAMETER` help; Step 0d after 0c:

```powershell
# Step 0d. The embedded catalog seed must be the latest stable catalog: a seed ahead of it ships
# content the stable channel does not serve, one behind it ships stale content to every fresh
# machine, and until now only a manual commit before each release kept it current. A Preview build
# embeds stable too: promotion never rebuilds. The fix is Scripts/Update-CatalogSeed.ps1 and a
# commit; this script never writes the seed.
Write-Host "Step 0d: the embedded catalog seed is the latest stable catalog" -ForegroundColor Cyan
pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-CatalogSeed.ps1') -RepoRoot $repoRoot
switch ($LASTEXITCODE) {
    0 { }
    3 {
        if (-not $AllowOlderCatalog) {
            throw "The embedded catalog seed is not the latest stable catalog (listed above). Run pwsh Scripts/Update-CatalogSeed.ps1 and commit, or re-run with -AllowOlderCatalog to ship this seed on purpose. Nothing was built or changed."
        }
        Write-Warning "Releasing WITHOUT the latest stable catalog seed (-AllowOlderCatalog)."
    }
    default { throw "The embedded catalog seed could not be checked (see above). Nothing was built or changed." }
}
# What Step 0d judged, for Step 1c: the packaged app must report exactly this seed.
. (Join-Path $PSScriptRoot 'CatalogRelease.ps1')
$seed = Read-CatalogManifest (Get-Content -LiteralPath (Join-Path $repoRoot $CatalogSeedFolder 'manifest.json') -Raw) 'the embedded seed manifest'
```

Step 1c, after the `sdk` comparison:

```powershell
$packagedSeed = $buildInfo.catalogSeed
if (-not $packagedSeed) { throw "The packaged app reports no catalogSeed. This build is not what the release script expects." }
if ("$($packagedSeed.version)" -ne "$($seed.Version)" -or "$($packagedSeed.commit)".ToLowerInvariant() -ne $seed.Commit -or "$($packagedSeed.sha256)".ToLowerInvariant() -ne $seed.Sha256) {
    throw "The packaged app says it bundles catalog v$($packagedSeed.version) ($($packagedSeed.commit)); Step 0d checked v$($seed.Version) ($($seed.Commit)). This build does not contain what was checked."
}
```

The green line adds `, catalog seed v$($seed.Version) ($($seed.Short))`. The notes: `$catalogLine = "Bundled catalog v$($seed.Version) ($($seed.Channel.ToLowerInvariant()))"`, appended after the SDK line. The `.DESCRIPTION` / final hint mention `-AllowOlderCatalog` next to `-AllowOlderSdk`.

- [ ] **Step 4: Run all `Scripts/Tests`, expect green** (pin 31, release 6, account 9, seed 14, update 7).

- [ ] **Step 5: Docs.** README "Publishing a release": the gates paragraph gains **0d** and `Update-CatalogSeed.ps1`; the build-info paragraph gains the seed; the notes lines. `docs/manual-smoke.md` §1.6: one sentence that `Scripts/Update-CatalogSeed.ps1` now regenerates the seed and `New-Release.ps1` refuses one that is not the latest stable release. Spec 4.3: the `-ReleaseBase` / `DIFFUSIONNEXUS_CATALOG_RELEASES` sentence; 9: the seed tests serve fixture releases over a local `HttpListener` (a 302 for `latest`, a 404 for a missing asset).

- [ ] **Step 6: Commit, push, PR** (base `feature/release-gates-sdk`, title `Release gates 2/2: embedded catalog seed equals the latest stable catalog (#38)`, body: what changed, how to run the tests, the env var, "Closes #38", stacked on #40).

---

## Rulings made during execution

Deviations from the code blocks above, each found by a failing run and pinned by the tests:

- **`Compress-Archive` → `[IO.Compression.ZipFile]`** in the kit: `Compress-Archive` globs its destination path, and the `[x]` in every fixture path made it fail with "Cannot bind argument to parameter 'Path' because it is null".
- **`Start-ThreadJob` → a runspace of its own** for the release server: thread jobs are throttled to five at a time, every server blocks in `GetContext` for the whole run, so the sixth fixture was never served and its downloads timed out.
- **`-Expect` is one string** (`"<version> <commit> <sha256>"`, split on spaces or commas), not `[string[]]`: `pwsh -File` hands a script literal strings and cannot fill an array parameter, and `ValueFromRemainingArguments` refuses extra tokens once the parameter was named. Also: the split goes into a separate variable, because assigning an array back to the `[string]` parameter joins it again.
- **`git status --porcelain --untracked-files=all`**: without it an untracked seed folder is reported as one collapsed line and the message could not name the files.
- **Fixture short commits are `0000000`** (the default commit for version N is `0000000N` repeated; its first seven digits are zeros), so the expected texts say `v5 (0000000)`.
- **`Update-CatalogSeed.ps1` also refuses a tag whose manifest says another version** (`-Version 5` served a v7 manifest): eight cases, not seven. The hold-back case seeds v3 first, because the script refuses a folder with no seed as "not the installer repo".
- **Review round 1 (PR #41): a fourth exit code.** Exit 3 covered every difference, so `-AllowOlderCatalog` also shipped a seed *ahead* of stable or another catalog under stable's number. Now exit 3 means "an older stable release": behind, and exactly the manifest at `download/v<N>/` (downloaded to confirm it; not readable → 2). Ahead, same number but other content, not the release of its number, or a Preview seed manifest → **exit 4**, which no flag overrides; `New-Release.ps1` (and `Promote-Release.ps1`, #30) refuse it with no override. Exit 4 rather than 2, because 2 means "could not be checked" and these seeds were checked and found wrong. In the working tree the whole manifest is compared, parsed, so a Preview stamp or a hand-made `generatedAt` over the right archive is caught: the SDK records both from the seed.
- **Proof from the real entry point**: `dotnet build` of the Electron project and `DiffusionNexus.Installer.Electron.exe --build-info` printed `catalogSeed { 5, 51e1684…, e7d3ee7a… }`, and `pwsh Scripts/Test-CatalogSeed.ps1` against GitHub answered exit 0 for the committed seed and exit 3 for `-Expect "4 …"`. The packaged proof (`New-Release.ps1 -SkipUpload`) stays owed with #37's, blocked on the v2.0.0 package publish.
