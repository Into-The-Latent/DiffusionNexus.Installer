<#
.SYNOPSIS
    Generates THIRD-PARTY-NOTICES.txt for the shipped DiffusionNexus Installer (Electron).

.DESCRIPTION
    The installer ships three kinds of third-party material:

      1. .NET packages    - read from the restore graph (obj/project.assets.json) and resolved
                            against the local NuGet cache. Run 'dotnet restore' first.
      2. Electron         - the Electron binary itself, plus Chromium/Node.js and their
                            dependencies, whose notices electron-builder installs beside the
                            executable (LICENSE.electron.txt, LICENSES.chromium.html).
      3. Node.js packages - the npm packages bundled into app.asar by electron-builder. Their
                            inventory is kept in Scripts/license-data/npm-inventory.json, which
                            this script REFRESHES from the packaged node_modules folder whenever
                            that folder exists (i.e. after 'dotnet publish'), so CI can verify
                            the notices without publishing.

    Hand-authored entries the graphs cannot express live in Scripts/license-data/supplements.json.

.PARAMETER ProjectDir
    Project whose restore graph is read. Defaults to DiffusionNexus.Installer.Electron.

.PARAMETER NpmModulesDir
    The node_modules folder of the PACKAGED app. Defaults to the Release publish output. When it
    exists the npm inventory file is regenerated from it (or, with -Check, compared against it).

.PARAMETER OutputPath
    Where to write the notices file. Defaults to THIRD-PARTY-NOTICES.txt at repo root.

.PARAMETER Check
    Do not write. Compare the committed notices (and, when NpmModulesDir exists, the committed
    npm inventory) against what would be generated and exit non-zero on any difference.
    Comparison ignores line-ending style.

.EXAMPLE
    pwsh Scripts/Generate-ThirdPartyNotices.ps1

.EXAMPLE
    pwsh Scripts/Generate-ThirdPartyNotices.ps1 -Check
#>
[CmdletBinding()]
param(
    [string]$ProjectDir = 'DiffusionNexus.Installer.Electron',
    [string]$NpmModulesDir,
    [string]$OutputPath,
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dataDir  = Join-Path $PSScriptRoot 'license-data'
$textsDir = Join-Path $dataDir 'texts'
$npmInventoryPath = Join-Path $dataDir 'npm-inventory.json'
if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot 'THIRD-PARTY-NOTICES.txt' }
if (-not $NpmModulesDir) {
    $NpmModulesDir = Join-Path $repoRoot $ProjectDir 'bin' 'Release' 'net10.0' 'win-x64' 'publish' 'app' 'node_modules'
}

function Normalize-Text { param([string]$Text) ; return ($Text -replace "`r`n", "`n") }

# ---------------------------------------------------------------- NuGet cache
$nugetRoot = $env:NUGET_PACKAGES
if (-not $nugetRoot) { $nugetRoot = Join-Path $env:USERPROFILE '.nuget/packages' }
if (-not (Test-Path $nugetRoot)) { throw "NuGet package cache not found at '$nugetRoot'." }

$assets = Join-Path $repoRoot $ProjectDir 'obj' 'project.assets.json'
if (-not (Test-Path $assets)) { throw "Restore graph not found at '$assets'. Run 'dotnet restore' first." }

Write-Host "Reading restore graph : $assets"
Write-Host "NuGet cache           : $nugetRoot"

$graph = Get-Content $assets -Raw | ConvertFrom-Json
$supplements = Get-Content (Join-Path $dataDir 'supplements.json') -Raw | ConvertFrom-Json

# ------------------------------------------------------- collect .NET packages
function Get-NuspecField {
    param([string]$Xml, [string]$Field)
    $m = [regex]::Match($Xml, "<$Field>(?<v>[^<]*)</$Field>")
    if ($m.Success) { return [System.Net.WebUtility]::HtmlDecode($m.Groups['v'].Value).Trim() }
    return ''
}

