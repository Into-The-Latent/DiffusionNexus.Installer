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
