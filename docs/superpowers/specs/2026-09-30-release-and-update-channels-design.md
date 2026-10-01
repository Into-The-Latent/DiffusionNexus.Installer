# Release and update channels — design

Date: 2026-09-30. Status: approved by the owner 2026-09-30. Issues filed: installer #37, #38, #30 (rewritten), #39, #36; SDK #71; catalog #3. #29 closed (its PR was #33). SDK `v2.0.0` tagged.

Supersedes the "Preview" handling in installer issues #29 and #30 (their validated plans are
reused where this document says so). Follow-up, deliberately not part of this: #36 (apply
catalog updates automatically, off by default).

## 1. Purpose

Three artifacts reach users: the SDK packages, the catalog, and the installer app. Each had its
own idea of "preview" and "stable", nothing proved that a release carried the newest SDK or the
newest catalog, and promotion to Stable ran no check at all. v3.0.9 shipped an old SDK because
nobody bumped a pin; v3.0.10's first upload failed after a five-minute build because the active
`gh` account could not write.

After this design:

- "Preview" means exactly one thing: content or a build that an end user can opt into early. The
  SDK has no channel.
- A release or a promotion is refused unless it carries the newest SDK and the latest stable
  catalog, and a refusal changes nothing on disk or on GitHub.
- Every release states, in a machine-readable asset, what it carries.
- The catalog repo cannot publish a catalog the shipped installer cannot read.
- Switching channels in the app gives you that channel's catalog, after telling you what goes away.

## 2. Rules that bound every decision below

1. **No deadlock, ever.** No update mechanism may leave a user in a state whose only exit is an
   uninstall or reinstall. Every runtime check falls through: refresh if possible, otherwise keep
   what is installed and say so. A hard refusal exists only at release time, where it costs the
   maintainer a retry and the user nothing.
2. **Updating stays the user's choice.** The app never applies a catalog update by itself. The
   one exception is a channel switch, because choosing a channel is the choice. #36 adds an
   opt-in automatic mode later.
3. **A refusal changes nothing.** Every precondition of a release runs before the version is
   written, and a promotion that fails leaves the release a pre-release.
4. **A check that cannot run is a refusal, not a pass**, and no override flag can wave it through.

## 3. What "channel" means per artifact

| Artifact | Preview | Stable | Consumer |
|---|---|---|---|
| SDK packages (`Little-God1983/DiffusionNexus.Installer.SDK`) | none | every tag; plain semver | installer csproj pins, gated |
| Catalog (`Into-The-Latent/DiffusionNexus.Catalog`) | `preview` release, rebuilt on every push to `main` | tag `vN`, becomes Latest | app at runtime; embedded seed at build time |
| Installer app (`Into-The-Latent/DiffusionNexus.Installer`) | GitHub pre-release flag on a plain `X.Y.Z` | flag off, set by `Promote-Release.ps1` | electron-updater |

One user setting (`UserSettings.CatalogChannel`, env override `DIFFUSIONNEXUS_CATALOG_CHANNEL`)
drives both the catalog channel and the app channel. Unchanged.

### 3.1 The SDK has no channel

The SDK's only consumers are our own repos, every one of them pins an exact version, and the
only real-world test of an SDK build is an installer release. A `-preview` suffix therefore
carried no information the pin did not, while letting a public Stable installer ship something
called "preview".

- Tag `v2.0.0` on the current `develop` head (`a50112f`, the same commit as `v2.0.0-preview.9`,
  so the packages have identical content). No `2.0.0` package exists in any cache or on the
  registry; verified 2026-09-29.
- From then on: patch for a fix, minor for a feature, major if the installer's compile breaks.
  Any merged PR to `develop` may be tagged; none has to be.
- The catalog repo's workflows install `dn-catalog` with `--prerelease`; drop the flag once
  `2.0.0` exists. It would keep working either way.
- The installer's release script already compares packaged DLLs by hash, not version string,
  precisely because a local SDK build stamps `2.0.0+<sha>`. Nothing to add.
- The SDK repo's `CLAUDE.md` gets a note ("no channels, plain semver, see this spec") in the
  SDK PR for section 7.1's overload (issue S1 in section 8).

## 4. Release-time gates in the installer

