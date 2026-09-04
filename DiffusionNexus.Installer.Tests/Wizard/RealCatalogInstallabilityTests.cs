using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services.Hardware;
using DiffusionNexus.Installer.SDK.Services.Settings;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Wizard;

/// <summary>
/// Spec Sec 7 requires the Detect/AppliesTo agreement to hold "for every catalog workload". Every
/// other test in this suite builds synthetic InstallationConfigurations from a RepositoryType
/// alone -- exactly the blind spot that let the installability gate ship wrong through fifteen
/// tasks while the whole suite stayed green throughout. This reads the actual catalog.zip the
/// Electron project embeds and ships (Assets/Catalog/catalog.zip) -- the same archive Program.cs
/// seeds a fresh install from -- rather than another synthetic fixture.
/// </summary>
public sealed class RealCatalogInstallabilityTests : IAsyncLifetime
{
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

    private string _catalogDir = string.Empty;
    private IReadOnlyList<InstallationConfiguration> _workloads = [];

    public async Task InitializeAsync() => (_catalogDir, _workloads) = await EmbeddedCatalog.LoadAsync();

    public Task DisposeAsync()
    {
        EmbeddedCatalog.Delete(_catalogDir);
        return Task.CompletedTask;
    }

    private Task<IReadOnlyList<InstallationConfiguration>> ReadCatalogWorkloadsAsync() => Task.FromResult(_workloads);

    private static IUserSettingsRepository Settings()
    {
        var repo = new Mock<IUserSettingsRepository>();
        repo.Setup(r => r.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings());
        return repo.Object;
    }

    private static IGpuDetectionService Gpu()
    {
        var gpu = new Mock<IGpuDetectionService>();
        gpu.Setup(g => g.DetectAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GpuDetectionResult(GpuDetectionState.CudaCapable));
        return gpu.Object;
    }

    private static IVcRuntimeDetectionService VcRuntime()
    {
        var vc = new Mock<IVcRuntimeDetectionService>();
        vc.Setup(v => v.Detect()).Returns(new VcRuntimeDetectionResult(VcRuntimeState.Present));
        return vc.Object;
    }

    /// <summary>
    /// Resolves whatever wheel it is asked for. The gate only cares that SOME module satisfies the
    /// LlamaCpp capability; whether a specific id resolves is the module's own validation, covered
    /// in CapabilityAgreementTests.
    /// </summary>
    private IWorkloadSource Wheels()
    {
        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetLamaCppWheelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return source.Object;
    }

    /// <summary>
    /// Mirrors CoreServiceCollectionExtensions.AddInstallerCore's slice-1 module set exactly, so
    /// the gate under test is the one the app actually registers, not an arbitrary stand-in.
    /// </summary>
    private WizardModuleRegistry ProductionRegistry() => new(() =>
    [
        new InstallFolderModule(Settings(), new PreInstallationService()),
        new ComfyFoldersModule(Settings()),
        new VramProfileModule(),
        new ModelSelectionModule(new ModelPresenceScanner(), Mock.Of<IDiskSpaceEstimator>()),
        new WorkflowSelectionModule(),
        new GpuPreflightModule(Gpu()),
        new VcRuntimeModule(VcRuntime()),
        new LlamaCppModule(Wheels()),
        new ShortcutsModule(),
        new DisclaimerModule(),
    ]);

    [Fact]
    public async Task Exactly_twenty_of_the_twenty_one_installer_workloads_are_installable()
    {
        var workloads = (await ReadCatalogWorkloadsAsync())
            .Where(w => w.WorkloadTarget == WorkloadTargetType.Installer)
            .ToList();

        workloads.Should().NotBeEmpty("a broken extraction/read must fail loudly, not vacuously pass below");

        var registry = ProductionRegistry();
        var installable = workloads.Where(w => registry.IsInstallable(w)).Select(w => w.Name).ToList();
        var blocked = workloads.Where(w => !registry.IsInstallable(w)).Select(w => w.Name).ToList();

        installable.Should().BeEquivalentTo(ExpectedInstallableNames,
            "these are the twenty Installer-targeted workloads slice 2's modules cover");
        blocked.Should().BeEquivalentTo(workloads.Select(w => w.Name).Except(ExpectedInstallableNames),
            "Config535 must be blocked, not silently allowed");
        blocked.Should().Equal("Config535");
    }

    [Fact]
    public async Task Config535_is_blocked_for_an_impossible_torch_pairing()
    {
        // Named explicitly rather than left implicit in the list above: this is the one workload
        // whose capabilities are all covered and which is still refused, and the reason is data in
        // the catalog rather than a missing module. If the catalog entry is corrected to a CUDA
        // version torch 2.8.0 actually ships (12.6, 12.8 or 12.9), this test is what should fail.
        var workload = (await ReadCatalogWorkloadsAsync()).Single(w => w.Name == "Config535");

        WorkloadCapabilities.DetectBlocking(workload).Should().Be(WorkloadCapability.None,
            "every capability Config535 needs has a slice-1 module");

        WorkloadCapabilities.DetectIncompatibility(workload)
            .Should().Contain("13.0", "torch 2.8.0 ships no CUDA 13.0 wheel");

        ProductionRegistry().IsInstallable(workload).Should().BeFalse();
    }

    [Fact]
    public async Task No_offered_workload_would_be_refused_by_the_pipeline()
    {
        // The general form of the Config535 case: whatever the catalog says tomorrow, nothing the
        // gallery enables may be a workload InstallationPipeline aborts before step 1.
        var workloads = (await ReadCatalogWorkloadsAsync())
            .Where(w => w.WorkloadTarget == WorkloadTargetType.Installer)
            .ToList();

        var registry = ProductionRegistry();

        foreach (var workload in workloads.Where(w => registry.IsInstallable(w)))
        {
            WorkloadCapabilities.DetectIncompatibility(workload).Should().BeNull(
                $"'{workload.Name}' is offered, so the pipeline must not refuse it outright");
        }
    }

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
}
