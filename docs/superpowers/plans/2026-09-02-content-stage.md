# Content Stage (Slice 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the wizard's Content stage — VRAM tier, model picker, workflow picker — so 20 of the 21 Installer-targeted catalog workloads become installable, with 1.x behaviour throughout.

**Architecture:** Three new `IWizardModule`s render into the existing `WizardStage.Content`. A shared `VramTiers` parser keeps the gallery gate and the VRAM module in agreement; a single `ModelPresenceScanner` replaces 1.x's two hand-synced scan copies and feeds both the "already downloaded" markers and the pre-install size verification. Modules become per-run instances (transient DI + a factory-backed registry). Pre-install verification runs when the user leaves Confirm, through a `ModelPreflight` that shows one mismatch dialog and refuses to advance if it is dismissed.

**Tech Stack:** .NET 10, Blazor Server + ElectronNET.Core, SDK 2.0.0-preview.4 (`Models`, `Services`, `Catalog`), xunit 2.9 + FluentAssertions 7 + Moq + bUnit 2.8.6.

**Spec:** `docs/superpowers/specs/2026-09-02-electron-wizard-slice-2-content-stage-design.md`

## Global Constraints

- Repo: `e:\Repos\DiffusionNexus.Installer`, branch `feature/content-stage` (already created; the spec is committed on it). Commit after every task and push (`git push -u origin feature/content-stage` the first time, `git push` after).
- Commit messages end with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- Run tests with `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~<ClassName>"` from the repo root; the full suite is `dotnet test DiffusionNexus.Installer.Tests`. 153 tests pass at the start of this plan.
- Two public classes are named `VramProfileHelper`. Link selection MUST use `DiffusionNexus.Installer.SDK.Services.Installation.Utilities.VramProfileHelper` (the one `ModelDownloadStepHandler` calls). Import it under the alias `PipelineVram`.
- Two public types are named `InstallationOptions` (`SDK.Models.Installation` class vs `SDK.Services` record). Importing both namespaces is CS0104-ambiguous; the draft uses the `SDK.Services` one.
- Never resolve the container's `HttpClient` for size lookups — it has an infinite timeout on purpose. Size lookups use their own 10 s client (Task 6).
- The model list shows every enabled model with no tier annotation (spec decision 2). Do not add "8 GB variant" / "skipped at this tier" labels.
- The VRAM dropdown offers only the tiers the workload declares, lowest preselected (decisions 3 and 4).
- Every editable control in a panel raises the `Changed` callback after updating its module.
- bUnit: an `@onclick` handler that returns `Task` must be driven with `await element.ClickAsync(new MouseEventArgs())`, not `.Click()`.

## File structure

**Core — new**
- `Wizard/VramTiers.cs` — the one tier parser.
- `Content/RepositoryPaths.cs` — where the main repo lands for an install folder (mirrors the orchestrator).
- `Content/ModelPresenceScanner.cs` — `IModelPresenceScanner`, `ModelScanRequest`, `ModelFileTarget`, `ModelPresence`, `ModelPresenceScanner`.
- `Content/DiskSpaceEstimator.cs` — `IDiskSpaceEstimator`, `DiskSpaceRequest`, `DiskSpaceEstimate`, `SdkDiskSpaceEstimator`.
- `Content/ExistingModelVerification.cs` — `IExistingModelVerifier`, `SdkExistingModelVerifier`.
- `Modules/VramProfileModule.cs`, `Modules/ModelSelectionModule.cs` (with `ModelRow`, `ModelGroup`), `Modules/WorkflowSelectionModule.cs` (with `WorkflowRow`).
- `Host/IMismatchedFilePrompt.cs` — interface + `MismatchResolution`; `Host/MismatchPromptService.cs` — the state holder the modal renders.
- `Install/ModelPreflight.cs` — `IModelPreflight`, `PreflightResult`, `ModelPreflight`.

**Core — modified**
- `Wizard/WorkloadCapabilities.cs` (Detect uses `VramTiers`), `Wizard/WizardModuleRegistry.cs` (factory ctor), `Wizard/WizardSelection.cs` (+ `ModelBaseFolder`, `FolderPathOverrides`), `Modules/InstallFolderModule.cs` and `Modules/ComfyFoldersModule.cs` (push answers into the selection eagerly), `CoreServiceCollectionExtensions.cs`.

**Electron — new**
- `Components/Wizard/VramProfilePanel.razor`, `ModelSelectionPanel.razor`, `WorkflowSelectionPanel.razor`; `Components/Shared/MismatchModal.razor`.

**Electron — modified**
- `Components/Pages/Install.razor` (three `RenderModule` arms, async Next with preflight), `Components/Wizard/ConfirmStage.razor` (tier/model/workflow rows), `Components/Layout/MainLayout.razor` (hosts the modal), `Program.cs` (prompt registration), `wwwroot/app.css`.

**Tests — new**
- `Support/EmbeddedCatalog.cs`; `Wizard/VramTiersTests.cs`; `Modules/SelectionSyncTests.cs`, `VramProfileModuleTests.cs`, `ModelSelectionModuleTests.cs`, `WorkflowSelectionModuleTests.cs`; `Content/RepositoryPathsTests.cs`, `ModelPresenceScannerTests.cs`, `ScannerPipelineAgreementTests.cs`, `DiskSpaceEstimatorTests.cs`; `Host/MismatchPromptServiceTests.cs`; `Install/ModelPreflightTests.cs`; `Components/VramProfilePanelTests.cs`, `ModelSelectionPanelTests.cs`, `WorkflowSelectionPanelTests.cs`, `MismatchModalTests.cs`.

**Tests — modified**
- `Wizard/WorkloadCapabilitiesTests.cs`, `Wizard/WizardModuleRegistryTests.cs`, `Wizard/CapabilityAgreementTests.cs`, `Wizard/RealCatalogInstallabilityTests.cs`, `DependencyInjectionTests.cs`, `Components/InstallPageTests.cs`, `Gallery/GalleryBuilderTests.cs`, `Install/InstallSessionTests.cs` and every other file constructing `WizardModuleRegistry`.

---

### Task 1: One VRAM tier parser, used by the gate

**Files:**
- Create: `DiffusionNexus.Installer.Core/Wizard/VramTiers.cs`
- Modify: `DiffusionNexus.Installer.Core/Wizard/WorkloadCapabilities.cs` (the `VramProfiles` check inside `Detect`)
- Test: `DiffusionNexus.Installer.Tests/Wizard/VramTiersTests.cs`, `DiffusionNexus.Installer.Tests/Wizard/WorkloadCapabilitiesTests.cs`

**Interfaces:**
- Produces: `public static class VramTiers { public static IReadOnlyList<int> Parse(string? profiles); }` — distinct, ascending GB values; `"24+"` parses as 24; junk yields an empty list.

- [ ] **Step 1: Write the failing tests**

```csharp
// DiffusionNexus.Installer.Tests/Wizard/VramTiersTests.cs
using DiffusionNexus.Installer.Core.Wizard;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Wizard;

public class VramTiersTests
{
    [Theory]
    [InlineData("8,12,16,24,32", new[] { 8, 12, 16, 24, 32 })]
    [InlineData("24,32", new[] { 24, 32 })]
    [InlineData("8,16,24,24+", new[] { 8, 16, 24 })]
    [InlineData(" 32 , 8 ,8", new[] { 8, 32 })]
    [InlineData("16GB,24+GB", new[] { 16, 24 })]
    [InlineData("-8,0,12", new[] { 12 })]
    public void Parses_the_tiers_a_workload_declares_and_nothing_else(string profiles, int[] expected)
        => VramTiers.Parse(profiles).Should().Equal(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,")]
    [InlineData("abc")]
    public void Junk_yields_no_tiers_rather_than_throwing(string? profiles)
        => VramTiers.Parse(profiles).Should().BeEmpty();
}
```

Append to `WorkloadCapabilitiesTests`:

```csharp
    [Theory]
    [InlineData("abc")]
    [InlineData(",,")]
    public void Vram_is_not_detected_from_an_unparseable_profile_string(string profiles)
    {
        // Detect and VramProfileModule.AppliesTo share VramTiers.Parse. If Detect kept its old
        // "non-blank string" rule, this workload would be gated as needing a tier (blocking) while
        // the module refused to render one -- a card that can never be installed.
        var w = new InstallationConfiguration();
        w.Vram.VramProfiles = profiles;

        WorkloadCapabilities.Detect(w).Should().NotHaveFlag(WorkloadCapability.VramProfile);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~VramTiersTests|FullyQualifiedName~WorkloadCapabilitiesTests"`
Expected: build error `The name 'VramTiers' does not exist`.

- [ ] **Step 3: Write the parser and switch Detect to it**

```csharp
// DiffusionNexus.Installer.Core/Wizard/VramTiers.cs
using System.Globalization;

namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>
/// The one parser for a workload's comma-separated VRAM tier list ("8,12,16,24,32", "24,32",
/// "8,16,24,24+"). Shared by the installability gate (WorkloadCapabilities.Detect) and
/// VramProfileModule so the two can never disagree about whether a workload has tiers.
/// </summary>
public static class VramTiers
{
    /// <summary>Distinct, ascending, in GB. Unparseable entries are dropped; "24+" means 24.</summary>
    public static IReadOnlyList<int> Parse(string? profiles)
    {
        if (string.IsNullOrWhiteSpace(profiles)) return [];

        var tiers = new SortedSet<int>();

        foreach (var raw in profiles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw;
            if (token.EndsWith("GB", StringComparison.OrdinalIgnoreCase)) token = token[..^2];
            token = token.TrimEnd().TrimEnd('+').TrimEnd();
            if (token.EndsWith("GB", StringComparison.OrdinalIgnoreCase)) token = token[..^2].TrimEnd();

            if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var gb) && gb > 0)
                tiers.Add(gb);
        }

        return [.. tiers];
    }
}
```

In `WorkloadCapabilities.Detect`, replace

```csharp
        if (!string.IsNullOrWhiteSpace(workload.Vram.VramProfiles))
            caps |= WorkloadCapability.VramProfile;
```

with

```csharp
        // The same parser VramProfileModule.AppliesTo uses. A non-blank but unparseable string
        // must not be gated as "needs a tier" -- the module would decline to render one and the
        // card could never be installed.
        if (VramTiers.Parse(workload.Vram.VramProfiles).Count > 0)
            caps |= WorkloadCapability.VramProfile;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~VramTiersTests|FullyQualifiedName~WorkloadCapabilitiesTests"`
Expected: all pass, including the pre-existing `Vram_is_detected_from_a_non_empty_profile_string` and `Vram_is_not_detected_from_whitespace`.

- [ ] **Step 5: Commit and push**

```bash
git add DiffusionNexus.Installer.Core/Wizard/VramTiers.cs DiffusionNexus.Installer.Core/Wizard/WorkloadCapabilities.cs DiffusionNexus.Installer.Tests/Wizard/VramTiersTests.cs DiffusionNexus.Installer.Tests/Wizard/WorkloadCapabilitiesTests.cs
git commit -m "feat(wizard): one VRAM tier parser shared by the gate and the module

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push -u origin feature/content-stage
```

---

### Task 2: Per-run module lifetime

**Files:**
- Modify: `DiffusionNexus.Installer.Core/Wizard/WizardModuleRegistry.cs`
- Modify: `DiffusionNexus.Installer.Core/CoreServiceCollectionExtensions.cs`
- Test: `DiffusionNexus.Installer.Tests/Wizard/WizardModuleRegistryTests.cs`, `DiffusionNexus.Installer.Tests/DependencyInjectionTests.cs`, plus every test file that constructs `WizardModuleRegistry`.

**Interfaces:**
- Produces: `public WizardModuleRegistry(Func<IEnumerable<IWizardModule>> modules)` — the ONLY constructor. `SatisfiedCapabilities` is computed once from a single factory call; `BuildPlanAsync` calls the factory once per run.

- [ ] **Step 1: Replace the "same instances" test and add the factory tests**

In `WizardModuleRegistryTests.cs`, delete `Building_a_second_plan_reinitializes_the_same_module_instances` and add:

```csharp
    [Fact]
    public async Task Every_plan_gets_the_instances_its_factory_produced_that_time()
    {
        // The whole point of per-run modules: answers from one workload cannot leak into the
        // next because the next run never sees the first run's objects.
        var produced = new List<StubModule>();
        var registry = new WizardModuleRegistry(() =>
        {
            var m = new StubModule("m", WizardStage.Location, 0, WorkloadCapability.None, applies: true);
            produced.Add(m);
            return [m];
        });

        var first = await registry.BuildPlanAsync(Selection());
        var second = await registry.BuildPlanAsync(Selection());

        // produced[0] is the constructor's capability probe; plans get [1] and [2].
        first.AllModules.Single().Should().BeSameAs(produced[1]);
        second.AllModules.Single().Should().BeSameAs(produced[2]);
        first.AllModules.Single().Should().NotBeSameAs(second.AllModules.Single());
        produced[1].InitializeCount.Should().Be(1, "a fresh instance is initialized exactly once");
    }

    [Fact]
    public void Satisfied_capabilities_are_computed_once_at_construction()
    {
        // The gallery asks this for every card on every render; it must not build modules to answer.
        var calls = 0;
        var registry = new WizardModuleRegistry(() =>
        {
            calls++;
            return [new StubModule("f", WizardStage.Location, 0, WorkloadCapability.ComfyFolders, applies: true)];
        });

        _ = registry.SatisfiedCapabilities;
        _ = registry.SatisfiedCapabilities;

        calls.Should().Be(1);
        registry.SatisfiedCapabilities.Should().Be(WorkloadCapability.ComfyFolders);
    }
```

Append to `DependencyInjectionTests.cs` (add `using DiffusionNexus.Installer.SDK.Models.Configuration;` at the top):

```csharp
    [Fact]
    public async Task Two_plans_built_from_the_container_do_not_share_module_instances()
    {
        using var provider = Build();
        var registry = provider.GetRequiredService<WizardModuleRegistry>();

        var workload = new InstallationConfiguration { Name = "Fooocus" };
        workload.Repository.Type = RepositoryType.Fooocus;

        var first = await registry.BuildPlanAsync(new WizardSelection { Workload = workload });
        var second = await registry.BuildPlanAsync(new WizardSelection { Workload = workload });

        first.AllModules.Select(m => m.Id).Should().BeEquivalentTo(second.AllModules.Select(m => m.Id));
        foreach (var (a, b) in first.AllModules.Zip(second.AllModules))
            a.Should().NotBeSameAs(b, $"module '{a.Id}' must be a fresh instance per run");
    }
```

- [ ] **Step 2: Update every existing call site to the factory constructor**

Single-line form. Run from the repo root (Git Bash):

```bash
grep -rl "new WizardModuleRegistry(\[" DiffusionNexus.Installer.Tests --include=*.cs | xargs sed -i 's/new WizardModuleRegistry(\[/new WizardModuleRegistry(() => [/g'
```

Two files use the multi-line `new(` form and need a manual edit:

`Wizard/CapabilityAgreementTests.cs`:
```csharp
    private static WizardModuleRegistry Registry(params LamaCppWheel[] wheels) => new(() =>
    [
        new InstallFolderModule(Settings(), new PreInstallationService()),
```

`Wizard/RealCatalogInstallabilityTests.cs`:
```csharp
    private WizardModuleRegistry ProductionRegistry() => new(() =>
    [
        new InstallFolderModule(Settings(), new PreInstallationService()),
```

Then confirm nothing else constructs it the old way:

```bash
grep -rn "WizardModuleRegistry(\[" DiffusionNexus.Installer.Tests DiffusionNexus.Installer.Core DiffusionNexus.Installer.Electron --include=*.cs --include=*.razor
```
Expected: no output.

- [ ] **Step 3: Run the registry tests to verify they fail**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~WizardModuleRegistryTests"`
Expected: build error — no constructor takes `Func<IEnumerable<IWizardModule>>`.

- [ ] **Step 4: Rewrite the registry around a factory**

