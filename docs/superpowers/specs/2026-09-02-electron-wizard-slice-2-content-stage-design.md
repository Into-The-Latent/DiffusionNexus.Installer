# Electron Installer 3.x — Content Stage (Slice 2)

Status: design approved in chat 2026-09-02, pending written review.
Follows `2026-08-29-electron-wizard-slice-1-design.md`, which shipped as v3.0.5.

## 1. Context

Slice 1 shipped the catalog-driven gallery and a wizard whose stages are composed from
capability modules. Nine of the catalog's 25 workloads install today. The other 16 are
visible but disabled, because the wizard has no way to ask the two questions that make a
tiered pack install correctly: **how much VRAM** and **which models**.

The gate is deliberately narrow. `WorkloadCapabilities.Blocking` is
`VramProfile | ModelDownloads | LlamaCpp`, and only those block, because a module never adds
to what the pipeline does — it narrows or configures it. With no `CustomNodes` module every
declared node is cloned; with no `Workflows` module every declared workflow is exported; with
no `Accelerators` module the Triton/SageAttention steps run off the workload's own flags.
Those are correct defaults. VRAM and model selection are not: with `SelectedVramProfile = 0`
the SDK applies no filtering at all, so a tiered pack downloads every tier's variant.

`LlamaCpp` is already satisfied. So registering two modules — `VramProfile` and
`ModelSelection` — is what unlocks the remaining workloads. A third, `WorkflowSelection`, is
in this slice for parity with 1.x even though it gates nothing.

### What the catalog actually holds

Measured 2026-09-02 against `Into-The-Latent/DiffusionNexus.Catalog`:

| | range |
|---|---|
| Models per workload | 1–11 (26 download links at most, on `wan-2-2-gguf`) |
| Models with per-tier link variants | 1–4 per workload |
| Declared VRAM tiers | `8,12,16,24,32` typical; `12,16,24,32`; `24,32` on `ideogram-4-0` |
| Git repositories (custom nodes) | 1–12 |
| Workflows | 1–6, joined in from the catalog's `workflows/` folder by each workflow's own `workloads` list |

Every list is small. Nothing here needs virtualization, paging or search.

## 2. Decisions locked with the user (2026-09-02)

1. **Three modules, no node picker.** VRAM, models and workflows get pickers. Custom nodes do
   not: unticking a node silently breaks every workflow that references it, and nothing in the
   wizard can detect that. The user chose this cut explicitly.
2. **1.x behaviour, not an improved one.** The model list shows every model the workload
   declares, with no annotation of what the chosen tier will actually download or skip. Reason
   given: extra detail invites users to overthink the choice and then blame the installer.
   The tier still drives the *existence* check and the disk-space estimate, silently, exactly
   as 1.x does.
3. **Lowest tier preselected**, matching `InstallationViewModel`'s `SelectVramProfile(profiles[0])`.
   No VRAM auto-detection: the SDK's `GpuDetectionResult` carries state, GPU name and driver
   version but no memory size, and the user declined spending an SDK release on adding it.
   Choosing a tier that does not match the card is the user's responsibility.
4. **Only the tiers the workload declares are offered.** `ideogram-4-0` declares `24,32`, so
   the dropdown offers 24 and 32 — never a padded standard list.
5. **A workload with no declared tiers filters nothing.** No dropdown renders, the tier stays
   0, and every declared model downloads. `upscaling-z-image-turbo` (4 models, no tiers) is the
   shipped example.
6. **Module state is fixed properly, not patched.** Modules become per-run instances rather
   than app-lifetime singletons — see §4.1.

## 3. Scope

### In scope

- `VramProfileModule`, `ModelSelectionModule`, `WorkflowSelectionModule` and their panels,
  composing the `Content` stage.
- `ModelPresenceScanner` — one implementation of the destination/link/filename resolution that
  1.x carries in two hand-synced copies.
- Live disk-space estimate driven by tier and ticks.
- Pre-install verification of already-present files, and the single mismatch dialog that
  resolves them.
- Per-run module lifetime.
- The `Install.razor` regression guard parked as Ruling 31 in slice 1, since this slice edits
  that file's module wiring anyway.

### Out of scope

- Custom-node picker (decision 1).
- Persisting answers between runs. `IUserSettingsRepository` is still read-only in this app, so
  "remembered defaults" do not yet remember. Unchanged from slice 1; still the top carry-in.
