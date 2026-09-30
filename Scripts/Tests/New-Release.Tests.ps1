#Requires -Version 7.2
# Tests for the Step 0 gates of Scripts/New-Release.ps1. Run: pwsh -NoProfile -File Scripts/Tests/New-Release.Tests.ps1
# Exit code = number of failed cases. CI runs every Scripts/Tests/*.Tests.ps1.
#
# Only the gates are under test. -RepoRoot points the script at a fixture repo, the SDK pin check
# finds a fixture SDK through LocalSDKPath, and -SkipUpload keeps gh out of it. A run that gets past
# the gates fails at dotnet publish, because the fixture has no project. That failure is the proof
# it got there: the version was written and "dotnet publish failed" is on the output.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestKit.ps1')

function Invoke-NewRelease($Fixture, [string[]]$ExtraArgs = @(), [switch]$NativeErrors) {
    $script = Join-Path $PSScriptRoot '..' 'New-Release.ps1'
    $props = Join-Path $Fixture.Installer 'Directory.Build.props'
    Set-Content -Path $props -Value '<Project><PropertyGroup><Version>0.0.1</Version></PropertyGroup></Project>'
    $savedSdkPath = $env:LocalSDKPath
    $savedToken = $env:GITHUB_PACKAGES_TOKEN
    $env:LocalSDKPath = $Fixture.Sdk
    $env:GITHUB_PACKAGES_TOKEN = 'fixture-token'
    try {
        # A profile may set $PSNativeCommandUseErrorActionPreference; the gate must work either way.
        $preference = if ($NativeErrors) { '$true' } else { '$false' }
        $command = "`$PSNativeCommandUseErrorActionPreference = $preference; & '$script' -RepoRoot '$($Fixture.Installer)' -Version 9.9.9 -SkipUpload $($ExtraArgs -join ' ')"
        $output = & pwsh -NoProfile -Command $command 2>&1
        $exit = $LASTEXITCODE
        [pscustomobject]@{
            ExitCode       = $exit
            Text           = ($output | ForEach-Object { "$_" }) -join "`n"
            VersionWritten = (Get-Content $props -Raw).Contains('<Version>9.9.9</Version>')
        }
    } finally {
        $env:LocalSDKPath = $savedSdkPath
        $env:GITHUB_PACKAGES_TOKEN = $savedToken
    }
}

Test-Case 'a pin behind SDK develop refuses before the version write and names -AllowOlderSdk' {
    $f = New-SdkFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: unreleased change'
    $r = Invoke-NewRelease $f
    if ($r.ExitCode -eq 0) { throw "expected a refusal, got exit 0. Output:`n$($r.Text)" }
    Assert-Like $r.Text '*fix: unreleased change*re-run with -AllowOlderSdk*Nothing was built or changed*' 'output'
    Assert-Equal $r.VersionWritten $false 'version written'
}

Test-Case '-AllowOlderSdk gets past a behind pin even when the session turns native exit codes into errors' {
    $f = New-SdkFixture
    Add-SdkCommit $f 'DiffusionNexus.Installer.SDK.Services/Service.cs' 'fix: unreleased change'
    $r = Invoke-NewRelease $f -ExtraArgs @('-AllowOlderSdk') -NativeErrors
    Assert-Like $r.Text '*Releasing WITHOUT the SDK commits*dotnet publish failed*' 'output'
    Assert-Equal $r.VersionWritten $true 'version written'
}

Test-Case 'a pin that could not be checked refuses even with -AllowOlderSdk, whatever the native error setting' {
    $f = New-SdkFixture
    Set-InstallerPins $f '2.0.0-preview.1' -AppVersion '2.0.0-preview.2'
    $r = Invoke-NewRelease $f -ExtraArgs @('-AllowOlderSdk') -NativeErrors
    if ($r.ExitCode -eq 0) { throw "expected a refusal, got exit 0. Output:`n$($r.Text)" }
    Assert-Like $r.Text '*do not all pin the same version*could not be checked*Nothing was built or changed*' 'output'
    Assert-Equal $r.VersionWritten $false 'version written'
}

Complete-Tests