$components = @()
foreach ($entry in $graph.libraries.PSObject.Properties) {
    if ($entry.Value.type -ne 'package') { continue }
    $id, $version = $entry.Name -split '/', 2

    # First-party packages carry no third-party obligation. Skipping them also keeps local and
    # CI output identical: Directory.Build.targets rewrites the SDK PackageReferences to
    # ProjectReferences locally, so they are packages only in CI.
    $skip = $false
    foreach ($prefix in $supplements.excludePrefixes) {
        if ($id.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $skip = $true; break }
    }
    if ($skip) { continue }

    $pkgDir = Join-Path $nugetRoot $id.ToLowerInvariant() $version
    $nuspec = Get-ChildItem -Path $pkgDir -Filter '*.nuspec' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $nuspec) { throw "Package '$id/$version' is not in the NuGet cache. Run 'dotnet restore' first." }

    $xml = Get-Content $nuspec.FullName -Raw

    $license = ''
    $licenseFile = ''
    $mExpr = [regex]::Match($xml, '<license type="expression">(?<v>[^<]+)</license>')
    $mFile = [regex]::Match($xml, '<license type="file">(?<v>[^<]+)</license>')
    if ($mExpr.Success) {
        $license = $mExpr.Groups['v'].Value
    }
    elseif ($mFile.Success) {
        $license = 'FILE'
        $licenseFile = $mFile.Groups['v'].Value
    }

    $components += [pscustomobject]@{
        Id          = $id
        Version     = $version
        License     = if ($license) { $license } else { 'UNDECLARED' }
        LicenseFile = $licenseFile
        Copyright   = Get-NuspecField $xml 'copyright'
        Authors     = Get-NuspecField $xml 'authors'
        ProjectUrl  = Get-NuspecField $xml 'projectUrl'
        PackageDir  = $pkgDir
    }
}
$components = $components | Sort-Object Id
Write-Host ("Packages resolved     : {0}" -f $components.Count)

$undeclared = $components | Where-Object { $_.License -eq 'UNDECLARED' }
if ($undeclared) {
    $names = ($undeclared | ForEach-Object { $_.Id }) -join ', '
    throw "These packages declare no license and need a manual supplement entry: $names"
}

# A package whose nuspec points at a license FILE has no SPDX text to print in section 2, so
# it must be reproduced verbatim through a bundledNotices entry, or it would silently vanish.
foreach ($c in ($components | Where-Object { $_.License -eq 'FILE' })) {
    if (-not ($supplements.bundledNotices | Where-Object { $_.packageId -eq $c.Id })) {
        throw "Package '$($c.Id)' ships its license as a file ('$($c.LicenseFile)'). Add a bundledNotices entry for it in supplements.json."
    }
}