- The `Accelerators` capability. It is detected, non-blocking, and `VcRuntimeModule` already
  handles the only consent question it implies.
- Release blockers: unsigned installer, the Start Menu shortcut that exits instantly, npm and
  Chromium third-party notices.
- `Config535`'s catalog data. It pairs torch 2.8.0 with CUDA 13.0, for which no wheel exists,
  so `WorkloadCapabilities.DetectIncompatibility` keeps it out of the gallery no matter what
  this slice registers. The fix is one line in the catalog repo and is offered separately.

## 4. Architecture

### 4.1 Per-run module lifetime

Today every `IWizardModule` is `AddSingleton`, `WizardModuleRegistry` holds those instances for
the life of the app, and `BuildPlanAsync` re-initializes the same objects for each new run. The
registry's own summary admits the constraint: "exactly one plan may be in flight at a time".

That already produced a shipped bug — CPU-only consent, shortcut name and overwrite choice
carried into the next workload because `InitializeAsync` reset only some fields. Three modules
holding *lists keyed by model and workflow id* make the same failure worse: a stale row set
would contribute another workload's ids into `ExcludedModelIds`, silently skipping downloads.

Change:

- Module registrations become `AddTransient`.
- `WizardModuleRegistry` takes `Func<IEnumerable<IWizardModule>>` instead of
  `IEnumerable<IWizardModule>`, and calls it once per `BuildPlanAsync`, so every run gets fresh
  instances. A delegate rather than `IServiceProvider`: tests pass `() => [new FakeModule()]`
  with no container.
- `SatisfiedCapabilities` is computed once in the constructor from a single resolution and
  cached. Capability is a constant of the type, and the gate must answer without a run.
- Each module keeps its defensive resets in `InitializeAsync`. Belt and braces: the resets stop
  being the only thing standing between two runs, but a module is still allowed to be
  re-initialized.

