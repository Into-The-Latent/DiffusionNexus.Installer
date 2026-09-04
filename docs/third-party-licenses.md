# Third-Party Licenses

How this repo handles third-party attribution for the shipped Electron installer.

## What ships, and what it owes

| Material | Where it comes from | Licences | Obligation |
|---|---|---|---|
| .NET packages | restore graph of `DiffusionNexus.Installer.Electron` | MIT, Apache-2.0 | reproduce notice + licence text |
| .NET runtime + ASP.NET Core | self-contained publish | MIT | reproduce notice |
| Electron | `ElectronVersion` in the .csproj | MIT | reproduce notice; Chromium/Node notices ship as `LICENSE.electron.txt` + `LICENSES.chromium.html` beside the exe (electron-builder does this) |
| Node.js packages | bundled into `app.asar` from the ElectronNET host's `package.json` | MIT, ISC, Python-2.0 (argparse), BlueOak-1.0.0 (sax) | reproduce licence file of each package |

All notice-only. Every one of them is discharged by `THIRD-PARTY-NOTICES.txt`, which is
embedded in the app (Confirm stage → "Third-party licences", route `/licenses`) and copied next
to the binaries.

The frameworks the installer downloads on request (ComfyUI GPL-3.0, A1111/Forge AGPL-3.0,
Fooocus GPL-3.0, ...) are fetched at install time under their own licences and run as separate
processes. They are not part of this product and are not listed. Do not re-litigate this.

The SDK packages (`DiffusionNexus.Installer.SDK.*`) are first-party and excluded by prefix.
Locally they are project references, in CI they are packages; the exclusion keeps the generated
file identical either way (verified 2026-09-04: both restore graphs give the same 15 packages).

## Generating THIRD-PARTY-NOTICES.txt

```powershell
dotnet restore DiffusionNexus.Installer.slnx          # the restore graph is the source of truth
dotnet publish DiffusionNexus.Installer.Electron -c Release   # only needed when npm deps change
pwsh Scripts/Generate-ThirdPartyNotices.ps1           # writes THIRD-PARTY-NOTICES.txt + npm-inventory.json
```

Commit both outputs. The npm inventory (`Scripts/license-data/npm-inventory.json`) is
refreshed from the packaged `publish/app/node_modules` whenever that folder exists, so CI,
which never publishes, can still verify the notices from the committed inventory.

To check without writing, which is what CI runs on every push and `New-Release.ps1` runs after
publishing:

```powershell
pwsh Scripts/Generate-ThirdPartyNotices.ps1 -Check
```

### What the generator does

1. Reads `DiffusionNexus.Installer.Electron/obj/project.assets.json` and resolves every
   third-party package against the local NuGet cache.
2. Emits the .NET inventory and the full text of each distinct licence with the copyright
   holders from each nuspec.
3. Appends the hand-authored entries in `Scripts/license-data/supplements.json` (Electron, the
   .NET runtime).
4. Scans the packaged npm tree (or reads the committed inventory) and reproduces each package's
   licence file; a package without one gets the standard text of its declared licence.
5. Fails loudly if a package declares no licence, a licence has no text on file, or a package
   ships its licence as a file that has no `bundledNotices` entry.

### Adding a dependency

Add the package, restore (and publish, for npm), run the generator, commit the regenerated
files. If it throws, it tells you which text or supplement entry is missing.

## Repo licence

This repo currently has no LICENSE file of its own (the Avalonia `DiffusionNexus.Installers`
repo is MIT). Decision pending.
