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

Test-Case 'changes that do not ship never count: tests, the dn-catalog tool, docs, a version bump, package metadata, a comment' {
    $f = New-SdkFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Tests/ServiceTests.cs' 'test: more tests'
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Catalog.Tool/Program.cs' 'feat: dn-catalog change'
    Add-SdkCommit $f 'Directory.Build.props' 'build: version bump' -Content (New-SdkProps '2.0.0-preview.2')
    Add-SdkCommit $f 'Directory.Build.props' 'build: repo moved' -Content (New-SdkProps '2.0.0-preview.2' -RepositoryUrl 'https://github.com/y/sdk')
    Add-SdkCommit $f 'Directory.Build.props' 'build: comment' -Content (New-SdkProps '2.0.0-preview.2' -RepositoryUrl 'https://github.com/y/sdk' -Extra '<!-- why -->')
    Add-SdkCommit $f 'docs/notes.md' 'docs: note'
    Assert-Result (Invoke-SdkPinCheck $f) 0
}

Test-Case 'a root build file change that affects the build counts: MSBuild imports it into every package' {
    $f = New-SdkFixture
    Add-SdkCommit $f 'Directory.Build.props' 'build: language version' -Content (New-SdkProps '2.0.0-preview.1' -Extra '<LangVersion>latest</LangVersion>')
    Assert-Result (Invoke-SdkPinCheck $f) 3 -Contains 'build: language version'
    $g = New-SdkFixture
    Add-SdkCommit $g 'Directory.Packages.props' 'build: central pin' -Content '<Project><ItemGroup><PackageVersion Include="X" Version="1" /></ItemGroup></Project>'
    Assert-Result (Invoke-SdkPinCheck $g) 3 -Contains 'build: central pin'
    # A dependency pin in child-element form is a <Version> too - the 1.2.35 SQLitePCLRaw security pin looked like this.
    $h = New-SdkFixture
    Add-SdkCommit $h 'Directory.Build.props' 'build: pin sqlite' -Content (New-SdkProps '2.0.0-preview.1' -SqlitePin '2.1.12')
    Assert-Result (Invoke-SdkPinCheck $h) 3 -Contains 'build: pin sqlite'
}

Test-Case 'a tag [semver] cannot parse is skipped, not fatal' {
    # One oddly named tag anywhere in develop's history must not turn "behind" into exit 2.
    $f = New-SdkFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: unreleased change'
    Add-SdkTag $f '2.1.0-rc.01'
    Assert-Result (Invoke-SdkPinCheck $f) 3 -Contains 'None of them is in a tagged SDK release yet' -Lacks 'NOT checked'
}

Test-Case 'tags that exist only in the local SDK clone are not releases' {
    # The SDK publish flow is tag, then push. Between the two, a local-only tag must count neither as
    # the newest release nor as the pin's own tag.
    $f = New-SdkFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: unreleased change'
    Invoke-FixtureGit $f.Sdk @('fetch', '--quiet', 'origin')
    Invoke-FixtureGit $f.Sdk @('tag', 'v2.1.0', 'origin/develop')
    Assert-Result (Invoke-SdkPinCheck $f) 3 -Contains 'None of them is in a tagged SDK release yet' -Lacks 'v2.1.0'
    Set-InstallerPins $f '2.1.0'
    Assert-Result (Invoke-SdkPinCheck $f) 2 -Contains 'the pinned version 2.1.0 has no tag v2.1.0'
}

Test-Case 'the check never touches the local SDK clone''s own tags' {
    # The clone is the maintainer's everyday working repo. A tag re-pointed locally (to republish after a
    # failed publish) must survive the check exactly as it was.
    $f = New-SdkFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: to republish'
    Invoke-FixtureGit $f.Sdk @('fetch', '--quiet', 'origin')
    Invoke-FixtureGit $f.Sdk @('tag', '--force', 'v2.0.0-preview.1', 'origin/develop')
    $before = (& git -C $f.Sdk rev-parse 'refs/tags/v2.0.0-preview.1').Trim()
    Assert-Result (Invoke-SdkPinCheck $f) 3 -Contains 'fix: to republish'
    Assert-Equal (& git -C $f.Sdk rev-parse 'refs/tags/v2.0.0-preview.1').Trim() $before 'local tag after the check'
}

Test-Case 'a tag the SDK moved (re-published after a failed publish) is taken from the remote, not refused' {
    # git 2.20+ refuses a plain --tags fetch that would move a local tag ("would clobber existing tag"),
    # and a refused fetch is exit 2, which nothing overrides. Only the remote's tags matter here.
    $f = New-SdkFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: republished'
    Invoke-FixtureGit $f.Author @('tag', '--force', '--annotate', 'v2.0.0-preview.1', '-m', 'moved')
    Invoke-FixtureGit $f.Author @('push', '--quiet', '--force', 'origin', 'v2.0.0-preview.1')
    Assert-Result (Invoke-SdkPinCheck $f) 0 -Lacks 'would clobber'
}

