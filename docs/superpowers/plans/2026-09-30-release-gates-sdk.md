# Release Gates, SDK Half (#37) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `New-Release.ps1` refuses, before it changes anything, when the packages token is missing, when no signed-in `gh` account can publish, or when the SDK pin is behind SDK `develop`; the packaged app answers `--build-info` and that answer ships as `build-info.json`; the pins move to a plain SDK version.

**Architecture:** Three small scripts under `Scripts/` (`Test-SdkPin.ps1`, `ReleaseAccount.ps1`, and the existing `New-Release.ps1` calling them in a fixed Step 0 order) plus one .NET class (`BuildInfo`) the entry point calls first. Tests are plain pwsh under `Scripts/Tests/` sharing `TestKit.ps1` (throwaway git repos in a path with a space, a fake global `gh`), run by CI before .NET is even set up. The .NET side has one xunit test class.

**Tech Stack:** pwsh 7.2+, git, xunit + FluentAssertions, GitHub Actions windows-latest.

**Spec:** `docs/superpowers/specs/2026-09-30-release-and-update-channels-design.md` sections 4.1, 4.2, 4.5 (`app`/`sdk`/`catalogSchema` only), 4.6 (SDK line only), 4.7, 9. Issue: Into-The-Latent/DiffusionNexus.Installer #37. The script code below is #29's posted design and the 2026-09-25 account comment, reused as posted, with two additions called out inline (`-Pin`, and `Assert-Equal`/`Assert-Like` in the kit).

## Global Constraints

- Exit codes of every check script: **0** current, **3** behind (overridable), **2** not checked (never overridable). Never 1; a `trap` maps unexpected errors to 2.
- `-AllowOlderSdk` overrides exit 3 only.
- Everything under Step 0 runs before `Directory.Build.props` is touched; a refusal changes nothing on disk.
- No `gh auth switch`, ever. The resolved token is used only by the scripts' own `gh` calls, via `Invoke-WithGhToken`, and `GH_TOKEN` is restored afterwards.
- The pin check never touches the SDK checkout's working tree or branch; it runs `git fetch` and reads `origin/develop` and tags.
- pwsh 7.2+, no Pester. Tests run in a child `pwsh -NoProfile`.
- Branch `feature/release-gates-sdk` off `main` (already exists with the spec). One PR.
- Line endings: index LF, worktree CRLF. Before each commit `git diff --cached --numstat` must equal `git diff --cached -w --numstat`.
- Pushing needs the Into-The-Latent token: `git -c credential.helper= -c "credential.helper=!f() { echo username=Into-The-Latent; echo password=$(gh auth token --user Into-The-Latent); }; f" push ...`. Never switch the active `gh` account.
- The pins move to `2.0.0` (identical content to `2.0.0-preview.9`). If `Test-SdkPin.ps1` names a newer plain tag at execution time, use that one instead.
- `catalogSeed` in `build-info.json` and the "Bundled catalog" notes line are #38, not here.

## Review Focus

1. **The packaged app is asked `--build-info` but prints something else first** (a log line, a host banner): Step 1c must fail with the raw text, not parse garbage. Pinned in Task 3 (`Handles_only_the_exact_flag`) and in Task 4 step 1c's `ConvertFrom-Json` failing loudly on non-JSON.
2. **`GH_TOKEN` already set in the user's session** (a profile exports it): the account probe must judge each candidate under its own token and put the user's value back. Pinned in Task 2 (`GH_TOKEN is left as it was`).
3. **The `-Pin` given to `Test-SdkPin.ps1` has no tag**: exit 2, not 3, so promotion cannot wave through a version that was never published. Pinned in Task 1 (`-Pin with no tag is "not checked"`).
4. **Project pins disagree while `-Pin` is given**: the explicit pin wins and the disagreement is not a refusal (promotion judges a shipped build, not the working tree). Pinned in Task 1 (`-Pin checks the given version instead of the project pins`).
5. **The entry point is started by Electron with its own arguments** (`/electronPort=…` style): `--build-info` must be matched exactly and only as a plain argument, so a normal launch never prints JSON and exits. Pinned in Task 3 (`Handles_only_the_exact_flag`).

---

### Task 1: `Test-SdkPin.ps1` with `-Pin`, test first

**Files:**
- Create: `Scripts/Tests/TestKit.ps1`
- Create: `Scripts/Tests/Test-SdkPin.Tests.ps1`
- Create: `Scripts/Test-SdkPin.ps1`

**Interfaces:**
- Produces: `pwsh -NoProfile -File Scripts/Test-SdkPin.ps1 [-RepoRoot <path>] [-SdkPath <path>] [-SdkBranch develop] [-Pin <version>]` → exit 0/3/2; prints `SDK pin <v> includes everything on SDK develop.` or the missing-commits block. Task 4 switches on the exit code; #30 uses `-Pin`.
- Produces (kit): `New-SdkFixture`, `Add-SdkCommit $f <paths[]> <subject>`, `Add-SdkTag $f <version>`, `Set-InstallerPins $f <version> [-AppVersion] [-AppPackages]`, `Write-FixtureProject`, `Invoke-FixtureGit <dir> <args[]>`, `Invoke-SdkPinCheck $f [-SdkPath] [-ExtraArgs]` → `{ExitCode, Text}`, `Test-Case`, `Assert-Result <result> <exit> [-Contains] [-Lacks]`, `Assert-Equal <actual> <expected> <what>`, `Assert-Like <actual> <pattern> <what>`, `Complete-Tests`.

- [ ] **Step 1: Branch**

```powershell
git switch feature/release-gates-sdk; git pull
```

- [ ] **Step 2: Create `Scripts/Tests/TestKit.ps1`**

The #29 kit as posted, plus `Assert-Equal` and `Assert-Like` (the account tests use them; the posted kit never defined them).

```powershell
# Shared by Scripts/Tests/*.Tests.ps1 - dot-source it. Plain pwsh: the repo has no Pester for pwsh 7,
# and these scripts need nothing Pester adds.
#
# Every fixture is a set of throwaway git repos under the temp folder, in a directory whose name
# contains a SPACE (so every test also proves paths are quoted - the Windows user folder of a
# real machine may have one):
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
    $root = Join-Path ([IO.Path]::GetTempPath()) ('sdkpin test-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
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
    Add-SdkTag $fixture '2.0.0-preview.1'
    Invoke-FixtureGit $root @('clone', '--quiet', $fixture.Remote, $fixture.Sdk)
    Set-InstallerPins $fixture '2.0.0-preview.1'
    $fixture
}

# One commit that appends a line to every path given, pushed to the "GitHub" remote.
function Add-SdkCommit($Fixture, [string[]]$Paths, [string]$Subject) {
    foreach ($path in $Paths) {
        $file = Join-Path $Fixture.Author $path
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $file) | Out-Null
        Add-Content -Path $file -Value $Subject
    }
    Invoke-FixtureGit $Fixture.Author @('add', '--all')
    Invoke-FixtureGit $Fixture.Author @('commit', '--quiet', '-m', $Subject)
    Invoke-FixtureGit $Fixture.Author @('push', '--quiet', 'origin', 'develop')
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
function Invoke-SdkPinCheck($Fixture, [string]$SdkPath = $Fixture.Sdk, [string[]]$ExtraArgs = @()) {
    $check = Join-Path $PSScriptRoot '..' 'Test-SdkPin.ps1'
    $output = & pwsh -NoProfile -File $check -RepoRoot $Fixture.Installer -SdkPath $SdkPath @ExtraArgs 2>&1
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
```

- [ ] **Step 3: Create `Scripts/Tests/Test-SdkPin.Tests.ps1`**

The 15 posted cases plus 3 for `-Pin`.

