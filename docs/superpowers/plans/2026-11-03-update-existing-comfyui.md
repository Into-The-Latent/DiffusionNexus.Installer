# Update existing ComfyUI and Select multiple: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (the owner chose native execution). Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver SDK #70 and Installer #28: the update path, the Update only tile, and Select multiple with the memory selector and card dropdowns on both new paths.

**Architecture:** The SDK gets an inspector, a torch target resolver, a merger split in two, an unattended update step and an extend flow. The installer gets a third `WizardMode`, a workload screen with Select multiple, and modules that read one `ExistingRun` state on the selection. The stage list is re-evaluated on Next on the update path, so the System stage can follow the torch decision.

**Tech Stack:** .NET 10, Blazor Server in Electron, xunit, FluentAssertions, bUnit, Moq.

**Spec:** `docs/superpowers/specs/2026-11-03-update-or-download-models-design.md`. It wins over the older plans inside issues #28 and #70, which still apply where it doesn't disagree.

## Section 6 settled: when the merge happens

Facts from the code: `WizardSelection.Workload` is `required init`. `BuildPlanAsync` fixes the stage list once. `VcRuntimeModule.AppliesTo` reads the workload's Triton and SageAttention flags. `ModelSelectionModule` builds its rows once, in `InitializeAsync`, but re-reads `_selection.Workload` for every scan.

The merge splits along what the folder decides:

1. **Before the wizard, no folder needed:** `ExtendConfigurationMerger.Union(choices)` merges nodes, models and workflows. Each workload's links are narrowed here by its own version. Rows and ids never change after this.
2. **After Location, folder known:** `ExtendConfigurationMerger.ForExistingInstall(union, profile, target, upgradeTorch, managerRepository)` returns a new configuration with the same model, workflow and node objects, plus the Manager repository, torch and Python values. `ExistingRun` writes it to `selection.Workload`, which becomes `required ... { get; set; }`. It is recomputed when the profile, the Update checkbox or the GPU answer changes.
3. **The stage list follows:** `WizardPlan.RefreshStages()` re-reads `AppliesTo` for the modules it already holds. `WizardRun.TryNext` calls it on the update path only and finds the next stage by enum value, so a System stage that vanished can't shift the index. Fresh installs and models only keep today's fixed list.

Because `ForExistingInstall` sets torch from `upgradeTorch`, not the raw target, `VcRuntimeModule`, the pipeline's compatibility check and the flow all follow the same decision without knowing about it.

**Cost if wrong:** `Workload` being settable means a second writer could appear. `ExistingRun` is the only one, and a test pins that rows survive a rewrite.

## Global Constraints