All in `Scripts/`, PowerShell 7.2+, no Pester (tests are plain pwsh scripts under
`Scripts/Tests/`, run in CI; they build throwaway git repos and never talk to GitHub). Exit codes
are a contract shared by every check script: **0** passes, **3** behind (overridable), **2** not
checked (never overridable). Exit 1 is left to pwsh for a script that does not parse.

### 4.1 `New-Release.ps1`, step order

Everything under "Step 0" runs before `Directory.Build.props` is touched.

| Step | What | Refusal can be overridden by |
|---|---|---|
| 0a | `GITHUB_PACKAGES_TOKEN` is set (moved up from after the version write) | — |
| 0b | A signed-in `gh` account can write to the repo (`ReleaseAccount.ps1`, skipped with `-SkipUpload`) | — |
| 0c | SDK pin is current (`Test-SdkPin.ps1`) | `-AllowOlderSdk`, exit 3 only |
| 0d | Embedded catalog seed equals the latest stable catalog (`Test-CatalogSeed.ps1`) | `-AllowOlderCatalog`, exit 3 only (an older stable release) |
| 1 | Write version, clear publish folder, `dotnet publish -p:UseLocalSDK=false` | — |
| 1a | Packaged SDK DLLs are byte-identical to the pinned packages (exists) | — |
| 1b | Third-party notices current (exists) | — |
| 1c | `build-info.json`: run the packaged app with `--build-info`, verify its answer against 0c/0d (section 4.5) | — |
| 2 | Repackage with the publish config, `app-update.yml` present (exists) | — |
| 3 | Upload with the resolved token; notes end with the two lines from section 4.6 | — |

### 4.2 `Test-SdkPin.ps1` — issue #29's design, amended in review

Reads the SDK pins from every `*.csproj` one folder deep and requires them to agree. `git fetch`
in the local SDK checkout (the folder `Directory.Build.targets` redirects to; the check never
touches its working tree, branch or tags: the remote's tags are read into a private ref namespace,
forced and pruned, so a tag re-pointed on GitHub is taken as it is there and a tag that exists
only locally is not a release). Lists `git log --no-merges v<pin>..origin/develop --
<pinned package folders> Directory.Build.props Directory.Build.targets Directory.Packages.props`:
the packages' own folders, plus the root build files MSBuild imports into every package, except a
root commit that only changes the project version, package metadata properties or comments. The
newest remote tag on `origin/develop` at or above the pin (semver, any major; unparsable tags
skipped) decides the advice: "bump to vX" or "tag and publish the SDK first".
`-Pin <version>` checks a given version instead of the project pins (for promotion). Exit 2 for:
no SDK checkout, failed fetch, pins that disagree, a pin with no tag, a project file that is not
valid XML. Runs in a child `pwsh` so a profile's `$PSNativeCommandUseErrorActionPreference` cannot
turn git's "no" answers into errors.

### 4.3 `Test-CatalogSeed.ps1` — new

The embedded seed (`DiffusionNexus.Installer.Electron/Assets/Catalog/catalog.zip` +
`manifest.json`) must **equal** the latest stable catalog release: same `catalogVersion`, same
`commit`, same `archive.sha256`, and the zip's actual sha256 must match its own manifest. Not
"not older": a seed ahead of the tag ships content the channel does not serve (under SDK 2.0.0 it
rolled back on the first check, and a seed behind the tag reseeded over a newer installed copy on
the next launch; `docs/manual-smoke.md` §1.4–1.6 records the ping-pong. SDK 2.1.0 ignores an
older publication of the section's own channel and never reseeds over a remote apply, so the
ping-pong is gone, but a wrong seed is still wrong content on a fresh machine). A Preview
installer build embeds stable too, because promotion never rebuilds and a promoted binary must
not carry a preview seed to Stable users.

- Downloads `manifest.json` from `https://github.com/Into-The-Latent/DiffusionNexus.Catalog/releases/latest/download/manifest.json`
  (public, no token). Cannot download → exit 2. The releases page is `-ReleaseBase`, else
  `$env:DIFFUSIONNEXUS_CATALOG_RELEASES`, else that URL (the script tests serve a fixture there);
  the URL read is printed with the answer. `Update-CatalogSeed.ps1` takes the same parameter.
  `New-Release.ps1` (and `Promote-Release.ps1`) never rely on the environment variable: they pass
  `-ReleaseBase` explicitly (their own `-CatalogReleases`, default the real page), so a value left
  in a developer's environment cannot steer a release.
  `Scripts/CatalogRelease.ps1` (dot-sourced by both scripts and by `New-Release.ps1`) holds the
  seed folder, the download and the manifest reader.
