# Shared by Scripts/Tests/*.Tests.ps1 - dot-source it. Plain pwsh: the repo has no Pester for pwsh 7,
# and these scripts need nothing Pester adds.
#
# Every fixture is a set of throwaway git repos under the temp folder, in a directory whose name
# contains a SPACE, an APOSTROPHE and SQUARE BRACKETS (so every test also proves paths are quoted,
# never pasted into source text, and never handed to a -Path parameter, which reads [x] as a
# wildcard - the Windows user folder of a real machine may hold any of them, C:\Users\O'Neil [2]):
#   remote.git  bare repo  = the SDK on GitHub
#   author      work repo  = whoever merges SDK PRs: commits, tags, pushes to remote.git
#   sdk         clone      = the local SDK checkout Test-SdkPin.ps1 reads. Cloned right after the
#                            first tag, so everything later reaches it only through git fetch.
#   installer   projects   = this repo: <Name>\<Name>.csproj files holding the SDK pins
#   releases    folder     = the catalog repo's GitHub Releases, served over local HTTP (below)

$script:Passed = 0
$script:Failed = 0
$script:FixtureRoots = [System.Collections.Generic.List[string]]::new()
$script:ReleaseServers = [System.Collections.Generic.List[object]]::new()

function Invoke-FixtureGit([string]$Dir, [string[]]$GitArgs) {
    $output = & git -C $Dir -c core.autocrlf=false -c commit.gpgsign=false -c tag.gpgsign=false `
        -c user.name=sdkpin-test -c user.email=sdkpin-test@example.invalid @GitArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed in ${Dir}:`n$($output -join "`n")" }
}

function New-SdkFixture {
    $root = Join-Path ([IO.Path]::GetTempPath()) ("sdkpin o'test [x]-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $script:FixtureRoots.Add($root)
    $fixture = [pscustomobject]@{
        Root      = $root
        Remote    = Join-Path $root 'remote.git'
        Author    = Join-Path $root 'author'
        Sdk       = Join-Path $root 'sdk'
        Installer = Join-Path $root 'installer'
    }
    New-Item -ItemType Directory -Path $fixture.Remote, $fixture.Author, $fixture.Installer | Out-Null
    Invoke-FixtureGit $fixture.Remote @('init', '--quiet', '--bare', '--initial-branch=develop')
    Invoke-FixtureGit $fixture.Author @('init', '--quiet', '--initial-branch=develop')
    Invoke-FixtureGit $fixture.Author @('remote', 'add', 'origin', $fixture.Remote)
    Add-SdkCommit $fixture @(
        'DiffusionNexus.Installer.SDK.Models/Start.cs'
        'DiffusionNexus.Installer.SDK.Services/Start.cs'
        'DiffusionNexus.Installer.SDK.Catalog/Start.cs'
        'docs/start.md'
    ) 'initial'
    Add-SdkCommit $fixture 'Directory.Build.props' 'build: props' -Content (New-SdkProps '2.0.0-preview.1')
    Add-SdkTag $fixture '2.0.0-preview.1'
    Invoke-FixtureGit $root @('clone', '--quiet', $fixture.Remote, $fixture.Sdk)
    Set-InstallerPins $fixture '2.0.0-preview.1'
    $fixture
}

# One commit that appends a line to every path given (or, with -Content, replaces each file with
# that text), pushed to the "GitHub" remote.
function Add-SdkCommit($Fixture, [string[]]$Paths, [string]$Subject, [string]$Content) {
    foreach ($path in $Paths) {
        $file = Join-Path $Fixture.Author $path
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $file) | Out-Null
        if ($PSBoundParameters.ContainsKey('Content')) { Set-Content -LiteralPath $file -Value $Content }
        else { Add-Content -LiteralPath $file -Value $Subject }
    }
    Invoke-FixtureGit $Fixture.Author @('add', '--all')
    Invoke-FixtureGit $Fixture.Author @('commit', '--quiet', '-m', $Subject)
    Invoke-FixtureGit $Fixture.Author @('push', '--quiet', 'origin', 'develop')
}

