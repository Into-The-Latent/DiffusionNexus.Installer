using DiffusionNexus.Installer.Core;
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Services;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Installation;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
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
        services.AddSingleton<Core.Host.IMismatchedFilePrompt>(new Core.Host.MismatchPromptService());
        services.AddInstallerCore();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void All_modules_resolve()
    {
        using var provider = Build();

        var registry = provider.GetRequiredService<WizardModuleRegistry>();

        registry.SatisfiedCapabilities.Should().Be(
            WorkloadCapability.ComfyFolders | WorkloadCapability.LlamaCpp
            | WorkloadCapability.VramProfile | WorkloadCapability.ModelDownloads | WorkloadCapability.Workflows);
    }

    [Fact]
    public void Every_registered_module_is_reachable_through_the_registry()
    {
        // Resolving the registry alone would not notice a module whose own dependencies cannot be
        // constructed -- GetServices would simply throw, or the module would be missing. Naming the
        // ids makes an accidentally-dropped registration a failure rather than a silent absence.
        using var provider = Build();

        provider.GetServices<IWizardModule>().Select(m => m.Id).Should().BeEquivalentTo(
            "install-folder", "comfy-folders", "vram-profile", "model-selection", "workflow-selection",
            "gpu-preflight", "vc-runtime", "llama-cpp", "shortcuts", "disclaimer");
    }

    [Fact]
    public void The_install_session_is_a_singleton()
    {
        using var provider = Build();

        var first = provider.GetRequiredService<IInstallSession>();
        var second = provider.GetRequiredService<IInstallSession>();

        first.Should().BeSameAs(second);
    }

    [Fact]
    public void Gallery_builder_resolves()
    {
        // Neither test above resolves IWorkloadSource or GalleryBuilder, so a broken ICatalog
        // registration -- e.g. a missing AddDiffusionNexusCatalog call -- would take out the first
        // screen the user sees while this suite stayed green. GalleryBuilder's constructor pulls in
        // IWorkloadSource (and so ICatalog) and WizardModuleRegistry, so resolving it proves that
        // whole chain is wired.
        using var provider = Build();

        var builder = provider.GetRequiredService<GalleryBuilder>();

        builder.Should().NotBeNull();
    }

    [Fact]
    public void The_embedded_catalog_resources_open_by_their_production_logical_names()
    {
        // This file's Build() configures the catalog with a bare temp InstalledCatalogPath, nothing
        // like Program.cs's EmbeddedArchive/EmbeddedManifest wiring -- so a typo'd LogicalName or a
        // dropped EmbeddedResource item in the Electron csproj would still leave every test here
        // green. Checked directly against the Electron assembly instead.
        var electronAssembly = typeof(UpdaterLog).Assembly;

        using var archive = electronAssembly.GetManifestResourceStream("catalog.zip");
        using var manifest = electronAssembly.GetManifestResourceStream("manifest.json");

        archive.Should().NotBeNull("Assets/Catalog/catalog.zip must be embedded with LogicalName 'catalog.zip'");
        manifest.Should().NotBeNull("Assets/Catalog/manifest.json must be embedded with LogicalName 'manifest.json'");
    }

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

    [Fact]
    public void The_model_preflight_resolves()
    {
        using var provider = Build();
        provider.GetRequiredService<Core.Install.IModelPreflight>().Should().NotBeNull();
    }
}