- `-Expect "<version> <commit> <sha256> <channel> <generatedAt>"` (one string, because `pwsh -File`
  hands a script literal strings and cannot fill an array parameter) compares a given seed instead
  of the working tree's (for promotion: the `catalogSeed` of `build-info.json`); nothing on disk is
  read. A channel other than Stable is exit 4; `generatedAt` is compared as an instant. Both
  paths validate through one `New-CatalogSeed` in `CatalogRelease.ps1`.
- In the working tree the whole seed manifest must be the release's (parsed, so line endings do
  not count): the SDK records the seed's channel and pack time as where the installed catalog came
  from, so a Preview stamp or a hand-made `generatedAt` over the right archive is a wrong seed.
- Exit 3 when the seed is an older stable release: behind the latest, and exactly the release of
  its own number (`<releases>/download/v<N>/manifest.json`), with the exact command to fix it:
  `pwsh Scripts/Update-CatalogSeed.ps1` then commit. Only exit 3 is overridable: the override is for
  a deliberate hold-back, which `Update-CatalogSeed.ps1 -Version N` produces.
- Exit 4 when the seed is no stable catalog release: ahead of the latest (content the stable channel
  does not serve), another catalog under the latest's number, not the release of its own number,
  or not a Stable manifest. Listed the same way; no flag ships it.
- Exit 2 when the seed or a release cannot be
  read, when `git status --porcelain -- Assets/Catalog` is not empty (uncommitted seed files must
  never ship), or when the zip's hash does not match its manifest.

### 4.4 `Update-CatalogSeed.ps1` — new

The write half, kept out of `New-Release.ps1` so a refused release still changes nothing.
Downloads the latest stable `manifest.json` and `catalog.zip`, verifies the archive against the
manifest's sha256, replaces the two files under `Assets/Catalog`, and prints the version it
installed. The "chore(catalog): embed the vN stable catalog seed" commit that preceded every
release becomes this script plus one `git commit`. `-Version N` fetches a specific stable tag
instead, for a deliberate hold-back.

### 4.5 `build-info.json` — what a release says about itself

The .NET entry point of the installer (the one under `resources/bin`, which is what runs; the
Electron exe is only a shell) accepts `--build-info`: it prints one JSON object to stdout and
exits 0 before any host or window is created.

```json
{
  "app": "3.0.11",
  "sdk": "2.0.0",
  "catalogSchema": 1,
  "catalogSeed": { "version": 5, "commit": "51e1684cfa48d22344e82e7037e8e97018be72bd", "sha256": "e7d3…",
                   "channel": "Stable", "generatedAt": "2026-09-25T14:15:43.8266732+00:00" },
  "builtAt": "2026-09-30T18:00:00Z",
  "sdkPackages": [ "DiffusionNexus.Installer.SDK.Catalog", "DiffusionNexus.Installer.SDK.Models",
                   "DiffusionNexus.Installer.SDK.Services", "DiffusionNexus.Installer.SDK.Shared" ]
}
```

`builtAt` is the write time of the app's own assembly file. The app reports `null` when it has no
such file to date (a single-file publish); the shipped app never is one, and Step 1c refuses a
build that reports `null`, so readers of the uploaded asset may rely on a timestamp.