Nothing else moves. Panels already receive their module as a `[Parameter]` from
`WizardPlan` (`Install.razor`'s `RenderModule` switch) rather than resolving it from DI, so no
component can observe a different instance than the plan's. `InstallSession.Plan` holds the
running plan, which keeps that run's modules alive for as long as the install needs them.

### 4.2 `VramProfileModule`

`Content`, Order 0, satisfies `WorkloadCapability.VramProfile`.

- **Parsing** moves into a shared `VramTiers.Parse(string?)` in `Core`: split on commas, trim,
  drop a trailing `+` (`"24+"` means "24 GB or more" and is offered as 24), parse to int, drop
  anything unparseable or non-positive, distinct, ascending.
- `WorkloadCapabilities.Detect` calls the same parser instead of
  `!string.IsNullOrWhiteSpace(workload.Vram.VramProfiles)`. Without that, a workload whose
  `vramProfiles` is non-empty but unparseable (`"abc"`, `","`) is detected as needing a tier —
  a *blocking* capability — while the module's `AppliesTo` says it does not apply, so no
  dropdown renders and the agreement test between `Detect` and `AppliesTo` fails. Sharing the
  parser makes the two agree by construction, and such a workload correctly becomes
  "no tiers, nothing filtered" per decision 5.
- `Tiers` (the parsed list) and `SelectedTier` are what the panel renders.
- `InitializeAsync` parses, then sets `SelectedTier` to `Tiers[0]` — the lowest — and writes it
  to `WizardSelection.SelectedVramProfile`.
- The panel's change handler writes both `SelectedTier` and `selection.SelectedVramProfile`.
  The module owns that field; downstream modules read the *value* from the selection, never
  this module, which is the decoupling rule the contract is built on.
- `AppliesTo` is `Tiers.Count > 0`.
- `Contribute` sets `draft.SelectedVramProfile = SelectedTier`.
- `Validate` is always `Ok` — a preselected tier cannot be invalid, and there is no "none"
  option to leave unanswered.

`WizardPlan.ToOptions` already seeds `draft.SelectedVramProfile` from the selection *before*
the contribute loop, so both paths agree and a workload without this module still contributes 0.

### 4.3 `ModelSelectionModule`

`Content`, Order 10, satisfies `WorkloadCapability.ModelDownloads`.

- Rows come from `workload.ModelDownloads.Where(m => m.Enabled)` — the authoring "disabled"
  flag is not a user-facing choice — every row ticked.
- Rows are grouped for display by their resolved destination folder, as 1.x does in
  `GroupModelsByDestination`; a model with no destination groups under "Not assigned".
  Grouping is presentation only. Editing those folders stays with `ComfyFoldersModule`, which
  already owns `FolderPathOverrides`.
- Each row shows whether the file is already on disk, from `ModelPresenceScanner` (§4.4).
- `Contribute` sets `draft.ExcludedModelIds` to the unticked ids, and applies the verification
  outcome (§4.6) to `draft.ForceRedownloadUrls` / `draft.TrustedUrls`.
- `Validate` is `Ok` even when everything is unticked. Installing a workload without its models
  is a legitimate choice — 1.x allows it, and the pipeline handles an empty download set.
- `AppliesTo` is `ModelDownloads.Count > 0` — deliberately NOT `Any(m => m.Enabled)`. It has to
  mirror `Detect`, which counts every declared model: a workload whose models are all
  author-disabled would otherwise be detected as needing a module that then declines to render,
  which is exactly the disagreement §4.7 of the slice-1 design makes a test. The `Enabled`
  filter stays where it belongs, on the row list; if that leaves no rows the panel says the
  author disabled them all.

**Refresh on tier change.** The presence scan and the disk-space estimate both depend on the
tier, which is edited in the same stage. The VRAM panel already raises `Changed`, which
re-renders the stage; the model panel compares the tier it last scanned against
`selection.SelectedVramProfile` in `OnParametersSetAsync` and rescans when it differs. The
scan is filesystem-only and fast; the size estimate is debounced (§4.5).

### 4.4 `ModelPresenceScanner`

New in `Core`. One implementation of what 1.x carries twice, under a comment on both copies
reading "KEEP THIS METHOD IN LOCKSTEP" — `CheckExistingModels` for display and
`BuildExistingModelCandidates` for pre-flight, which must agree on destination, link selection
and filename or the dialog verifies files the install will never write.

```csharp
public sealed record ModelFileTarget(
    ModelDownload Model, string Url, string DestinationDirectory, string FileName, string? ExistingPath);

public sealed record ModelPresence(
    Guid ModelId, bool AllPartsPresent, string? ExistingPath, IReadOnlyList<ModelFileTarget> Targets);

IReadOnlyList<ModelPresence> Scan(
    InstallationConfiguration workload,
    string repositoryPath,
    string? modelBaseFolder,
    IReadOnlyDictionary<string, string>? folderPathOverrides,
    int selectedVramGb);
```

Per model, mirroring the pipeline exactly:

1. Destination via `ModelDestinationResolver.Resolve(workload, model, repositoryPath, modelBaseFolder, folderPathOverrides)`.
2. Links: `model.DownloadLinks.Where(l => l.Enabled)`. When there are none, fall back to
   `model.Url`, and drop the model when `selectedVramGb > 0` and
   `VramProfileHelper.VramProfileFitsSelection(model.VramProfile, selectedVramGb)` is false.
   With links, `VramProfileHelper.SelectBestMatchingLinks(enabled, selectedVramGb, null, name)`.
3. Filename: `Path.GetFileName(uri.LocalPath)` for an absolute URI, else the model is skipped
   rather than probed under 1.x's `"unknown_file"` placeholder.
4. Existing path: exact `Path.Combine(dir, fileName)` first, then
   `Directory.GetFiles(dir, fileName, SearchOption.AllDirectories)` — models are commonly
   filed into subfolders. Permission and IO errors yield "not found", never a throw.
5. `AllPartsPresent` is true only when every selected link's file is present, matching 1.x:
   a half-downloaded multi-part model is not "already downloaded".

**Which `VramProfileHelper`.** Two public classes share that name —
`SDK.Models.Helpers` (parsing, display strings, `DefaultSelectedProfile`) and
`SDK.Services.Installation.Utilities` (`GetVramProfileGb`, `VramProfileFitsSelection`,
`SelectBestMatchingLinks`). Link selection MUST use the Services one, because that is the class
`ModelDownloadStepHandler` itself calls. Import it under an alias, as 1.x does.

### 4.5 Disk space

`DiskSpaceCalculator.CalculateRequiredSpaceAsync(workload, onlyModelDownload: false, tier,
excludedModelIds, progress, ct, existingModelIds)` then
`DiskSpaceCalculator.ValidateDiskSpace(targetFolder, requirement)`, rendered under the model
list as required-versus-available, flagged when insufficient. `existingModelIds` comes from the
scan, so files already on disk are not counted as downloads.

The estimate issues HTTP HEAD requests per URL, so:

- It is debounced (400 ms) and every recalculation cancels the one before it.
- It shares one `UrlSizeResolver` with the pre-install verification, so a size resolved here is
  not fetched a second time when the user clicks Install. 1.x learned this the hard way — a
  separate resolver added a full sequential HEAD pass of apparent hang after the click.
- Failure is not fatal: unknown sizes land in `DiskSpaceRequirement.UnknownSizeModels` and the
  panel says the estimate is partial. The install is never blocked on it.

`UrlSizeResolver` and `DiskSpaceCalculator` are registered as singletons in `AddInstallerCore`
over **their own `HttpClient` with a 10 s timeout**, never the container's. The SDK registers
`HttpClient` with `Timeout.InfiniteTimeSpan` on purpose — downloads of many-GB model files must
not be cut off — and its registration carries an explicit warning that size-resolution consumers
must own a bounded client instead. Resolving the shared one would mean a HEAD against a dead
host hangs the Content stage forever. 1.x does the same thing with its own `_sizeHttpClient`.
The resolver is otherwise stateless apart from its size cache, which is exactly what should be
shared between the estimate and the verification.

### 4.6 Pre-install verification and the mismatch dialog

1.x behaviour, carried over unchanged in effect:

1. On Install, build candidates from the scan — every *ticked* model's selected links whose
   file already exists — and pass them to `ExistingModelVerifier.VerifyAsync`, which compares
   each file's size on disk against the server's.
2. No mismatches: proceed.
3. Mismatches: one dialog listing every suspect file, with redownload-or-keep per file.
   Never a prompt per file, never mid-install.
4. Dismissing the dialog cancels the install before anything starts.
5. Verification itself failing (offline, timeout) is a warning line, not a stop.

This runs in a new `InstallLauncher` in `Core`, not in `Install.razor`:

```csharp
public interface IInstallLauncher
{
    /// Returns false when the user dismissed the mismatch dialog and nothing was started.
    Task<bool> LaunchAsync(WizardPlan plan, CancellationToken ct = default);
}
```

It verifies, prompts, hands the resulting URL sets to the plan's `ModelSelectionModule`
(`plan.AllModules.OfType<ModelSelectionModule>().FirstOrDefault()`, the pattern `Install.razor`
already uses for `ShortcutsModule`), then calls `IInstallSession.StartAsync`. Order matters:
`StartAsync` calls `ToOptions()`, so the sets must be on the module before it starts.

The dialog needs a list-with-choices modal, which `IUserPrompt` (yes/no only) cannot express:

```csharp
public sealed record MismatchResolution(HashSet<string> RedownloadUrls, HashSet<string> TrustedUrls);

public interface IMismatchedFilePrompt
{
    /// Null means the user dismissed the dialog, which cancels the install.
    Task<MismatchResolution?> ResolveAsync(IReadOnlyList<ExistingModelMismatch> mismatches, CancellationToken ct = default);
}
```

Implemented in the Electron project beside `ModalPromptService`, rendered by a new modal
component, and given `IInstallSession.RunToken` so a cancel can release it. Decisions are keyed
by **URL**, never by model id — `ExistingModelMismatch` documents why: a model can have several
links and only some of them mismatch.

### 4.7 `WorkflowSelectionModule`

`Content`, Order 20, satisfies `WorkloadCapability.Workflows`.

Rows from `workload.Workflows`, name plus `Version.SubVersion`, all ticked, contributing
`draft.ExcludedWorkflowIds`. `AppliesTo` is `Workflows.Count > 0`. `Validate` is always `Ok`.
It gates nothing — `Workflows` is not in `Blocking` — so registering it changes no workload's
installability; it exists so the user can see and skip what will be written.

### 4.8 Stage composition

The `Content` stage renders VRAM, then models, then workflows, and is skipped whole when none
apply — which is every thin workload, so the six slice-1 workloads see exactly the four screens
they see today. `WizardStage.Content` already exists in the enum and already sorts between
`Location` and `System`; `WizardPlan` builds stages from what applies, so no navigation code
changes.

`Install.razor` gains three arms in `RenderModule` and passes `Changed="_moduleChanged"` to all
three panels — the wiring whose absence made the wizard uncompletable in slice 1, and which
§7 now covers with a test.

## 5. Gate and gallery impact

Registering `VramProfileModule` and `ModelSelectionModule` adds `VramProfile | ModelDownloads`
to `SatisfiedCapabilities`, which satisfies every blocking capability the catalog declares. The
installable set goes from 9 to **24 of 25**; `Config535` stays out on
`DetectIncompatibility`, with its torch message rather than a "coming soon" note.

No gallery code changes. That was the point of the gate design, and this slice is its first
real test.

## 6. Error handling

| Situation | Behaviour |
|---|---|
| `VramProfiles` is junk (`"abc"`, `",,"`) | Parses to no tiers: no dropdown, tier 0, nothing filtered. Never throws. |
| Model destination cannot be resolved | Row renders without a presence marker; the install still attempts it, as the pipeline owns the real resolution. |
| Scanning hits an unreadable folder | That file counts as absent. No dialog, no crash. |
| Size lookups fail | Partial estimate, named in `UnknownSizeModels`; install proceeds. |
| Verification throws | Warning line; install proceeds without redownload decisions — a file the user was never asked about is left alone. |
| Mismatch dialog dismissed | Install does not start; the wizard stays on Confirm with a log line saying so. |
| Cancel while the dialog is open | `RunToken` releases it; treated as dismissal. |

## 7. Testing

Unit, in `DiffusionNexus.Installer.Tests`:

- Tier parsing: `"8,12,16,24,32"`, `"24,32"`, `"24+"`, whitespace, duplicates, junk, empty.
  Lowest preselected. Selection round-trips into `WizardSelection` and the draft.
- `Detect`-versus-`AppliesTo` agreement across every catalog workload, which is what the shared
  parser and the `Count > 0` choice above exist to satisfy.
- `AppliesTo` for all three modules, including the no-tiers-with-models case
  (`upscaling-z-image-turbo`) and the models-with-tiers case.
- Exclusions: unticking contributes exactly those ids; ticking everything contributes an empty
  set; no module contributes 0 to `SelectedVramProfile` for a workload without tiers.
- **Scanner-versus-pipeline agreement**: for every catalog workload and every tier it declares,
  the scanner's chosen links equal `VramProfileHelper.SelectBestMatchingLinks` on the same
  input. This is the test that stops the display and the install from drifting apart.
- Presence: temp directories covering exact hit, nested hit, missing part of a multi-link
  model, unreadable directory.
- `InstallLauncher`: no mismatches starts the session; mismatches prompt and apply the sets to
  the module before `StartAsync`; dismissal starts nothing and returns false; a throwing
  verifier still starts.
- Per-run lifetime: two consecutive `BuildPlanAsync` calls for different workloads yield
  different module instances, and the second plan carries none of the first's answers.

bUnit:

- The Content stage renders the applicable panels in Order.
- Changing the tier re-renders the model rows.
- **Every editable control in all three new panels raises `Changed`** — plus the guard parked
  as Ruling 31: `Install.razor` wires `Changed` for each panel, and the callback is created
  before the reconnect early-return. Deleting any of those wirings must fail a test, because
  losing them is what made the wizard uncompletable in slice 1.

Real catalog:

- `RealCatalogInstallabilityTests` updates 9 → 24, with `Config535` named as the exclusion and
  its reason asserted. While the file is open, set `InstalledCatalogPath` to a temp path — it
  currently enumerates and deletes `catalog.staging-*` directories under the real
  `%LocalAppData%` despite a comment claiming it touches nothing outside the repo.

Manual smoke, added to `docs/manual-smoke.md`:

- `wan-2-2-gguf` at tier 8 — heaviest case at 10 models and 26 links. Confirm the dropdown
  offers exactly `8,12,16,24,32`, files land in the right folders, and the report has no
  unexplained skips.
- `ideogram-4-0` — dropdown offers exactly 24 and 32, preselected 24.
- `upscaling-z-image-turbo` — no dropdown, and all four models download.
- Re-run one install over an existing folder with a deliberately truncated model file, to see
  the mismatch dialog and both of its answers.

## 8. Follow-ups this slice deliberately leaves open

1. Settings write-back, so the install folder and folder overrides are actually remembered.
2. `Cancel()`'s remaining race (Ruling 32): `_cts` is assigned in a second lock acquisition, so
   a cancel between the two still reads null. Unreachable through the UI today.
3. The two `VramProfileHelper` classes should converge; this slice only documents which is
   authoritative for link selection.
4. `Config535`'s torch/CUDA pairing in the catalog repo.

## 9. Open items

None. Every question this design raised was answered in the 2026-09-02 session.