# ------------------------------------------------ collect Node.js packages
# Every package.json directly under node_modules (or under a scoped @org folder) is one shipped
# package. First-party file: dependencies (the ElectronHostHook) are skipped by npmExcludePrefixes.
function Get-NpmInventory {
    param([string]$ModulesDir)
    $found = @()
    $dirs = @(Get-ChildItem -Path $ModulesDir -Directory | Where-Object { -not $_.Name.StartsWith('@') })
    foreach ($scope in (Get-ChildItem -Path $ModulesDir -Directory | Where-Object { $_.Name.StartsWith('@') })) {
        $dirs += Get-ChildItem -Path $scope.FullName -Directory
    }
    foreach ($dir in $dirs) {
        $pj = Join-Path $dir.FullName 'package.json'
        if (-not (Test-Path $pj)) { continue }
        $j = Get-Content $pj -Raw | ConvertFrom-Json
        if (-not $j.name) { continue }

        $skip = $false
        foreach ($prefix in $supplements.npmExcludePrefixes) {
            if ($j.name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $skip = $true; break }
        }
        if ($skip) { continue }

        $license = ''
        if ($j.license -is [string]) { $license = $j.license }
        elseif ($j.license -and $j.license.type) { $license = $j.license.type }
        elseif ($j.licenses) { $license = (($j.licenses | ForEach-Object { if ($_.type) { $_.type } else { $_ } }) -join ' OR ') }
        if (-not $license) { throw "npm package '$($j.name)' declares no license. Add it to supplements.json." }

        $licenseText = ''
        $licenseFile = Get-ChildItem -Path $dir.FullName -File |
            Where-Object { $_.Name -match '^(LICEN[CS]E|COPYING)(\..*)?$' } |
            Select-Object -First 1
        if ($licenseFile) { $licenseText = (Get-Content $licenseFile.FullName -Raw).TrimEnd() }

        $repo = ''
        if ($j.repository -is [string]) { $repo = $j.repository }
        elseif ($j.repository -and $j.repository.url) { $repo = $j.repository.url }

        $author = ''
        if ($j.author -is [string]) { $author = $j.author }
        elseif ($j.author -and $j.author.name) { $author = $j.author.name }

        $found += [pscustomobject][ordered]@{
            name        = $j.name
            version     = [string]$j.version
            license     = $license
            author      = $author
            repository  = $repo
            licenseText = Normalize-Text $licenseText
        }
    }
    return @($found | Sort-Object name)
}

$npmFresh = $null
if (Test-Path $NpmModulesDir) {
    Write-Host "Node.js packages from : $NpmModulesDir"
    $npmFresh = Get-NpmInventory -ModulesDir $NpmModulesDir
    Write-Host ("Node.js packages      : {0}" -f $npmFresh.Count)
}
else {
    Write-Host "Node.js packages      : packaged app not found at '$NpmModulesDir'; using the committed inventory"
}

if (-not (Test-Path $npmInventoryPath) -and -not $npmFresh) {
    throw "No npm inventory at '$npmInventoryPath' and no packaged app to build one from. Run 'dotnet publish -c Release' first."
}

$npmFreshJson = $null
if ($npmFresh) { $npmFreshJson = Normalize-Text ($npmFresh | ConvertTo-Json -Depth 5) }
$npmCommittedJson = if (Test-Path $npmInventoryPath) { Normalize-Text (Get-Content $npmInventoryPath -Raw) } else { '' }

if ($Check) {
    if ($npmFreshJson -and ($npmFreshJson.TrimEnd() -ne $npmCommittedJson.TrimEnd())) {
        Write-Host "::error::$npmInventoryPath is out of date against the packaged app. Run: pwsh Scripts/Generate-ThirdPartyNotices.ps1 and commit the result."
        exit 1
    }
    $npmInventory = @($npmCommittedJson | ConvertFrom-Json)
}
elseif ($npmFreshJson) {
    [System.IO.File]::WriteAllText($npmInventoryPath, ($npmFreshJson -replace "`n", "`r`n") + "`r`n", [System.Text.UTF8Encoding]::new($false))
    Write-Host "Wrote                 : $npmInventoryPath"
    $npmInventory = $npmFresh
}
else {
    $npmInventory = @($npmCommittedJson | ConvertFrom-Json)
}

# --------------------------------------------------------------------- emit
$sb   = [System.Text.StringBuilder]::new()
$rule = '=' * 80
$thin = '-' * 80
function Add-Line { param([string]$Text = '') ; [void]$sb.AppendLine($Text) }