- `app`: `AppVersion`. `sdk`: the informational version of the packaged
  `DiffusionNexus.Installer.SDK.Catalog` assembly, with any `+sha` stripped. `catalogSchema`:
  `CatalogSchema.Supported`. `catalogSeed`: read from the embedded `manifest.json` (channel by
  name, `generatedAt` as in the manifest: the SDK records both from the seed), and `sha256` is
  checked against the embedded `catalog.zip` bytes; no seed, or bytes that are not the manifest's,
  gets no document and a non-zero exit. `sdkPackages` (added in #30's review): the
  `DiffusionNexus.Installer.SDK.*` assemblies beside the loaded Catalog assembly, sorted, without
  `.dll`; none gets no document. Promotion judges the SDK commits in exactly these packages, so
  the checkout it runs from cannot narrow the check.
- Step 1c runs it against the **packaged** app in the publish folder, so the asset is what the
  binary says, not what the script assumed. The script then requires `sdk` = the csproj pin,
  every pinned SDK package among `sdkPackages`, and `catalogSeed` = what step 0d confirmed, and
  refuses on any mismatch: that is a build that does not contain what was checked.
- Uploaded as a release asset next to the installer, the blockmap and `latest.yml`. It is the
  source of truth for promotion (section 5) and for the catalog repo's gate (section 6).
- The unit test that already checks the embedded resources is extended to run the entry point's
  build-info path and parse the result.

### 4.6 Release notes

`New-Release.ps1` appends two human lines, generated from the same data as the asset:

```
Built with Installer SDK 2.0.0
Bundled catalog v5 (stable)
```

Nothing reads these back. The notes-line reader from #30's plan (`SdkReleaseLine.ps1`,
`Get-SdkVersionFromNotes`) is dropped; the asset replaces it.

### 4.7 `ReleaseAccount.ps1` — the 2026-09-25 comment on #29, unchanged

`Resolve-ReleaseToken` tries the active `gh` account, then the repo owner's, probing each with
`gh api repos/<repo> --jq .permissions.push` under that token; a failed probe is never "can
write". `Invoke-WithGhToken` runs one command with `GH_TOKEN` set and restores it afterwards,
also on throw. `Get-NoReleaseAccountMessage` names the active account and the one-time fix
(`gh auth login` as the owner). No `gh auth switch`; the active account never changes. Only the
scripts' own `gh` calls use the token.

## 5. Promotion — `Promote-Release.ps1` (issue #30, rewritten)

`.\Scripts\Promote-Release.ps1 -Version X [-AllowOlderSdk] [-AllowOlderCatalog]`:

1. Resolves the release token (section 4.7); none → refused before anything is read.
2. `gh release view vX` and `releases/latest` (a 404 there = no full release yet, nothing to go
   backwards from; any other failure, or a latest tag that is no `vX.Y.Z`, → refused). vX must
   exist, not be a draft, and carry the four assets `New-Release.ps1` uploads (installer,
   blockmap, `latest.yml`, `build-info.json`), each in state `uploaded` with a size: Stable
   installs read `latest.yml` and the installer from whatever release is latest. vX already
   latest → "nothing to promote"; below latest → refused (an older Stable release: nothing to
   promote); un-marked but not latest (a half-done promotion, a hand edit) → promoted again.
3. Downloads `build-info.json` from that release. Missing (a release made before this design) →
   refused with "cut a new Preview".
4. `Test-SdkPin.ps1 -Pin <sdk> -Packages <sdkPackages>` and `Test-CatalogSeed.ps1 -Expect <seed>`,
   judged against SDK `develop` and the latest stable catalog **as of now**, not as of the build;
   `<seed>` is the five `catalogSeed` values, read from the asset's text (`Read-BuildInfoSeed`).
   Both always run. Exit 3 on either → refused with the list and "cut a new Preview", unless the
   matching `-AllowOlder*` flag is given. Exit 2, and the seed gate's exit 4 (no stable release at
   all) → refused, no override.
5. Step 2 again (the gates take a while; another promotion may have landed), then
   `gh release edit vX --prerelease=false --latest` under the resolved token, then a read-back. The
   message says what GitHub shows afterwards: still a pre-release, un-marked but not latest
   (re-run to finish), or promoted. What GitHub shows decides the outcome, not gh's exit code: a
   success GitHub does not show (after a few reads a second apart, as `releases/latest` can trail
   the edit) is a failure, and a gh failure GitHub shows as done is a promotion with a warning.
   A refusal in step 4 says what the release stays as: a pre-release, or un-marked but not latest.

Since promotion walks the packages the build ships, `Test-SdkPin.ps1` treats a package folder the
pin has but SDK `develop` removed or renamed as behind (the removing commit is listed, exit 3),
not as unchecked; and Step 1c requires the shipped and the pinned SDK packages to be one set, both
ways, so release and promotion judge the same packages.

Promotion never rebuilds: Stable gets exactly the binaries testers ran. The README, the
`New-Release.ps1` help and its final hint point at this script instead of the hand-typed command.