- SDK stays at version `2.4.0`, because that version was never tagged. **No tags.** The installer's pins stay `2.4.0`, built with `-p:UseLocalSDK=true`. Cost if wrong: one line.
- One PR per repo: SDK base `develop`, installer base `main`. Never merge the second half without the owner's go.
- New interface members in the SDK get a default implementation.
- `InstallationStep.ComfyUIUpdate` is appended to the enum, never inserted.
- Fresh installs don't change. Models only single-workload doesn't change.
- Cards never grey out for memory reasons. No VRAM detection. No undo script.
- Torch only ever changes with the Update checkbox ticked **and** a usable NVIDIA GPU found. Unknown GPU counts as usable: an unsure probe never blocks (`GpuPreflightModule`'s rule).
- GUI tests on the BenQ only. Bash truncates long commands: put edits in scripts written with the Write tool.
- Wording comes from the mockups in the design's `assets/.../mockups/`, verbatim where it exists.

## Review Focus

1. A venv not named `venv` (`.venv`, `env`): the update script's `pip install` must use that venv, never system Python. Test in Task 5.
2. A ComfyUI with no Manager, updated in the run that installs Manager: the code must land on stable, or the Manager later puts older code on a migrated database. Test in Task 5.
3. Back to Location and a different ComfyUI folder: profile, target, torch note and merged configuration all follow, and model rows and ticks stay. Test in Task 12.
4. An unreachable or slow folder: inspecting never freezes the window, Next stays off until the answer, and an answer about a folder the user has left is never used. Test in Task 12.
5. The update script can't update (not a git checkout, fetch failed): the report says the code wasn't updated, in a warning row, and the rest of the run carries on. Test in Task 6.

---

## SDK: `E:\Repos\DiffusionNexus.Installer.SDK`, branch `feature/70-existing-comfyui-update`

New types live in `DiffusionNexus.Installer.SDK.Services/Installation/Existing/`, namespace `DiffusionNexus.Installer.SDK.Services.Installation.Existing`. Tests in `DiffusionNexus.Installer.SDK.Tests/Installation/Existing/`. The public records are those in #70's "Public contract" section, except where a task below changes them.

### Task 1: Small parsers

**Files:** create `PyvenvCfgReader.cs`, `RunScriptOutputFolderParser.cs`, `ExtraModelPathsBasePathReader.cs`, `UpdateScriptStateDetector.cs`, `ManagerConfigReader.cs`, one test class each.

**Produces:** `PyvenvCfgReader.FindVenv(string repositoryPath, string preferredName = "venv") -> string?`, `ReadPythonVersion(string venvPath) -> string?`; `RunScriptOutputFolderParser.Parse(string text) -> string?`; `ExtraModelPathsBasePathReader.Read(string yaml) -> string?`; `UpdateScriptStateDetector.Detect(string repositoryPath) -> UpdateScriptState`; `ManagerConfigReader.Read(string repositoryPath) -> (bool Installed, string? Policy)`. Behaviour per #70 task 1.

- [ ] Write tests: quoted path with spaces, bare path, missing flag; yaml with comments and a second `a111:` block; marker on line 2 against an old script; a commented-out policy line gives null; venv named `.venv` found, `venv` preferred over another, a folder without `pyvenv.cfg` ignored.
- [ ] Run, watch them fail, implement, run. Expected: green.
- [ ] Commit `feat(existing): parsers for an installed ComfyUI (#70)`.

### Task 2: Inspector

**Files:** create `ExistingComfyUIInspector.cs`, `IExistingComfyUIInspector.cs`, the records; register in `InstallationServiceCollectionExtensions.cs` as `AddTransient`.

**Produces:** `IExistingComfyUIInspector.InspectAsync(string folder, CancellationToken) -> Task<ExistingComfyUIProfile>`, ctor `(IPythonService, IProcessRunner)`. Verdict order per #70 task 2. `TorchInstallStepHandler.ParseVerifyOutput` is reachable through `InternalsVisibleTo` or by moving the record, not copied.

- [ ] Write tests with temp folders: each verdict; canned stdout from a fake `IProcessRunner` gives the stack; a CUDA of `None` gives a null `CudaVersion`; non-zero exit gives `Torch = null` and no throw; a run script with a spaced quoted output path; a folder that doesn't exist gives `NotComfyUI`.
- [ ] Red, implement, green. Commit `feat(existing): inspect an installed ComfyUI (#70)`.

### Task 3: Target resolver

**Files:** create `ExtendTargetResolver.cs`, `ExtendTorchTarget.cs`.

**Interfaces change from #70:** `ExtendTorchTarget` adds `IReadOnlyList<ExtendPackTorchNeed> PacksNeedingNewer`, with `record ExtendPackTorchNeed(string PackName, string TorchVersion, string? CudaVersion)`: packs whose effective torch or CUDA is above the installed stack, for the design's "X needs torch Y" note. Empty when the stack is unknown.

- [ ] Tests from #70 task 3, plus: installed 2.8 with packs at 2.11 and 2.8 gives one need; `PacksNeedingNewer` is filled even when the target would change; empty packs give the installed stack and no change.
- [ ] Red, implement, green. Commit `feat(existing): one torch target across workloads (#70)`.

### Task 4: Merger, split in two

**Files:** create `ExtendConfigurationMerger.cs`, `WorkloadChoice.cs`, `ExtendUnion.cs`.

**Produces:**
- `record WorkloadChoice(InstallationConfiguration Workload, int VramGb)`. `VramGb` 0 means no narrowing.
- `record ExtendUnion(InstallationConfiguration Configuration, IReadOnlyDictionary<Guid,string> PackNameByModelId, IReadOnlyDictionary<Guid,string> PackNameByNodeId)`.
- `ExtendConfigurationMerger.Union(IReadOnlyList<WorkloadChoice> choices, string? name = null) -> ExtendUnion`.
- `ExtendConfigurationMerger.ForExistingInstall(ExtendUnion union, ExistingComfyUIProfile profile, ExtendTorchTarget target, bool upgradeTorch, GitRepository? managerRepository) -> InstallationConfiguration`.
- `ExtendConfigurationMerger.HasManagerRepository(InstallationConfiguration)`, for the update step.

**Union rules.** Never mutates the catalog's objects. Each model is copied with `DownloadLinks` narrowed by `VramProfileHelper.SelectBestMatchingLinks(enabled, choice.VramGb, null, name)`. A model with no enabled links keeps its URL only if `VramProfileFitsSelection` passes at that version. A model left with nothing is dropped, because that is what that version means. Models dedupe by their set of chosen URLs (normalized by `DownloadUrlNormalizer`), then by destination plus file name; the first pack keeps it. `GitRepositories` dedupe per #70 task 4. `Workflows` concatenate, distinct by id. `SelectedLamaCppWheelId` is the first non-null. **With one choice at 0, `Id`, `Name`, `Vram` and `Paths` are the workload's own, so a single workload behaves as today.** Otherwise `Id` is new, `Vram` is empty so `VramProfileModule` stays out, and `Name` is the `name` argument. `Repository`, `Python`, `Torch`, `Paths` come from the first choice. Zero choices give a minimal ComfyUI configuration, using `name`.

**ForExistingInstall rules.** Shares the union's model and workflow list objects. Appends `managerRepository` when `!profile.ManagerInstalled` and it isn't already there. `Python.VirtualEnvironmentName = Path.GetFileName(profile.VenvPath)`, `Python.PythonVersion = profile.PythonVersion`. **With `upgradeTorch`:** `Torch = target.Torch`, flags from the target. **Without:** `Torch` is the installed stack, or an empty `TorchSettings`, and both flags false, so nothing downstream can schedule torch.

- [ ] Tests: 16 GB narrows links and leaves the catalog object unchanged; two versions in one union; same node twice keeps the lower priority; same URL twice attributed to the first pack; different URLs with the same destination and name keep the first; the same file name in different destinations stays two entries; one choice at 0 keeps id and `Vram`; two choices clear `Vram`; a model above its version is dropped; Manager appended only when missing; `.venv` survives; `upgradeTorch` false gives installed torch and flags false with the raw target at a higher version; model list is the same object after `ForExistingInstall`.
- [ ] Red, implement, green. Commit `feat(existing): merge workloads before and after the folder is known (#70)`.

### Task 5: The update scripts run unattended

**Files:** `BatchScriptGenerator.cs`, `ShellScriptGenerator.cs`, their tests.

Three environment variables, none changing the script when unset:
- `DN_UNATTENDED`: no pause at the end. Exit `1` when the update failed (`SWITCH_FAILED`, install not found, database in use), exit `2` when the code was left alone (not a git checkout, fetch failed), otherwise `0`.
- `DN_PYTHON_EXEC`: used for `pip install -r requirements.txt` instead of the `venv` / portable / system guess. **Without it a `.venv` install would pip-install into system Python.**
- `DN_ASSUME_MANAGER`: counts as having a Manager. **Without it, a run that installs Manager would pull master first, and the Manager's next stable update would put older code on a migrated database.**

Marker and git logic don't change. A failed path that sets nothing today sets `SWITCH_FAILED`.

- [ ] Extend the generator tests: each variable's guard is in the script, batch and shell; the error paths reach `exit /b 1`; the fetch failure and non-git paths reach `exit /b 2`; the cmd `( )`-in-echo guard test still passes. Add a real `cmd.exe` run in a temp folder with no git, `[Fact]` Windows only, that finishes without a key press and exits 1.
- [ ] Red, implement, green. Commit `feat(existing): the update script can run unattended (#70)`.

### Task 6: `ComfyUIUpdateStepHandler`

**Files:** create `Steps/Existing/ComfyUIUpdateStepHandler.cs`; append `InstallationStep.ComfyUIUpdate`; add its description in `InstallationPipeline.GetStepDescription` and `GetStepDisplayName`; register in DI.

Ctor `(IProcessRunner)`. `ShouldExecute`: `Options.Extend != null`. Writes the script through `LauncherScriptFactory`, only when `UpdateScriptStateDetector` says `None` or `Legacy`. If `RunUpdate`: runs it from the repository folder with `DN_UNATTENDED=1`, `DN_PYTHON_EXEC=<venv python>`, and `DN_ASSUME_MANAGER=1` when `HasManagerRepository(Configuration)`. Streams output to the log with ANSI colour codes removed. Exit 1: `Failure("ComfyUI was not updated. See the log for why.")`, which stops the run. Exit 2: a warning row "ComfyUI code was not updated", and the run continues. Exit 0: success with the channel line the script printed.

- [ ] Tests with a fake runner: legacy replaced, current left alone; `RunUpdate` false writes and never runs; environment variables passed; exit 1 fails; exit 2 is a warning row and success; ANSI stripped; cancellation passes through.
- [ ] Red, implement, green. Commit `feat(existing): run the ComfyUI update as a step (#70)`.

### Task 7: Extend flow and pipeline

**Files:** create `Flows/ExtendExistingInstallationFlow.cs`; `InstallationFlowFactory.cs` (`CreateExtendExistingFlow()` with a default implementation); `InstallationPipeline.cs`; `InstallationOrchestrator.cs`; `IInstallationOrchestrator.cs` (`ExtendExistingOptions? Extend`); `ValidateInstallationStepHandler.cs`; `PostInstallStepHandler.cs`.

**Interfaces change from #70:** `record ExtendExistingOptions(string VenvPath, bool RunUpdate, bool UpgradeTorchStack, bool RewriteLauncher = false)`.

Steps: `Update`, `ComfyUIUpdate`; with `UpgradeTorchStack`: `VcRuntimeSetup` (Windows, Triton or Sage on), `InstallTorch`, `InstallTriton`, `InstallSageAttention` by flag; `InstallLlamaCpp` when `SelectedLamaCppWheelId` has a value; `CloneAdditionalRepositories`, `DownloadModels`, `ExportWorkflows` when non-empty; `PostInstall`.

Orchestrator: with `Extend`, the target is used as given and `RepositoryPath = target`. Pipeline picks the flow first; the torch check runs only with `UpgradeTorchStack`. Validate handler also runs with `Extend`, sets `VenvPath`, fails on portable. **`PostInstallStepHandler` with `Extend`: writes the launcher only with `RewriteLauncher`, because the design says a working AMD or CPU launcher stays; never writes the update script (Task 6 did); shortcuts as options say.** `RunName` says "update" with `Extend`.

- [ ] Tests: step list for update on or off by torch on or off; llama step; pipeline picks the flow; the folder name isn't normalized; validate sets `VenvPath` and refuses portable; torch check skipped without upgrade and blocking with it; launcher untouched by default and rewritten with `RewriteLauncher`; `extra_model_paths.yaml` only when asked; a whole run with real step handlers over stubs writes nothing else.
- [ ] Red, implement, green. Commit `feat(existing): flow that extends an installed ComfyUI (#70)`.

### Task 8: Name clashes, settings, notes

**Files:** `ModelDownloadStepHandler.cs`; `InstallationContext.cs`; `UserSettings.cs`; `CLAUDE.md`.

In one run, a file the run itself wrote from another URL, then found again at that path, gets a warning row: `Skipped: another model in this run has the same file name, so this one was not downloaded (<path>)`. Design section 5's decision. `UserSettings.LastExistingComfyUIFolder`. CLAUDE.md: the 2.4.0 paragraph gains the `Existing` namespace, the split merger and the script variables.

- [ ] Tests: flat run with two URLs and one name: first downloaded, second is the warning row, one file on disk; a settings JSON round trip.
- [ ] Red, implement, green. Run the whole suite. Open the SDK PR into `develop`. Commit `feat(existing): report a name clash inside one run (#70)`.

---

## Installer: `E:\Repos\DiffusionNexus.Installer`, branch `feature/28-update-existing-comfyui`

Build and test with `dotnet test DiffusionNexus.Installer.slnx -p:UseLocalSDK=true`.

### Task 9: Mode, picks, versions, stage refresh

**Files:** `Core/Wizard/WizardMode.cs` (`UpdateExisting`); `WizardRoutes.cs`; `WizardSelection.cs`; `WizardPlan.cs`; `WizardRun.cs`; `WizardModuleRegistry.cs`; create `Core/Wizard/WorkloadVersions.cs`, `Core/Wizard/WorkloadPicks.cs`, `Core/Existing/ExistingRun.cs`.

**Produces:**
- `WizardRoutes.Install(Guid, WizardMode)` as a switch with no discard arm; `WizardRoutes.Workloads(mode)`; `WizardRoutes.Selected(mode)` (`/existing/models/selected`, `/existing/update/selected`); `WizardRoutes.UpdateOnly` (`/existing/update/only`); `WizardRoutes.For(WizardSelection)`.
- `WorkloadVersions.Tiers(workload)`, `ForMemory(IReadOnlyList<int> tiers, int memoryGb) -> int`: largest that fits, else smallest, 0 with no tiers. `TooLargeNote(int version, int memoryGb) -> string?`: exactly `This version needs {version} GB, so it may not run on your {memoryGb} GB card.`
- `WorkloadPicks` (singleton, like `ReturnTarget`): `Set(mode, IReadOnlyList<WorkloadPick>)`, `Get(mode)`; `record WorkloadPick(Guid WorkloadId, int VramGb)`.
- `WizardSelection`: `Workload { get; set; }`; `IReadOnlyList<WorkloadChoice> Choices` (empty on a single workload); `bool UpdateOnly`; `ExistingRun? Existing`; `IReadOnlyDictionary<Guid,string> PackNameByModelId`; `IsMultiple => Choices.Count > 0`.
- `WizardPlan.RefreshStages()`; `WizardRun.TryNext` uses it on the update path and moves to the first stage after the current one by enum value.
- `ExistingRun(WizardSelection, ExtendUnion, GitRepository? manager)`: settable `Profile`, `RunUpdate`, `GpuUsable`, each recomputing; read-only `Target`, `UpgradeTorch => RunUpdate && GpuUsable && Target.ChangesInstalledStack`, `TorchHardStop`, `event Changed`. Sole writer of `selection.Workload` and of `TargetFolder` from the profile.

- [ ] Tests: `ForMemory` at 16 of `[8,12,16,24,32]` is 16, at 16 of `[24,32]` is 24, no tiers is 0; the note's exact text and null when it fits; routes for each mode; `For` for a single, several and Update only; `RefreshStages` drops a stage and `TryNext` lands on the right one; fresh path never refreshes; `UpgradeTorch` truth table; `ExistingRun` rewrites the workload on each change with the same model list object.
- [ ] Red, implement, green. Commit `feat(update): mode, versions and the state of an update run (#28)`.

### Task 10: Workload screen

**Files:** `Pages/SoftwareWorkloads.razor`; `Shared/WorkloadCard.razor`; create `Pages/UpdateWorkloads.razor` (`/existing/update`), `Shared/MemorySelector.razor`, `Shared/UpdateOnlyTile.razor`; `Pages/ExistingChoice.razor`; `wwwroot/app.css`.

Update path: offers ComfyUI workloads with enabled models or any workflow, installable as on models only. Subtitle: `Choose what to add to your ComfyUI, or just update it.` Button: `Choose`. **Update only tile:** first, `Update only` / `Updates ComfyUI and adds no workload.` / button `Continue`, hidden while Select multiple is on.

**Select multiple switch** on both paths. On: the memory panel `How much memory does your graphics card have?` / `This sets every workload below to the best version that fits. You can still change a single workload on its card.`, starting at 32, options 8, 12, 16, 24, 32. Cards swap the button for an `Include` checkbox and show a `Version` dropdown of their tiers, or `One version only`, and the red note when too large. **Changing the panel resets every card to `ForMemory`, including ones changed by hand; a card's dropdown changes only that card.** A bar says `No workloads selected` or `Continue with N workloads`, disabled at 0. Filters and the legacy switch don't lose picks. Continue stores picks and navigates to `WizardRoutes.Selected(mode)`. The fresh path has none of this.

Choice screen: the Update card becomes a link and loses `Coming soon`.

- [ ] bUnit tests: fresh screen unchanged; update path has the tile first; tile hidden with Select multiple; models-only has no tile; bare workload hidden on both; panel starts at 32; 16 moves cards and resets a hand-changed one; card change affects only itself; Ideogram-style `[24,32]` at 16 stays selectable on 24 with the exact note; no greyed class; `One version only`; bar counts; a filter keeps a pick; Continue stores versions; choice card links.
- [ ] Red, implement, green. Commit `feat(update): Update only tile and Select multiple on the workload screen (#28)`.

### Task 11: Wizard entry

**Files:** `Pages/Install.razor`; create `Pages/UpdateInstall.razor`, `UpdateOnlyInstall.razor`, `SelectedUpdateInstall.razor`, `SelectedModelsInstall.razor`; create `Core/Existing/ManagerRepositoryLocator.cs`; `Wizard/WorkloadHero.razor`; `Core/Wizard/WizardRoutes.cs` for `IsThisWizard`.

`Install` gains `bool UpdateOnly`, `bool Selected`. It builds the selection: **single models-only as today**; update path or several: `Union`, with `ExistingRun` on the update path and `PackNameByModelId`. Names: `ComfyUI update` for Update only, `{N} workloads` for several. `IsThisWizard` compares `WizardRoutes.For(selection)` with this page's route, and `ResumeRunning` uses the same. No picks on a `selected` route (a refresh, a new circuit): back to the workload screen. Hero shows no thumbnail for a union.

- [ ] Tests: each route builds the right plan; models-only several at 16 and 24 narrow links; a refresh with no picks redirects; a running update blocks and resumes on its own route; the fresh and single models-only paths are unchanged.
- [ ] Red, implement, green. Commit `feat(update): the wizard opens on an update or on several workloads (#28)`.

### Task 12: Location on the update path

**Files:** create `Core/Modules/ExistingFolderModule.cs`, `Core/Existing/SupportMessages.cs`, `Wizard/ExistingFolderPanel.razor`; `Core/Modules/ComfyFoldersModule.cs`; `Wizard/ComfyFoldersPanel.razor`; `CoreServiceCollectionExtensions.cs`.

`ExistingFolderModule` (Location, Order 0, `UsedIn` update only): folder box and Browse, starts from `LastExistingComfyUIFolder`, saves it in `PersistAsync`. Inspects off the render thread, replaced by the next folder as `InstallFolderModule` does; a stale answer is never used. `Validate` errors while checking, on an unsupported verdict, and on a blank folder. The panel shows the check result: ComfyUI folder, venv, Python, torch, update script state, Manager. Unsupported messages per #28 task 2 with a `Fresh install` link.

`ComfyFoldersModule`: `UsedIn` adds update. On the update path it seeds from the profile on `ExistingRun.Changed`, never from or to user settings. `OutputFolderDiffers`, `ModelFolderDiffers` compare full paths ignoring case and trailing separator. `Contribute`: `GenerateExtraModelPaths` only when the model folder differs; output rewrites the launcher only when it differs. Red boxes per #28 task 5.

- [ ] Tests: each verdict's message; Next off while checking; a stale slow answer ignored; Back and a new folder updates profile, target and workload with ticks kept; remembered folder; persisted only by Next; seeds; same folder with other case or trailing slash doesn't differ; unchanged folders write no yaml and no launcher; user settings untouched; red box only when different.
- [ ] Red, implement, green. Commit `feat(update): choose and check the ComfyUI folder (#28)`.

### Task 13: Update checkbox

**Files:** create `Core/Modules/ComfyUpdateModule.cs`, `Wizard/ComfyUpdatePanel.razor`; `VramProfileModule.cs`, `WorkflowSelectionModule.cs`; `ModelSelectionModule.cs`.

Content, Order -10, update only. `RunUpdate` starts on; `Locked` with Update only. Mockup wording verbatim: `Update ComfyUI`; the channel line; `An update can destroy a working ComfyUI`; `If your ComfyUI works today, this is the moment to think twice. After an update:` with the three bullets; `Everything working?` / `Untick this. You still get the workload's custom nodes and models.`; `Open my ComfyUI folder to back it up`. Unticked: `Good choice if it's working.` / `Your ComfyUI stays on its current version. We only add the workload's custom nodes and models.`, and the red note from `PacksNeedingNewer`: `{names} need torch {x} with CUDA {y}.` / `You have torch {a} with CUDA {b}, and we won't change it, so these workloads may not run until you update.` `Validate`: compatibility errors only when `UpgradeTorch`. `Contribute`: `Extend`, shortcuts off, and `RewriteLauncher` from the output folder.

`VramProfileModule` starts at the highest on any non-fresh path. `WorkflowSelectionModule` and the scan use the repository path as given on the update path.

- [ ] Tests: on by default; locked with Update only; warning and calm swap; note names the workloads and exact versions and shows only unticked; a hard stop only when torch will change, and unticking clears it; `Extend` values for each combination; open folder uses the host action; scan path isn't normalized.
- [ ] Red, implement, green. Commit `feat(update): the Update checkbox and its warnings (#28)`.

### Task 14: System stage

**Files:** create `Core/Modules/ExistingGpuModule.cs`, `Wizard/ExistingGpuPanel.razor`; `VcRuntimeModule.cs`, `LlamaCppModule.cs`.

`ExistingGpuModule` (System, Order 0, update only): detects at init and writes `ExistingRun.GpuUsable`. Applies when no usable GPU and not Update only. Red, never blocks: `No usable NVIDIA GPU was found. Torch and your launcher stay as they are, and this workload may not run on this machine.` `VcRuntimeModule`, `LlamaCppModule`: `UsedIn` adds update; `GpuPreflightModule`, `ShortcutsModule`, `InstallFolderModule` stay out.

- [ ] Tests: warning with no GPU, Next enabled, no CPU build; no warning on Update only; Unknown counts as usable; VC runtime stage appears only when torch will change with Triton or Sage, and goes when unticked, going back and forth; plan has no shortcuts or GPU consent.
- [ ] Red, implement, green. Commit `feat(update): System stage warns and never replaces a working setup (#28)`.

### Task 15: Confirm, result, labels

**Files:** create `Core/Modules/ExistingReviewModule.cs`, `Wizard/ExistingReviewPanel.razor`; `Wizard/ConfirmStage.razor`, `InstallStage.razor`, `ModelSelectionPanel.razor`, `ModelSelectionModule.cs`; `Install.razor` (`Update` as the last stage's label stays `Install`; buttons say `Start update`).

Review (Confirm, update only): the list per #28 task 7, red boxes for update, torch and the note, one `I understand what will change` checkbox. Confirm: `ComfyUI folder`, `Workloads` with each chosen version, `ComfyUI update`. Both new paths' multi-select list versions, as "a last chance to catch a wrong size". Result: `Update complete` for Update only, `Extension complete` otherwise, and failures to match. Model rows show their workload's name when `PackNameByModelId` has it. Session: wording and log file land in the ComfyUI folder.

- [ ] Tests: lines for Update only and for two workloads; checkbox blocks; red boxes per flag; versions on Confirm on both paths; headings; pack tag; log file in the ComfyUI folder; launcher button available.
- [ ] Red, implement, green. Commit `feat(update): Confirm says what will change (#28)`.

### Task 16: Smoke and PR

**Files:** `docs/manual-smoke.md`; screenshots under `docs/superpowers/specs/assets/2026-11-03-update-or-download-models/smoke-28/`.

Packaged build, BenQ only, real folders built in `%TEMP%`, never the owner's real ComfyUI unless a temp copy. Verified on screen: unsupported verdicts; a real venv ComfyUI at `E:\Installer\17\ComfyUI` copied or inspected read-only, with Update unticked so nothing changes it; Select multiple at 16 GB on both paths; models-only several in flat with a real name clash; Update only with the script run against a throwaway clone with a small git repo.

- [ ] Run CI's commands against locally packed packages, as in #64.
- [ ] Whole-branch review rounds with `/code-review high` on both PRs.
- [ ] Open the installer PR. **Don't merge the second half without the owner's go.**
