# Catalog-Driven Wizard (Slice 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Electron Installer 3.x install six real workloads (A1111, Forge, Fooocus, ACE-Step, AI-Toolkit, Blank ComfyUI) end to end, through a catalog-driven wizard composed of capability modules.

**Architecture:** A UI-free `Core` project holds the wizard state machine and capability modules; each module declares what it applies to by reading the selected catalog workload, and contributes to a single `InstallationOptions` record that the SDK's `IInstallationOrchestrator` consumes. Modules render into a fixed set of stages, so wizard length stays flat as workload complexity grows. A singleton `InstallSession` owns the running install so a Blazor circuit reconnect cannot kill it.

**Tech Stack:** .NET 10, ElectronNET.Core 0.5.2 + Blazor Server, DiffusionNexus.Installer.SDK 2.0.0-preview.3 (Models, Shared, Services, Catalog), xUnit + FluentAssertions + Moq.

**Spec:** `docs/superpowers/specs/2026-08-29-electron-wizard-slice-1-design.md`

## Global Constraints

- Target framework for every project: `net10.0`.
- SDK package version: `2.0.0-preview.3` exactly. `v2.0.0` is not tagged; do not reference `2.0.0`.
- The SDK is under a different GitHub account. CI restore needs the `PACKAGES_READ_TOKEN` secret. A local `-p:UseLocalSDK=false` restore will 403 with a gh CLI token lacking `read:packages` — **CI is the package-completeness gate**, not a local restore.
- Never reference `DiffusionNexus.Installer.SDK.DataAccess` or `.Database`. Both were deleted in SDK 2.0.
- `DiffusionNexus.Installer.Core` must not reference Blazor, ASP.NET, or ElectronNET. It is headless and testable.
- Do not call `ICatalog.Source`, `.State`, or `.Diagnostics` on a render path — first access can run the catalog seed on the calling thread. Use the `GetXxxAsync` members.
- `InstallLogEntry.Level` is the SDK's own `LogLevel`, which collides with `Microsoft.Extensions.Logging.LogLevel`. Alias it (`using SdkLogLevel = DiffusionNexus.Installer.SDK.Models.Enums.LogLevel;`) wherever both are in scope.
- Branch: `feature/catalog-driven-wizard`. Commit after every task.
- Run tests with `dotnet test DiffusionNexus.Installer.slnx`.

---

### Task 1: Project scaffolding and the SDK seam

**Files:**
- Modify: `Directory.Build.targets`
- Modify: `DiffusionNexus.Installer.LocalSDK.slnx`
- Modify: `DiffusionNexus.Installer.slnx`
- Modify: `DiffusionNexus.Installer.Electron/DiffusionNexus.Installer.Electron.csproj`
- Create: `DiffusionNexus.Installer.Core/DiffusionNexus.Installer.Core.csproj`
- Create: `DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj`
- Test: `DiffusionNexus.Installer.Tests/SeamTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: a `DiffusionNexus.Installer.Core` assembly referencing SDK Models/Shared/Services/Catalog, and a test project referencing Core.

Today the Electron csproj declares no SDK `PackageReference` at all, and `Directory.Build.targets` lists ProjectReferences for five SDK projects, two of which (`DataAccess`, `Database`) no longer exist. Those dead entries are silently skipped on restore, so the repo builds in both modes — this task makes the seam correct, it is not fixing a broken build.

- [ ] **Step 1: Create the Core project**

Create `DiffusionNexus.Installer.Core/DiffusionNexus.Installer.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="DiffusionNexus.Installer.SDK.Models" Version="2.0.0-preview.3" />
    <PackageReference Include="DiffusionNexus.Installer.SDK.Shared" Version="2.0.0-preview.3" />
    <PackageReference Include="DiffusionNexus.Installer.SDK.Services" Version="2.0.0-preview.3" />
    <PackageReference Include="DiffusionNexus.Installer.SDK.Catalog" Version="2.0.0-preview.3" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the test project**

Create `DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FluentAssertions" Version="7.2.2" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
    <PackageReference Include="Moq" Version="4.20.72" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\DiffusionNexus.Installer.Core\DiffusionNexus.Installer.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Add the SDK package references to the Electron project**

In `DiffusionNexus.Installer.Electron/DiffusionNexus.Installer.Electron.csproj`, add to the `ItemGroup` that already holds the ElectronNET references:

```xml
    <PackageReference Include="DiffusionNexus.Installer.SDK.Models" Version="2.0.0-preview.3" />
    <PackageReference Include="DiffusionNexus.Installer.SDK.Shared" Version="2.0.0-preview.3" />
    <PackageReference Include="DiffusionNexus.Installer.SDK.Services" Version="2.0.0-preview.3" />
    <PackageReference Include="DiffusionNexus.Installer.SDK.Catalog" Version="2.0.0-preview.3" />
```

And a new `ItemGroup`:

```xml
  <ItemGroup>
    <ProjectReference Include="..\DiffusionNexus.Installer.Core\DiffusionNexus.Installer.Core.csproj" />
  </ItemGroup>
```

- [ ] **Step 4: Fix the local-SDK redirect**

In `Directory.Build.targets`, replace the whole `<ItemGroup Condition="'$(UseLocalSDK)' == 'true'">` block with:

```xml
  <ItemGroup Condition="'$(UseLocalSDK)' == 'true'">
    <PackageReference Remove="DiffusionNexus.Installer.SDK.Models" />
    <PackageReference Remove="DiffusionNexus.Installer.SDK.Shared" />
    <PackageReference Remove="DiffusionNexus.Installer.SDK.Services" />
    <PackageReference Remove="DiffusionNexus.Installer.SDK.Catalog" />

    <ProjectReference Include="$(LocalSDKPath)\DiffusionNexus.Installer.SDK.Models\DiffusionNexus.Installer.SDK.Models.csproj" />
    <ProjectReference Include="$(LocalSDKPath)\DiffusionNexus.Installer.SDK.Shared\DiffusionNexus.Installer.SDK.Shared.csproj" />
    <ProjectReference Include="$(LocalSDKPath)\DiffusionNexus.Installer.SDK.Services\DiffusionNexus.Installer.SDK.Services.csproj" />
    <ProjectReference Include="$(LocalSDKPath)\DiffusionNexus.Installer.SDK.Catalog\DiffusionNexus.Installer.SDK.Catalog.csproj" />
  </ItemGroup>
```

In `DiffusionNexus.Installer.LocalSDK.slnx`, replace the two `DataAccess` and `Database` `<Project>` lines with a single Catalog line:

```xml
    <Project Path="..\DiffusionNexus.Installer.SDK\DiffusionNexus.Installer.SDK.Catalog\DiffusionNexus.Installer.SDK.Catalog.csproj" />
```

- [ ] **Step 5: Add both new projects to both solution files**

Add to `DiffusionNexus.Installer.slnx` and `DiffusionNexus.Installer.LocalSDK.slnx`:

```xml
    <Project Path="DiffusionNexus.Installer.Core\DiffusionNexus.Installer.Core.csproj" />
    <Project Path="DiffusionNexus.Installer.Tests\DiffusionNexus.Installer.Tests.csproj" />
```

- [ ] **Step 6: Write a seam test**

Create `DiffusionNexus.Installer.Tests/SeamTests.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests;

public class SeamTests
{
    [Fact]
    public void Sdk_model_types_are_reachable_from_core()
    {
        var config = new InstallationConfiguration { Name = "probe" };

        config.WorkloadTarget.Should().Be(WorkloadTargetType.Installer);
        config.Repository.Type.Should().Be(RepositoryType.ComfyUI);
    }

    [Fact]
    public void Catalog_package_is_referenced()
    {
        typeof(ICatalog).Assembly.GetName().Name
            .Should().Be("DiffusionNexus.Installer.SDK.Catalog");
    }
}
```

- [ ] **Step 7: Build and run the tests**

Run: `dotnet test DiffusionNexus.Installer.slnx`
Expected: PASS, 2 tests.

- [ ] **Step 8: Verify the local-SDK path also restores**

Run: `dotnet restore DiffusionNexus.Installer.slnx -p:UseLocalSDK=true --force`
Expected: restores Models, Shared, Services **and Catalog** from `E:\Repos\DiffusionNexus.Installer.SDK`; no errors.

- [ ] **Step 9: Commit**

```bash
git add Directory.Build.targets DiffusionNexus.Installer.slnx DiffusionNexus.Installer.LocalSDK.slnx DiffusionNexus.Installer.Electron/DiffusionNexus.Installer.Electron.csproj DiffusionNexus.Installer.Core DiffusionNexus.Installer.Tests
git commit -m "build: add Core and Tests projects, wire the SDK 2.x seam"
```

---

### Task 2: Catalog source with the embedded seed

**Files:**
- Create: `DiffusionNexus.Installer.Electron/Assets/Catalog/catalog.zip` (binary, downloaded)
- Create: `DiffusionNexus.Installer.Electron/Assets/Catalog/manifest.json` (downloaded)
- Modify: `DiffusionNexus.Installer.Electron/DiffusionNexus.Installer.Electron.csproj`
- Create: `DiffusionNexus.Installer.Core/Catalog/IWorkloadSource.cs`
- Create: `DiffusionNexus.Installer.Core/Catalog/CatalogWorkloadSource.cs`
- Test: `DiffusionNexus.Installer.Tests/Catalog/CatalogWorkloadSourceTests.cs`

**Interfaces:**
- Consumes: `ICatalog` from Task 1's package reference.
- Produces: `IWorkloadSource.GetInstallerWorkloadsAsync(CancellationToken) -> Task<IReadOnlyList<InstallationConfiguration>>`, used by the gallery in Task 12.

- [ ] **Step 1: Download the seed assets**

```bash
gh auth switch --user Into-The-Latent
mkdir -p DiffusionNexus.Installer.Electron/Assets/Catalog
gh release download v1 --repo Into-The-Latent/DiffusionNexus.Catalog \
  --pattern catalog.zip --pattern manifest.json \
  --dir DiffusionNexus.Installer.Electron/Assets/Catalog --clobber
gh auth switch --user Little-God1983
```

- [ ] **Step 2: Embed them**

Add to `DiffusionNexus.Installer.Electron.csproj`:

```xml
  <ItemGroup>
    <EmbeddedResource Include="Assets\Catalog\catalog.zip" LogicalName="catalog.zip" />
    <EmbeddedResource Include="Assets\Catalog\manifest.json" LogicalName="manifest.json" />
  </ItemGroup>
```

- [ ] **Step 3: Write the failing test**

Create `DiffusionNexus.Installer.Tests/Catalog/CatalogWorkloadSourceTests.cs`:

```csharp
using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Catalog;

public class CatalogWorkloadSourceTests
{
    private static InstallationConfiguration Workload(string name, WorkloadTargetType target) =>
        new() { Name = name, WorkloadTarget = target };

    [Fact]
    public async Task Only_installer_targeted_workloads_are_returned()
    {
        var catalog = new Mock<ICatalog>();
        catalog.Setup(c => c.GetWorkloadsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InstallationConfiguration>
            {
                Workload("Blank ComfyUI", WorkloadTargetType.Installer),
                Workload("Inpainting Qwen", WorkloadTargetType.DiffusionNexusCore),
            });

        var source = new CatalogWorkloadSource(catalog.Object);

        var result = await source.GetInstallerWorkloadsAsync();

        result.Should().ContainSingle().Which.Name.Should().Be("Blank ComfyUI");
    }

