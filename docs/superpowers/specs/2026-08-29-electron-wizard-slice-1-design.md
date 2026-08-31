# Electron Installer 3.x — Catalog-Driven Wizard (Slice 1)

Date: 2026-08-29
Repo: `Into-The-Latent/DiffusionNexus.Installer`
Status: design approved, plan pending

## 1. Context

The 3.x Electron + Blazor installer exists as a shell only: `App.razor`, `MainLayout`,
`ReconnectModal`, three placeholder pages, `Program.cs`, `UpdaterLog`. Auto-update is proven
end to end (v3.0.1 → v3.0.4, disk-verified swap). No installer functionality has been ported.

Meanwhile the content side is finished: SDK 2.x ships the JSON `Catalog` package, the catalog
repo is live with a `v1` stable release and a `preview` channel, and the WPF Catalog Editor
(Tools repo, PR #1 merged) authors and publishes it. The gap is the consumer.

The 2.x Avalonia installer offers two flows from a landing page: **Classic**
(`InstallationView.axaml` 1,706 lines + `InstallationViewModel.cs` 4,329 LOC — everything on one
page) and **Wizard** (8 steps, ~2,554 LOC C#). This design replaces both.

## 2. Decisions locked

1. **Wizard only.** The classic page is not ported. Its genuinely-needed capabilities — per-model
   download selection, model base folders, mismatched-model handling, shortcut-conflict
   resolution, install report — fold into the wizard as capability modules. The landing-page mode
   picker disappears.
   *Rationale:* classic is ~6,000 lines and a third of the port, and its reason for existing has
   eroded — the "full control" knobs (interpreter override, Paths fields, torch/CUDA, VRAM
   authoring) are now authored by the maintainer in the Catalog Editor, not chosen by end users.
   Two flows also means every future feature is built twice.

2. **First slice = thin installs.** The five non-ComfyUI workloads (A1111, Forge, Fooocus,
   ACE-Step, AI-Toolkit) plus Blank ComfyUI. Six of the 21 installer-targeted workloads.
   *Rationale:* they share one install shape — clone → python → venv → torch → requirements →
   launcher → shortcut — with no model downloads, no workflows, no VRAM profiles. They prove the
   whole Blazor↔SDK↔pipeline wiring with the fewest moving parts, and Blank ComfyUI puts the
   slice on the critical path from day one (ComfyUI is 16 of the 21).

3. **Catalog-driven navigation, not enum-driven.** The 2.x wizard keys its first three steps off
   the hardcoded `RepositoryType` enum and `WorkloadChoice { Image, Video, Blank }`. That enum is
   already wrong: ACE-Step is `workflowType: "Audio"` in the catalog as of SDK 2.0.0-preview.3 and
   the wizard cannot express it. Navigation derives from catalog data instead.

4. **Modular by stage, because of ComfyUI.** ComfyUI workloads vary enormously (0–9 custom-node
   repos, 0–5 model downloads, VRAM profiles on 13 of 25, Triton/SageAttention, VC++ runtime,
   custom model folder, custom output folder). The wizard composes itself from capability modules
   that a workload's own catalog data switches on.

## 3. Scope

### In scope (slice 1)

- Catalog acquisition and read, with the embedded seed archive.
- Workload gallery: card grid over installer-targeted workloads, thumbnail + name + markdown
  description, filter by `workflowType` (Image/Video/Audio) and by software.
- Installability gate — a workload is offered only when every module it requires exists.
- Stages: Location, System, Confirm, Install.
- Modules: `InstallFolder`, `ComfyFolders`, `GpuPreflight`, `Shortcuts`.
- Install session that survives Blazor circuit reconnects, with live log, cancel, and report.
- Folder-picker and prompt services backed by Electron dialogs.
- A test project (the repo currently has none).

### Out of scope (slice 1)

- Modules for VRAM, model selection, custom-node selection, workflow selection, accelerators.
  These are slice 2 and are what unlock the remaining 15 ComfyUI workloads.
- Catalog update-check modal (section-level apply). Specced separately; not needed to prove installs.
- Code signing / SmartScreen work.
- The open shortcut-launch bug (launching the packaged Electron exe directly exits instantly;
  only the .NET entry point under `resources/bin` works). Slice 1 is an internal milestone run
  from a dev build, so this is not on its critical path. **It must be fixed before any 3.x public
  release**, because the Start Menu shortcut targets that exe.
- Third-party notices for the ~295 npm packages and the Chromium/ffmpeg LGPL layer.

## 4. Architecture

### 4.1 Projects

| Project | TFM | Contents |
|---|---|---|
| `DiffusionNexus.Installer.Electron` | net10.0 | existing ElectronNET + Blazor host; Razor components and Electron-specific services only |
| `DiffusionNexus.Installer.Core` (new) | net10.0 | wizard state machine, module contract and implementations' logic, catalog queries, install session. No Blazor, no Electron — headless and testable |
| `DiffusionNexus.Installer.Tests` (new) | net10.0 | xUnit + FluentAssertions + Moq, matching SDK test conventions |

Blazor Server inside Electron is a single process, C# end to end. The SDK is referenced directly.
The sidecar + JSON-RPC bridge from the original migration plan is superseded and stays dead.

### 4.2 SDK seam

The Electron csproj currently declares **no** SDK `PackageReference` at all, and
`Directory.Build.targets` lists ProjectReferences for five SDK projects — two of which
(`DataAccess`, `Database`) were deleted in SDK 2.0. Those dead entries are silently skipped on
restore, so the repo builds today in both modes; this is a tidy-up, not a blocker.

Required change: reference **Models, Shared, Services, Catalog** — as `PackageReference` in the
csproj (for CI) and in the `Directory.Build.targets` remove/add lists and
`DiffusionNexus.Installer.LocalSDK.slnx` (for local dev). Delete the `DataAccess` and `Database`
entries.

SDK 2.x is published only as `2.0.0-preview.1/2/3`; `v2.0.0` is not tagged. Slice 1 consumes
`2.0.0-preview.3` (the first with the Audio workflow type). CI must have `PACKAGES_READ_TOKEN`,
since the SDK lives under a different GitHub account; a local `-p:UseLocalSDK=false` restore will
403 with a gh CLI token lacking `read:packages`, so **CI is the package-completeness gate**.

### 4.3 Catalog acquisition

Register the SDK's own service: `AddDiffusionNexusCatalog(...)`, configuring `CatalogOptions`:

- `EmbeddedArchive` / `EmbeddedManifest` → the `catalog.zip` and `manifest.json` shipped as
  embedded resources in the Electron project, used to seed `%LocalAppData%\DiffusionNexus\catalog`
  on a machine that has none.
- `LocalOverridePath` → wired to a developer setting so a local catalog checkout can be tested
  before publishing. A missing override warns and falls back; it must never crash (the
  `db_override.txt` lesson from SDK 1.2.30).
- `Channel` → Stable by default, Preview selectable in settings.

Reads go through `ICatalog`. Use the async members (`GetWorkloadsAsync`,
`GetReleaseWorkloadsAsync`, `GetWorkloadAsync`, `ReadThumbnailAsync`) — never the blocking
`Source`/`State`/`Diagnostics` properties on a render path, because the first access can perform
the seed on the calling thread.

The gallery lists workloads where `WorkloadTarget == WorkloadTargetType.Installer`.
`DiffusionNexusCore` workloads belong to the main app and are never offered here.

### 4.4 Wizard model: stages and modules

A **module** is one capability: a Core class holding its logic and state, plus a Razor component
rendering its panel. A **stage** is a screen that renders every applicable module in order. A
stage with no applicable modules is skipped entirely.

| Stage | Modules | Slice |
|---|---|---|
| Gallery | (not a module — workload selection) | 1 |
| Location | `InstallFolder` · `ComfyFolders` | 1 |
| Content | `VramProfile` → `ModelSelection` → `CustomNodes` → `WorkflowSelection` | 2 |
| System | `GpuPreflight` · `Accelerators` · `Shortcuts` | 1 (GpuPreflight, Shortcuts) |
| Confirm | summary of every module's contribution | 1 |
| Install | live log, cancel, skip-download, report | 1 |

Slice 1 renders four screens after the gallery. A fully loaded pack like `krea-2-turbo` renders
five. **Wizard length stays flat as workload complexity grows** — that is the property this
structure buys, and the reason modules are not one-per-page.

### 4.5 Module contract

```csharp
public interface IWizardModule
{
    string Id { get; }
    WizardStage Stage { get; }
    int Order { get; }

    /// Reads the selected catalog workload. Never an enum switch.
    bool AppliesTo(WizardSelection selection);

    Task InitializeAsync(WizardSelection selection, CancellationToken ct);

    /// Folds this module's answers into the draft. Modules never see each other.
    void Contribute(InstallationOptionsDraft draft);

    ModuleValidation Validate();
}
```

`WizardSelection` is the accumulated state: the chosen `InstallationConfiguration`, the target
folder, and each module's answers keyed by module id. `InstallationOptionsDraft` is a mutable
builder converted once, at Confirm, into the SDK's init-only `InstallationOptions` record.

**Ordering with decoupling.** VRAM→Models is a real dependency: `SelectedVramProfile` filters
which models download. Modules still never reference each other — a downstream module reads the
value it needs from `WizardSelection` and is sequenced by `Order`. `ModelSelection` reads "the
selected VRAM tier", not `VramModule`.

**Module ≠ pipeline step.** A module is a UI panel that contributes to `InstallationOptions`. The
pipeline always installs whatever the catalog declares; a module only ever *narrows* it
(`ExcludedModelIds`, `ExcludedNodeIds`, `ExcludedWorkflowIds`) or *configures* it. Blank ComfyUI
therefore still clones ComfyUI-Manager in slice 1 with no `CustomNodes` UI present, and the
picker arrives in slice 2 without touching the pipeline.

### 4.6 Module inventory and applicability

| Module | `AppliesTo` | Contributes | Slice |
|---|---|---|---|
| `InstallFolder` | always | target path (+ remembered default from user settings) | 1 |
| `ComfyFolders` | `Repository.Type` is ComfyUI (model folder + output folder) or AIToolkit (model folder only) | `ModelBaseFolder`, `OutputFolder`, `GenerateExtraModelPaths`, `OverwriteExtraModelPaths`, `FolderPathOverrides`, `AdditionalFolders` | 1 |
| `GpuPreflight` | GPU detection reports no compatible NVIDIA GPU | `CpuTorch` (ComfyUI only); otherwise blocks with `NoCompatibleGpu` | 1 |
| `Shortcuts` | always | `CreateDesktopShortcut`, `CreateStartMenuShortcut`, `DesktopShortcutName`, `StartMenuShortcutName`, `OnShortcutConflict` | 1 |
| `VramProfile` | `vram.vramProfiles` non-empty (13 of 25 workloads; all ComfyUI) | `SelectedVramProfile` | 2 |
| `ModelSelection` | `modelDownloads.Count > 0` | `ExcludedModelIds`, `ForceRedownloadUrls`, `TrustedUrls` | 2 |
| `CustomNodes` | `gitRepositories.Count > 0` | `ExcludedNodeIds` | 2 |
| `WorkflowSelection` | `Workflows.Count > 0` on the resolved `InstallationConfiguration` | `ExcludedWorkflowIds` | 2 |
| `Accelerators` | `installTriton` or `installSageAttention` | `SkipVcRuntimeProvisioning` | 2 |

`ComfyFolders` is in slice 1 specifically because Blank ComfyUI and AI-Toolkit are: a blank
ComfyUI install is exactly the case where a user points `extra_model_paths.yaml` at an existing
model library, and the output folder lands in the generated launcher script via
`CreateComfyUILauncherScript(appName, OutputFolder, CpuTorch)`.

### 4.7 Installability gate

Asking "which modules does this workload require?" cannot be answered by calling `AppliesTo` on
modules that may not be registered — that is circular. So capability detection is a separate,
module-independent read of the workload:

```csharp
[Flags]
public enum WorkloadCapability
{
    None            = 0,
    ComfyFolders    = 1 << 0,   // Repository.Type is ComfyUI or AIToolkit
    VramProfile     = 1 << 1,   // vram.vramProfiles non-empty
    ModelDownloads  = 1 << 2,   // ModelDownloads.Count > 0
    CustomNodes     = 1 << 3,   // GitRepositories.Count > 0
    Workflows       = 1 << 4,   // Workflows.Count > 0
    Accelerators    = 1 << 5,   // installTriton or installSageAttention
}

// Pure function of catalog data. No module involvement.
WorkloadCapability Detect(InstallationConfiguration workload);
```

Each module declares the single capability it satisfies (`InstallFolder` and `Shortcuts` declare
`None` — they are unconditional). A workload is installable when every capability `Detect`
reports is satisfied by a registered module:

```csharp
bool IsInstallable(InstallationConfiguration w) =>
    Detect(w) == (Detect(w) & _registry.SatisfiedCapabilities);
```

`AppliesTo` on a registered module remains the runtime authority for whether its panel renders;
`Detect` is only the gate. The two must agree, which is a test.

Non-installable workloads render disabled with a "coming soon" affordance rather than being
hidden, so the catalog's real breadth stays visible. The gate is self-maintaining: as slice 2
registers modules, workloads become installable with no gallery change. This is what stops a user
in slice 1 from picking `krea-2-turbo` and getting every model downloaded at no VRAM tier.

### 4.8 Install session lifecycle

**The hazard.** In Avalonia the ViewModel owned the running install. In Blazor Server a component
lives on a SignalR circuit; a reconnect or navigation disposes it. `ReconnectModal.razor` already
ships in the shell, so reconnects are expected, and a multi-hour model download must not die with
the circuit.

**The rule.** `IInstallSession` is a **singleton**. It owns the `CancellationTokenSource`, the
bounded log ring buffer, current step/progress state, and the final `InstallationResult.Report`.
Components subscribe and render from it; they never own it. After a reconnect the Install stage
re-renders from the session's current state and the install never notices.

Consequences:

- Log lines flood (pip output). The session buffers and flushes on a ~100 ms timer through
  `InvokeAsync(StateHasChanged)`, into a bounded ring buffer, rather than raising per line.
- Cancel and per-file skip-download are session methods, mirroring the 2.x
  `InstallProgressStep`'s `_cts` and `_skipDownloadCts`.
- Exactly one install may run at a time; the session refuses a second start.
- `OnShortcutConflict` is a `Func<string, string, Task<ShortcutConflictResult>>` the session
  fulfils by surfacing a modal and awaiting the answer — so the pipeline blocks on a UI decision
  without the session knowing about Blazor.

### 4.9 UI services

The SDK calls back into the host for interactive decisions. Slice 1 implements:

- Folder picker → Electron's native dialog API (`Electron.Dialog.ShowOpenDialogAsync`).
- Confirm/prompt → a Blazor modal awaited through a `TaskCompletionSource`.
- Shortcut conflict → the same modal mechanism, wired to `OnShortcutConflict`.

These live in the Electron project (they are host-specific); Core depends only on their interfaces.

## 5. Data flow

```
ICatalog ──GetWorkloadsAsync──► Gallery (filter: Installer target, installable)
                                    │  user picks one
                                    ▼
                            WizardSelection { Workload }
                                    │
        ┌───────────────────────────┴───────────────────────────┐
        │  For each stage: modules where AppliesTo(selection)    │
        │  render, InitializeAsync, collect answers into         │
        │  WizardSelection                                       │
        └───────────────────────────┬───────────────────────────┘
                                    ▼
                    Confirm: Contribute() each module → InstallationOptionsDraft
                                    │
                                    ▼
                          InstallationOptions (SDK record)
                                    │
                                    ▼
        IInstallSession ──► InstallationPipeline ──► IInstallationFlow (per RepositoryType)
                                    │                         └─► IInstallationStepHandler xN
                                    ▼
                    progress + log + InstallationStepResult stream
                                    ▼
                        Install stage renders from session state
```

## 6. Error handling

- **Catalog unavailable or corrupt.** Seed from the embedded archive; a missing or unreadable
  local override warns and falls back. The gallery must render something, or state plainly why
  it cannot — it must never crash on startup.
- **Step failure.** `InstallationStepResult` carries `IsSuccess`, `Message`, and `ShouldContinue`.
  The session records every result and renders the report rows; a failure with
  `ShouldContinue == false` halts the pipeline and the Install stage shows the failure with the
  log tail, preserving the 2.x failure-report behaviour.
- **Cancellation.** Cancel triggers the session CTS. The SDK threads the caller's token through
  every awaited HTTP call, which is the invariant that makes the infinite-timeout `HttpClient`
  safe. Any new consumer resolving that singleton must uphold it.
- **Circuit drop mid-install.** Nothing happens to the install. See §4.8.
- **No compatible GPU.** `GpuPreflight` offers CPU-only for ComfyUI (`CpuTorch`) and blocks other
  workloads with `NoCompatibleGpu`, matching SDK 1.2.33 behaviour.

## 7. Testing

TDD throughout. Core is UI-free, so the interesting logic is directly testable:

- `AppliesTo` for every module against real catalog fixtures — in particular, that the six
  slice-1 workloads light up exactly `InstallFolder`, `ComfyFolders` (ComfyUI/AI-Toolkit only),
  `GpuPreflight` (when applicable) and `Shortcuts`, and nothing else.
- Stage composition: empty stages are skipped; module order is stable.
- `Contribute` → the resulting `InstallationOptions` matches expectations per workload.
- The installability gate: a `krea-2-turbo`-shaped workload is not installable in slice 1 and
  becomes installable once slice-2 modules register.
- `Detect` and `AppliesTo` agree: for every catalog workload, each capability `Detect` reports has
  exactly one registered module whose `AppliesTo` returns true, and vice versa. This is the test
  that stops the gate and the runtime from drifting apart as modules are added.
- Install session: single-run enforcement, log ring-buffer bounds, cancellation, state survives
  subscriber churn (the circuit-drop analogue).

Component tests with bUnit only where a component holds real logic. Manual smoke is required for
the Electron-native pieces (folder dialog, window behaviour) and is tracked in a
`docs/manual-smoke.md`, following the Catalog Editor's precedent.

## 8. Deferred to slice 2 and beyond

- The `Content` stage and its four modules, plus `Accelerators` — unlocks the remaining 15
  ComfyUI workloads.
- Catalog update-check modal with section-level apply and `.bak` rollback.
- Shortcut-launch bug, code signing, npm/Chromium third-party notices — all required before a
  public 3.x release.
- The pre-migration SDK refactor noted in the migration plan: four duplicated folder-key tables,
  two hand-synced model-scan copies, wizard duplicating the install lifecycle.

## 9. Open items

1. Whether slice 1 ends as an internal dev milestone (assumed here) or a published 3.1.0. If the
   latter, the shortcut-launch bug moves into scope and becomes the highest-risk task.
2. Whether the gallery's "coming soon" affordance should name the missing capability
   ("needs model selection") or stay generic.
