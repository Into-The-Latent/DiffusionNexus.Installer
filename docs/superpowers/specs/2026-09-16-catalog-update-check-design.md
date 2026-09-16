# Installer 3.x — catalog update check, apply, and the Preview channel

Date: 2026-09-16. Status: approved in conversation, awaiting spec review.
Follow-up filed: [#19](https://github.com/Into-The-Latent/DiffusionNexus.Installer/issues/19)
(app self-updater Preview channel).

## 1. Why

The catalog editor already publishes two ways. **Preview** commits and pushes to `main`,
and the catalog repo's CI repoints the `preview` pre-release within a minute. **Release**
tags `vN` and CI creates the stable release GitHub serves as "Latest". The SDK's
`ICatalogUpdateService` knows how to check either channel and apply the result
atomically, and the installer registers it at startup.

Nothing in the installer ever calls it. The "Check for Updates" page runs only the
Electron app self-updater. The catalog a user has is whatever the embedded seed held
when their installer build was made. So today a Preview push reaches nobody, a Release
reaches nobody, and content ships only inside a new installer build, after a manual
two-file seed swap.

This spec wires the check and apply into the installer and gives each editor button an
audience: Release reaches everyone on Stable; Preview reaches whoever opted into Preview,
which is the author and any tester, through the real download, verify and swap path.

## 2. Decisions already made

| Decision | Choice |
|----------|--------|
| Channel policy | Stable by default. Preview is opt-in per machine. Never Preview for everyone. |
| Where the preference lives | The installer's user settings, via a new `UserSettings.CatalogChannel` property in the SDK. An environment variable overrides it per process. |
| Where it is switched | Developer tools page (Debug builds) writes the setting. Release builds switch only through the environment variable. |
| Sections | Apply both. The SDK-2 spec's per-kind toggles are dropped; the editor releases the catalog as a unit. |
| Auto-apply | No. The check runs automatically, applying is the user's click. |
| App self-updater channel | Out of scope, issue #19. The app has only the `latest` channel today. |
| Seed refresh at build time | Stays manual (accepted 2026-09-15). Rule recorded in §10. |

## 3. Vocabulary

Two update mechanisms, three channels that exist:

| Mechanism | Stable | Preview |
|-----------|--------|---------|
| Catalog | Releases `v1`, `v2`, `v3` ("Catalog vN"), read via `releases/latest/download/manifest.json`. Manifest says `"channel": "stable"`. | Pre-release tagged `preview`, title "Preview (sha)", rebuilt on every push to `main`. Manifest says `"channel": "preview"`. |
| App | Full releases `v3.0.x` with `latest.yml`. electron-updater calls this `latest`. | Does not exist. See #19. |

The UI says **Stable** and **Preview** for the catalog, and "latest release" for the app.
electron-updater's `latest` / `beta` words never appear.

Preview manifests carry `catalogVersion = last stable + 1`. When that content is later
released as `v(N+1)`, a Preview client that already applied it sees no item-hash
changes and reports up to date. `CatalogDiff` is hash-based and version-agnostic, so a
client switching Preview → Stable is simply offered the stable content.

## 4. Channel preference

### 4.1 SDK change (develop, `2.0.0-preview.8`)

`DiffusionNexus.Installer.SDK.Models/Installation/UserSettings.cs` gains:

```csharp
/// <summary>
/// Catalog update channel the host follows: "Stable" or "Preview". Null means Stable.
/// A string rather than the enum because Models cannot reference the Catalog package.
/// </summary>
public string? CatalogChannel { get; set; }
```

One property, one round-trip test in the SDK's JSON settings repository tests. Branch
`feature/catalog-channel-setting`, PR to `develop`, tag `v2.0.0-preview.8`.

### 4.2 Resolution (Installer.Core, pure)

```csharp
public enum CatalogChannelSource { Default, Setting, Environment }

public static class CatalogChannelResolver
{
    public const string EnvironmentVariable = "DIFFUSIONNEXUS_CATALOG_CHANNEL";

    /// <summary>Environment wins over the saved setting, which wins over Stable.
    /// Values are matched case-insensitively against "stable" / "preview"; anything else is
    /// ignored (logged by the caller) and resolution falls through.</summary>
    public static (CatalogChannel Channel, CatalogChannelSource Source) Resolve(string? environmentValue, string? savedValue);
}
```

The environment value is never written back to settings. A tester on a Release build
sets the variable and gets Preview for that process only; removing it returns them to
whatever the saved setting says.

### 4.3 The SDK's own channel stamp stays provenance

`LocalCatalogState.Channel` is stamped by `ApplyAsync` for content that actually landed.
The installer never writes it as a preference. The `/updates` page shows both when they
differ: "Following: Preview. Installed content: Stable v3."

## 5. `ICatalogUpdateCoordinator` (Installer.Core)

One singleton owns the check/apply lifecycle, mirroring what `UpdaterLog` does for the
app updater: Electron-free, subscribable through a plain `Action`, testable with a fake
`ICatalogUpdateService`.

```csharp
public enum CatalogUpdatePhase { Idle, Checking, Checked, Applying, Applied }

public interface ICatalogUpdateCoordinator
{
    CatalogChannel Channel { get; }
    CatalogChannelSource ChannelSource { get; }
    CatalogUpdatePhase Phase { get; }
    CatalogUpdateCheck? LastCheck { get; }          // null until the first check completes
    LocalCatalogState? Installed { get; }           // refreshed after every check and apply
    CatalogDownloadProgress? Progress { get; }      // non-null only while Applying
    CatalogApplyResult? LastApply { get; }
    bool UpdateAvailable { get; }                   // LastCheck?.Outcome == UpdatesAvailable
    bool CanApply { get; }                          // UpdateAvailable && Phase == Checked && no install running
    string? ApplyBlockedReason { get; }             // "…once <workload> has finished" while an install runs

    event Action? Changed;

    Task CheckAsync(CancellationToken ct = default);
    Task ApplyAsync(CancellationToken ct = default);
    Task SetChannelAsync(CatalogChannel channel, CancellationToken ct = default);
}
```

Implementation `CatalogUpdateCoordinator(ICatalogUpdateService updates, CatalogOptions options,
IUserSettingsRepository settings, IInstallSession session, Func<string?> readEnvironment, ILogger<…>)`:

- **First use** resolves the channel: reads `UserSettings.CatalogChannel` through
  `GetOrCreateForCurrentUserAsync`, calls `CatalogChannelResolver.Resolve`, and sets
  `options.Channel`. `CatalogOptions` is the mutable singleton `CheckAsync` reads, so this
  is the one place that writes it.
- **CheckAsync**: no-op while Checking or Applying (returns the in-flight task's
  completion). Sets Phase Checking, clears `Progress` and `LastApply`, calls the SDK,
  stores `LastCheck`, reloads `Installed` from `options.InstalledCatalogPath`, sets
  Phase Checked. Clearing `LastApply` here as well as `Progress` matters because the
  `/updates` apply-failure line reads `LastApply` directly rather than through
  `LastCheck`'s outcome -- without this, a fresh check run after a failed apply would
  still show the previous attempt's error and retry banner. The SDK's `CheckAsync` never
  throws, but the settings read and state reload can; those are caught and reported as a
  `Failed` check with the exception message.
- **ApplyAsync**: refused (no-op, logged) unless `CanApply`. Sets Phase Applying, calls
  `ApplyAsync(LastCheck, CatalogSections.All, progress)`, forwards every progress report
  through `Changed`, stores `LastApply`, reloads `Installed`, sets Phase Applied. On a
  result with `Failed != None` or an `Error`, Phase returns to Checked so the user can
  retry; `LastApply` carries the message. The SDK lets a user cancel through as
  `OperationCanceledException`; the coordinator catches it and resets Phase to Checked
  without recording an error (§8).
- **SetChannelAsync**: refused while Checking or Applying. Saves the setting, sets
  `options.Channel`, clears `LastCheck`, `LastApply` and `Progress`, Phase Idle, fires
  `Changed`. When the environment variable is set the saved setting still changes but
  `Channel` keeps reporting the environment value and `ChannelSource == Environment`;
  the Developer tools page says so.
- **Install running**: `CanApply` consults `session.Phase == InstallPhase.Running` at
  read time and the coordinator subscribes to `session.Changed` to re-raise `Changed`,
  so the Apply button re-enables when the install ends without anyone polling.
- **Logging**: every step (channel resolved and from where, check started, outcome with
  counts, download started, bytes, apply result per section) goes to `ILogger` so a
  stalled update shows its last successful step in the console log.

Concurrency guard is a single `SemaphoreSlim(1,1)` around check and apply; a second
caller observes Phase and returns.

### 5.1 Startup check

`CatalogUpdateStartupCheck : IHostedService` in the Electron project. `StartAsync`
fires `Task.Run(() => coordinator.CheckAsync())` and returns immediately, so a slow
GitHub never delays the window. It runs in both hosting modes, Electron and plain web,
because unlike the app updater the catalog check has no Electron dependency. Registered
from `AddInstallerHostServices` so `DependencyInjectionTests` covers it.

### 5.2 Change rows (Installer.Core, pure)

```csharp
public sealed record CatalogChangeRow(string Kind, string Name, ChangeKind Change, string VersionText);

public static class CatalogChangeRows
{
    /// <summary>Flattens a check into display rows: Kind "Workload" or "Workflow", Name (workflows as
    /// "Workload – Workflow" when WorkloadNames is non-empty), VersionText "v1 → v1.1" for Updated,
    /// the single version for Added/Removed. Grouped Added, Updated, Removed, stable order within a group.</summary>
    public static IReadOnlyList<CatalogChangeRow> Build(CatalogUpdateCheck check);
}
```

The same shape the editor's Release dialog renders, so what the author approved is what
the user reads.

## 6. UI

### 6.1 Top bar

`TopBar` injects `ICatalogUpdateCoordinator` and `UpdaterLog`, subscribes to both
`Changed` events (method groups, unsubscribed in `Dispose`), and renders a dot on the
"Check for Updates" link when `coordinator.UpdateAvailable || log.UpdateReady`. CSS class
`top-bar-attention` on the link, with a `title` attribute naming what is waiting
("Catalog update available", "App update ready", or both).

Every fixture that renders `TopBar`, directly or through `ScreenShell`, registers both
services. A `Support/UpdateSignals` test helper registers a stub coordinator and an
empty `UpdaterLog` so each affected fixture gains one line rather than two registrations.

### 6.2 Welcome page notice

Under the gallery title, when `UpdateAvailable`:

> A catalog update is available: 3 workloads and 2 workflows changed.
> [Review and apply](/updates)

When the outcome is `RequiresNewerSoftware`:

> The catalog has moved to a format this version cannot read. Update the installer to
> receive it. [Check for updates](/updates)

No notice for UpToDate, Failed or OverrideActive. The notice is a `<p class="catalog-update-notice">`
inside the existing welcome column, above the software strip, and disappears after a
successful apply.

When both counts are zero -- a push that only touched shared files such as
`repositories.json` or `wheels.json`, changing neither section -- the counts are omitted
rather than read as "0 workloads and 0 workflows changed", which reads as nothing having
happened when there is still an update worth taking:

> A catalog update is available. [Review and apply](/updates)

### 6.3 `/updates` page

The page keeps its app section and gains a **Content catalog** section beneath it. The
existing "Check for updates" button runs both checks; the app half stays guarded by
`ElectronActive`, the catalog half always runs, so the button is enabled in plain-web
mode too and its label stays "Check for updates".

The catalog section renders, top to bottom:

1. **Following:** `Stable` or `Preview`, with "(set by DIFFUSIONNEXUS_CATALOG_CHANNEL)"
   when `ChannelSource == Environment`.
2. **Installed:** "v3 (Stable), applied 2026-09-15" from `Installed`, or "not yet
   installed" when the state file has no sections.
3. **Outcome line**, one of:

   | Outcome | Text |
   |---------|------|
   | Phase Checking | "Checking the catalog…" |
   | UpToDate | "The catalog is up to date." |
   | UpdatesAvailable | "Catalog v4 is available on Preview." then the change lists |
   | RequiresNewerSoftware | "This catalog update needs a newer version of the installer. Install the app update above first." |
   | OverrideActive | "Update check skipped: a local catalog override is active at `<path>`." |
   | Failed | "The catalog check failed: `<error>`" |
   | LastCheck null, Phase Idle | "Not checked yet." |

   The Failed row carries no added period: the SDK's own errors already end in one, and
   appending a second would read "HTTP 503.." The apply-failure line below is the
   opposite case -- its error can end mid-sentence ("sha256 mismatch") because the
   coordinator built it, not the SDK -- so it folds a trailing period from the error
   before appending its own sentence.

4. **Change lists** and **Apply button** are both gated on Phase as well as
   `UpdateAvailable`: while Phase is Checking -- a re-check the user asked for from a page
   that was already showing an update -- both stay hidden, even though the previous
   check's result is technically still sitting in `LastCheck`. Rendering the old list
   and an Apply button under "Checking the catalog…" would let the user apply content the
   running check might be about to replace.
   - Change lists: three groups, Added / Updated / Removed, each hidden when empty, rows
     from `CatalogChangeRows.Build`.
   - Apply button "Apply catalog update", rendered only for UpdatesAvailable and only
     once Phase has left Checking. Disabled while Applying. When an install is running
     the button is replaced by the same hint the app update uses: "It can be applied
     once **<workload>** has finished."
5. **Progress** while Applying: "Downloading… 42%" when the total is known, otherwise
   "Downloading… 3.1 MB".
6. **Result** after Applied: "Catalog updated to v4. [Back to all software](/)". On a
   partial or failed apply: "The catalog update failed: `<error>`. Nothing was changed."
   or, when one section landed and one did not, "Workloads were updated; workflows
   failed: `<error>`." with the Apply button back for a retry. The all-failed line folds
   the error's own trailing period rather than doubling it: an error of "sha256 mismatch"
   reads "The catalog update failed: sha256 mismatch. Nothing was changed."

The app section's updater log stays app-only. Catalog steps go to the console log via
`ILogger`, not into that list.

### 6.4 Developer tools page (Debug builds)

A **Catalog channel** panel with two radio buttons, Stable and Preview, bound to
`coordinator.Channel`, calling `SetChannelAsync` on change, followed by a "Check now"
button that navigates to `/updates` after triggering `CheckAsync`. When
`ChannelSource == Environment` the radios are disabled and a hint reads "Set by
DIFFUSIONNEXUS_CATALOG_CHANNEL for this run; the saved setting is not in effect." --
naming that the saved preference exists and is being overridden, not what it is: the
value itself is not shown, since it plays no part while the environment variable pins
the channel.

The page subscribes to `coordinator.Changed` (method group, unsubscribed in `Dispose`)
so a channel switch the coordinator refuses -- a no-op while a check or apply is already
in flight -- re-renders the radios back to the channel actually in effect, instead of
leaving them showing the click the user just made.

## 7. Apply semantics

- Both sections, always. `CatalogSections.All`.
- Refused while `IInstallSession.Phase == Running`. A running plan was built from the
  current catalog; swapping content under it has no upside.
- Allowed while a wizard is mid-configuration but not running. Every page reads the
  catalog through `IWorkloadSource` on navigation and the SDK invalidates its cache on
  apply, so the next screen reflects the new content. The `/updates` result line offers
  "Back to all software" rather than forcing a reload.
- No automatic re-check after apply. The SDK reloads the catalog lazily; the coordinator
  reloads `Installed` from the state file so the section shows the new version at once.

## 8. Error handling

| Failure | Behaviour |
|---------|-----------|
| Manifest unreachable, timed out, HTTP error | SDK returns Failed with a message; page shows it; startup stays silent (no notice, no dot). |
| Schema newer than supported | RequiresNewerSoftware; Welcome notice + page text point at the app update. Apply not offered. |
| Local override active | OverrideActive; page names the path; nothing fetched. |
| Settings file unreadable | Coordinator logs, resolves channel as Default, proceeds. Saving a channel that then fails surfaces as an error line on the Developer tools panel. |
| Download stall / sha256 mismatch / swap failure | SDK returns `CatalogApplyResult` with `Failed` sections and `Error`; nothing is changed on disk (staged apply). Phase back to Checked, Apply offered again. |
| User cancels apply | The coordinator catches the `OperationCanceledException` the SDK lets through, resets Phase to Checked, leaves `LastApply` null and fires `Changed`. Not shown as an error. |
| Install starts while page open | `session.Changed` re-raises `Changed`; the Apply button turns into the wait hint without a refresh. |

## 9. Testing

**Installer.Core (xunit, no UI):**
- `CatalogChannelResolverTests`: env wins, setting wins over default, case-insensitive,
  garbage env falls through to setting, garbage setting falls through to Stable.
- `CatalogUpdateCoordinatorTests` against a fake `ICatalogUpdateService` and a mock
  `IInstallSession`: first check resolves channel and sets `options.Channel`; each
  outcome maps to state; second concurrent check is a no-op; apply refused when not
  Checked, when nothing available, when an install runs; apply forwards progress and
  fires `Changed` per report; failed apply returns to Checked with the message; success
  reaches Applied and reloads `Installed`; `SetChannelAsync` saves, clears, and reports
  `Environment` source when the variable is set.
- `CatalogChangeRowsTests`: grouping, version text per kind, workflow naming with and
  without workload names.

**Electron (bUnit):**
- `TopBarTests`: dot present for catalog available, for app ready, for both, absent
  otherwise; title text; handler unsubscribed on dispose.
- `WelcomePageTests`: notice for UpdatesAvailable with counts, notice for
  RequiresNewerSoftware, none for UpToDate/Failed.
- `UpdatesPageTests`: every outcome row from the table in §6.3, Apply button rendering
  and disabled states, install-running hint, progress text with and without total, result
  and failure lines, button runs the catalog check in plain-web mode.
- `DebugToolsTests` (Debug only): radios reflect channel, change calls `SetChannelAsync`,
  disabled under environment override.
- `DependencyInjectionTests`: `ICatalogUpdateCoordinator` and the hosted service resolve
  from the real registrations.

**Manual smoke (new §7 in `docs/manual-smoke.md`):**
1. Edit a workload in the editor, press Preview. Wait for the `preview` release to
   repoint.
2. Launch the installer with `DIFFUSIONNEXUS_CATALOG_CHANNEL=preview`. Expect the top-bar
   dot within seconds, the Welcome notice with the right counts, and `/updates` listing
   the exact change the editor showed.
3. Apply. Expect progress, "Catalog updated to vN", and the gallery showing the change
   after "Back to all software".
4. Launch without the variable. Expect Stable, up to date against `v3`, no notice.
5. Press Release in the editor. After a minute, launch on Stable: expect the same update
   offered and applied.
6. Start an install, open `/updates` with an update pending: expect the wait hint, and
   the button once the install finishes.

## 10. Out of scope and constraints recorded

- **App Preview channel**: #19.
- **Seed refresh** stays manual. Rule: the embedded `Assets/Catalog/{catalog.zip,manifest.json}`
  must be packed from a commit **no newer than the latest stable tag**. A newer seed on a
  cold-started machine is "downgraded" to the older stable release on its first check,
  because the diff is hash-based and cannot tell newer from older. Today seed and tag are
  both `v3`.
- **Editor allows an empty release** (no changes since last stable). Worth a block in
  the Tools repo; separate change.
- **Per-kind toggles** from the SDK-2 spec: dropped, not deferred.
- **Periodic re-check** while the app is open: not needed for a short-lived installer.

## 11. Rollout order

1. SDK: `feature/catalog-channel-setting` → PR to `develop` → merge → tag `v2.0.0-preview.8`.
2. Installer: this branch `feature/catalog-update-check` builds locally with
   `-p:UseLocalSDK=true` until the tag exists, then bumps all `2.0.0-preview.7` references
   to `preview.8`. CI is red until the SDK tag is published; that is expected and the PR
   says so.
3. Installer PR to `main`, review wave, manual smoke §7.
4. Refresh the embedded seed from the latest stable tag, cut `v3.0.8` with
   `Scripts/New-Release.ps1`. From then on catalog Releases reach users without an
   installer build.
