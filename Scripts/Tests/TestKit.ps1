# Shared by Scripts/Tests/*.Tests.ps1 - dot-source it. Plain pwsh: the repo has no Pester for pwsh 7,
# and these scripts need nothing Pester adds.
#
# Every fixture is a set of throwaway git repos under the temp folder, in a directory whose name
# contains a SPACE and an APOSTROPHE (so every test also proves paths are quoted and never pasted
# into source text - the Windows user folder of a real machine may hold either, C:\Users\O'Neil):
#   remote.git  bare repo  = the SDK on GitHub
#   author      work repo  = whoever merges SDK PRs: commits, tags, pushes to remote.git
#   sdk         clone      = the local SDK checkout Test-SdkPin.ps1 reads. Cloned right after the
#                            first tag, so everything later reaches it only through git fetch.
#   installer   projects   = this repo: <Name>\<Name>.csproj files holding the SDK pins

$script:Passed = 0
$script:Failed = 0
$script:FixtureRoots = [System.Collections.Generic.List[string]]::new()

function Invoke-FixtureGit([string]$Dir, [string[]]$GitArgs) {
    $output = & git -C $Dir -c core.autocrlf=false -c commit.gpgsign=false -c tag.gpgsign=false `
        -c user.name=sdkpin-test -c user.email=sdkpin-test@example.invalid @GitArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed in ${Dir}:`n$($output -join "`n")" }
}

function New-SdkFixture {
    $root = Join-Path ([IO.Path]::GetTempPath()) ("sdkpin o'test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
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
        if ($PSBoundParameters.ContainsKey('Content')) { Set-Content -Path $file -Value $Content }
        else { Add-Content -Path $file -Value $Subject }
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
    Set-Content -Path (Join-Path $dir "$Name.csproj") -Value $lines
}

# Runs Test-SdkPin.ps1 in a child pwsh, exactly as New-Release.ps1 does.
function Invoke-SdkPinCheck($Fixture, [string]$SdkPath = $Fixture.Sdk, [string[]]$ExtraArgs = @(), [switch]$NoSdkPath) {
    $check = Join-Path $PSScriptRoot '..' 'Test-SdkPin.ps1'
    $sdkArgs = if ($NoSdkPath) { @() } else { @('-SdkPath', $SdkPath) }
    $output = & pwsh -NoProfile -File $check -RepoRoot $Fixture.Installer @sdkArgs @ExtraArgs 2>&1
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
    foreach ($root in $script:FixtureRoots) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host "$($script:Passed) passed, $($script:Failed) failed"
    exit $script:Failed
}
