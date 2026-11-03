# Download Models Only Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One new Welcome tile leads to a choice screen, and its "Download models only" card downloads one workload's models into any folder, sorted into subfolders or flat, with nothing else installed or checked.

**Architecture:** Part 1 (SDK #85, repo `E:\Repos\DiffusionNexus.Installer.SDK`) adds a one-step flow, two options and one resolver method. Part 2 (Installer #63, repo `E:\Repos\DiffusionNexus.Installer`) adds a `WizardMode` on the selection. The registry drops every module that isn't used in that mode before it initializes any of them, so the fresh install modules never run, probe or save settings on this path.

**Tech Stack:** .NET 10, xunit + FluentAssertions + Moq, bUnit, Blazor Server inside Electron.

**Spec:** `docs/superpowers/specs/2026-11-03-update-or-download-models-design.md` (this branch), plus issue bodies Into-The-Latent/DiffusionNexus.Installer#63 and Little-God1983/DiffusionNexus.Installer.SDK#85.

## Global Constraints

- One issue, one PR. SDK: branch `feature/85-download-models-only` from `develop`, PR into `develop`. Installer: this branch `feature/existing-comfyui`, PR into `main`.
- Never merge a PR or push a tag without the owner's explicit go.
- `gh auth switch --user Little-God1983` before SDK pushes, `--user Into-The-Latent` before Installer pushes.
- SDK version becomes `2.4.0`. New interface members get a default implementation.
- `ModelOnlyInstallationFlow` and `OnlyModelDownload` keep today's behaviour.
- A fresh ComfyUI install behaves exactly as it does today.
- Models-only runs write only model files, only inside the chosen folder: no custom nodes, workflows, scripts, shortcuts, `extra_model_paths.yaml` or log file.
- No check of the folder's contents, not even for ComfyUI. The folder box only refuses a blank or malformed path.
- Installer builds use `-p:UseLocalSDK=true` until `v2.4.0` is on the feed, with the SDK checkout on the feature branch.
- GUI tests run on the BenQ monitor only (`BNQ7F05`), and select the test build by exe path.
- Exact copy:
  - Tile: `Update ComfyUI or Download Models`
  - Choice heading: `What do you want to do?`, sub-line `Choose one. Nothing changes until you confirm at the end.`
  - Cards: `Update existing ComfyUI` (`Updates your ComfyUI and adds a workload's custom nodes, models and workflows. You choose your ComfyUI folder and we check it.`) and `Download models only` (`Downloads a workload's models into any folder you choose. We don't check the folder, and nothing else is installed or changed.`)
  - Folder panel heading `Download folder`, hint `Models are downloaded here. We don't check this folder, so make sure it's the right one. For an existing ComfyUI, choose its models folder.`
  - Switch `Sort models into subfolders`, on hint `On: each model goes into the subfolder ComfyUI expects for its type.`, off hint `Off: every file goes directly into this folder.`
  - Red box, off only: `ComfyUI won't find these models until you move them into the right folders yourself.`

## Review Focus

- A folder named after the repository (`E:\AI\ComfyUI`): files land inside it, nothing is written one level up. Tests in Tasks 2 and 8.
- Every download fails, or some do: the result must not read "complete". Tests in Tasks 2 and 8.
- The user flips only the subfolder switch and returns to Content: "already downloaded" marks and the preflight must follow the new layout. Test in Task 6.
- A same-named but different file already in a flat folder: its bytes stay untouched and the report shows it as skipped. Test in Task 2.
- A catalog destination that is absolute, climbs with `..` or uses a placeholder: it can't leave the chosen folder. Test in Task 1.

---

# Part 1: SDK #85

Branch first: `cd E:\Repos\DiffusionNexus.Installer.SDK && git checkout develop && git pull && git checkout -b feature/85-download-models-only`

### Task 1: Destination rule for models-only runs

**Files:**
- Modify: `DiffusionNexus.Installer.SDK.Services/ModelDestinationResolver.cs`
- Test: `DiffusionNexus.Installer.SDK.Tests/CoreServices/ModelDestinationResolverTests.cs`

**Interfaces:**
- Produces: `public static string ModelDestinationResolver.ResolveDownloadOnly(InstallationConfiguration configuration, ModelDownload model, ModelDownloadLink? link, string folder, bool sortIntoSubfolders)`

- [ ] **Step 1: Write the failing tests**

With `folder = @"E:\AI\models"`, as one `[Theory]` per row plus named facts:

| Test | Input | Expected |
|---|---|---|
| `Flat_ignores_every_destination` | sort off, model `models/vae`, link `models/loras` | `E:\AI\models` |
| `Sorted_strips_the_models_prefix` | model `models/vae` | `E:\AI\models\vae` |
| `Sorted_prefers_the_link_destination` | link `models/loras`, model `models/vae` | `E:\AI\models\loras` |
| `Sorted_falls_back_to_the_workload_default` | no destinations, `Paths.DefaultModelDownloadDirectory = "models/checkpoints/x"` | `E:\AI\models\checkpoints\x` |
| `Sorted_falls_back_to_checkpoints` | nothing set | `E:\AI\models\checkpoints` |
| `Rooted_destination_stays_inside` | link `D:\Elsewhere`, model `models/vae` | `E:\AI\models\vae` |
| `Climbing_destination_stays_inside` | model `..\..\Windows`, nothing else | `E:\AI\models\checkpoints` |
| `Placeholder_is_removed` | link `{RepositoryPath}/models/loras` | `E:\AI\models\loras` |
| `Drive_root_works` | folder `E:\`, model `models/vae` | `E:\vae` |

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test DiffusionNexus.Installer.SDK.Tests --filter ModelDestinationResolverTests`
Expected: FAIL, `ResolveDownloadOnly` not defined.

- [ ] **Step 3: Implement `ResolveDownloadOnly`**

Flat returns `folder`. Otherwise try in order: `link?.Destination`, `model.Destination`, `configuration.Paths.DefaultModelDownloadDirectory`, `"checkpoints"`. For each: skip blank; remove `{RepositoryPath}` and `{Repository}`, trim leading separators; skip if rooted; apply `StripModelPrefix`; `Path.Combine(folder, candidate)`; skip unless its `Path.GetFullPath` equals the folder or starts with the folder's trimmed full path plus a separator (OrdinalIgnoreCase). Doc comment states that overrides don't apply here and why nothing may leave the folder.

- [ ] **Step 4: Run to verify they pass**

Run: same command. Expected: PASS.

- [ ] **Step 5: Commit**

`git commit -m "feat(models-only): destination rule that stays inside the chosen folder (#85)"`

### Task 2: The download-only run

**Files:**
- Create: `DiffusionNexus.Installer.SDK.Services/Installation/Flows/DownloadModelsOnlyFlow.cs`
- Modify: `DiffusionNexus.Installer.SDK.Services/IInstallationOrchestrator.cs` (`InstallationOptions` and `Default`), `.../Installation/InstallationFlowFactory.cs`, `.../Installation/InstallationPipeline.cs`, `.../InstallationOrchestrator.cs`, `.../Installation/Steps/Content/ModelDownloadStepHandler.cs`
- Test: create `DiffusionNexus.Installer.SDK.Tests/Installation/DownloadModelsOnlyRunTests.cs`

**Interfaces:**
- Consumes: `ResolveDownloadOnly` from Task 1.
- Produces: `InstallationOptions.DownloadModelsOnly` (bool, default false); `InstallationOptions.SortModelsIntoSubfolders` (bool, default true); `IInstallationFlowFactory.CreateDownloadModelsOnlyFlow()`, default implementation `=> new Flows.DownloadModelsOnlyFlow()`. The caller passes the chosen folder as `targetDirectory`.

- [ ] **Step 1: Write the failing tests**

Real `InstallationPipeline`, real `InstallationFlowFactory` over a mocked `IServiceProvider`, real `ModelDownloadStepHandler` and `FileDownloader` over a stub `HttpMessageHandler` (copy `SuccessHandler` from `ModelDownloadStepHandlerTests`), a temp folder. Workload: two models, `models/vae` and `models/loras`, plus custom nodes, workflows and an impossible torch pairing.

```csharp
Flow_has_only_the_download_step      // BuildSteps == [InstallationStep.DownloadModels]
Sorted_run_writes_only_models        // files == vae\a.safetensors, loras\b.safetensors, nothing else
Flat_run_writes_only_models          // files == a.safetensors, b.safetensors, nothing else
Torch_pairing_does_not_stop_the_run  // result.IsSuccess
Existing_flat_file_is_left_alone     // pre-write a.safetensors with other bytes:
                                     // bytes unchanged, row Outcome == Skipped, Comment contains its path
Every_download_failing_fails_the_run // 404 handler: !result.IsSuccess
Partial_failure_message_counts_it    // 1 ok + 1 404: IsSuccess, Message contains "1 failed"
Success_message_is_the_step_summary  // Message starts "Downloaded 2 models"
```

In `InstallationOrchestratorTests` add `Folder_named_after_the_repository_is_used_as_is`: `targetDirectory = @"E:\AI\ComfyUI"`, `DownloadModelsOnly = true`, a capturing stub step handler: `context.TargetDirectory` and `context.GetRepositoryPath()` both equal `@"E:\AI\ComfyUI"`. Add `Old_model_only_flow_is_unchanged` to `InstallationPipelineTests`: `OnlyModelDownload = true` still resolves `CreateModelOnlyFlow`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test DiffusionNexus.Installer.SDK.Tests --filter "DownloadModelsOnlyRunTests|InstallationOrchestratorTests|InstallationPipelineTests"`
Expected: FAIL to compile, options not defined.

- [ ] **Step 3: Implement**

- `DownloadModelsOnlyFlow`: `BuildSteps` returns only `DownloadModels`, `GetCustomHandlers` null, `SupportedType` ComfyUI.
- Pipeline: `DownloadModelsOnly` picks the new flow ahead of `OnlyModelDownload`; `TryValidateTorchCompatibility` returns true for it. On this path the success message is the last executed step's message (`"Nothing to download."` if none ran) and the final progress line says `Download finished`.
- Orchestrator: on this path `TargetDirectory` and `RepositoryPath` are the raw `targetDirectory`, not normalized.
- Handler: both destination sites (direct URL with `link: null`, and the link loop) call `ResolveDownloadOnly(config, model, link, repositoryPath, options.SortModelsIntoSubfolders)` when `DownloadModelsOnly`. The all-failed return uses `shouldContinue: !options.DownloadModelsOnly`.

- [ ] **Step 4: Run to verify they pass**

Run: same command. Expected: PASS.

- [ ] **Step 5: Commit**

`git commit -m "feat(models-only): a run that only downloads models into any folder (#85)"`

### Task 3: Version, docs, PR

**Files:**
- Modify: `Directory.Build.props` (`<Version>2.4.0</Version>`), `CLAUDE.md` (the version line and a `2.4.0:` paragraph naming the two options, the flow, `ResolveDownloadOnly`, and that the old flow is unchanged)

- [ ] **Step 1: Run the whole suite**

Run: `dotnet test DiffusionNexus.Installer.SDK.slnx`
Expected: all green. Report any failure with its output.

- [ ] **Step 2: Commit, push, open the PR into `develop`**

Body ends `Closes #85`. Tell the owner the Installer can't pin until the merge and `v2.4.0` tag, and ask for that go when ready. Part 2 continues with the local SDK meanwhile.

---

# Part 2: Installer #63

On `feature/existing-comfyui`. Every test command adds `-p:UseLocalSDK=true`.

### Task 4: Wizard mode and which modules run

**Files:**
- Create: `DiffusionNexus.Installer.Core/Wizard/WizardMode.cs`, `DiffusionNexus.Installer.Core/Wizard/WizardRoutes.cs`
- Modify: `.../Wizard/WizardSelection.cs`, `IWizardModule.cs`, `WizardModuleRegistry.cs`, `WizardPlan.cs`, `InstallationOptionsDraft.cs`, `.../Modules/VramProfileModule.cs`, `ModelSelectionModule.cs`, `DisclaimerModule.cs`
- Test: `DiffusionNexus.Installer.Tests/Wizard/WizardModuleRegistryTests.cs`, `InstallationOptionsDraftTests.cs`, `Modules/VramProfileModuleTests.cs`, new `Wizard/WizardRoutesTests.cs`

**Interfaces:**
- Produces:
  - `enum WizardMode { FreshInstall, ModelsOnly }`
  - `WizardSelection.Mode` (`init`, default `FreshInstall`); `WizardSelection.SortModelsIntoSubfolders` (`set`, default true)
  - `IWizardModule.UsedIn(WizardMode mode) => mode == WizardMode.FreshInstall` (default member)
  - `WizardRoutes.Install(Guid workloadId, WizardMode mode)` returning `/install/{id}` or `/existing/models/{id}`
  - `InstallationOptionsDraft.DownloadModelsOnly`, `.SortModelsIntoSubfolders` (default true), both mapped in `ToOptions`

- [ ] **Step 1: Write the failing tests**

```csharp
Models_only_plan_has_only_its_stages        // Stages == Content, Confirm, Install; no System (Task 5 adds Location)
Models_only_never_initializes_other_modules // a fake module with default UsedIn: InitializeAsync call count == 0
Fresh_plan_is_unchanged                     // same module ids, in order, as before
ToOptions_says_models_only                  // DownloadModelsOnly true; fresh: false, SortModelsIntoSubfolders true
Vram_starts_highest_on_models_only          // "8,16,24": SelectedTier == 24
Vram_starts_lowest_on_a_fresh_install       // "8,16,24": SelectedTier == 8
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test DiffusionNexus.Installer.Tests -p:UseLocalSDK=true --filter "WizardModuleRegistryTests|InstallationOptionsDraftTests|VramProfileModuleTests|WizardRoutesTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

Registry: `_modules().Where(m => m.UsedIn(selection.Mode))` ahead of initialization. `WizardPlan.ToOptions` seeds `DownloadModelsOnly` from the mode. `VramProfileModule`, `ModelSelectionModule` and `DisclaimerModule` override `UsedIn => true`. The doc comment on `UsedIn` says why filtering happens before initialization: `InstallFolderModule.InitializeAsync` writes the remembered install folder into the selection, and `GpuPreflightModule` runs a hardware probe.

- [ ] **Step 4: Run to verify they pass**

Run: same command. Expected: PASS.

- [ ] **Step 5: Commit**

`git commit -m "feat(models-only): wizard mode that decides which modules run (#63)"`

### Task 5: Folder step

**Files:**
- Create: `DiffusionNexus.Installer.Core/Modules/ModelsOnlyFolderModule.cs`, `DiffusionNexus.Installer.Electron/Components/Wizard/ModelsOnlyFolderPanel.razor`
- Modify: `DiffusionNexus.Installer.Core/CoreServiceCollectionExtensions.cs` (register), `DiffusionNexus.Installer.Electron/Components/Pages/Install.razor` (`RenderModule`)
- Test: new `DiffusionNexus.Installer.Tests/Modules/ModelsOnlyFolderModuleTests.cs`, `Components/ModelsOnlyFolderPanelTests.cs`

**Interfaces:**
- Consumes: `WizardMode`, `UsedIn`, draft fields from Task 4; `ResolveDownloadOnly` from Task 1.
- Produces: `ModelsOnlyFolderModule : IWizardModule` with `Id = "models-only-folder"`, Stage Location, Order 0, `ShowsOwnValidation = true`, `UsedIn(mode) => mode == ModelsOnly`, `string Folder { get; set; }`, `bool SortIntoSubfolders { get; set; }`, `IReadOnlyList<ModelsOnlyPreviewRow> Preview`; `record ModelsOnlyPreviewRow(string Subfolder, string ModelName)`.

- [ ] **Step 1: Write the failing tests**

```csharp
Models_only_plan_opens_on_location      // registry with all real modules: Stages == Location, Content, Confirm, Install
Absent_from_a_fresh_install             // UsedIn(FreshInstall) == false
Starts_empty_and_sorted                 // Folder == "", SortIntoSubfolders, Validate() invalid "Choose a folder to download into."
Any_full_path_is_valid                  // non-empty, non-ComfyUI, not yet existing: Validate().IsValid
Relative_path_is_refused                // "AI\models" invalid, names a full path example
Pasted_quotes_are_cleaned               // "\"E:\\AI\\models\\\"" => selection.TargetFolder == E:\AI\models
Switch_reaches_selection_and_options    // off: selection flag false, draft.SortModelsIntoSubfolders false
Saves_nothing                           // settings repository: SaveAsync never called after PersistAsync
Preview_sorted_and_flat                 // on: Subfolder "vae"; off: Subfolder ""
Panel_shows_red_box_only_when_off       // .validation-error text exact copy; absent when on
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test DiffusionNexus.Installer.Tests -p:UseLocalSDK=true --filter "ModelsOnlyFolder"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

Clean with `FolderInput.Clean`, judge with `FolderInput.ShapeProblem(folder, @"Enter the full path of the download folder, for example D:\AI\models.")`. It writes `TargetFolder` and the sort flag to the selection eagerly, as `InstallFolderModule` does. The panel uses `FolderSwitch` for the switch and `IFolderPicker` for Browse, two `.panel` sections as in screenshot 04, with the preview as a `<pre>`-style list. The preview lists model names because the file name depends on the memory size, which isn't chosen yet. No persistence, because writing `DefaultTargetInstallFolder` or `DefaultModelBaseFolder` would change the next fresh install.

- [ ] **Step 4: Run to verify they pass**

Run: same command. Expected: PASS.

- [ ] **Step 5: Commit**

`git commit -m "feat(models-only): download folder step with the subfolder switch (#63)"`

### Task 6: Model scan and disk estimate agree

**Files:**
- Modify: `DiffusionNexus.Installer.Core/Content/ModelPresenceScanner.cs`, `Content/DiskSpaceEstimator.cs`, `Modules/ModelSelectionModule.cs`, `DiffusionNexus.Installer.Electron/Components/Wizard/ModelSelectionPanel.razor`
- Test: `DiffusionNexus.Installer.Tests/Content/ModelPresenceScannerTests.cs`, `Content/DiskSpaceEstimatorTests.cs`, `Modules/ModelSelectionModuleTests.cs`, `Components/ModelSelectionPanelTests.cs`

**Interfaces:**
- Consumes: `WizardSelection.Mode`, `.SortModelsIntoSubfolders`; `ResolveDownloadOnly`.
- Produces: `ModelScanRequest(..., bool DownloadOnly = false, bool SortIntoSubfolders = true)`; `DiskSpaceRequest(..., bool ModelsOnly = false)`; `ModelSelectionModule.LastScannedSort`.

- [ ] **Step 1: Write the failing tests**

```csharp
Scan_sorted_looks_in_the_subfolder       // file at <folder>\vae\a.safetensors: present; DestinationDirectory == <folder>\vae
Scan_flat_looks_in_the_folder            // file at <folder>\a.safetensors: present; DestinationDirectory == <folder>
Scan_uses_the_folder_not_a_repo_subfolder // folder E:\x: no target under E:\x\ComfyUI
Estimate_counts_no_repo_or_venv          // ModelsOnly: RequiredBytes == models + buffer
Flipping_only_the_switch_rescans         // panel: same folder, flag flipped: scanner called again, marks follow
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test DiffusionNexus.Installer.Tests -p:UseLocalSDK=true --filter "ModelPresenceScannerTests|DiskSpaceEstimatorTests|ModelSelection"`
Expected: FAIL.

- [ ] **Step 3: Implement**

Scanner: with `DownloadOnly`, both the model and link destination come from `ResolveDownloadOnly(workload, model, link, request.RepositoryPath, request.SortIntoSubfolders)`. Module on this mode: `RepositoryPath = folder`, not `RepositoryPaths.Resolve`; commits `LastScannedSort`; passes `ModelsOnly` to the estimator. Estimator: `onlyModelDownload: request.ModelsOnly`. Panel: `ScanIsStale` also compares the sort flag; "install drive" reads "download drive" in this mode.

- [ ] **Step 4: Run to verify they pass**

Run: same command. Expected: PASS.

- [ ] **Step 5: Commit**

`git commit -m "feat(models-only): model scan and disk estimate follow the chosen layout (#63)"`

### Task 7: Tile, choice screen, workload screen

**Files:**
- Create: `DiffusionNexus.Installer.Electron/Components/Pages/ExistingChoice.razor` (`@page "/existing"`), `Pages/ModelsOnlyWorkloads.razor` (`@page "/existing/models"`), `Pages/ModelsOnlyInstall.razor` (`@page "/existing/models/{WorkloadId:guid}"`)
- Modify: `Pages/Welcome.razor`, `Pages/SoftwareWorkloads.razor`, `Pages/Install.razor`, `Shared/WorkloadCard.razor`, `Services/ReturnTarget.cs`, `wwwroot/app.css`
- Test: `DiffusionNexus.Installer.Tests/Components/WelcomePageTests.cs`, `SoftwareWorkloadsPageTests.cs`, `WorkloadCardTests.cs`, `Host/ReturnTargetTests.cs`, new `Components/ExistingChoicePageTests.cs`

**Interfaces:**
- Consumes: `WizardRoutes.Install`, `WizardSelection.Mode`.
- Produces: `[Parameter] WizardMode Mode` on `SoftwareWorkloads` and `Install`; `[Parameter] string ActionLabel = "Install"` on `WorkloadCard`. `ModelsOnlyWorkloads` renders `<SoftwareWorkloads Software="ComfyUI" Mode="WizardMode.ModelsOnly" />`; `ModelsOnlyInstall` renders `<Install WorkloadId="@WorkloadId" Mode="WizardMode.ModelsOnly" />`. Thin pages rather than URL sniffing, and path routes rather than a query, because `ReturnTarget.Normalize` drops queries.

- [ ] **Step 1: Write the failing tests**

```csharp
One_new_tile_directly_after_ComfyUI  // ComfyUI + Fooocus: 3 cards, card[1] href "/existing", exact label
No_tile_without_ComfyUI              // Fooocus only: 1 card, no "/existing" link
Choice_models_card_links_on          // href "/existing/models"
Choice_update_card_is_disabled       // no anchor, shows "Coming soon"
Workloads_hide_those_without_models  // workload with none, or all disabled: absent
Workloads_offer_blocked_ones_too     // incompatible torch workload: button "Download models", no reason text
Workload_button_goes_to_models_route // navigates to /existing/models/{id}
Fresh_workload_screen_is_unchanged   // /software/ComfyUI: "Install", /install/{id}, blank workload shown
Legacy_switch_hides_when_empty       // the only legacy pack has no models: no switch on this path
Return_target_knows_the_models_route // kept models-only wizard survives Remember("existing/models/{id}")
A_running_fresh_install_blocks_models_only // same workload id, other mode: "Another installation is running"
```

Existing Welcome assertions that count `.software-card` with a ComfyUI workload present go up by one, which is the intended change.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test DiffusionNexus.Installer.Tests -p:UseLocalSDK=true --filter "WelcomePageTests|SoftwareWorkloadsPageTests|WorkloadCardTests|ReturnTargetTests|ExistingChoicePageTests"`
Expected: FAIL.

- [ ] **Step 3: Implement**

- Welcome: the tile is a `.software-card` right after ComfyUI's, so it's visible without scrolling the strip. It uses the ComfyUI logo, the exact label, and the second line `For existing ComfyUI users`. Shown only when ComfyUI is in the strip. This replaces the mockup's NEW chip, which would need removing later.
- Choice: `.workload-screen` layout with back link `← All software`. The update card has no anchor and shows `Coming soon`.
- `SoftwareWorkloads` in this mode: filters to `ModelDownloads.Any(m => m.Enabled)`, maps entries `with { IsInstallable = true, Incompatibility = null }` because torch can't block a download, button `Download models`, back link `← Back` to `/existing`, sub-line `Choose which workload's models you want to download.`, navigates with `WizardRoutes.Install`.
- `Install`: passes `Mode` into `WizardSelection`; the running, finished and kept checks also compare `Selection.Mode`; `ResumeRunning` uses `WizardRoutes.Install`.
- `ReturnTarget.InstallPath`: uses `WizardRoutes.Install(id, plan.Selection.Mode)`.

- [ ] **Step 4: Run to verify they pass**

Run: same command. Expected: PASS.

- [ ] **Step 5: Commit**

`git commit -m "feat(models-only): new tile, choice screen and workload screen (#63)"`

### Task 8: Confirm, progress and result tell the truth

**Files:**
- Modify: `DiffusionNexus.Installer.Electron/Components/Wizard/ConfirmStage.razor`, `Wizard/InstallStage.razor`, `Pages/Install.razor`, `DiffusionNexus.Installer.Core/Install/PostInstallTargets.cs`, `Install/InstallSession.cs`
- Test: `DiffusionNexus.Installer.Tests/Install/InstallSessionTests.cs`, new `Install/PostInstallTargetsTests.cs` (or the existing file if one covers it), `Components/ConfirmStageTests.cs`, `InstallStageTests.cs`

**Interfaces:**
- Consumes: `WizardSelection.Mode`, `.SortModelsIntoSubfolders`, `InstallationResult`.

- [ ] **Step 1: Write the failing tests**

```csharp
No_launcher_on_models_only      // RepositoryPath holds run_nvidia.bat: LauncherScript == null, Folder set
No_log_file_on_models_only      // folder named E:\...\ComfyUI: nothing written there or one level up, LogFilePath null
Fresh_install_still_logs        // unchanged
Confirm_shows_folder_and_switch // "Ready to download", "Download folder", "Subfolders" On/Off;
                                // no "Python", "Shortcuts", "Software" rows
Confirm_button_says_start_download // Install page on Confirm: "Start download"
Result_all_ok                   // "Download complete", Message visible
Result_with_a_failed_row        // IsSuccess, one Failed row: "Some downloads failed", class result-bad
Result_failed                   // "Download failed"; running heading "Downloading"; button "Cancel download"
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test DiffusionNexus.Installer.Tests -p:UseLocalSDK=true --filter "PostInstallTargets|InstallSessionTests|ConfirmStage|InstallStage"`
Expected: FAIL.

- [ ] **Step 3: Implement**

`PostInstallTargets.From` returns no launcher in this mode: the user's folder may hold a real ComfyUI, and "Start app" isn't what they asked for. `InstallSession.LogFolder` returns null in this mode: the log would otherwise be a stray file among the models, or land outside the chosen folder for a folder named `ComfyUI`. Copy log still works. The headings and labels above branch on `Mode`. The `Some downloads failed` heading is decided from `Session.ReportRows` containing a `Failed` outcome.

- [ ] **Step 4: Run to verify they pass**

Run: same command. Expected: PASS.

- [ ] **Step 5: Commit**

`git commit -m "feat(models-only): confirm and result screens say what this path does (#63)"`

### Task 9: Smoke, pin, PR

**Files:**
- Modify: `docs/manual-smoke.md` (new section for this path), `DiffusionNexus.Installer.Core/*.csproj` and any other project pinning the SDK (to `2.4.0`, once tagged)

- [ ] **Step 1: Run the full suite**

Run: `dotnet test DiffusionNexus.Installer.slnx -p:UseLocalSDK=true`
Expected: all green.

- [ ] **Step 2: GUI smoke on the BenQ**

Follow the `run` skill and the Electron smoke recipe in memory `installer-electron-44-bump`. Choose a workload with small files. Capture each screen. Check:
- One new tile; the choice screen with the update card disabled.
- Blank workloads absent; memory panel starts at the highest size; no System stage.
- A run into an empty non-ComfyUI folder, subfolders on, then a new folder with subfolders off. List both folders and confirm they hold only model files.
- A run into an existing folder of the same files shows them as present.
- A fresh ComfyUI wizard still opens with its usual stages and folder.

Report what was seen. Post screenshots on the PR.

- [ ] **Step 3: Commit the smoke doc, push, open the PR into `main`**

Body ends `Closes #63`. It says that CI stays red until `v2.4.0` is on the feed, and that the release waits for #28, per the design's section 9a.

- [ ] **Step 4: After the owner merges SDK #85 and tags `v2.4.0`**

Bump the pins, then run `dotnet build DiffusionNexus.Installer.slnx -c Release -p:UseLocalSDK=false` and the test suite. 2.3.1 raised the floor for Microsoft.Extensions.*, Serilog and CommunityToolkit.Mvvm, so an exact pin below it fails restore with NU1605: bump that pin too. Commit and push.