    [Fact]
    public async Task Legacy_workloads_are_returned_but_flagged()
    {
        var legacy = Workload("Old pack", WorkloadTargetType.Installer);
        legacy.IsLegacy = true;

        var catalog = new Mock<ICatalog>();
        catalog.Setup(c => c.GetWorkloadsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InstallationConfiguration> { legacy });

        var source = new CatalogWorkloadSource(catalog.Object);

        var result = await source.GetInstallerWorkloadsAsync();

        result.Should().ContainSingle().Which.IsLegacy.Should().BeTrue();
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter CatalogWorkloadSourceTests`
Expected: FAIL — `CatalogWorkloadSource` does not exist.

- [ ] **Step 5: Implement**

Create `DiffusionNexus.Installer.Core/Catalog/IWorkloadSource.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Catalog;

/// <summary>Reads the workloads this installer may offer. Hides ICatalog from the UI.</summary>
public interface IWorkloadSource
{
    Task<IReadOnlyList<InstallationConfiguration>> GetInstallerWorkloadsAsync(CancellationToken ct = default);
    Task<byte[]?> GetThumbnailAsync(Guid workloadId, CancellationToken ct = default);
}
```

Create `DiffusionNexus.Installer.Core/Catalog/CatalogWorkloadSource.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;

namespace DiffusionNexus.Installer.Core.Catalog;

/// <summary>
/// Installer-facing view of the catalog. DiffusionNexusCore workloads belong to the main app and
/// are never offered here. Uses only the async ICatalog members: the blocking Source/State
/// properties can run the first-load seed on the calling thread.
/// </summary>
public sealed class CatalogWorkloadSource(ICatalog catalog) : IWorkloadSource
{
    public async Task<IReadOnlyList<InstallationConfiguration>> GetInstallerWorkloadsAsync(CancellationToken ct = default)
    {
        var all = await catalog.GetWorkloadsAsync(ct).ConfigureAwait(false);
        return all.Where(w => w.WorkloadTarget == WorkloadTargetType.Installer).ToList();
    }

    public Task<byte[]?> GetThumbnailAsync(Guid workloadId, CancellationToken ct = default)
        => catalog.ReadThumbnailAsync(workloadId, ct);
}
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter CatalogWorkloadSourceTests`
Expected: PASS, 2 tests.

- [ ] **Step 7: Commit**

```bash
git add DiffusionNexus.Installer.Electron DiffusionNexus.Installer.Core DiffusionNexus.Installer.Tests
git commit -m "feat(catalog): installer-facing workload source with embedded seed assets"
```

---

### Task 3: Workload capability detection

**Files:**
- Create: `DiffusionNexus.Installer.Core/Wizard/WorkloadCapability.cs`
- Create: `DiffusionNexus.Installer.Core/Wizard/WorkloadCapabilities.cs`
- Test: `DiffusionNexus.Installer.Tests/Wizard/WorkloadCapabilitiesTests.cs`

**Interfaces:**
- Consumes: `InstallationConfiguration`.
- Produces: `[Flags] WorkloadCapability` and `WorkloadCapabilities.Detect(InstallationConfiguration) -> WorkloadCapability`, used by the installability gate in Task 9.

This is a pure function of catalog data with no module involvement — asking modules "do you apply?" cannot answer "which modules does this need?" when the module may not be registered.

- [ ] **Step 1: Write the failing test**

Create `DiffusionNexus.Installer.Tests/Wizard/WorkloadCapabilitiesTests.cs`:

```csharp
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Wizard;

public class WorkloadCapabilitiesTests
{
    [Theory]
    [InlineData(RepositoryType.ComfyUI, true)]
    [InlineData(RepositoryType.AIToolkit, true)]
    [InlineData(RepositoryType.A1111, false)]
    [InlineData(RepositoryType.Forge, false)]
    [InlineData(RepositoryType.Fooocus, false)]
    [InlineData(RepositoryType.AceStep, false)]
    public void ComfyFolders_applies_to_comfyui_and_aitoolkit_only(RepositoryType type, bool expected)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = type;

        var has = WorkloadCapabilities.Detect(w).HasFlag(WorkloadCapability.ComfyFolders);

        has.Should().Be(expected);
    }

    [Fact]
    public void Vram_is_detected_from_a_non_empty_profile_string()
    {
        var w = new InstallationConfiguration();
        w.Vram.VramProfiles = "8,12,16,24,32";

        WorkloadCapabilities.Detect(w).Should().HaveFlag(WorkloadCapability.VramProfile);
    }

    [Fact]
    public void Vram_is_not_detected_from_whitespace()
    {
        var w = new InstallationConfiguration();
        w.Vram.VramProfiles = "   ";

        WorkloadCapabilities.Detect(w).Should().NotHaveFlag(WorkloadCapability.VramProfile);
    }

    [Fact]
    public void Content_capabilities_come_from_collection_counts()
    {
        var w = new InstallationConfiguration();
        w.ModelDownloads.Add(new ModelDownload());
        w.GitRepositories.Add(new GitRepository());
        w.Workflows.Add(new ComfUIWorkflow());

        var caps = WorkloadCapabilities.Detect(w);

        caps.Should().HaveFlag(WorkloadCapability.ModelDownloads);
        caps.Should().HaveFlag(WorkloadCapability.CustomNodes);
        caps.Should().HaveFlag(WorkloadCapability.Workflows);
    }

    [Fact]
    public void Accelerators_are_detected_from_either_toggle()
    {
        var triton = new InstallationConfiguration();
        triton.Python.InstallTriton = true;

        var sage = new InstallationConfiguration();
        sage.Python.InstallSageAttention = true;

        WorkloadCapabilities.Detect(triton).Should().HaveFlag(WorkloadCapability.Accelerators);
        WorkloadCapabilities.Detect(sage).Should().HaveFlag(WorkloadCapability.Accelerators);
    }

    [Fact]
    public void A_thin_non_comfy_workload_reports_no_capabilities()
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.Fooocus;

        WorkloadCapabilities.Detect(w).Should().Be(WorkloadCapability.None);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter WorkloadCapabilitiesTests`
Expected: FAIL — `WorkloadCapability` does not exist.

- [ ] **Step 3: Implement**

Create `DiffusionNexus.Installer.Core/Wizard/WorkloadCapability.cs`:

```csharp
namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>
/// What a workload needs the wizard to ask about. Detected from catalog data alone, independent
/// of which modules happen to be registered — that independence is what lets the gallery decide
/// whether a workload is installable at all.
/// </summary>
[Flags]
public enum WorkloadCapability
{
    None           = 0,
    ComfyFolders   = 1 << 0,
    VramProfile    = 1 << 1,
    ModelDownloads = 1 << 2,
    CustomNodes    = 1 << 3,
    Workflows      = 1 << 4,
    Accelerators   = 1 << 5,
}
```

Create `DiffusionNexus.Installer.Core/Wizard/WorkloadCapabilities.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Wizard;

public static class WorkloadCapabilities
{
    /// <summary>Pure function of the workload. No module involvement — see WorkloadCapability.</summary>
    public static WorkloadCapability Detect(InstallationConfiguration workload)
    {
        ArgumentNullException.ThrowIfNull(workload);

        var caps = WorkloadCapability.None;

        // ComfyUI gets a model base folder AND an output folder; AI-Toolkit only writes
        // extra_model_paths.yaml, so it gets the model-folder half of the same module.
        if (workload.Repository.Type is RepositoryType.ComfyUI or RepositoryType.AIToolkit)
            caps |= WorkloadCapability.ComfyFolders;

        if (!string.IsNullOrWhiteSpace(workload.Vram.VramProfiles))
            caps |= WorkloadCapability.VramProfile;

        if (workload.ModelDownloads.Count > 0)
            caps |= WorkloadCapability.ModelDownloads;

        if (workload.GitRepositories.Count > 0)
            caps |= WorkloadCapability.CustomNodes;

        if (workload.Workflows.Count > 0)
            caps |= WorkloadCapability.Workflows;

        if (workload.Python.InstallTriton || workload.Python.InstallSageAttention)
            caps |= WorkloadCapability.Accelerators;

        return caps;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter WorkloadCapabilitiesTests`
Expected: PASS, 11 tests (6 theory cases + 5 facts).

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.Installer.Core/Wizard DiffusionNexus.Installer.Tests/Wizard
git commit -m "feat(wizard): detect workload capabilities from catalog data"
```

---

### Task 4: Module contract, selection state and options draft

**Files:**
- Create: `DiffusionNexus.Installer.Core/Wizard/WizardStage.cs`
- Create: `DiffusionNexus.Installer.Core/Wizard/WizardSelection.cs`
- Create: `DiffusionNexus.Installer.Core/Wizard/InstallationOptionsDraft.cs`
- Create: `DiffusionNexus.Installer.Core/Wizard/ModuleValidation.cs`
- Create: `DiffusionNexus.Installer.Core/Wizard/IWizardModule.cs`
- Test: `DiffusionNexus.Installer.Tests/Wizard/InstallationOptionsDraftTests.cs`

**Interfaces:**
- Consumes: `WorkloadCapability` from Task 3, `InstallationOptions` from the SDK.
- Produces: `IWizardModule`, `WizardSelection`, `InstallationOptionsDraft.ToOptions()`, `ModuleValidation.Ok()` / `.Error(string)`. Every module in Tasks 5-8 implements `IWizardModule`.

- [ ] **Step 1: Write the failing test**

Create `DiffusionNexus.Installer.Tests/Wizard/InstallationOptionsDraftTests.cs`:

```csharp
using DiffusionNexus.Installer.Core.Wizard;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Wizard;

public class InstallationOptionsDraftTests
{
    [Fact]
    public void An_untouched_draft_produces_the_sdk_defaults()
    {
        var options = new InstallationOptionsDraft().ToOptions();

        options.OnlyModelDownload.Should().BeFalse();
        options.CreateDesktopShortcut.Should().BeTrue();
        options.CreateStartMenuShortcut.Should().BeTrue();
        options.SkipVcRuntimeProvisioning.Should().BeFalse();
        options.ExcludedModelIds.Should().BeEmpty();
    }

    [Fact]
    public void Contributed_values_survive_conversion()
    {
        var draft = new InstallationOptionsDraft
        {
            ModelBaseFolder = @"D:\Models",
            OutputFolder = @"D:\Output",
            GenerateExtraModelPaths = true,
            CreateDesktopShortcut = false,
            CpuTorch = true,
            SelectedVramProfile = 16,
        };
        draft.ExcludedModelIds.Add(Guid.Parse("11111111-1111-1111-1111-111111111111"));

        var options = draft.ToOptions();

        options.ModelBaseFolder.Should().Be(@"D:\Models");
        options.OutputFolder.Should().Be(@"D:\Output");
        options.GenerateExtraModelPaths.Should().BeTrue();
        options.CreateDesktopShortcut.Should().BeFalse();
        options.CpuTorch.Should().BeTrue();
        options.SelectedVramProfile.Should().Be(16);
        options.ExcludedModelIds.Should().ContainSingle();
    }

    [Fact]
    public void Url_sets_are_case_insensitive_like_the_sdk_defaults()
    {
        var draft = new InstallationOptionsDraft();
        draft.ForceRedownloadUrls.Add("https://Example.com/A.safetensors");

        draft.ToOptions().ForceRedownloadUrls
            .Should().Contain("https://example.com/a.safetensors");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter InstallationOptionsDraftTests`
Expected: FAIL — `InstallationOptionsDraft` does not exist.

- [ ] **Step 3: Implement the supporting types**

Create `DiffusionNexus.Installer.Core/Wizard/WizardStage.cs`:

```csharp
namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>
/// The fixed screens a wizard run passes through. Modules render into a stage; a stage with no
/// applicable modules is skipped whole, which is what keeps wizard length flat as workload
/// complexity grows.
/// </summary>
public enum WizardStage
{
    Location = 0,
    Content = 1,
    System = 2,
    Confirm = 3,
    Install = 4,
}
```

Create `DiffusionNexus.Installer.Core/Wizard/ModuleValidation.cs`:

```csharp
namespace DiffusionNexus.Installer.Core.Wizard;

// The positional property cannot be named Error: it would collide with the static Error factory
// below (CS0102). ErrorMessage is the property; Error(string) stays the factory.
public sealed record ModuleValidation(bool IsValid, string? ErrorMessage)
{
    public static ModuleValidation Ok() => new(true, null);
    public static ModuleValidation Error(string message) => new(false, message);
}
```

Create `DiffusionNexus.Installer.Core/Wizard/WizardSelection.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>
/// Everything the wizard has learned so far. Modules read from here rather than from each other:
/// a downstream module that needs an upstream answer (ModelSelection needs the VRAM tier) reads
/// the value, not the module that produced it.
/// </summary>
public sealed class WizardSelection
{
    public required InstallationConfiguration Workload { get; init; }

    /// <summary>Where the workload gets installed. Set by the InstallFolder module.</summary>
    public string TargetFolder { get; set; } = string.Empty;

    /// <summary>Chosen VRAM tier in GB, 0 when the workload has no profiles.</summary>
    public int SelectedVramProfile { get; set; }

    public WorkloadCapability Capabilities => WorkloadCapabilities.Detect(Workload);
}
```

Create `DiffusionNexus.Installer.Core/Wizard/InstallationOptionsDraft.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services;

namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>
/// Mutable accumulator that every module contributes to, converted exactly once at Confirm into
/// the SDK's init-only InstallationOptions record.
/// </summary>
public sealed class InstallationOptionsDraft
{
    public int SelectedVramProfile { get; set; }
    public bool VerboseLogging { get; set; }
    public bool SkipVcRuntimeProvisioning { get; set; }
    public bool CreateDesktopShortcut { get; set; } = true;
    public bool CreateStartMenuShortcut { get; set; } = true;
    public string? DesktopShortcutName { get; set; }
    public string? StartMenuShortcutName { get; set; }
    public HashSet<Guid> ExcludedModelIds { get; } = [];
    public HashSet<Guid> ExcludedNodeIds { get; } = [];
    public HashSet<Guid> ExcludedWorkflowIds { get; } = [];
    public HashSet<string> ForceRedownloadUrls { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> TrustedUrls { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Func<string, string, Task<ShortcutConflictResult>>? OnShortcutConflict { get; set; }
    public string? ModelBaseFolder { get; set; }
    public bool GenerateExtraModelPaths { get; set; }
    public bool OverwriteExtraModelPaths { get; set; }
    public Dictionary<string, string> FolderPathOverrides { get; } = [];
    public List<AdditionalFolder> AdditionalFolders { get; } = [];
    public string? OutputFolder { get; set; }
    public bool CpuTorch { get; set; }

    // Qualified deliberately: the SDK defines TWO InstallationOptions types --
    // Models.Installation.InstallationOptions (a class) and Services.InstallationOptions (the
    // record the orchestrator takes). This file imports both namespaces, so the bare name is
    // CS0104-ambiguous.
    public SDK.Services.InstallationOptions ToOptions() => new()
    {
        OnlyModelDownload = false,
        SelectedVramProfile = SelectedVramProfile,
        VerboseLogging = VerboseLogging,
        SkipVcRuntimeProvisioning = SkipVcRuntimeProvisioning,
        CreateDesktopShortcut = CreateDesktopShortcut,
        CreateStartMenuShortcut = CreateStartMenuShortcut,
        DesktopShortcutName = DesktopShortcutName,
        StartMenuShortcutName = StartMenuShortcutName,
        ExcludedModelIds = [.. ExcludedModelIds],
        ExcludedNodeIds = [.. ExcludedNodeIds],
        ExcludedWorkflowIds = [.. ExcludedWorkflowIds],
        ForceRedownloadUrls = new HashSet<string>(ForceRedownloadUrls, StringComparer.OrdinalIgnoreCase),
        TrustedUrls = new HashSet<string>(TrustedUrls, StringComparer.OrdinalIgnoreCase),
        OnShortcutConflict = OnShortcutConflict,
        ModelBaseFolder = ModelBaseFolder,
        GenerateExtraModelPaths = GenerateExtraModelPaths,
        OverwriteExtraModelPaths = OverwriteExtraModelPaths,
        FolderPathOverrides = new Dictionary<string, string>(FolderPathOverrides),
        AdditionalFolders = [.. AdditionalFolders],
        OutputFolder = OutputFolder,
        CpuTorch = CpuTorch,
    };
}
```

Create `DiffusionNexus.Installer.Core/Wizard/IWizardModule.cs`:

```csharp
namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>
/// One capability the wizard can ask about. A module never references another module: it reads
/// what it needs from WizardSelection and is sequenced by Order.
/// <para>
/// A module is a UI concern, not a pipeline step. The pipeline always installs whatever the
/// catalog declares; a module only ever narrows that (Excluded*Ids) or configures it.
/// </para>
/// </summary>
public interface IWizardModule
{
    string Id { get; }

    WizardStage Stage { get; }

    /// <summary>Sequences modules inside a stage. Lower runs and renders first.</summary>
    int Order { get; }

    /// <summary>
    /// The single capability this module satisfies, or <see cref="WorkloadCapability.None"/> for
    /// unconditional modules. Used by the installability gate.
    /// </summary>
    WorkloadCapability Satisfies { get; }

    /// <summary>Reads the selected catalog workload. Never an enum switch on software name.</summary>
    bool AppliesTo(WizardSelection selection);

    Task InitializeAsync(WizardSelection selection, CancellationToken ct = default);

    void Contribute(InstallationOptionsDraft draft);

    ModuleValidation Validate();
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter InstallationOptionsDraftTests`
Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.Installer.Core/Wizard DiffusionNexus.Installer.Tests/Wizard
git commit -m "feat(wizard): module contract, selection state and options draft"
```

---

### Task 5: InstallFolder and Shortcuts modules

**Files:**
- Create: `DiffusionNexus.Installer.Core/Modules/InstallFolderModule.cs`
- Create: `DiffusionNexus.Installer.Core/Modules/ShortcutsModule.cs`
- Test: `DiffusionNexus.Installer.Tests/Modules/UnconditionalModuleTests.cs`

**Interfaces:**
- Consumes: `IWizardModule`, `WizardSelection`, `InstallationOptionsDraft` from Task 4; `IUserSettingsRepository` from the SDK.
- Produces: `InstallFolderModule` (Id `"install-folder"`) and `ShortcutsModule` (Id `"shortcuts"`), both with `Satisfies == WorkloadCapability.None`.

Both are unconditional — every workload needs somewhere to go and gets shortcuts.

- [ ] **Step 1: Write the failing test**

Create `DiffusionNexus.Installer.Tests/Modules/UnconditionalModuleTests.cs`:

```csharp
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services.Settings;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

public class UnconditionalModuleTests
{
    private static WizardSelection Selection(RepositoryType type = RepositoryType.Fooocus)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = type;
        return new WizardSelection { Workload = w };
    }

    private static Mock<IUserSettingsRepository> Settings(string defaultFolder)
    {
        var repo = new Mock<IUserSettingsRepository>();
        repo.Setup(r => r.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings { DefaultTargetInstallFolder = defaultFolder });
        return repo;
    }

    [Theory]
    [InlineData(RepositoryType.ComfyUI)]
    [InlineData(RepositoryType.A1111)]
    [InlineData(RepositoryType.AceStep)]
    public void InstallFolder_applies_to_every_workload(RepositoryType type)
    {
        var module = new InstallFolderModule(Settings("").Object);

        module.AppliesTo(Selection(type)).Should().BeTrue();
        module.Satisfies.Should().Be(WorkloadCapability.None);
        module.Stage.Should().Be(WizardStage.Location);
    }

    [Fact]
    public async Task InstallFolder_seeds_from_the_remembered_default()
    {
        var module = new InstallFolderModule(Settings(@"D:\AI").Object);
        var selection = Selection();

        await module.InitializeAsync(selection);

        module.TargetFolder.Should().Be(@"D:\AI");
    }

    [Fact]
    public async Task InstallFolder_writes_its_answer_back_to_the_selection()
    {
        var module = new InstallFolderModule(Settings("").Object);
        var selection = Selection();
        await module.InitializeAsync(selection);

        module.TargetFolder = @"E:\Installs\Fooocus";
        module.Contribute(new InstallationOptionsDraft());

        selection.TargetFolder.Should().Be(@"E:\Installs\Fooocus");
    }

    [Fact]
    public async Task InstallFolder_rejects_an_empty_path()
    {
        var module = new InstallFolderModule(Settings("").Object);
        await module.InitializeAsync(Selection());

        module.TargetFolder = "   ";

        module.Validate().IsValid.Should().BeFalse();
    }

    [Fact]
    public void Shortcuts_contributes_both_flags_and_the_conflict_callback()
    {
        var module = new ShortcutsModule { CreateDesktopShortcut = false, CustomName = "Fooocus (test)" };
        var draft = new InstallationOptionsDraft();

        module.Contribute(draft);

        draft.CreateDesktopShortcut.Should().BeFalse();
        draft.CreateStartMenuShortcut.Should().BeTrue();
        draft.DesktopShortcutName.Should().Be("Fooocus (test)");
        draft.StartMenuShortcutName.Should().Be("Fooocus (test)");
    }

    [Fact]
    public void Shortcuts_leaves_names_null_when_the_user_did_not_rename()
    {
        var draft = new InstallationOptionsDraft();

        new ShortcutsModule().Contribute(draft);

        draft.DesktopShortcutName.Should().BeNull();
        draft.StartMenuShortcutName.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter UnconditionalModuleTests`
Expected: FAIL — `InstallFolderModule` does not exist.

- [ ] **Step 3: Implement `InstallFolderModule`**

Create `DiffusionNexus.Installer.Core/Modules/InstallFolderModule.cs`:

```csharp
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Services.Settings;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>Where the workload gets installed. Applies to everything.</summary>
public sealed class InstallFolderModule(IUserSettingsRepository settings) : IWizardModule
{
    private WizardSelection? _selection;

    public string Id => "install-folder";
    public WizardStage Stage => WizardStage.Location;
    public int Order => 0;
    public WorkloadCapability Satisfies => WorkloadCapability.None;

    public string TargetFolder { get; set; } = string.Empty;

    public bool AppliesTo(WizardSelection selection) => true;

    public async Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        _selection = selection;
        var user = await settings.GetOrCreateForCurrentUserAsync(ct).ConfigureAwait(false);
        TargetFolder = user.DefaultTargetInstallFolder;
    }

    public void Contribute(InstallationOptionsDraft draft)
    {
        // The target folder is not an InstallationOptions field — the orchestrator takes it as a
        // separate argument — so it lands on the selection instead.
        if (_selection is not null)
            _selection.TargetFolder = TargetFolder;
    }

    public ModuleValidation Validate() =>
        string.IsNullOrWhiteSpace(TargetFolder)
            ? ModuleValidation.Error("Choose a folder to install into.")
            : ModuleValidation.Ok();
}
```

- [ ] **Step 4: Implement `ShortcutsModule`**

Create `DiffusionNexus.Installer.Core/Modules/ShortcutsModule.cs`:

```csharp
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Services;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>Desktop and Start Menu shortcuts, plus the conflict-resolution callback.</summary>
public sealed class ShortcutsModule : IWizardModule
{
    public string Id => "shortcuts";
    public WizardStage Stage => WizardStage.System;
    public int Order => 100;
    public WorkloadCapability Satisfies => WorkloadCapability.None;

    public bool CreateDesktopShortcut { get; set; } = true;
    public bool CreateStartMenuShortcut { get; set; } = true;

    /// <summary>Null or blank leaves the SDK's default name for the repository type.</summary>
    public string? CustomName { get; set; }

    /// <summary>
    /// Set by the host so a name clash can be resolved by the user mid-install. Null overwrites
    /// silently, which is the SDK's documented default.
    /// </summary>
    public Func<string, string, Task<ShortcutConflictResult>>? OnShortcutConflict { get; set; }

    public bool AppliesTo(WizardSelection selection) => true;

    public Task InitializeAsync(WizardSelection selection, CancellationToken ct = default) => Task.CompletedTask;

    public void Contribute(InstallationOptionsDraft draft)
    {
        draft.CreateDesktopShortcut = CreateDesktopShortcut;
        draft.CreateStartMenuShortcut = CreateStartMenuShortcut;

        var name = string.IsNullOrWhiteSpace(CustomName) ? null : CustomName;
        draft.DesktopShortcutName = name;
        draft.StartMenuShortcutName = name;
        draft.OnShortcutConflict = OnShortcutConflict;
    }

    public ModuleValidation Validate() => ModuleValidation.Ok();
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter UnconditionalModuleTests`
Expected: PASS, 8 tests.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.Installer.Core/Modules DiffusionNexus.Installer.Tests/Modules
git commit -m "feat(wizard): install-folder and shortcuts modules"
```

---

### Task 6: ComfyFolders module

**Files:**
- Create: `DiffusionNexus.Installer.Core/Modules/ComfyFoldersModule.cs`
- Test: `DiffusionNexus.Installer.Tests/Modules/ComfyFoldersModuleTests.cs`

**Interfaces:**
- Consumes: `IWizardModule`, `IUserSettingsRepository`.
- Produces: `ComfyFoldersModule` (Id `"comfy-folders"`, `Satisfies == WorkloadCapability.ComfyFolders`) with `SupportsOutputFolder` telling the UI whether to show the output-folder field.

ComfyUI gets a custom model base folder **and** a custom output folder; AI-Toolkit writes `extra_model_paths.yaml` but has no launcher output flag, so it gets only the model half.

- [ ] **Step 1: Write the failing test**

Create `DiffusionNexus.Installer.Tests/Modules/ComfyFoldersModuleTests.cs`:

```csharp
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services.Settings;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

public class ComfyFoldersModuleTests
{
    private static WizardSelection Selection(RepositoryType type)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = type;
        return new WizardSelection { Workload = w };
    }

    private static ComfyFoldersModule Module(string modelFolder = "", string outputFolder = "")
    {
        var repo = new Mock<IUserSettingsRepository>();
        repo.Setup(r => r.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings
            {
                DefaultModelBaseFolder = modelFolder,
                OutputFolder = outputFolder,
            });
        return new ComfyFoldersModule(repo.Object);
    }

    [Theory]
    [InlineData(RepositoryType.ComfyUI, true)]
    [InlineData(RepositoryType.AIToolkit, true)]
    [InlineData(RepositoryType.A1111, false)]
    [InlineData(RepositoryType.Forge, false)]
    [InlineData(RepositoryType.Fooocus, false)]
    [InlineData(RepositoryType.AceStep, false)]
    public void Applies_to_comfyui_and_aitoolkit_only(RepositoryType type, bool expected)
        => Module().AppliesTo(Selection(type)).Should().Be(expected);

    [Fact]
    public async Task Output_folder_is_offered_for_comfyui_but_not_aitoolkit()
    {
        var comfy = Module();
        await comfy.InitializeAsync(Selection(RepositoryType.ComfyUI));

        var toolkit = Module();
        await toolkit.InitializeAsync(Selection(RepositoryType.AIToolkit));

        comfy.SupportsOutputFolder.Should().BeTrue();
        toolkit.SupportsOutputFolder.Should().BeFalse();
    }

    [Fact]
    public async Task Seeds_from_remembered_settings()
    {
        var module = Module(modelFolder: @"D:\Models", outputFolder: @"D:\Out");

        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));

        module.ModelBaseFolder.Should().Be(@"D:\Models");
        module.OutputFolder.Should().Be(@"D:\Out");
    }

    [Fact]
    public async Task A_model_base_folder_turns_on_extra_model_paths_generation()
    {
        var module = Module();
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));
        module.ModelBaseFolder = @"D:\Models";

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);

        draft.ModelBaseFolder.Should().Be(@"D:\Models");
        draft.GenerateExtraModelPaths.Should().BeTrue();
    }

    [Fact]
    public async Task No_model_base_folder_leaves_generation_off_and_the_value_null()
    {
        var module = Module();
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);

        draft.ModelBaseFolder.Should().BeNull();
        draft.GenerateExtraModelPaths.Should().BeFalse();
    }

    [Fact]
    public async Task Output_folder_is_not_contributed_for_aitoolkit_even_if_set()
    {
        var module = Module();
        await module.InitializeAsync(Selection(RepositoryType.AIToolkit));
        module.OutputFolder = @"D:\Out";

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);

        draft.OutputFolder.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter ComfyFoldersModuleTests`
Expected: FAIL — `ComfyFoldersModule` does not exist.

- [ ] **Step 3: Implement**

Create `DiffusionNexus.Installer.Core/Modules/ComfyFoldersModule.cs`:

```csharp
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services.Settings;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>
/// Custom model base folder and custom output folder.
/// <para>
/// The model folder writes extra_model_paths.yaml, which both ComfyUI and AI-Toolkit post-install
/// handlers honour. The output folder becomes --output-directory in the generated ComfyUI
/// launcher script, so it exists for ComfyUI only.
/// </para>
/// </summary>
public sealed class ComfyFoldersModule(IUserSettingsRepository settings) : IWizardModule
{
    public string Id => "comfy-folders";
    public WizardStage Stage => WizardStage.Location;
    public int Order => 10;
    public WorkloadCapability Satisfies => WorkloadCapability.ComfyFolders;

    public string ModelBaseFolder { get; set; } = string.Empty;
    public string OutputFolder { get; set; } = string.Empty;
    public bool OverwriteExtraModelPaths { get; set; }

    /// <summary>True for ComfyUI only. The UI hides the output-folder field when false.</summary>
    public bool SupportsOutputFolder { get; private set; }

    public bool AppliesTo(WizardSelection selection) =>
        selection.Workload.Repository.Type is RepositoryType.ComfyUI or RepositoryType.AIToolkit;

    public async Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        SupportsOutputFolder = selection.Workload.Repository.Type == RepositoryType.ComfyUI;

        var user = await settings.GetOrCreateForCurrentUserAsync(ct).ConfigureAwait(false);
        ModelBaseFolder = user.DefaultModelBaseFolder;
        OutputFolder = SupportsOutputFolder ? user.OutputFolder : string.Empty;
    }

    public void Contribute(InstallationOptionsDraft draft)
    {
        var model = string.IsNullOrWhiteSpace(ModelBaseFolder) ? null : ModelBaseFolder;
        draft.ModelBaseFolder = model;

        // Generating the YAML without a base folder would write an empty mapping, so the two
        // travel together.
        draft.GenerateExtraModelPaths = model is not null;
        draft.OverwriteExtraModelPaths = model is not null && OverwriteExtraModelPaths;

        draft.OutputFolder = SupportsOutputFolder && !string.IsNullOrWhiteSpace(OutputFolder)
            ? OutputFolder
            : null;
    }

    public ModuleValidation Validate() => ModuleValidation.Ok();
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter ComfyFoldersModuleTests`
Expected: PASS, 11 tests.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.Installer.Core/Modules DiffusionNexus.Installer.Tests/Modules
git commit -m "feat(wizard): comfy folders module for model base and output folders"
```

---

### Task 7: GpuPreflight module

**Files:**
- Create: `DiffusionNexus.Installer.Core/Modules/GpuPreflightModule.cs`
- Test: `DiffusionNexus.Installer.Tests/Modules/GpuPreflightModuleTests.cs`

**Interfaces:**
- Consumes: `IGpuDetectionService`, `GpuDetectionResult`, `GpuDetectionState` from the SDK.
- Produces: `GpuPreflightModule` (Id `"gpu-preflight"`, `Satisfies == WorkloadCapability.None`) exposing `CanOfferCpuFallback`, `AcceptCpuOnly`.

Detection is async and must never throw. `Unknown` fails open — never block on an inconclusive probe.

- [ ] **Step 1: Write the failing test**

Create `DiffusionNexus.Installer.Tests/Modules/GpuPreflightModuleTests.cs`:

```csharp
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services.Hardware;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

public class GpuPreflightModuleTests
{
    private static WizardSelection Selection(RepositoryType type)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = type;
        return new WizardSelection { Workload = w };
    }

    private static GpuPreflightModule Module(GpuDetectionState state)
    {
        var gpu = new Mock<IGpuDetectionService>();
        gpu.Setup(g => g.DetectAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GpuDetectionResult(state));
        return new GpuPreflightModule(gpu.Object);
    }

    [Fact]
    public async Task Does_not_apply_when_a_cuda_capable_gpu_is_present()
    {
        var module = Module(GpuDetectionState.CudaCapable);
        var selection = Selection(RepositoryType.ComfyUI);

        await module.InitializeAsync(selection);

        module.AppliesTo(selection).Should().BeFalse();
    }

    [Fact]
    public async Task Fails_open_on_an_inconclusive_probe()
    {
        var module = Module(GpuDetectionState.Unknown);
        var selection = Selection(RepositoryType.ComfyUI);

        await module.InitializeAsync(selection);

        module.AppliesTo(selection).Should().BeFalse();
    }

    [Fact]
    public async Task Applies_and_offers_cpu_fallback_for_comfyui_without_a_gpu()
    {
        var module = Module(GpuDetectionState.NoNvidiaGpu);
        var selection = Selection(RepositoryType.ComfyUI);

        await module.InitializeAsync(selection);

        module.AppliesTo(selection).Should().BeTrue();
        module.CanOfferCpuFallback.Should().BeTrue();
    }

    [Fact]
    public async Task Blocks_non_comfyui_workloads_without_a_gpu()
    {
        var module = Module(GpuDetectionState.NoNvidiaGpu);
        var selection = Selection(RepositoryType.Forge);

        await module.InitializeAsync(selection);

        module.CanOfferCpuFallback.Should().BeFalse();
        module.Validate().IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Cpu_fallback_is_only_valid_once_accepted()
    {
        var module = Module(GpuDetectionState.NoNvidiaGpu);
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));

        module.Validate().IsValid.Should().BeFalse();

        module.AcceptCpuOnly = true;

        module.Validate().IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Accepting_cpu_only_contributes_cpu_torch()
    {
        var module = Module(GpuDetectionState.NoNvidiaGpu);
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));
        module.AcceptCpuOnly = true;

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);

        draft.CpuTorch.Should().BeTrue();
    }

    [Fact]
    public async Task A_driverless_nvidia_card_is_treated_as_no_usable_gpu()
    {
        var module = Module(GpuDetectionState.NvidiaGpuWithoutDriver);
        var selection = Selection(RepositoryType.ComfyUI);

        await module.InitializeAsync(selection);

        module.AppliesTo(selection).Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter GpuPreflightModuleTests`
Expected: FAIL — `GpuPreflightModule` does not exist.

- [ ] **Step 3: Implement**

Create `DiffusionNexus.Installer.Core/Modules/GpuPreflightModule.cs`:

```csharp
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services.Hardware;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>
/// Warns before an install that cannot work. ComfyUI can fall back to a CPU-only torch build;
/// every other workload has to stop.
/// <para>
/// Detection is inconclusive on plenty of real machines, so <see cref="GpuDetectionState.Unknown"/>
/// fails open — an unsure probe must never block an install that would have worked.
/// </para>
/// </summary>
public sealed class GpuPreflightModule(IGpuDetectionService gpuDetection) : IWizardModule
{
    private GpuDetectionState _state = GpuDetectionState.Unknown;

    public string Id => "gpu-preflight";
    public WizardStage Stage => WizardStage.System;
    public int Order => 0;
    public WorkloadCapability Satisfies => WorkloadCapability.None;

    /// <summary>Name of the detected adapter, when the probe found one.</summary>
    public string? GpuName { get; private set; }

    /// <summary>True only for ComfyUI, which ships a CPU launcher and a CPU wheel.</summary>
    public bool CanOfferCpuFallback { get; private set; }

    /// <summary>Set when the user has seen the consequence and chosen to continue on CPU.</summary>
    public bool AcceptCpuOnly { get; set; }

    /// <summary>
    /// The probe result, not the selection, decides this — so it is computed once and read from
    /// both AppliesTo and Contribute rather than passing a fake selection around.
    /// </summary>
    private bool NoUsableGpu =>
        _state is GpuDetectionState.NoNvidiaGpu or GpuDetectionState.NvidiaGpuWithoutDriver;

    public bool AppliesTo(WizardSelection selection) => NoUsableGpu;

    public async Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        var result = await gpuDetection.DetectAsync(ct).ConfigureAwait(false);
        _state = result.State;
        GpuName = result.GpuName;
        CanOfferCpuFallback = selection.Workload.Repository.Type == RepositoryType.ComfyUI;
    }

    public void Contribute(InstallationOptionsDraft draft)
    {
        if (NoUsableGpu && CanOfferCpuFallback && AcceptCpuOnly)
            draft.CpuTorch = true;
    }

    public ModuleValidation Validate()
    {
        if (_state is GpuDetectionState.CudaCapable or GpuDetectionState.Unknown)
            return ModuleValidation.Ok();

        if (!CanOfferCpuFallback)
            return ModuleValidation.Error(
                "No compatible NVIDIA GPU was found. This workload requires one and cannot run on CPU.");

        return AcceptCpuOnly
            ? ModuleValidation.Ok()
            : ModuleValidation.Error("No compatible NVIDIA GPU was found. Accept the CPU-only install to continue.");
    }
}
```

Note: this module's applicability comes from the GPU probe rather than the selection, which is why `InitializeAsync` must run before `AppliesTo` is read — the registry in Task 8 guarantees that ordering.

- [ ] **Step 4: Run the tests**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter GpuPreflightModuleTests`
Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.Installer.Core/Modules DiffusionNexus.Installer.Tests/Modules
git commit -m "feat(wizard): GPU pre-flight module with ComfyUI CPU fallback"
```

---

### Task 8: Module registry, stage composition and the installability gate

**Files:**
- Create: `DiffusionNexus.Installer.Core/Wizard/WizardModuleRegistry.cs`
- Create: `DiffusionNexus.Installer.Core/Wizard/WizardPlan.cs`
- Test: `DiffusionNexus.Installer.Tests/Wizard/WizardModuleRegistryTests.cs`
- Test: `DiffusionNexus.Installer.Tests/Wizard/CapabilityAgreementTests.cs`

**Interfaces:**
- Consumes: `IWizardModule`, `WorkloadCapabilities.Detect`.
- Produces: `WizardModuleRegistry.SatisfiedCapabilities`, `.IsInstallable(InstallationConfiguration)`, `.BuildPlanAsync(WizardSelection, CancellationToken) -> Task<WizardPlan>`; `WizardPlan.Stages` (ordered, non-empty only), `.Modules(WizardStage)`, `.ToOptions()`.

- [ ] **Step 1: Write the failing registry test**

Create `DiffusionNexus.Installer.Tests/Wizard/WizardModuleRegistryTests.cs`:

```csharp
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Wizard;

public class WizardModuleRegistryTests
{
    private sealed class StubModule(
        string id, WizardStage stage, int order, WorkloadCapability satisfies, bool applies) : IWizardModule
    {
        public string Id => id;
        public WizardStage Stage => stage;
        public int Order => order;
        public WorkloadCapability Satisfies => satisfies;
        public bool Initialized { get; private set; }

        public bool AppliesTo(WizardSelection selection) => applies;
        public Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
        {
            Initialized = true;
            return Task.CompletedTask;
        }
        public void Contribute(InstallationOptionsDraft draft) { }
        public ModuleValidation Validate() => ModuleValidation.Ok();
    }

    private static WizardSelection Selection(InstallationConfiguration? workload = null) =>
        new() { Workload = workload ?? new InstallationConfiguration { Name = "x" } };

    [Fact]
    public async Task Only_applicable_modules_land_in_the_plan()
    {
        var yes = new StubModule("yes", WizardStage.Location, 0, WorkloadCapability.None, applies: true);
        var no = new StubModule("no", WizardStage.Location, 1, WorkloadCapability.None, applies: false);
        var registry = new WizardModuleRegistry([yes, no]);

        var plan = await registry.BuildPlanAsync(Selection());

        plan.Modules(WizardStage.Location).Should().ContainSingle().Which.Id.Should().Be("yes");
    }

    [Fact]
    public async Task Empty_stages_are_skipped()
    {
        var location = new StubModule("loc", WizardStage.Location, 0, WorkloadCapability.None, applies: true);
        var registry = new WizardModuleRegistry([location]);

        var plan = await registry.BuildPlanAsync(Selection());

        plan.Stages.Should().Equal(WizardStage.Location, WizardStage.Confirm, WizardStage.Install);
        plan.Stages.Should().NotContain(WizardStage.Content);
    }

    [Fact]
    public async Task Modules_render_in_order_within_a_stage()
    {
        var second = new StubModule("second", WizardStage.Location, 10, WorkloadCapability.None, applies: true);
        var first = new StubModule("first", WizardStage.Location, 0, WorkloadCapability.None, applies: true);
        var registry = new WizardModuleRegistry([second, first]);

        var plan = await registry.BuildPlanAsync(Selection());

        plan.Modules(WizardStage.Location).Select(m => m.Id).Should().Equal("first", "second");
    }

    [Fact]
    public async Task Every_module_is_initialized_before_applicability_is_read()
    {
        var module = new StubModule("m", WizardStage.System, 0, WorkloadCapability.None, applies: true);
        var registry = new WizardModuleRegistry([module]);

        await registry.BuildPlanAsync(Selection());

        module.Initialized.Should().BeTrue();
    }

    [Fact]
    public void A_workload_needing_an_unregistered_capability_is_not_installable()
    {
        var registry = new WizardModuleRegistry(
            [new StubModule("folders", WizardStage.Location, 0, WorkloadCapability.ComfyFolders, applies: true)]);

        var heavy = new InstallationConfiguration();
        heavy.Repository.Type = RepositoryType.ComfyUI;
        heavy.ModelDownloads.Add(new ModelDownload());

        registry.IsInstallable(heavy).Should().BeFalse();
    }

    [Fact]
    public void A_workload_whose_capabilities_are_all_covered_is_installable()
    {
        var registry = new WizardModuleRegistry(
            [new StubModule("folders", WizardStage.Location, 0, WorkloadCapability.ComfyFolders, applies: true)]);

        var blank = new InstallationConfiguration();
        blank.Repository.Type = RepositoryType.ComfyUI;

        registry.IsInstallable(blank).Should().BeTrue();
    }

    [Fact]
    public void A_thin_workload_is_installable_with_no_capability_modules_at_all()
    {
        var registry = new WizardModuleRegistry([]);

        var fooocus = new InstallationConfiguration();
        fooocus.Repository.Type = RepositoryType.Fooocus;

        registry.IsInstallable(fooocus).Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter WizardModuleRegistryTests`
Expected: FAIL — `WizardModuleRegistry` does not exist.

- [ ] **Step 3: Implement `WizardPlan`**

Create `DiffusionNexus.Installer.Core/Wizard/WizardPlan.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Services;

namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>The screens and modules one wizard run will actually show.</summary>
public sealed class WizardPlan
{
    private readonly Dictionary<WizardStage, IReadOnlyList<IWizardModule>> _byStage;

    internal WizardPlan(WizardSelection selection, Dictionary<WizardStage, IReadOnlyList<IWizardModule>> byStage)
    {
        Selection = selection;
        _byStage = byStage;
        Stages = byStage.Keys.OrderBy(s => (int)s).ToList();
    }

    public WizardSelection Selection { get; }

    /// <summary>Ordered, and containing only stages that have something to show.</summary>
    public IReadOnlyList<WizardStage> Stages { get; }

    public IReadOnlyList<IWizardModule> Modules(WizardStage stage) =>
        _byStage.TryGetValue(stage, out var modules) ? modules : [];

    public IEnumerable<IWizardModule> AllModules => _byStage.Values.SelectMany(m => m);

    public IReadOnlyList<ModuleValidation> Validate() =>
        AllModules.Select(m => m.Validate()).Where(v => !v.IsValid).ToList();

    /// <summary>Folds every module's answers into the SDK options record. Called once, at Confirm.</summary>
    public InstallationOptions ToOptions()
    {
        var draft = new InstallationOptionsDraft();
        foreach (var module in AllModules.OrderBy(m => (int)m.Stage).ThenBy(m => m.Order))
            module.Contribute(draft);

        draft.SelectedVramProfile = Selection.SelectedVramProfile;
        return draft.ToOptions();
    }
}
```

- [ ] **Step 4: Implement `WizardModuleRegistry`**

Create `DiffusionNexus.Installer.Core/Wizard/WizardModuleRegistry.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>
/// Every capability module the app knows about. Also the installability gate: the gallery may only
/// offer a workload whose every detected capability has a registered module behind it.
/// </summary>
public sealed class WizardModuleRegistry(IEnumerable<IWizardModule> modules)
{
    private readonly IReadOnlyList<IWizardModule> _modules = modules.ToList();

    /// <summary>The union of what the registered modules can handle.</summary>
    public WorkloadCapability SatisfiedCapabilities =>
        _modules.Aggregate(WorkloadCapability.None, (acc, m) => acc | m.Satisfies);

    /// <summary>
    /// A workload is installable when nothing it needs is missing. Deliberately asks
    /// WorkloadCapabilities.Detect rather than the modules themselves — a module that is not
    /// registered cannot be asked whether it applies.
    /// </summary>
    public bool IsInstallable(InstallationConfiguration workload)
    {
        var needed = WorkloadCapabilities.Detect(workload);
        return (needed & ~SatisfiedCapabilities) == WorkloadCapability.None;
    }

    /// <summary>
    /// Initializes every module against the selection, then keeps the ones that apply. Modules are
    /// initialized before AppliesTo is read because applicability can depend on work done during
    /// initialization (GPU detection, for one).
    /// </summary>
    public async Task<WizardPlan> BuildPlanAsync(WizardSelection selection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);

        foreach (var module in _modules)
            await module.InitializeAsync(selection, ct).ConfigureAwait(false);

        var byStage = _modules
            .Where(m => m.AppliesTo(selection))
            .GroupBy(m => m.Stage)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<IWizardModule>)g.OrderBy(m => m.Order).ToList());

        // Confirm and Install always run: they are the summary and the install itself, not modules.
        byStage[WizardStage.Confirm] = [];
        byStage[WizardStage.Install] = [];

        return new WizardPlan(selection, byStage);
    }
}
```

- [ ] **Step 5: Run the registry tests**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter WizardModuleRegistryTests`
Expected: PASS, 7 tests.

- [ ] **Step 6: Write the capability-agreement test**

This is the test that stops the gate and the runtime from drifting apart as modules are added.

Create `DiffusionNexus.Installer.Tests/Wizard/CapabilityAgreementTests.cs`:

```csharp
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services.Hardware;
using DiffusionNexus.Installer.SDK.Services.Settings;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Wizard;

/// <summary>
/// Detect() decides whether a workload is offered; AppliesTo() decides whether a module renders.
/// If they disagree, a workload is either offered without the panel it needs, or shows a panel the
/// gate never accounted for. These tests pin them together.
/// </summary>
public class CapabilityAgreementTests
{
    private static IUserSettingsRepository Settings()
    {
        var repo = new Mock<IUserSettingsRepository>();
        repo.Setup(r => r.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings());
        return repo.Object;
    }

    private static IGpuDetectionService Gpu(GpuDetectionState state = GpuDetectionState.CudaCapable)
    {
        var gpu = new Mock<IGpuDetectionService>();
        gpu.Setup(g => g.DetectAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GpuDetectionResult(state));
        return gpu.Object;
    }

    private static WizardModuleRegistry Registry() => new(
    [
        new InstallFolderModule(Settings()),
        new ComfyFoldersModule(Settings()),
        new GpuPreflightModule(Gpu()),
        new ShortcutsModule(),
    ]);

    private static InstallationConfiguration Workload(RepositoryType type)
    {
        var w = new InstallationConfiguration { Name = type.ToString() };
        w.Repository.Type = type;
        return w;
    }

    [Theory]
    [InlineData(RepositoryType.A1111)]
    [InlineData(RepositoryType.Forge)]
    [InlineData(RepositoryType.Fooocus)]
    [InlineData(RepositoryType.AceStep)]
    [InlineData(RepositoryType.AIToolkit)]
    [InlineData(RepositoryType.ComfyUI)]
    public void Every_slice_one_workload_is_installable(RepositoryType type)
        => Registry().IsInstallable(Workload(type)).Should().BeTrue();

    [Fact]
    public void A_content_heavy_comfyui_pack_is_not_installable_in_slice_one()
    {
        var pack = Workload(RepositoryType.ComfyUI);
        pack.Vram.VramProfiles = "8,12,16,24,32";
        pack.ModelDownloads.Add(new ModelDownload());

        Registry().IsInstallable(pack).Should().BeFalse();
    }

    [Theory]
    [InlineData(RepositoryType.ComfyUI, true)]
    [InlineData(RepositoryType.AIToolkit, true)]
    [InlineData(RepositoryType.Fooocus, false)]
    public async Task Detect_and_AppliesTo_agree_on_the_comfy_folders_capability(
        RepositoryType type, bool expected)
    {
        var workload = Workload(type);
        var selection = new WizardSelection { Workload = workload };

        var detected = WorkloadCapabilities.Detect(workload).HasFlag(WorkloadCapability.ComfyFolders);

        var plan = await Registry().BuildPlanAsync(selection);
        var rendered = plan.AllModules.Any(m => m.Satisfies == WorkloadCapability.ComfyFolders);

        detected.Should().Be(expected);
        rendered.Should().Be(detected);
    }

    [Fact]
    public async Task A_thin_workload_shows_exactly_the_unconditional_modules()
    {
        var plan = await Registry().BuildPlanAsync(
            new WizardSelection { Workload = Workload(RepositoryType.Fooocus) });

        plan.AllModules.Select(m => m.Id).Should().BeEquivalentTo("install-folder", "shortcuts");
    }

    [Fact]
    public async Task Blank_comfyui_adds_only_the_folders_module()
    {
        var plan = await Registry().BuildPlanAsync(
            new WizardSelection { Workload = Workload(RepositoryType.ComfyUI) });

        plan.AllModules.Select(m => m.Id)
            .Should().BeEquivalentTo("install-folder", "comfy-folders", "shortcuts");
    }
}
```

- [ ] **Step 7: Run the agreement tests**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter CapabilityAgreementTests`
Expected: PASS, 12 tests.

- [ ] **Step 8: Commit**

```bash
git add DiffusionNexus.Installer.Core/Wizard DiffusionNexus.Installer.Tests/Wizard
git commit -m "feat(wizard): module registry, stage composition and installability gate"
```

---

### Task 9: Install session

**Files:**
- Create: `DiffusionNexus.Installer.Core/Install/InstallPhase.cs`
- Create: `DiffusionNexus.Installer.Core/Install/InstallLogLine.cs`
- Create: `DiffusionNexus.Installer.Core/Install/InlineProgress.cs`
- Create: `DiffusionNexus.Installer.Core/Install/IInstallSession.cs`
- Create: `DiffusionNexus.Installer.Core/Install/InstallSession.cs`
- Test: `DiffusionNexus.Installer.Tests/Install/InstallSessionTests.cs`

**Interfaces:**
- Consumes: `IInstallationOrchestrator.InstallAsync(configuration, targetDirectory, options, logProgress, stepProgress, downloadProgress, skipDownloadTokenProvider, cancellationToken)`.
- Produces: `IInstallSession` with `Phase`, `Progress`, `CurrentDownload`, `LogLines`, `Result`, `Changed` event, `StartAsync(WizardPlan, CancellationToken)`, `Cancel()`, `SkipCurrentDownload()`.

This is a **singleton**. A Blazor component lives on a SignalR circuit, and a reconnect or navigation disposes it — but a multi-hour download must survive that. Components subscribe; they never own.

- [ ] **Step 1: Write the failing test**

Create `DiffusionNexus.Installer.Tests/Install/InstallSessionTests.cs`:

```csharp
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services;
using FluentAssertions;
using Moq;
using Xunit;
using SdkLogLevel = DiffusionNexus.Installer.SDK.Models.Enums.LogLevel;

namespace DiffusionNexus.Installer.Tests.Install;

public class InstallSessionTests
{
    private static async Task<WizardPlan> PlanAsync()
    {
        var workload = new InstallationConfiguration { Name = "Fooocus" };
        workload.Repository.Type = RepositoryType.Fooocus;

        var registry = new WizardModuleRegistry([]);
        var plan = await registry.BuildPlanAsync(new WizardSelection { Workload = workload });
        plan.Selection.TargetFolder = @"C:\Installs\Fooocus";
        return plan;
    }

