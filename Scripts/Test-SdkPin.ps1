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