# The SDK's root Directory.Build.props: the version line every release bumps, package metadata that
# never ships, a compile setting that does - plus whatever else the test adds.
function New-SdkProps([string]$Version, [string[]]$Extra = @(), [string]$RepositoryUrl = 'https://github.com/x/sdk', [string]$SqlitePin = '2.1.11') {
    (@('<Project>', '  <PropertyGroup>', "    <Version>$Version</Version>", "    <RepositoryUrl>$RepositoryUrl</RepositoryUrl>", '    <Nullable>enable</Nullable>') +
     @($Extra | ForEach-Object { "    $_" }) +
     @('  </PropertyGroup>', '  <ItemGroup>', '    <PackageReference Update="SQLitePCLRaw.lib.e_sqlite3">', "      <Version>$SqlitePin</Version>", '    </PackageReference>', '  </ItemGroup>', '</Project>')) -join "`n"
}

function Add-SdkTag($Fixture, [string]$Version) {
    Invoke-FixtureGit $Fixture.Author @('tag', '--annotate', "v$Version", '-m', "v$Version")
    Invoke-FixtureGit $Fixture.Author @('push', '--quiet', 'origin', "v$Version")
}

# Installer.Core pins Models + Catalog, Installer.App pins $AppPackages - like the real repo, whose
# Core and Electron projects both pin the SDK.
function Set-InstallerPins($Fixture, [string]$Version, [string]$AppVersion = $Version,
                           [string[]]$AppPackages = @('DiffusionNexus.Installer.SDK.Services')) {
    Write-FixtureProject $Fixture 'Installer.Core' @('DiffusionNexus.Installer.SDK.Models', 'DiffusionNexus.Installer.SDK.Catalog') $Version
    Write-FixtureProject $Fixture 'Installer.App' $AppPackages $AppVersion
}

