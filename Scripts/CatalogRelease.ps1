# Shared by Test-CatalogSeed.ps1, Update-CatalogSeed.ps1 and New-Release.ps1 - dot-source it.
# Where the catalog is published, how one asset is fetched, and what a manifest has to say.

# Where the embedded seed lives, relative to the installer repo: the two files the Electron project
# embeds as catalog.zip and manifest.json.
$CatalogSeedFolder = 'DiffusionNexus.Installer.Electron/Assets/Catalog'

# The catalog repo's releases. releases/latest is GitHub's own "newest full release" redirect, which
# never names a pre-release, so <base>/latest/download/manifest.json is the stable channel's manifest.
$DefaultCatalogReleases = 'https://github.com/Into-The-Latent/DiffusionNexus.Catalog/releases'

# The releases page to read: the one given, else DIFFUSIONNEXUS_CATALOG_RELEASES (the script tests
# serve a fixture there), else the real one. Callers print the URL they read, so a redirected check
# is never mistaken for the real one.
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

# A file's sha256 as manifests write it (lower-case hex), or '' when there is no file. One recipe
# for the gate and the update script, so the two can never disagree about the same bytes.
function Get-Sha256([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }
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
