# DiffusionNexus Installer

The DiffusionNexus installer, 3.x line — **Electron shell + Blazor UI, written in C#**.

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

`electron-builder` auto-publishes whenever its config names a publish provider, so the default
build deliberately uses a config with that block removed. To actually upload:

```
dotnet publish DiffusionNexus.Installer.Electron -c Release -p:ElectronBuilderJson=electron-builder.json
```

with `GH_TOKEN` set to a token that can write releases to this repository.

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
