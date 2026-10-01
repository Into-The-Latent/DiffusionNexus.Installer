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

# One definition of a seed, for a manifest's text and for the values -Expect is given alike: the
# catalogVersion, the catalog commit, the archive's sha256 (lower-cased; hex is case-insensitive),
# the channel and the pack time (generatedAt, compared as an instant). The last two are what the SDK
# records from a seed besides the first three. Throws naming $What when a value is not that.
function New-CatalogSeed([string]$Version, [string]$Commit, [string]$Sha256, [string]$Channel, [string]$GeneratedAt, [string]$What) {
    if ($Version -notmatch '^\d+$') { throw "$What has no catalogVersion (got '$Version')." }
    if ($Commit -notmatch '^[0-9a-fA-F]{40}$') { throw "$What has no 40-digit commit (got '$Commit')." }
    if ($Sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw "$What has no archive sha256 (got '$Sha256')." }
    if ($Channel -notmatch '^[A-Za-z]+$') { throw "$What names no channel (got '$Channel')." }
    $packed = [DateTimeOffset]::MinValue
    if ($GeneratedAt -notmatch '^\d{4}-\d\d-\d\dT' -or
        -not [DateTimeOffset]::TryParse($GeneratedAt, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$packed)) {
        throw "$What has no generatedAt (got '$GeneratedAt')."
    }
    [pscustomobject]@{
        Version = [int]$Version; Commit = $Commit.ToLowerInvariant(); Sha256 = $Sha256.ToLowerInvariant()
        Channel = $Channel; GeneratedAt = $packed; Short = $Commit.Substring(0, 7).ToLowerInvariant()
    }
}

# The seed a manifest's text describes. generatedAt is read as the text it is: ConvertFrom-Json would
# turn it into a local DateTime first.
function Read-CatalogManifest([string]$Text, [string]$What) {
    try { $json = $Text | ConvertFrom-Json }
    catch { throw "$What is not JSON ($($_.Exception.Message))" }
    $doc = [System.Text.Json.JsonDocument]::Parse($Text.TrimStart([char]0xFEFF))
    try { $packed = "$(@($doc.RootElement.EnumerateObject() | Where-Object Name -eq 'generatedAt' | ForEach-Object { $_.Value.ToString() })[0])" }
    finally { $doc.Dispose() }
    New-CatalogSeed "$($json.catalogVersion)" "$($json.commit)" "$($json.archive.sha256)" "$($json.channel)" $packed $What
}

# The catalogSeed a build-info.json reports, as a seed (New-Release Step 1c, Promote-Release).
# Read from the text, for the same reason as generatedAt above.
function Read-BuildInfoSeed([string]$Text, [string]$What) {
    $doc = [System.Text.Json.JsonDocument]::Parse($Text.TrimStart([char]0xFEFF))
    try {
        $values = @{}
        foreach ($property in $doc.RootElement.EnumerateObject()) {
            if ($property.Name -eq 'catalogSeed' -and $property.Value.ValueKind -eq 'Object') {
                foreach ($field in $property.Value.EnumerateObject()) { $values[$field.Name] = $field.Value.ToString() }
            }
        }
        if ($values.Count -eq 0) { throw "$What reports no catalogSeed." }
        New-CatalogSeed $values['version'] $values['commit'] $values['sha256'] $values['channel'] $values['generatedAt'] "the catalogSeed in $What"
    } finally { $doc.Dispose() }
}

# Every field in which two seeds differ, in full, so a refusal shows what differs. Empty = the same.
function Compare-CatalogSeed($Actual, $Expected) {
    @(
        if ($Actual.Version -ne $Expected.Version) { "catalogVersion $($Actual.Version) vs $($Expected.Version)" }
        if ($Actual.Commit -ne $Expected.Commit) { "commit $($Actual.Commit) vs $($Expected.Commit)" }
        if ($Actual.Sha256 -ne $Expected.Sha256) { "archive sha256 $($Actual.Sha256) vs $($Expected.Sha256)" }
        if ($Actual.Channel -ne $Expected.Channel) { "channel $($Actual.Channel) vs $($Expected.Channel)" }
        if ($Actual.GeneratedAt -ne $Expected.GeneratedAt) { "generatedAt $($Actual.GeneratedAt.ToString('o')) vs $($Expected.GeneratedAt.ToString('o'))" }
    )
}

# A release's manifest: downloaded to $Path, read, and returned as its seed plus its Text (the gate
# compares whole manifests, the writer keeps the file). Throws naming the URL on any failure.
function Read-CatalogReleaseManifest([string]$Url, [string]$Path, [string]$What) {
    Save-CatalogAsset $Url $Path
    $text = Get-Content -LiteralPath $Path -Raw
    Read-CatalogManifest $text "$What ($Url)" | Add-Member -NotePropertyName Text -NotePropertyValue $text -PassThru
}