Test-Case 'a newer SDK major that ships the commits is named: tags are not limited to the pin''s major' {
    $f = New-SdkFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'feat!: breaking change'
    Add-SdkTag $f '3.0.0'
    Assert-Result (Invoke-SdkPinCheck $f) 3 -Contains 'All of them ship in v3.0.0 -> bump the SDK references to 3.0.0.'
}

Test-Case 'a candidate folder that exists but is not a git checkout is skipped, not fatal' {
    $f = New-SdkFixture
    $notGit = Join-Path $f.Root 'notgit'
    New-Item -ItemType Directory -Path $notGit | Out-Null
    # The second candidate, <RepoRoot>\..\DiffusionNexus.Installer.SDK, is a real clone here.
    Invoke-FixtureGit $f.Root @('clone', '--quiet', $f.Remote, (Join-Path $f.Root 'DiffusionNexus.Installer.SDK'))
    $saved = $env:LocalSDKPath
    $env:LocalSDKPath = $notGit
    try { Assert-Result (Invoke-SdkPinCheck $f -NoSdkPath) 0 -Contains 'includes everything' }
    finally { $env:LocalSDKPath = $saved }
}

Test-Case 'a commit that touches a package and its tests is listed once' {
    $f = New-SdkFixture
    Add-SdkCommit $f @('DiffusionNexus.Installer.SDK.Services/Service.cs', 'DiffusionNexus.Installer.SDK.Tests/ServiceTests.cs') 'fix: service with tests'
    Assert-Result (Invoke-SdkPinCheck $f) 3 -Contains 'is missing 1 commit from SDK develop:', 'fix: service with tests'
}

Test-Case 'the SDK checkout''s own branch, commits and edits are ignored - only origin/develop counts' {
    $f = New-SdkFixture
    Invoke-FixtureGit $f.Sdk @('switch', '--quiet', '--create', 'feature/local-work')
    Add-Content -LiteralPath (Join-Path $f.Sdk 'DiffusionNexus.Installer.SDK.Services' 'Local.cs') -Value 'local'
    Invoke-FixtureGit $f.Sdk @('add', '--all')
    Invoke-FixtureGit $f.Sdk @('commit', '--quiet', '-m', 'wip: local only')
    Add-Content -LiteralPath (Join-Path $f.Sdk 'DiffusionNexus.Installer.SDK.Models' 'Dirty.cs') -Value 'uncommitted'
    Assert-Result (Invoke-SdkPinCheck $f) 0 -Lacks 'wip: local only'
}