    [Fact]
    public async Task A_successful_run_ends_completed_and_keeps_the_report()
    {
        var orchestrator = new Mock<IInstallationOrchestrator>();
        orchestrator
            .Setup(o => o.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(InstallationResult.Success("done", @"C:\Installs\Fooocus"));

        var session = new InstallSession(orchestrator.Object);

        await session.StartAsync(await PlanAsync());

        session.Phase.Should().Be(InstallPhase.Completed);
        session.Result!.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_second_start_while_running_is_refused()
    {
        var gate = new TaskCompletionSource();
        var orchestrator = new Mock<IInstallationOrchestrator>();
        orchestrator
            .Setup(o => o.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async () => { await gate.Task; return InstallationResult.Success("done"); });

        var session = new InstallSession(orchestrator.Object);
        var plan = await PlanAsync();

        var first = session.StartAsync(plan);

        var second = async () => await session.StartAsync(plan);
        await second.Should().ThrowAsync<InvalidOperationException>();

        gate.SetResult();
        await first;
    }

    [Fact]
    public async Task Log_lines_are_captured_and_bounded()
    {
        var orchestrator = new Mock<IInstallationOrchestrator>();
        orchestrator
            .Setup(o => o.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>>(),
                It.IsAny<CancellationToken>()))
            .Returns((InstallationConfiguration _, string _, InstallationOptions _,
                      IProgress<InstallLogEntry>? log, IProgress<InstallationProgress>? _,
                      IProgress<DownloadProgress>? _, Func<CancellationToken>? _, CancellationToken _) =>
            {
                for (var i = 0; i < InstallSession.MaxLogLines + 50; i++)
                    log!.Report(new InstallLogEntry { Message = $"line {i}", Level = SdkLogLevel.Info });
                return Task.FromResult(InstallationResult.Success("done"));
            });

        var session = new InstallSession(orchestrator.Object);

        await session.StartAsync(await PlanAsync());

        session.LogLines.Count.Should().Be(InstallSession.MaxLogLines);
        session.LogLines.Last().Message.Should().Be($"line {InstallSession.MaxLogLines + 49}");
    }

    [Fact]
    public async Task Log_lines_are_coalesced_rather_than_notified_one_by_one()
    {
        var orchestrator = new Mock<IInstallationOrchestrator>();
        orchestrator
            .Setup(o => o.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>>(),
                It.IsAny<CancellationToken>()))
            .Returns((InstallationConfiguration _, string _, InstallationOptions _,
                      IProgress<InstallLogEntry>? log, IProgress<InstallationProgress>? _,
                      IProgress<DownloadProgress>? _, Func<CancellationToken>? _, CancellationToken _) =>
            {
                for (var i = 0; i < 500; i++)
                    log!.Report(new InstallLogEntry { Message = $"line {i}", Level = SdkLogLevel.Info });
                return Task.FromResult(InstallationResult.Success("done"));
            });

        // A flush interval long enough that no tick can fire during the run isolates the
        // coalescing from timing: only the two phase transitions may notify.
        using var session = new InstallSession(orchestrator.Object, TimeSpan.FromMinutes(10));

        var notifications = 0;
        session.Changed += () => notifications++;

        await session.StartAsync(await PlanAsync());

        notifications.Should().Be(2, "only the start and the terminal transition bypass coalescing");
        session.LogLines.Should().HaveCount(500, "every line is still captured");
    }