## 6. The catalog repo's ordering gate

Closes the one deadlock that exists today: a catalog tagged with a schema the shipped app cannot
read leaves every user on `RequiresNewerSoftware` with an app updater that says "up to date".

- `release.yml` (stable tag): after packing, download `build-info.json` from the installer repo's
  **Latest** release (`releases/latest/download/build-info.json`, public). Refuse the release when
  the packed manifest's `schemaVersion` > `catalogSchema`, with the message "the Stable installer
  reads schema N; ship an installer that reads M first".
- `ci.yml` (preview on push to `main`): same, against the **newest** installer release including
  pre-releases (`gh release list --limit 1`), because a Preview app may read a newer schema before
  Stable does.
- Cannot download the asset → refuse (rule 4). Enable both gates only after the first installer
  release that carries the asset exists, otherwise they refuse everything.
- Only the schema is gated. Content never is; `SchemaVersion` is the only compatibility contract
  the app enforces.
- Per the standing rule for that repo: direct commits on `main`, no branch, no PR.

## 7. Runtime behaviour in the app

### 7.1 Switching channels applies that channel's catalog

Today the radio saves the preference and runs a check; the page then says "Catalog v4 is
available on Stable" with an Apply button, and the other channel's content stays until clicked.
New flow in `CatalogUpdateCoordinator`, surfaced on `/updates`:

1. **Preview the switch.** Run a check against the *target* channel without saving anything.
   The SDK gets `ICatalogUpdateService.CheckAsync(CatalogChannel channel, ct)`; the existing
   overload keeps reading `CatalogOptions.Channel`. The coordinator never flips the shared option
   for a preview. `ApplyAsync` already takes the channel from the check it is given, so applying
   a target-channel check downloads from and stamps that channel. The diff the SDK already
   computes says what the switch would change.
2. **Warn before, not after** (standing rule: content warnings come before the action). When the
   diff removes or changes anything, show it before the switch:
   > Stable is at v4. Switching removes **Qwen-Image-2.1** and changes 3 workflows. Software you
   > have already installed is not affected.
   > [Switch to Stable] [Keep Preview]
   "Removed" and "Updated" entries from the diff are what goes away; the sentence about installed
   software is there because the warning is about the list of things you can install, not about
   anything on disk. When the target channel's content equals the installed content (a preview
   numbered by content is exactly that case), there is no warning and nothing to apply; only the
   preference is saved.
3. **Switch.** Save the preference, then apply, with the same progress line and result display as
   a manual apply. On success `catalog-state.json` carries the new channel (the SDK already
   stamps it only when content landed).
4. **Fall through.** If the preview check or the apply fails (offline, 404 in the preview window,
   stalled download), the preference is still saved (rule 1: never block a choice), the
   installed content stays, and the page says: "You follow Stable. The installed catalog is
   still from Preview (v5). [Retry]". The next successful check shows the same diff with Apply.
   "Keep Preview" saves nothing and snaps the radio back.

The app channel follows the saved preference as today (`AppUpdateChecker` reads it on every
check), so a switch to Stable while on a newer Preview build keeps that build until Stable
overtakes it. Unchanged, and already stated on the page.

As built (#39): `SwitchChannelAsync` previews, `ConfirmSwitchAsync` is Switch, `KeepChannel`
is Keep. Only "Removed" and "Updated" entries warn: a diff of additions alone, or an empty one
(shared files only), switches and applies without asking. While a switch waits for an answer,
checks, applies and other switches are refused and the radios show the choice being asked about.
A switch during an install saves the preference and holds the apply behind the usual "once
<workload> has finished" line. "Retry" runs a check; when that check offers Apply, the Apply
button is the retry. The "still from" line (`SwitchIncomplete`) ends when an apply succeeds or a
check finds the installed catalog current. Under `DIFFUSIONNEXUS_CATALOG_CHANNEL` a switch saves
the preference only: the variable decides what this run installs.

### 7.2 The install says which catalog it used

At install start the log gets one line, and the result view one row:
`Catalog v5 (Stable, 51e1684)`, read from `LocalCatalogState` at that moment. Read-only, so a
support question can be answered from the report. No refresh before an install (rule 2); an
install uses the catalog that is installed when it starts.

### 7.3 Accepted: the preview window

The catalog workflow deletes and recreates the `preview` release on every push to `main`, so
the preview manifest URL answers 404 for a few seconds, and a check can read a new manifest
before the archive is replaced (hash verification then fails, nothing is changed). Both end in
a clear message and a retry, pushes are rare, and only Preview users can hit it. Accepted, not a
bug. Narrowing it (keep the release, replace assets in order) would remove only the first window
and add an upload-order rule nobody would remember.

## 8. Issues and PRs

One issue, one PR. #29 already had its PR (#33 delivered its pin bump from the branch named
for it) and is closed with a pointer to #33 and issue 1. #30 keeps its number and gets this
document's section 5 as its body.