Replace the class in `WizardModuleRegistry.cs` (keep the file's `using` and namespace lines):

```csharp
/// <summary>
/// Every capability module the app knows about. Also the installability gate: the gallery may only
/// offer a workload whose every <em>blocking</em> detected capability has a registered module behind it.
/// The gate considers only <see cref="WorkloadCapabilities.Blocking"/> — non-blocking capabilities
/// like CustomNodes and Accelerators are correct without a module, since the catalog's own
/// declarations (gitRepositories, installTriton) handle them.
/// <para>
/// Modules are per run. The factory is invoked once per <see cref="BuildPlanAsync"/>, so every plan
/// owns fresh instances and nothing a user answered for one workload can leak into the next. The
/// factory is also invoked once at construction to learn <see cref="SatisfiedCapabilities"/>, which
/// is a constant of the module types and must be answerable without building a plan.
/// </para>
/// </summary>
public sealed class WizardModuleRegistry
{
    private readonly Func<IEnumerable<IWizardModule>> _modules;

    public WizardModuleRegistry(Func<IEnumerable<IWizardModule>> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        _modules = modules;
        SatisfiedCapabilities = modules().Aggregate(WorkloadCapability.None, (acc, m) => acc | m.Satisfies);
    }

    /// <summary>The union of what the registered modules can handle.</summary>
    public WorkloadCapability SatisfiedCapabilities { get; }

    /// <summary>
    /// A workload is installable when nothing it needs is missing AND the pipeline would not refuse
    /// it outright. Deliberately asks WorkloadCapabilities.DetectBlocking rather than the modules
    /// themselves — a module that is not registered cannot be asked whether it applies.
    /// </summary>
    public bool IsInstallable(InstallationConfiguration workload)
    {
        var needed = WorkloadCapabilities.DetectBlocking(workload);
        return (needed & ~SatisfiedCapabilities) == WorkloadCapability.None
            && WorkloadCapabilities.DetectIncompatibility(workload) is null;
    }

    /// <summary>
    /// Builds fresh modules, initializes every one against the selection, then keeps the ones that
    /// apply. Modules are initialized before AppliesTo is read because applicability can depend on
    /// work done during initialization (GPU detection, for one).
    /// </summary>
    public async Task<WizardPlan> BuildPlanAsync(WizardSelection selection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var modules = _modules().ToList();

        // Same Stage-then-Order sequence WizardPlan.ToOptions uses for Contribute: a downstream
        // module's InitializeAsync can depend on an upstream module's answer.
        foreach (var module in modules.OrderBy(m => (int)m.Stage).ThenBy(m => m.Order))
            await module.InitializeAsync(selection, ct).ConfigureAwait(false);

        var byStage = modules
            .Where(m => m.AppliesTo(selection))
            .GroupBy(m => m.Stage)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<IWizardModule>)g.OrderBy(m => m.Order).ToList());

        // Confirm and Install always run. TryAdd so a module targeting one of them is kept.
        byStage.TryAdd(WizardStage.Confirm, []);
        byStage.TryAdd(WizardStage.Install, []);

        return new WizardPlan(selection, byStage);
    }
}
```

In `CoreServiceCollectionExtensions.AddInstallerCore`, replace the seven `AddSingleton<IWizardModule, ...>` lines and the registry line with:

```csharp
        // Transient on purpose: modules hold per-run answers. The registry's factory resolves a
        // fresh set for every plan, so a workload never sees another workload's answers.
        services.AddTransient<IWizardModule, InstallFolderModule>();
        services.AddTransient<IWizardModule, ComfyFoldersModule>();
        services.AddTransient<IWizardModule, GpuPreflightModule>();
        services.AddTransient<IWizardModule, VcRuntimeModule>();
        services.AddTransient<IWizardModule, LlamaCppModule>();
        services.AddTransient<IWizardModule, ShortcutsModule>();
        services.AddTransient<IWizardModule, DisclaimerModule>();

        services.AddSingleton<DevTools.LauncherScriptPreview>();
        services.AddSingleton<Gallery.GalleryBuilder>();
        services.AddSingleton(sp => new WizardModuleRegistry(() => sp.GetServices<IWizardModule>()));
```

Also change the first sentence of the class comment on `ModuleStateResetTests.cs` to: `Modules are per-run instances since slice 2, but InitializeAsync still resets everything it owns as a second line of defence; these tests keep that reset honest.` The tests themselves stay.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test DiffusionNexus.Installer.Tests`
Expected: all pass (153 − 1 removed + 3 added = 155).

- [ ] **Step 6: Commit and push**

```bash
git add -A DiffusionNexus.Installer.Core DiffusionNexus.Installer.Tests
git commit -m "refactor(wizard): per-run module instances via a factory-backed registry

Modules were DI singletons re-initialized per run, and a field any module
forgot to reset carried into the next workload. Transient modules plus a
factory the registry calls per BuildPlanAsync make every run fresh.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---
### Task 3: Location modules push their answers into the selection eagerly

The Content stage scans the install folder for models before Confirm ever runs `ToOptions()`, so the folder answers must be on `WizardSelection` as soon as the user types them, not only from `Contribute`.

**Files:**
- Modify: `DiffusionNexus.Installer.Core/Wizard/WizardSelection.cs`
- Modify: `DiffusionNexus.Installer.Core/Modules/InstallFolderModule.cs` (the `TargetFolder` property)
- Modify: `DiffusionNexus.Installer.Core/Modules/ComfyFoldersModule.cs` (`ModelBaseFolder`, `UseSavedFolderDefaults`, `InitializeAsync`)
- Test: `DiffusionNexus.Installer.Tests/Modules/SelectionSyncTests.cs`

**Interfaces:**
- Produces on `WizardSelection`: `string? ModelBaseFolder { get; set; }` and `IReadOnlyDictionary<string, string> FolderPathOverrides { get; set; }` (case-insensitive, empty by default). `TargetFolder` now reflects the module's value immediately.

- [ ] **Step 1: Write the failing tests**

```csharp
// DiffusionNexus.Installer.Tests/Modules/SelectionSyncTests.cs
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Settings;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

/// <summary>
/// The Content stage reads the install folder, model library and per-type overrides from the
/// selection while the user is still on that stage. Contribute only runs at Confirm, so the
/// Location modules must write those answers into the selection as they change.
/// </summary>
public class SelectionSyncTests
{
    private static IUserSettingsRepository Settings(UserSettings? settings = null)
    {
        var repo = new Mock<IUserSettingsRepository>();
        repo.Setup(r => r.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings ?? new UserSettings());
        return repo.Object;
    }

    private static WizardSelection Selection(RepositoryType type)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = type;
        return new WizardSelection { Workload = w };
    }

    [Fact]
    public async Task The_install_folder_reaches_the_selection_without_Contribute()
    {
        var module = new InstallFolderModule(Settings(), new PreInstallationService());
        var selection = Selection(RepositoryType.ComfyUI);
        await module.InitializeAsync(selection);

        module.TargetFolder = @"D:\Installs\Krea";

        selection.TargetFolder.Should().Be(@"D:\Installs\Krea");
    }

    [Fact]
    public async Task The_remembered_install_folder_reaches_the_selection_at_initialization()
    {
        var module = new InstallFolderModule(
            Settings(new UserSettings { DefaultTargetInstallFolder = @"D:\Installs" }), new PreInstallationService());
        var selection = Selection(RepositoryType.ComfyUI);

        await module.InitializeAsync(selection);

        selection.TargetFolder.Should().Be(@"D:\Installs");
    }

    [Fact]
    public async Task Model_library_and_overrides_reach_the_selection_for_a_comfy_workload()
    {
        var module = new ComfyFoldersModule(Settings(new UserSettings
        {
            DefaultModelBaseFolder = @"D:\Models",
            DefaultLorasFolder = @"E:\Loras",
        }));
        var selection = Selection(RepositoryType.ComfyUI);

        await module.InitializeAsync(selection);

        selection.ModelBaseFolder.Should().Be(@"D:\Models");
        selection.FolderPathOverrides.Should().ContainKey("loras").WhoseValue.Should().Be(@"E:\Loras");
    }

    [Fact]
    public async Task Opting_out_of_saved_folders_empties_the_selection_overrides()
    {
        var module = new ComfyFoldersModule(Settings(new UserSettings { DefaultLorasFolder = @"E:\Loras" }));
        var selection = Selection(RepositoryType.ComfyUI);
        await module.InitializeAsync(selection);
        selection.FolderPathOverrides.Should().NotBeEmpty();

        module.UseSavedFolderDefaults = false;

        selection.FolderPathOverrides.Should().BeEmpty();
    }

    [Fact]
    public async Task Clearing_the_model_library_yields_null_not_empty_string()
    {
        // ModelDestinationResolver treats null and "" the same, but the SDK options record
        // documents null as "no custom library"; the selection follows the record.
        var module = new ComfyFoldersModule(Settings(new UserSettings { DefaultModelBaseFolder = @"D:\Models" }));
        var selection = Selection(RepositoryType.ComfyUI);
        await module.InitializeAsync(selection);

        module.ModelBaseFolder = "   ";

        selection.ModelBaseFolder.Should().BeNull();
    }

    [Fact]
    public async Task A_workload_the_folders_module_does_not_apply_to_gets_no_library_in_its_selection()
    {
        // The registry initializes EVERY module, applicable or not. A saved library must not leak
        // into a Fooocus selection: the pipeline will never use it there, and a scan against it
        // would mark models "already downloaded" in a folder the install never reads.
        var module = new ComfyFoldersModule(Settings(new UserSettings { DefaultModelBaseFolder = @"D:\Models" }));
        var selection = Selection(RepositoryType.Fooocus);

        await module.InitializeAsync(selection);

        selection.ModelBaseFolder.Should().BeNull();
        selection.FolderPathOverrides.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~SelectionSyncTests"`
Expected: build error — `WizardSelection` has no `ModelBaseFolder`.

- [ ] **Step 3: Extend the selection and make the modules push into it**

Add to `WizardSelection`:

```csharp
    /// <summary>Custom model library root, or null for the install's own models folder. Written by ComfyFoldersModule.</summary>
    public string? ModelBaseFolder { get; set; }

    /// <summary>Per-type folder overrides in effect — empty when the user opted out. Written by ComfyFoldersModule.</summary>
    public IReadOnlyDictionary<string, string> FolderPathOverrides { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
```

In `InstallFolderModule`, replace `public string TargetFolder { get; set; } = string.Empty;` with:

```csharp
    private string _targetFolder = string.Empty;

    public string TargetFolder
    {
        get => _targetFolder;
        set
        {
            _targetFolder = value;
            // Pushed eagerly, not only from Contribute: the Content stage scans the install folder
            // for models already on disk before Confirm ever runs ToOptions.
            if (_selection is not null) _selection.TargetFolder = value;
        }
    }
```

(`InitializeAsync` already assigns `_selection` before it sets `TargetFolder`, so the remembered default is pushed too. Leave `Contribute` as it is.)

In `ComfyFoldersModule`:

```csharp
    private static readonly IReadOnlyDictionary<string, string> NoOverrides =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private WizardSelection? _selection;
    private string _modelBaseFolder = string.Empty;
    private bool _useSavedFolderDefaults = true;

    public string ModelBaseFolder
    {
        get => _modelBaseFolder;
        set { _modelBaseFolder = value; SyncSelection(); }
    }

    public bool UseSavedFolderDefaults
    {
        get => _useSavedFolderDefaults;
        set { _useSavedFolderDefaults = value; SyncSelection(); }
    }
```

Delete the old auto-properties `ModelBaseFolder` and `UseSavedFolderDefaults` (keep their XML comments on the new ones). In `InitializeAsync`, add `_selection = selection;` as the first line, and add `SyncSelection();` as the last line after `AdditionalFolders = ...`. Add the method:

```csharp
    /// <summary>
    /// Mirrors the answers the Content stage needs onto the selection. Only when this module applies:
    /// the registry initializes every module, and a saved library pushed into a Fooocus selection
    /// would make the model scan look in a folder that install never reads.
    /// </summary>
    private void SyncSelection()
    {
        if (_selection is null || !AppliesTo(_selection)) return;

        _selection.ModelBaseFolder = string.IsNullOrWhiteSpace(_modelBaseFolder) ? null : _modelBaseFolder;
        _selection.FolderPathOverrides = _useSavedFolderDefaults ? FolderPathOverrides : NoOverrides;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~SelectionSyncTests|FullyQualifiedName~ComfyFoldersModuleTests|FullyQualifiedName~OptionsFidelityTests|FullyQualifiedName~InstallSessionTests"`
Expected: all pass.

- [ ] **Step 5: Commit and push**

```bash
git add DiffusionNexus.Installer.Core/Wizard/WizardSelection.cs DiffusionNexus.Installer.Core/Modules/InstallFolderModule.cs DiffusionNexus.Installer.Core/Modules/ComfyFoldersModule.cs DiffusionNexus.Installer.Tests/Modules/SelectionSyncTests.cs
git commit -m "feat(wizard): location modules publish their answers to the selection eagerly

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---

### Task 4: VramProfileModule and its panel

**Files:**
- Create: `DiffusionNexus.Installer.Core/Modules/VramProfileModule.cs`
- Create: `DiffusionNexus.Installer.Electron/Components/Wizard/VramProfilePanel.razor`
- Modify: `DiffusionNexus.Installer.Electron/wwwroot/app.css` (add `.select`)
- Test: `DiffusionNexus.Installer.Tests/Modules/VramProfileModuleTests.cs`, `DiffusionNexus.Installer.Tests/Components/VramProfilePanelTests.cs`

**Interfaces:**
- Consumes: `VramTiers.Parse` (Task 1).
- Produces: `VramProfileModule` — `Id "vram-profile"`, `Stage Content`, `Order 0`, `Satisfies VramProfile`; `IReadOnlyList<int> Tiers`; `int SelectedTier { get; set; }` (setter also writes `WizardSelection.SelectedVramProfile`). Panel `VramProfilePanel` with `[Parameter] VramProfileModule Module`, `[Parameter] EventCallback Changed`.

- [ ] **Step 1: Write the failing module tests**

```csharp
// DiffusionNexus.Installer.Tests/Modules/VramProfileModuleTests.cs
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

public class VramProfileModuleTests
{
    private static WizardSelection Selection(string profiles)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Vram.VramProfiles = profiles;
        return new WizardSelection { Workload = w };
    }

    [Fact]
    public async Task Offers_exactly_the_declared_tiers_with_the_lowest_preselected()
    {
        // Decision 4: ideogram-4-0 declares 24,32 -- the dropdown must not pad in 8/12/16.
        var module = new VramProfileModule();
        var selection = Selection("24,32");

        await module.InitializeAsync(selection);

        module.Tiers.Should().Equal(24, 32);
        module.SelectedTier.Should().Be(24);
        selection.SelectedVramProfile.Should().Be(24, "the selection is what ModelSelection reads");
    }

    [Fact]
    public async Task Changing_the_tier_updates_the_selection_and_the_draft()
    {
        var module = new VramProfileModule();
        var selection = Selection("8,12,16,24,32");
        await module.InitializeAsync(selection);

        module.SelectedTier = 16;

        selection.SelectedVramProfile.Should().Be(16);
        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);
        draft.SelectedVramProfile.Should().Be(16);
    }

    [Fact]
    public void Applies_only_when_the_workload_parses_to_at_least_one_tier()
    {
        // Stateless on purpose: the agreement test calls AppliesTo without InitializeAsync.
        var module = new VramProfileModule();

        module.AppliesTo(Selection("8,12")).Should().BeTrue();
        module.AppliesTo(Selection("")).Should().BeFalse();
        module.AppliesTo(Selection("abc")).Should().BeFalse("Detect uses the same parser and says no tier");
    }

    [Fact]
    public async Task A_workload_without_tiers_contributes_zero_which_the_sdk_treats_as_no_filtering()
    {
        // Decision 5: no tiers means every declared model downloads.
        var module = new VramProfileModule();
        var selection = Selection("");
        await module.InitializeAsync(selection);

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);

        module.Tiers.Should().BeEmpty();
        draft.SelectedVramProfile.Should().Be(0);
        selection.SelectedVramProfile.Should().Be(0);
    }

    [Fact]
    public async Task Validation_never_blocks()
    {
        var module = new VramProfileModule();
        await module.InitializeAsync(Selection("8,12"));

        module.Validate().IsValid.Should().BeTrue("a preselected tier cannot be unanswered");
    }

    [Fact]
    public async Task Reinitializing_for_another_workload_reselects_that_workloads_lowest_tier()
    {
        var module = new VramProfileModule();
        await module.InitializeAsync(Selection("8,12,16"));
        module.SelectedTier = 16;

        var next = Selection("24,32");
        await module.InitializeAsync(next);

        module.SelectedTier.Should().Be(24);
        next.SelectedVramProfile.Should().Be(24);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~VramProfileModuleTests"`
Expected: build error — `VramProfileModule` does not exist.

- [ ] **Step 3: Write the module**

```csharp
// DiffusionNexus.Installer.Core/Modules/VramProfileModule.cs
using DiffusionNexus.Installer.Core.Wizard;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>
/// Which VRAM tier the install targets. The tier drives which per-tier model variants the SDK
/// downloads (ModelDownloadStepHandler via VramProfileHelper.SelectBestMatchingLinks) and what
/// the disk-space estimate counts. Only the tiers the workload declares are offered, lowest first
/// and preselected -- 1.x behaviour, and the user's explicit choice: no auto-detection.
/// </summary>
public sealed class VramProfileModule : IWizardModule
{
    private WizardSelection? _selection;
    private int _selectedTier;

    public string Id => "vram-profile";
    public WizardStage Stage => WizardStage.Content;
    public int Order => 0;
    public WorkloadCapability Satisfies => WorkloadCapability.VramProfile;

    /// <summary>Ascending, distinct, from the catalog. Empty when the workload declares none.</summary>
    public IReadOnlyList<int> Tiers { get; private set; } = [];

    /// <summary>
    /// Chosen tier in GB, 0 when there are no tiers. Written through to the selection on every
    /// change so ModelSelection reads the value, never this module.
    /// </summary>
    public int SelectedTier
    {
        get => _selectedTier;
        set
        {
            _selectedTier = value;
            if (_selection is not null) _selection.SelectedVramProfile = value;
        }
    }

    /// <summary>Stateless: the same parser the gate uses, so Detect and AppliesTo agree by construction.</summary>
    public bool AppliesTo(WizardSelection selection) =>
        VramTiers.Parse(selection.Workload.Vram.VramProfiles).Count > 0;

    public Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        _selection = selection;
        Tiers = VramTiers.Parse(selection.Workload.Vram.VramProfiles);
        SelectedTier = Tiers.Count > 0 ? Tiers[0] : 0;
        return Task.CompletedTask;
    }

    public void Contribute(InstallationOptionsDraft draft) => draft.SelectedVramProfile = SelectedTier;

    public ModuleValidation Validate() => ModuleValidation.Ok();
}
```

- [ ] **Step 4: Run the module tests**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~VramProfileModuleTests"`
Expected: all pass.

- [ ] **Step 5: Write the failing panel tests**

```csharp
// DiffusionNexus.Installer.Tests/Components/VramProfilePanelTests.cs
using Bunit;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class VramProfilePanelTests : BunitContext
{
    private static async Task<(VramProfileModule Module, WizardSelection Selection)> ModuleAsync(string profiles)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Vram.VramProfiles = profiles;
        var selection = new WizardSelection { Workload = w };
        var module = new VramProfileModule();
        await module.InitializeAsync(selection);
        return (module, selection);
    }

    [Fact]
    public async Task Renders_only_the_declared_tiers_with_the_lowest_selected()
    {
        var (module, _) = await ModuleAsync("24,32");

        var cut = Render<VramProfilePanel>(p => p.Add(x => x.Module, module));

        cut.FindAll("option").Select(o => o.TextContent.Trim()).Should().Equal("24 GB", "32 GB");
        cut.Find("select").GetAttribute("value").Should().Be("24");
    }

    [Fact]
    public async Task Picking_a_tier_updates_the_module_and_raises_Changed()
    {
        var (module, selection) = await ModuleAsync("8,12,16");
        var changed = false;

        var cut = Render<VramProfilePanel>(p => p
            .Add(x => x.Module, module)
            .Add(x => x.Changed, EventCallback.Factory.Create(this, () => changed = true)));

        cut.Find("select").Change("16");

        module.SelectedTier.Should().Be(16);
        selection.SelectedVramProfile.Should().Be(16);
        changed.Should().BeTrue("the sibling model panel rescans only when the page re-renders");
    }

    [Fact]
    public async Task A_value_outside_the_declared_tiers_is_ignored()
    {
        var (module, _) = await ModuleAsync("24,32");
        var cut = Render<VramProfilePanel>(p => p.Add(x => x.Module, module));

        cut.Find("select").Change("8");

        module.SelectedTier.Should().Be(24);
    }
}
```

- [ ] **Step 6: Run to verify they fail**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~VramProfilePanelTests"`
Expected: build error — `VramProfilePanel` does not exist.

- [ ] **Step 7: Write the panel and its style**

```razor
@* DiffusionNexus.Installer.Electron/Components/Wizard/VramProfilePanel.razor *@
@using DiffusionNexus.Installer.Core.Modules

<section class="panel">
    <h2>Graphics card memory</h2>
    <p class="panel-hint">
        Pick how much VRAM your graphics card has. This workload ships different model variants per
        tier, and the tier you pick decides which ones are downloaded.
    </p>
    <select class="select" value="@Module.SelectedTier" @onchange="OnTierChanged">
        @foreach (var tier in Module.Tiers)
        {
            <option value="@tier">@tier GB</option>
        }
    </select>
</section>

@code {
    [Parameter, EditorRequired] public VramProfileModule Module { get; set; } = default!;

    /// <summary>
    /// Raised after any edit. The parent owns the Next button and the sibling model panel, and
    /// Blazor re-renders only the component that owns a handler.
    /// </summary>
    [Parameter] public EventCallback Changed { get; set; }

    private async Task OnTierChanged(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out var tier) && Module.Tiers.Contains(tier))
            Module.SelectedTier = tier;

        await Changed.InvokeAsync();
    }
}
```

Append to `wwwroot/app.css`, directly after the `.path-row input:focus` rule:

```css
.select {
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: 6px;
    color: var(--text);
    padding: .5rem .7rem;
    font-family: inherit;
    font-size: .9rem;
    min-width: 10rem;
}

.select:focus {
    outline: none;
    border-color: var(--accent);
}
```

- [ ] **Step 8: Run the panel tests**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~VramProfilePanelTests"`
Expected: all pass.

- [ ] **Step 9: Commit and push**

```bash
git add DiffusionNexus.Installer.Core/Modules/VramProfileModule.cs DiffusionNexus.Installer.Electron/Components/Wizard/VramProfilePanel.razor DiffusionNexus.Installer.Electron/wwwroot/app.css DiffusionNexus.Installer.Tests/Modules/VramProfileModuleTests.cs DiffusionNexus.Installer.Tests/Components/VramProfilePanelTests.cs
git commit -m "feat(wizard): VRAM tier module and panel

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---
### Task 5: ModelPresenceScanner — one scan for markers and pre-flight

**Files:**
- Create: `DiffusionNexus.Installer.Core/Content/RepositoryPaths.cs`
- Create: `DiffusionNexus.Installer.Core/Content/ModelPresenceScanner.cs`
- Create: `DiffusionNexus.Installer.Tests/Support/EmbeddedCatalog.cs`
- Test: `DiffusionNexus.Installer.Tests/Content/RepositoryPathsTests.cs`, `DiffusionNexus.Installer.Tests/Content/ModelPresenceScannerTests.cs`, `DiffusionNexus.Installer.Tests/Content/ScannerPipelineAgreementTests.cs`

**Interfaces:**
- Produces:
  - `RepositoryPaths.Resolve(InstallationConfiguration workload, string targetFolder) : string`
  - `record ModelScanRequest(InstallationConfiguration Workload, string RepositoryPath, string? ModelBaseFolder, IReadOnlyDictionary<string,string> FolderPathOverrides, int SelectedVramGb)`
  - `record ModelFileTarget(ModelDownload Model, string Url, string DestinationDirectory, string FileName, string? ExistingPath)`
  - `record ModelPresence(Guid ModelId, bool AllPartsPresent, string? ExistingPath, IReadOnlyList<ModelFileTarget> Targets)`
  - `interface IModelPresenceScanner { IReadOnlyList<ModelPresence> Scan(ModelScanRequest request); }` and `ModelPresenceScanner : IModelPresenceScanner`
  - `EmbeddedCatalog.LoadAsync() : Task<(string Directory, IReadOnlyList<InstallationConfiguration> Workloads)>` and `EmbeddedCatalog.Delete(string directory)` for tests.

- [ ] **Step 1: Write the failing RepositoryPaths test**

```csharp
// DiffusionNexus.Installer.Tests/Content/RepositoryPathsTests.cs
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Content;

public class RepositoryPathsTests
{
    private static InstallationConfiguration Workload(string url)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Repository.RepositoryUrl = url;
        return w;
    }

    [Fact]
    public void The_repository_lands_in_a_folder_named_after_the_repo_under_the_install_folder()
        => RepositoryPaths.Resolve(Workload("https://github.com/comfyanonymous/ComfyUI.git"), @"C:\AI")
            .Should().Be(@"C:\AI\ComfyUI");

    [Fact]
    public void An_install_folder_already_named_after_the_repo_is_not_nested_twice()
    {
        // InstallationOrchestrator normalizes this way before the pipeline runs; a scan that did
        // not would look in C:\AI\ComfyUI\ComfyUI and mark every model as missing.
        RepositoryPaths.Resolve(Workload("https://github.com/comfyanonymous/ComfyUI"), @"C:\AI\ComfyUI")
            .Should().Be(@"C:\AI\ComfyUI");
    }

    [Fact]
    public void A_workload_without_a_repository_url_uses_the_install_folder_itself()
        => RepositoryPaths.Resolve(Workload(""), @"C:\AI").Should().Be(@"C:\AI");
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~RepositoryPathsTests"`
Expected: build error — `RepositoryPaths` does not exist.

- [ ] **Step 3: Write RepositoryPaths**

```csharp
// DiffusionNexus.Installer.Core/Content/RepositoryPaths.cs
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;

namespace DiffusionNexus.Installer.Core.Content;

/// <summary>
/// Where the main repository will land for an install folder — derived exactly the way
/// InstallationOrchestrator (NormalizeTargetDirectory) and InstallationContext.GetRepositoryPath
/// derive it, so a pre-install scan looks in the folder the pipeline will actually write to.
/// </summary>
public static class RepositoryPaths
{
    public static string Resolve(InstallationConfiguration workload, string targetFolder)
    {
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);

        var url = workload.Repository.RepositoryUrl;
        var normalizedTarget = PathNormalizer.NormalizeTargetDirectory(
            targetFolder,
            url,
            workload.Repository.Type == RepositoryType.AIToolkit ? "AI-Toolkit" : null);

        return Path.Combine(normalizedTarget, PathNormalizer.GetRepositoryName(url));
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~RepositoryPathsTests"`
Expected: 3 pass.

- [ ] **Step 5: Write the failing scanner tests**

```csharp
// DiffusionNexus.Installer.Tests/Content/ModelPresenceScannerTests.cs
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Models.Enums;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Content;

/// <summary>
/// One scan replaces 1.x's two hand-synced copies (CheckExistingModels for display and
/// BuildExistingModelCandidates for pre-flight). Filesystem cases use a temp folder as the
/// repository path and a relative model destination under it.
/// </summary>
public sealed class ModelPresenceScannerTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"dn-scan-{Guid.NewGuid():N}");
    private readonly ModelPresenceScanner _scanner = new();

    private static readonly IReadOnlyDictionary<string, string> NoOverrides =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ModelPresenceScannerTests() => Directory.CreateDirectory(_repo);

    private static InstallationConfiguration Workload(params ModelDownload[] models)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.ModelDownloads.AddRange(models);
        return w;
    }

    private static ModelDownload Model(string name, string destination, params ModelDownloadLink[] links)
    {
        var m = new ModelDownload { Name = name, Destination = destination };
        m.DownloadLinks.AddRange(links);
        return m;
    }

    private static ModelDownloadLink Link(string url, VramProfile? vram = null) =>
        new() { Url = url, VramProfile = vram };

    private ModelScanRequest Request(InstallationConfiguration workload, int tier = 0) =>
        new(workload, _repo, null, NoOverrides, tier);

    private string Touch(string relative)
    {
        var path = Path.Combine(_repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void A_file_in_the_destination_marks_the_model_present()
    {
        var expected = Touch(@"models\vae\ae.safetensors");
        var model = Model("VAE", @"models\vae", Link("https://host.invalid/files/ae.safetensors"));

        var presence = _scanner.Scan(Request(Workload(model))).Single();

        presence.AllPartsPresent.Should().BeTrue();
        presence.ExistingPath.Should().Be(expected);
        presence.Targets.Single().FileName.Should().Be("ae.safetensors");
        presence.Targets.Single().DestinationDirectory.Should().Be(Path.Combine(_repo, @"models\vae"));
    }

    [Fact]
    public void A_file_filed_into_a_subfolder_still_counts()
    {
        // Users sort models into subfolders ("Wan 2.2\..."); 1.x searched recursively and so do we.
        Touch(@"models\unet\Wan 2.2\wan.gguf");
        var model = Model("Wan", @"models\unet", Link("https://host.invalid/wan.gguf"));

        _scanner.Scan(Request(Workload(model))).Single().AllPartsPresent.Should().BeTrue();
    }

    [Fact]
    public void A_multi_part_model_with_one_part_missing_is_not_present()
    {
        Touch(@"models\clip\part1.safetensors");
        var model = Model("CLIP", @"models\clip",
            Link("https://host.invalid/part1.safetensors"),
            Link("https://host.invalid/part2.safetensors"));

        var presence = _scanner.Scan(Request(Workload(model))).Single();

        presence.AllPartsPresent.Should().BeFalse();
        presence.ExistingPath.Should().BeNull();
        presence.Targets.Should().HaveCount(2);
        presence.Targets.Count(t => t.ExistingPath is not null).Should().Be(1, "the pre-flight verifies the part that exists");
    }

    [Fact]
    public void Only_the_links_the_pipeline_would_download_at_the_tier_are_targets()
    {
        var model = Model("Tiered", @"models\unet",
            Link("https://host.invalid/q8.gguf", VramProfile.VRAM_8GB),
            Link("https://host.invalid/q16.gguf", VramProfile.VRAM_16GB));

        var atEight = _scanner.Scan(Request(Workload(model), tier: 8)).Single();
        var unfiltered = _scanner.Scan(Request(Workload(model), tier: 0)).Single();

        atEight.Targets.Select(t => t.FileName).Should().Equal("q8.gguf");
        unfiltered.Targets.Select(t => t.FileName).Should().Equal("q8.gguf", "q16.gguf");
    }

    [Fact]
    public void A_link_less_model_falls_back_to_its_url_and_honours_the_model_level_tier()
    {
        var model = new ModelDownload { Name = "Direct", Destination = @"models\x", Url = "https://host.invalid/big.safetensors", VramProfile = VramProfile.VRAM_16GB };

        _scanner.Scan(Request(Workload(model), tier: 8)).Single().Targets.Should().BeEmpty("16 GB does not fit 8 GB");
        _scanner.Scan(Request(Workload(model), tier: 0)).Single().Targets.Single().FileName.Should().Be("big.safetensors");
    }

    [Fact]
    public void A_model_with_neither_links_nor_url_has_no_targets_and_is_not_present()
    {
        var model = new ModelDownload { Name = "Empty", Destination = @"models\x" };

        var presence = _scanner.Scan(Request(Workload(model))).Single();

        presence.Targets.Should().BeEmpty();
        presence.AllPartsPresent.Should().BeFalse();
    }

    [Fact]
    public void A_link_destination_placeholder_resolves_under_the_repository_like_the_pipeline()
    {
        var link = Link("https://host.invalid/lora.safetensors");
        link.Destination = @"{RepositoryPath}\models\loras";
        var model = Model("LoRA", @"models\x", link);

        _scanner.Scan(Request(Workload(model))).Single().Targets.Single().DestinationDirectory
            .Should().Be(Path.Combine(_repo, @"models\loras"));
    }

    [Fact]
    public void Disabled_models_and_disabled_links_are_ignored()
    {
        var disabledModel = Model("Off", @"models\x", Link("https://host.invalid/a.bin"));
        disabledModel.Enabled = false;
        var disabledLink = Link("https://host.invalid/b.bin");
        disabledLink.Enabled = false;
        var model = Model("On", @"models\x", disabledLink, Link("https://host.invalid/c.bin"));

        var results = _scanner.Scan(Request(Workload(disabledModel, model)));

        results.Should().ContainSingle().Which.Targets.Select(t => t.FileName).Should().Equal("c.bin");
    }

    [Fact]
    public void A_destination_that_is_a_file_rather_than_a_folder_counts_as_absent_without_throwing()
    {
        Touch(@"models\notafolder");
        var model = Model("Odd", @"models\notafolder", Link("https://host.invalid/m.bin"));

        var act = () => _scanner.Scan(Request(Workload(model)));

        act.Should().NotThrow();
        act().Single().AllPartsPresent.Should().BeFalse();
    }

    [Fact]
    public void A_url_with_no_file_name_yields_no_target()
    {
        var model = Model("Bare", @"models\x", Link("https://host.invalid/"));

        _scanner.Scan(Request(Workload(model))).Single().Targets.Should().BeEmpty();
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
```

- [ ] **Step 6: Run to verify they fail**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~ModelPresenceScannerTests"`
Expected: build error — `ModelPresenceScanner` does not exist.

- [ ] **Step 7: Write the scanner**

```csharp
// DiffusionNexus.Installer.Core/Content/ModelPresenceScanner.cs
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services;
using PipelineVram = DiffusionNexus.Installer.SDK.Services.Installation.Utilities.VramProfileHelper;

namespace DiffusionNexus.Installer.Core.Content;

/// <param name="RepositoryPath">Where the main repository will be — see <see cref="RepositoryPaths"/>.</param>
/// <param name="SelectedVramGb">0 means no tier filtering, exactly as the SDK reads it.</param>
public sealed record ModelScanRequest(
    InstallationConfiguration Workload,
    string RepositoryPath,
    string? ModelBaseFolder,
    IReadOnlyDictionary<string, string> FolderPathOverrides,
    int SelectedVramGb);

/// <summary>One file the install would write for a model, and whether it is already there.</summary>
public sealed record ModelFileTarget(
    ModelDownload Model,
    string Url,
    string DestinationDirectory,
    string FileName,
    string? ExistingPath);

/// <param name="AllPartsPresent">True only when every target's file exists — a half-downloaded multi-link model is not "already downloaded".</param>
public sealed record ModelPresence(
    Guid ModelId,
    bool AllPartsPresent,
    string? ExistingPath,
    IReadOnlyList<ModelFileTarget> Targets);

public interface IModelPresenceScanner
{
    /// <summary>One entry per enabled model, in catalog order. Never throws on filesystem trouble.</summary>
    IReadOnlyList<ModelPresence> Scan(ModelScanRequest request);
}

/// <summary>
/// Resolves, for each enabled model, the files the pipeline would write at the selected tier and
/// whether they already exist. 1.x carried this logic twice under a "KEEP IN LOCKSTEP" comment —
/// once for the "already downloaded" markers, once for pre-install verification. Both read this.
/// <para>
/// Mirrors ModelDownloadStepHandler exactly: destination via ModelDestinationResolver, link
/// selection via the Services VramProfileHelper (the class the handler itself calls), per-link
/// destination overrides via the same placeholder rules as the handler's ResolvePath.
/// </para>
/// </summary>
public sealed class ModelPresenceScanner : IModelPresenceScanner
{
    public IReadOnlyList<ModelPresence> Scan(ModelScanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var overrides = new Dictionary<string, string>(request.FolderPathOverrides, StringComparer.OrdinalIgnoreCase);
        var results = new List<ModelPresence>();

        foreach (var model in request.Workload.ModelDownloads.Where(m => m.Enabled))
        {
            var targets = TargetsFor(request, model, overrides);
            var allPresent = targets.Count > 0 && targets.All(t => t.ExistingPath is not null);
            results.Add(new ModelPresence(model.Id, allPresent, allPresent ? targets[^1].ExistingPath : null, targets));
        }

        return results;
    }

    private static List<ModelFileTarget> TargetsFor(ModelScanRequest request, ModelDownload model, Dictionary<string, string> overrides)
    {
        var modelDestination = ModelDestinationResolver.Resolve(
            request.Workload, model, request.RepositoryPath, request.ModelBaseFolder, overrides);

        var enabledLinks = model.DownloadLinks.Where(l => l.Enabled).ToList();

        if (enabledLinks.Count == 0)
        {
            // The handler's fallback: the model's own URL, subject to the model-level tier.
            if (string.IsNullOrWhiteSpace(model.Url)) return [];

            if (request.SelectedVramGb > 0 && !PipelineVram.VramProfileFitsSelection(model.VramProfile, request.SelectedVramGb))
                return [];

            return Target(model, model.Url, modelDestination) is { } single ? [single] : [];
        }

        var links = PipelineVram.SelectBestMatchingLinks(enabledLinks, request.SelectedVramGb, null, model.Name);
        var targets = new List<ModelFileTarget>();

        foreach (var link in links)
        {
            if (string.IsNullOrWhiteSpace(link.Url)) continue;

            var destination = string.IsNullOrWhiteSpace(link.Destination)
                ? modelDestination
                : ResolveLinkDestination(link.Destination, request.RepositoryPath);

            if (Target(model, link.Url, destination) is { } target) targets.Add(target);
        }

        return targets;
    }

    /// <summary>Mirrors ModelDownloadStepHandler.ResolvePath: rooted as-is, placeholders, else under the repository.</summary>
    private static string ResolveLinkDestination(string path, string repositoryPath)
    {
        if (Path.IsPathRooted(path)) return path;

        var resolved = path
            .Replace("{RepositoryPath}", repositoryPath)
            .Replace("{Repository}", repositoryPath);

        return Path.IsPathRooted(resolved) ? resolved : Path.Combine(repositoryPath, resolved);
    }

    private static ModelFileTarget? Target(ModelDownload model, string url, string destinationDirectory)
    {
        var fileName = FileNameFromUrl(url);
        if (fileName is null) return null;

        return new ModelFileTarget(model, url, destinationDirectory, fileName, FindFile(destinationDirectory, fileName));
    }

    private static string? FileNameFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var name = Path.GetFileName(uri.LocalPath);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>Exact path first, then any subfolder. Anything the filesystem refuses counts as absent.</summary>
    private static string? FindFile(string directory, string fileName)
    {
        try
        {
            var exact = Path.Combine(directory, fileName);
            if (File.Exists(exact)) return exact;
            if (!Directory.Exists(directory)) return null;

            return Directory.GetFiles(directory, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 8: Run the scanner tests**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~ModelPresenceScannerTests"`
Expected: 10 pass.

- [ ] **Step 9: Write the shared embedded-catalog helper and the agreement test**

```csharp
// DiffusionNexus.Installer.Tests/Support/EmbeddedCatalog.cs
using System.IO.Compression;
using DiffusionNexus.Installer.Electron.Services;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Tests.Support;

/// <summary>
/// Reads the catalog.zip the Electron assembly embeds and ships — the same archive Program.cs
/// seeds a fresh install from — into a temp folder, so tests run against real catalog data
/// rather than synthetic fixtures.
/// </summary>
internal static class EmbeddedCatalog
{
    public static async Task<(string Directory, IReadOnlyList<InstallationConfiguration> Workloads)> LoadAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dn-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var electronAssembly = typeof(UpdaterLog).Assembly;
        using (var zipStream = electronAssembly.GetManifestResourceStream("catalog.zip")
            ?? throw new InvalidOperationException("catalog.zip is not embedded in the Electron assembly -- check the EmbeddedResource item in DiffusionNexus.Installer.Electron.csproj."))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
        {
            archive.ExtractToDirectory(dir);
        }

        // InstalledCatalogPath pinned under the temp dir: the default points at the real
        // %LocalAppData% catalog, and FileCatalog enumerates and deletes catalog.staging-*
        // folders there on load. A test must never touch a path outside its own temp folder.
        var options = new CatalogOptions
        {
            LocalOverridePath = dir,
            InstalledCatalogPath = Path.Combine(dir, "installed"),
        };
        ICatalog catalog = new FileCatalog(new CatalogLocator(options), options);
        var workloads = await catalog.GetWorkloadsAsync();

        return (dir, workloads);
    }

    public static void Delete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
```

```csharp
// DiffusionNexus.Installer.Tests/Content/ScannerPipelineAgreementTests.cs
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Xunit;
using PipelineVram = DiffusionNexus.Installer.SDK.Services.Installation.Utilities.VramProfileHelper;

namespace DiffusionNexus.Installer.Tests.Content;

/// <summary>
/// The scanner decides which files the wizard checks and verifies; the pipeline decides which
/// files it downloads. For every real catalog workload and every tier it declares (plus 0), the
/// two must name the same links -- otherwise the dialog verifies files the install never writes.
/// </summary>
public sealed class ScannerPipelineAgreementTests : IAsyncLifetime
{
    private string _dir = string.Empty;
    private IReadOnlyList<DiffusionNexus.Installer.SDK.Models.Configuration.InstallationConfiguration> _workloads = [];

    public async Task InitializeAsync() => (_dir, _workloads) = await EmbeddedCatalog.LoadAsync();

    public Task DisposeAsync()
    {
        EmbeddedCatalog.Delete(_dir);
        return Task.CompletedTask;
    }

    [Fact]
    public void Scanner_targets_equal_the_pipelines_link_selection_for_every_workload_and_tier()
    {
        var withModels = _workloads
            .Where(w => w.WorkloadTarget == WorkloadTargetType.Installer && w.ModelDownloads.Count > 0)
            .ToList();
        withModels.Should().NotBeEmpty("the real catalog has tiered packs; an empty set means a broken read");

        var scanner = new ModelPresenceScanner();
        var noOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var workload in withModels)
        {
            var tiers = VramTiers.Parse(workload.Vram.VramProfiles).Prepend(0).Distinct();

            foreach (var tier in tiers)
            {
                var presence = scanner.Scan(new ModelScanRequest(workload, @"C:\dn-agreement\repo", null, noOverrides, tier))
                    .ToDictionary(p => p.ModelId);

                foreach (var model in workload.ModelDownloads.Where(m => m.Enabled))
                {
                    var enabledLinks = model.DownloadLinks.Where(l => l.Enabled).ToList();
                    var expected = enabledLinks.Count == 0
                        ? (string.IsNullOrWhiteSpace(model.Url)
                            || (tier > 0 && !PipelineVram.VramProfileFitsSelection(model.VramProfile, tier))
                            ? [] : new[] { model.Url })
                        : PipelineVram.SelectBestMatchingLinks(enabledLinks, tier, null, model.Name).Select(l => l.Url).ToArray();

                    presence[model.Id].Targets.Select(t => t.Url).Should().Equal(expected,
                        $"'{workload.Name}' / '{model.Name}' at {tier} GB must scan exactly what the pipeline downloads");
                }
            }
        }
    }
}
```

- [ ] **Step 10: Run the agreement test**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~ScannerPipelineAgreementTests"`
Expected: 1 pass.

- [ ] **Step 11: Commit and push**

```bash
git add DiffusionNexus.Installer.Core/Content DiffusionNexus.Installer.Tests/Content DiffusionNexus.Installer.Tests/Support
git commit -m "feat(content): one model presence scanner in place of 1.x's lockstep pair

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---

### Task 6: Disk-space estimator and the bounded size resolver

**Files:**
- Create: `DiffusionNexus.Installer.Core/Content/DiskSpaceEstimator.cs`
- Create: `DiffusionNexus.Installer.Core/Content/ExistingModelVerification.cs`
- Modify: `DiffusionNexus.Installer.Core/CoreServiceCollectionExtensions.cs`
- Test: `DiffusionNexus.Installer.Tests/Content/DiskSpaceEstimatorTests.cs`, `DiffusionNexus.Installer.Tests/DependencyInjectionTests.cs`

**Interfaces:**
- Produces:
  - `record DiskSpaceRequest(InstallationConfiguration Workload, string TargetFolder, int SelectedVramGb, HashSet<Guid> ExcludedModelIds, HashSet<Guid> ExistingModelIds)`
  - `record DiskSpaceEstimate(long RequiredBytes, long AvailableBytes, bool IsSufficient, IReadOnlyList<string> UnknownSizeModels)` with `RequiredText` / `AvailableText`
  - `interface IDiskSpaceEstimator { Task<DiskSpaceEstimate> EstimateAsync(DiskSpaceRequest request, CancellationToken ct = default); }`
  - `interface IExistingModelVerifier { Task<IReadOnlyList<ExistingModelMismatch>> VerifyAsync(IReadOnlyList<ExistingModelCandidate> candidates, CancellationToken ct = default); }`
  - DI: `UrlSizeResolver` (singleton, own 10 s `HttpClient`), `IDiskSpaceEstimator`, `IExistingModelVerifier`, `IModelPresenceScanner`.

- [ ] **Step 1: Write the failing tests**

```csharp
// DiffusionNexus.Installer.Tests/Content/DiskSpaceEstimatorTests.cs
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Content;

public class DiskSpaceEstimatorTests
{
    [Fact]
    public async Task A_workload_without_models_is_estimated_offline_from_the_sdk_constants()
    {
        // No models means no HEAD requests, so this runs without network. The SDK charges a fixed
        // repo + venv + buffer estimate; the point here is the adapter's plumbing, not the numbers.
        var estimator = new SdkDiskSpaceEstimator(new UrlSizeResolver(new HttpClient { Timeout = TimeSpan.FromSeconds(1) }));
        var workload = new InstallationConfiguration();
        workload.Repository.Type = RepositoryType.ComfyUI;

        var estimate = await estimator.EstimateAsync(new DiskSpaceRequest(workload, Path.GetTempPath(), 0, [], []));

        estimate.RequiredBytes.Should().BePositive();
        estimate.AvailableBytes.Should().BePositive("the temp drive exists");
        estimate.UnknownSizeModels.Should().BeEmpty();
        estimate.RequiredText.Should().NotBeNullOrWhiteSpace();
    }
}
```

Append to `DependencyInjectionTests.cs` (add `using DiffusionNexus.Installer.Core.Content;` and `using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;`):

```csharp
    [Fact]
    public void Content_services_resolve_with_their_own_size_resolver()
    {
        using var provider = Build();

        provider.GetRequiredService<IModelPresenceScanner>().Should().NotBeNull();
        provider.GetRequiredService<IDiskSpaceEstimator>().Should().NotBeNull();
        provider.GetRequiredService<IExistingModelVerifier>().Should().NotBeNull();

        // One shared cache between the estimate and the pre-flight verification.
        provider.GetRequiredService<UrlSizeResolver>().Should().BeSameAs(provider.GetRequiredService<UrlSizeResolver>());
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~DiskSpaceEstimatorTests|FullyQualifiedName~DependencyInjectionTests"`
Expected: build error — `SdkDiskSpaceEstimator` does not exist.

- [ ] **Step 3: Write the adapters and register them**

```csharp
// DiffusionNexus.Installer.Core/Content/DiskSpaceEstimator.cs
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;

namespace DiffusionNexus.Installer.Core.Content;

/// <param name="ExistingModelIds">Models already on disk; their downloads are not counted.</param>
public sealed record DiskSpaceRequest(
    InstallationConfiguration Workload,
    string TargetFolder,
    int SelectedVramGb,
    HashSet<Guid> ExcludedModelIds,
    HashSet<Guid> ExistingModelIds);

public sealed record DiskSpaceEstimate(
    long RequiredBytes,
    long AvailableBytes,
    bool IsSufficient,
    IReadOnlyList<string> UnknownSizeModels)
{
    public string RequiredText => DiskSpaceRequirement.FormatBytes(RequiredBytes);
    public string AvailableText => DiskSpaceRequirement.FormatBytes(AvailableBytes);
}

/// <summary>Seam over the SDK's calculator so panels can be tested without HEAD requests.</summary>
public interface IDiskSpaceEstimator
{
    Task<DiskSpaceEstimate> EstimateAsync(DiskSpaceRequest request, CancellationToken ct = default);
}

public sealed class SdkDiskSpaceEstimator(UrlSizeResolver sizeResolver) : IDiskSpaceEstimator
{
    private readonly DiskSpaceCalculator _calculator = new(sizeResolver);

    public async Task<DiskSpaceEstimate> EstimateAsync(DiskSpaceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requirement = await _calculator.CalculateRequiredSpaceAsync(
            request.Workload,
            onlyModelDownload: false,
            request.SelectedVramGb,
            request.ExcludedModelIds,
            progress: null,
            ct,
            request.ExistingModelIds).ConfigureAwait(false);

        var validation = DiskSpaceCalculator.ValidateDiskSpace(request.TargetFolder, requirement);

        return new DiskSpaceEstimate(
            validation.RequiredBytes,
            validation.AvailableBytes,
            validation.HasSufficientSpace,
            requirement.UnknownSizeModels.ToList());
    }
}
```

```csharp
// DiffusionNexus.Installer.Core/Content/ExistingModelVerification.cs
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;

namespace DiffusionNexus.Installer.Core.Content;

/// <summary>Seam over the SDK's sealed ExistingModelVerifier so the pre-flight can be tested without network.</summary>
public interface IExistingModelVerifier
{
    Task<IReadOnlyList<ExistingModelMismatch>> VerifyAsync(
        IReadOnlyList<ExistingModelCandidate> candidates,
        CancellationToken ct = default);
}

public sealed class SdkExistingModelVerifier(UrlSizeResolver sizeResolver) : IExistingModelVerifier
{
    private readonly ExistingModelVerifier _verifier = new(sizeResolver);

    public Task<IReadOnlyList<ExistingModelMismatch>> VerifyAsync(
        IReadOnlyList<ExistingModelCandidate> candidates,
        CancellationToken ct = default) => _verifier.VerifyAsync(candidates, ct);
}
```

In `CoreServiceCollectionExtensions.AddInstallerCore`, add `using DiffusionNexus.Installer.Core.Content;` and `using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;` at the top, and insert before the `services.AddSingleton<DevTools.LauncherScriptPreview>();` line:

```csharp
        // Size lookups get their OWN bounded client, never the container's: AddInstallationServices
        // registers HttpClient with an infinite timeout on purpose (model downloads run for hours)
        // and documents that size-resolution consumers must construct their own. A HEAD against a
        // dead host on the shared client would hang the Content stage with no way out. One
        // resolver instance so the disk-space estimate and the pre-flight verification share a
        // size cache -- 1.x learned that a second resolver adds a full HEAD pass after Install.
        services.AddSingleton(_ => new UrlSizeResolver(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }));
        services.AddSingleton<IDiskSpaceEstimator, SdkDiskSpaceEstimator>();
        services.AddSingleton<IExistingModelVerifier, SdkExistingModelVerifier>();
        services.AddSingleton<IModelPresenceScanner, ModelPresenceScanner>();
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~DiskSpaceEstimatorTests|FullyQualifiedName~DependencyInjectionTests"`
Expected: all pass.

- [ ] **Step 5: Commit and push**

```bash
git add DiffusionNexus.Installer.Core/Content DiffusionNexus.Installer.Core/CoreServiceCollectionExtensions.cs DiffusionNexus.Installer.Tests/Content/DiskSpaceEstimatorTests.cs DiffusionNexus.Installer.Tests/DependencyInjectionTests.cs
git commit -m "feat(content): disk-space estimate and file verification seams over the SDK

Own 10 s HttpClient for size lookups; the container's client has an
infinite timeout by design.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---
### Task 7: ModelSelectionModule

**Files:**
- Create: `DiffusionNexus.Installer.Core/Modules/ModelSelectionModule.cs`
- Test: `DiffusionNexus.Installer.Tests/Modules/ModelSelectionModuleTests.cs`

**Interfaces:**
- Consumes: `IModelPresenceScanner`, `RepositoryPaths`, `IDiskSpaceEstimator` (Tasks 5, 6); `WizardSelection.ModelBaseFolder` / `FolderPathOverrides` / `SelectedVramProfile` (Tasks 3, 4).
- Produces: `ModelSelectionModule(IModelPresenceScanner scanner, IDiskSpaceEstimator estimator)` — `Id "model-selection"`, `Stage Content`, `Order 10`, `Satisfies ModelDownloads`; `IReadOnlyList<ModelRow> Rows`; `IReadOnlyList<ModelGroup> Groups`; `int SelectedCount`; `DiskSpaceEstimate? Estimate`; `string? EstimateError`; `int LastScannedTier`; `void SetSelected(Guid id, bool selected)`; `void RefreshPresence()`; `Task RefreshEstimateAsync(CancellationToken)`; `IReadOnlyList<ModelFileTarget> ExistingTargetsForSelectedModels()`; `void ApplyVerification(IEnumerable<string> forceRedownloadUrls, IEnumerable<string> trustedUrls)`. `ModelRow(Guid Id, string Name, string Group)` with `bool IsSelected` (settable), `bool IsExisting`, `string? ExistingPath`. `ModelGroup(string Name, IReadOnlyList<ModelRow> Rows)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// DiffusionNexus.Installer.Tests/Modules/ModelSelectionModuleTests.cs
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

public class ModelSelectionModuleTests
{
    private static readonly ModelDownload Vae = new() { Name = "VAE", Destination = @"models\vae", Url = "https://h.invalid/ae.safetensors" };
    private static readonly ModelDownload Unet = new() { Name = "UNet", Destination = @"models\unet", Url = "https://h.invalid/unet.gguf" };
    private static readonly ModelDownload Loose = new() { Name = "Loose", Url = "https://h.invalid/loose.bin" };

    private static WizardSelection Selection(params ModelDownload[] models)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Repository.RepositoryUrl = "https://github.com/comfyanonymous/ComfyUI";
        w.ModelDownloads.AddRange(models);
        return new WizardSelection { Workload = w, TargetFolder = @"C:\AI" };
    }

    private static Mock<IModelPresenceScanner> Scanner(params ModelPresence[] presences)
    {
        var scanner = new Mock<IModelPresenceScanner>();
        scanner.Setup(s => s.Scan(It.IsAny<ModelScanRequest>())).Returns(presences);
        return scanner;
    }

    private static ModelPresence Present(ModelDownload m, string path) =>
        new(m.Id, true, path, [new ModelFileTarget(m, m.Url, Path.GetDirectoryName(path)!, Path.GetFileName(path), path)]);

    private static ModelPresence Absent(ModelDownload m) =>
        new(m.Id, false, null, [new ModelFileTarget(m, m.Url, @"C:\AI\ComfyUI\models", "x", null)]);

    private static ModelSelectionModule Module(Mock<IModelPresenceScanner>? scanner = null, IDiskSpaceEstimator? estimator = null) =>
        new((scanner ?? Scanner()).Object, estimator ?? Mock.Of<IDiskSpaceEstimator>());

    [Fact]
    public async Task Every_enabled_model_is_a_ticked_row_grouped_by_destination_with_unassigned_last()
    {
        var disabled = new ModelDownload { Name = "Off", Enabled = false };
        var module = Module();

        await module.InitializeAsync(Selection(Unet, Loose, Vae, disabled));

        module.Rows.Select(r => r.Name).Should().Equal("UNet", "Loose", "VAE");
        module.Rows.Should().OnlyContain(r => r.IsSelected);
        module.Groups.Select(g => g.Name).Should().Equal(@"models\unet", @"models\vae", ModelSelectionModule.NotAssignedGroup);
        module.SelectedCount.Should().Be(3);
    }

    [Fact]
    public async Task Applies_whenever_the_workload_declares_models_even_if_all_are_disabled()
    {
        // Must mirror WorkloadCapabilities.Detect (Count > 0), or the gate demands a module that
        // then declines to render. The Enabled filter lives on the rows, not on applicability.
        var module = Module();
        var allDisabled = new ModelDownload { Name = "Off", Enabled = false };

        module.AppliesTo(Selection(allDisabled)).Should().BeTrue();
        module.AppliesTo(Selection()).Should().BeFalse();

        await module.InitializeAsync(Selection(allDisabled));
        module.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Unticked_rows_become_excluded_ids_and_nothing_else_does()
    {
        var module = Module();
        await module.InitializeAsync(Selection(Vae, Unet));

        module.SetSelected(Unet.Id, false);
        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);

        draft.ExcludedModelIds.Should().BeEquivalentTo([Unet.Id]);
        module.SelectedCount.Should().Be(1);
        module.Validate().IsValid.Should().BeTrue("installing without some models is a legitimate choice");
    }

    [Fact]
    public async Task Rows_are_marked_from_the_scan_and_the_scan_uses_the_selections_folders_and_tier()
    {
        var scanner = Scanner(Present(Vae, @"D:\Models\vae\ae.safetensors"), Absent(Unet));
        var module = Module(scanner);
        var selection = Selection(Vae, Unet);
        selection.ModelBaseFolder = @"D:\Models";
        selection.FolderPathOverrides = new Dictionary<string, string> { ["loras"] = @"E:\Loras" };
        selection.SelectedVramProfile = 12;

        await module.InitializeAsync(selection);

        module.Rows.Single(r => r.Id == Vae.Id).IsExisting.Should().BeTrue();
        module.Rows.Single(r => r.Id == Vae.Id).ExistingPath.Should().Be(@"D:\Models\vae\ae.safetensors");
        module.Rows.Single(r => r.Id == Unet.Id).IsExisting.Should().BeFalse();
        module.LastScannedTier.Should().Be(12);
        scanner.Verify(s => s.Scan(It.Is<ModelScanRequest>(r =>
            r.RepositoryPath == @"C:\AI\ComfyUI"
            && r.ModelBaseFolder == @"D:\Models"
            && r.FolderPathOverrides.ContainsKey("loras")
            && r.SelectedVramGb == 12)), Times.Once);
    }

    [Fact]
    public async Task No_install_folder_means_no_scan_and_no_markers()
    {
        var scanner = Scanner(Present(Vae, @"C:\x\ae.safetensors"));
        var module = Module(scanner);
        var selection = Selection(Vae);
        selection.TargetFolder = string.Empty;

        await module.InitializeAsync(selection);

        scanner.Verify(s => s.Scan(It.IsAny<ModelScanRequest>()), Times.Never);
        module.Rows.Single().IsExisting.Should().BeFalse();
    }

    [Fact]
    public async Task The_estimate_excludes_unticked_models_and_does_not_count_files_already_on_disk()
    {
        var estimator = new Mock<IDiskSpaceEstimator>();
        DiskSpaceRequest? seen = null;
        estimator.Setup(e => e.EstimateAsync(It.IsAny<DiskSpaceRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DiskSpaceRequest, CancellationToken>((r, _) => seen = r)
            .ReturnsAsync(new DiskSpaceEstimate(10, 20, true, []));
        var module = Module(Scanner(Present(Vae, @"C:\AI\ComfyUI\models\vae\ae.safetensors"), Absent(Unet)), estimator.Object);
        var selection = Selection(Vae, Unet);
        selection.SelectedVramProfile = 8;
        await module.InitializeAsync(selection);
        module.SetSelected(Unet.Id, false);

        await module.RefreshEstimateAsync();

        module.Estimate!.IsSufficient.Should().BeTrue();
        seen!.TargetFolder.Should().Be(@"C:\AI");
        seen.SelectedVramGb.Should().Be(8);
        seen.ExcludedModelIds.Should().BeEquivalentTo([Unet.Id]);
        seen.ExistingModelIds.Should().BeEquivalentTo([Vae.Id]);
    }

    [Fact]
    public async Task A_failing_estimate_is_reported_not_thrown()
    {
        var estimator = new Mock<IDiskSpaceEstimator>();
        estimator.Setup(e => e.EstimateAsync(It.IsAny<DiskSpaceRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));
        var module = Module(estimator: estimator.Object);
        await module.InitializeAsync(Selection(Vae));

        await module.RefreshEstimateAsync();

        module.Estimate.Should().BeNull();
        module.EstimateError.Should().Contain("offline");
    }

    [Fact]
    public async Task Existing_targets_for_selected_models_drive_the_preflight()
    {
        var module = Module(Scanner(Present(Vae, @"C:\AI\ComfyUI\models\vae\ae.safetensors"), Present(Unet, @"C:\AI\ComfyUI\models\unet\unet.gguf")));
        await module.InitializeAsync(Selection(Vae, Unet));
        module.SetSelected(Unet.Id, false);

        module.ExistingTargetsForSelectedModels().Select(t => t.Url).Should().Equal(Vae.Url,
            "an unticked model is never downloaded, so its file is never verified");
    }

    [Fact]
    public async Task Verification_decisions_reach_the_draft_keyed_by_url()
    {
        var module = Module();
        await module.InitializeAsync(Selection(Vae));

        module.ApplyVerification(["https://h.invalid/ae.safetensors"], ["https://h.invalid/other.bin"]);
        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);

        draft.ForceRedownloadUrls.Should().BeEquivalentTo(["https://h.invalid/ae.safetensors"]);
        draft.TrustedUrls.Should().BeEquivalentTo(["https://h.invalid/other.bin"]);
    }

    [Fact]
    public async Task Reinitializing_for_another_workload_starts_clean()
    {
        var module = Module();
        await module.InitializeAsync(Selection(Vae, Unet));
        module.SetSelected(Unet.Id, false);
        module.ApplyVerification(["https://h.invalid/ae.safetensors"], []);

        await module.InitializeAsync(Selection(Loose));

        module.Rows.Select(r => r.Name).Should().Equal("Loose");
        module.Rows.Should().OnlyContain(r => r.IsSelected);
        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);
        draft.ExcludedModelIds.Should().BeEmpty();
        draft.ForceRedownloadUrls.Should().BeEmpty();
        module.Estimate.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~ModelSelectionModuleTests"`
Expected: build error — `ModelSelectionModule` does not exist.

- [ ] **Step 3: Write the module**

```csharp
// DiffusionNexus.Installer.Core/Modules/ModelSelectionModule.cs
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Wizard;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>One model the workload declares. <see cref="Group"/> is the catalog destination, for display only.</summary>
public sealed class ModelRow(Guid id, string name, string group)
{
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public string Group { get; } = group;
    public bool IsSelected { get; set; } = true;
    public bool IsExisting { get; internal set; }
    public string? ExistingPath { get; internal set; }
}

public sealed record ModelGroup(string Name, IReadOnlyList<ModelRow> Rows);

/// <summary>
/// Which of the workload's models to download. 1.x behaviour: every enabled model listed and
/// ticked, grouped by destination, marked when already on disk, with a live disk-space estimate.
/// No tier annotation on the rows -- the tier silently drives the existence check and the
/// estimate, exactly as 1.x did (spec decision 2).
/// </summary>
public sealed class ModelSelectionModule(IModelPresenceScanner scanner, IDiskSpaceEstimator estimator) : IWizardModule
{
    public const string NotAssignedGroup = "Not assigned";

    private WizardSelection? _selection;
    private IReadOnlyList<ModelPresence> _presence = [];
    private readonly HashSet<string> _forceRedownloadUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _trustedUrls = new(StringComparer.OrdinalIgnoreCase);

    public string Id => "model-selection";
    public WizardStage Stage => WizardStage.Content;
    public int Order => 10;
    public WorkloadCapability Satisfies => WorkloadCapability.ModelDownloads;

    public IReadOnlyList<ModelRow> Rows { get; private set; } = [];

    /// <summary>Rows by catalog destination, "Not assigned" last. Presentation grouping only.</summary>
    public IReadOnlyList<ModelGroup> Groups => Rows
        .GroupBy(r => r.Group)
        .OrderBy(g => g.Key == NotAssignedGroup)
        .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
        .Select(g => new ModelGroup(g.Key, g.ToList()))
        .ToList();

    public int SelectedCount => Rows.Count(r => r.IsSelected);

    public DiskSpaceEstimate? Estimate { get; private set; }
    public string? EstimateError { get; private set; }

    /// <summary>Tier the last presence scan used; -1 before any scan. The panel rescans when the selection's tier differs.</summary>
    public int LastScannedTier { get; private set; } = -1;

    /// <summary>Mirrors Detect (Count > 0), NOT Any(Enabled): the gate and the module must agree.</summary>
    public bool AppliesTo(WizardSelection selection) => selection.Workload.ModelDownloads.Count > 0;

    public Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        _selection = selection;
        Rows = selection.Workload.ModelDownloads
            .Where(m => m.Enabled)
            .Select(m => new ModelRow(m.Id, m.Name, string.IsNullOrWhiteSpace(m.Destination) ? NotAssignedGroup : m.Destination))
            .ToList();
        _presence = [];
        _forceRedownloadUrls.Clear();
        _trustedUrls.Clear();
        Estimate = null;
        EstimateError = null;
        LastScannedTier = -1;

        RefreshPresence();
        return Task.CompletedTask;
    }

    public void SetSelected(Guid id, bool selected)
    {
        if (Rows.FirstOrDefault(r => r.Id == id) is { } row) row.IsSelected = selected;
    }

    /// <summary>Filesystem only, synchronous. Nothing to scan until an install folder is known.</summary>
    public void RefreshPresence()
    {
        if (_selection is null) return;

        var tier = _selection.SelectedVramProfile;
        LastScannedTier = tier;

        if (string.IsNullOrWhiteSpace(_selection.TargetFolder))
        {
            _presence = [];
            foreach (var row in Rows) { row.IsExisting = false; row.ExistingPath = null; }
            return;
        }

        _presence = scanner.Scan(new ModelScanRequest(
            _selection.Workload,
            RepositoryPaths.Resolve(_selection.Workload, _selection.TargetFolder),
            _selection.ModelBaseFolder,
            _selection.FolderPathOverrides,
            tier));

        var byId = _presence.ToDictionary(p => p.ModelId);
        foreach (var row in Rows)
        {
            var found = byId.TryGetValue(row.Id, out var presence) && presence.AllPartsPresent;
            row.IsExisting = found;
            row.ExistingPath = found ? presence!.ExistingPath : null;
        }
    }

    /// <summary>Network-bound (HEAD per URL). Failure is reported through <see cref="EstimateError"/>, never thrown.</summary>
    public async Task RefreshEstimateAsync(CancellationToken ct = default)
    {
        if (_selection is null || string.IsNullOrWhiteSpace(_selection.TargetFolder))
        {
            Estimate = null;
            return;
        }

        try
        {
            Estimate = await estimator.EstimateAsync(new DiskSpaceRequest(
                _selection.Workload,
                _selection.TargetFolder,
                _selection.SelectedVramProfile,
                Rows.Where(r => !r.IsSelected).Select(r => r.Id).ToHashSet(),
                Rows.Where(r => r.IsExisting).Select(r => r.Id).ToHashSet()), ct).ConfigureAwait(false);
            EstimateError = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Estimate = null;
            EstimateError = $"Could not estimate disk space: {ex.Message}";
        }
    }

    /// <summary>Files already on disk for ticked models — the pre-install verification's input.</summary>
    public IReadOnlyList<ModelFileTarget> ExistingTargetsForSelectedModels()
    {
        var selected = Rows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();
        return _presence
            .Where(p => selected.Contains(p.ModelId))
            .SelectMany(p => p.Targets)
            .Where(t => t.ExistingPath is not null)
            .ToList();
    }

    /// <summary>Records the mismatch dialog's answers. Keyed by URL, never by model id: a model can have several links and only some mismatch.</summary>
    public void ApplyVerification(IEnumerable<string> forceRedownloadUrls, IEnumerable<string> trustedUrls)
    {
        _forceRedownloadUrls.Clear();
        _trustedUrls.Clear();
        _forceRedownloadUrls.UnionWith(forceRedownloadUrls);
        _trustedUrls.UnionWith(trustedUrls);
    }

    public void Contribute(InstallationOptionsDraft draft)
    {
        draft.ExcludedModelIds.Clear();
        foreach (var row in Rows.Where(r => !r.IsSelected)) draft.ExcludedModelIds.Add(row.Id);

        draft.ForceRedownloadUrls.Clear();
        draft.ForceRedownloadUrls.UnionWith(_forceRedownloadUrls);
        draft.TrustedUrls.Clear();
        draft.TrustedUrls.UnionWith(_trustedUrls);
    }

    public ModuleValidation Validate() => ModuleValidation.Ok();
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~ModelSelectionModuleTests"`
Expected: 10 pass.

- [ ] **Step 5: Commit and push**

```bash
git add DiffusionNexus.Installer.Core/Modules/ModelSelectionModule.cs DiffusionNexus.Installer.Tests/Modules/ModelSelectionModuleTests.cs
git commit -m "feat(wizard): model selection module

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---

### Task 8: ModelSelectionPanel

**Files:**
- Create: `DiffusionNexus.Installer.Electron/Components/Wizard/ModelSelectionPanel.razor`
- Modify: `DiffusionNexus.Installer.Electron/wwwroot/app.css`
- Test: `DiffusionNexus.Installer.Tests/Components/ModelSelectionPanelTests.cs`

**Interfaces:**
- Consumes: `ModelSelectionModule` (Task 7), `WizardSelection`.
- Produces: `ModelSelectionPanel` with `[Parameter] ModelSelectionModule Module`, `[Parameter] WizardSelection Selection`, `[Parameter] EventCallback Changed`, `[Parameter] TimeSpan EstimateDebounce` (default 400 ms).

- [ ] **Step 1: Write the failing tests**

```csharp
// DiffusionNexus.Installer.Tests/Components/ModelSelectionPanelTests.cs
using Bunit;
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class ModelSelectionPanelTests : BunitContext
{
    private static readonly ModelDownload Vae = new() { Name = "VAE", Destination = @"models\vae", Url = "https://h.invalid/ae.safetensors" };
    private static readonly ModelDownload Unet = new() { Name = "UNet", Destination = @"models\unet", Url = "https://h.invalid/unet.gguf" };

    private static WizardSelection Selection(string tiers = "")
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Repository.RepositoryUrl = "https://github.com/comfyanonymous/ComfyUI";
        w.Vram.VramProfiles = tiers;
        w.ModelDownloads.AddRange([Vae, Unet]);
        return new WizardSelection { Workload = w, TargetFolder = @"C:\AI" };
    }

    private static Mock<IModelPresenceScanner> Scanner(bool vaePresent)
    {
        var scanner = new Mock<IModelPresenceScanner>();
        scanner.Setup(s => s.Scan(It.IsAny<ModelScanRequest>())).Returns(
        [
            new ModelPresence(Vae.Id, vaePresent, vaePresent ? @"C:\AI\ComfyUI\models\vae\ae.safetensors" : null, []),
            new ModelPresence(Unet.Id, false, null, []),
        ]);
        return scanner;
    }

    private static IDiskSpaceEstimator Estimator(bool sufficient = true)
    {
        var estimator = new Mock<IDiskSpaceEstimator>();
        estimator.Setup(e => e.EstimateAsync(It.IsAny<DiskSpaceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiskSpaceEstimate(3L * 1024 * 1024 * 1024, 40L * 1024 * 1024 * 1024, sufficient, []));
        return estimator.Object;
    }

    private IRenderedComponent<ModelSelectionPanel> RenderPanel(ModelSelectionModule module, WizardSelection selection, Action? onChanged = null) =>
        Render<ModelSelectionPanel>(p => p
            .Add(x => x.Module, module)
            .Add(x => x.Selection, selection)
            .Add(x => x.EstimateDebounce, TimeSpan.Zero)
            .Add(x => x.Changed, EventCallback.Factory.Create(this, () => onChanged?.Invoke())));

    [Fact]
    public async Task Lists_every_model_ticked_under_its_folder_and_marks_the_one_already_on_disk()
    {
        var module = new ModelSelectionModule(Scanner(vaePresent: true).Object, Estimator());
        var selection = Selection();
        await module.InitializeAsync(selection);

        var cut = RenderPanel(module, selection);

        cut.FindAll("h3").Select(h => h.TextContent.Trim()).Should().Equal(@"models\unet", @"models\vae");
        cut.FindAll("input[type=checkbox]").Should().HaveCount(2).And.OnlyContain(i => i.HasAttribute("checked"));
        cut.FindAll(".tag").Should().ContainSingle().Which.TextContent.Should().Contain("already downloaded");
        cut.Markup.Should().NotContain("variant", "spec decision 2: no tier annotation on rows");
    }

    [Fact]
    public async Task Unticking_a_model_updates_the_module_and_raises_Changed()
    {
        var module = new ModelSelectionModule(Scanner(vaePresent: false).Object, Estimator());
        var selection = Selection();
        await module.InitializeAsync(selection);
        var changed = false;

        var cut = RenderPanel(module, selection, () => changed = true);
        cut.FindAll("input[type=checkbox]")[0].Change(false);

        module.SelectedCount.Should().Be(1);
        changed.Should().BeTrue();
    }

    [Fact]
    public async Task Shows_the_disk_space_estimate_and_flags_a_shortfall()
    {
        var module = new ModelSelectionModule(Scanner(vaePresent: false).Object, Estimator(sufficient: false));
        var selection = Selection();
        await module.InitializeAsync(selection);

        var cut = RenderPanel(module, selection);

        cut.WaitForAssertion(() =>
        {
            cut.Find(".disk-space").TextContent.Should().Contain("Needs about");
            cut.Find(".disk-space").ClassList.Should().Contain("disk-space-bad");
        }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_tier_change_made_elsewhere_triggers_a_rescan_on_re_render()
    {
        // The VRAM panel is a sibling; its Changed re-renders the page, which re-renders this panel
        // with the same parameters. That render must notice the tier moved and rescan.
        var scanner = Scanner(vaePresent: false);
        var module = new ModelSelectionModule(scanner.Object, Estimator());
        var selection = Selection("8,12,16");
        selection.SelectedVramProfile = 8;
        await module.InitializeAsync(selection);

        var cut = RenderPanel(module, selection);
        var scansBefore = scanner.Invocations.Count(i => i.Method.Name == nameof(IModelPresenceScanner.Scan));

        selection.SelectedVramProfile = 16;
        cut.Render();

        module.LastScannedTier.Should().Be(16);
        scanner.Invocations.Count(i => i.Method.Name == nameof(IModelPresenceScanner.Scan)).Should().Be(scansBefore + 1);
    }

    [Fact]
    public async Task A_workload_whose_models_are_all_disabled_says_so()
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.ModelDownloads.Add(new ModelDownload { Name = "Off", Enabled = false });
        var selection = new WizardSelection { Workload = w, TargetFolder = @"C:\AI" };
        var module = new ModelSelectionModule(Scanner(false).Object, Estimator());
        await module.InitializeAsync(selection);

        var cut = RenderPanel(module, selection);

        cut.Markup.Should().Contain("disabled by its author");
        cut.FindAll("input[type=checkbox]").Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~ModelSelectionPanelTests"`
Expected: build error — `ModelSelectionPanel` does not exist.

- [ ] **Step 3: Write the panel and its styles**

```razor
@* DiffusionNexus.Installer.Electron/Components/Wizard/ModelSelectionPanel.razor *@
@using DiffusionNexus.Installer.Core.Modules
@using DiffusionNexus.Installer.Core.Wizard
@implements IDisposable

<section class="panel">
    <h2>Models</h2>
    <p class="panel-hint">
        Everything ticked is downloaded. Untick a model to skip it. Models already in their
        destination folder are marked and are not downloaded again.
    </p>

    @if (Module.Rows.Count == 0)
    {
        <p class="panel-hint">This workload's models are all disabled by its author, so nothing will be downloaded.</p>
    }

    @foreach (var group in Module.Groups)
    {
        <div class="model-group">
            <h3>@group.Name</h3>
            @foreach (var row in group.Rows)
            {
                <label class="checkbox">
                    <input type="checkbox" checked="@row.IsSelected" @onchange="e => OnRowChanged(row, e)" />
                    <span>@row.Name</span>
                    @if (row.IsExisting)
                    {
                        <span class="tag" title="@row.ExistingPath">already downloaded</span>
                    }
                </label>
            }
        </div>
    }

    <p class="@(Module.Estimate is { IsSufficient: false } ? "disk-space disk-space-bad" : "disk-space")">
        @if (_estimating)
        {
            <text>Estimating disk space...</text>
        }
        else if (Module.Estimate is { } estimate)
        {
            <text>Needs about @estimate.RequiredText, @estimate.AvailableText free on that drive.</text>
            @if (!estimate.IsSufficient)
            {
                <text> Not enough space.</text>
            }
            @if (estimate.UnknownSizeModels.Count > 0)
            {
                <text> (@estimate.UnknownSizeModels.Count file@(estimate.UnknownSizeModels.Count == 1 ? "" : "s") of unknown size not counted.)</text>
            }
        }
        else if (Module.EstimateError is { } error)
        {
            <text>@error</text>
        }
    </p>
</section>

@code {
    [Parameter, EditorRequired] public ModelSelectionModule Module { get; set; } = default!;

    /// <summary>The tier lives here; the panel rescans when it differs from the module's last scan.</summary>
    [Parameter, EditorRequired] public WizardSelection Selection { get; set; } = default!;

    /// <summary>
    /// Raised after any edit. The parent owns the Next button, and Blazor re-renders only the
    /// component that owns a handler.
    /// </summary>
    [Parameter] public EventCallback Changed { get; set; }

    /// <summary>Delay before a size estimate (HEAD per URL) runs after an edit. Tests pass zero.</summary>
    [Parameter] public TimeSpan EstimateDebounce { get; set; } = TimeSpan.FromMilliseconds(400);

    private CancellationTokenSource? _estimateCts;
    private bool _estimating;

    protected override void OnInitialized()
    {
        // The install folder may have changed since the module was initialized on the Location stage.
        Module.RefreshPresence();
        ScheduleEstimate();
    }

    protected override void OnParametersSet()
    {
        // A sibling panel edits the tier. Its Changed re-renders the page, which re-renders this
        // panel with the same parameters -- that render is when the moved tier becomes visible.
        if (Module.LastScannedTier != Selection.SelectedVramProfile)
        {
            Module.RefreshPresence();
            ScheduleEstimate();
        }
    }

    private async Task OnRowChanged(ModelRow row, ChangeEventArgs e)
    {
        Module.SetSelected(row.Id, (bool)(e.Value ?? false));
        ScheduleEstimate();
        await Changed.InvokeAsync();
    }

    /// <summary>Cancels the previous estimate and starts a debounced new one. Only the latest writes back.</summary>
    private void ScheduleEstimate()
    {
        _estimateCts?.Cancel();
        _estimateCts?.Dispose();
        var cts = new CancellationTokenSource();
        _estimateCts = cts;
        _estimating = true;
        _ = RunEstimateAsync(cts.Token);
    }

    private async Task RunEstimateAsync(CancellationToken ct)
    {
        try
        {
            if (EstimateDebounce > TimeSpan.Zero) await Task.Delay(EstimateDebounce, ct);
            await Module.RefreshEstimateAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested) return;

        _estimating = false;
        await InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        _estimateCts?.Cancel();
        _estimateCts?.Dispose();
    }
}
```

Append to `wwwroot/app.css`, directly after the `.checkbox input` rule:

```css
.model-group {
    margin-top: .75rem;
}

.model-group h3 {
    margin: 0 0 .25rem;
    font-size: .8rem;
    color: var(--muted);
    text-transform: uppercase;
    letter-spacing: .06em;
}

.tag {
    margin-left: .5rem;
    padding: .05rem .45rem;
    border-radius: 999px;
    background: #17332f;
    color: var(--accent);
    font-size: .72rem;
    white-space: nowrap;
}

.disk-space {
    margin: 1rem 0 0;
    font-size: .87rem;
    color: var(--muted);
}

.disk-space-bad {
    color: #ff8f8f;
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~ModelSelectionPanelTests"`
Expected: 5 pass.

- [ ] **Step 5: Commit and push**

```bash
git add DiffusionNexus.Installer.Electron/Components/Wizard/ModelSelectionPanel.razor DiffusionNexus.Installer.Electron/wwwroot/app.css DiffusionNexus.Installer.Tests/Components/ModelSelectionPanelTests.cs
git commit -m "feat(wizard): model selection panel with presence markers and disk-space estimate

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---
### Task 9: WorkflowSelectionModule and its panel

**Files:**
- Create: `DiffusionNexus.Installer.Core/Modules/WorkflowSelectionModule.cs`
- Create: `DiffusionNexus.Installer.Electron/Components/Wizard/WorkflowSelectionPanel.razor`
- Test: `DiffusionNexus.Installer.Tests/Modules/WorkflowSelectionModuleTests.cs`, `DiffusionNexus.Installer.Tests/Components/WorkflowSelectionPanelTests.cs`

**Interfaces:**
- Produces: `WorkflowSelectionModule` — `Id "workflow-selection"`, `Stage Content`, `Order 20`, `Satisfies Workflows`; `IReadOnlyList<WorkflowRow> Rows`; `int SelectedCount`; `void SetSelected(Guid id, bool selected)`. `WorkflowRow(Guid Id, string Name, string Version)` with `bool IsSelected`. Panel `WorkflowSelectionPanel` with `[Parameter] WorkflowSelectionModule Module`, `[Parameter] EventCallback Changed`.

- [ ] **Step 1: Write the failing tests**

```csharp
// DiffusionNexus.Installer.Tests/Modules/WorkflowSelectionModuleTests.cs
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

public class WorkflowSelectionModuleTests
{
    private static WizardSelection Selection(params ComfUIWorkflow[] workflows)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Workflows.AddRange(workflows);
        return new WizardSelection { Workload = w };
    }

    [Fact]
    public async Task Every_workflow_is_a_ticked_row_with_its_version()
    {
        var module = new WorkflowSelectionModule();
        var a = new ComfUIWorkflow { Name = "1.Text2Image", Version = 1, SubVersion = 2 };
        var b = new ComfUIWorkflow { Name = "2.Upscale", Version = 3 };

        await module.InitializeAsync(Selection(a, b));

        module.Rows.Select(r => (r.Name, r.Version)).Should().Equal(("1.Text2Image", "v1.2"), ("2.Upscale", "v3.0"));
        module.Rows.Should().OnlyContain(r => r.IsSelected);
        module.SelectedCount.Should().Be(2);
    }

    [Fact]
    public void Applies_exactly_when_the_workload_has_workflows()
    {
        var module = new WorkflowSelectionModule();

        module.AppliesTo(Selection(new ComfUIWorkflow())).Should().BeTrue();
        module.AppliesTo(Selection()).Should().BeFalse();
    }

    [Fact]
    public async Task Unticked_workflows_become_excluded_ids()
    {
        var module = new WorkflowSelectionModule();
        var a = new ComfUIWorkflow { Name = "a" };
        var b = new ComfUIWorkflow { Name = "b" };
        await module.InitializeAsync(Selection(a, b));

        module.SetSelected(b.Id, false);
        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);

        draft.ExcludedWorkflowIds.Should().BeEquivalentTo([b.Id]);
        module.Validate().IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Reinitializing_for_another_workload_starts_clean()
    {
        var module = new WorkflowSelectionModule();
        var a = new ComfUIWorkflow { Name = "a" };
        await module.InitializeAsync(Selection(a));
        module.SetSelected(a.Id, false);

        await module.InitializeAsync(Selection(new ComfUIWorkflow { Name = "b" }));

        module.Rows.Should().ContainSingle().Which.IsSelected.Should().BeTrue();
    }
}
```

```csharp
// DiffusionNexus.Installer.Tests/Components/WorkflowSelectionPanelTests.cs
using Bunit;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class WorkflowSelectionPanelTests : BunitContext
{
    private static async Task<WorkflowSelectionModule> ModuleAsync()
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Workflows.Add(new ComfUIWorkflow { Name = "1.Text2Image", Version = 1, SubVersion = 1 });
        w.Workflows.Add(new ComfUIWorkflow { Name = "2.Upscale", Version = 2 });
        var module = new WorkflowSelectionModule();
        await module.InitializeAsync(new WizardSelection { Workload = w });
        return module;
    }

    [Fact]
    public async Task Lists_every_workflow_ticked_with_its_version()
    {
        var module = await ModuleAsync();

        var cut = Render<WorkflowSelectionPanel>(p => p.Add(x => x.Module, module));

        cut.FindAll("input[type=checkbox]").Should().HaveCount(2).And.OnlyContain(i => i.HasAttribute("checked"));
        cut.Markup.Should().Contain("1.Text2Image").And.Contain("v1.1").And.Contain("v2.0");
    }

    [Fact]
    public async Task Unticking_a_workflow_updates_the_module_and_raises_Changed()
    {
        var module = await ModuleAsync();
        var changed = false;

        var cut = Render<WorkflowSelectionPanel>(p => p
            .Add(x => x.Module, module)
            .Add(x => x.Changed, EventCallback.Factory.Create(this, () => changed = true)));
        cut.FindAll("input[type=checkbox]")[1].Change(false);

        module.SelectedCount.Should().Be(1);
        changed.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~WorkflowSelection"`
Expected: build error — `WorkflowSelectionModule` does not exist.

- [ ] **Step 3: Write the module and the panel**

```csharp
// DiffusionNexus.Installer.Core/Modules/WorkflowSelectionModule.cs
using DiffusionNexus.Installer.Core.Wizard;

namespace DiffusionNexus.Installer.Core.Modules;

public sealed class WorkflowRow(Guid id, string name, string version)
{
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public string Version { get; } = version;
    public bool IsSelected { get; set; } = true;
}

/// <summary>
/// Which of the workload's workflows to write into the install. Non-blocking: with no module the
/// pipeline exports every declared workflow, which is correct. This exists so the user can see and
/// skip them, as 1.x allowed.
/// </summary>
public sealed class WorkflowSelectionModule : IWizardModule
{
    public string Id => "workflow-selection";
    public WizardStage Stage => WizardStage.Content;
    public int Order => 20;
    public WorkloadCapability Satisfies => WorkloadCapability.Workflows;

    public IReadOnlyList<WorkflowRow> Rows { get; private set; } = [];
    public int SelectedCount => Rows.Count(r => r.IsSelected);

    public bool AppliesTo(WizardSelection selection) => selection.Workload.Workflows.Count > 0;

    public Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        Rows = selection.Workload.Workflows
            .Select(w => new WorkflowRow(w.Id, w.Name, $"v{w.Version}.{w.SubVersion}"))
            .ToList();
        return Task.CompletedTask;
    }

    public void SetSelected(Guid id, bool selected)
    {
        if (Rows.FirstOrDefault(r => r.Id == id) is { } row) row.IsSelected = selected;
    }

    public void Contribute(InstallationOptionsDraft draft)
    {
        draft.ExcludedWorkflowIds.Clear();
        foreach (var row in Rows.Where(r => !r.IsSelected)) draft.ExcludedWorkflowIds.Add(row.Id);
    }

    public ModuleValidation Validate() => ModuleValidation.Ok();
}
```

```razor
@* DiffusionNexus.Installer.Electron/Components/Wizard/WorkflowSelectionPanel.razor *@
@using DiffusionNexus.Installer.Core.Modules

<section class="panel">
    <h2>Workflows</h2>
    <p class="panel-hint">Ready-made ComfyUI workflows for this workload. Untick any you do not want written into the install.</p>

    @foreach (var row in Module.Rows)
    {
        <label class="checkbox">
            <input type="checkbox" checked="@row.IsSelected" @onchange="e => OnRowChanged(row, e)" />
            <span>@row.Name</span>
            <span class="panel-hint" style="margin:0">@row.Version</span>
        </label>
    }
</section>

@code {
    [Parameter, EditorRequired] public WorkflowSelectionModule Module { get; set; } = default!;

    /// <summary>Raised after any edit so the parent, which owns Next, re-renders.</summary>
    [Parameter] public EventCallback Changed { get; set; }

    private async Task OnRowChanged(WorkflowRow row, ChangeEventArgs e)
    {
        Module.SetSelected(row.Id, (bool)(e.Value ?? false));
        await Changed.InvokeAsync();
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~WorkflowSelection"`
Expected: 6 pass.

- [ ] **Step 5: Commit and push**

```bash
git add DiffusionNexus.Installer.Core/Modules/WorkflowSelectionModule.cs DiffusionNexus.Installer.Electron/Components/Wizard/WorkflowSelectionPanel.razor DiffusionNexus.Installer.Tests/Modules/WorkflowSelectionModuleTests.cs DiffusionNexus.Installer.Tests/Components/WorkflowSelectionPanelTests.cs
git commit -m "feat(wizard): workflow selection module and panel

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---

### Task 10: Register the modules — the Content stage goes live and 11 workloads unlock

**Files:**
- Modify: `DiffusionNexus.Installer.Core/CoreServiceCollectionExtensions.cs`
- Modify: `DiffusionNexus.Installer.Electron/Components/Pages/Install.razor` (`RenderModule`)
- Modify: `DiffusionNexus.Installer.Electron/Components/Wizard/ConfirmStage.razor`
- Test: `DiffusionNexus.Installer.Tests/DependencyInjectionTests.cs`, `Wizard/CapabilityAgreementTests.cs`, `Wizard/RealCatalogInstallabilityTests.cs`, `Components/InstallPageTests.cs`

**Interfaces:**
- Consumes: the three modules and three panels (Tasks 4, 7, 8, 9).

- [ ] **Step 1: Update the DI test expectations**

In `DependencyInjectionTests.All_slice_one_modules_resolve` (rename to `All_modules_resolve`), expect:

```csharp
        registry.SatisfiedCapabilities.Should().Be(
            WorkloadCapability.ComfyFolders | WorkloadCapability.LlamaCpp
            | WorkloadCapability.VramProfile | WorkloadCapability.ModelDownloads | WorkloadCapability.Workflows);
```

and in `Every_registered_module_is_reachable_through_the_registry`:

```csharp
        provider.GetServices<IWizardModule>().Select(m => m.Id).Should().BeEquivalentTo(
            "install-folder", "comfy-folders", "vram-profile", "model-selection", "workflow-selection",
            "gpu-preflight", "vc-runtime", "llama-cpp", "shortcuts", "disclaimer");
```

- [ ] **Step 2: Update CapabilityAgreementTests**

Add `using DiffusionNexus.Installer.Core.Content;` and extend `Registry()`:

```csharp
    private static WizardModuleRegistry Registry(params LamaCppWheel[] wheels) => new(() =>
    [
        new InstallFolderModule(Settings(), new PreInstallationService()),
        new ComfyFoldersModule(Settings()),
        new VramProfileModule(),
        new ModelSelectionModule(new ModelPresenceScanner(), Mock.Of<IDiskSpaceEstimator>()),
        new WorkflowSelectionModule(),
        new GpuPreflightModule(Gpu()),
        new VcRuntimeModule(VcRuntime()),
        new LlamaCppModule(Wheels(wheels)),
        new ShortcutsModule(),
        new DisclaimerModule(),
    ]);
```

Replace `A_content_heavy_comfyui_pack_is_not_installable_in_slice_one` with:

```csharp
    [Fact]
    public void A_content_heavy_comfyui_pack_is_installable_now_that_tier_and_model_modules_exist()
    {
        var pack = Workload(RepositoryType.ComfyUI);
        pack.Vram.VramProfiles = "8,12,16,24,32";
        pack.ModelDownloads.Add(new ModelDownload());

        Registry().IsInstallable(pack).Should().BeTrue();
    }
```

Replace `A_workload_needing_a_vram_tier_is_not_installable` with:

```csharp
    [Fact]
    public void A_workload_needing_a_vram_tier_is_installable()
    {
        var pack = Workload(RepositoryType.ComfyUI);
        pack.Vram.VramProfiles = "8,12,16,24,32";

        Registry().IsInstallable(pack).Should().BeTrue();
    }
```

Add the agreement theory for the three new capabilities:

```csharp
    [Theory]
    [InlineData(WorkloadCapability.VramProfile)]
    [InlineData(WorkloadCapability.ModelDownloads)]
    [InlineData(WorkloadCapability.Workflows)]
    public async Task Detect_and_AppliesTo_agree_on_each_content_capability(WorkloadCapability capability)
    {
        var with = Workload(RepositoryType.ComfyUI);
        var without = Workload(RepositoryType.ComfyUI);
        switch (capability)
        {
            case WorkloadCapability.VramProfile: with.Vram.VramProfiles = "8,12"; without.Vram.VramProfiles = "abc"; break;
            case WorkloadCapability.ModelDownloads: with.ModelDownloads.Add(new ModelDownload { Enabled = false }); break;
            case WorkloadCapability.Workflows: with.Workflows.Add(new ComfUIWorkflow()); break;
        }

        foreach (var workload in new[] { with, without })
        {
            var detected = WorkloadCapabilities.Detect(workload).HasFlag(capability);
            var plan = await Registry().BuildPlanAsync(new WizardSelection { Workload = workload });
            var rendered = plan.AllModules.Any(m => m.Satisfies == capability);

            rendered.Should().Be(detected, $"{capability}: the gate and the module must agree");
        }
    }
```

- [ ] **Step 3: Update RealCatalogInstallabilityTests**

Replace the constructor, `_catalogDir` field, `ReadCatalogWorkloadsAsync` and `Dispose` with the shared helper (add `using DiffusionNexus.Installer.Core.Content;` and `using DiffusionNexus.Installer.Tests.Support;`; drop `using System.IO.Compression;`, the `SDK.Catalog` and `SDK.Catalog.Updates` usings, and `using DiffusionNexus.Installer.Electron.Services;`):

```csharp
public sealed class RealCatalogInstallabilityTests : IAsyncLifetime
{
    private string _catalogDir = string.Empty;
    private IReadOnlyList<InstallationConfiguration> _workloads = [];

    public async Task InitializeAsync() => (_catalogDir, _workloads) = await EmbeddedCatalog.LoadAsync();

    public Task DisposeAsync()
    {
        EmbeddedCatalog.Delete(_catalogDir);
        return Task.CompletedTask;
    }

    private Task<IReadOnlyList<InstallationConfiguration>> ReadCatalogWorkloadsAsync() => Task.FromResult(_workloads);
```

Replace `ExpectedInstallableNames` and its comment:

```csharp
    // Every Installer-targeted workload in the embedded seed except Config535, which pairs torch
    // 2.8.0 with CUDA 13.0 (no such wheel) and is refused by the pipeline before step 1 -- see
    // Config535_is_blocked_for_an_impossible_torch_pairing. The four DiffusionNexusCore workloads
    // never reach the gallery and are filtered out before this list is compared.
    private static readonly string[] ExpectedInstallableNames =
    [
        "Stable Diffusion web UI",
        "Stable Diffusion WebUI Forge",
        "Fooocus",
        "ACE-Step-1.5",
        "AI-Toolkit",
        "Blanck-ComfyUI",
        "Base-install-Triton-SageAttention-Manager",
        "ComfyUI Llama Cpp test",
        "FlashVSR-Video&Image Upscale",
        "Ernie-Image-Turbo",
        "Flux-Klein 9b + 4b",
        "Ideogram-4.0",
        "Krea-2-Turbo",
        "LTX-2-3-GGUF",
        "LTX-2-3-V1.1-Director-GGUF",
        "LTX2 - GGUF - Legacy",
        "MiniMax H3",
        "Qwen-Image-Edit-2511 - 2512 - Layered",
        "Qwen-Image-Edit-2511 - Deprecated",
        "Wan 2.2 - GGUF",
    ];
```

Extend `ProductionRegistry()` with the same three modules as `CapabilityAgreementTests.Registry()` (in the same positions). Rename `Exactly_the_slice_one_workloads_are_installable_in_the_real_catalog` to `Exactly_twenty_of_the_twenty_one_installer_workloads_are_installable`, and change its two `because` strings to `"these are the twenty Installer-targeted workloads slice 2's modules cover"` and `"Config535 must be blocked, not silently allowed"`. Add after the installable assertion:

```csharp
        blocked.Should().Equal("Config535");
```

Replace `Detect_and_AppliesTo_agree_on_ComfyFolders_for_every_real_catalog_workload` with one covering all four module-backed capabilities:

```csharp
    [Fact]
    public async Task Detect_and_AppliesTo_agree_for_every_real_catalog_workload()
    {
        var modules = new IWizardModule[]
        {
            new ComfyFoldersModule(Settings()),
            new VramProfileModule(),
            new ModelSelectionModule(new ModelPresenceScanner(), Mock.Of<IDiskSpaceEstimator>()),
            new WorkflowSelectionModule(),
        };
        var workloads = await ReadCatalogWorkloadsAsync();
        workloads.Should().NotBeEmpty();

        foreach (var workload in workloads)
        {
            var selection = new WizardSelection { Workload = workload };
            foreach (var module in modules)
            {
                var detected = WorkloadCapabilities.Detect(workload).HasFlag(module.Satisfies);
                module.AppliesTo(selection).Should().Be(detected,
                    $"'{workload.Name}': {module.Satisfies} must agree between Detect and AppliesTo");
            }
        }
    }
```

- [ ] **Step 4: Run the three test classes to verify they fail**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~DependencyInjectionTests|FullyQualifiedName~CapabilityAgreementTests|FullyQualifiedName~RealCatalogInstallabilityTests"`
Expected: DI tests fail on `SatisfiedCapabilities` / ids (modules not registered); the other two pass already because they construct modules directly — that is fine, they pin the behaviour Step 5 must keep.

- [ ] **Step 5: Register, render, and summarize**

In `CoreServiceCollectionExtensions.AddInstallerCore`, after `services.AddTransient<IWizardModule, ComfyFoldersModule>();` add:

```csharp
        services.AddTransient<IWizardModule, VramProfileModule>();
        services.AddTransient<IWizardModule, ModelSelectionModule>();
        services.AddTransient<IWizardModule, WorkflowSelectionModule>();
```

In `Install.razor`'s `RenderModule`, after the `ComfyFoldersModule` arm add:

```csharp
        VramProfileModule m => @<VramProfilePanel Module="m" Changed="_moduleChanged" />,
        ModelSelectionModule m => @<ModelSelectionPanel Module="m" Selection="_run!.Plan.Selection" Changed="_moduleChanged" />,
        WorkflowSelectionModule m => @<WorkflowSelectionPanel Module="m" Changed="_moduleChanged" />,
```

In `ConfirmStage.razor`, add `@using DiffusionNexus.Installer.Core.Modules` and, inside the `@{ }` block after `var options = ...`:

```csharp
    var models = Run.Plan.AllModules.OfType<ModelSelectionModule>().FirstOrDefault();
    var workflows = Run.Plan.AllModules.OfType<WorkflowSelectionModule>().FirstOrDefault();
```

and after the `Python` row:

```razor
        @if (options.SelectedVramProfile > 0)
        {
            <dt>VRAM tier</dt>
            <dd>@options.SelectedVramProfile GB</dd>
        }

        @if (models is not null)
        {
            <dt>Models</dt>
            <dd>@models.SelectedCount of @models.Rows.Count selected</dd>
        }

        @if (workflows is not null)
        {
            <dt>Workflows</dt>
            <dd>@workflows.SelectedCount of @workflows.Rows.Count selected</dd>
        }
```

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test DiffusionNexus.Installer.Tests`
Expected: all pass.

- [ ] **Step 7: Commit and push**

```bash
git add -A DiffusionNexus.Installer.Core DiffusionNexus.Installer.Electron DiffusionNexus.Installer.Tests
git commit -m "feat(wizard): register the Content stage modules; 20 of 21 workloads installable

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---
### Task 11: The mismatched-files prompt and modal

**Files:**
- Create: `DiffusionNexus.Installer.Core/Host/IMismatchedFilePrompt.cs`
- Create: `DiffusionNexus.Installer.Core/Host/MismatchPromptService.cs`
- Create: `DiffusionNexus.Installer.Electron/Components/Shared/MismatchModal.razor`
- Modify: `DiffusionNexus.Installer.Electron/Components/Layout/MainLayout.razor`, `DiffusionNexus.Installer.Electron/Program.cs`, `DiffusionNexus.Installer.Electron/wwwroot/app.css`
- Test: `DiffusionNexus.Installer.Tests/Host/MismatchPromptServiceTests.cs`, `DiffusionNexus.Installer.Tests/Components/MismatchModalTests.cs`

**Interfaces:**
- Produces: `record MismatchResolution(HashSet<string> RedownloadUrls, HashSet<string> TrustedUrls)`; `interface IMismatchedFilePrompt { Task<MismatchResolution?> ResolveAsync(IReadOnlyList<ExistingModelMismatch> mismatches, CancellationToken ct = default); }` (null = dismissed); `MismatchPromptService : IMismatchedFilePrompt` with `bool IsOpen`, `IReadOnlyList<ExistingModelMismatch> Mismatches`, `event Action? Changed`, `void Answer(MismatchResolution? resolution)`.

- [ ] **Step 1: Write the failing service tests**

```csharp
// DiffusionNexus.Installer.Tests/Host/MismatchPromptServiceTests.cs
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Host;

public class MismatchPromptServiceTests
{
    private static ExistingModelMismatch Mismatch(string url) =>
        new(new ModelDownload { Name = "m", Url = url }, @"C:\m\file.bin", 10, 20, url);

    [Fact]
    public async Task Opens_with_the_mismatches_and_completes_with_the_answer()
    {
        var service = new MismatchPromptService();
        var raised = 0;
        service.Changed += () => raised++;

        var pending = service.ResolveAsync([Mismatch("https://h.invalid/a.bin")]);

        service.IsOpen.Should().BeTrue();
        service.Mismatches.Should().ContainSingle();
        raised.Should().Be(1);

        service.Answer(new MismatchResolution(["https://h.invalid/a.bin"], []));

        (await pending)!.RedownloadUrls.Should().BeEquivalentTo(["https://h.invalid/a.bin"]);
        service.IsOpen.Should().BeFalse();
        raised.Should().Be(2);
    }

    [Fact]
    public async Task Dismissal_completes_with_null()
    {
        var service = new MismatchPromptService();
        var pending = service.ResolveAsync([Mismatch("https://h.invalid/a.bin")]);

        service.Answer(null);

        (await pending).Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_dismisses_the_prompt()
    {
        var service = new MismatchPromptService();
        using var cts = new CancellationTokenSource();
        var pending = service.ResolveAsync([Mismatch("https://h.invalid/a.bin")], cts.Token);

        cts.Cancel();

        (await pending).Should().BeNull();
        service.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void A_second_prompt_while_one_is_open_throws_rather_than_replacing_it()
    {
        var service = new MismatchPromptService();
        _ = service.ResolveAsync([Mismatch("https://h.invalid/a.bin")]);

        var act = () => service.ResolveAsync([Mismatch("https://h.invalid/b.bin")]);

        act.Should().Throw<InvalidOperationException>();
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~MismatchPromptServiceTests"`
Expected: build error — `MismatchPromptService` does not exist.

- [ ] **Step 3: Write the interface and the service**

```csharp
// DiffusionNexus.Installer.Core/Host/IMismatchedFilePrompt.cs
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;

namespace DiffusionNexus.Installer.Core.Host;

/// <summary>The user's per-file answers, keyed by download URL — the file's identity, not the model's.</summary>
public sealed record MismatchResolution(HashSet<string> RedownloadUrls, HashSet<string> TrustedUrls);

/// <summary>
/// One dialog listing every already-present file whose size differs from the server's, with a
/// redownload-or-keep choice per file. Shown before an install starts, never mid-install.
/// </summary>
public interface IMismatchedFilePrompt
{
    /// <summary>Null means the user dismissed the dialog, which cancels the install.</summary>
    Task<MismatchResolution?> ResolveAsync(IReadOnlyList<ExistingModelMismatch> mismatches, CancellationToken ct = default);
}
```

```csharp
// DiffusionNexus.Installer.Core/Host/MismatchPromptService.cs
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;

namespace DiffusionNexus.Installer.Core.Host;

/// <summary>
/// State behind the mismatch modal, shaped like <see cref="ModalPromptService"/>: singleton, one
/// prompt at a time, completed by the modal's Answer or by the caller's token.
/// </summary>
public sealed class MismatchPromptService : IMismatchedFilePrompt
{
    private readonly Lock _gate = new();
    private Pending? _pending;

    private sealed class Pending
    {
        public required TaskCompletionSource<MismatchResolution?> Completion { get; init; }
        public CancellationTokenRegistration Registration { get; set; }
    }

    public IReadOnlyList<ExistingModelMismatch> Mismatches { get; private set; } = [];

    public bool IsOpen { get { lock (_gate) return _pending is not null; } }

    public event Action? Changed;

    public Task<MismatchResolution?> ResolveAsync(IReadOnlyList<ExistingModelMismatch> mismatches, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mismatches);

        var pending = new Pending
        {
            Completion = new TaskCompletionSource<MismatchResolution?>(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        lock (_gate)
        {
            if (_pending is not null)
                throw new InvalidOperationException("A mismatch dialog is already awaiting an answer.");

            Mismatches = mismatches;
            _pending = pending;
        }

        // Keyed to THIS prompt and disposed with it, so a stale registration can never answer a later one.
        pending.Registration = ct.Register(() => Complete(pending, null));

        Changed?.Invoke();
        return pending.Completion.Task;
    }

    /// <summary>Answers the dialog on screen; null is a dismissal. No-op when nothing is pending.</summary>
    public void Answer(MismatchResolution? resolution)
    {
        Pending? pending;
        lock (_gate) pending = _pending;

        if (pending is not null) Complete(pending, resolution);
    }

    private void Complete(Pending pending, MismatchResolution? resolution)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_pending, pending)) return;
            _pending = null;
            Mismatches = [];
        }

        pending.Registration.Dispose();
        pending.Completion.TrySetResult(resolution);
        Changed?.Invoke();
    }
}
```

- [ ] **Step 4: Run the service tests**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~MismatchPromptServiceTests"`
Expected: 4 pass.

- [ ] **Step 5: Write the failing modal test**

```csharp
// DiffusionNexus.Installer.Tests/Components/MismatchModalTests.cs
using Bunit;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Electron.Components.Shared;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class MismatchModalTests : BunitContext
{
    private static ExistingModelMismatch Mismatch(string name, string url) =>
        new(new ModelDownload { Name = name, Url = url }, $@"C:\models\{name}.bin", 1_000, 2_000, url);

    [Fact]
    public async Task Lists_every_file_redownload_ticked_and_returns_the_split_by_url()
    {
        var service = new MismatchPromptService();
        Services.AddSingleton(service);
        var cut = Render<MismatchModal>();
        cut.Markup.Should().NotContain("modal-card", "closed until a prompt is raised");

        var pending = service.ResolveAsync([Mismatch("a", "https://h.invalid/a.bin"), Mismatch("b", "https://h.invalid/b.bin")]);

        cut.WaitForAssertion(() => cut.FindAll("input[type=checkbox]").Should().HaveCount(2));
        cut.FindAll("input[type=checkbox]").Should().OnlyContain(i => i.HasAttribute("checked"));
        cut.Markup.Should().Contain("a.bin").And.Contain("b.bin");

        cut.FindAll("input[type=checkbox]")[1].Change(false);
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Continue").ClickAsync(new MouseEventArgs());

        var resolution = (await pending)!;
        resolution.RedownloadUrls.Should().BeEquivalentTo(["https://h.invalid/a.bin"]);
        resolution.TrustedUrls.Should().BeEquivalentTo(["https://h.invalid/b.bin"]);
    }

    [Fact]
    public async Task Cancel_dismisses_with_null()
    {
        var service = new MismatchPromptService();
        Services.AddSingleton(service);
        var cut = Render<MismatchModal>();

        var pending = service.ResolveAsync([Mismatch("a", "https://h.invalid/a.bin")]);
        cut.WaitForAssertion(() => cut.FindAll("button").Should().NotBeEmpty());

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel installation").ClickAsync(new MouseEventArgs());

        (await pending).Should().BeNull();
    }
}
```

- [ ] **Step 6: Run to verify it fails**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~MismatchModalTests"`
Expected: build error — `MismatchModal` does not exist.

- [ ] **Step 7: Write the modal, host it, register the service, style it**

```razor
@* DiffusionNexus.Installer.Electron/Components/Shared/MismatchModal.razor *@
@using DiffusionNexus.Installer.Core.Host
@using DiffusionNexus.Installer.SDK.Services.Installation.Utilities
@implements IDisposable
@inject MismatchPromptService Prompts

@if (Prompts.IsOpen)
{
    <div class="modal-backdrop">
        <div class="modal-card modal-card-wide" role="dialog" aria-modal="true">
            <h2>Some existing model files look wrong</h2>
            <p class="panel-hint">
                These files are already in place but their size does not match what the server
                reports. Ticked files are downloaded again; unticked files are kept as they are.
            </p>
            <table class="report">
                <thead><tr><th>Redownload</th><th>File</th><th>On disk</th><th>Expected</th></tr></thead>
                <tbody>
                    @foreach (var m in Prompts.Mismatches)
                    {
                        <tr>
                            <td><input type="checkbox" checked="@IsRedownload(m.Url)" @onchange="e => Toggle(m.Url, e)" /></td>
                            <td title="@m.FilePath">@System.IO.Path.GetFileName(m.FilePath)</td>
                            <td>@DiskSpaceRequirement.FormatBytes(m.ActualBytes)</td>
                            <td>@DiskSpaceRequirement.FormatBytes(m.ExpectedBytes)</td>
                        </tr>
                    }
                </tbody>
            </table>
            <div class="modal-actions">
                <button class="btn-secondary" @onclick="() => Prompts.Answer(null)">Cancel installation</button>
                <button class="btn-primary" @onclick="Continue">Continue</button>
            </div>
        </div>
    </div>
}

@code {
    // URLs the user unticked. Everything else redownloads: a size mismatch usually means a partial
    // or corrupt file, so "download it again" is the safe default.
    private readonly HashSet<string> _keep = new(StringComparer.OrdinalIgnoreCase);

    protected override void OnInitialized() => Prompts.Changed += OnChanged;

    private bool IsRedownload(string url) => !_keep.Contains(url);

    private void Toggle(string url, ChangeEventArgs e)
    {
        if ((bool)(e.Value ?? false)) _keep.Remove(url); else _keep.Add(url);
    }

    private void Continue()
    {
        var all = Prompts.Mismatches.Select(m => m.Url).ToList();
        var redownload = all.Where(u => !_keep.Contains(u)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trusted = all.Where(_keep.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _keep.Clear();
        Prompts.Answer(new MismatchResolution(redownload, trusted));
    }

    private void OnChanged()
    {
        if (!Prompts.IsOpen) _keep.Clear();
        InvokeAsync(StateHasChanged);
    }

    public void Dispose() => Prompts.Changed -= OnChanged;
}
```

In `MainLayout.razor`, after the `PromptModal` line add:

```razor
<DiffusionNexus.Installer.Electron.Components.Shared.MismatchModal />
```

In `Program.cs`, after the `IUserPrompt` registration add:

```csharp
builder.Services.AddSingleton<MismatchPromptService>();
builder.Services.AddSingleton<IMismatchedFilePrompt>(sp => sp.GetRequiredService<MismatchPromptService>());
```

Append to `wwwroot/app.css` after the `.modal-card h2` rule:

```css
.modal-card-wide {
    max-width: 48rem;
}
```

- [ ] **Step 8: Run the modal tests**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~MismatchModalTests"`
Expected: 2 pass.

- [ ] **Step 9: Commit and push**

```bash
git add DiffusionNexus.Installer.Core/Host DiffusionNexus.Installer.Electron/Components/Shared/MismatchModal.razor DiffusionNexus.Installer.Electron/Components/Layout/MainLayout.razor DiffusionNexus.Installer.Electron/Program.cs DiffusionNexus.Installer.Electron/wwwroot/app.css DiffusionNexus.Installer.Tests/Host/MismatchPromptServiceTests.cs DiffusionNexus.Installer.Tests/Components/MismatchModalTests.cs
git commit -m "feat(host): one mismatched-files dialog with redownload-or-keep per file

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---

### Task 12: ModelPreflight, run when the user leaves Confirm

Spec §4.6 describes an `InstallLauncher` that verifies and then starts the session. This task implements the same behaviour one step earlier — on Confirm's Next — because `WizardRun` forbids going back from the Install stage, so a dismissed dialog could not return the user to Confirm. The session start in `InstallStage` is untouched. Amend the spec accordingly (Step 6).

**Files:**
- Create: `DiffusionNexus.Installer.Core/Install/ModelPreflight.cs`
- Modify: `DiffusionNexus.Installer.Core/CoreServiceCollectionExtensions.cs`
- Modify: `DiffusionNexus.Installer.Electron/Components/Pages/Install.razor`
- Modify: `docs/superpowers/specs/2026-09-02-electron-wizard-slice-2-content-stage-design.md` (§4.6)
- Test: `DiffusionNexus.Installer.Tests/Install/ModelPreflightTests.cs`, `DiffusionNexus.Installer.Tests/Components/InstallPageTests.cs`, `DiffusionNexus.Installer.Tests/DependencyInjectionTests.cs`

**Interfaces:**
- Consumes: `IExistingModelVerifier` (Task 6), `IMismatchedFilePrompt` (Task 11), `ModelSelectionModule` (Task 7).
- Produces: `record PreflightResult(bool Proceed, string? Warning)`; `interface IModelPreflight { Task<PreflightResult> RunAsync(WizardPlan plan, CancellationToken ct = default); }`; `ModelPreflight(IExistingModelVerifier verifier, IMismatchedFilePrompt prompt)`.

- [ ] **Step 1: Write the failing preflight tests**

```csharp
// DiffusionNexus.Installer.Tests/Install/ModelPreflightTests.cs
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Install;

public class ModelPreflightTests
{
    private static readonly ModelDownload Vae = new() { Name = "VAE", Destination = @"models\vae", Url = "https://h.invalid/ae.safetensors" };

    private static async Task<(WizardPlan Plan, ModelSelectionModule Module)> PlanAsync(bool vaePresent)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Repository.RepositoryUrl = "https://github.com/comfyanonymous/ComfyUI";
        w.ModelDownloads.Add(Vae);

        var scanner = new Mock<IModelPresenceScanner>();
        scanner.Setup(s => s.Scan(It.IsAny<ModelScanRequest>())).Returns(
        [
            new ModelPresence(Vae.Id, vaePresent, vaePresent ? @"C:\AI\ComfyUI\models\vae\ae.safetensors" : null,
                [new ModelFileTarget(Vae, Vae.Url, @"C:\AI\ComfyUI\models\vae", "ae.safetensors", vaePresent ? @"C:\AI\ComfyUI\models\vae\ae.safetensors" : null)]),
        ]);
        var module = new ModelSelectionModule(scanner.Object, Mock.Of<IDiskSpaceEstimator>());

        var registry = new WizardModuleRegistry(() => [module]);
        var plan = await registry.BuildPlanAsync(new WizardSelection { Workload = w, TargetFolder = @"C:\AI" });
        return (plan, module);
    }

    private static ExistingModelMismatch Mismatch() =>
        new(Vae, @"C:\AI\ComfyUI\models\vae\ae.safetensors", 10, 20, Vae.Url);

    [Fact]
    public async Task No_files_on_disk_means_no_verification_and_no_prompt()
    {
        var verifier = new Mock<IExistingModelVerifier>();
        var prompt = new Mock<IMismatchedFilePrompt>();
        var (plan, _) = await PlanAsync(vaePresent: false);

        var result = await new ModelPreflight(verifier.Object, prompt.Object).RunAsync(plan);

        result.Proceed.Should().BeTrue();
        result.Warning.Should().BeNull();
        verifier.Verify(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()), Times.Never);
        prompt.Verify(p => p.ResolveAsync(It.IsAny<IReadOnlyList<ExistingModelMismatch>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Matching_files_proceed_without_a_prompt()
    {
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var prompt = new Mock<IMismatchedFilePrompt>();
        var (plan, _) = await PlanAsync(vaePresent: true);

        var result = await new ModelPreflight(verifier.Object, prompt.Object).RunAsync(plan);

        result.Proceed.Should().BeTrue();
        verifier.Verify(v => v.VerifyAsync(It.Is<IReadOnlyList<ExistingModelCandidate>>(c => c.Single().Url == Vae.Url), It.IsAny<CancellationToken>()), Times.Once);
        prompt.Verify(p => p.ResolveAsync(It.IsAny<IReadOnlyList<ExistingModelMismatch>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Mismatches_prompt_once_and_the_answer_reaches_the_options()
    {
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Mismatch()]);
        var prompt = new Mock<IMismatchedFilePrompt>();
        prompt.Setup(p => p.ResolveAsync(It.IsAny<IReadOnlyList<ExistingModelMismatch>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MismatchResolution([Vae.Url], []));
        var (plan, _) = await PlanAsync(vaePresent: true);

        var result = await new ModelPreflight(verifier.Object, prompt.Object).RunAsync(plan);

        result.Proceed.Should().BeTrue();
        plan.ToOptions().ForceRedownloadUrls.Should().BeEquivalentTo([Vae.Url]);
        prompt.Verify(p => p.ResolveAsync(It.Is<IReadOnlyList<ExistingModelMismatch>>(m => m.Count == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_dismissed_dialog_does_not_proceed()
    {
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Mismatch()]);
        var prompt = new Mock<IMismatchedFilePrompt>();
        prompt.Setup(p => p.ResolveAsync(It.IsAny<IReadOnlyList<ExistingModelMismatch>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MismatchResolution?)null);
        var (plan, _) = await PlanAsync(vaePresent: true);

        var result = await new ModelPreflight(verifier.Object, prompt.Object).RunAsync(plan);

        result.Proceed.Should().BeFalse();
        plan.ToOptions().ForceRedownloadUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_verification_proceeds_with_a_warning()
    {
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));
        var (plan, _) = await PlanAsync(vaePresent: true);

        var result = await new ModelPreflight(verifier.Object, Mock.Of<IMismatchedFilePrompt>()).RunAsync(plan);

        result.Proceed.Should().BeTrue();
        result.Warning.Should().Contain("offline");
    }

    [Fact]
    public async Task A_plan_without_a_model_module_proceeds_untouched()
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.Fooocus;
        var plan = await new WizardModuleRegistry(() => []).BuildPlanAsync(new WizardSelection { Workload = w });

        var result = await new ModelPreflight(Mock.Of<IExistingModelVerifier>(), Mock.Of<IMismatchedFilePrompt>()).RunAsync(plan);

        result.Proceed.Should().BeTrue();
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var (plan, _) = await PlanAsync(vaePresent: true);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => new ModelPreflight(verifier.Object, Mock.Of<IMismatchedFilePrompt>()).RunAsync(plan, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~ModelPreflightTests"`
Expected: build error — `ModelPreflight` does not exist.

- [ ] **Step 3: Write the preflight and register it**

```csharp
// DiffusionNexus.Installer.Core/Install/ModelPreflight.cs
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;

namespace DiffusionNexus.Installer.Core.Install;

/// <param name="Proceed">False only when the user dismissed the mismatch dialog.</param>
/// <param name="Warning">Set when verification itself failed; the install still proceeds.</param>
public sealed record PreflightResult(bool Proceed, string? Warning);

/// <summary>Runs when the user leaves Confirm, before the install session starts.</summary>
public interface IModelPreflight
{
    Task<PreflightResult> RunAsync(WizardPlan plan, CancellationToken ct = default);
}

/// <summary>
/// 1.x's pre-install verification: every ticked model's files already on disk are size-checked
/// against the server, mismatches go into ONE dialog, and the answers land on the model module so
/// ToOptions carries them as ForceRedownloadUrls / TrustedUrls. Never a prompt per file, never
/// mid-install. Dismissing the dialog refuses to proceed; a failing check warns and proceeds.
/// </summary>
public sealed class ModelPreflight(IExistingModelVerifier verifier, IMismatchedFilePrompt prompt) : IModelPreflight
{
    public async Task<PreflightResult> RunAsync(WizardPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var module = plan.AllModules.OfType<ModelSelectionModule>().FirstOrDefault();
        if (module is null) return new PreflightResult(true, null);

        // The folder may have changed since the Content stage rendered; scan against what Confirm shows.
        module.RefreshPresence();
        module.ApplyVerification([], []);

        var candidates = module.ExistingTargetsForSelectedModels()
            .Select(t => new ExistingModelCandidate(t.Model, t.ExistingPath!, t.Url))
            .ToList();

        if (candidates.Count == 0) return new PreflightResult(true, null);

        IReadOnlyList<ExistingModelMismatch> mismatches;
        try
        {
            mismatches = await verifier.VerifyAsync(candidates, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PreflightResult(true, $"Could not verify existing model files: {ex.Message}");
        }

        if (mismatches.Count == 0) return new PreflightResult(true, null);

        var resolution = await prompt.ResolveAsync(mismatches, ct).ConfigureAwait(false);
        if (resolution is null) return new PreflightResult(false, null);

        module.ApplyVerification(resolution.RedownloadUrls, resolution.TrustedUrls);
        return new PreflightResult(true, null);
    }
}
```

In `CoreServiceCollectionExtensions.AddInstallerCore`, after the `IInstallSession` registration add:

```csharp
        services.AddSingleton<IModelPreflight, ModelPreflight>();
```

`ModelPreflight` needs `IMismatchedFilePrompt`, which the Electron host registers (Task 11). `DependencyInjectionTests.Build()` must register one too — add before `services.AddInstallerCore();`:

```csharp
        services.AddSingleton<Core.Host.IMismatchedFilePrompt>(new Core.Host.MismatchPromptService());
```

and add a fact:

```csharp
    [Fact]
    public void The_model_preflight_resolves()
    {
        using var provider = Build();
        provider.GetRequiredService<Core.Install.IModelPreflight>().Should().NotBeNull();
    }
```

- [ ] **Step 4: Run the preflight and DI tests**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~ModelPreflightTests|FullyQualifiedName~DependencyInjectionTests"`
Expected: all pass.

- [ ] **Step 5: Wire the preflight into Confirm's Next**

In `Install.razor`:

1. Add `@implements IDisposable` under the `@page` line and `@inject IModelPreflight Preflight` after the `IUserPrompt` injection.
2. Replace the wizard-actions block and the validation-error loop inside the `else` branch with:

```razor
        @if (_preflightBusy)
        {
            <p class="panel-hint">Verifying existing model files...</p>
        }

        @if (_preflightNotice is not null)
        {
            <p class="validation-error">@_preflightNotice</p>
        }

        <div class="wizard-actions">
            @* Always available, including at stage 0. A GPU-blocked System stage validates false
               forever, and with Back disabled at index 0 there would be no exit from the wizard. *@
            <button class="btn-secondary" @onclick="GoToGallery">Cancel</button>
            <button class="btn-secondary" disabled="@(!_run.CanGoBack || _preflightBusy)" @onclick="Back">Back</button>
            <button class="btn-primary" disabled="@(!_run.CanGoNext || _preflightBusy)" @onclick="Next">Next</button>
        </div>

        @foreach (var error in _run.ValidationErrors)
        {
            <p class="validation-error">@error</p>
        }
```

3. Directly above `<InstallStage Run="_run" Done="_backToGallery" />` add:

```razor
        @if (_preflightWarning is not null)
        {
            <p class="validation-error">@_preflightWarning</p>
        }
```

4. In `@code`, add the fields and replace `Next()`:

```csharp
    private readonly CancellationTokenSource _pageCts = new();
    private bool _preflightBusy;
    private string? _preflightNotice;
    private string? _preflightWarning;

    private async Task Next()
    {
        if (_run!.CurrentStage == WizardStage.Confirm)
        {
            // 1.x checks already-present files when Install is pressed. Here that is Confirm's
            // Next: WizardRun cannot go back from the Install stage, so a dismissed dialog must
            // stop the user HERE rather than strand them on an install that never started.
            _preflightBusy = true;
            _preflightNotice = null;
            try
            {
                var result = await Preflight.RunAsync(_run.Plan, _pageCts.Token);
                _preflightWarning = result.Warning;
                if (!result.Proceed)
                {
                    _preflightNotice = "Installation not started: the mismatched model files were not resolved.";
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _preflightBusy = false;
            }
        }

        _run.TryNext();
    }

    public void Dispose()
    {
        // Navigating away while the mismatch dialog is open releases it as a dismissal.
        _pageCts.Cancel();
        _pageCts.Dispose();
    }
```

In `InstallPageTests.Register`, add `using DiffusionNexus.Installer.Core.Install;` is already present; add before `Services.AddSingleton(Mock.Of<IUserPrompt>());`:

```csharp
        var preflight = new Mock<IModelPreflight>();
        preflight.Setup(p => p.RunAsync(It.IsAny<WizardPlan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PreflightResult(true, null));
        Services.AddSingleton(preflight.Object);
```

In `The_disclaimer_gates_the_confirm_stage`, `next.Click()` inside the `while` loop stays (those clicks happen before Confirm; `Click()` on an async handler still dispatches). Add `using Microsoft.AspNetCore.Components.Web;` to the file for the next task.

- [ ] **Step 6: Amend the spec**

In the spec's §4.6, replace the paragraph beginning `This runs in a new \`InstallLauncher\` in \`Core\`, not in \`Install.razor\`:` through the end of the `IInstallLauncher` code block and the following paragraph (ending `...so the sets must be on the module before it starts.`) with:

```markdown
This runs in a new `ModelPreflight` in `Core`, invoked from `Install.razor` when the user presses
Next on Confirm — the moment 1.x calls "pressing Install":

```csharp
public sealed record PreflightResult(bool Proceed, string? Warning);

public interface IModelPreflight
{
    Task<PreflightResult> RunAsync(WizardPlan plan, CancellationToken ct = default);
}
```

It verifies, prompts, and hands the resulting URL sets to the plan's `ModelSelectionModule`
(`plan.AllModules.OfType<ModelSelectionModule>().FirstOrDefault()`, the pattern `Install.razor`
already uses for `ShortcutsModule`). Only when it proceeds does the wizard advance to the Install
stage, where `InstallStage` starts the session exactly as in slice 1. Confirm rather than Install
because `WizardRun` cannot go back from the Install stage: a dismissed dialog has to leave the
user on Confirm, not on an install screen that never started. The dialog's cancellation token is
the page's own, disposed when the user navigates away.
```

Also change `given \`IInstallSession.RunToken\` so a cancel can release it` in the same section to `given the page's cancellation token so navigating away releases it`.

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test DiffusionNexus.Installer.Tests`
Expected: all pass.

- [ ] **Step 8: Commit and push**

```bash
git add -A DiffusionNexus.Installer.Core DiffusionNexus.Installer.Electron DiffusionNexus.Installer.Tests docs/superpowers/specs
git commit -m "feat(install): verify existing model files when leaving Confirm

One dialog for every mismatched file; dismissing it keeps the user on
Confirm. Spec amended: preflight on Confirm's Next, not an install launcher.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---

### Task 13: Install page guards for the new wiring

**Files:**
- Test: `DiffusionNexus.Installer.Tests/Components/InstallPageTests.cs`

**Interfaces:**
- Consumes: everything above.

- [ ] **Step 1: Add the page-level tests**

Add these helpers and facts to `InstallPageTests` (add `using DiffusionNexus.Installer.Core.Content;`, `using DiffusionNexus.Installer.SDK.Models.Entities;`, `using DiffusionNexus.Installer.Electron.Components.Wizard;`):

```csharp
    private static InstallationConfiguration ContentWorkload()
    {
        var w = new InstallationConfiguration { Id = WorkloadId, Name = "Krea-2-Turbo" };
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Repository.RepositoryUrl = "https://github.com/comfyanonymous/ComfyUI";
        w.Vram.VramProfiles = "8,12,16";
        w.ModelDownloads.Add(new ModelDownload { Name = "VAE", Destination = @"models\vae", Url = "https://h.invalid/ae.safetensors" });
        w.Workflows.Add(new ComfUIWorkflow { Name = "1.Text2Image" });
        return w;
    }

    /// <summary>Registers the content workload with a registry that mirrors production's Content stage.</summary>
    private Mock<IInstallSession> RegisterContent(Mock<IModelPresenceScanner> scanner)
    {
        var workload = ContentWorkload();
        var settings = new Mock<IUserSettingsRepository>();
        settings.Setup(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings { DefaultTargetInstallFolder = @"C:\Installs" });

        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([workload]);
        source.Setup(s => s.GetLamaCppWheelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var session = new Mock<IInstallSession>();
        session.SetupGet(s => s.Phase).Returns(InstallPhase.Idle);
        session.SetupGet(s => s.LogLines).Returns([]);
        session.Setup(s => s.Tail(It.IsAny<int>())).Returns([]);

        var preflight = new Mock<IModelPreflight>();
        preflight.Setup(p => p.RunAsync(It.IsAny<WizardPlan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PreflightResult(true, null));

        var estimator = new Mock<IDiskSpaceEstimator>();
        estimator.Setup(e => e.EstimateAsync(It.IsAny<DiskSpaceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiskSpaceEstimate(1, 2, true, []));

        Services.AddSingleton(source.Object);
        Services.AddSingleton(session.Object);
        Services.AddSingleton(preflight.Object);
        Services.AddSingleton(Mock.Of<IUserPrompt>());
        Services.AddSingleton(Mock.Of<IFolderPicker>());
        Services.AddSingleton(new WizardModuleRegistry(() =>
        [
            new InstallFolderModule(settings.Object, new PreInstallationService()),
            new ComfyFoldersModule(settings.Object),
            new VramProfileModule(),
            new ModelSelectionModule(scanner.Object, estimator.Object),
            new WorkflowSelectionModule(),
            new ShortcutsModule(),
            new DisclaimerModule(),
        ]));

        return session;
    }

    private static Mock<IModelPresenceScanner> EmptyScanner()
    {
        var scanner = new Mock<IModelPresenceScanner>();
        scanner.Setup(s => s.Scan(It.IsAny<ModelScanRequest>())).Returns([]);
        return scanner;
    }

    [Fact]
    public void The_content_stage_renders_its_three_panels_with_the_Changed_callback_wired()
    {
        // Ruling 31 from slice 1: the Changed wiring in RenderModule is what lets a panel edit
        // re-render this page. Deleting any of these three lines leaves every panel test green.
        RegisterContent(EmptyScanner());
        var page = Render<InstallPage>(p => p.Add(x => x.WorkloadId, WorkloadId));

        // Location (folder pre-filled from settings) -> Content.
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Next").Click();

        page.FindComponent<VramProfilePanel>().Instance.Changed.HasDelegate.Should().BeTrue();
        page.FindComponent<ModelSelectionPanel>().Instance.Changed.HasDelegate.Should().BeTrue();
        page.FindComponent<WorkflowSelectionPanel>().Instance.Changed.HasDelegate.Should().BeTrue();
    }

    [Fact]
    public void Changing_the_tier_rescans_the_models_through_the_page()
    {
        // End to end: VRAM panel -> Changed -> page re-render -> ModelSelectionPanel notices -> rescan.
        var scanner = EmptyScanner();
        RegisterContent(scanner);
        var page = Render<InstallPage>(p => p.Add(x => x.WorkloadId, WorkloadId));
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Next").Click();
        var scansBefore = scanner.Invocations.Count(i => i.Method.Name == nameof(IModelPresenceScanner.Scan));

        page.Find("select").Change("16");

        page.FindComponent<ModelSelectionPanel>().Instance.Module.LastScannedTier.Should().Be(16);
        scanner.Invocations.Count(i => i.Method.Name == nameof(IModelPresenceScanner.Scan)).Should().BeGreaterThan(scansBefore);
    }

    [Fact]
    public async Task A_dismissed_preflight_keeps_the_user_on_Confirm()
    {
        RegisterContent(EmptyScanner());
        Services.AddSingleton(Mock.Of<IMismatchedFilePrompt>());
        var preflight = Services.GetRequiredService<IModelPreflight>();
        Mock.Get(preflight).Setup(p => p.RunAsync(It.IsAny<WizardPlan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PreflightResult(false, null));
        var page = Render<InstallPage>(p => p.Add(x => x.WorkloadId, WorkloadId));

        // Location -> Content -> System -> Confirm.
        while (!page.Markup.Contains("Ready to install"))
            page.FindAll("button").Single(b => b.TextContent.Trim() == "Next").Click();
        page.Find(".checkbox input").Change(true); // disclaimer

        await page.FindAll("button").Single(b => b.TextContent.Trim() == "Next").ClickAsync(new MouseEventArgs());

        page.Markup.Should().Contain("Ready to install", "a dismissed dialog must not advance");
        page.Markup.Should().Contain("not started");
        page.Markup.Should().NotContain("Installing");
    }

    [Fact]
    public void The_confirm_summary_reports_tier_models_and_workflows()
    {
        RegisterContent(EmptyScanner());
        var page = Render<InstallPage>(p => p.Add(x => x.WorkloadId, WorkloadId));

        while (!page.Markup.Contains("Ready to install"))
            page.FindAll("button").Single(b => b.TextContent.Trim() == "Next").Click();

        page.Markup.Should().Contain("8 GB").And.Contain("1 of 1 selected");
    }
```

Note for `A_dismissed_preflight_keeps_the_user_on_Confirm`: `RegisterContent` registers the preflight mock as a singleton instance, so `Mock.Get(...)` on the resolved service returns that same mock and the re-`Setup` applies. In `The_disclaimer_gates_the_confirm_stage`, the disclaimer checkbox selector `.checkbox input` must still find the disclaimer for the Fooocus workload; it does, because Fooocus has no Content stage.

- [ ] **Step 2: Run the page tests**

Run: `dotnet test DiffusionNexus.Installer.Tests --filter "FullyQualifiedName~InstallPageTests"`
Expected: all pass. If `Changing_the_tier_rescans_the_models_through_the_page` fails with `LastScannedTier` still 8, the `Changed` wiring on `VramProfilePanel` in `RenderModule` is missing — that is the regression this test exists to catch.

- [ ] **Step 3: Commit and push**

```bash
git add DiffusionNexus.Installer.Tests/Components/InstallPageTests.cs
git commit -m "test(wizard): page-level guards for the Content stage wiring and preflight

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---

### Task 14: Smoke checklist, full verification, and the package-only build

**Files:**
- Modify: `docs/manual-smoke.md`

- [ ] **Step 1: Update the smoke checklist**

In `docs/manual-smoke.md` §1 step 2, replace the sentence beginning `**Expect:** exactly nine cards are enabled` through `...neither of which is in slice 1).` with:

```markdown
   **Expect:** every card is enabled except Config535 — 20 of 21. The DiffusionNexusCore
   workloads (Captioning, Inpainting, Outpainting, Upscaling-Z-Image-Turbo) are not listed at all.
```

Replace §2 step 1's `No model, VRAM, workflow or accelerator screen appears.` with `No Content screen (VRAM, models, workflows) appears.` and append to §2:

```markdown
6. Pick Krea-2-Turbo. **Expect:** after Location comes a Content screen with a VRAM dropdown
   offering exactly 8, 12, 16, 24, 32 GB with 8 preselected, every model ticked and grouped by
   folder, every workflow ticked, and a disk-space line that updates when you untick a model or
   change the tier.
7. Pick Ideogram-4.0. **Expect:** the dropdown offers exactly 24 and 32 GB, 24 preselected.
8. On the Content screen, point the install folder (Back, then edit) at a folder that already
   holds one of the listed models. **Expect:** that row shows "already downloaded".
```

Append to §3:

```markdown
6. Install Wan 2.2 - GGUF at tier 8 into a scratch folder — the heaviest case, 10 models and 26
   links. **Expect:** files land under the right `models\...` folders, the report shows no
   unexplained skips, and no row says "Requires more VRAM" for a model you expected.
7. Re-run the same install over that folder after truncating one downloaded model file to a few
   bytes. **Expect:** pressing Next on Confirm shows ONE dialog listing that file; Continue with it
   ticked re-downloads it; Cancel installation leaves you on Confirm with a notice.
```

- [ ] **Step 2: Run the whole suite and the package-only build**

Run: `dotnet test DiffusionNexus.Installer.Tests`
Expected: all pass, 0 skipped.

Run: `dotnet build -c Release -p:UseLocalSDK=false`
Expected: build succeeds against the published SDK packages. If restore returns 401/403, the local NuGet source needs the `read:packages` PAT — CI is the authoritative gate for this build and runs on push.

- [ ] **Step 3: Commit, push, and open the PR**

```bash
git add docs/manual-smoke.md
git commit -m "docs(smoke): Content stage checks and the 20-of-21 gallery expectation

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
gh pr create --base main --title "Content stage (slice 2): VRAM tier, model and workflow pickers" --body "$(cat <<'EOF'
Adds the wizard's Content stage — VRAM tier (lowest preselected, only the tiers the workload declares), model picker with "already downloaded" markers and a live disk-space estimate, and a workflow picker — unlocking 20 of the 21 Installer-targeted workloads. Config535 stays out on its torch 2.8.0 / CUDA 13.0 pairing.

Also: modules are per-run instances now (transient DI + factory registry), one `ModelPresenceScanner` replaces 1.x's hand-synced scan pair, the gate and the VRAM module share one tier parser, and pre-install file verification shows one mismatch dialog when leaving Confirm.

Spec: `docs/superpowers/specs/2026-09-02-electron-wizard-slice-2-content-stage-design.md`
Plan: `docs/superpowers/plans/2026-09-02-content-stage.md`

Manual smoke owed (docs/manual-smoke.md §2.6–2.8, §3.6–3.7).

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```
