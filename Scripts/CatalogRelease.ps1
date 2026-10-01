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

# The seed a manifest's text describes, parsed once and as leniently as the SDK's reader
# (CatalogSchema.Json: trailing commas, comments). Every value is read as the text it is: ConvertFrom-Json
# would turn generatedAt into a local DateTime first.
function Read-CatalogManifest([string]$Text, [string]$What) {
    $options = [System.Text.Json.JsonDocumentOptions]@{ AllowTrailingCommas = $true; CommentHandling = 'Skip' }
    try { $doc = [System.Text.Json.JsonDocument]::Parse($Text.TrimStart([char]0xFEFF), $options) }
    catch { throw "$What is not JSON ($($_.InnerException.Message ?? $_.Exception.Message))" }
    try {
        $fields = @{}
        if ($doc.RootElement.ValueKind -eq 'Object') {
            foreach ($property in $doc.RootElement.EnumerateObject()) {
                if ($property.Name -eq 'archive' -and $property.Value.ValueKind -eq 'Object') {
                    foreach ($field in $property.Value.EnumerateObject()) { if ($field.Name -eq 'sha256') { $fields['sha256'] = $field.Value.ToString() } }
                } else { $fields[$property.Name] = $property.Value.ToString() }
            }
        }
    } finally { $doc.Dispose() }
    New-CatalogSeed $fields['catalogVersion'] $fields['commit'] $fields['sha256'] $fields['channel'] $fields['generatedAt'] $What
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

# What an SDK package name looks like, for the build-info reader below and Test-SdkPin -Packages alike:
# a name one accepts and the other refuses would turn a promotion into an unoverridable "not checked".
$SdkPackageNamePattern = '^DiffusionNexus\.Installer\.SDK\.[A-Za-z0-9.]+$'

# The SDK packages a build-info.json lists (sdkPackages): the assemblies the build ships, which
# Promote-Release hands Test-SdkPin -Packages. Throws naming $What when there is no such list.
function Read-BuildInfoSdkPackages([string]$Text, [string]$What) {
    $doc = [System.Text.Json.JsonDocument]::Parse($Text.TrimStart([char]0xFEFF))
    try {
        $names = @()
        if ($doc.RootElement.ValueKind -eq 'Object') {
            foreach ($property in $doc.RootElement.EnumerateObject()) {
                if ($property.Name -eq 'sdkPackages' -and $property.Value.ValueKind -eq 'Array') {
                    $names = @($property.Value.EnumerateArray() | ForEach-Object { $_.ToString() })
                }
            }
        }
    } finally { $doc.Dispose() }
    if ($names.Count -eq 0) { throw "$What lists no sdkPackages." }
    foreach ($name in $names) {
        if ($name -notmatch $SdkPackageNamePattern) { throw "'$name' in the sdkPackages of $What is no DiffusionNexus.Installer.SDK.* package." }
    }
    $names
}

# How the SDK packages a build ships differ from the ones the projects pin, one line per direction;
# empty = the same set. Step 0c walks the pinned ones and promotion the shipped ones, so a difference
# either way would have the two gates judge one build differently.
function Get-SdkPackageMismatch([string[]]$Pinned, [string[]]$Shipped) {
    $notShipped = @($Pinned | Where-Object { $_ -notin $Shipped })
    $notPinned = @($Shipped | Where-Object { $_ -notin $Pinned })
    if ($notShipped.Count -gt 0) { "pinned but not shipped: $($notShipped -join ', ')" }
    if ($notPinned.Count -gt 0) { "shipped but not pinned (Step 0c never checked it): $($notPinned -join ', ')" }
}

# A native command's answer: its exit code, its stdout lines (the answer, the only data) and its stderr
# lines (for a message: git and gh write warnings there and still exit 0). Under 2>&1 stderr comes back
# as ErrorRecords, which is how the two are told apart. The block runs in a child scope of this
# function, so it sees its caller's variables except any named like the three locals here
# ($NativeCommand, $nativeLines, $nativeExit), which hide them.
function Invoke-Native([scriptblock]$NativeCommand) {
    $nativeLines = @(& $NativeCommand 2>&1)
    $nativeExit = $LASTEXITCODE
    [pscustomobject]@{
        ExitCode = $nativeExit
        Out      = @($nativeLines | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
        Err      = @($nativeLines | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
    }
}

# The catalog releases page New-Release.ps1 and Promote-Release.ps1 judge against: the one given, else
# the real one. Never DIFFUSIONNEXUS_CATALOG_RELEASES (the standalone seed scripts honour it for their
# tests), so a value left in the environment cannot steer a release or a promotion. Another page is
# announced, naming the step that reads it.
function Resolve-GateCatalogReleases([string]$Given, [string]$Step) {
    $base = if ($Given) { $Given.TrimEnd('/') } else { $DefaultCatalogReleases }
    if ($base -ne $DefaultCatalogReleases) { Write-Warning "$Step reads $base, not the real catalog releases (-CatalogReleases)." }
    $base
}

# The packaged app's --build-info answer: its stdout, the JSON document. A refusing build writes its
# reason to stderr and exits 1, so the throw carries stderr - the console is not the only log.
function Get-BuildInfoText([string]$EntryPoint) {
    $run = Invoke-Native { & $EntryPoint --build-info }
    $answer = $run.Out -join "`n"
    if ($run.ExitCode -ne 0) {
        throw "The packaged app did not answer --build-info (exit $($run.ExitCode)):`n$(@(($run.Err -join "`n"), $answer) -ne '' -join "`n")"
    }
    $answer
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