| # | Repo | Scope | Depends on |
|---|---|---|---|
| 1 (#37) | Installer | Release gates, SDK half: `Test-SdkPin.ps1`, `ReleaseAccount.ps1`, every precondition before the version write, `--build-info` + `build-info.json` with `app`/`sdk`/`catalogSchema`, tests in CI, README | SDK tag `v2.0.0` |
| 2 (#38) | Installer | Release gates, catalog half: `Test-CatalogSeed.ps1`, `Update-CatalogSeed.ps1`, `catalogSeed` in `build-info.json`, notes lines, tests | 1 |
| 3 (#30) | Installer | `Promote-Release.ps1` reading the asset and rerunning both gates | 2 |
| 4 (#39) | Installer | Channel switch applies the channel's catalog with the warning (7.1) + install names its catalog (7.2) | SDK 2.1.0 |
| — | SDK | Tag `v2.0.0` (no code) | — |
| S1 (SDK #71) | SDK | `CheckAsync(CatalogChannel, ct)` overload + CLAUDE.md note "no channels, plain semver"; tag `v2.1.0` | — |
| C1 (Catalog #3) | Catalog | Ordering gate in both workflows; tracked by an issue there, delivered as a direct commit on `main` | one release from 2 |
| #36 | Installer | Automatic apply setting, off by default | later |

Branches: `feature/release-gates-sdk`, `feature/release-gates-catalog`,
`feature/promote-release`, `feature/channel-switch-applies`, all off `main` (this repo has no
`develop`). This document lives on the first of them.

## 9. Testing

- Scripts: plain pwsh tests under `Scripts/Tests/`, one file per script, shared `TestKit.ps1`
  (fixtures = throwaway git repos in a path with a space, an apostrophe and square brackets, so
  quoting, source-text pasting and `-Path` wildcard handling are all under test; a fake global
  `gh` function stands in for the CLI). #29's 15 cases, #30's 13 + 2 account cases and the 9 `ReleaseAccount` cases are
  reused as posted; `Test-CatalogSeed` and `Update-CatalogSeed` get their own cases (match, wrong
  version, wrong commit, wrong hash, zip disagrees with its manifest, uncommitted seed, download
  fails → 2, `-Expect`). The seed cases serve a fixture "GitHub Releases" folder over a local
  `HttpListener` in its own runspace (a 302 for `releases/latest`, as GitHub answers; a 404 for a
  missing asset), so the download path under test is the real one and no case touches the network.
  CI runs every `Scripts/Tests/*.Tests.ps1` and fails on any failed case.
- `--build-info`: one unit test runs the entry point's build-info path in-process and parses the
  JSON; `New-Release.ps1 -SkipUpload` is the packaged proof.
- Runtime: coordinator tests for preview-then-switch, warning only when the diff is non-empty,
  preference saved even when apply fails, "Keep" saves nothing, apply refused while an install
  runs; bUnit tests for the warning block and the retry line. Manual smoke section for a real
  Preview→Stable switch with content on both sides.
- Catalog gate: verified once by hand with a scratch `build-info.json` that declares a lower
  schema than the packed manifest (the workflow step is a shell snippet that can run locally).

## 10. Out of scope

- Automatic catalog apply (#36).
- App self-update mechanics (electron-updater, the Preview pin via `updateConfigPath`).
- SDK 1.x, the Avalonia installers and the main app: they consume `release/1.x` and none of this.
- Repairing old `Update-ComfyUI.bat` scripts on existing installs (main-app #577).
- Dropping the SDK `-preview` suffix from anything already published; old versions stay as they are.
