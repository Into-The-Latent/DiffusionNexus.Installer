using System.IO.Compression;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Services;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services.Hardware;
using DiffusionNexus.Installer.SDK.Services.Settings;
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
public sealed class RealCatalogInstallabilityTests : IDisposable
{
    // The exact ten names manual-smoke.md documents as enabled in slice 1 (Fix 8 keeps that
    // document in sync with this list). Everything else among the installer-targeted workloads
    // must be blocked -- that is what the second half of the Fact below checks.
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
        "Config535",
        "FlashVSR-Video&Image Upscale",
    ];

    private readonly string _catalogDir;

    public RealCatalogInstallabilityTests()
    {
        _catalogDir = Path.Combine(Path.GetTempPath(), $"dn-catalog-agreement-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_catalogDir);

        // Read the same embedded resource Program.cs seeds a fresh install from, by logical name
        // off the Electron assembly, rather than a path relative to the test assembly's output
        // directory: catalog.zip is an EmbeddedResource, not Content, so nothing copies it there,
        // and this way the test does not depend on any path outside the repo.
        var electronAssembly = typeof(UpdaterLog).Assembly;
        using var zipStream = electronAssembly.GetManifestResourceStream("catalog.zip")
            ?? throw new InvalidOperationException(
                "catalog.zip is not embedded in the Electron assembly -- check the EmbeddedResource item in DiffusionNexus.Installer.Electron.csproj.");
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        archive.ExtractToDirectory(_catalogDir);
    }

    private async Task<IReadOnlyList<InstallationConfiguration>> ReadCatalogWorkloadsAsync()
    {
        var options = new CatalogOptions { LocalOverridePath = _catalogDir };
        var locator = new CatalogLocator(options);
        ICatalog catalog = new FileCatalog(locator, options);
        return await catalog.GetWorkloadsAsync();
    }

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

    /// <summary>
    /// Mirrors CoreServiceCollectionExtensions.AddInstallerCore's slice-1 module set exactly, so
    /// the gate under test is the one the app actually registers, not an arbitrary stand-in.
    /// </summary>
    private static WizardModuleRegistry ProductionRegistry() => new(
    [
        new InstallFolderModule(Settings()),
        new ComfyFoldersModule(Settings()),
        new GpuPreflightModule(Gpu()),
        new ShortcutsModule(),
    ]);

    [Fact]
    public async Task Exactly_the_slice_one_workloads_are_installable_in_the_real_catalog()
    {
        var workloads = (await ReadCatalogWorkloadsAsync())
            .Where(w => w.WorkloadTarget == WorkloadTargetType.Installer)
            .ToList();

        workloads.Should().NotBeEmpty("a broken extraction/read must fail loudly, not vacuously pass below");

        var registry = ProductionRegistry();
        var installable = workloads.Where(w => registry.IsInstallable(w)).Select(w => w.Name).ToList();
        var blocked = workloads.Where(w => !registry.IsInstallable(w)).Select(w => w.Name).ToList();

        installable.Should().BeEquivalentTo(ExpectedInstallableNames,
            "these are the only workloads slice 1's registered modules cover");
        blocked.Should().BeEquivalentTo(workloads.Select(w => w.Name).Except(ExpectedInstallableNames),
            "every installer-targeted workload not in the installable list must be blocked, not silently allowed");
    }

    [Fact]
    public async Task Detect_and_AppliesTo_agree_on_ComfyFolders_for_every_real_catalog_workload()
    {
        // ComfyFolders is the only capability slice 1 registers a module for besides the
        // unconditional ones, so it is the only pairing that can actually drift between the gate
        // (Detect) and the runtime (AppliesTo).
        var comfyFolders = new ComfyFoldersModule(Settings());
        var workloads = await ReadCatalogWorkloadsAsync();
        workloads.Should().NotBeEmpty();

        foreach (var workload in workloads)
        {
            var selection = new WizardSelection { Workload = workload };
            var detected = WorkloadCapabilities.Detect(workload).HasFlag(WorkloadCapability.ComfyFolders);
            var applies = comfyFolders.AppliesTo(selection);

            applies.Should().Be(detected,
                $"'{workload.Name}' ({workload.Repository.Type}) must agree between Detect and AppliesTo");
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_catalogDir, recursive: true); }
        catch (IOException) { /* best-effort cleanup of a temp dir */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup of a temp dir */ }
    }
}