function Write-FixtureProject($Fixture, [string]$Name, [string[]]$Packages, [string]$Version) {
    $dir = Join-Path $Fixture.Installer $Name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $lines = @('<Project Sdk="Microsoft.NET.Sdk">', '  <ItemGroup>', '    <PackageReference Include="Some.Other.Package" Version="9.9.9" />') +
             @($Packages | ForEach-Object { "    <PackageReference Include=`"$_`" Version=`"$Version`" />" }) +
             @('  </ItemGroup>', '</Project>')
    Set-Content -LiteralPath (Join-Path $dir "$Name.csproj") -Value $lines
}

# Runs Test-SdkPin.ps1 in a child pwsh, exactly as New-Release.ps1 does.
function Invoke-SdkPinCheck($Fixture, [string]$SdkPath = $Fixture.Sdk, [string[]]$ExtraArgs = @(), [switch]$NoSdkPath) {
    $check = Join-Path $PSScriptRoot '..' 'Test-SdkPin.ps1'
    $sdkArgs = if ($NoSdkPath) { @() } else { @('-SdkPath', $SdkPath) }
    $output = & pwsh -NoProfile -File $check -RepoRoot $Fixture.Installer @sdkArgs @ExtraArgs 2>&1
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = ($output | ForEach-Object { "$_" }) -join "`n" }
}

# ------------------------------------------------------------------ the catalog repo's releases
# What the seed scripts see of Into-The-Latent/DiffusionNexus.Catalog: a folder served over local
# HTTP, so the path under test is the real one (Invoke-WebRequest, a redirect, a 404), never a
# file copy standing in for a download.
#   <Releases>\download\vN\{manifest.json,catalog.zip}   = the assets of release vN
#   <Releases>\latest                                    = a text file naming the tag releases/latest
#                                                          redirects to, as GitHub does (302)
# -Port is the first port tried (0: probe a free one). Another process can take a probed port before
# the listener binds it, on a busy runner or with test files running in parallel, so a bind that
# fails moves to a newly probed port instead of failing the case.
function Start-ReleaseServer([string]$Root, [int]$Port = 0) {
    $listener = $null
    for ($attempt = 1; -not $listener; $attempt++) {
        if (-not $Port) {
            $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
            $probe.Start(); $Port = $probe.LocalEndpoint.Port; $probe.Stop()
        }
        $candidate = [System.Net.HttpListener]::new()
        $candidate.Prefixes.Add("http://127.0.0.1:$Port/")
        try { $candidate.Start(); $listener = $candidate; $port = $Port }
        catch [System.Net.HttpListenerException] {
            $candidate.Close()
            if ($attempt -ge 10) { throw "no free port for the fixture release server after $attempt attempts: $($_.Exception.Message)" }
            $Port = 0
        }
    }
    # A runspace of its own, not Start-ThreadJob: thread jobs are throttled to five at a time, and
    # every server here blocks in GetContext for the whole run, so the sixth fixture would never
    # be served and every download from it would time out.
    $serve = {
        param($listener, $root)
        while ($listener.IsListening) {
            try { $context = $listener.GetContext() } catch { break }
            $response = $context.Response
            $path = $context.Request.Url.AbsolutePath
            $latest = Join-Path $root 'releases' 'latest'
            if ($path -match '^/releases/latest/download/(.+)$' -and (Test-Path -LiteralPath $latest -PathType Leaf)) {
                $tag = (Get-Content -LiteralPath $latest -Raw).Trim()
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
    $shell = [powershell]::Create()
    [void]$shell.AddScript($serve.ToString()).AddArgument($listener).AddArgument($Root)
    $handle = $shell.BeginInvoke()
    $server = [pscustomobject]@{ Url = "http://127.0.0.1:$port/releases"; Listener = $listener; Shell = $shell; Handle = $handle }
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
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }   # a re-pack of the same version
    # ZipFile, not Compress-Archive: the latter globs its destination, and [x] in the fixture path breaks it.
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
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
function Invoke-CatalogSeedCheck($Fixture, [string[]]$ExtraArgs = @(), [string]$ReleaseBase = $Fixture.ReleaseUrl) {
    $check = Join-Path $PSScriptRoot '..' 'Test-CatalogSeed.ps1'
    $output = & pwsh -NoProfile -File $check -RepoRoot $Fixture.Installer -ReleaseBase $ReleaseBase @ExtraArgs 2>&1
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = ($output | ForEach-Object { "$_" }) -join "`n" }
}

function Test-Case([string]$Name, [scriptblock]$Body) {
    try {
        & $Body
        $script:Passed++
        Write-Host "PASS  $Name" -ForegroundColor Green
    } catch {
        $script:Failed++
        Write-Host "FAIL  $Name`n      $($_.Exception.Message -replace "`n", "`n      ")" -ForegroundColor Red
    }
}

function Assert-Result($Result, [int]$ExitCode, [string[]]$Contains = @(), [string[]]$Lacks = @()) {
    if ($Result.ExitCode -ne $ExitCode) { throw "exit code $($Result.ExitCode), expected $ExitCode. Output:`n$($Result.Text)" }
    foreach ($text in $Contains) { if (-not $Result.Text.Contains($text)) { throw "output lacks '$text'. Output:`n$($Result.Text)" } }
    foreach ($text in $Lacks) { if ($Result.Text.Contains($text)) { throw "output should not contain '$text'. Output:`n$($Result.Text)" } }
}

function Assert-Equal($Actual, $Expected, [string]$What) {
    if ($null -eq $Expected) { if ($null -ne $Actual) { throw "$What was '$Actual', expected null" }; return }
    if ("$Actual" -ne "$Expected") { throw "$What was '$Actual', expected '$Expected'" }
}

function Assert-Like([string]$Actual, [string]$Pattern, [string]$What) {
    if ($Actual -notlike $Pattern) { throw "$What did not match '$Pattern'. Was:`n$Actual" }
}

# Removes the fixtures and exits with the number of failed cases, so CI fails on any.
function Complete-Tests {
    foreach ($server in $script:ReleaseServers) {
        $server.Listener.Stop(); $server.Listener.Close()
        $server.Shell.Stop(); $server.Shell.Dispose()
    }
    foreach ($root in $script:FixtureRoots) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host "$($script:Passed) passed, $($script:Failed) failed"
    exit $script:Failed
}
