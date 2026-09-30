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