Add-Line $rule
Add-Line 'THIRD-PARTY SOFTWARE NOTICES AND INFORMATION'
Add-Line 'DiffusionNexus Installer'
Add-Line $rule
Add-Line ''
Add-Line 'This product incorporates material from the projects listed below. The original'
Add-Line 'copyright notices and the licenses under which we received such material are set'
Add-Line 'forth in this document.'
Add-Line ''
Add-Line 'Sections 1 and 2 are the complete restore closure of the shipped .NET project. It is'
Add-Line 'a superset of what the released binaries actually link: a few entries may be'
Add-Line 'build-time only. They are listed anyway so the notice is never narrower than the'
Add-Line 'product. Section 4 covers the Electron runtime and the .NET runtime, section 5 the'
Add-Line 'Node.js packages bundled into the application archive.'
Add-Line ''
Add-Line 'The frameworks this installer downloads and installs on request (ComfyUI, Forge and'
Add-Line 'others) are not part of this product. They are fetched from their own repositories'
Add-Line 'at install time under their own licenses, which they carry themselves.'
Add-Line ''
Add-Line 'GENERATED FILE - DO NOT EDIT BY HAND.'
Add-Line 'Regenerate with:  pwsh Scripts/Generate-ThirdPartyNotices.ps1'
Add-Line 'Inputs:           <project>/obj/project.assets.json          (restore graph)'
Add-Line '                  Scripts/license-data/npm-inventory.json    (bundled Node.js packages)'
Add-Line '                  Scripts/license-data/supplements.json      (hand-authored entries)'
Add-Line ''

Add-Line $thin
Add-Line '1. .NET COMPONENT INVENTORY'
Add-Line $thin
Add-Line ''
foreach ($c in $components) {
    if ($c.License -eq 'FILE') { $label = 'see sect. 3' } else { $label = $c.License }
    Add-Line ("  {0,-14} {1} {2}" -f $label, $c.Id, $c.Version)
}
Add-Line ''

Add-Line $thin
Add-Line '2. LICENSE TEXTS (.NET COMPONENTS)'
Add-Line $thin
Add-Line ''

$byLicense = $components | Where-Object { $_.License -ne 'FILE' } | Group-Object License | Sort-Object Name
foreach ($group in $byLicense) {
    $textFile = Join-Path $textsDir ($group.Name + '.txt')
    if (-not (Test-Path $textFile)) {
        throw "No license text for '$($group.Name)'. Add Scripts/license-data/texts/$($group.Name).txt."
    }
    Add-Line ("### {0}" -f $group.Name)
    Add-Line ''
    Add-Line 'Applies to the following components:'
    foreach ($c in ($group.Group | Sort-Object Id)) {
        if ($c.Copyright) { $attribution = $c.Copyright }
        elseif ($c.Authors) { $attribution = "Copyright (c) $($c.Authors)" }
        else { $attribution = '(no copyright notice declared)' }
        Add-Line ("  - {0} {1}" -f $c.Id, $c.Version)
        Add-Line ("      {0}" -f $attribution)
    }
    Add-Line ''
    Add-Line (Get-Content $textFile -Raw).TrimEnd()
    Add-Line ''
    Add-Line $thin
    Add-Line ''
}

Add-Line '3. BUNDLED NOTICE FILES'
Add-Line $thin
Add-Line ''
if (-not $supplements.bundledNotices -or $supplements.bundledNotices.Count -eq 0) {
    Add-Line 'No shipped .NET package carries its own notice file.'
    Add-Line ''
}
foreach ($b in $supplements.bundledNotices) {
    $pkg = $components | Where-Object { $_.Id -eq $b.packageId } | Select-Object -First 1
    if (-not $pkg) { throw "Bundled-notice package '$($b.packageId)' is no longer in the restore graph. Update supplements.json." }
    $file = Join-Path $pkg.PackageDir $b.file
    if (-not (Test-Path $file)) { throw "Notice file '$($b.file)' not found in package '$($b.packageId)'." }
    Add-Line ("### {0}" -f $b.title)
    Add-Line ("Source: {0} {1} :: {2}" -f $pkg.Id, $pkg.Version, $b.file)
    Add-Line ''
    Add-Line ("NOTE: {0}" -f $b.note)
    Add-Line ''
    Add-Line (Get-Content $file -Raw).TrimEnd()
    Add-Line ''
    Add-Line $thin
    Add-Line ''
}

