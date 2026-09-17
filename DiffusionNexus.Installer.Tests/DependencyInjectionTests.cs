using DiffusionNexus.Installer.Core;
using DiffusionNexus.Installer.Core.Announcements;
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.Electron.Services;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Installation;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
using DiffusionNexus.Installer.SDK.Services.Settings;
using DiffusionNexus.Installer.SDK.Shared.Services;
using DiffusionNexus.Installer.SDK.Shared.Services.Feedback;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Microsoft.JSInterop;
using Xunit;

namespace DiffusionNexus.Installer.Tests;

public class DependencyInjectionTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        // What the Blazor host provides per circuit; JsClipboard is the one host service that takes it.
        services.AddScoped(_ => Mock.Of<IJSRuntime>());
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IGitService, GitService>();
        services.AddSingleton<IPythonService, PythonService>();
        services.AddInstallationServices();
        services.AddSingleton<IInstallationOrchestrator, InstallationOrchestrator>();
        services.AddDiffusionNexusUserSettings(Path.Combine(Path.GetTempPath(), $"dn-{Guid.NewGuid():N}.json"));
        services.AddDiffusionNexusCatalog(o =>
            o.InstalledCatalogPath = Path.Combine(Path.GetTempPath(), $"dn-catalog-{Guid.NewGuid():N}"));
        services.AddInstallerCore();

        // Exactly what Program.cs calls, so this container is the app's container. The prompt
        // registration used to be duplicated here by hand, which is how the rest of what
        // Program.cs registers stayed uncovered.
        services.AddInstallerHostServices();
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

        // SoftwareGalleryBuilder too, and for the same reason: it is what the welcome screen --
        // the actual first screen -- injects. Every bUnit test registers it by hand, so dropping
        // its AddSingleton would leave the whole suite green and throw on the app's first render.
        provider.GetRequiredService<SoftwareGalleryBuilder>().Should().NotBeNull();
    }

    [Fact]
    public void Host_services_the_screens_depend_on_resolve()
    {
        // The hole the gallery-builder guard below does not cover. Program.cs is top-level
        // statements in an executable -- no test can reach what it registers -- and
        // IFeedbackReportingService lived only there, while both ScreenShell-hosted screens depend
        // on it through <FeedbackDialog>. Deleting those lines left this whole suite green (every
        // bUnit fixture registers its own mock) and threw
        // "Cannot provide a value for property 'Feedback'" on the app's very first screen.
        //
        // Registrations now live in AddInstallerHostServices, which Program.cs calls and Build()
        // calls, so the two cannot diverge.
        using var provider = Build();

        provider.GetRequiredService<IFeedbackReportingService>().Should().NotBeNull();
        provider.GetRequiredService<UpdaterLog>().Should().NotBeNull();
        provider.GetRequiredService<AppUpdateChecker>().Should().NotBeNull();
        provider.GetRequiredService<IFolderPicker>().Should().NotBeNull();
        provider.GetRequiredService<IPostInstallActions>().Should().NotBeNull();

        // Scoped -- it holds a circuit's IJSRuntime -- so resolved the way a circuit resolves it.
        using (var scope = provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<IClipboard>().Should().BeOfType<JsClipboard>();

        // The footer on every screen reads the cache; the cache reads the Gist service. Both must
        // resolve, and the cache must be ONE instance or every screen would fetch again.
        provider.GetRequiredService<ICommunityLinksService>().Should().BeOfType<GistCommunityLinksService>();
        provider.GetRequiredService<CommunityLinksCache>().Should()
            .BeSameAs(provider.GetRequiredService<CommunityLinksCache>());

        // The announcements banner, the same shape: Gist service -> ONE cache (issue #12). The
        // dismissal file sits beside user_settings.json under the name the 1.x installer uses, so
        // a notice dismissed in either stays dismissed in both.
        provider.GetRequiredService<IServerMessageService>().Should().BeOfType<GistServerMessageService>();
        provider.GetRequiredService<DismissedMessageStore>().Should().NotBeNull();
        provider.GetRequiredService<ServerMessageCache>().Should()
            .BeSameAs(provider.GetRequiredService<ServerMessageCache>());
        HostServiceCollectionExtensions.DismissedMessagesPath.Should().Be(
            Path.Combine(Path.GetDirectoryName(UserSettingsPaths.Default)!, "dismissed_messages.json"));

        // Both spellings resolve to ONE instance: the modal component subscribes to the concrete
        // service and the wizard raises through the interface, so two instances mean a prompt that
        // is raised and never shown.
        provider.GetRequiredService<IUserPrompt>().Should()
            .BeSameAs(provider.GetRequiredService<ModalPromptService>());
        provider.GetRequiredService<IMismatchedFilePrompt>().Should()
            .BeSameAs(provider.GetRequiredService<MismatchPromptService>());
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

        // A ComfyUI workload with one enabled model: identity alone does not prove per-run
        // isolation, since a fresh instance can still leak state if the container reused any
        // captured reference. An actual answer given on the first plan must not be visible on the
        // second.
        var modelId = Guid.NewGuid();
        var workload = new InstallationConfiguration { Name = "ComfyUI" };
        workload.Repository.Type = RepositoryType.ComfyUI;
        workload.ModelDownloads.Add(new ModelDownload
        {
            Id = modelId,
            Name = "VAE",
            Destination = @"models\vae",
            Url = "https://host.invalid/ae.safetensors",
        });

        var first = await registry.BuildPlanAsync(new WizardSelection { Workload = workload });
        var second = await registry.BuildPlanAsync(new WizardSelection { Workload = workload });

        first.AllModules.Select(m => m.Id).Should().BeEquivalentTo(second.AllModules.Select(m => m.Id));
        foreach (var (a, b) in first.AllModules.Zip(second.AllModules))
            a.Should().NotBeSameAs(b, $"module '{a.Id}' must be a fresh instance per run");

        first.AllModules.OfType<ModelSelectionModule>().Single().SetSelected(modelId, false);

        var secondModelModule = second.AllModules.OfType<ModelSelectionModule>().Single();
        secondModelModule.Rows.Single(r => r.Id == modelId).IsSelected.Should().BeTrue(
            "unticking a model on the first plan must not leak onto the second plan's module");
        second.ToOptions().ExcludedModelIds.Should().BeEmpty(
            "the second plan carries none of the first plan's answers");
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

    [Fact]
    public void The_catalog_update_coordinator_is_a_singleton_and_the_startup_check_is_hosted()
    {
        using var provider = Build();

        var coordinator = provider.GetRequiredService<ICatalogUpdateCoordinator>();
        coordinator.Should().BeSameAs(provider.GetRequiredService<ICatalogUpdateCoordinator>(),
            "the top bar, the welcome page and /updates must all read one state");

        // The startup check is what makes "most installs would simply never update" untrue for
        // the catalog too. A dropped AddHostedService line would leave every page test green.
        provider.GetServices<IHostedService>().Should().ContainSingle(h => h is CatalogUpdateStartupCheck);
    }
}