```powershell
#Requires -Version 7.2
# Tests for Scripts/Test-SdkPin.ps1. Run: pwsh -NoProfile -File Scripts/Tests/Test-SdkPin.Tests.ps1
# Exit code = number of failed cases. CI runs every Scripts/Tests/*.Tests.ps1.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestKit.ps1')

Test-Case 'current: the pin is the newest tag and nothing followed it' {
    $f = New-SdkFixture
    Assert-Result (Invoke-SdkPinCheck $f) 0 -Contains 'SDK pin 2.0.0-preview.1 includes everything on SDK develop.'
}

Test-Case 'behind, released: fetches first, lists the commit, names the v2 tag to bump to - never a v1 tag' {
    $f = New-SdkFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: services change'
    Add-SdkTag $f '2.0.0-preview.2'
    Add-SdkCommit $f 'docs/notes.md' 'docs: later note'
    Add-SdkTag $f '1.9.9'
    Assert-Result (Invoke-SdkPinCheck $f) 3 `
        -Contains 'SDK pin 2.0.0-preview.1 is missing 1 commit from SDK develop:', 'fix: services change',
                  'All of them ship in v2.0.0-preview.2 -> bump the SDK references to 2.0.0-preview.2.' `
        -Lacks 'v1.9.9', 'docs: later note'
}

Test-Case 'behind, unreleased: says to tag and publish the SDK first' {
    $f = New-SdkFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Models/Model.cs' 'feat: models change'
    Assert-Result (Invoke-SdkPinCheck $f) 3 `
        -Contains 'feat: models change', 'None of them is in a tagged SDK release yet -> tag and publish the SDK first'
}

Test-Case 'behind, mixed: counts what a tag ships and what is unreleased' {
    $f = New-SdkFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: released change'
    Add-SdkTag $f '2.0.0-preview.2'
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Catalog/Catalog.cs' 'feat: unreleased change'
    Assert-Result (Invoke-SdkPinCheck $f) 3 `
        -Contains 'is missing 2 commits from SDK develop:', 'fix: released change', 'feat: unreleased change',
                  'Released in v2.0.0-preview.2: 1. In no tagged SDK release yet: 1.'
}

Test-Case 'changes that do not ship never count: tests, the dn-catalog tool, docs, root props' {
    $f = New-SdkFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Tests/ServiceTests.cs' 'test: more tests'
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Catalog.Tool/Program.cs' 'feat: dn-catalog change'
    Add-SdkCommit $f 'Directory.Build.props' 'build: version bump'
    Add-SdkCommit $f 'docs/notes.md' 'docs: note'
    Assert-Result (Invoke-SdkPinCheck $f) 0
}

Test-Case 'a commit that touches a package and its tests is listed once' {
    $f = New-SdkFixture
    Add-SdkCommit $f @('DiffusionNexus.Installer.SDK.Services/Service.cs', 'DiffusionNexus.Installer.SDK.Tests/ServiceTests.cs') 'fix: service with tests'
    Assert-Result (Invoke-SdkPinCheck $f) 3 -Contains 'is missing 1 commit from SDK develop:', 'fix: service with tests'
}

Test-Case 'the SDK checkout''s own branch, commits and edits are ignored - only origin/develop counts' {
    $f = New-SdkFixture
    Invoke-FixtureGit $f.Sdk @('switch', '--quiet', '--create', 'feature/local-work')
    Add-Content -Path (Join-Path $f.Sdk 'DiffusionNexus.Installer.SDK.Services' 'Local.cs') -Value 'local'
    Invoke-FixtureGit $f.Sdk @('add', '--all')
    Invoke-FixtureGit $f.Sdk @('commit', '--quiet', '-m', 'wip: local only')
    Add-Content -Path (Join-Path $f.Sdk 'DiffusionNexus.Installer.SDK.Models' 'Dirty.cs') -Value 'uncommitted'
    Assert-Result (Invoke-SdkPinCheck $f) 0 -Lacks 'wip: local only'
}

Test-Case 'a session that turns native exit codes into errors does not break the check' {
    $f = New-SdkFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: released change'
    Add-SdkTag $f '2.0.0-preview.2'
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Models/Model.cs' 'feat: unreleased change'
    $check = Join-Path $PSScriptRoot '..' 'Test-SdkPin.ps1'
    $command = "`$PSNativeCommandUseErrorActionPreference = `$true; & '$check' -RepoRoot '$($f.Installer)' -SdkPath '$($f.Sdk)'; exit `$LASTEXITCODE"
    $output = & pwsh -NoProfile -Command $command 2>&1
    $result = [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = ($output | ForEach-Object { "$_" }) -join "`n" }
    Assert-Result $result 3 -Contains 'In no tagged SDK release yet: 1.'
}

Test-Case 'pins that disagree between projects are "not checked"' {
    $f = New-SdkFixture
    Set-InstallerPins $f '2.0.0-preview.1' -AppVersion '2.0.0-preview.2'
    Assert-Result (Invoke-SdkPinCheck $f) 2 `
        -Contains 'do not all pin the same version', 'Installer.App.csproj: DiffusionNexus.Installer.SDK.Services 2.0.0-preview.2'
}

Test-Case 'a pin with no tag in the SDK repo is "not checked"' {
    $f = New-SdkFixture
    Set-InstallerPins $f '2.0.0-preview.7'
    Assert-Result (Invoke-SdkPinCheck $f) 2 -Contains 'the pinned version 2.0.0-preview.7 has no tag v2.0.0-preview.7'
}

Test-Case 'a failed fetch is "not checked", never a pass' {
    $f = New-SdkFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: unseen change'
    Invoke-FixtureGit $f.Sdk @('remote', 'set-url', 'origin', (Join-Path $f.Root 'gone.git'))
    Assert-Result (Invoke-SdkPinCheck $f) 2 -Contains 'git fetch in', 'failed'
}

Test-Case 'no SDK checkout is "not checked"' {
    $f = New-SdkFixture
    Assert-Result (Invoke-SdkPinCheck $f -SdkPath (Join-Path $f.Root 'nowhere')) 2 -Contains 'no SDK git checkout found'
}

Test-Case 'a pinned package with no folder on develop is "not checked"' {
    $f = New-SdkFixture
    Set-InstallerPins $f '2.0.0-preview.1' -AppPackages 'DiffusionNexus.Installer.SDK.Shared'
    Assert-Result (Invoke-SdkPinCheck $f) 2 `
        -Contains 'package folder DiffusionNexus.Installer.SDK.Shared does not exist on origin/develop'
}

Test-Case 'no SDK references at all is "not checked"' {
    $f = New-SdkFixture
    Write-FixtureProject $f 'Installer.Core' @() '1.0.0'
    Write-FixtureProject $f 'Installer.App' @() '1.0.0'
    Assert-Result (Invoke-SdkPinCheck $f) 2 -Contains 'no DiffusionNexus.Installer.SDK.* PackageReference'
}

Test-Case 'a project file that is not valid XML is "not checked", never "behind"' {
    $f = New-SdkFixture
    Add-Content -Path (Join-Path $f.Installer 'Installer.Core' 'Installer.Core.csproj') -Value '<<<<<<< HEAD'
    Assert-Result (Invoke-SdkPinCheck $f) 2 -Contains 'SDK pin NOT checked'
}

Test-Case '-Pin checks the given version instead of the project pins' {
    # Promotion judges a shipped build's SDK version, not the working tree's csproj files.
    $f = New-SdkFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: services change'
    Add-SdkTag $f '2.0.0-preview.2'
    Set-InstallerPins $f '2.0.0-preview.1' -AppVersion '2.0.0-preview.2'   # disagreeing pins are irrelevant here
    Assert-Result (Invoke-SdkPinCheck $f -ExtraArgs @('-Pin', '2.0.0-preview.2')) 0 -Contains 'SDK pin 2.0.0-preview.2 includes everything'
    Assert-Result (Invoke-SdkPinCheck $f -ExtraArgs @('-Pin', '2.0.0-preview.1')) 3 -Contains 'SDK pin 2.0.0-preview.1 is missing 1 commit', 'fix: services change'
}

