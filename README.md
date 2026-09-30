# Easy Workload Installer by Into the Latent

The Easy Workload Installer by Into the Latent (formerly the Into the Latent Easy Installer and, before that, the DiffusionNexus Installer), 3.x line — **Electron shell + Blazor UI, written in C#**.

Replaces the 2.x Avalonia installer. Install logic is not duplicated here: it lives in the
**DiffusionNexus Installer SDK** and is consumed as NuGet packages.

> **Licence:** this repository is public so that releases are downloadable without a token
> and so CI runs on free runners. It carries **no licence — all rights reserved**. You may
> read the source; you may not copy, modify or redistribute it.

## Repositories

| Repo | Purpose |
|------|---------|
| **this one** | The installer application, and its Releases are the public download + auto-update channel |
| `Little-God1983/DiffusionNexus.Installer.SDK` | Install/git/python/download/catalog logic → NuGet |

Releases are published to this repository. Because it is public, the installed app reads
update metadata with **no credentials of any kind** — nothing secret ships to users.

## Requirements

- .NET 10 SDK
- Node.js 22.x or later (ElectronNET.Core drives `electron-builder` through npm)

## Build and run

```
dotnet run --project DiffusionNexus.Installer.Electron
```

Running the project directly serves the Blazor UI in a browser without starting Electron,
which is faster for UI work. Packaging is what produces the desktop app:

```
dotnet publish DiffusionNexus.Installer.Electron -c Release
```

That emits an NSIS installer to `DiffusionNexus.Installer.Electron/bin/Release/net10.0/win-x64/publish/`.
It does **not** upload anything — publishing is opt-in, see below.

## Publishing a release

```
.\Scripts\New-Release.ps1 -Version 3.0.5 -Notes "What changed."

# For testers first: a GitHub pre-release, offered only to installs following Preview
.\Scripts\New-Release.ps1 -Version 3.0.6 -Notes "What changed." -Prerelease

# ...and later to everyone, without a rebuild
gh release edit v3.0.6 --repo Into-The-Latent/DiffusionNexus.Installer --prerelease=false --latest  # Promote-Release.ps1 (issue #30) will replace this
```

Before it changes or builds anything, `New-Release.ps1` runs four gates. **0a** the packages
token is set. **0b** a signed-in `gh` account can write to this repo: the active account if it
can, otherwise the signed-in Into-The-Latent account; if neither can, it stops here, and no `gh
auth switch` is ever needed (only the script's own upload uses that token). **0c**
`Scripts/Test-SdkPin.ps1` fetches your SDK checkout and stops the release when SDK `develop` has
commits the pinned version does not contain that change what the packages ship: their own folders,
or the SDK's root `Directory.*.props` / `.targets` files beyond a version bump. It lists them and
says whether to bump the pin or to tag and publish the SDK first. Add `-AllowOlderSdk` to leave them
out on purpose. **0d** `Scripts/Test-CatalogSeed.ps1` downloads the latest stable catalog
release's `manifest.json` (public, no token) and stops the release when the embedded seed under
`DiffusionNexus.Installer.Electron/Assets/Catalog` is not that release: same version, commit and
archive hash, and a zip that matches its manifest. The fix is `pwsh Scripts/Update-CatalogSeed.ps1`
(it downloads and verifies both files; `-Version N` embeds an older stable tag on purpose) and a
commit; the gate refuses an uncommitted seed. Add `-AllowOlderCatalog` to ship a different seed on
purpose. A Preview build embeds the stable seed too, because promotion never rebuilds. A refusal
leaves the working tree untouched. Run either check on its own at any time with
`pwsh Scripts/Test-SdkPin.ps1` or `pwsh Scripts/Test-CatalogSeed.ps1`.

After packaging, the script runs the packaged app with `--build-info` and uploads its answer as
`build-info.json` next to the installer: the app version, the SDK version it was built with, the
catalog schema it reads and the catalog seed it embeds (version, commit, sha256); the script refuses
a build whose answer differs from what the gates checked. That asset, not the release notes, is what
promotion and the catalog repo's gate read. The notes end with two generated lines,
`Built with Installer SDK X` and `Bundled catalog vN (stable)`, for people; nothing reads them back.

Do not hand-roll this. Packaging takes two steps, and skipping the second produces an installer
that runs fine and then fails permanently at its first update check:

- `dotnet publish` packages using `Properties/electron-builder.local.json`, which has the
  `publish` block removed. It has to be removed, because electron-builder auto-publishes
  whenever a provider is configured, and a plain local build would fail with
  "GitHub Personal Access Token is not set".
- But electron-builder only emits `resources/app-update.yml` when a provider **is** configured,
  and that file is how the installed app learns where its updates live. So the script re-runs
  electron-builder with the real config plus `--publish never`: provider present, upload
  suppressed, no token required. It aborts if `app-update.yml` is missing.

Assets are uploaded with `gh` under the account gate 0b found, never by switching your active login.

## Working on the SDK at the same time

`Directory.Build.targets` detects the SDK source at `..\DiffusionNexus.Installer.SDK` and swaps
the SDK `PackageReference`s for `ProjectReference`s automatically. Open
`DiffusionNexus.Installer.LocalSDK.slnx` in Visual Studio when doing this — the committed
`.slnx` lists only this app, and VS's solution-scoped restore needs the SDK projects present.

Verify against real packages before pushing, since the redirect hides missing package refs:

```
dotnet build DiffusionNexus.Installer.slnx -c Release -p:UseLocalSDK=false
```

That path needs `GITHUB_PACKAGES_TOKEN` with `read:packages` — the SDK packages live under a
different GitHub account than this repo.

> `Directory.Build.targets` is **git-ignored on purpose** — it contains machine-specific
> absolute paths and must never influence CI, which always builds against real packages.
> Copy it from `DiffusionNexus.Installers` (or write your own) when setting up a new machine.