Test-Case 'a session that turns native exit codes into errors does not break the check' {
    $f = New-SdkFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: released change'
    Add-SdkTag $f '2.0.0-preview.2'
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Models/Model.cs' 'feat: unreleased change'
    $env:SDKPIN_TEST_SCRIPT = Join-Path $PSScriptRoot '..' 'Test-SdkPin.ps1'
    $env:SDKPIN_TEST_REPOROOT = $f.Installer
    $env:SDKPIN_TEST_SDKPATH = $f.Sdk
    try {
        $command = '$PSNativeCommandUseErrorActionPreference = $true; & $env:SDKPIN_TEST_SCRIPT -RepoRoot $env:SDKPIN_TEST_REPOROOT -SdkPath $env:SDKPIN_TEST_SDKPATH; exit $LASTEXITCODE'
        $output = & pwsh -NoProfile -Command $command 2>&1
        $result = [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = ($output | ForEach-Object { "$_" }) -join "`n" }
    } finally { $env:SDKPIN_TEST_SCRIPT = $null; $env:SDKPIN_TEST_REPOROOT = $null; $env:SDKPIN_TEST_SDKPATH = $null }
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
    Add-Content -LiteralPath (Join-Path $f.Installer 'Installer.Core' 'Installer.Core.csproj') -Value '<<<<<<< HEAD'
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

Test-Case 'a clone whose fetch refspec no longer covers develop still sees new develop commits' {
    # `git remote set-branches` (or a --single-branch clone) narrows remote.origin.fetch. A plain
    # `git fetch origin` then leaves origin/develop where it was, and a stale ref reads as "current":
    # a false pass, the one answer this gate must never give.
    $f = New-SdkFixture
    Invoke-FixtureGit $f.Sdk @('remote', 'set-branches', 'origin', 'some-other-branch')
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: unseen by a narrowed fetch'
    Assert-Result (Invoke-SdkPinCheck $f) 3 -Contains 'fix: unseen by a narrowed fetch'
}

Test-Case 'a local branch or tag named origin/<branch> does not shadow the remote branch' {
    # git resolves a short name against refs/tags and refs/heads before refs/remotes, so the classic slip
    # `git checkout -b origin/develop` (or `git tag origin/develop`) left at an old commit read as "current":
    # the fetch updated refs/remotes/origin/develop, the log walked the stray local ref.
    $f = New-SdkFixture
    Invoke-FixtureGit $f.Sdk @('branch', 'origin/develop', 'HEAD')
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: unseen behind a local branch named origin/develop'
    Assert-Result (Invoke-SdkPinCheck $f) 3 -Contains 'fix: unseen behind a local branch named origin/develop'
    $g = New-SdkFixture
    Invoke-FixtureGit $g.Sdk @('tag', 'origin/develop', 'HEAD')
    Add-SdkCommit $g 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: unseen behind a tag named origin/develop'
    Assert-Result (Invoke-SdkPinCheck $g) 3 -Contains 'fix: unseen behind a tag named origin/develop'
}

Test-Case 'a commit that touches only a file with a non-ASCII name counts' {
    # core.quotePath prints such a path C-quoted and escaped ("...\303\234bersetzung.json"), which matched no
    # package prefix, so the commit was dropped: a false "current".
    $f = New-SdkFixture
    Add-SdkCommit $f "DiffusionNexus.Installer.SDK.Services/Resources/$([char]0x00DC)bersetzung.json" 'feat: German strings'
    Assert-Result (Invoke-SdkPinCheck $f) 3 -Contains 'feat: German strings'
}

Test-Case 'a clone whose fetch refspec maps tags: its own tags stay untouched and the check still runs' {
    # git applies the configured remote.origin.fetch lines to every ref it fetched even when the command names
    # its own refspecs. `+refs/tags/*:refs/tags/*` (the "always update tags" recipe) then re-pointed the clone's
    # own re-pointed tag; the non-forced form refused the fetch, which is exit 2. --refmap= turns the configured
    # mapping off for this fetch.
    foreach ($refspec in @('+refs/tags/*:refs/tags/*', 'refs/tags/*:refs/tags/*')) {
        $f = New-SdkFixture
        Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: to republish'
        Invoke-FixtureGit $f.Sdk @('fetch', '--quiet', 'origin')
        Invoke-FixtureGit $f.Sdk @('tag', '--force', 'v2.0.0-preview.1', 'origin/develop')
        Invoke-FixtureGit $f.Sdk @('config', '--add', 'remote.origin.fetch', $refspec)
        $before = (& git -C $f.Sdk rev-parse 'refs/tags/v2.0.0-preview.1').Trim()
        Assert-Result (Invoke-SdkPinCheck $f) 3 -Contains 'fix: to republish' -Lacks 'would clobber'
        Assert-Equal (& git -C $f.Sdk rev-parse 'refs/tags/v2.0.0-preview.1').Trim() $before "local tag after the check ($refspec)"
    }
}

Test-Case 'a tag deleted on GitHub after an earlier check is gone from the next one: the namespace is pruned' {
    $f = New-SdkFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: services change'
    Add-SdkTag $f '2.0.0-preview.2'
    Assert-Result (Invoke-SdkPinCheck $f) 3 -Contains 'All of them ship in v2.0.0-preview.2'
    Invoke-FixtureGit $f.Author @('push', '--quiet', '--delete', 'origin', 'v2.0.0-preview.2')
    Assert-Result (Invoke-SdkPinCheck $f) 3 -Contains 'None of them is in a tagged SDK release yet' -Lacks 'v2.0.0-preview.2'
}

Test-Case 'a tag moved backwards on GitHub after an earlier check is taken as it is now: the namespace is forced' {
    # A forward move fast-forwards even without the force; only a backward move proves it is needed.
    $f = New-SdkFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: services change'
    Add-SdkTag $f '2.0.0-preview.2'
    Assert-Result (Invoke-SdkPinCheck $f) 3 -Contains 'All of them ship in v2.0.0-preview.2'
    Invoke-FixtureGit $f.Author @('tag', '--force', '--annotate', 'v2.0.0-preview.2', '-m', 'moved back', 'v2.0.0-preview.1^{commit}')
    Invoke-FixtureGit $f.Author @('push', '--quiet', '--force', 'origin', 'v2.0.0-preview.2')
    Assert-Result (Invoke-SdkPinCheck $f) 3 -Contains 'None of them is in a tagged SDK release yet' -Lacks 'All of them ship'
}

Complete-Tests