Test-Case '-Pin with no tag is "not checked"' {
    $f = New-SdkFixture
    Assert-Result (Invoke-SdkPinCheck $f -ExtraArgs @('-Pin', '2.5.0')) 2 -Contains 'the pinned version 2.5.0 has no tag v2.5.0'
}

Test-Case 'a plain version pin works like a preview one (the SDK has no channel any more)' {
    $f = New-SdkFixture
    Add-SdkTag $f '2.0.0'
    Set-InstallerPins $f '2.0.0'
    Assert-Result (Invoke-SdkPinCheck $f) 0 -Contains 'SDK pin 2.0.0 includes everything'
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Catalog/Catalog.cs' 'feat: catalog change'
    Add-SdkTag $f '2.1.0'
    Assert-Result (Invoke-SdkPinCheck $f) 3 -Contains 'All of them ship in v2.1.0 -> bump the SDK references to 2.1.0.'
}

Complete-Tests
```

- [ ] **Step 4: Run it and watch every case fail**

Run from the repo root: `pwsh -NoProfile -File Scripts/Tests/Test-SdkPin.Tests.ps1`
Expected: `0 passed, 18 failed`. Most say `exit code 64, expected …` (pwsh's code for a missing script file); the session-preference case says `exit code 0, expected 3`.

- [ ] **Step 5: Create `Scripts/Test-SdkPin.ps1`**

The posted script plus `-Pin`: three changes, marked `# -Pin:`.

```powershell
<#
.SYNOPSIS
    Fails when the installer's SDK pin leaves out SDK work that is already on the SDK's develop branch.

.DESCRIPTION
    The installer pins the DiffusionNexus.Installer.SDK.* packages to one version. v3.0.9 shipped
    2.0.0-preview.8 although preview.9 - the Manager-aware Update-ComfyUI.bat - had been published
    the evening before, because nothing compared the pin with the SDK. This script does, and
    New-Release.ps1 runs it before it builds anything.

    It fetches the SDK checkout (the folder Directory.Build.targets redirects to), then lists every
    commit on origin/<SdkBranch> that touches a pinned package's folder and is not in the pin's tag
    v<pin>. Only the pinned packages' own folders count: tests, docs, the dn-catalog tool and the
    SDK's root Directory.Build.props (package metadata and version) never ship.

    Exit codes - New-Release.ps1 and Promote-Release.ps1 rely on them:
      0  the pin contains everything on the SDK branch
      3  the pin is behind. The commits are listed, with what to do: bump to the tag that ships
         them, or tag and publish the SDK first.
      2  the pin could not be checked. Any unexpected error lands here too, never on 3, so it can
         never be waved through with -AllowOlderSdk. (1 is left to pwsh, which uses it for a script
         that does not even parse.)

.PARAMETER RepoRoot
    The installer repo whose project files hold the pins - every *.csproj one folder deep, which
    is where this solution keeps its projects. Defaults to the folder above this script.

.PARAMETER SdkPath
    The SDK git checkout. Defaults to the first that exists of $env:LocalSDKPath,
    <RepoRoot>\..\DiffusionNexus.Installer.SDK and E:\Repos\DiffusionNexus.Installer.SDK - the
    same candidates as Directory.Build.targets.

.PARAMETER SdkBranch
    The SDK branch the installer ships from. Default: develop.

.PARAMETER Pin
    Check this SDK version instead of the one the project files pin. Promote-Release.ps1 passes
    the version a shipped build reports in its build-info.json. The project files still say which
    packages count; their versions are ignored.

.EXAMPLE
    pwsh Scripts/Test-SdkPin.ps1

.EXAMPLE
    pwsh Scripts/Test-SdkPin.ps1 -Pin 2.0.0
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$SdkPath,
    [string]$SdkBranch = 'develop',
    [string]$Pin
)

$ErrorActionPreference = 'Stop'
# git answers some questions with a non-zero exit (merge-base --is-ancestor says "no" with 1). Here
# that is data, so a session that turns native exit codes into errors must not apply.
$PSNativeCommandUseErrorActionPreference = $false

trap {
    Write-Host "SDK pin NOT checked: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}

function Stop-Unchecked([string]$Reason) {
    Write-Host "SDK pin NOT checked: $Reason" -ForegroundColor Red
    exit 2
}

function Invoke-SdkGit([string[]]$GitArgs) {
    $output = & git -C $SdkPath @GitArgs 2>&1
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Lines = @($output | ForEach-Object { "$_" }) }
}

if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }

# ------------------------------------------------------------------------------------ the pin
$refs = @(foreach ($project in Get-ChildItem -Path (Join-Path $RepoRoot '*' '*.csproj') -File) {
    Select-Xml -Path $project.FullName -XPath '//PackageReference[starts-with(@Include, "DiffusionNexus.Installer.SDK.")]' |
        ForEach-Object { [pscustomobject]@{ Project = $project.Name; Package = $_.Node.Include; Version = $_.Node.Version } }
})
if ($refs.Count -eq 0) { Stop-Unchecked "no DiffusionNexus.Installer.SDK.* PackageReference in any project under $RepoRoot." }
if (-not $Pin) {   # -Pin: an explicit version makes the project versions irrelevant
    $versions = @($refs | Select-Object -ExpandProperty Version -Unique)
    if ($versions.Count -ne 1) {
        $list = ($refs | ForEach-Object { "  $($_.Project): $($_.Package) $($_.Version)" }) -join "`n"
        Stop-Unchecked "the SDK references do not all pin the same version:`n$list"
    }
    $Pin = $versions[0]
}
$pinTag   = "v$Pin"
$packages = @($refs | Select-Object -ExpandProperty Package -Unique)

# ------------------------------------------------------------------------- the SDK checkout
if (-not $SdkPath) {
    $SdkPath = @($env:LocalSDKPath, (Join-Path $RepoRoot '..' 'DiffusionNexus.Installer.SDK'), 'E:\Repos\DiffusionNexus.Installer.SDK') |
        Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $SdkPath -or -not (Test-Path (Join-Path $SdkPath '.git'))) {
    Stop-Unchecked "no SDK git checkout found$(if ($SdkPath) { " at $SdkPath" }). Pass -SdkPath or set LocalSDKPath."
}