    [Fact]
    public async Task Cancellation_lands_as_the_cancelled_phase()
    {
        var orchestrator = new Mock<IInstallationOrchestrator>();
        orchestrator
            .Setup(o => o.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(InstallationResult.Cancelled("cancelled by user"));

        var session = new InstallSession(orchestrator.Object);

        await session.StartAsync(await PlanAsync());

        session.Phase.Should().Be(InstallPhase.Cancelled);
    }

    [Fact]
    public async Task An_unexpected_exception_becomes_a_failed_result_not_a_throw()
    {
        var orchestrator = new Mock<IInstallationOrchestrator>();
        orchestrator
            .Setup(o => o.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));

        var session = new InstallSession(orchestrator.Object);

        await session.StartAsync(await PlanAsync());

        session.Phase.Should().Be(InstallPhase.Failed);
        session.Result!.Message.Should().Contain("disk full");
    }

    [Fact]
    public async Task State_outlives_a_subscriber_that_goes_away()
    {
        var orchestrator = new Mock<IInstallationOrchestrator>();
        orchestrator
            .Setup(o => o.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(InstallationResult.Success("done"));

        var session = new InstallSession(orchestrator.Object);

        var notifications = 0;
        void Handler() => notifications++;
        session.Changed += Handler;
        session.Changed -= Handler;   // the circuit dropped

        await session.StartAsync(await PlanAsync());

        session.Phase.Should().Be(InstallPhase.Completed);
        notifications.Should().Be(0);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter InstallSessionTests`
Expected: FAIL — `InstallSession` does not exist.

- [ ] **Step 3: Implement the supporting types**

Create `DiffusionNexus.Installer.Core/Install/InstallPhase.cs`:

```csharp
namespace DiffusionNexus.Installer.Core.Install;

public enum InstallPhase
{
    Idle,
    Running,
    Completed,
    Failed,
    Cancelled,
}
```

Create `DiffusionNexus.Installer.Core/Install/InstallLogLine.cs`:

```csharp
using SdkLogLevel = DiffusionNexus.Installer.SDK.Models.Enums.LogLevel;

namespace DiffusionNexus.Installer.Core.Install;

public sealed record InstallLogLine(DateTimeOffset Timestamp, string Message, SdkLogLevel Level);
```

Create `DiffusionNexus.Installer.Core/Install/InlineProgress.cs`:

```csharp
namespace DiffusionNexus.Installer.Core.Install;

/// <summary>
/// An <see cref="IProgress{T}"/> that invokes its handler inline, on the reporting thread.
/// <para>
/// <see cref="Progress{T}"/> is deliberately NOT used: it hops through the captured
/// SynchronizationContext, or the thread pool when there is none, so the session's state would lag
/// the reports it was given and a caller that awaited the install could observe a half-filled log.
/// The session does its own coalescing and marshals to the UI itself, so it wants the callback
/// inline and synchronous.
/// </para>
/// </summary>
internal sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
```

Create `DiffusionNexus.Installer.Core/Install/IInstallSession.cs`:

```csharp
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Services;

namespace DiffusionNexus.Installer.Core.Install;

/// <summary>
/// Owns a running installation for the lifetime of the app, not of a UI component.
/// Registered as a singleton: a Blazor circuit reconnect disposes components, and an install that
/// can run for hours must not go with them.
/// </summary>
public interface IInstallSession
{
    InstallPhase Phase { get; }
    InstallationProgress? Progress { get; }
    DownloadProgress? CurrentDownload { get; }
    IReadOnlyList<InstallLogLine> LogLines { get; }
    InstallationResult? Result { get; }

    /// <summary>Raised when any of the above changes. Subscribers re-render; they never own state.</summary>
    event Action? Changed;

    Task StartAsync(WizardPlan plan, CancellationToken ct = default);
    void Cancel();
    void SkipCurrentDownload();
}
```

- [ ] **Step 4: Implement `InstallSession`**

Create `DiffusionNexus.Installer.Core/Install/InstallSession.cs`:

```csharp
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Services;

namespace DiffusionNexus.Installer.Core.Install;

/// <inheritdoc cref="IInstallSession"/>
public sealed class InstallSession : IInstallSession, IDisposable
{
    /// <summary>pip output floods; the buffer is bounded so a long install cannot grow unbounded.</summary>
    public const int MaxLogLines = 5000;

    /// <summary>
    /// How often coalesced changes reach subscribers. Raising Changed per log line would push a
    /// render over the SignalR circuit for every line pip prints, which no browser survives.
    /// </summary>
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromMilliseconds(100);

    private readonly IInstallationOrchestrator _orchestrator;
    private readonly TimeSpan _flushInterval;
    private readonly Lock _gate = new();
    private readonly Queue<InstallLogLine> _log = new();
    private readonly Timer _flushTimer;
    private int _dirty;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _skipDownloadCts;

    public InstallSession(IInstallationOrchestrator orchestrator, TimeSpan? flushInterval = null)
    {
        _orchestrator = orchestrator;
        _flushInterval = flushInterval ?? DefaultFlushInterval;
        _flushTimer = new Timer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public InstallPhase Phase { get; private set; } = InstallPhase.Idle;
    public InstallationProgress? Progress { get; private set; }
    public DownloadProgress? CurrentDownload { get; private set; }
    public InstallationResult? Result { get; private set; }

    public IReadOnlyList<InstallLogLine> LogLines
    {
        get { lock (_gate) return [.. _log]; }
    }

    public event Action? Changed;

    public async Task StartAsync(WizardPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        lock (_gate)
        {
            if (Phase == InstallPhase.Running)
                throw new InvalidOperationException("An installation is already running.");

            Phase = InstallPhase.Running;
            Result = null;
            Progress = null;
            CurrentDownload = null;
            _log.Clear();
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _skipDownloadCts = new CancellationTokenSource();
        NotifyNow();
        _flushTimer.Change(_flushInterval, _flushInterval);

        try
        {
            var result = await _orchestrator.InstallAsync(
                plan.Selection.Workload,
                plan.Selection.TargetFolder,
                plan.ToOptions(),
                new InlineProgress<InstallLogEntry>(OnLog),
                new InlineProgress<InstallationProgress>(OnStep),
                new InlineProgress<DownloadProgress>(OnDownload),
                () => _skipDownloadCts!.Token,
                _cts.Token).ConfigureAwait(false);

            Result = result;
            Phase = result.IsCancelled ? InstallPhase.Cancelled
                  : result.IsSuccess ? InstallPhase.Completed
                  : InstallPhase.Failed;
        }
        catch (OperationCanceledException)
        {
            Result = InstallationResult.Cancelled("Installation cancelled.");
            Phase = InstallPhase.Cancelled;
        }
        catch (Exception ex)
        {
            // The UI must always end with a truthful outcome; an escaping exception would leave
            // the Install screen stuck on "Running" forever.
            Result = InstallationResult.Failure($"Installation failed: {ex.Message}");
            Phase = InstallPhase.Failed;
        }
        finally
        {
            // Stop coalescing before the final notification, so the terminal state is never left
            // sitting in the buffer waiting for a tick that no longer comes.
            _flushTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            NotifyNow();
        }
    }

    public void Cancel() => _cts?.Cancel();

    public void SkipCurrentDownload()
    {
        var cts = _skipDownloadCts;
        if (cts is null || cts.IsCancellationRequested) return;

        cts.Cancel();
        _skipDownloadCts = new CancellationTokenSource();
        NotifyNow();
    }

    private void OnLog(InstallLogEntry entry)
    {
        lock (_gate)
        {
            _log.Enqueue(new InstallLogLine(entry.Timestamp, entry.Message, entry.Level));
            while (_log.Count > MaxLogLines) _log.Dequeue();
        }
        MarkDirty();
    }

    private void OnStep(InstallationProgress progress)
    {
        Progress = progress;
        MarkDirty();
    }

    private void OnDownload(DownloadProgress progress)
    {
        CurrentDownload = progress;
        MarkDirty();
    }

    /// <summary>Records that something changed without waking subscribers yet.</summary>
    private void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

    /// <summary>Timer tick: wake subscribers once if anything changed since the last tick.</summary>
    private void Flush()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 1)
            Changed?.Invoke();
    }

    /// <summary>Phase transitions bypass coalescing — those must never be a tick late.</summary>
    private void NotifyNow()
    {
        Interlocked.Exchange(ref _dirty, 0);
        Changed?.Invoke();
    }

    public void Dispose() => _flushTimer.Dispose();
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter InstallSessionTests`
Expected: PASS, 6 tests.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.Installer.Core/Install DiffusionNexus.Installer.Tests/Install
git commit -m "feat(install): singleton install session that survives circuit reconnects"
```

---

### Task 10: Electron host services — folder picker and modal prompts

**Files:**
- Create: `DiffusionNexus.Installer.Core/Host/IFolderPicker.cs`
- Create: `DiffusionNexus.Installer.Core/Host/IUserPrompt.cs`
- Create: `DiffusionNexus.Installer.Electron/Services/ElectronFolderPicker.cs`
- Create: `DiffusionNexus.Installer.Core/Host/ModalPromptService.cs` (Core, not Electron: it holds no UI type, and Core is where its direct analogue `InstallSession` lives, so it stays unit-testable)
- Create: `DiffusionNexus.Installer.Electron/Components/Shared/PromptModal.razor`
- Test: `DiffusionNexus.Installer.Tests/Host/ModalPromptContractTests.cs`

**Interfaces:**
- Consumes: `IWizardModule` implementations that need a folder.
- Produces: `IFolderPicker.PickFolderAsync(string title, string? startIn, CancellationToken) -> Task<string?>` and `IUserPrompt.ConfirmAsync(string title, string message, string confirmLabel, string cancelLabel, CancellationToken) -> Task<bool>`.

Core owns the interfaces; the Electron project owns the implementations, because they are host-specific.

- [ ] **Step 1: Define the Core contracts**

Create `DiffusionNexus.Installer.Core/Host/IFolderPicker.cs`:

```csharp
namespace DiffusionNexus.Installer.Core.Host;

/// <summary>Native folder selection. Returns null when the user dismisses the dialog.</summary>
public interface IFolderPicker
{
    Task<string?> PickFolderAsync(string title, string? startIn = null, CancellationToken ct = default);
}
```

Create `DiffusionNexus.Installer.Core/Host/IUserPrompt.cs`:

```csharp
namespace DiffusionNexus.Installer.Core.Host;

/// <summary>A yes/no question the pipeline can block on mid-install.</summary>
public interface IUserPrompt
{
    Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmLabel = "Continue",
        string cancelLabel = "Cancel",
        CancellationToken ct = default);
}
```

- [ ] **Step 2: Write the failing contract test**

Create `DiffusionNexus.Installer.Tests/Host/ModalPromptContractTests.cs`:

```csharp
using DiffusionNexus.Installer.Core.Host;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Host;

public class ModalPromptContractTests
{
    [Fact]
    public async Task A_dismissed_folder_dialog_yields_null_not_an_exception()
    {
        var picker = new Mock<IFolderPicker>();
        picker.Setup(p => p.PickFolderAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var chosen = await picker.Object.PickFolderAsync("Pick a folder");

        chosen.Should().BeNull();
    }

    [Fact]
    public async Task A_declined_prompt_yields_false()
    {
        var prompt = new Mock<IUserPrompt>();
        prompt.Setup(p => p.ConfirmAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var answer = await prompt.Object.ConfirmAsync("Overwrite?", "A shortcut with that name exists.");

        answer.Should().BeFalse();
    }
}
```

- [ ] **Step 3: Run the test**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter ModalPromptContractTests`
Expected: PASS, 2 tests (the contracts exist after Step 1).

- [ ] **Step 4: Implement the Electron folder picker**

Create `DiffusionNexus.Installer.Electron/Services/ElectronFolderPicker.cs`:

```csharp
using DiffusionNexus.Installer.Core.Host;
using ElectronNET.API;
using ElectronNET.API.Entities;

namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// Native folder chooser. Falls back to null outside Electron (plain `dotnet run` in a browser),
/// which callers already treat as "user dismissed" — so UI work in a browser stays possible.
/// </summary>
public sealed class ElectronFolderPicker : IFolderPicker
{
    public async Task<string?> PickFolderAsync(string title, string? startIn = null, CancellationToken ct = default)
    {
        if (!HybridSupport.IsElectronActive) return null;

        var window = Electron.WindowManager.BrowserWindows.FirstOrDefault();
        if (window is null) return null;

        var options = new OpenDialogOptions
        {
            Title = title,
            Properties = [OpenDialogProperty.openDirectory, OpenDialogProperty.createDirectory],
        };

        if (!string.IsNullOrWhiteSpace(startIn) && Directory.Exists(startIn))
            options.DefaultPath = startIn;

        var paths = await Electron.Dialog.ShowOpenDialogAsync(window, options);
        return paths is { Length: > 0 } ? paths[0] : null;
    }
}
```

- [ ] **Step 5: Implement the modal prompt service and its component**

Create `DiffusionNexus.Installer.Core/Host/ModalPromptService.cs`:

```csharp
using DiffusionNexus.Installer.Core.Host;

namespace DiffusionNexus.Installer.Core.Host;

/// <summary>
/// Bridges a pipeline call that must block on a human answer to a Blazor modal.
/// Singleton, for the same reason the install session is: the pipeline outlives any component.
/// </summary>
public sealed class ModalPromptService : IUserPrompt
{
    private TaskCompletionSource<bool>? _pending;

    public string Title { get; private set; } = string.Empty;
    public string Message { get; private set; } = string.Empty;
    public string ConfirmLabel { get; private set; } = "Continue";
    public string CancelLabel { get; private set; } = "Cancel";
    public bool IsOpen => _pending is not null;

    public event Action? Changed;

    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmLabel = "Continue",
        string cancelLabel = "Cancel",
        CancellationToken ct = default)
    {
        Title = title;
        Message = message;
        ConfirmLabel = confirmLabel;
        CancelLabel = cancelLabel;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;
        Changed?.Invoke();

        // A cancelled install must not leave the pipeline awaiting an answer nobody will give.
        ct.Register(() => Answer(false));
        return tcs.Task;
    }

    public void Answer(bool confirmed)
    {
        var pending = _pending;
        if (pending is null) return;

        _pending = null;
        pending.TrySetResult(confirmed);
        Changed?.Invoke();
    }
}
```

Create `DiffusionNexus.Installer.Electron/Components/Shared/PromptModal.razor`:

```razor
@using DiffusionNexus.Installer.Core.Host
@implements IDisposable
@inject ModalPromptService Prompts

@if (Prompts.IsOpen)
{
    <div class="modal-backdrop">
        <div class="modal-card" role="dialog" aria-modal="true">
            <h2>@Prompts.Title</h2>
            <p>@Prompts.Message</p>
            <div class="modal-actions">
                <button class="btn-secondary" @onclick="() => Prompts.Answer(false)">@Prompts.CancelLabel</button>
                <button class="btn-primary" @onclick="() => Prompts.Answer(true)">@Prompts.ConfirmLabel</button>
            </div>
        </div>
    </div>
}

@code {
    protected override void OnInitialized() => Prompts.Changed += OnChanged;

    private void OnChanged() => InvokeAsync(StateHasChanged);

    public void Dispose() => Prompts.Changed -= OnChanged;
}
```

- [ ] **Step 6: Build and run all tests**

Run: `dotnet test DiffusionNexus.Installer.slnx`
Expected: PASS, all tests.

- [ ] **Step 7: Commit**

```bash
git add DiffusionNexus.Installer.Core/Host DiffusionNexus.Installer.Electron/Services DiffusionNexus.Installer.Electron/Components/Shared DiffusionNexus.Installer.Tests/Host
git commit -m "feat(host): Electron folder picker and modal prompt service"
```

---

### Task 11: DI wiring and catalog options

**Files:**
- Create: `DiffusionNexus.Installer.Core/CoreServiceCollectionExtensions.cs`
- Modify: `DiffusionNexus.Installer.Electron/Program.cs`
- Test: `DiffusionNexus.Installer.Tests/DependencyInjectionTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2-10.
- Produces: `AddInstallerCore(this IServiceCollection)` registering the module registry, workload source, install session and every slice-1 module.

- [ ] **Step 1: Write the failing test**

Create `DiffusionNexus.Installer.Tests/DependencyInjectionTests.cs`:

```csharp
using DiffusionNexus.Installer.Core;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Installation;
using DiffusionNexus.Installer.SDK.Services.Settings;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DiffusionNexus.Installer.Tests;

public class DependencyInjectionTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IGitService, GitService>();
        services.AddSingleton<IPythonService, PythonService>();
        services.AddInstallationServices();
        services.AddSingleton<IInstallationOrchestrator, InstallationOrchestrator>();
        services.AddDiffusionNexusUserSettings(Path.Combine(Path.GetTempPath(), $"dn-{Guid.NewGuid():N}.json"));
        services.AddDiffusionNexusCatalog(o =>
            o.InstalledCatalogPath = Path.Combine(Path.GetTempPath(), $"dn-catalog-{Guid.NewGuid():N}"));
        services.AddInstallerCore();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void All_slice_one_modules_resolve()
    {
        using var provider = Build();

        var registry = provider.GetRequiredService<WizardModuleRegistry>();

        registry.SatisfiedCapabilities.Should().Be(WorkloadCapability.ComfyFolders);
    }

    [Fact]
    public void The_install_session_is_a_singleton()
    {
        using var provider = Build();

        var first = provider.GetRequiredService<IInstallSession>();
        var second = provider.GetRequiredService<IInstallSession>();

        first.Should().BeSameAs(second);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter DependencyInjectionTests`
Expected: FAIL — `AddInstallerCore` does not exist.

- [ ] **Step 3: Implement the Core registration**

Create `DiffusionNexus.Installer.Core/CoreServiceCollectionExtensions.cs`:

```csharp
using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Installer.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the wizard. Call after AddInstallationServices, AddDiffusionNexusCatalog and
    /// AddDiffusionNexusUserSettings — the modules depend on services those register.
    /// </summary>
    public static IServiceCollection AddInstallerCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IWorkloadSource, CatalogWorkloadSource>();
        services.AddSingleton<IInstallSession, InstallSession>();

        // Slice 1 modules. Adding a slice-2 module here is the only change needed to make the
        // workloads that need it installable -- the gallery gate reads the registry.
        services.AddSingleton<IWizardModule, InstallFolderModule>();
        services.AddSingleton<IWizardModule, ComfyFoldersModule>();
        services.AddSingleton<IWizardModule, GpuPreflightModule>();
        services.AddSingleton<IWizardModule, ShortcutsModule>();

        services.AddSingleton(sp => new WizardModuleRegistry(sp.GetServices<IWizardModule>()));

        return services;
    }
}
```

- [ ] **Step 4: Wire it into `Program.cs`**

In `DiffusionNexus.Installer.Electron/Program.cs`, after `builder.Services.AddSingleton<UpdaterLog>();` add:

```csharp
// SDK core services. IGitService/IPythonService/IProcessRunner are not registered by
// AddInstallationServices -- the host owns them, exactly as the 2.x app does.
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<IGitService, GitService>();
builder.Services.AddSingleton<IPythonService, PythonService>();
builder.Services.AddInstallationServices();
builder.Services.AddSingleton<IInstallationOrchestrator, InstallationOrchestrator>();
builder.Services.AddDiffusionNexusUserSettings();

builder.Services.AddDiffusionNexusCatalog(options =>
{
    // The installer ships the catalog it was built against, so a machine with no catalog yet
    // still has a full workload list before it ever reaches the network.
    var assembly = typeof(Program).Assembly;
    options.EmbeddedArchive = () => assembly.GetManifestResourceStream("catalog.zip")!;
    options.EmbeddedManifest = () => assembly.GetManifestResourceStream("manifest.json")!;

    // Point at a catalog checkout to test content changes before publishing them. A missing
    // path warns and falls back rather than failing to start.
    options.LocalOverridePath = Environment.GetEnvironmentVariable("DIFFUSIONNEXUS_CATALOG_PATH");
});

builder.Services.AddInstallerCore();
builder.Services.AddSingleton<ModalPromptService>();
builder.Services.AddSingleton<IUserPrompt>(sp => sp.GetRequiredService<ModalPromptService>());
builder.Services.AddSingleton<IFolderPicker, ElectronFolderPicker>();
```

Add the matching usings at the top of `Program.cs`:

```csharp
using DiffusionNexus.Installer.Core;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Electron.Services;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Installation;
using DiffusionNexus.Installer.SDK.Services.Settings;
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter DependencyInjectionTests`
Expected: PASS, 2 tests.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.Installer.Core DiffusionNexus.Installer.Electron/Program.cs DiffusionNexus.Installer.Tests/DependencyInjectionTests.cs
git commit -m "feat(di): register the SDK, catalog and wizard core"
```

---

### Task 12: Workload gallery

**Files:**
- Create: `DiffusionNexus.Installer.Core/Gallery/GalleryEntry.cs`
- Create: `DiffusionNexus.Installer.Core/Gallery/GalleryBuilder.cs`
- Create: `DiffusionNexus.Installer.Electron/Components/Pages/Gallery.razor`
- Modify: `DiffusionNexus.Installer.Electron/Components/Pages/Home.razor`
- Test: `DiffusionNexus.Installer.Tests/Gallery/GalleryBuilderTests.cs`

**Interfaces:**
- Consumes: `IWorkloadSource`, `WizardModuleRegistry.IsInstallable`.
- Produces: `GalleryBuilder.BuildAsync(CancellationToken) -> Task<IReadOnlyList<GalleryEntry>>`; `GalleryEntry` with `Workload`, `IsInstallable`, `MissingCapabilities`.

- [ ] **Step 1: Write the failing test**

Create `DiffusionNexus.Installer.Tests/Gallery/GalleryBuilderTests.cs`:

```csharp
using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Models.Enums;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Gallery;

public class GalleryBuilderTests
{
    private sealed class FoldersModule : IWizardModule
    {
        public string Id => "comfy-folders";
        public WizardStage Stage => WizardStage.Location;
        public int Order => 10;
        public WorkloadCapability Satisfies => WorkloadCapability.ComfyFolders;
        public bool AppliesTo(WizardSelection s) => true;
        public Task InitializeAsync(WizardSelection s, CancellationToken ct = default) => Task.CompletedTask;
        public void Contribute(InstallationOptionsDraft d) { }
        public ModuleValidation Validate() => ModuleValidation.Ok();
    }

    private static InstallationConfiguration Workload(string name, RepositoryType type)
    {
        var w = new InstallationConfiguration { Name = name };
        w.Repository.Type = type;
        return w;
    }

    private static GalleryBuilder Builder(params InstallationConfiguration[] workloads)
    {
        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(workloads);
        return new GalleryBuilder(source.Object, new WizardModuleRegistry([new FoldersModule()]));
    }

    [Fact]
    public async Task Thin_and_blank_workloads_are_installable()
    {
        var entries = await Builder(
            Workload("Fooocus", RepositoryType.Fooocus),
            Workload("Blank ComfyUI", RepositoryType.ComfyUI)).BuildAsync();

        entries.Should().OnlyContain(e => e.IsInstallable);
    }

    [Fact]
    public async Task A_content_pack_is_listed_but_not_installable()
    {
        var pack = Workload("Krea 2 Turbo", RepositoryType.ComfyUI);
        pack.ModelDownloads.Add(new ModelDownload());
        pack.Vram.VramProfiles = "8,12,16";

        var entries = await Builder(pack).BuildAsync();

        var entry = entries.Should().ContainSingle().Subject;
        entry.IsInstallable.Should().BeFalse();
        entry.MissingCapabilities.Should().HaveFlag(WorkloadCapability.ModelDownloads);
        entry.MissingCapabilities.Should().HaveFlag(WorkloadCapability.VramProfile);
        entry.MissingCapabilities.Should().NotHaveFlag(WorkloadCapability.ComfyFolders);
    }

    [Fact]
    public async Task Installable_entries_sort_before_unavailable_ones()
    {
        var pack = Workload("Krea 2 Turbo", RepositoryType.ComfyUI);
        pack.ModelDownloads.Add(new ModelDownload());

        var entries = await Builder(pack, Workload("Fooocus", RepositoryType.Fooocus)).BuildAsync();

        entries[0].Workload.Name.Should().Be("Fooocus");
    }

    [Fact]
    public async Task Legacy_workloads_sort_last_among_their_peers()
    {
        var legacy = Workload("Old ComfyUI pack", RepositoryType.ComfyUI);
        legacy.IsLegacy = true;

        var entries = await Builder(legacy, Workload("Blank ComfyUI", RepositoryType.ComfyUI)).BuildAsync();

        entries.Last().Workload.Name.Should().Be("Old ComfyUI pack");
    }

    [Fact]
    public async Task Workflow_type_travels_to_the_entry_for_filtering()
    {
        var audio = Workload("ACE-Step", RepositoryType.AceStep);
        audio.WorkflowType = WorkflowType.Audio;

        var entries = await Builder(audio).BuildAsync();

        entries.Should().ContainSingle().Which.Workload.WorkflowType.Should().Be(WorkflowType.Audio);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter GalleryBuilderTests`
Expected: FAIL — `GalleryBuilder` does not exist.

- [ ] **Step 3: Implement**

Create `DiffusionNexus.Installer.Core/Gallery/GalleryEntry.cs`:

```csharp
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Gallery;

/// <summary>One card in the workload gallery.</summary>
public sealed record GalleryEntry(
    InstallationConfiguration Workload,
    bool IsInstallable,
    WorkloadCapability MissingCapabilities);
```

Create `DiffusionNexus.Installer.Core/Gallery/GalleryBuilder.cs`:

```csharp
using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Wizard;

namespace DiffusionNexus.Installer.Core.Gallery;

/// <summary>
/// Turns the catalog into cards. Workloads whose capabilities are not yet covered stay visible but
/// disabled: hiding them would misrepresent what the catalog actually contains.
/// </summary>
public sealed class GalleryBuilder(IWorkloadSource source, WizardModuleRegistry registry)
{
    public async Task<IReadOnlyList<GalleryEntry>> BuildAsync(CancellationToken ct = default)
    {
        var workloads = await source.GetInstallerWorkloadsAsync(ct).ConfigureAwait(false);

        return workloads
            .Select(w =>
            {
                var needed = WorkloadCapabilities.Detect(w);
                var missing = needed & ~registry.SatisfiedCapabilities;
                return new GalleryEntry(w, missing == WorkloadCapability.None, missing);
            })
            .OrderByDescending(e => e.IsInstallable)
            .ThenBy(e => e.Workload.IsLegacy)
            .ThenByDescending(e => e.Workload.IsReleaseConfig)
            .ThenBy(e => e.Workload.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
```

Register it in `CoreServiceCollectionExtensions.AddInstallerCore`, before the registry registration:

```csharp
        services.AddSingleton<Gallery.GalleryBuilder>();
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter GalleryBuilderTests`
Expected: PASS, 5 tests.

- [ ] **Step 5: Build the gallery page**

Create `DiffusionNexus.Installer.Electron/Components/Pages/Gallery.razor`:

```razor
@page "/"
@using DiffusionNexus.Installer.Core.Gallery
@using DiffusionNexus.Installer.SDK.Models.Configuration
@using DiffusionNexus.Installer.SDK.Models.Enums
@inject GalleryBuilder Builder
@inject NavigationManager Nav

<PageTitle>Choose a workload</PageTitle>

<h1>Choose a workload</h1>

@if (_entries is null)
{
    <p>Loading the catalog...</p>
}
else if (_entries.Count == 0)
{
    <p>No workloads are available. The catalog could not be read.</p>
}
else
{
    <div class="filters">
        <span class="filter-label">Type</span>
        <button class="@TypeClass(null)" @onclick="() => _type = null">All</button>
        @foreach (var type in Enum.GetValues<WorkflowType>())
        {
            <button class="@TypeClass(type)" @onclick="() => _type = type">@type</button>
        }
    </div>

    <div class="filters">
        <span class="filter-label">Software</span>
        <button class="@SoftwareClass(null)" @onclick="() => _software = null">All</button>
        @foreach (var software in AvailableSoftware())
        {
            <button class="@SoftwareClass(software)" @onclick="() => _software = software">@software</button>
        }
    </div>

    <div class="gallery">
        @foreach (var entry in Filtered())
        {
            <div class="card @(entry.IsInstallable ? "" : "card-disabled")">
                <h2>@entry.Workload.Name</h2>
                <p class="card-software">@entry.Workload.Repository.Type — @entry.Workload.WorkflowType</p>
                <p class="card-description">@entry.Workload.Description</p>

                @if (entry.IsInstallable)
                {
                    <button class="btn-primary" @onclick="() => Install(entry)">Install</button>
                }
                else
                {
                    <p class="card-unavailable">Coming soon — needs @entry.MissingCapabilities</p>
                }
            </div>
        }
    </div>
}

@code {
    private IReadOnlyList<GalleryEntry>? _entries;
    private WorkflowType? _type;
    private RepositoryType? _software;

    protected override async Task OnInitializedAsync() => _entries = await Builder.BuildAsync();

    private IEnumerable<GalleryEntry> Filtered() => _entries!
        .Where(e => _type is null || e.Workload.WorkflowType == _type)
        .Where(e => _software is null || e.Workload.Repository.Type == _software);

    // Driven by what the catalog actually contains, not by every value the enum can hold, so an
    // unused software never shows an empty filter.
    private IEnumerable<RepositoryType> AvailableSoftware() => _entries!
        .Select(e => e.Workload.Repository.Type)
        .Distinct()
        .OrderBy(t => t.ToString(), StringComparer.CurrentCultureIgnoreCase);

    private string TypeClass(WorkflowType? type) => _type == type ? "filter-active" : "filter";

    private string SoftwareClass(RepositoryType? software) =>
        _software == software ? "filter-active" : "filter";

    private void Install(GalleryEntry entry) => Nav.NavigateTo($"/install/{entry.Workload.Id}");
}
```

Delete the `@page "/"` directive from `Components/Pages/Home.razor` (Gallery now owns the root route), or delete `Home.razor` entirely if it holds nothing else.

- [ ] **Step 6: Build**

Run: `dotnet build DiffusionNexus.Installer.slnx`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add DiffusionNexus.Installer.Core/Gallery DiffusionNexus.Installer.Electron/Components DiffusionNexus.Installer.Tests/Gallery
git commit -m "feat(gallery): catalog-driven workload gallery with installability gate"
```

---

### Task 13: Stage navigation

**Files:**
- Create: `DiffusionNexus.Installer.Core/Wizard/WizardRun.cs`
- Test: `DiffusionNexus.Installer.Tests/Wizard/WizardRunTests.cs`

**Interfaces:**
- Consumes: `WizardPlan`, `WizardModuleRegistry`, `IWorkloadSource`.
- Produces: `WizardRun` with `CurrentStage`, `CurrentModules`, `CanGoNext`, `CanGoBack`, `TryNext()`, `Back()`, `ValidationErrors`. Task 15 builds the page that drives it.

> **Scope note:** the wizard host page (`Install.razor`) is deliberately NOT in this task. It
> references the four module panels from Task 14 and the two stage components from Task 15, so
> creating it here would not compile. It lands in Task 15.

- [ ] **Step 1: Write the failing test**

Create `DiffusionNexus.Installer.Tests/Wizard/WizardRunTests.cs`:

```csharp
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Wizard;

public class WizardRunTests
{
    private sealed class GateModule(WizardStage stage, bool valid) : IWizardModule
    {
        public string Id => $"gate-{stage}";
        public WizardStage Stage => stage;
        public int Order => 0;
        public WorkloadCapability Satisfies => WorkloadCapability.None;
        public bool AppliesTo(WizardSelection s) => true;
        public Task InitializeAsync(WizardSelection s, CancellationToken ct = default) => Task.CompletedTask;
        public void Contribute(InstallationOptionsDraft d) { }
        public ModuleValidation Validate() =>
            valid ? ModuleValidation.Ok() : ModuleValidation.Error("not ready");
    }

    private static async Task<WizardRun> RunAsync(params IWizardModule[] modules)
    {
        var workload = new InstallationConfiguration { Name = "Fooocus" };
        workload.Repository.Type = RepositoryType.Fooocus;

        var plan = await new WizardModuleRegistry(modules)
            .BuildPlanAsync(new WizardSelection { Workload = workload });

        return new WizardRun(plan);
    }

    [Fact]
    public async Task Starts_on_the_first_populated_stage()
    {
        var run = await RunAsync(new GateModule(WizardStage.System, valid: true));

        run.CurrentStage.Should().Be(WizardStage.System);
    }

    [Fact]
    public async Task Advances_through_the_planned_stages_only()
    {
        var run = await RunAsync(new GateModule(WizardStage.Location, valid: true));

        run.CurrentStage.Should().Be(WizardStage.Location);
        run.TryNext().Should().BeTrue();
        run.CurrentStage.Should().Be(WizardStage.Confirm);
        run.TryNext().Should().BeTrue();
        run.CurrentStage.Should().Be(WizardStage.Install);
    }

    [Fact]
    public async Task Cannot_advance_past_an_invalid_stage()
    {
        var run = await RunAsync(new GateModule(WizardStage.Location, valid: false));

        run.CanGoNext.Should().BeFalse();
        run.TryNext().Should().BeFalse();
        run.CurrentStage.Should().Be(WizardStage.Location);
        run.ValidationErrors.Should().ContainSingle().Which.Should().Be("not ready");
    }

    [Fact]
    public async Task Cannot_go_back_from_the_first_stage()
    {
        var run = await RunAsync(new GateModule(WizardStage.Location, valid: true));

        run.CanGoBack.Should().BeFalse();
    }

    [Fact]
    public async Task Cannot_go_back_out_of_the_install_stage()
    {
        var run = await RunAsync(new GateModule(WizardStage.Location, valid: true));
        run.TryNext();
        run.TryNext();

        run.CurrentStage.Should().Be(WizardStage.Install);
        run.CanGoBack.Should().BeFalse();
    }

    [Fact]
    public async Task Back_returns_to_the_previous_planned_stage()
    {
        var run = await RunAsync(new GateModule(WizardStage.Location, valid: true));
        run.TryNext();

        run.Back();

        run.CurrentStage.Should().Be(WizardStage.Location);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter WizardRunTests`
Expected: FAIL — `WizardRun` does not exist.

- [ ] **Step 3: Implement**

Create `DiffusionNexus.Installer.Core/Wizard/WizardRun.cs`:

```csharp
namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>Position within one wizard run. Navigation only ever visits planned stages.</summary>
public sealed class WizardRun(WizardPlan plan)
{
    private int _index;

    public WizardPlan Plan => plan;

    public WizardStage CurrentStage => plan.Stages[_index];

    public IReadOnlyList<IWizardModule> CurrentModules => plan.Modules(CurrentStage);

    public IReadOnlyList<string> ValidationErrors =>
        CurrentModules.Select(m => m.Validate())
            .Where(v => !v.IsValid)
            .Select(v => v.ErrorMessage!)
            .ToList();

    public bool CanGoNext => _index < plan.Stages.Count - 1 && ValidationErrors.Count == 0;

    /// <summary>Once the install has started there is nothing to go back to.</summary>
    public bool CanGoBack => _index > 0 && CurrentStage != WizardStage.Install;

    public bool TryNext()
    {
        if (!CanGoNext) return false;
        _index++;
        return true;
    }

    public void Back()
    {
        if (CanGoBack) _index--;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test DiffusionNexus.Installer.slnx --filter WizardRunTests`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.Installer.Core/Wizard DiffusionNexus.Installer.Electron/Components DiffusionNexus.Installer.Tests/Wizard
git commit -m "feat(wizard): stage navigation and the wizard host page"
```

---

### Task 14: Module panels

**Files:**
- Create: `DiffusionNexus.Installer.Electron/Components/Wizard/InstallFolderPanel.razor`
- Create: `DiffusionNexus.Installer.Electron/Components/Wizard/ComfyFoldersPanel.razor`
- Create: `DiffusionNexus.Installer.Electron/Components/Wizard/GpuPreflightPanel.razor`
- Create: `DiffusionNexus.Installer.Electron/Components/Wizard/ShortcutsPanel.razor`

**Interfaces:**
- Consumes: the four slice-1 modules from Tasks 5-7, `IFolderPicker` from Task 10.
- Produces: components named in Task 13's `RenderModule` switch.

These are thin views over module state — the logic already has tests, so these carry none of their own.

- [ ] **Step 1: Install folder panel**

Create `DiffusionNexus.Installer.Electron/Components/Wizard/InstallFolderPanel.razor`:

```razor
@using DiffusionNexus.Installer.Core.Host
@using DiffusionNexus.Installer.Core.Modules
@inject IFolderPicker FolderPicker

<section class="panel">
    <h2>Install location</h2>
    <p class="panel-hint">Where the workload gets installed.</p>
    <div class="path-row">
        <input type="text" value="@Module.TargetFolder"
               @oninput="e => Module.TargetFolder = e.Value?.ToString() ?? string.Empty" />
        <button class="btn-secondary" @onclick="Browse">Browse...</button>
    </div>
</section>

@code {
    [Parameter, EditorRequired] public InstallFolderModule Module { get; set; } = default!;

    private async Task Browse()
    {
        var chosen = await FolderPicker.PickFolderAsync("Select the install folder", Module.TargetFolder);
        if (chosen is not null) Module.TargetFolder = chosen;
    }
}
```

- [ ] **Step 2: ComfyUI folders panel**

Create `DiffusionNexus.Installer.Electron/Components/Wizard/ComfyFoldersPanel.razor`:

```razor
@using DiffusionNexus.Installer.Core.Host
@using DiffusionNexus.Installer.Core.Modules
@inject IFolderPicker FolderPicker

<section class="panel">
    <h2>Model and output folders</h2>

    <label>Model library folder <span class="panel-hint">(optional)</span></label>
    <p class="panel-hint">
        Point this at an existing model library and the installer writes an
        <code>extra_model_paths.yaml</code> so the workload reads from it instead of duplicating
        downloads.
    </p>
    <div class="path-row">
        <input type="text" value="@Module.ModelBaseFolder"
               @oninput="e => Module.ModelBaseFolder = e.Value?.ToString() ?? string.Empty" />
        <button class="btn-secondary" @onclick="BrowseModels">Browse...</button>
    </div>

    @if (!string.IsNullOrWhiteSpace(Module.ModelBaseFolder))
    {
        <label class="checkbox">
            <input type="checkbox" checked="@Module.OverwriteExtraModelPaths"
                   @onchange="e => Module.OverwriteExtraModelPaths = (bool)(e.Value ?? false)" />
            Overwrite an existing extra_model_paths.yaml
        </label>
    }

    @if (Module.SupportsOutputFolder)
    {
        <label>Output folder <span class="panel-hint">(optional)</span></label>
        <p class="panel-hint">Generated images and videos are written here instead of inside the install.</p>
        <div class="path-row">
            <input type="text" value="@Module.OutputFolder"
                   @oninput="e => Module.OutputFolder = e.Value?.ToString() ?? string.Empty" />
            <button class="btn-secondary" @onclick="BrowseOutput">Browse...</button>
        </div>
    }
</section>

@code {
    [Parameter, EditorRequired] public ComfyFoldersModule Module { get; set; } = default!;

    private async Task BrowseModels()
    {
        var chosen = await FolderPicker.PickFolderAsync("Select your model library folder", Module.ModelBaseFolder);
        if (chosen is not null) Module.ModelBaseFolder = chosen;
    }

    private async Task BrowseOutput()
    {
        var chosen = await FolderPicker.PickFolderAsync("Select the output folder", Module.OutputFolder);
        if (chosen is not null) Module.OutputFolder = chosen;
    }
}
```

- [ ] **Step 3: GPU pre-flight panel**

Create `DiffusionNexus.Installer.Electron/Components/Wizard/GpuPreflightPanel.razor`:

```razor
@using DiffusionNexus.Installer.Core.Modules

<section class="panel panel-warning">
    <h2>No compatible GPU found</h2>

    @if (Module.CanOfferCpuFallback)
    {
        <p>
            No usable NVIDIA GPU was detected@(Module.GpuName is null ? "" : $" (found: {Module.GpuName})").
            ComfyUI can run on CPU, but generation will be very slow.
        </p>
        <label class="checkbox">
            <input type="checkbox" checked="@Module.AcceptCpuOnly"
                   @onchange="e => Module.AcceptCpuOnly = (bool)(e.Value ?? false)" />
            Install the CPU-only build anyway
        </label>
    }
    else
    {
        <p>
            No usable NVIDIA GPU was detected@(Module.GpuName is null ? "" : $" (found: {Module.GpuName})").
            This workload requires one and cannot run on CPU.
        </p>
    }
</section>

@code {
    [Parameter, EditorRequired] public GpuPreflightModule Module { get; set; } = default!;
}
```

- [ ] **Step 4: Shortcuts panel**

Create `DiffusionNexus.Installer.Electron/Components/Wizard/ShortcutsPanel.razor`:

```razor
@using DiffusionNexus.Installer.Core.Modules

<section class="panel">
    <h2>Shortcuts</h2>

    <label class="checkbox">
        <input type="checkbox" checked="@Module.CreateDesktopShortcut"
               @onchange="e => Module.CreateDesktopShortcut = (bool)(e.Value ?? false)" />
        Create a desktop shortcut
    </label>

    <label class="checkbox">
        <input type="checkbox" checked="@Module.CreateStartMenuShortcut"
               @onchange="e => Module.CreateStartMenuShortcut = (bool)(e.Value ?? false)" />
        Create a Start Menu shortcut
    </label>

    <label>Shortcut name <span class="panel-hint">(optional)</span></label>
    <input type="text" value="@Module.CustomName"
           @oninput="e => Module.CustomName = e.Value?.ToString()" placeholder="Use the default name" />
</section>

@code {
    [Parameter, EditorRequired] public ShortcutsModule Module { get; set; } = default!;
}
```

- [ ] **Step 5: Build**

Run: `dotnet build DiffusionNexus.Installer.slnx`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.Installer.Electron/Components/Wizard
git commit -m "feat(wizard): panels for the four slice-1 modules"
```

---

### Task 15: Wizard host page, Confirm and Install stages

**Files:**
- Create: `DiffusionNexus.Installer.Electron/Components/Wizard/ConfirmStage.razor`
- Create: `DiffusionNexus.Installer.Electron/Components/Wizard/InstallStage.razor`
- Create: `DiffusionNexus.Installer.Electron/Components/Pages/Install.razor` (moved here from Task 13 — it references Task 14's panels and this task's stage components, so it cannot compile earlier)
- Modify: `DiffusionNexus.Installer.Electron/Components/Layout/MainLayout.razor`

**Interfaces:**
- Consumes: `WizardRun`, `IInstallSession`, `ModalPromptService`.
- Produces: the two components Task 13's host renders for the last two stages.

`InstallStage` renders **from** the session, never owns it — that is what makes a circuit reconnect harmless.

- [ ] **Step 1: Confirm stage**

Create `DiffusionNexus.Installer.Electron/Components/Wizard/ConfirmStage.razor`:

```razor
@using DiffusionNexus.Installer.Core.Wizard

<section class="panel">
    <h2>Ready to install</h2>

    <dl class="summary">
        <dt>Workload</dt>
        <dd>@Run.Plan.Selection.Workload.Name</dd>

        <dt>Software</dt>
        <dd>@Run.Plan.Selection.Workload.Repository.Type</dd>

        <dt>Install folder</dt>
        <dd>@Run.Plan.Selection.TargetFolder</dd>

        <dt>Python</dt>
        <dd>@Run.Plan.Selection.Workload.Python.PythonVersion</dd>

        @{
            var options = Run.Plan.ToOptions();
        }

        @if (options.ModelBaseFolder is not null)
        {
            <dt>Model library</dt>
            <dd>@options.ModelBaseFolder</dd>
        }

        @if (options.OutputFolder is not null)
        {
            <dt>Output folder</dt>
            <dd>@options.OutputFolder</dd>
        }

        @if (options.CpuTorch)
        {
            <dt>Torch</dt>
            <dd>CPU-only build</dd>
        }

        <dt>Shortcuts</dt>
        <dd>
            @(options.CreateDesktopShortcut ? "Desktop" : null)
            @(options.CreateDesktopShortcut && options.CreateStartMenuShortcut ? " + " : null)
            @(options.CreateStartMenuShortcut ? "Start Menu" : null)
            @(!options.CreateDesktopShortcut && !options.CreateStartMenuShortcut ? "None" : null)
        </dd>
    </dl>
</section>

@code {
    [Parameter, EditorRequired] public WizardRun Run { get; set; } = default!;
}
```

- [ ] **Step 2: Install stage**

Create `DiffusionNexus.Installer.Electron/Components/Wizard/InstallStage.razor`:

```razor
@using DiffusionNexus.Installer.Core.Install
@using DiffusionNexus.Installer.Core.Wizard
@implements IDisposable
@inject IInstallSession Session

<section class="panel">
    <h2>Installing</h2>

    @if (Session.Progress is { } progress)
    {
        <p>Step @(progress.StepIndex + 1) of @progress.TotalSteps — @progress.CurrentStep</p>
        <progress max="100" value="@progress.ProgressPercentage"></progress>
    }

    @if (Session.CurrentDownload is { IsActive: true } download)
    {
        <p class="download">
            @download.DownloadedSizeText of @download.TotalSizeText at @download.SpeedText
            <button class="btn-secondary" @onclick="() => Session.SkipCurrentDownload()">Skip this file</button>
        </p>
    }

    <pre class="install-log">@string.Join('\n', Session.LogLines.Select(l => $"[{l.Level}] {l.Message}"))</pre>

    @if (Session.Phase == InstallPhase.Running)
    {
        <button class="btn-secondary" @onclick="() => Session.Cancel()">Cancel installation</button>
    }

    @if (Session.Result is { } result)
    {
        <div class="@(result.IsSuccess ? "result-ok" : "result-bad")">
            <h3>@(result.IsSuccess ? "Installation complete" : result.IsCancelled ? "Cancelled" : "Installation failed")</h3>
            <p>@result.Message</p>

            @if (result.Report.Count > 0)
            {
                <table class="report">
                    <thead><tr><th>Planned operation</th><th>Result</th><th>Comment</th></tr></thead>
                    <tbody>
                        @foreach (var row in result.Report)
                        {
                            <tr class="@(row.IsWarning ? "report-warning" : null)">
                                <td>@row.PlannedOperation</td>
                                <td>@row.Outcome</td>
                                <td>@row.Comment</td>
                            </tr>
                        }
                    </tbody>
                </table>
            }
        </div>
    }
</section>

@code {
    [Parameter, EditorRequired] public WizardRun Run { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        Session.Changed += OnChanged;

        // Fire and forget: awaiting here would block the render, and the session -- not this
        // component -- owns the running install. A reconnect re-renders from session state.
        if (Session.Phase != InstallPhase.Running)
            _ = Session.StartAsync(Run.Plan);

        await Task.CompletedTask;
    }

    private void OnChanged() => InvokeAsync(StateHasChanged);

    public void Dispose() => Session.Changed -= OnChanged;
}
```

`InstallReportEntry` lives in `DiffusionNexus.Installer.SDK.Models/Installation/InstallReport.cs` and its members are `PlannedOperation`, `Category`, `Outcome`, `Comment`, `IsWarning`, `ItemId` — the markup above uses those exact names.

- [ ] **Step 3: Build the wizard host page**

Create `DiffusionNexus.Installer.Electron/Components/Pages/Install.razor`:

```razor
@page "/install/{WorkloadId:guid}"
@using DiffusionNexus.Installer.Core.Catalog
@using DiffusionNexus.Installer.Core.Modules
@using DiffusionNexus.Installer.Core.Wizard
@using DiffusionNexus.Installer.Electron.Components.Wizard
@inject IWorkloadSource Workloads
@inject WizardModuleRegistry Registry
@inject NavigationManager Nav

<PageTitle>Install</PageTitle>

@if (_run is null)
{
    <p>Preparing...</p>
}
else
{
    <h1>@_run.Plan.Selection.Workload.Name</h1>
    <ol class="stage-strip">
        @foreach (var stage in _run.Plan.Stages)
        {
            <li class="@(stage == _run.CurrentStage ? "stage-current" : "stage")">@stage</li>
        }
    </ol>

    @if (_run.CurrentStage == WizardStage.Confirm)
    {
        <ConfirmStage Run="_run" />
    }
    else if (_run.CurrentStage == WizardStage.Install)
    {
        <InstallStage Run="_run" />
    }
    else
    {
        @foreach (var module in _run.CurrentModules)
        {
            @RenderModule(module)
        }
    }

    @if (_run.CurrentStage != WizardStage.Install)
    {
        <div class="wizard-actions">
            <button class="btn-secondary" disabled="@(!_run.CanGoBack)" @onclick="Back">Back</button>
            <button class="btn-primary" disabled="@(!_run.CanGoNext)" @onclick="Next">Next</button>
        </div>

        @foreach (var error in _run.ValidationErrors)
        {
            <p class="validation-error">@error</p>
        }
    }
}

@code {
    [Parameter] public Guid WorkloadId { get; set; }

    private WizardRun? _run;

    protected override async Task OnInitializedAsync()
    {
        var workloads = await Workloads.GetInstallerWorkloadsAsync();
        var workload = workloads.FirstOrDefault(w => w.Id == WorkloadId);

        if (workload is null)
        {
            Nav.NavigateTo("/");
            return;
        }

        var plan = await Registry.BuildPlanAsync(new WizardSelection { Workload = workload });
        _run = new WizardRun(plan);
    }

    private void Next() => _run!.TryNext();

    private void Back() => _run!.Back();

    private RenderFragment RenderModule(IWizardModule module) => module switch
    {
        InstallFolderModule m => @<InstallFolderPanel Module="m" />,
        ComfyFoldersModule m => @<ComfyFoldersPanel Module="m" />,
        GpuPreflightModule m => @<GpuPreflightPanel Module="m" />,
        ShortcutsModule m => @<ShortcutsPanel Module="m" />,
        _ => @<p>Unknown module: @module.Id</p>
    };
}
```

- [ ] **Step 4: Mount the prompt modal globally**

In `DiffusionNexus.Installer.Electron/Components/Layout/MainLayout.razor`, add just before the closing tag of the layout markup:

```razor
<DiffusionNexus.Installer.Electron.Components.Shared.PromptModal />
```

- [ ] **Step 5: Build and run every test**

Run: `dotnet build DiffusionNexus.Installer.slnx && dotnet test DiffusionNexus.Installer.slnx`
Expected: Build succeeded 0 errors; all tests PASS.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.Installer.Electron/Components
git commit -m "feat(wizard): confirm summary and install stage rendered from the session"
```

---

### Task 16: Manual smoke checklist and CI check

**Files:**
- Create: `docs/manual-smoke.md`
- Modify: `.github/workflows/` build workflow (create if absent)

**Interfaces:**
- Consumes: everything.
- Produces: a written checklist and a CI job that proves the package restore works without the local SDK.

- [ ] **Step 1: Write the smoke checklist**

Create `docs/manual-smoke.md`:

```markdown
# Manual smoke checklist — Installer 3.x wizard (slice 1)

Automated tests cover the module logic, the gate and the session. These are the things only a
real run can prove. Use a scratch install folder, never a real one.

## 1. Gallery

1. Launch with no catalog installed (delete `%LocalAppData%\DiffusionNexus\catalog`).
   **Expect:** the gallery populates from the embedded seed; no error, no empty state.
2. **Expect:** exactly six cards are enabled — Stable Diffusion web UI, Forge, Fooocus,
   ACE-Step, AI-Toolkit, Blanck-ComfyUI. Every other card is visible but disabled with a
   "Coming soon" note.
3. Filter by type Audio. **Expect:** ACE-Step appears; the Image and Video cards do not.
4. Filter by software ComfyUI. **Expect:** only ComfyUI-based cards remain, and the software
   filter offers exactly the software the catalog actually contains — no empty options.
5. Set `DIFFUSIONNEXUS_CATALOG_PATH` to a catalog checkout and relaunch.
   **Expect:** the gallery reflects that checkout.
6. Set `DIFFUSIONNEXUS_CATALOG_PATH` to a folder that does not exist and relaunch.
   **Expect:** the app still starts and falls back to the installed catalog. It must not crash.

## 2. Wizard stages

1. Pick Fooocus. **Expect:** Location → System → Confirm → Install. No model, VRAM,
   workflow or accelerator screen appears.
2. Pick Blanck-ComfyUI. **Expect:** the Location stage also shows the model-library and output
   folder fields.
3. Pick AI-Toolkit. **Expect:** the model-library field is present, the output folder field is not.
4. Clear the install folder. **Expect:** Next is disabled and the validation message shows.
5. Click Browse. **Expect:** a native folder dialog opens. Dismiss it. **Expect:** the field is
   unchanged and nothing crashes.

## 3. A real install

1. Install Fooocus into a scratch folder and let it finish.
   **Expect:** live log streams, the step counter advances, the report table renders, and the
   launcher script and shortcuts exist on disk afterwards.
2. Start again and press Cancel mid-clone.
   **Expect:** the run ends as Cancelled, not Failed, and no bug-report prompt appears.
3. Start an install, then resize/minimise and restore the window several times to force a circuit
   reconnect. **Expect:** the install keeps running and the log continues where it left off.

## 4. Known gap

Launching the packaged Electron exe directly still exits instantly — only the .NET entry point
under `resources/bin` works. This blocks any Start Menu shortcut and must be fixed before a
public 3.x release. Slice 1 is run from a dev build.
```

- [ ] **Step 2: Add the CI workflow**

Create `.github/workflows/build.yml`:

```yaml
name: build

on:
  push:
    branches: [main, 'feature/**']
  pull_request:
    branches: [main]

jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      # The SDK packages live under a different GitHub account, so the default GITHUB_TOKEN
      # cannot read them. UseLocalSDK=false is the point: this job is the gate that proves the
      # package references are complete, which a local build with the SDK checkout cannot.
      - name: Restore
        run: dotnet restore DiffusionNexus.Installer.slnx -p:UseLocalSDK=false
        env:
          # nuget.config reads %GITHUB_PACKAGES_TOKEN% for the github-littlegod source.
          # The repository secret is named PACKAGES_READ_TOKEN; the env var name is not
          # negotiable, so map one to the other here or every restore 401s.
          GITHUB_PACKAGES_TOKEN: ${{ secrets.PACKAGES_READ_TOKEN }}

      - name: Build
        run: dotnet build DiffusionNexus.Installer.slnx -c Release --no-restore -p:UseLocalSDK=false
        env:
          GITHUB_PACKAGES_TOKEN: ${{ secrets.PACKAGES_READ_TOKEN }}

      - name: Test
        run: dotnet test DiffusionNexus.Installer.slnx -c Release --no-build -p:UseLocalSDK=false
```

- [ ] **Step 3: Verify the whole suite one more time**

Run: `dotnet test DiffusionNexus.Installer.slnx`
Expected: all tests PASS.

- [ ] **Step 4: Commit and push**

```bash
git add docs/manual-smoke.md .github/workflows/build.yml
git commit -m "docs: manual smoke checklist and CI build workflow"
git push -u origin feature/catalog-driven-wizard
```

- [ ] **Step 5: Confirm CI is green**

Run: `gh run list --repo Into-The-Latent/DiffusionNexus.Installer --limit 3`
**The repository currently has NO secrets configured** (verified with `gh secret list`), so this
first run is expected to fail its restore with a 401 until the owner adds a `PACKAGES_READ_TOKEN`
secret holding a token with `read:packages` on the Little-God1983 account. That is an owner
action, not a build fix: do not work around it by removing `-p:UseLocalSDK=false`, which would
destroy the only gate proving the package references are complete. Record the red run and move on.

---

## After the plan

Slice 1 is done when the six workloads install end to end and `docs/manual-smoke.md` has been
walked by hand. Slice 2 adds the `Content` stage modules (VramProfile, ModelSelection, CustomNodes,
WorkflowSelection) plus Accelerators; each one registered in `AddInstallerCore` makes more gallery
cards installable with no gallery change.