Add-Line '4. RUNTIMES AND OTHER COMPONENTS NOT EXPRESSED BY A PACKAGE GRAPH'
Add-Line $thin
Add-Line ''
foreach ($s in $supplements.supplements) {
    $file = Join-Path $textsDir $s.textFile
    if (-not (Test-Path $file)) { throw "Supplement text '$($s.textFile)' not found." }
    Add-Line ("### {0} - {1}" -f $s.title, $s.license)
    Add-Line ''
    Add-Line ("NOTE: {0}" -f $s.note)
    Add-Line ''
    Add-Line (Get-Content $file -Raw).TrimEnd()
    Add-Line ''
    Add-Line $thin
    Add-Line ''
}

Add-Line '5. NODE.JS PACKAGES BUNDLED INTO THE APPLICATION'
Add-Line $thin
Add-Line ''
Add-Line 'The following npm packages are packed into app.asar by electron-builder. Each entry'
Add-Line 'reproduces the license file found in the package as shipped; a package that ships no'
Add-Line 'license file is covered by the standard text of its declared license.'
Add-Line ''
foreach ($n in $npmInventory) {
    Add-Line ("  {0,-14} {1} {2}" -f $n.license, $n.name, $n.version)
}
Add-Line ''
foreach ($n in $npmInventory) {
    Add-Line ("### {0} {1} - {2}" -f $n.name, $n.version, $n.license)
    if ($n.author) { Add-Line ("Author: {0}" -f $n.author) }
    if ($n.repository) { Add-Line ("Source: {0}" -f $n.repository) }
    Add-Line ''
    if ($n.licenseText) {
        Add-Line $n.licenseText.TrimEnd()
    }
    else {
        $stdFile = Join-Path $textsDir ($n.license + '.txt')
        if (-not (Test-Path $stdFile)) {
            throw "npm package '$($n.name)' ships no license file and there is no standard text for '$($n.license)'. Add Scripts/license-data/texts/$($n.license).txt."
        }
        Add-Line ("(no license file in the package; the standard {0} text applies)" -f $n.license)
        Add-Line ''
        Add-Line (Get-Content $stdFile -Raw).TrimEnd()
    }
    Add-Line ''
    Add-Line $thin
    Add-Line ''
}

Add-Line 'END OF THIRD-PARTY NOTICES'

# Normalise to pure CRLF. Upstream notice texts carry mixed endings, and a mixed-ending output
# would not round-trip through git's autocrlf normalisation, which would make the CI freshness
# check flap.
$text = $sb.ToString() -replace "`r`n", "`n" -replace "`n", "`r`n"

if ($Check) {
    if (-not (Test-Path $OutputPath)) {
        Write-Host "::error::$OutputPath is missing. Run: pwsh Scripts/Generate-ThirdPartyNotices.ps1"
        exit 1
    }
    $committed = Normalize-Text (Get-Content $OutputPath -Raw)
    $expected  = Normalize-Text $text
    if ($committed -ne $expected) {
        Write-Host "::error::$OutputPath is out of date. Run: pwsh Scripts/Generate-ThirdPartyNotices.ps1 and commit the result."
        Write-Host 'First differing lines:'
        Compare-Object ($committed -split "`n") ($expected -split "`n") |
            Select-Object -First 30 |
            Format-Table -AutoSize | Out-String | Write-Host
        exit 1
    }
    Write-Host 'Third-party notices are current.'
    return
}

$dir = Split-Path -Parent $OutputPath
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
[System.IO.File]::WriteAllText($OutputPath, $text, [System.Text.UTF8Encoding]::new($false))

$lineCount = ($text -split "`n").Count
Write-Host ("Wrote                 : {0} ({1:N0} bytes, {2:N0} lines)" -f $OutputPath, (Get-Item $OutputPath).Length, $lineCount)