$fetch = Invoke-SdkGit @('fetch', '--quiet', '--tags', 'origin')
if ($fetch.ExitCode -ne 0) {
    Stop-Unchecked "git fetch in $SdkPath failed, so the newest SDK work is unknown:`n$($fetch.Lines -join "`n")"
}
$branchRef = "origin/$SdkBranch"
if ((Invoke-SdkGit @('rev-parse', '--verify', '--quiet', "$branchRef^{commit}")).ExitCode -ne 0) {
    Stop-Unchecked "$branchRef does not exist in $SdkPath."
}
if ((Invoke-SdkGit @('rev-parse', '--verify', '--quiet', "refs/tags/$pinTag^{commit}")).ExitCode -ne 0) {
    Stop-Unchecked "the pinned version $Pin has no tag $pinTag in the SDK repo."
}
foreach ($package in $packages) {
    if ((Invoke-SdkGit @('cat-file', '-e', "${branchRef}:$package")).ExitCode -ne 0) {
        Stop-Unchecked "package folder $package does not exist on $branchRef. A moved or renamed project would be invisible to this check."
    }
}

# ------------------------------------------------------------------- what the pin leaves out
$log = Invoke-SdkGit (@('log', '--no-merges', '--reverse', '--format=%H%x09%h%x09%s', "$pinTag..$branchRef", '--') + $packages)
if ($log.ExitCode -ne 0) { Stop-Unchecked "git log failed:`n$($log.Lines -join "`n")" }
$missing = @(foreach ($line in $log.Lines) {
    if ($line -match '^([0-9a-f]{40})\t(\S+)\t(.*)$') {
        [pscustomobject]@{ Hash = $Matches[1]; Short = $Matches[2]; Subject = $Matches[3] }
    }
})
if ($missing.Count -eq 0) {
    Write-Host "SDK pin $Pin includes everything on SDK $SdkBranch." -ForegroundColor Green
    exit 0
}

# Whether a released SDK version already ships them decides the advice: bump, or tag first. Only
# tags of the pin's own major version count - the old v1.x tags are reachable from develop too.
$major     = $Pin.Split('.')[0]
$newest    = Invoke-SdkGit @('describe', '--tags', '--abbrev=0', '--match', "v$major.*", $branchRef)
$newestTag = if ($newest.ExitCode -eq 0) { $newest.Lines[0].Trim() } else { $null }
$released  = @($missing | Where-Object { $newestTag -and (Invoke-SdkGit @('merge-base', '--is-ancestor', $_.Hash, $newestTag)).ExitCode -eq 0 })
$unreleased = @($missing | Where-Object { $_ -notin $released })

$noun = if ($missing.Count -eq 1) { 'commit' } else { 'commits' }
Write-Host "SDK pin $Pin is missing $($missing.Count) $noun from SDK ${SdkBranch}:" -ForegroundColor Yellow
$missing | ForEach-Object { Write-Host "  $($_.Short) $($_.Subject)" }
if ($unreleased.Count -eq 0) {
    Write-Host "All of them ship in $newestTag -> bump the SDK references to $($newestTag.Substring(1))." -ForegroundColor Yellow
} elseif ($released.Count -eq 0) {
    Write-Host "None of them is in a tagged SDK release yet -> tag and publish the SDK first, then bump the SDK references to the new version." -ForegroundColor Yellow
} else {
    Write-Host "Released in ${newestTag}: $($released.Count). In no tagged SDK release yet: $($unreleased.Count). -> tag and publish the SDK first, then bump the SDK references to the new version." -ForegroundColor Yellow
}
exit 3
```

- [ ] **Step 6: Run the tests and see them pass**

Run: `pwsh -NoProfile -File Scripts/Tests/Test-SdkPin.Tests.ps1`
Expected: 18 `PASS` lines, then `18 passed, 0 failed`, exit code 0. About a minute.

- [ ] **Step 7: Run it against the real repos**

Run: `pwsh -NoProfile -File Scripts/Test-SdkPin.ps1; $LASTEXITCODE`
Expected today: the pins say `2.0.0-preview.9`, SDK `develop` is `a50112f` (= `v2.0.0-preview.9` = `v2.0.0`), so `SDK pin 2.0.0-preview.9 includes everything on SDK develop.` and `0`. If SDK #72 has merged by now: exit `3`, listing its commits with "tag and publish the SDK first" (until `v2.1.0` is tagged) or "bump to 2.1.0". Either answer is the gate working; note which you got.

- [ ] **Step 8: Commit**

```powershell
git add Scripts/Test-SdkPin.ps1 Scripts/Tests/TestKit.ps1 Scripts/Tests/Test-SdkPin.Tests.ps1
git commit -m "feat(release): Test-SdkPin.ps1 - is the SDK pin behind SDK develop?"
```

---

### Task 2: `ReleaseAccount.ps1`, test first

**Files:**
- Create: `Scripts/ReleaseAccount.ps1`
- Create: `Scripts/Tests/ReleaseAccount.Tests.ps1`

**Interfaces:**
- Consumes: `Test-Case`, `Assert-Equal`, `Assert-Like`, `Complete-Tests` from `TestKit.ps1` (Task 1).
- Produces (dot-sourced): `Resolve-ReleaseToken <owner/repo>` → token string or `$null`; `Invoke-WithGhToken <token> { … }` → runs the block with `GH_TOKEN` set and restores it, `$LASTEXITCODE` is the block's; `Get-NoReleaseAccountMessage <owner/repo>` → the refusal text. Task 4 and #30 use all three.

- [ ] **Step 1: Create `Scripts/Tests/ReleaseAccount.Tests.ps1`** (the posted 9 cases, verbatim)

```powershell
#Requires -Version 7.2
# Tests for Scripts/ReleaseAccount.ps1. Run: pwsh -NoProfile -File Scripts/Tests/ReleaseAccount.Tests.ps1
# Exit code = number of failed cases.
#
# The fake `gh` below stands in for the real one - PowerShell resolves a function before an
# executable of the same name - so no test talks to GitHub. $global:FakeAccounts lists the signed-in
# accounts, active one first; `gh api repos/...` answers for whichever token GH_TOKEN holds.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestKit.ps1')
. (Join-Path $PSScriptRoot '..' 'ReleaseAccount.ps1')

$repo = 'Into-The-Latent/DiffusionNexus.Installer'

function global:gh {
    $call = $args -join ' '
    $global:FakeGhCalls.Add([pscustomobject]@{ Call = $call; Token = $env:GH_TOKEN })
    $global:LASTEXITCODE = 0
    $byToken = $global:FakeAccounts | Where-Object Token -eq $env:GH_TOKEN | Select-Object -First 1
    if ($call -eq 'auth token') { return $global:FakeAccounts[0].Token }
    if ($call -like 'auth token --user *') {
        $account = $global:FakeAccounts | Where-Object User -eq $args[3] | Select-Object -First 1
        if ($account) { return $account.Token }
        $global:LASTEXITCODE = 1; return
    }
    if ($call -like 'api repos/*') {
        if ($global:FakeApiDown) { $global:LASTEXITCODE = 1; return }
        return $(if ($byToken.CanPush) { 'true' } else { 'false' })
    }
    if ($call -like 'api user *') {
        $account = if ($byToken) { $byToken } else { $global:FakeAccounts[0] }
        return $account.User
    }
    $global:LASTEXITCODE = 1
}

function Set-FakeAccounts([hashtable[]]$Accounts, [switch]$ApiDown) {
    $global:FakeAccounts = $Accounts
    $global:FakeApiDown = [bool]$ApiDown
    $global:FakeGhCalls = [System.Collections.Generic.List[object]]::new()
}

$reader = @{ User = 'Little-God1983'; Token = 'tok-reader'; CanPush = $false }
$owner  = @{ User = 'Into-The-Latent'; Token = 'tok-owner'; CanPush = $true }

Test-Case 'the active account is used when it can write' {
    Set-FakeAccounts @(@{ User = 'Maintainer'; Token = 'tok-active'; CanPush = $true }, $owner)
    Assert-Equal (Resolve-ReleaseToken $repo) 'tok-active' 'the token'
    Assert-Equal @($global:FakeGhCalls | Where-Object Call -like 'auth token --user*').Count 0 'owner token lookups'
}

Test-Case 'a read-only active account falls back to the signed-in owner (the v3.0.10 case)' {
    Set-FakeAccounts @($reader, $owner)
    Assert-Equal (Resolve-ReleaseToken $repo) 'tok-owner' 'the token'
}

Test-Case 'the permission probe runs with the token it is judging' {
    Set-FakeAccounts @($reader, $owner)
    Resolve-ReleaseToken $repo | Out-Null
    $probes = @($global:FakeGhCalls | Where-Object Call -like 'api repos/*' | ForEach-Object Token)
    Assert-Equal ($probes -join ',') 'tok-reader,tok-owner' 'the tokens probed'
}

Test-Case 'no token when the owner is not signed in' {
    Set-FakeAccounts @($reader)
    Assert-Equal (Resolve-ReleaseToken $repo) $null 'the token'
}

Test-Case 'no token when no signed-in account can write' {
    Set-FakeAccounts @($reader, @{ User = 'Into-The-Latent'; Token = 'tok-owner'; CanPush = $false })
    Assert-Equal (Resolve-ReleaseToken $repo) $null 'the token'
}

Test-Case 'a failed permission probe (offline, revoked token) is never "can write"' {
    Set-FakeAccounts @($owner) -ApiDown
    Assert-Equal (Resolve-ReleaseToken $repo) $null 'the token'
}

Test-Case 'GH_TOKEN is left as it was: a value stays, no value stays unset' {
    Set-FakeAccounts @($reader, $owner)
    $env:GH_TOKEN = 'tok-from-profile'
    try {
        Resolve-ReleaseToken $repo | Out-Null
        Assert-Equal $env:GH_TOKEN 'tok-from-profile' 'GH_TOKEN after resolving'
    } finally { Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue }
    Resolve-ReleaseToken $repo | Out-Null
    Assert-Equal (Test-Path Env:GH_TOKEN) $false 'GH_TOKEN exists after resolving'
}

Test-Case 'Invoke-WithGhToken: gh sees the token, the exit code survives, GH_TOKEN is restored after a throw' {
    Set-FakeAccounts @($reader, $owner)
    Invoke-WithGhToken 'tok-owner' { gh release create v9.9.9 } | Out-Null
    Assert-Equal $LASTEXITCODE 1 'the exit code of an unknown fake call'
    Assert-Equal $global:FakeGhCalls[-1].Token 'tok-owner' 'the token gh saw'
    try { Invoke-WithGhToken 'tok-owner' { throw 'boom' } } catch { }
    Assert-Equal (Test-Path Env:GH_TOKEN) $false 'GH_TOKEN exists after a throw'
}

Test-Case 'the refusal names the active account and the one-time fix' {
    Set-FakeAccounts @($reader)
    $message = Get-NoReleaseAccountMessage $repo
    Assert-Like $message '*the active gh account (Little-God1983) has no write access*' 'the message'
    Assert-Like $message "*Sign Into-The-Latent in once with 'gh auth login'*" 'the message'
}

Remove-Item function:global:gh
Complete-Tests
```

- [ ] **Step 2: Run it and watch it fail**

Run: `pwsh -NoProfile -File Scripts/Tests/ReleaseAccount.Tests.ps1`
Expected: the dot-source of the missing `ReleaseAccount.ps1` throws before any case runs; exit code 1, error names `ReleaseAccount.ps1`.

- [ ] **Step 3: Create `Scripts/ReleaseAccount.ps1`** (the posted code, verbatim)

```powershell
# Which gh account publishes. Creating or editing a release needs write access to the installer
# repo, and the maintainer's everyday gh account may only have read access (Little-God1983 does).
# v3.0.10's first upload failed that way, after a five-minute build. So the release scripts find a
# token that can write BEFORE they change anything, and hand it to their own gh calls only: no
# `gh auth switch`, and the active account is never changed.

# The token of the first signed-in gh account that can push to $Repo: the active account first,
# then the repo owner's. $null when neither can, or when gh cannot tell - that must never read as yes.
function Resolve-ReleaseToken([string]$Repo) {
    $PSNativeCommandUseErrorActionPreference = $false
    $owner = $Repo.Split('/')[0]
    foreach ($user in @('', $owner)) {
        $tokenArgs = @('auth', 'token') + $(if ($user) { @('--user', $user) } else { @() })
        $token = "$(gh @tokenArgs 2>$null)".Trim()
        if ($LASTEXITCODE -ne 0 -or -not $token) { continue }
        $push = Invoke-WithGhToken $token { gh api "repos/$Repo" --jq '.permissions.push' 2>$null }
        if ($LASTEXITCODE -eq 0 -and "$push".Trim() -eq 'true') { return $token }
    }
    $null
}

# Runs $Command with gh authenticated as $Token, then puts GH_TOKEN back as it was - also when
# $Command throws. $LASTEXITCODE is the command's.
function Invoke-WithGhToken([string]$Token, [scriptblock]$Command) {
    $saved = $env:GH_TOKEN
    try {
        $env:GH_TOKEN = $Token
        & $Command
    } finally {
        $env:GH_TOKEN = $saved
    }
}

# The refusal both release scripts print, naming the account that is signed in now.
function Get-NoReleaseAccountMessage([string]$Repo) {
    $PSNativeCommandUseErrorActionPreference = $false
    $login = "$(gh api user --jq '.login' 2>$null)".Trim()
    $who = if ($LASTEXITCODE -eq 0 -and $login) { "the active gh account ($login) has" } else { 'gh has' }
    $owner = $Repo.Split('/')[0]
    "No signed-in gh account can create releases on ${Repo}: $who no write access, and $owner is not signed in or cannot write either. Sign $owner in once with 'gh auth login' - the release scripts then use it for their own gh calls, and you never need 'gh auth switch'."
}
```

- [ ] **Step 4: Run the tests and see them pass**

Run: `pwsh -NoProfile -File Scripts/Tests/ReleaseAccount.Tests.ps1`
Expected: `9 passed, 0 failed`, exit 0.

- [ ] **Step 5: Read-only proof against real GitHub**

```powershell
pwsh -NoProfile -Command ". Scripts/ReleaseAccount.ps1; (Resolve-ReleaseToken 'Into-The-Latent/DiffusionNexus.Installer') -ne `$null; (Resolve-ReleaseToken 'nobody-here-xyz/none') -eq `$null; gh auth status 2>&1 | Select-String 'Active account'"
```
Expected: `True`, `True`, and the active account line unchanged from before (Little-God1983).

- [ ] **Step 6: Commit**

```powershell
git add Scripts/ReleaseAccount.ps1 Scripts/Tests/ReleaseAccount.Tests.ps1
git commit -m "feat(release): ReleaseAccount.ps1 - find a gh account that can publish, without switching"
```

---

### Task 3: `--build-info` on the entry point, test first

**Files:**
- Create: `DiffusionNexus.Installer.Electron/Services/BuildInfo.cs`
- Modify: `DiffusionNexus.Installer.Electron/Program.cs` (top, before `WebApplication.CreateBuilder`)
- Create: `DiffusionNexus.Installer.Tests/Services/BuildInfoTests.cs`

**Interfaces:**
- Consumes: `AppVersion.Display`, `AppVersion.Strip(string?)` (existing), `DiffusionNexus.Installer.SDK.Catalog.CatalogSchema.Supported`.
- Produces: `static class BuildInfo` with `const string Flag = "--build-info"`, `static bool Handles(string[] args)`, `static string ToJson()`, `static BuildInfoDocument Create()`; `sealed record BuildInfoDocument(string App, string Sdk, int CatalogSchema, DateTimeOffset BuiltAt)`. #38 adds `CatalogSeed` to the record and the JSON. Task 4 reads `app`, `sdk`, `catalogSchema` from the JSON.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json;
using DiffusionNexus.Installer.Electron.Services;
using DiffusionNexus.Installer.SDK.Catalog;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Services;

public class BuildInfoTests
{
    [Fact]
    public void Handles_only_the_exact_flag()
    {
        // Electron starts the .NET side with its own arguments; only the release script passes
        // --build-info, and it must be matched as a plain argument, never as a prefix or a fragment.
        BuildInfo.Handles(["--build-info"]).Should().BeTrue();
        BuildInfo.Handles(["/electronPort=1234", "--build-info"]).Should().BeTrue();
        BuildInfo.Handles([]).Should().BeFalse();
        BuildInfo.Handles(["/electronPort=1234"]).Should().BeFalse();
        BuildInfo.Handles(["--build-info=1"]).Should().BeFalse();
        BuildInfo.Handles(["--build-infos"]).Should().BeFalse();
    }

    [Fact]
    public void The_document_names_this_build()
    {
        var info = BuildInfo.Create();

        info.App.Should().Be(AppVersion.Display);
        info.Sdk.Should().MatchRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$", "the informational version minus any +sha");
        info.Sdk.Should().Be(AppVersion.Strip(typeof(CatalogSchema).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion));
        info.CatalogSchema.Should().Be(CatalogSchema.Supported);
        info.BuiltAt.Should().BeAfter(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
            .And.BeBefore(DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public void The_json_is_one_object_with_the_release_scripts_property_names()
    {
        // New-Release.ps1 reads .app, .sdk and .catalogSchema from this text with ConvertFrom-Json,
        // and the catalog repo's gate reads .catalogSchema. The names are a contract.
        var text = BuildInfo.ToJson();

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        root.ValueKind.Should().Be(JsonValueKind.Object);
        root.GetProperty("app").GetString().Should().Be(AppVersion.Display);
        root.GetProperty("sdk").GetString().Should().NotContain("+");
        root.GetProperty("catalogSchema").GetInt32().Should().Be(CatalogSchema.Supported);
        root.GetProperty("builtAt").GetDateTimeOffset().Should().Be(BuildInfo.Create().BuiltAt);
        text.Should().NotContain("\"App\"", "property names are camelCase");
    }
}
```

Add `using System.Reflection;` at the top of the file (the second test uses `GetCustomAttribute`).

- [ ] **Step 2: Run them and watch the build fail**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~BuildInfoTests"`
Expected: `error CS0103: The name 'BuildInfo' does not exist in the current context`.

- [ ] **Step 3: Create `Services/BuildInfo.cs`**

```csharp
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiffusionNexus.Installer.SDK.Catalog;

namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>What this build says about itself: the `--build-info` answer, uploaded by
/// New-Release.ps1 as the `build-info.json` release asset. The property names are read by the
/// release scripts and by the catalog repo's schema gate, so they are a contract.</summary>
public sealed record BuildInfoDocument(
    [property: JsonPropertyName("app")] string App,
    [property: JsonPropertyName("sdk")] string Sdk,
    [property: JsonPropertyName("catalogSchema")] int CatalogSchema,
    [property: JsonPropertyName("builtAt")] DateTimeOffset BuiltAt);

public static class BuildInfo
{
    public const string Flag = "--build-info";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Exactly this argument, anywhere in the list. Electron passes its own arguments to
    /// the .NET side, and none of them may ever turn a normal launch into a print-and-exit.</summary>
    public static bool Handles(string[] args) => args.Contains(Flag, StringComparer.Ordinal);

    public static BuildInfoDocument Create()
    {
        // The Catalog assembly is the one whose version the release gates judge: the pin check
        // reads the same version from the csproj, and the schema constant lives here.
        var catalogAssembly = typeof(CatalogSchema).Assembly;
        var sdk = AppVersion.Strip(catalogAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        // The entry assembly's write time: no MSBuild-generated timestamp, so nothing forces a
        // rebuild on every build, and the packaged file is what the release script runs anyway.
        var entry = Assembly.GetEntryAssembly()?.Location ?? typeof(BuildInfo).Assembly.Location;
        var builtAt = File.Exists(entry) ? new DateTimeOffset(File.GetLastWriteTimeUtc(entry), TimeSpan.Zero) : DateTimeOffset.UtcNow;
        return new BuildInfoDocument(AppVersion.Display, sdk, CatalogSchema.Supported, builtAt);
    }

    public static string ToJson() => JsonSerializer.Serialize(Create(), Json);
}
```

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~BuildInfoTests"`
Expected: 3 passed.

- [ ] **Step 5: Hook the entry point**

In `Program.cs`, insert directly above `var builder = WebApplication.CreateBuilder(args);`:

```csharp
// The release script asks the PACKAGED app what it contains and ships the answer as
// build-info.json. Before any host, logger or window exists: this must print one JSON object and
// nothing else, and exit 0.
if (BuildInfo.Handles(args))
{
    Console.Out.Write(BuildInfo.ToJson());
    return 0;
}

```

Top-level statements may `return 0;` (the generated `Main` returns `int`); `app.Run()` at the bottom stays as it is. `DiffusionNexus.Installer.Electron.Services` is already imported.

- [ ] **Step 6: Prove it on the built exe**

```powershell
dotnet build DiffusionNexus.Installer.Electron -c Debug --nologo | Select-String -Pattern "error" ; $LASTEXITCODE
$exe = Get-ChildItem DiffusionNexus.Installer.Electron/bin/Debug/net10.0/win-x64 -Filter DiffusionNexus.Installer.Electron.exe -Recurse | Select-Object -First 1
& $exe.FullName --build-info | ConvertFrom-Json | Format-List; $LASTEXITCODE
```
Expected: an object with `app` (= the `<Version>` in `Directory.Build.props`), `sdk` (the local SDK's version when built with the redirect, e.g. `2.0.0` or `2.1.0`, no `+sha`), `catalogSchema` `1`, `builtAt` a recent timestamp; exit `0`; no window, no log line.

- [ ] **Step 7: Commit**

```powershell
git add DiffusionNexus.Installer.Electron/Services/BuildInfo.cs DiffusionNexus.Installer.Electron/Program.cs DiffusionNexus.Installer.Tests/Services/BuildInfoTests.cs
git commit -m "feat(release): the entry point answers --build-info with app, sdk and catalogSchema"
```

---

### Task 4: `New-Release.ps1` gates, `build-info.json`, CI, README

**Files:**
- Modify: `Scripts/New-Release.ps1`
- Modify: `.github/workflows/build.yml`
- Modify: `README.md` ("Publishing a release")

**Interfaces:**
- Consumes: `Test-SdkPin.ps1` exit codes (Task 1); `Resolve-ReleaseToken`, `Invoke-WithGhToken`, `Get-NoReleaseAccountMessage` (Task 2); the `--build-info` JSON with `app`, `sdk`, `catalogSchema` (Task 3).
- Produces: `New-Release.ps1 -AllowOlderSdk`; the `build-info.json` release asset; the notes line `Built with Installer SDK <sdk>`. #38 adds step 0d and the catalog line; #30 reads the asset.

- [ ] **Step 1: Help text.** In the comment help, replace the paragraph starting `    To promote a Preview build to everyone, un-mark it` (three lines, through the `gh release edit …` line) with:

```text
    To promote a Preview build to everyone, un-mark it - no rebuild, same binaries. Today that is
    the gh command below; Promote-Release.ps1 (issue #30) will check the SDK pin and the catalog
    seed against what is current first.
        gh release edit v3.0.9 --repo Into-The-Latent/DiffusionNexus.Installer --prerelease=false --latest

.PARAMETER AllowOlderSdk
    Release even though Scripts/Test-SdkPin.ps1 found commits on SDK develop that the pinned SDK
    version does not contain - a deliberate hold-back, such as a Stable hotfix while newer SDK work
    is meant for Preview only. The commits left out are still listed. It does not override a check
    that could not run (no SDK checkout, a failed fetch, pins that disagree).
```

- [ ] **Step 2: Parameter.** Replace

```powershell
    [switch]$Prerelease
)
```
with
```powershell
    [switch]$Prerelease,
    [switch]$AllowOlderSdk
)
```

- [ ] **Step 3: Step 0.** Delete the existing `if (-not $env:GITHUB_PACKAGES_TOKEN) { throw … }` block together with the comment paragraph above it (from `# A release must be built from the PUBLISHED SDK packages` down to the closing `}`), and insert this block directly above `Write-Host "Setting version to $Version" -ForegroundColor Cyan`:

```powershell
# ---------------------------------------------------------------------------------------- Step 0
# Everything here runs BEFORE this script changes anything on disk. A refusal leaves the working
# tree exactly as it was: no version write, no cleared publish folder.

# Step 0a. A release must be built from the PUBLISHED SDK packages, never from a local checkout.
# Directory.Build.targets auto-enables UseLocalSDK whenever E:\Repos\DiffusionNexus.Installer.SDK
# exists - and it exists on every dev machine here - so without this pin a release silently embeds
# whatever branch the SDK repo happens to have checked out, and cannot be reproduced from a clean
# clone. Pinned explicitly rather than left to the environment, because a User-scope
# UseLocalSDK=true survives shells and would otherwise win. The token is what that restore needs.
Write-Host "Step 0a: the packages token is present" -ForegroundColor Cyan
if (-not $env:GITHUB_PACKAGES_TOKEN) {
    throw "GITHUB_PACKAGES_TOKEN is not set. The release build restores the SDK from GitHub Packages (see nuget.config) and would fail the restore. Nothing was built or changed."
}

# Step 0b. The upload at the very end needs write access to the repo, and the everyday gh account
# may only have read access - v3.0.10's first upload failed that way, after the whole build. Find a
# signed-in account that can publish now; only the upload gets its token, the active account stays.
. (Join-Path $PSScriptRoot 'ReleaseAccount.ps1')
if (-not $SkipUpload) {
    Write-Host "Step 0b: a signed-in gh account can publish to $ghRepo" -ForegroundColor Cyan
    $releaseToken = Resolve-ReleaseToken $ghRepo
    if (-not $releaseToken) { throw "$(Get-NoReleaseAccountMessage $ghRepo) Nothing was built or changed." }
}

# Step 0c. A release must not leave out SDK work that is already on the SDK's develop branch:
# v3.0.9 shipped SDK 2.0.0-preview.8 the day after preview.9 (the Manager-aware Update-ComfyUI.bat)
# was published, because nothing compared the pin with the SDK.
Write-Host "Step 0c: the SDK pin includes everything on SDK develop" -ForegroundColor Cyan
pwsh -NoProfile -File (Join-Path $repoRoot 'Scripts\Test-SdkPin.ps1')
switch ($LASTEXITCODE) {
    0 { }
    3 {
        if (-not $AllowOlderSdk) {
            throw "The SDK pin is behind SDK develop (listed above). Bump it, or re-run with -AllowOlderSdk to release without those commits on purpose. Nothing was built or changed."
        }
        Write-Warning "Releasing WITHOUT the SDK commits listed above (-AllowOlderSdk)."
    }
    default { throw "The SDK pin could not be checked (see above). Nothing was built or changed." }
}

```

- [ ] **Step 4: The SDK pin as a variable.** In the existing hash-check block, directly after `if ($sdkRefs.Count -eq 0) { throw "No SDK PackageReferences found in the Electron csproj." }`, add:

```powershell
$sdkPin = @($sdkRefs | Select-Object -ExpandProperty Version -Unique)
if ($sdkPin.Count -ne 1) { throw "The Electron csproj pins more than one SDK version: $($sdkPin -join ', ')" }
$sdkPin = $sdkPin[0]
```

- [ ] **Step 5: Step 1c.** Insert directly above `Write-Host "Step 2/3: repackaging with the publish config (emits app-update.yml)" -ForegroundColor Cyan`:

```powershell
# Step 1c: build-info.json. The PACKAGED app is asked what it contains, so the asset is what the
# binary says, not what this script assumed - and the answer has to agree with what Step 0
# checked, or this is a build that does not contain what was checked. Promote-Release.ps1 and the
# catalog repo's schema gate read this asset; nothing reads the release notes back.
Write-Host "Step 1c: build-info.json from the packaged app" -ForegroundColor Cyan
$entryPoint = Join-Path $publish 'bin\DiffusionNexus.Installer.Electron.exe'
if (-not (Test-Path $entryPoint)) { throw "Packaged entry point missing: $entryPoint" }
$buildInfoText = (& $entryPoint --build-info | ForEach-Object { "$_" }) -join "`n"
if ($LASTEXITCODE -ne 0) { throw "The packaged app did not answer --build-info (exit $LASTEXITCODE):`n$buildInfoText" }
try { $buildInfo = $buildInfoText | ConvertFrom-Json }
catch { throw "The packaged app's --build-info answer is not JSON:`n$buildInfoText" }
if ($buildInfo.app -ne $Version) { throw "The packaged app says it is version '$($buildInfo.app)', not $Version." }
if ($buildInfo.sdk -ne $sdkPin) { throw "The packaged app says it was built with SDK $($buildInfo.sdk); the projects pin $sdkPin. This build does not contain what was checked." }
$buildInfoPath = Join-Path $publish 'build-info.json'
Set-Content $buildInfoPath $buildInfoText -Encoding utf8 -NoNewline
Write-Host "  app $($buildInfo.app), SDK $($buildInfo.sdk), catalog schema $($buildInfo.catalogSchema)" -ForegroundColor Green

```

- [ ] **Step 6: Upload.** Replace the block from `$setup = Join-Path $publish "EasyWorkloadInstaller-ITL-Setup-$Version.exe"` through `if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }` with:

```powershell
$setup = Join-Path $publish "EasyWorkloadInstaller-ITL-Setup-$Version.exe"
foreach ($f in @($setup, "$setup.blockmap", (Join-Path $publish 'latest.yml'), $buildInfoPath)) {
    if (-not (Test-Path $f)) { throw "Expected artifact missing: $f" }
}
# The notes end with what the build contains, generated from the same data as build-info.json.
# Human lines only; nothing reads them back.
$sdkLine = "Built with Installer SDK $($buildInfo.sdk)"
$notesWithBuild = if ($Notes.Trim()) { "$($Notes.TrimEnd())`n`n$sdkLine" } else { $sdkLine }
# latest.yml for both channels: electron-updater reads it for any tag without a suffix, even
# with allowPrerelease set, so a Preview build needs no separately named channel file and a
# promoted one is already complete.
$ghArgs = @('release', 'create', "v$Version", $setup, "$setup.blockmap", (Join-Path $publish 'latest.yml'), $buildInfoPath,
            '--repo', $ghRepo, '--title', $Version, '--notes', $notesWithBuild)
if ($Prerelease) { $ghArgs += '--prerelease' }
# Under the token Step 0b resolved: the active gh account may be read-only here.
Invoke-WithGhToken $releaseToken { gh @ghArgs }
if ($LASTEXITCODE -ne 0) { throw "gh release create failed (see gh's output above). The release was not created." }
```

- [ ] **Step 7: Final hint.** Replace the last `Write-Host "Promote it to Stable with: …"` line's text with:

```powershell
    Write-Host "Promote it to Stable, once the pin and the seed are still current: gh release edit v$Version --repo $ghRepo --prerelease=false --latest (Promote-Release.ps1 will do these checks; issue #30)" -ForegroundColor Yellow
```

- [ ] **Step 8: Prove the refusals change nothing (no token needed)**

```powershell
$saved = $env:GITHUB_PACKAGES_TOKEN; Remove-Item Env:GITHUB_PACKAGES_TOKEN -ErrorAction SilentlyContinue
pwsh -NoProfile -File Scripts/New-Release.ps1 -Version 9.9.9 -SkipUpload; $LASTEXITCODE; git status --short Directory.Build.props
$env:GITHUB_PACKAGES_TOKEN = $saved
```
Expected: `Step 0a: …`, the `GITHUB_PACKAGES_TOKEN is not set … Nothing was built or changed.` error, exit `1`, and no `git status` line: `Directory.Build.props` untouched (before this change the version was written first).

Then with the token set and a pin the check refuses:

```powershell
$env:GITHUB_PACKAGES_TOKEN = 'x'   # any value: 0a only checks presence
pwsh -NoProfile -File Scripts/New-Release.ps1 -Version 9.9.9 -SkipUpload -AllowOlderSdk:$false; $LASTEXITCODE; git status --short Directory.Build.props
```
Expected: `Step 0a`, `Step 0c` (0b is skipped by `-SkipUpload`), then either `Setting version to 9.9.9` (pin current: stop it there with Ctrl+C or let publish fail on the fake token; then `git checkout -- Directory.Build.props`) or the refusal with exit `1` and an untouched props file. Note which.

- [ ] **Step 9: CI.** In `.github/workflows/build.yml`, insert directly after `      - uses: actions/checkout@v4` and its blank line, before `actions/setup-dotnet`:

```yaml
      # The release scripts' own tests (Scripts/Tests). They build throwaway git repos and fake gh,
      # so they need neither the private SDK repo nor the packages token - nor .NET, so they run first.
      - name: Release script tests
        shell: pwsh
        run: |
          $failed = 0
          foreach ($test in Get-ChildItem Scripts/Tests -Filter *.Tests.ps1) {
            pwsh -NoProfile -File $test.FullName
            if ($LASTEXITCODE -ne 0) { $failed++ }
          }
          exit $failed

```

- [ ] **Step 10: Run the CI loop locally**

Paste the `run:` block into `pwsh` from the repo root.
Expected: `18 passed, 0 failed`, `9 passed, 0 failed`, exit 0.

- [ ] **Step 11: README.** Under `## Publishing a release`, replace the line `gh release edit v3.0.6 --repo Into-The-Latent/DiffusionNexus.Installer --prerelease=false --latest` in the code block with the same command plus a trailing comment `  # Promote-Release.ps1 (issue #30) will replace this`, and insert this between the closing ```` ``` ```` of that block and `Do not hand-roll this. …`, with a blank line on each side:

```markdown
Before it changes or builds anything, `New-Release.ps1` runs three gates. **0a** the packages
token is set. **0b** a signed-in `gh` account can write to this repo: the active account if it
can, otherwise the signed-in Into-The-Latent account; if neither can, it stops here, and no `gh
auth switch` is ever needed (only the script's own upload uses that token). **0c**
`Scripts/Test-SdkPin.ps1` fetches your SDK checkout and stops the release when SDK `develop` has
commits in the pinned packages that the pinned version does not contain, listing them and saying
whether to bump the pin or to tag and publish the SDK first. Add `-AllowOlderSdk` to leave them
out on purpose. A refusal leaves the working tree untouched. Run the pin check on its own at any
time with `pwsh Scripts/Test-SdkPin.ps1`.

After packaging, the script runs the packaged app with `--build-info` and uploads its answer as
`build-info.json` next to the installer: the app version, the SDK version it was built with and
the catalog schema it reads. That asset, not the release notes, is what promotion and the catalog
repo's gate read.
```

Also replace `Assets are uploaded with `gh`, which uses your existing login rather than a token in the build.` with:

```markdown
Assets are uploaded with `gh` under the account gate 0b found, never by switching your active login.
```

- [ ] **Step 12: Commit**

```powershell
git add Scripts/New-Release.ps1 .github/workflows/build.yml README.md
git commit -m "fix(release): New-Release gates before it writes anything; build-info.json asset"
```

---

### Task 5: Move the pins to the plain SDK version, packaged proof, PR

**Files:**
- Modify: `DiffusionNexus.Installer.Core/DiffusionNexus.Installer.Core.csproj:10-13`
- Modify: `DiffusionNexus.Installer.Electron/DiffusionNexus.Installer.Electron.csproj:96-99`

**Interfaces:**
- Consumes: SDK tag `v2.0.0` (exists on `a50112f`); the published `2.0.0` packages on GitHub Packages.

- [ ] **Step 1: Bump all eight references.** Replace every `Version="2.0.0-preview.9"` on a `DiffusionNexus.Installer.SDK.*` `PackageReference` with `Version="2.0.0"`. If Task 1 Step 7 named a newer plain tag whose packages are published, use that version.

- [ ] **Step 2: The check passes on the new pin**

Run: `pwsh -NoProfile -File Scripts/Test-SdkPin.ps1; $LASTEXITCODE`
Expected: `SDK pin 2.0.0 includes everything on SDK develop.` and `0` (or, if SDK #72 has merged, the "tag and publish first" refusal naming its commits; then wait for `v2.1.0` and pin that).

- [ ] **Step 3: Build and test against the real packages**

Needs `$env:GITHUB_PACKAGES_TOKEN` (read:packages on the Little-God1983 feed) **and the `2.0.0` packages on the registry**. As of 2026-09-30 the `v2.0.0` publish run was refused by GitHub billing; until it is re-run this step cannot pass, and the PR stays a draft.

```powershell
dotnet restore DiffusionNexus.Installer.slnx -p:UseLocalSDK=false
dotnet build DiffusionNexus.Installer.slnx -c Release --no-restore -p:UseLocalSDK=false
dotnet test DiffusionNexus.Installer.slnx -c Release --no-build -p:UseLocalSDK=false
pwsh Scripts/Generate-ThirdPartyNotices.ps1 -Check
```
Expected: restore resolves `DiffusionNexus.Installer.SDK.* 2.0.0`, 0 errors, all tests pass, notices current.

- [ ] **Step 4: Packaged proof**

Run: `.\Scripts\New-Release.ps1 -Version 3.0.11 -SkipUpload` (several minutes)
Expected in order: `Step 0a`, `Step 0c` + `SDK pin 2.0.0 includes everything`, `Setting version to 3.0.11`, `SDK assemblies are byte-identical to the pinned packages`, `Step 1b`, `Step 1c` + `app 3.0.11, SDK 2.0.0, catalog schema 1`, `app-update.yml present`, `SkipUpload set - done.` Then `Get-Content DiffusionNexus.Installer.Electron/bin/Release/net10.0/win-x64/publish/build-info.json` shows the four properties.

- [ ] **Step 5: The owed #29 smoke**

Install the packaged `EasyWorkloadInstaller-ITL-Setup-3.0.11.exe` from the publish folder (or the published 3.0.10, which carries the same SDK content). Install **Blank ComfyUI + Manager + Triton & SageAttention** into an empty folder, then:

```powershell
Select-String -Path '<install folder>\ComfyUI\Update-ComfyUI.bat' -Pattern 'dn-script-version: 2', 'git pull origin master' -SimpleMatch
```
Expected: a hit on `dn-script-version: 2`, none on `git pull origin master`.

- [ ] **Step 6: Drop the smoke's version write, commit, push, PR**

```powershell
git checkout -- Directory.Build.props
git add DiffusionNexus.Installer.Core/DiffusionNexus.Installer.Core.csproj DiffusionNexus.Installer.Electron/DiffusionNexus.Installer.Electron.csproj
git commit -m "build: consume SDK 2.0.0 (plain version; same content as 2.0.0-preview.9)"
git -c credential.helper= -c "credential.helper=!f() { echo username=Into-The-Latent; echo password=$(gh auth token --user Into-The-Latent); }; f" push -u origin feature/release-gates-sdk
```

Then `gh pr create --repo Into-The-Latent/DiffusionNexus.Installer --base main --title "Release gates 1/2: token, account and SDK pin before anything changes; build-info.json (#37)" --body-file <file>` under `GH_TOKEN=$(gh auth token --user Into-The-Latent)`, as a draft (`--draft`) while Step 3 is blocked. Body: closes #37; the three gates and their exit-code contract; `--build-info` and the asset; the pin move; test counts (18 + 9 script cases, 3 xunit); what was proven by hand (Task 4 Step 8, Task 5 Steps 4 and 5) and what is still blocked; ends with the Claude Code attribution line.
